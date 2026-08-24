using System.Text;

namespace SlimeHelper
{
    public static class ContextManager
    {
        public static string BuildFullContext(SlimeSettings settings, string userPrompt, string currentVsCodeStatus = "")
        {
            var sb = new StringBuilder();

            // 1. Obsidian: Relevanta sökresultat baserat på frågan
            var matchingNotes = ObsidianService.SearchVaultContent(settings.ObsidianVaultPath, userPrompt);
            if (matchingNotes.Count > 0)
            {
                sb.AppendLine("[Relevant Obsidian Notes Found:]");
                foreach (var note in matchingNotes)
                {
                    sb.AppendLine(note);
                }
            }

            // 2. Obsidian: Aktiva To-Dos
            var todos = ObsidianService.GetUnfinishedTodos(settings.ObsidianVaultPath);
            if (todos.Count > 0)
            {
                sb.AppendLine($"[Obsidian Tasks: {string.Join(", ", todos)}]");
            }

            // 3. Obsidian: Dagens anteckning
            string dailySnippet = ObsidianService.GetDailyNoteSnippet(settings.ObsidianVaultPath);
            if (!string.IsNullOrEmpty(dailySnippet))
            {
                sb.AppendLine($"[Today's Daily Note: {dailySnippet}]");
            }

            // 4. Git / Repos-kontext
            var dirtyRepos = RepoScannerService.ScanDirtyRepos(settings.ReposRootPath);
            if (dirtyRepos.Count > 0)
            {
                sb.AppendLine($"[Uncommitted changes in repos: {string.Join(", ", dirtyRepos)}]");
            }

            // 5. VS Code-status
            if (!string.IsNullOrEmpty(currentVsCodeStatus))
            {
                sb.AppendLine($"[Editor Status: {currentVsCodeStatus}]");
            }

            return sb.ToString().Trim();
        }
    }
}