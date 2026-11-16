using System.IO;
using System.Text.RegularExpressions;

namespace SlimeHelper
{
    public static class NotesManager
    {
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
    }
}