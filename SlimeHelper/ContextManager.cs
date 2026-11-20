using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SlimeHelper
{
    public class SlimeSettings
    {
        public string CurrentSkin { get; set; } = "Default";
        public string SelectedProvider { get; set; } = "Gemini";
        public string GeminiKey { get; set; } = "";
        public string ClaudeKey { get; set; } = "";
        public string GitHubToken { get; set; } = "";
        public string ObsidianVaultPath { get; set; } = "";
        public string ReposRootPath { get; set; } = "";
        public string ResponseLanguage { get; set; } = "Auto";
        public bool AutoStartWithWindows { get; set; } = false;
        public bool AllowDesktopRoaming { get; set; } = true;
    }

    public static class ContextManager
    {
        private static readonly Regex TodoIntentRegex = new(
            @"\b(todo|todos|to-do|task|tasks|uppdrag|uppgift|uppgifter|att\s+göra|lista|backlog|plan)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex DailyNoteIntentRegex = new(
            @"\b(daily|today|idag|dagbok|dagens|anteckning|journal|morning|briefing)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex VaultSearchIntentRegex = new(
            @"\b(obsidian|vault|note|notes|anteckningar|minne|brain|remember|wrote\s+about|skrev\s+om)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex GitDirtyIntentRegex = new(
            @"\b(dirty|uncommitted|ocommittade|repos|repositories|git\s+status|changes\s+across)\b",
            RegexOptions.IgnoreCase);

        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (text.Length + 3) / 4;
        }

        public static string BuildLanguageRule(SlimeSettings? settings = null, string? userPrompt = null)
        {
            string mode = settings?.ResponseLanguage?.Trim() ?? "English";
            if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                return "[Language Rule]\nYou are Slime, a desktop AI companion. Match the language the user writes in (e.g. reply in Swedish if the user writes in Swedish, or English if they write in English), and never use emojis in what you say.\n";
            }
            if (mode.Equals("Swedish", StringComparison.OrdinalIgnoreCase))
            {
                return "[Language Rule]\nYou are Slime, a desktop AI companion. Always reply in Swedish and never use emojis in what you say.\n";
            }
            return "[Language Rule]\nYou are Slime, a desktop AI companion. You must ALWAYS respond in English and never use emojis in what you say. Regardless of whether the user addresses you in Swedish, English, or any other language, every reply, explanation, and message from you MUST be exclusively in English without emojis. Never reply in Swedish.\n";
        }

        /// <summary>
        /// Builds a low-noise, cache-friendly context summary. Detailed vault notes, To-Do lists,
        /// daily notes, and multi-repo git statuses are only inlined when directly relevant to the
        /// prompt; otherwise compact pointers are provided so the agent can fetch them on demand via tools.
        /// </summary>
        public static string BuildCompactContext(
            SlimeSettings settings,
            string userPrompt,
            string currentVsCodeStatus = "",
            string?[]? extraTodoFolders = null,
            string? activeRepoPath = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BuildLanguageRule(settings, userPrompt));

            var summaryItems = new List<string>();

            // 1. To-Dos: count is cheap; only inline full task texts when the prompt asks about tasks/todos
            var todos = extraTodoFolders != null
                ? ObsidianService.GetUnfinishedTodos(settings.ObsidianVaultPath, 8, extraTodoFolders)
                : ObsidianService.GetUnfinishedTodos(settings.ObsidianVaultPath, 8);

            if (todos.Count > 0)
            {
                if (TodoIntentRegex.IsMatch(userPrompt ?? ""))
                {
                    sb.AppendLine($"[Active To-Do Tasks: {string.Join(", ", todos)}]");
                }
                else
                {
                    summaryItems.Add($"Open To-Dos: {todos.Count}");
                }
            }

            // 2. Today's Daily Note: only inline snippet when prompt asks about today/daily note
            if (!string.IsNullOrWhiteSpace(settings.ObsidianVaultPath) && Directory.Exists(settings.ObsidianVaultPath))
            {
                if (DailyNoteIntentRegex.IsMatch(userPrompt ?? ""))
                {
                    string dailySnippet = ObsidianService.GetDailyNoteSnippet(settings.ObsidianVaultPath, 400);
                    if (!string.IsNullOrEmpty(dailySnippet))
                    {
                        sb.AppendLine($"[Today's Daily Note: {dailySnippet}]");
                    }
                }
                else if (ObsidianService.HasDailyNote(settings.ObsidianVaultPath))
                {
                    summaryItems.Add("Today's Daily Note: available");
                }

                // 3. Obsidian Vault search: only pre-search on vault/note-related prompts
                if (VaultSearchIntentRegex.IsMatch(userPrompt ?? ""))
                {
                    var matchingNotes = ObsidianService.SearchVaultContent(settings.ObsidianVaultPath, userPrompt ?? "", 3);
                    if (matchingNotes.Count > 0)
                    {
                        sb.AppendLine("[Relevant Obsidian Notes Found:]");
                        foreach (var note in matchingNotes)
                        {
                            sb.AppendLine(note);
                        }
                    }
                }
                else
                {
                    summaryItems.Add("Obsidian Vault: connected");
                }
            }

            // 4. Git status: avoid scanning every repo in ReposRootPath unless asked
            if (GitDirtyIntentRegex.IsMatch(userPrompt ?? ""))
            {
                var dirtyRepos = RepoScannerService.ScanDirtyRepos(settings.ReposRootPath);
                if (dirtyRepos.Count > 0)
                {
                    sb.AppendLine($"[Uncommitted changes in repos: {string.Join(", ", dirtyRepos)}]");
                }
            }
            else if (!string.IsNullOrWhiteSpace(activeRepoPath) && Directory.Exists(Path.Combine(activeRepoPath, ".git")))
            {
                summaryItems.Add("Git repo: active");
            }

            if (summaryItems.Count > 0)
            {
                sb.AppendLine($"[Context Summary: {string.Join(" | ", summaryItems)}]");
            }

            // 5. Editor status
            if (!string.IsNullOrEmpty(currentVsCodeStatus))
            {
                sb.AppendLine($"[Editor Status: {currentVsCodeStatus}]");
            }

            return sb.ToString().Trim();
        }

        public static string BuildFullContext(SlimeSettings settings, string userPrompt, string currentVsCodeStatus = "", string?[]? extraTodoFolders = null)
        {
            var sb = new StringBuilder();

            // 0. Language Rule
            sb.AppendLine(BuildLanguageRule(settings, userPrompt));

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