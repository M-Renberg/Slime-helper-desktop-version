using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SlimeHelper
{
    public class GlobalWordWatcher : IDisposable
    {
        private static IntPtr _hookID = IntPtr.Zero;
        private static LowLevelKeyboardProc? _proc;
        private static readonly StringBuilder _wordBuffer = new();
        private static readonly StringBuilder _recentBuffer = new();
        private const int MaxBufferLength = 25;
        private const int MaxRecentLength = 45;

        public event Action<string, string>? OnReaction;

        /// <summary>
        /// Fired on any global key press so ActivityWatcher can share a single WH_KEYBOARD_LL OS hook.
        /// </summary>
        public static event Action? OnGlobalKeyActivity;

        public static bool IsHookActive => _hookID != IntPtr.Zero;

        public GlobalWordWatcher()
        {
            _proc = HookCallback;
            _hookID = SetHook(_proc);
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                OnGlobalKeyActivity?.Invoke();

                if (wParam == (IntPtr)0x0100) // WM_KEYDOWN
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    char keyChar = KeyCodeToChar(vkCode);

                    if (keyChar != '\0')
                    {
                        Instance?.ProcessChar(keyChar);
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public static GlobalWordWatcher? Instance { get; private set; }

        public void RegisterInstance()
        {
            Instance = this;
        }

        public void ProcessChar(char keyChar)
        {
            if (keyChar == '\b') // Backspace
            {
                if (_wordBuffer.Length > 0)
                    _wordBuffer.Remove(_wordBuffer.Length - 1, 1);
                if (_recentBuffer.Length > 0)
                    _recentBuffer.Remove(_recentBuffer.Length - 1, 1);
                return;
            }

            char normalizedChar = (keyChar == '\n' || keyChar == '\r') ? ' ' : char.ToLowerInvariant(keyChar);
            _recentBuffer.Append(normalizedChar);
            if (_recentBuffer.Length > MaxRecentLength)
            {
                _recentBuffer.Remove(0, _recentBuffer.Length - MaxRecentLength);
            }

            if (keyChar == ' ' || keyChar == '\n' || keyChar == '\r' || keyChar == ';' || keyChar == ')')
            {
                if (keyChar == ';' || keyChar == ')')
                {
                    if (_wordBuffer.Length < MaxBufferLength)
                        _wordBuffer.Append(normalizedChar);
                }

                string completedWord = _wordBuffer.ToString().ToLowerInvariant().Trim();
                AnalyzeWord(completedWord);
                _wordBuffer.Clear();
            }
            else
            {
                if (_wordBuffer.Length < MaxBufferLength)
                    _wordBuffer.Append(normalizedChar);
            }
        }

        public void ResetBuffers()
        {
            _wordBuffer.Clear();
            _recentBuffer.Clear();
        }

        private void AnalyzeWord(string word)
        {
            string recentPhrase = _recentBuffer.ToString();
            var match = MatchTrigger(word, recentPhrase);
            if (match.HasValue)
            {
                _recentBuffer.Clear();
                OnReaction?.Invoke(match.Value.Status, match.Value.Message);
            }
        }

        public static (string Status, string Message)? MatchTrigger(string word, string recentPhrase = "")
        {
            string w = (word ?? "").ToLowerInvariant().Trim();
            string phrase = string.IsNullOrWhiteSpace(recentPhrase) ? w : recentPhrase.ToLowerInvariant();

            if (w.Length < 3 && phrase.Length < 3) return null;

            // Multi-word and symbol phrase triggers checked first against the rolling phrase buffer
            if (phrase.Contains("while(true)") || phrase.Contains("for(;;)"))
            {
                return ("ERROR", "Wait... Eternity loop?!?!?!'");
            }
            if (phrase.Contains("return null") || w.Contains("null;"))
            {
                return ("PUSH", "Null? Are you sure about that?");
            }
            if (phrase.Contains("!important"))
            {
                return ("ANNOYED", "Cheater! Dont use !important!");
            }
            if (phrase.Contains("drop table") || phrase.Contains("delete from") || w.Contains("truncate"))
            {
                return ("POKE", "Wait! Don't delete the database!");
            }
            if (phrase.Contains("select *"))
            {
                return ("STREAK", "Give me ALL the data!");
            }
            if (phrase.Contains("127.0.0.1") || w.Contains("localhost"))
            {
                return ("FUNNY", "I found the connection!");
            }
            if ((w.Contains("any") && w.Contains(":")) || phrase.Contains(": any") || phrase.Contains(":any"))
            {
                return ("FUNNY", "Type safety? Never heard of it?");
            }

            if (w.Length < 3) return null;

            // Single-word triggers
            if (w.Contains("console") || w.Contains("print") || w.Contains("writeline"))
            {
                return ("BREAK", SlimeResponses.PickRandom(SlimeResponses.ConsoleResponses));
            }
            if (w.Contains("debug"))
            {
                return ("POKE", SlimeResponses.PickRandom(SlimeResponses.DebugResponses));
            }
            if (w.Contains("1337") || w.Contains("hacker") || w.Contains("root"))
            {
                return ("STREAK", SlimeResponses.PickRandom(SlimeResponses.CoolResponses));
            }
            if (w.Contains("fuck") || w.Contains("damn") || w.Contains("fucking") || w.Contains("shit"))
            {
                return ("ANNOYED", SlimeResponses.PickRandom(SlimeResponses.SwearResponses));
            }
            if (w.Contains("slime"))
            {
                return ("STREAK", "We're talking about me?");
            }
            if (w.Contains("todo") || w.Contains("fixme"))
            {
                return ("TIRED", SlimeResponses.PickRandom(SlimeResponses.TodoResponses));
            }
            if (w.Contains("foo") || w.Contains("bar") || w.Contains("temp"))
            {
                return ("FUNNY", SlimeResponses.PickRandom(SlimeResponses.FunnyResponses));
            }
            if (w.Contains("http") || w.Contains("www.") || w.Contains("stackoverflow"))
            {
                return ("FUNNY", "Ctrl+C, Ctrl+V champion!");
            }
            if (w.Contains("async") || w.Contains("await") || w.Contains("thread.sleep") || phrase.Contains("thread.sleep"))
            {
                return ("SLEEP", "Zzz... Yeah I'm waiting...");
            }
            if (w.Contains("coffee") || w.Contains("drink") || w.Contains("pizza") || w.Contains("snack"))
            {
                return ("BREAK", "Did you say food? I want some! Give me!");
            }
            if (w.Contains("sudo"))
            {
                return ("STREAK", "Yes master!");
            }
            if (w.Contains("regex") || w.Contains("regexp"))
            {
                return ("WARNING", "Magic spells? I don't speak RegEx");
            }
            if (w.Contains("copilot") || w.Contains("chatgpt") || w.Contains("generate"))
            {
                return ("STREAK", "Am I being replaced by another AI?");
            }
            if (w.Contains("password") || w.Contains("secret"))
            {
                return ("WARNING", "Don't hardcode secrets!");
            }

            return null;
        }

        public static char KeyCodeToChar(int vkCode, bool? shiftOverride = null)
        {
            bool shift = shiftOverride ?? ((GetAsyncKeyState(0x10) & 0x8000) != 0);

            if (vkCode >= 65 && vkCode <= 90) // A-Z
            {
                return shift ? (char)vkCode : (char)(vkCode + 32);
            }

            if (vkCode >= 48 && vkCode <= 57) // Top-row 0-9 and Shift symbols (US + Nordic layouts)
            {
                if (shift)
                {
                    return vkCode switch
                    {
                        49 => '!', // Shift+1
                        56 => '(', // Shift+8 (Nordic '(')
                        57 => ')', // Shift+9 (Nordic ')' / US '(')
                        48 => ')', // Shift+0 (US ')')
                        _ => (char)vkCode
                    };
                }
                return (char)vkCode;
            }

            if (vkCode >= 96 && vkCode <= 105) // Numpad 0-9
            {
                return (char)('0' + (vkCode - 96));
            }

            if (vkCode == 106) return '*'; // Numpad multiply
            if (vkCode == 32) return ' ';
            if (vkCode == 13) return '\n';
            if (vkCode == 8) return '\b';
            if (vkCode == 186) return shift ? ':' : ';'; // US OEM_1 (; / :)
            if (vkCode == 188 && shift) return ';';      // Nordic Shift+, -> ';'
            if (vkCode == 190) return shift ? ':' : '.'; // Period or Nordic Shift+. -> ':'
            if (vkCode == 110) return '.';               // Numpad decimal
            if (vkCode == 191 && shift) return '*';      // Nordic Shift+' -> '*'

            return '\0';
        }

        public void Dispose()
        {
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
            if (Instance == this) Instance = null;
            GC.SuppressFinalize(this);
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
        private static extern short GetAsyncKeyState(int vKey);
    }
}