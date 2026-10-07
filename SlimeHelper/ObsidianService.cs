using System.IO;

namespace SlimeHelper
{
    public static class ObsidianService
    {
        // Mappar som aldrig innehåller relevanta anteckningar (brus, genererat, versionshantering).
        // Medvetet konservativ lista så att vanliga anteckningsmappar i ett Obsidian-valv inte döljs.
        private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".obsidian", ".trash", ".git", "node_modules", "bin", "obj", ".vs", ".idea",
            "__pycache__", "venv", ".venv"
        };

        private const long MaxFileBytes = 1_000_000; // hoppa över enorma .md-filer

        /// <summary>
        /// Listar .md-filer under root utan att gå ner i uteslutna mappar (node_modules, bin, obj, .git ...)
        /// och utan att följa symlänkar/junctions.
        /// </summary>
        private static IEnumerable<string> EnumerateMarkdownFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();

                List<string> files;
                List<string> subDirs;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.md").ToList();
                    subDirs = Directory.EnumerateDirectories(dir).ToList();
                }
                catch
                {
                    continue; // ingen åtkomst eller mappen försvann
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                foreach (var sub in subDirs)
                {
                    if (ExcludedDirNames.Contains(Path.GetFileName(sub))) continue;
                    if (IsLink(sub)) continue;
                    pending.Push(sub);
                }
            }
        }

        private static bool IsLink(string path)
        {
            try { return new DirectoryInfo(path).LinkTarget != null; }
            catch { return false; }
        }

        private static bool IsTooLarge(string file)
        {
            try { return new FileInfo(file).Length > MaxFileBytes; }
            catch { return true; }
        }

        // 1. Scanner för relevanta anteckningar baserat på användarens fråga
        public static List<string> SearchVaultContent(string vaultPath, string query, int maxResults = 3)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath) || string.IsNullOrWhiteSpace(query))
                return results;

            try
            {
                // Plocka ut relevanta sökord (hoppa över vanliga korta ord)
                var keywords = query.Split(new[] { ' ', '?', '!', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(w => w.Length > 3)
                                    .Select(w => w.ToLower())
                                    .ToList();

                if (keywords.Count == 0) return results;

                foreach (var file in EnumerateMarkdownFiles(vaultPath))
                {
                    if (IsTooLarge(file))
                        continue;

                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string text = File.ReadAllText(file);

                    // Kolla om filnamnet eller innehållet matchar något sökord
                    bool match = keywords.Any(k => fileName.ToLower().Contains(k) || text.ToLower().Contains(k));

                    if (match)
                    {
                        // Hämta ett utdrag runt träffen eller början av filen
                        string snippet = ExtractSnippet(text, keywords);
                        results.Add($"[Note: {fileName}] -> {snippet}");

                        if (results.Count >= maxResults)
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error searching Obsidian: {ex.Message}");
            }

            return results;
        }

        // 2. Scanner för oavslutade To-Dos
        public static List<string> GetUnfinishedTodos(string vaultPath, int maxItems = 5)
        {
            var todos = new List<string>();
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
                return todos;

            try
            {
                foreach (var file in EnumerateMarkdownFiles(vaultPath))
                {
                    if (IsTooLarge(file))
                        continue;

                    var lines = File.ReadAllLines(file);
                    foreach (var line in lines)
                    {
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("- [ ]"))
                        {
                            string item = trimmed.Substring(5).Trim();
                            if (!string.IsNullOrEmpty(item))
                            {
                                todos.Add(item);
                                if (todos.Count >= maxItems)
                                    return todos;
                            }
                        }
                    }
                }
            }
            catch { }

            return todos;
        }

        // 3. Scanner för Dagens Anteckning
        public static string GetDailyNoteSnippet(string vaultPath)
        {
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
                return string.Empty;

            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                var dailyFile = EnumerateMarkdownFiles(vaultPath)
                    .FirstOrDefault(f => Path.GetFileName(f).Contains(today, StringComparison.OrdinalIgnoreCase));

                if (dailyFile != null && File.Exists(dailyFile))
                {
                    string content = File.ReadAllText(dailyFile);
                    return content.Length > 250 ? content.Substring(0, 250) + "..." : content;
                }
            }
            catch { }

            return string.Empty;
        }

        // Hjälpmetod för att plocka ut relevanta meningar
        private static string ExtractSnippet(string content, List<string> keywords)
        {
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (keywords.Any(k => line.ToLower().Contains(k)))
                {
                    string cleanLine = line.Trim();
                    return cleanLine.Length > 150 ? cleanLine.Substring(0, 150) + "..." : cleanLine;
                }
            }
            return content.Length > 150 ? content.Substring(0, 150).Trim() + "..." : content.Trim();
        }
    }
}