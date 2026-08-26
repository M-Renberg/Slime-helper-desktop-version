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
        private const int MaxBufferLength = 15;

        public event Action<string, string>? OnReaction;

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
            if (nCode >= 0 && wParam == (IntPtr)0x0100) // WM_KEYDOWN
            {
                int vkCode = Marshal.ReadInt32(lParam);
                char keyChar = KeyCodeToChar(vkCode);

                if (keyChar != '\0')
                {
                    if (keyChar == ' ' || keyChar == '.' || keyChar == '\n' || keyChar == '\r')
                    {
                        string completedWord = _wordBuffer.ToString().ToLower().Trim();

                        // Skicka vidare det färdiga ordet för analys
                        Instance?.AnalyzeWord(completedWord);

                        _wordBuffer.Clear();
                    }
                    else if (keyChar == '\b') // Backspace
                    {
                        if (_wordBuffer.Length > 0)
                            _wordBuffer.Remove(_wordBuffer.Length - 1, 1);
                    }
                    else
                    {
                        if (_wordBuffer.Length < MaxBufferLength)
                            _wordBuffer.Append(keyChar);
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

        private void AnalyzeWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word) || word.Length < 3) return;

            // Komplett matchningslista
            if (word.Contains("console") || word.Contains("print") || word.Contains("writeline"))
            {
                OnReaction?.Invoke("BREAK", SlimeResponses.PickRandom(SlimeResponses.ConsoleResponses));
            }
            else if (word.Contains("debug"))
            {
                OnReaction?.Invoke("POKE", SlimeResponses.PickRandom(SlimeResponses.DebugResponses));
            }
            else if (word.Contains("1337") || word.Contains("hacker") || word.Contains("root"))
            {
                OnReaction?.Invoke("STREAK", SlimeResponses.PickRandom(SlimeResponses.CoolResponses));
            }
            else if (word.Contains("fuck") || word.Contains("damn") || word.Contains("fucking") || word.Contains("shit"))
            {
                OnReaction?.Invoke("ANNOYED", SlimeResponses.PickRandom(SlimeResponses.SwearResponses));
            }
            else if (word.Contains("slime"))
            {
                OnReaction?.Invoke("STREAK", "We're talking about me?");
            }
            else if (word.Contains("todo") || word.Contains("fixme"))
            {
                OnReaction?.Invoke("TIRED", SlimeResponses.PickRandom(SlimeResponses.TodoResponses));
            }
            else if (word.Contains("foo") || word.Contains("bar") || word.Contains("temp"))
            {
                OnReaction?.Invoke("FUNNY", SlimeResponses.PickRandom(SlimeResponses.FunnyResponses));
            }
            else if (word.Contains("while(true)") || word.Contains("for(;;)"))
            {
                OnReaction?.Invoke("ERROR", "Wait... Eternity loop?!?!?!'");
            }
            else if (word.Contains("return null") || word.Contains("null;"))
            {
                OnReaction?.Invoke("PUSH_NEEDED", "Null? Are you sure about that?");
            }
            else if (word.Contains("!important"))
            {
                OnReaction?.Invoke("ANNOYED", "Cheater! Dont use !important!");
            }
            else if (word.Contains("http") || word.Contains("www.") || word.Contains("stackoverflow"))
            {
                OnReaction?.Invoke("FUNNY", "Ctrl+C, Ctrl+V champion!");
            }
            else if (word.Contains("async") || word.Contains("await") || word.Contains("thread.sleep"))
            {
                OnReaction?.Invoke("AFK", "Zzz... Yeah I'm waiting...");
            }
            else if (word.Contains("coffee") || word.Contains("drink") || word.Contains("pizza") || word.Contains("snack"))
            {
                OnReaction?.Invoke("BREAK", "Did you say food? I want some! Give me!");
            }
            else if (word.Contains("sudo"))
            {
                OnReaction?.Invoke("STREAK", "Yes master!");
            }
            else if (word.Contains("any") && word.Contains(":"))
            {
                OnReaction?.Invoke("FUNNY", "Type safety? Never heard of it?");
            }
            else if (word.Contains("localhost") || word.Contains("127.0.0.1"))
            {
                OnReaction?.Invoke("FUNNY", "I found the connection!");
            }
            else if (word.Contains("drop table") || word.Contains("delete from") || word.Contains("truncate"))
            {
                OnReaction?.Invoke("POKE", "Wait! Don't delete the database!");
            }
            else if (word.Contains("select *"))
            {
                OnReaction?.Invoke("STREAK", "Give me ALL the data!");
            }
            else if (word.Contains("regex") || word.Contains("regexp"))
            {
                OnReaction?.Invoke("WARNING", "Magic spells? I don't speak RegEx");
            }
            else if (word.Contains("copilot") || word.Contains("chatgpt") || word.Contains("generate"))
            {
                OnReaction?.Invoke("STREAK", "Am I being replaced by another AI?");
            }
            else if (word.Contains("password") || word.Contains("secret"))
            {
                OnReaction?.Invoke("WARNING", "Don't hardcode secrets!");
            }
        }

        private static char KeyCodeToChar(int vkCode)
        {
            if (vkCode >= 65 && vkCode <= 90) // A-Z
            {
                bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                return shift ? (char)vkCode : (char)(vkCode + 32);
            }
            if (vkCode == 32) return ' ';
            if (vkCode == 190 || vkCode == 110) return '.';
            if (vkCode == 13) return '\n';
            if (vkCode == 8) return '\b';

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