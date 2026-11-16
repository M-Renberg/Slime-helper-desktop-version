using System.IO;
using System.Text.RegularExpressions;

namespace SlimeHelper
{
    public sealed record TodoEntry(int Index, string Text, string FilePath, int LineIndex);

    public static class NotesManager
    {
        private static readonly HashSet<string> IgnoredPlaceholderTodos = new(StringComparer.OrdinalIgnoreCase)
        {
            "My first To-Do",
            "Initialized the project in brain."
        };

        public static string EnsureAndOpenNotes(string targetFolder)
        {
            if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
                return string.Empty;

            string notesPath = Path.Combine(targetFolder, "slime_notes.md");

            if (!File.Exists(notesPath))
            {
                File.WriteAllText(notesPath, "# Slime Notes & TODOs\n\n- [ ] My first To-Do\n");
                EnsureNotesInGitignore(targetFolder);
            }

            return notesPath;
        }

        public static void EnsureNotesInGitignore(string workspaceRoot)
        {
            string gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
            const string notesFileName = "slime_notes.md";

            if (File.Exists(gitignorePath))
            {
                string content = File.ReadAllText(gitignorePath);
                var regex = new Regex($@"^{Regex.Escape(notesFileName)}\s*$", RegexOptions.Multiline);

                if (!regex.IsMatch(content))
                {
                    string newContent = content.EndsWith("\n")
                        ? $"{content}# Slime Helper Notes\n{notesFileName}\n"
                        : $"{content}\n\n# Slime Helper Notes\n{notesFileName}\n";

                    File.WriteAllText(gitignorePath, newContent);
                }
            }
        }

        /// <summary>
        /// Appends a Markdown "- [ ] task" item to slime_notes.md inside targetFolder.
        /// Creates the folder and file header if needed and ensures slime_notes.md is in .gitignore.
        /// </summary>
        public static string AddTodo(string targetFolder, string taskText)
        {
            if (string.IsNullOrWhiteSpace(targetFolder))
                throw new ArgumentException("Target folder cannot be empty.", nameof(targetFolder));

            taskText = (taskText ?? "").Trim();
            if (taskText.StartsWith("add ", StringComparison.OrdinalIgnoreCase) && taskText.Length > 4)
            {
                taskText = taskText[4..].Trim();
            }

            if (string.IsNullOrWhiteSpace(taskText))
                throw new ArgumentException("Task description cannot be empty.", nameof(taskText));

            Directory.CreateDirectory(targetFolder);
            EnsureNotesInGitignore(targetFolder);

            string notesPath = Path.Combine(targetFolder, "slime_notes.md");
            if (!File.Exists(notesPath))
            {
                File.WriteAllText(notesPath, $"# Slime Notes & TODOs\n\n- [ ] {taskText}\n");
            }
            else
            {
                string existing = File.ReadAllText(notesPath);
                string prefix = existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "";
                File.AppendAllText(notesPath, $"{prefix}- [ ] {taskText}\n");
            }

            return notesPath;
        }

        /// <summary>
        /// Scans markdown files across the provided folders for unfinished "- [ ] " items.
        /// Prioritizes slime_notes.md in the primary folder, then scans additional markdown files.
        /// </summary>
        public static List<TodoEntry> ListUnfinishedTodos(params string?[] foldersToScan)
        {
            var results = new List<TodoEntry>();
            var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int nextIndex = 1;

            void ScanFile(string filePath)
            {
                try
                {
                    string full = Path.GetFullPath(filePath);
                    if (!File.Exists(full) || !visitedFiles.Add(full)) return;
                    if (new FileInfo(full).Length > 1_000_000) return;

                    string[] lines = File.ReadAllLines(full);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmed = lines[i].TrimStart();
                        if (trimmed.StartsWith("- [ ]"))
                        {
                            string itemText = trimmed[5..].Trim();
                            if (!string.IsNullOrWhiteSpace(itemText) && !IgnoredPlaceholderTodos.Contains(itemText))
                            {
                                results.Add(new TodoEntry(nextIndex++, itemText, full, i));
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var folder in foldersToScan)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;

                try
                {
                    if (File.Exists(folder) && folder.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        ScanFile(folder);
                        continue;
                    }

                    if (!Directory.Exists(folder)) continue;

                    // Scan slime_notes.md first if present in this directory
                    string directNotes = Path.Combine(folder, "slime_notes.md");
                    if (File.Exists(directNotes))
                    {
                        ScanFile(directNotes);
                    }

                    // Scan markdown files inside the folder (using ObsidianService's safe traversal)
                    foreach (var mdFile in ObsidianService.GetMarkdownFiles(folder))
                    {
                        ScanFile(mdFile);
                    }
                }
                catch { }
            }

            return results;
        }

        /// <summary>
        /// Marks the 1-based TodoEntry at index as completed ("- [x]") in its source Markdown file.
        /// </summary>
        public static (bool Success, string CompletedText, string ErrorMessage) CompleteTodo(int index, params string?[] foldersToScan)
        {
            if (index <= 0)
            {
                return (false, "", "Task number must be 1 or greater.");
            }

            var items = ListUnfinishedTodos(foldersToScan);
            var match = items.FirstOrDefault(t => t.Index == index);
            if (match == null)
            {
                return (false, "", items.Count == 0
                    ? "You have no open To-Do items right now."
                    : $"Task #{index} was not found (you have {items.Count} open item(s)).");
            }

            try
            {
                string[] lines = File.ReadAllLines(match.FilePath);
                if (match.LineIndex < 0 || match.LineIndex >= lines.Length)
                {
                    return (false, "", "The file changed while updating the task.");
                }

                string line = lines[match.LineIndex];
                int bracketPos = line.IndexOf("- [ ]", StringComparison.Ordinal);
                if (bracketPos < 0)
                {
                    return (false, "", "Could not locate '- [ ]' on the target line.");
                }

                lines[match.LineIndex] = string.Concat(line.AsSpan(0, bracketPos), "- [x]", line.AsSpan(bracketPos + 5));
                File.WriteAllLines(match.FilePath, lines);
                return (true, match.Text, "");
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }
    }
}