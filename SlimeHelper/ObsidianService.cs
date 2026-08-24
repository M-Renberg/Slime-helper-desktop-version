using System.IO;

namespace SlimeHelper
{
    public static class ObsidianService
    {
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

                var files = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    if (file.Contains(".obsidian") || file.Contains(".trash"))
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
                var files = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.Contains(".obsidian") || file.Contains(".trash"))
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
                var dailyFile = Directory.GetFiles(vaultPath, $"*{today}*.md", SearchOption.AllDirectories).FirstOrDefault();

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