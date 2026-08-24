using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SlimeHelper
{
    public class ActivityWatcher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private readonly DispatcherTimer _timer;
        private DateTime _workStartTime = DateTime.Now;
        private int _streakMinutes = 0;
        private bool _isCurrentlyAfk = false;

        public event Action<string, string>? OnReaction; // Trigger (Status, Message)

        private readonly DispatcherTimer _idleTalkTimer;

        public ActivityWatcher()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += CheckActivity;
            _timer.Start();

            _idleTalkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) }; //[cite: 1]
            _idleTalkTimer.Tick += (s, e) =>
            {
                if (!_isCurrentlyAfk)
                {
                    OnReaction?.Invoke("IDLE", SlimeResponses.PickRandom(SlimeResponses.IdleThoughts)); //[cite: 1]
                }
            };
            _idleTalkTimer.Start();
        }

        private void CheckActivity(object? sender, EventArgs e)
        {
            uint idleTimeMs = GetIdleTimeMs();

            // AFK Check (> 6 minuter inaktivitet i Windows)
            if (idleTimeMs > 6 * 60 * 1000)
            {
                if (!_isCurrentlyAfk)
                {
                    _isCurrentlyAfk = true;
                    _streakMinutes = 0;
                    OnReaction?.Invoke("AFK", "Zzz...");
                }
                return;
            }

            // Användaren är tillbaka från AFK
            if (_isCurrentlyAfk)
            {
                _isCurrentlyAfk = false;
                OnReaction?.Invoke("IDLE", "Oh, you're back!");
            }

            // Paustimer (55 min)
            if ((DateTime.Now - _workStartTime).TotalMinutes >= 55)
            {
                _workStartTime = DateTime.Now;
                OnReaction?.Invoke("BREAK", SlimeResponses.PickRandom(SlimeResponses.BreakResponses));
                return;
            }

            // Streak uppdatering
            _streakMinutes++;
            if (_streakMinutes >= 5 && _streakMinutes % 10 == 0)
            {
                OnReaction?.Invoke("STREAK", $"You're on fire! {_streakMinutes} min streak!");
            }
        }

        private static uint GetIdleTimeMs()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            return (uint)Environment.TickCount - lii.dwTime;
        }
    }
}