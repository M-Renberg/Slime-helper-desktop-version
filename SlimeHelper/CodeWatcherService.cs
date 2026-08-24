using System.IO;

namespace SlimeHelper
{
    public class CodeWatcherService : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private DateTime _lastCommentTime = DateTime.MinValue;
        private DateTime _lastPasteTime = DateTime.Now;
        private int _pasteCount = 0;
        private const int PasteLimit = 4;
        private readonly TimeSpan _pasteResetTime = TimeSpan.FromMinutes(4);

        public event Action<string, string>? OnReaction;

        public void Start(string reposPath)
        {
            if (string.IsNullOrWhiteSpace(reposPath) || !Directory.Exists(reposPath))
                return;

            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(reposPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                Filter = "*.*"
            };

            _watcher.Created += (s, e) => HandleFileCreated(e.FullPath);
            _watcher.Deleted += (s, e) => HandleFileDeleted(e.FullPath);
            _watcher.Changed += (s, e) => HandleFileChanged(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }

        private void HandleFileCreated(string path)
        {
            if (IsIgnoredPath(path)) return;

            string name = Path.GetFileName(path).ToLower();
            if (name.Contains("test"))
                OnReaction?.Invoke("STREAK", "Writing tests? I love testing new things"); //
            else
                OnReaction?.Invoke("DIRTY", "Making a new file are we?!"); //[cite: 1]
        }

        private void HandleFileDeleted(string path)
        {
            if (IsIgnoredPath(path)) return;
            OnReaction?.Invoke("ANNOYED", "Well... we didnt need that anyway?"); //[cite: 1]
        }

        private void HandleFileChanged(string path)
        {
            if (IsIgnoredPath(path)) return;

            try
            {
                var fileInfo = new FileInfo(path);

                // Anti-vibe coding / Paste-detektering vid större filändringar[cite: 1]
                if (fileInfo.Length > 200)
                {
                    var now = DateTime.Now;
                    if (now - _lastPasteTime > _pasteResetTime)
                    {
                        _pasteCount = 0;
                    }

                    _pasteCount++;
                    _lastPasteTime = now;

                    if (_pasteCount >= PasteLimit)
                    {
                        OnReaction?.Invoke("ANNOYED", SlimeResponses.PickRandom(SlimeResponses.CopyPasteResponses)); //[cite: 1]
                        _pasteCount = 0;
                        return;
                    }
                }

                var lines = File.ReadLines(path).Reverse().Take(15).ToList();
                foreach (var line in lines)
                {
                    string l = line.ToLower();

                    // Fullständig lista av reaktioner från extension.ts[cite: 1]
                    if (l.Contains("console.log") || l.Contains("print(") || l.Contains("writeline"))
                        OnReaction?.Invoke("BREAK", SlimeResponses.PickRandom(SlimeResponses.ConsoleResponses)); //[cite: 1]
                    else if (l.Contains("debug"))
                        OnReaction?.Invoke("POKE", SlimeResponses.PickRandom(SlimeResponses.DebugResponses)); //[cite: 1]
                    else if (l.Contains("1337") || l.Contains("hacker") || l.Contains("root"))
                        OnReaction?.Invoke("STREAK", SlimeResponses.PickRandom(SlimeResponses.CoolResponses)); //[cite: 1]
                    else if (l.Contains("fuck") || l.Contains("damn") || l.Contains("fucking") || l.Contains("shit"))
                        OnReaction?.Invoke("ANNOYED", SlimeResponses.PickRandom(SlimeResponses.SwearResponses)); //[cite: 1]
                    else if (l.Contains("slime"))
                        OnReaction?.Invoke("STREAK", "We're talking about me?"); //[cite: 1]
                    else if (l.Contains("todo") || l.Contains("fixme"))
                        OnReaction?.Invoke("TIRED", SlimeResponses.PickRandom(SlimeResponses.TodoResponses)); //[cite: 1]
                    else if (l.Contains("foo") || l.Contains("bar") || l.Contains("temp"))
                        OnReaction?.Invoke("FUNNY", SlimeResponses.PickRandom(SlimeResponses.FunnyResponses)); //[cite: 1]
                    else if (l.Contains("while(true)") || l.Contains("for(;;)"))
                        OnReaction?.Invoke("ERROR", "Wait... Eternity loop?!?!?!'"); //[cite: 1]
                    else if (l.Contains("return null") || l.Contains("null;"))
                        OnReaction?.Invoke("PUSH_NEEDED", "Null? Are you sure about that?"); //[cite: 1]
                    else if (l.Contains("!important"))
                        OnReaction?.Invoke("ANNOYED", "Cheater! Dont use !important!"); //[cite: 1]
                    else if (l.Contains("http") || l.Contains("www.") || l.Contains("stackoverflow"))
                        OnReaction?.Invoke("FUNNY", "Ctrl+C, Ctrl+V champion!"); //[cite: 1]
                    else if (l.Contains("await ") || l.Contains("async ") || l.Contains("thread.sleep"))
                        OnReaction?.Invoke("AFK", "Zzz... Yeah I'm waiting..."); //[cite: 1]
                    else if (l.Contains("coffee") || l.Contains("drink") || l.Contains("pizza") || l.Contains("snack"))
                        OnReaction?.Invoke("BREAK", "Did you say food? I want some! Give me!"); //[cite: 1]
                    else if (l.Contains("sudo "))
                        OnReaction?.Invoke("STREAK", "Yes master!"); //[cite: 1]
                    else if (l.Contains(": any") || l.Contains("as any"))
                        OnReaction?.Invoke("FUNNY", "Type safety? Never heard of it?"); //[cite: 1]
                    else if (l.Contains("try {") || l.Contains("catch (") || l.Contains("try\n") || l.Contains("catch"))
                        OnReaction?.Invoke("POKE", "Preparing for disaster?"); //[cite: 1]
                    else if (l.Contains("localhost") || l.Contains("127.0.0.1"))
                        OnReaction?.Invoke("FUNNY", "I found the connection!"); //[cite: 1]
                    else if (l.Contains("drop table") || l.Contains("delete from") || l.Contains("truncate"))
                        OnReaction?.Invoke("POKE", "Wait! Don't delete the database!"); //[cite: 1]
                    else if (l.Contains("select *"))
                        OnReaction?.Invoke("STREAK", "Give me ALL the data!"); //[cite: 1]
                    else if (l.Contains("git push --force") || l.Contains("git commit -m \"fix\""))
                        OnReaction?.Invoke("STREAK", "Living dangerously I see!"); //[cite: 1]
                    else if (l.Contains("<<<<<<< head"))
                        OnReaction?.Invoke("ERROR", "Merge conflict! Fight!"); //[cite: 1]
                    else if (l.Contains("regex") || l.Contains("regexp") || l.Contains("^.*$"))
                        OnReaction?.Invoke("WARNING", "Magic spells? I don't speak RegEx"); //[cite: 1]
                    else if (l.Contains("copilot") || l.Contains("chatgpt") || l.Contains("generate"))
                        OnReaction?.Invoke("STREAK", "Am I being replaced by another AI?"); //[cite: 1]
                    else if (l.Contains("border:") && l.Contains("red"))
                        OnReaction?.Invoke("FUNNY", "CSS Debugging? Classic moves."); //[cite: 1]
                    else if (l.Contains("password =") || l.Contains("secret ="))
                        OnReaction?.Invoke("WARNING", "Don't hardcode secrets!"); //[cite: 1]
                    else if ((l.Contains("//") || l.Contains("/*") || l.Contains("<!--")) && (DateTime.Now - _lastCommentTime).TotalMilliseconds > 8000)
                    {
                        _lastCommentTime = DateTime.Now;
                        OnReaction?.Invoke("FUNNY", SlimeResponses.PickRandom(SlimeResponses.CommentResponses)); //[cite: 1]
                    }
                    break;
                }
            }
            catch { }
        }

        private static bool IsIgnoredPath(string path)
        {
            return path.Contains(".git") || path.Contains(".vs") || path.Contains("bin") ||
                   path.Contains("obj") || path.Contains("node_modules") || path.Contains(".obsidian");
        }

        public void Dispose() => _watcher?.Dispose();
    }
}