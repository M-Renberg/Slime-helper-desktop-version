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
        private readonly GitHubService _service;
        private readonly System.Timers.Timer _timer;
        private readonly SemaphoreSlim _pollLock = new(1, 1);
        private readonly Dictionary<long, string> _runConclusions = new();
        private bool _isFirstPoll = true;
        private bool _disposed = false;

        public event Action<GitHubWorkflowRun, string?>? OnWorkflowFailed;
        public event Action<GitHubWorkflowRun>? OnWorkflowSucceeded;
        public event Action<string, string>? OnReaction;

        public string? ActiveRepoPath { get; private set; }
        public string? GitHubToken { get; set; }
        public (string owner, string repo)? CurrentRepo { get; private set; }

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
            if (repoInfo.HasValue && (CurrentRepo == null || CurrentRepo.Value != repoInfo.Value))
            {
                CurrentRepo = repoInfo.Value;
                lock (_runConclusions)
                {
                    _runConclusions.Clear();
                }
                _isFirstPoll = true;
                Debug.WriteLine($"GitHubWatcher: Switched to repo {CurrentRepo.Value.owner}/{CurrentRepo.Value.repo}");
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

                // If repo not set or discovered, try from ActiveRepoPath
                if (CurrentRepo == null && !string.IsNullOrWhiteSpace(ActiveRepoPath))
                {
                    SetActiveRepo(ActiveRepoPath);
                }

                if (CurrentRepo == null)
                {
                    ScheduleNext(IdleInterval);
                    return;
                }

                var (owner, repo) = CurrentRepo.Value;
                var runs = await _service.GetLatestRunsAsync(owner, repo, limit: 10, token: GitHubToken);

                bool hasActiveRuns = false;

                foreach (var run in runs)
                {
                    bool isInProgress = string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(run.Status, "queued", StringComparison.OrdinalIgnoreCase);

                    if (isInProgress)
                    {
                        hasActiveRuns = true;
                    }

                    string currentConclusion = run.Conclusion ?? run.Status ?? "unknown";

                    lock (_runConclusions)
                    {
                        if (_isFirstPoll)
                        {
                            // Seed initial history so we do not notify on stale runs on launch
                            _runConclusions[run.Id] = currentConclusion;
                            continue;
                        }

                        bool hadPreviousState = _runConclusions.TryGetValue(run.Id, out string? prevConclusion);
                        _runConclusions[run.Id] = currentConclusion;

                        if (!hadPreviousState)
                        {
                            // Brand new run detected
                            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                            {
                                HandleCompletedRun(owner, repo, run);
                            }
                        }
                        else if (!string.Equals(prevConclusion, currentConclusion, StringComparison.OrdinalIgnoreCase))
                        {
                            // State changed!
                            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                            {
                                HandleCompletedRun(owner, repo, run);
                            }
                        }
                    }
                }

                if (_isFirstPoll)
                {
                    _isFirstPoll = false;
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
