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
        private int _gitCheckCounter = 0; // Håller koll på när vi ska köra git-koll

        public event Action<string, string>? OnReaction;

        private readonly DispatcherTimer _idleTalkTimer;

        private static IntPtr _hookID = IntPtr.Zero;
        private static LowLevelKeyboardProc? _proc;
        private static DateTime _lastKeyboardActivity = DateTime.Now;

        public ActivityWatcher()
        {
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
                    OnReaction?.Invoke("IDLE", SlimeResponses.PickRandom(SlimeResponses.IdleThoughts));
                }
            };
            _idleTalkTimer.Start();
        }

        private void CheckActivity(object? sender, EventArgs e)
        {
            double inactiveMinutes = (DateTime.Now - _lastKeyboardActivity).TotalMinutes;

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

            if (_isCurrentlyAfk)
            {
                _isCurrentlyAfk = false;
                OnReaction?.Invoke("IDLE", "Oh, you're back!");
            }

            // Git-koll var 5:e minut (30 sekunder * 10 = 5 minuter)
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

            _streakMinutes++;
            if (_streakMinutes >= 5 && _streakMinutes % 10 == 0)
            {
                OnReaction?.Invoke("STREAK", $"You're on fire! {_streakMinutes} min streak!");
            }
        }

        private void CheckGitStatus()
        {
            try
            {
                // Kör git status för mappen där appen körs (eller anpassa sökvägen till ditt repo)
                string repoPath = Directory.GetCurrentDirectory();

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

                    // Om output inte är tom har vi ocommittade ändringar
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        OnReaction?.Invoke("DIRTY", SlimeResponses.PickRandom(SlimeResponses.UncommittedResponses));
                        return;
                    }
                }

                // Kolla även om vi har opushade commits (ahead)
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
            catch
            {
                // Ignorera om git inte finns tillgängligt i mappen
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

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
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