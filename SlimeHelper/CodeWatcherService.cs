using System.IO;

namespace SlimeHelper
{
    public class CodeWatcherService : IDisposable
    {
        private FileSystemWatcher? _watcher;
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
                OnReaction?.Invoke("STREAK", "Writing tests? I love testing new things");
            else
                OnReaction?.Invoke("DIRTY", "Making a new file are we?!");
        }

        private void HandleFileDeleted(string path)
        {
            if (IsIgnoredPath(path)) return;
            OnReaction?.Invoke("ANNOYED", "Well... we didnt need that anyway?");
        }

        private void HandleFileChanged(string path)
        {
            if (IsIgnoredPath(path)) return;

            try
            {
                var fileInfo = new FileInfo(path);

                // Anti-vibe coding / Paste-detektering vid större filändringar
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
                        OnReaction?.Invoke("ANNOYED", SlimeResponses.PickRandom(SlimeResponses.CopyPasteResponses));
                        _pasteCount = 0;
                    }
                }
            }
            catch { }
        }

        private static bool IsIgnoredPath(string path)
        {
            return path.Contains(".git") || path.Contains(".vs") || path.Contains("bin") ||
                   path.Contains("obj") || path.Contains("node_modules") || path.Contains(".obsidian");
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}