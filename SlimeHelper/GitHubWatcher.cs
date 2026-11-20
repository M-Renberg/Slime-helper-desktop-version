using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace SlimeHelper
{
    public class GitHubWatcher : IDisposable
    {
        private sealed class RepoWatchEntry
        {
            public required string Key { get; init; }
            public required string Owner { get; init; }
            public required string Repo { get; init; }
            public string? Path { get; set; }
            public Dictionary<long, string> RunConclusions { get; } = new();
            public bool IsFirstPoll { get; set; } = true;
        }

        private readonly GitHubService _service;
        private readonly System.Timers.Timer _timer;
        private readonly SemaphoreSlim _pollLock = new(1, 1);
        private readonly Dictionary<string, RepoWatchEntry> _watchedRepos = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _watchLock = new();
        private bool _disposed = false;

        public event Action<GitHubWorkflowRun, string?>? OnWorkflowFailed;
        public event Action<GitHubWorkflowRun>? OnWorkflowSucceeded;
        public event Action<string, string>? OnReaction;

        public string? ActiveRepoPath { get; private set; }
        public string? GitHubToken { get; set; }
        public (string owner, string repo)? CurrentRepo { get; private set; }

        public IReadOnlyList<(string owner, string repo)> WatchedRepos
        {
            get
            {
                lock (_watchLock)
                {
                    return _watchedRepos.Values.Select(e => (e.Owner, e.Repo)).ToList();
                }
            }
        }

        public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ActiveInterval { get; set; } = TimeSpan.FromSeconds(15);

        public GitHubService Service => _service;

        public GitHubWatcher(GitHubService? service = null)
        {
            _service = service ?? new GitHubService();

            _timer = new System.Timers.Timer
            {
                AutoReset = false,
                Interval = IdleInterval.TotalMilliseconds
            };
            _timer.Elapsed += async (s, e) => await PollAsync();
        }

        public void SetActiveRepo(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            ActiveRepoPath = directory;
            var repoInfo = GitHubService.GetRepoInfo(directory);
            if (repoInfo.HasValue)
            {
                string key = $"{repoInfo.Value.owner}/{repoInfo.Value.repo}";
                lock (_watchLock)
                {
                    if (!_watchedRepos.ContainsKey(key))
                    {
                        _watchedRepos[key] = new RepoWatchEntry
                        {
                            Key = key,
                            Owner = repoInfo.Value.owner,
                            Repo = repoInfo.Value.repo,
                            Path = directory
                        };
                    }
                    else
                    {
                        _watchedRepos[key].Path = directory;
                    }
                }

                if (CurrentRepo == null || CurrentRepo.Value != repoInfo.Value)
                {
                    CurrentRepo = repoInfo.Value;
                    Debug.WriteLine($"GitHubWatcher: Switched active repo to {key}");
                }
            }
        }

        /// <summary>
        /// Discovers all GitHub repositories across the given paths (both direct git repos
        /// and immediate subdirectories of workspace root folders) and watches them concurrently.
        /// </summary>
        public void SetWatchedPaths(IEnumerable<string?> directories)
        {
            var discovered = new Dictionary<string, (string owner, string repo, string path)>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in directories)
            {
                if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir)) continue;

                var direct = GitHubService.GetRepoInfo(dir);
                if (direct.HasValue)
                {
                    string key = $"{direct.Value.owner}/{direct.Value.repo}";
                    discovered.TryAdd(key, (direct.Value.owner, direct.Value.repo, dir));
                    continue;
                }

                // If dir is a parent folder (e.g. ReposRootPath), scan immediate subdirectories for git repos
                try
                {
                    foreach (var subDir in System.IO.Directory.GetDirectories(dir))
                    {
                        var subInfo = GitHubService.GetRepoInfo(subDir);
                        if (subInfo.HasValue)
                        {
                            string key = $"{subInfo.Value.owner}/{subInfo.Value.repo}";
                            discovered.TryAdd(key, (subInfo.Value.owner, subInfo.Value.repo, subDir));
                        }
                    }
                }
                catch { }
            }

            lock (_watchLock)
            {
                var toRemove = _watchedRepos.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
                foreach (var key in toRemove) _watchedRepos.Remove(key);

                foreach (var (key, info) in discovered)
                {
                    if (_watchedRepos.TryGetValue(key, out var existing))
                    {
                        existing.Path = info.path;
                    }
                    else
                    {
                        _watchedRepos[key] = new RepoWatchEntry
                        {
                            Key = key,
                            Owner = info.owner,
                            Repo = info.repo,
                            Path = info.path
                        };
                    }
                }

                if (CurrentRepo == null && _watchedRepos.Count > 0)
                {
                    var first = _watchedRepos.Values.First();
                    CurrentRepo = (first.Owner, first.Repo);
                    if (string.IsNullOrWhiteSpace(ActiveRepoPath)) ActiveRepoPath = first.Path;
                }
            }
        }

        public void Start(string? repoPath = null, string? token = null)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                GitHubToken = token;
            }

            if (!string.IsNullOrWhiteSpace(repoPath))
            {
                SetActiveRepo(repoPath);
            }

            _timer.Stop();
            _timer.Interval = 1000; // Kick off initial check quickly
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public async Task CheckNowAsync()
        {
            await PollAsync();
        }

        private async Task PollAsync()
        {
            if (_disposed) return;

            bool lockAcquired = false;
            try
            {
                lockAcquired = await _pollLock.WaitAsync(0);
                if (!lockAcquired) return;

                TimeSpan nextInterval = IdleInterval;

                // Check rate limits
                if (_service.LastRateRemaining < 5)
                {
                    var waitDuration = _service.LastRateReset - DateTimeOffset.UtcNow;
                    if (waitDuration > TimeSpan.Zero)
                    {
                        nextInterval = waitDuration < TimeSpan.FromMinutes(60) ? waitDuration : TimeSpan.FromMinutes(60);
                        Debug.WriteLine($"GitHubWatcher: Rate limit low ({_service.LastRateRemaining}). Backing off for {nextInterval.TotalSeconds}s");
                        ScheduleNext(nextInterval);
                        return;
                    }
                }

                // Discover repos from ActiveRepoPath if nothing has been registered yet
                List<RepoWatchEntry> snapshot;
                lock (_watchLock)
                {
                    snapshot = _watchedRepos.Values.ToList();
                }

                if (snapshot.Count == 0 && !string.IsNullOrWhiteSpace(ActiveRepoPath))
                {
                    SetWatchedPaths(new[] { ActiveRepoPath });
                    lock (_watchLock)
                    {
                        snapshot = _watchedRepos.Values.ToList();
                    }
                }

                if (snapshot.Count == 0)
                {
                    ScheduleNext(IdleInterval);
                    return;
                }

                bool hasActiveRuns = false;

                foreach (var entry in snapshot)
                {
                    if (_disposed) break;

                    if (_service.LastRateRemaining < 5)
                    {
                        var waitDuration = _service.LastRateReset - DateTimeOffset.UtcNow;
                        if (waitDuration > TimeSpan.Zero)
                        {
                            nextInterval = waitDuration < TimeSpan.FromMinutes(60) ? waitDuration : TimeSpan.FromMinutes(60);
                            Debug.WriteLine($"GitHubWatcher: Rate limit low ({_service.LastRateRemaining}). Backing off for {nextInterval.TotalSeconds}s");
                            ScheduleNext(nextInterval);
                            return;
                        }
                    }

                    try
                    {
                        var runs = await _service.GetLatestRunsAsync(entry.Owner, entry.Repo, limit: 10, token: GitHubToken);

                        foreach (var run in runs)
                        {
                            bool isInProgress = string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(run.Status, "queued", StringComparison.OrdinalIgnoreCase);

                            if (isInProgress)
                            {
                                hasActiveRuns = true;
                            }

                            string currentConclusion = run.Conclusion ?? run.Status ?? "unknown";

                            lock (entry.RunConclusions)
                            {
                                if (entry.IsFirstPoll)
                                {
                                    // Seed initial history so we do not notify on stale runs on launch
                                    entry.RunConclusions[run.Id] = currentConclusion;
                                    continue;
                                }

                                bool hadPreviousState = entry.RunConclusions.TryGetValue(run.Id, out string? prevConclusion);
                                entry.RunConclusions[run.Id] = currentConclusion;

                                if (!hadPreviousState)
                                {
                                    if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                                    {
                                        HandleCompletedRun(entry.Owner, entry.Repo, run);
                                    }
                                }
                                else if (!string.Equals(prevConclusion, currentConclusion, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                                    {
                                        HandleCompletedRun(entry.Owner, entry.Repo, run);
                                    }
                                }
                            }
                        }

                        if (entry.IsFirstPoll)
                        {
                            entry.IsFirstPoll = false;
                        }
                    }
                    catch (Exception repoEx)
                    {
                        Debug.WriteLine($"GitHubWatcher error polling {entry.Key}: {repoEx.Message}");
                    }
                }

                nextInterval = hasActiveRuns ? ActiveInterval : IdleInterval;
                ScheduleNext(nextInterval);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHubWatcher error during poll: {ex.Message}");
                ScheduleNext(IdleInterval);
            }
            finally
            {
                if (lockAcquired)
                {
                    _pollLock.Release();
                }
            }
        }

        private void ScheduleNext(TimeSpan delay)
        {
            if (_disposed) return;
            try
            {
                _timer.Interval = Math.Max(5000, delay.TotalMilliseconds);
                _timer.Start();
            }
            catch { }
        }

        private void HandleCompletedRun(string owner, string repo, GitHubWorkflowRun run)
        {
            string conclusion = (run.Conclusion ?? "").ToLowerInvariant();

            if (conclusion == "failure" || conclusion == "timed_out" || conclusion == "cancelled")
            {
                // Asynchronously fetch failed job logs
                _ = Task.Run(async () =>
                {
                    string? logSnippet = null;
                    try
                    {
                        var jobs = await _service.GetRunJobsAsync(owner, repo, run.Id, GitHubToken);
                        var failedJob = jobs.FirstOrDefault(j =>
                            string.Equals(j.Conclusion, "failure", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(j.Conclusion, "timed_out", StringComparison.OrdinalIgnoreCase));

                        if (failedJob != null)
                        {
                            logSnippet = await _service.GetJobLogTailAsync(owner, repo, failedJob.Id, maxLines: 100, GitHubToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to fetch job log snippet: {ex.Message}");
                    }

                    OnWorkflowFailed?.Invoke(run, logSnippet);
                    OnReaction?.Invoke("ERROR", $"CI failed on {run.Name} ({run.HeadBranch})! 🚨");
                });
            }
            else if (conclusion == "success")
            {
                OnWorkflowSucceeded?.Invoke(run);
                OnReaction?.Invoke("HURRAY", $"CI passed on {run.Name} ({run.HeadBranch})! 🎉");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();
            _timer.Dispose();
            _pollLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
