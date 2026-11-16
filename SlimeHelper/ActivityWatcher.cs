using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SlimeHelper
{
    public class ActivityWatcher : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private DateTime _workStartTime = DateTime.Now;
        private int _streakMinutes = 0;
        private bool _isCurrentlyAfk = false;
        private int _gitCheckCounter = 0;

        public event Action<string, string>? OnReaction;

        /// <summary>
        /// Configured repository path or root repositories directory used by CheckGitStatus.
        /// </summary>
        public string? ActiveRepoPath { get; set; }

        private readonly DispatcherTimer _idleTalkTimer;

        private static IntPtr _hookID = IntPtr.Zero;
        private static LowLevelKeyboardProc? _proc;
        private static DateTime _lastKeyboardActivity = DateTime.Now;

        // Instance reference so the static keyboard hook can reach the active watcher
        private static ActivityWatcher? _instance;

        private readonly Random _rng = new();

        public ActivityWatcher()
        {
            _instance = this;
            _proc = HookCallback;
            _hookID = SetHook(_proc);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += CheckActivity;
            _timer.Start();

            _idleTalkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            _idleTalkTimer.Tick += (s, e) =>
            {
                if (!_isCurrentlyAfk)
                {
                    var (selectedReaction, randomThought) = PickIdleChatter(DateTime.Now, _rng);

                    // Prefix with IDLE_CHAT: so MainWindow enforces the chatter cooldown across all emotions
                    OnReaction?.Invoke($"IDLE_CHAT:{selectedReaction}", randomThought);
                }
            };
            _idleTalkTimer.Start();
        }

        public static (string Status, string Message) PickIdleChatter(DateTime now, Random rng) =>
            SlimeResponses.PickIdleChatter(now, rng);

        private void CheckActivity(object? sender, EventArgs e)
        {
            double inactiveMinutes = (DateTime.Now - _lastKeyboardActivity).TotalMinutes;

            // Enter SLEEP after 6 minutes of inactivity and remain there until input resumes
            if (inactiveMinutes > 6)
            {
                if (!_isCurrentlyAfk)
                {
                    _isCurrentlyAfk = true;
                    _streakMinutes = 0;
                    OnReaction?.Invoke("SLEEP", "Zzz...");
                }
                return;
            }

            // Wake up if previously AFK
            if (_isCurrentlyAfk)
            {
                _isCurrentlyAfk = false;
                OnReaction?.Invoke("IDLE", "Oh, you're back!");
            }

            // Git check every 5 minutes
            _gitCheckCounter++;
            if (_gitCheckCounter >= 10)
            {
                _gitCheckCounter = 0;
                CheckGitStatus();
            }

            if ((DateTime.Now - _workStartTime).TotalMinutes >= 55)
            {
                _workStartTime = DateTime.Now;
                OnReaction?.Invoke("BREAK", SlimeResponses.PickRandom(SlimeResponses.BreakResponses));
                return;
            }

            if (inactiveMinutes < 2)
            {
                _streakMinutes++;
                if (_streakMinutes >= 5 && _streakMinutes % 10 == 0)
                {
                    OnReaction?.Invoke("STREAK", $"You're on fire! {_streakMinutes} min streak!");
                }
            }
            else
            {
                _streakMinutes = 0;
            }
        }

        public static string? ResolveGitRepoPath(string? configuredPath)
        {
            static bool IsGitRepo(string dir) =>
                !string.IsNullOrWhiteSpace(dir) &&
                (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")));

            static string? FindGitRootUpwards(string? startDir)
            {
                if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir)) return null;
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    if (IsGitRepo(dir.FullName)) return dir.FullName;
                    dir = dir.Parent;
                }
                return null;
            }

            if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
            {
                string? directRoot = FindGitRootUpwards(configuredPath);
                if (directRoot != null) return directRoot;

                // If configuredPath is a parent folder of multiple repos, pick the most recently modified repo
                try
                {
                    var subRepos = Directory.GetDirectories(configuredPath)
                        .Where(IsGitRepo)
                        .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                        .FirstOrDefault();
                    if (subRepos != null) return subRepos;
                }
                catch { }
            }

            return FindGitRootUpwards(Directory.GetCurrentDirectory());
        }

        private void CheckGitStatus()
        {
            try
            {
                string? repoPath = ResolveGitRepoPath(ActiveRepoPath);
                if (string.IsNullOrWhiteSpace(repoPath)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "status --porcelain",
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        OnReaction?.Invoke("DIRTY", SlimeResponses.PickRandom(SlimeResponses.UncommittedResponses));
                        return;
                    }
                }

                psi.Arguments = "log @{u}..HEAD --oneline";
                using var pushProcess = Process.Start(psi);
                if (pushProcess != null)
                {
                    string pushOutput = pushProcess.StandardOutput.ReadToEnd();
                    pushProcess.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(pushOutput))
                    {
                        OnReaction?.Invoke("PUSH", SlimeResponses.PickRandom(SlimeResponses.UnpushedResponses));
                    }
                }
            }
            catch { }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                // Om hon sov tidigare och vi nu trycker på en tangent -> väck henne direkt!
                if (_instance != null && _instance._isCurrentlyAfk)
                {
                    _instance._isCurrentlyAfk = false;
                    _instance.OnReaction?.Invoke("IDLE", "Oh, you're back!");
                }

                _lastKeyboardActivity = DateTime.Now;
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        protected bool _disposed = false;

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _timer?.Stop();
                    _idleTalkTimer?.Stop();
                }

                if (_hookID != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookID);
                    _hookID = IntPtr.Zero;
                }

                _instance = null;
                _disposed = true;
            }
        }

        private const int WH_KEYBOARD_LL = 13;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}