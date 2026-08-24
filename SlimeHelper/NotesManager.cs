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

            string notesPath = Path.Combine(targetFolder, "slime_notes.md"); //[cite: 1, 5]

            if (!File.Exists(notesPath))
            {
                File.WriteAllText(notesPath, "# Slime Notes & TODOs\n\n- [ ] My first To-Do\n"); //[cite: 1]
                EnsureNotesInGitignore(targetFolder); //[cite: 1]
            }

            return notesPath;
        }

        public static void EnsureNotesInGitignore(string workspaceRoot)
        {
            string gitignorePath = Path.Combine(workspaceRoot, ".gitignore"); //[cite: 5]
            const string notesFileName = "slime_notes.md"; //[cite: 5]

            if (File.Exists(gitignorePath))
            {
                string content = File.ReadAllText(gitignorePath); //[cite: 5]
                var regex = new Regex($@"^{Regex.Escape(notesFileName)}\s*$", RegexOptions.Multiline); //[cite: 5]

                if (!regex.IsMatch(content)) //[cite: 5]
                {
                    string newContent = content.EndsWith("\n") //[cite: 5]
                        ? $"{content}# Slime Helper Notes\n{notesFileName}\n" //[cite: 5]
                        : $"{content}\n\n# Slime Helper Notes\n{notesFileName}\n"; //[cite: 5]

                    File.WriteAllText(gitignorePath, newContent); //[cite: 5]
                }
            }
        }
    }
}