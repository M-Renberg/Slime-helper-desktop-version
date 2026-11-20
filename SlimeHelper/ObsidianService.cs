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
        public static IEnumerable<string> GetMarkdownFiles(string root) =>
            EnumerateMarkdownFiles(root);

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

        // 1. Ranked scanner for relevant notes based on the user's query
        public static List<string> SearchVaultContent(string vaultPath, string query, int maxResults = 3)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath) || string.IsNullOrWhiteSpace(query))
                return results;

            try
            {
                // Extract relevant keywords (> 3 characters)
                var keywords = query.Split(new[] { ' ', '?', '!', ',', '.', ';', ':', '(', ')', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(w => w.Length > 3)
                                    .Select(w => w.ToLowerInvariant())
                                    .Distinct()
                                    .ToList();

                if (keywords.Count == 0) return results;

                var scoredMatches = new List<(string Formatted, int Score, string FileName)>();

                foreach (var file in EnumerateMarkdownFiles(vaultPath))
                {
                    if (IsTooLarge(file))
                        continue;

                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string fileNameLower = fileName.ToLowerInvariant();
                    string text = File.ReadAllText(file);
                    string textLower = text.ToLowerInvariant();

                    int score = 0;
                    foreach (var k in keywords)
                    {
                        if (string.Equals(fileNameLower, k, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 8;
                        }
                        else if (fileNameLower.Contains(k))
                        {
                            score += 5;
                        }

                        if (textLower.Contains(k))
                        {
                            score += 2;
                        }
                    }

                    if (score > 0)
                    {
                        string snippet = ExtractSnippet(text, keywords);
                        scoredMatches.Add(($"[Note: {fileName}] -> {snippet}", score, fileName));
                    }
                }

                results = scoredMatches
                    .OrderByDescending(m => m.Score)
                    .ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(1, maxResults))
                    .Select(m => m.Formatted)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error searching Obsidian: {ex.Message}");
            }

            return results;
        }

        // 2. Scanner for unfinished To-Dos across the vault and optional extra directories
        public static List<string> GetUnfinishedTodos(string vaultPath, int maxItems = 5, params string?[] extraFolders)
        {
            var folders = new List<string?> { vaultPath };
            if (extraFolders != null && extraFolders.Length > 0)
            {
                folders.AddRange(extraFolders);
            }

            return NotesManager.ListUnfinishedTodos(folders.ToArray())
                               .Take(maxItems)
                               .Select(t => t.Text)
                               .ToList();
        }

        // 3. Scanner for Today's Daily Note
        public static bool HasDailyNote(string vaultPath)
        {
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
                return false;

            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                return EnumerateMarkdownFiles(vaultPath)
                    .Any(f => Path.GetFileName(f).Contains(today, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        public static string GetDailyNoteSnippet(string vaultPath, int maxChars = 250)
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
                    int limit = Math.Max(50, maxChars);
                    return content.Length > limit ? content.Substring(0, limit) + "..." : content;
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