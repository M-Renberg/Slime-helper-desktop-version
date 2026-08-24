using System.Diagnostics;
using System.IO;

namespace SlimeHelper
{
    public static class RepoScannerService
    {
        private static DateTime _lastDirtyNagTime = DateTime.MinValue;
        private static bool _lastGitWasDirty = false;
        private static readonly TimeSpan DirtyNagCooldown = TimeSpan.FromMinutes(30);

        // Scannar alla mappar under ReposRootPath efter ocommittade ändringar
        public static List<string> ScanDirtyRepos(string rootReposPath)
        {
            var dirtyRepos = new List<string>();
            if (string.IsNullOrWhiteSpace(rootReposPath) || !Directory.Exists(rootReposPath))
                return dirtyRepos;

            try
            {
                var subDirs = Directory.GetDirectories(rootReposPath);

                foreach (var dir in subDirs)
                {
                    if (Directory.Exists(Path.Combine(dir, ".git")))
                    {
                        string status = RunGitCommand(dir, "status --porcelain");
                        if (!string.IsNullOrWhiteSpace(status))
                        {
                            dirtyRepos.Add(Path.GetFileName(dir));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error scanning repos: {ex.Message}");
            }

            return dirtyRepos;
        }

        // Kontrollerar status för ett specifikt repo och ger status/meddelande
        public static (string Status, string Message) CheckGitStatus(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                return ("OK", "");

            try
            {
                string statusOutput = RunGitCommand(repoPath, "status --porcelain");
                bool isDirty = !string.IsNullOrWhiteSpace(statusOutput);
                var now = DateTime.Now;

                if (isDirty)
                {
                    if (!_lastGitWasDirty || (now - _lastDirtyNagTime > DirtyNagCooldown))
                    {
                        _lastDirtyNagTime = now;
                        _lastGitWasDirty = true;
                        return ("DIRTY", "You have forgotten to commit your code!");
                    }
                    _lastGitWasDirty = true;
                }
                else
                {
                    if (_lastGitWasDirty)
                    {
                        _lastDirtyNagTime = DateTime.MinValue;
                        _lastGitWasDirty = false;
                        return ("STREAK", "Great commit! Now the code is safe and sound");
                    }
                    _lastGitWasDirty = false;

                    string logOutput = RunGitCommand(repoPath, "log @{u}..");
                    if (!string.IsNullOrWhiteSpace(logOutput))
                    {
                        int commitCount = logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                        if (commitCount > 10)
                            return ("PUSH_NEEDED", $"Wow, {commitCount} local commits? Push already!");
                        if (commitCount > 0)
                            return ("PUSH_NEEDED", "Maybe it is time to push the code?");
                    }
                }
            }
            catch { }

            return ("OK", "");
        }

        private static string RunGitCommand(string workingDir, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return string.Empty;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return output;
        }
    }
}