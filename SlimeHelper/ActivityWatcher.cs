using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

        // Phase 2: Contextual awareness (Active Window & CPU load)
        private string? _lastWindowCategory = null;
        private readonly Dictionary<string, DateTime> _lastCategoryReactionAt = new(StringComparer.OrdinalIgnoreCase);
        private ulong _prevIdleTime = 0;
        private ulong _prevKernelTime = 0;
        private ulong _prevUserTime = 0;
        private int _highCpuConsecutiveChecks = 0;
        private DateTime _lastHighCpuReactionAt = DateTime.MinValue;

        public event Action<string, string>? OnReaction;

        /// <summary>
        /// Configured repository path or root repositories directory used by CheckGitStatus.
        /// </summary>
        public string? ActiveRepoPath { get; set; }

        /// <summary>
        /// Optional provider for Phase 4 contextual idle chatter (connected to SlimeBrainManager and Focus Timer).
        /// </summary>
        public Func<DateTime, Random, (string Status, string Message)>? CustomIdleChatterProvider { get; set; }

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
            GlobalWordWatcher.OnGlobalKeyActivity += NotifyKeyboardActivity;

            // Only install a fallback OS hook if GlobalWordWatcher's shared hook is not active
            if (!GlobalWordWatcher.IsHookActive)
            {
                _proc = HookCallback;
                _hookID = SetHook(_proc);
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += CheckActivity;
            _timer.Start();

            _idleTalkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            _idleTalkTimer.Tick += (s, e) =>
            {
                if (!_isCurrentlyAfk)
                {
                    var (selectedReaction, randomThought) = CustomIdleChatterProvider != null
                        ? CustomIdleChatterProvider(DateTime.Now, _rng)
                        : PickIdleChatter(DateTime.Now, _rng);

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

            // Git check every 5 minutes (run asynchronously on a background thread so UI never stalls)
            _gitCheckCounter++;
            if (_gitCheckCounter >= 10)
            {
                _gitCheckCounter = 0;
                _ = Task.Run(CheckGitStatusAsync);
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

            // Phase 2: Check CPU load and foreground window context
            if (CheckHighCpuLoad())
            {
                return;
            }

            CheckForegroundWindowContext();
        }

        private bool CheckHighCpuLoad()
        {
            try
            {
                if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
                    return false;

                if (_prevKernelTime == 0 && _prevUserTime == 0)
                {
                    _prevIdleTime = idle;
                    _prevKernelTime = kernel;
                    _prevUserTime = user;
                    return false;
                }

                ulong idleDelta = idle - _prevIdleTime;
                ulong kernelDelta = kernel - _prevKernelTime;
                ulong userDelta = user - _prevUserTime;
                ulong totalSystem = kernelDelta + userDelta;

                _prevIdleTime = idle;
                _prevKernelTime = kernel;
                _prevUserTime = user;

                if (totalSystem == 0) return false;

                double cpuUsage = 100.0 * (1.0 - ((double)idleDelta / totalSystem));
                if (cpuUsage >= 85.0)
                {
                    _highCpuConsecutiveChecks++;
                    if (_highCpuConsecutiveChecks >= 2 && (DateTime.Now - _lastHighCpuReactionAt).TotalMinutes >= 10)
                    {
                        _highCpuConsecutiveChecks = 0;
                        _lastHighCpuReactionAt = DateTime.Now;
                        var reaction = SlimeResponses.GetContextReaction("HIGH_CPU", _rng);
                        if (reaction.HasValue)
                        {
                            OnReaction?.Invoke(reaction.Value.Status, reaction.Value.Message);
                            return true;
                        }
                    }
                }
                else
                {
                    _highCpuConsecutiveChecks = 0;
                }
            }
            catch { }

            return false;
        }

        private void CheckForegroundWindowContext()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                var sb = new StringBuilder(256);
                _ = GetWindowText(hwnd, sb, sb.Capacity);
                string windowTitle = sb.ToString();

                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                string processName = "";
                if (pid != 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        processName = proc.ProcessName;
                    }
                    catch { }
                }

                string? category = SlimeResponses.ClassifyActiveWindow(processName, windowTitle);
                if (string.IsNullOrEmpty(category))
                {
                    _lastWindowCategory = null;
                    return;
                }

                // Trigger only when switching into a new category and respecting a 15-minute per-category cooldown
                if (!string.Equals(category, _lastWindowCategory, StringComparison.OrdinalIgnoreCase))
                {
                    _lastWindowCategory = category;
                    var now = DateTime.Now;
                    if (!_lastCategoryReactionAt.TryGetValue(category, out var lastAt) || (now - lastAt).TotalMinutes >= 15)
                    {
                        _lastCategoryReactionAt[category] = now;
                        var reaction = SlimeResponses.GetContextReaction(category, _rng);
                        if (reaction.HasValue)
                        {
                            OnReaction?.Invoke(reaction.Value.Status, reaction.Value.Message);
                        }
                    }
                }
            }
            catch { }
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

        private async Task CheckGitStatusAsync()
        {
            try
            {
                string? repoPath = ResolveGitRepoPath(ActiveRepoPath);
                if (string.IsNullOrWhiteSpace(repoPath)) return;

                string statusOutput = await RunGitBoundedAsync(repoPath, "status --porcelain");
                if (!string.IsNullOrWhiteSpace(statusOutput))
                {
                    _ = _timer.Dispatcher.InvokeAsync(() =>
                        OnReaction?.Invoke("DIRTY", SlimeResponses.PickRandom(SlimeResponses.UncommittedResponses)));
                    return;
                }

                string pushOutput = await RunGitBoundedAsync(repoPath, "log @{u}..HEAD --oneline");
                if (!string.IsNullOrWhiteSpace(pushOutput))
                {
                    _ = _timer.Dispatcher.InvokeAsync(() =>
                        OnReaction?.Invoke("PUSH", SlimeResponses.PickRandom(SlimeResponses.UnpushedResponses)));
                }
            }
            catch { }
        }

        private static async Task<string> RunGitBoundedAsync(string workingDir, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return string.Empty;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(4000))
                {
                    try { process.Kill(true); } catch { }
                    return string.Empty;
                }

                await Task.WhenAll(stdoutTask, stderrTask);
                return process.ExitCode == 0 ? stdoutTask.Result.Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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

        private static void NotifyKeyboardActivity()
        {
            _lastKeyboardActivity = DateTime.Now;
            var inst = _instance;
            if (inst != null && inst._isCurrentlyAfk)
            {
                inst._isCurrentlyAfk = false;
                _ = inst._timer.Dispatcher.InvokeAsync(() => inst.OnReaction?.Invoke("IDLE", "Oh, you're back!"));
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                NotifyKeyboardActivity();
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
                GlobalWordWatcher.OnGlobalKeyActivity -= NotifyKeyboardActivity;

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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ulong lpIdleTime, out ulong lpKernelTime, out ulong lpUserTime);
    }
}