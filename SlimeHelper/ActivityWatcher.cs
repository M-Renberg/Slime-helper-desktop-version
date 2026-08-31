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

        private readonly DispatcherTimer _idleTalkTimer;

        private static IntPtr _hookID = IntPtr.Zero;
        private static LowLevelKeyboardProc? _proc;
        private static DateTime _lastKeyboardActivity = DateTime.Now;

        // En instans-referens så att den statiska hooken kan nå händelsen
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
                    string randomThought = SlimeResponses.PickRandom(SlimeResponses.IdleThoughts);

                    // En array med roliga statusar/animationer hon kan växla mellan
                    string[] randomReactions = { "IDLE", "CUTE", "THINKING", "HURRAY" };
                    string selectedReaction = randomReactions[_rng.Next(randomReactions.Length)];

                    // Skicka med den slumpade reaktionen istället för att alltid köra "IDLE"
                    OnReaction?.Invoke(selectedReaction, randomThought);
                }
            };
            _idleTalkTimer.Start();
        }

        private void CheckActivity(object? sender, EventArgs e)
        {
            double inactiveMinutes = (DateTime.Now - _lastKeyboardActivity).TotalMinutes;

            // Om vi är inaktiva i mer än 6 minuter -> gå in i SLEEP och stanna där!
            if (inactiveMinutes > 6)
            {
                if (!_isCurrentlyAfk)
                {
                    _isCurrentlyAfk = true;
                    _streakMinutes = 0;
                    OnReaction?.Invoke("SLEEP", "Zzz...");
                }
                // VIKTIGT: Returnera direkt här så att inga andra IDLE- eller streak-regler körs medan hon sover!
                return;
            }

            // Om hon var AFK/sover men vi fångar upp att inaktiviteten är borta
            if (_isCurrentlyAfk)
            {
                _isCurrentlyAfk = false;
                OnReaction?.Invoke("IDLE", "Oh, you're back!");
            }

            // Git-koll var 5:e minut
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

        private void CheckGitStatus()
        {
            try
            {
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