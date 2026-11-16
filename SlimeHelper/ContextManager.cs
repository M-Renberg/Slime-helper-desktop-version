using System.Text;

namespace SlimeHelper
{
    public static class ContextManager
    {
        public static string BuildFullContext(SlimeSettings settings, string userPrompt, string currentVsCodeStatus = "", string?[]? extraTodoFolders = null)
        {
            var sb = new StringBuilder();

            // 0. Language Rule
            sb.AppendLine("[Language Rule]");
            sb.AppendLine("You are Slime, a desktop AI companion. You must ALWAYS respond in English and never use emojis in what you say. Regardless of whether the user addresses you in Swedish, English, or any other language, every reply, explanation, and message from you MUST be exclusively in English without emojis. Never reply in Swedish.\n");

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

            // 2. Obsidian & Local slime_notes.md: Active To-Dos
            var todos = extraTodoFolders != null
                ? ObsidianService.GetUnfinishedTodos(settings.ObsidianVaultPath, 8, extraTodoFolders)
                : ObsidianService.GetUnfinishedTodos(settings.ObsidianVaultPath);
            if (todos.Count > 0)
            {
                sb.AppendLine($"[Active To-Do Tasks: {string.Join(", ", todos)}]");
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