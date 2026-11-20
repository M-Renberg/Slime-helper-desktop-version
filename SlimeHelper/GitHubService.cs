using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SlimeHelper
{
    #region Models

    public class GitHubWorkflowRun
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("head_branch")]
        public string HeadBranch { get; set; } = "";

        [JsonPropertyName("head_sha")]
        public string HeadSha { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = ""; // queued, in_progress, completed, waiting

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; } // success, failure, neutral, cancelled, timed_out, action_required

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("event")]
        public string Event { get; set; } = "";

        [JsonPropertyName("display_title")]
        public string DisplayTitle { get; set; } = "";
    }

    public class GitHubJob
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("run_id")]
        public long RunId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("started_at")]
        public DateTimeOffset? StartedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }

        [JsonPropertyName("steps")]
        public List<GitHubStep> Steps { get; set; } = [];
    }

    public class GitHubStep
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }

        [JsonPropertyName("number")]
        public int Number { get; set; }
    }

    public class GitHubPullRequest
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("state")]
        public string State { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        public string UserLogin { get; set; } = "";
        public string HeadBranch { get; set; } = "";
        public string BaseBranch { get; set; } = "";
    }

    internal class GitHubRunsResponse
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("workflow_runs")]
        public List<GitHubWorkflowRun> WorkflowRuns { get; set; } = [];
    }

    internal class GitHubJobsResponse
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("jobs")]
        public List<GitHubJob> Jobs { get; set; } = [];
    }

    internal class GitHubUserResponse
    {
        [JsonPropertyName("login")]
        public string Login { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    #endregion

    public class GitHubService
    {
        private static readonly HttpClient _httpClient;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public int LastRateRemaining { get; private set; } = 5000;
        public DateTimeOffset LastRateReset { get; private set; } = DateTimeOffset.UtcNow;

        static GitHubService()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // Critical for 302 log redirect handling
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SlimeHelper");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        }

        #region Token and Repo Discovery

        public static string? TryGetGhCliToken()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    Arguments = "auth token",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return null;

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
            catch
            {
                // gh CLI not installed or failed
            }

            return null;
        }

        public static string? TryGetRepoNameFromUrl(string? htmlUrl)
        {
            if (string.IsNullOrWhiteSpace(htmlUrl)) return null;
            var match = Regex.Match(htmlUrl, @"github\.com/([^/]+/[^/]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static string ResolveToken(string? explicitToken = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitToken))
                return explicitToken.Trim();

            string? cliToken = TryGetGhCliToken();
            if (!string.IsNullOrWhiteSpace(cliToken))
                return cliToken.Trim();

            string? envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
                return envToken.Trim();

            return "";
        }

        public static (string owner, string repo)? GetRepoInfo(string workingDirectory)
        {
            return GetRepoInfoFromDirectory(workingDirectory);
        }

        public static (string owner, string repo)? GetRepoInfoFromDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            string? remoteUrl = GetGitRemoteOriginUrl(directory);
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                remoteUrl = ReadGitRemoteUrlFromConfig(directory);
            }

            if (string.IsNullOrWhiteSpace(remoteUrl))
                return null;

            return ParseGitHubUrl(remoteUrl);
        }

        public static (string owner, string repo)? ParseGitHubUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            string cleaned = url.Trim();
            if (cleaned.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 4);
            }

            // Patterns:
            // git@github.com:owner/repo
            // ssh://git@github.com/owner/repo
            // https://github.com/owner/repo
            // http://github.com/owner/repo
            // https://token@github.com/owner/repo

            var match = Regex.Match(cleaned, @"(?:https?:\/\/(?:[^@\/]+@)?|ssh:\/\/git@|git@)github\.com[:\/]([a-zA-Z0-9_\-\.]+)\/([a-zA-Z0-9_\-\.]+)");
            if (match.Success && match.Groups.Count >= 3)
            {
                string owner = match.Groups[1].Value.Trim();
                string repo = match.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo))
                {
                    return (owner, repo);
                }
            }

            return null;
        }

        private static string? GetGitRemoteOriginUrl(string directory)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "config --get remote.origin.url",
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return null;

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
            catch { }

            return null;
        }

        private static string? ReadGitRemoteUrlFromConfig(string directory)
        {
            try
            {
                var dir = new DirectoryInfo(directory);
                while (dir != null)
                {
                    string gitDir = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(gitDir))
                    {
                        string configFile = Path.Combine(gitDir, "config");
                        if (File.Exists(configFile))
                        {
                            return ParseRemoteFromGitConfigFile(configFile);
                        }
                    }
                    else if (File.Exists(gitDir)) // Submodule or worktree .git pointer
                    {
                        string content = File.ReadAllText(gitDir).Trim();
                        if (content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                        {
                            string targetPath = content.Substring(7).Trim();
                            if (!Path.IsPathRooted(targetPath))
                            {
                                targetPath = Path.GetFullPath(Path.Combine(dir.FullName, targetPath));
                            }
                            string configFile = Path.Combine(targetPath, "config");
                            if (File.Exists(configFile))
                            {
                                return ParseRemoteFromGitConfigFile(configFile);
                            }
                        }
                    }

                    dir = dir.Parent;
                }
            }
            catch { }

            return null;
        }

        private static string? ParseRemoteFromGitConfigFile(string configFilePath)
        {
            try
            {
                var lines = File.ReadAllLines(configFilePath);
                bool inOriginSection = false;

                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase))
                    {
                        inOriginSection = true;
                        continue;
                    }
                    else if (line.StartsWith("["))
                    {
                        inOriginSection = false;
                        continue;
                    }

                    if (inOriginSection && line.StartsWith("url =", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(5).Trim();
                    }
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region REST API Operations

        public async Task<(bool Ok, string UserLogin, int RateRemaining)> TestConnectionAsync(string? token = null)
        {
            string resolvedToken = ResolveToken(token);
            if (string.IsNullOrWhiteSpace(resolvedToken))
            {
                return (false, "No GitHub token configured or detected.", 0);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);

                using var response = await _httpClient.SendAsync(request);
                UpdateRateLimitHeaders(response.Headers);

                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var user = JsonSerializer.Deserialize<GitHubUserResponse>(json, _jsonOptions);
                    return (true, user?.Login ?? "authenticated", LastRateRemaining);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return (false, "Invalid or expired token (401 Unauthorized).", LastRateRemaining);
                }

                return (false, $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}", LastRateRemaining);
            }
            catch (Exception ex)
            {
                return (false, $"Connection error: {ex.Message}", LastRateRemaining);
            }
        }

        public async Task<List<GitHubWorkflowRun>> GetLatestRunsAsync(string owner, string repo, string? branch = null, int limit = 5, string? token = null)
        {
            string resolvedToken = ResolveToken(token);
            string url = $"repos/{owner}/{repo}/actions/runs?per_page={Math.Clamp(limit, 1, 30)}";
            if (!string.IsNullOrWhiteSpace(branch))
            {
                url += $"&branch={Uri.EscapeDataString(branch)}";
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(resolvedToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);
                }

                using var response = await _httpClient.SendAsync(request);
                UpdateRateLimitHeaders(response.Headers);

                if (!response.IsSuccessStatusCode)
                    return [];

                string json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<GitHubRunsResponse>(json, _jsonOptions);
                return result?.WorkflowRuns ?? [];
            }
            catch
            {
                return [];
            }
        }

        public async Task<GitHubWorkflowRun?> GetRunAsync(string owner, string repo, long runId, string? token = null)
        {
            string resolvedToken = ResolveToken(token);
            string url = $"repos/{owner}/{repo}/actions/runs/{runId}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(resolvedToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);
                }

                using var response = await _httpClient.SendAsync(request);
                UpdateRateLimitHeaders(response.Headers);

                if (!response.IsSuccessStatusCode)
                    return null;

                string json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<GitHubWorkflowRun>(json, _jsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<GitHubJob>> GetRunJobsAsync(string owner, string repo, long runId, string? token = null)
        {
            string resolvedToken = ResolveToken(token);
            string url = $"repos/{owner}/{repo}/actions/runs/{runId}/jobs";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(resolvedToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);
                }

                using var response = await _httpClient.SendAsync(request);
                UpdateRateLimitHeaders(response.Headers);

                if (!response.IsSuccessStatusCode)
                    return [];

                string json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<GitHubJobsResponse>(json, _jsonOptions);
                return result?.Jobs ?? [];
            }
            catch
            {
                return [];
            }
        }

        /// <summary>
        /// Retrieves the tail of a job log. Correctly handles GitHub's 302 Found redirect
        /// to pre-signed cloud storage by omitting the Authorization header on redirect.
        /// </summary>
        public async Task<string> GetJobLogTailAsync(string owner, string repo, long jobId, int maxLines = 150, string? token = null)
        {
            string resolvedToken = ResolveToken(token);
            string url = $"repos/{owner}/{repo}/actions/jobs/{jobId}/logs";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(resolvedToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);
                }

                using var response = await _httpClient.SendAsync(request);
                UpdateRateLimitHeaders(response.Headers);

                HttpResponseMessage logResponse;

                if (response.StatusCode == HttpStatusCode.Found ||
                    response.StatusCode == HttpStatusCode.SeeOther ||
                    response.StatusCode == HttpStatusCode.TemporaryRedirect)
                {
                    Uri? redirectUri = response.Headers.Location;
                    if (redirectUri == null)
                        return "Failed to retrieve logs: empty redirect location.";

                    // CRITICAL: Fetch redirect WITHOUT Authorization header
                    using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, redirectUri);
                    logResponse = await _httpClient.SendAsync(redirectRequest);
                }
                else if (response.IsSuccessStatusCode)
                {
                    logResponse = response;
                }
                else
                {
                    return $"Could not fetch log. HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }

                using (logResponse)
                {
                    if (!logResponse.IsSuccessStatusCode)
                    {
                        return $"Log download returned {(int)logResponse.StatusCode}";
                    }

                    using var stream = await logResponse.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    var tailQueue = new Queue<string>(maxLines);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (tailQueue.Count >= maxLines)
                        {
                            tailQueue.Dequeue();
                        }
                        tailQueue.Enqueue(line);
                    }

                    return string.Join(Environment.NewLine, tailQueue);
                }
            }
            catch (Exception ex)
            {
                return $"Error reading job log: {ex.Message}";
            }
        }

        public async Task<(bool Success, string Message, string? Url)> CreatePullRequestAsync(
            string owner,
            string repo,
            string title,
            string headBranch,
            string baseBranch = "main",
            string body = "",
            string? token = null)
        {
            if (string.IsNullOrWhiteSpace(title))
                return (false, "PR title cannot be empty.", null);

            if (string.IsNullOrWhiteSpace(headBranch))
                return (false, "Head branch cannot be empty.", null);

            if (string.Equals(headBranch.Trim(), baseBranch.Trim(), StringComparison.OrdinalIgnoreCase))
                return (false, "Head branch and base branch cannot be the same.", null);

            string resolvedToken = ResolveToken(token);
            if (string.IsNullOrWhiteSpace(resolvedToken))
                return (false, "No GitHub token available for PR creation.", null);

            string url = $"repos/{owner}/{repo}/pulls";
            var payload = new
            {
                title = title.Trim(),
                head = headBranch.Trim(),
                @base = baseBranch.Trim(),
                body = body ?? ""
            };

            try
            {
                string jsonPayload = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);

                using var response = await _httpClient.SendAsync(request);
                UpdateRateLimitHeaders(response.Headers);

                string responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseText);
                    var root = doc.RootElement;
                    int prNumber = root.GetProperty("number").GetInt32();
                    string htmlUrl = root.GetProperty("html_url").GetString() ?? "";

                    return (true, $"Pull Request #{prNumber} created: {htmlUrl}", htmlUrl);
                }

                // Error handling
                string errorMessage = response.ReasonPhrase ?? "Unknown error";
                try
                {
                    using var errDoc = JsonDocument.Parse(responseText);
                    if (errDoc.RootElement.TryGetProperty("message", out var msgElem))
                    {
                        errorMessage = msgElem.GetString() ?? errorMessage;
                    }
                }
                catch { }

                return (false, $"GitHub PR failed ({(int)response.StatusCode}): {errorMessage}", null);
            }
            catch (Exception ex)
            {
                return (false, $"Network error creating PR: {ex.Message}", null);
            }
        }

        private void UpdateRateLimitHeaders(HttpHeaders headers)
        {
            if (headers.TryGetValues("x-ratelimit-remaining", out var remVals) &&
                int.TryParse(remVals.FirstOrDefault(), out int remaining))
            {
                LastRateRemaining = remaining;
            }

            if (headers.TryGetValues("x-ratelimit-reset", out var resetVals) &&
                long.TryParse(resetVals.FirstOrDefault(), out long resetUnix))
            {
                LastRateReset = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            }
        }

        #endregion
    }
}
