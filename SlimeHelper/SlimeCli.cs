using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SlimeCli
{
    internal class Program
    {
        private static readonly string SettingsPath = Path.Combine(Path.GetTempPath(), "slime_settings.json");
        private static readonly string CommandFilePath = Path.Combine(Path.GetTempPath(), "slime_command.json");

        static async Task Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h")
            {
                ShowHelp();
                return;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "help":
                    ShowHelp();
                    break;
                case "gen":
                    if (args.Length < 2) { PrintUsage("slime gen \"<description>\""); return; }
                    await HandleGenerateAsync(string.Join(" ", args.Skip(1)));
                    break;
                case "edit":
                    if (args.Length < 3) { PrintUsage("slime edit <filename> \"<instructions>\""); return; }
                    await HandleEditAsync(args[1], string.Join(" ", args.Skip(2)));
                    break;
                case "todo":
                    if (args.Length < 2) { PrintUsage("slime todo \"<task>\""); return; }
                    HandleAddTodo(string.Join(" ", args.Skip(1)));
                    break;
                case "commit":
                    await HandleCommitAsync();
                    break;
                case "explain":
                    if (args.Length < 2) { PrintUsage("slime explain <filename>"); return; }
                    await HandleExplainAsync(args[1]);
                    break;
                case "sql":
                    if (args.Length < 2) { PrintUsage("slime sql \"<instruction>\""); return; }
                    await HandleSqlAsync(string.Join(" ", args.Skip(1)));
                    break;
                case "skin":
                    if (args.Length < 2) { PrintUsage("slime skin <color>"); return; }
                    await ForwardToDesktopAppAsync($"SET_SKIN:{args[1]}");
                    break;
                default:
                    string fullQuery = string.Join(" ", args);
                    await ForwardToDesktopAppAsync(fullQuery);
                    break;
            }
        }

        private static void ShowHelp()
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(@"
   ____ _ _                  _   _      _                 
  / ___| (_)_ __ ___   ___  | | | | ___| |_ __   ___ _ __ 
  \___ \| | | '_ ` _ \ / _ \ | |_| |/ _ \ | '_ \ / _ \ '__|
   ___) | | | | | | | |  __/ |  _  |  __/ | |_) |  __/ |   
  |____/|_|_|_| |_| |_|\___| |_| |_|\___|_| .__/ \___|_|   
                                           |_|             ");
            Console.ResetColor();
            Console.WriteLine("Slime Helper — Your Context-Aware AI Companion\n");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("COMMANDS:");
            Console.ResetColor();

            PrintCommand("slime <prompt>", "Ask Slime anything with project & Obsidian context.");
            PrintCommand("slime gen \"<prompt>\"", "Generate a new source code file.");
            PrintCommand("slime edit <file> \"<task>\"", "Refactor or edit an existing file with an interactive diff.");
            PrintCommand("slime todo \"<task>\"", "Add a new To-Do item to slime_notes.md.");
            PrintCommand("slime commit", "Generate a commit message based on staged git changes.");
            PrintCommand("slime explain <file>", "Get a quick explanation of a code file.");
            PrintCommand("slime sql \"<task>\"", "Generate raw SQL code for a specific requirement.");
            PrintCommand("slime skin <color>", "Change the Slime skin (e.g. Pink, Green, Default).");
            PrintCommand("slime help", "Display this overview.");
            Console.WriteLine();
        }

        private static void PrintCommand(string cmd, string desc)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  {cmd,-32}");
            Console.ResetColor();
            Console.WriteLine(desc);
        }

        private static void PrintUsage(string usage)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Usage: {usage}");
            Console.ResetColor();
        }

        private static async Task HandleCommitAsync()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("🟢 Reading git diff...");
            Console.ResetColor();

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "diff --cached",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            try
            {
                process.Start();
                string diff = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(diff))
                {
                    Console.WriteLine("No staged changes found. Did you forget to 'git add' your files?");
                    return;
                }

                string prompt = "Analyze the following git diff and write a clean, professional git commit message.\n" +
                                "Format it with a short subject line, an empty line, and a brief bulleted list of changes.\n" +
                                "Do not include any markdown code blocks or introductory text, just the commit message itself.\n\n" +
                                "Diff:\n" + diff;

                string commitMessage = await QueryAiDirectAsync(prompt);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n--- PROPOSED COMMIT MESSAGE ---");
                Console.ResetColor();
                Console.WriteLine(commitMessage);
                Console.WriteLine("-------------------------------\n");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error running git: {ex.Message}");
                Console.ResetColor();
            }
        }

        private static async Task HandleExplainAsync(string targetFile)
        {
            if (!File.Exists(targetFile))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: File '{targetFile}' not found.");
                Console.ResetColor();
                return;
            }

            string content = await File.ReadAllTextAsync(targetFile);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"🟢 Slime is reading {targetFile}...");
            Console.ResetColor();

            string prompt = "Explain the purpose and functionality of the following code.\n" +
                            "Keep it brief, pedagogical, and easy to read in a terminal.\n" +
                            "Code:\n" +
                            "```\n" +
                            content + "\n" +
                            "```";

            string explanation = await QueryAiDirectAsync(prompt);
            Console.WriteLine($"\n{explanation}\n");
        }

        private static async Task HandleSqlAsync(string instructions)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("🟢 Slime is writing SQL...");
            Console.ResetColor();

            string prompt = $"Write a SQL script for the following requirement: {instructions}\n" +
                            "Return ONLY the raw SQL code. No markdown formatting, no explanations.";

            string sql = await QueryAiDirectAsync(prompt);
            sql = CleanCodeBlock(sql);

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n--- GENERATED SQL ---");
            Console.ResetColor();
            Console.WriteLine(sql);
            Console.WriteLine();
        }

        private static async Task HandleEditAsync(string targetFile, string instructions)
        {
            if (!File.Exists(targetFile))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: File '{targetFile}' not found in current directory.");
                Console.ResetColor();
                return;
            }

            string originalContent = await File.ReadAllTextAsync(targetFile);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"🟢 Analyzing {targetFile}...");
            Console.ResetColor();

            string prompt = "You are a precise code editor.\n" +
                            $"Task: {instructions}\n\n" +
                            $"Existing content of {targetFile}:\n" +
                            "```\n" +
                            originalContent + "\n" +
                            "```\n\n" +
                            "Return ONLY the updated file content inside a single code block without introductory explanations.";

            string newContent = await QueryAiDirectAsync(prompt);
            newContent = CleanCodeBlock(newContent);

            if (string.IsNullOrWhiteSpace(newContent) || newContent == originalContent)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No changes proposed by Slime.");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n--- PROPOSED CHANGES ---");
            Console.ResetColor();
            ShowSimpleDiff(originalContent, newContent);

            Console.Write("\nApply these changes? [y/N]: ");
            var key = Console.ReadLine()?.Trim().ToLower();

            if (key == "y" || key == "yes")
            {
                File.WriteAllText($"{targetFile}.bak", originalContent);
                await File.WriteAllTextAsync(targetFile, newContent);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ {targetFile} updated! (Backup saved to {targetFile}.bak)");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Edit cancelled.");
                Console.ResetColor();
            }
        }

        private static async Task HandleGenerateAsync(string instructions)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("🟢 Slime is generating code...");
            Console.ResetColor();

            string prompt = "Generate code for the following specification:\n" +
                            instructions + "\n\n" +
                            "Return the filename as the very first line starting with '// FILENAME: filename.ext' followed immediately by the complete code in a code block.";

            string response = await QueryAiDirectAsync(prompt);
            if (response.StartsWith("Error:") || response.StartsWith("Missing API key"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(response);
                Console.ResetColor();
                return;
            }

            string fileName = "GeneratedFile.cs";
            var lines = response.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (line.Contains("FILENAME:"))
                {
                    fileName = line.Replace("// FILENAME:", "").Replace("FILENAME:", "").Trim();
                    break;
                }
            }

            string code = CleanCodeBlock(response);
            if (code.StartsWith("// FILENAME:"))
            {
                int firstBreak = code.IndexOf('\n');
                if (firstBreak > 0) code = code[(firstBreak + 1)..].TrimStart();
            }

            Console.WriteLine($"\nProposed file: {fileName}\n");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(code.Length > 300 ? code.Substring(0, 300) + "\n..." : code);
            Console.ResetColor();

            Console.Write($"\nCreate file '{fileName}'? [y/N]: ");
            if (Console.ReadLine()?.Trim().ToLower() == "y")
            {
                await File.WriteAllTextAsync(fileName, code);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ Created {fileName}");
                Console.ResetColor();
            }
        }

        private static void HandleAddTodo(string task)
        {
            string noteFile = "slime_notes.md";
            string entry = $"- [ ] {task}\n";

            File.AppendAllText(noteFile, entry);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ Added task to {noteFile}: \"{task}\"");
            Console.ResetColor();
        }

        private static async Task ForwardToDesktopAppAsync(string prompt)
        {
            var payload = new
            {
                Action = "ASK_AI",
                Prompt = prompt,
                WorkingDir = Directory.GetCurrentDirectory(),
                Timestamp = DateTime.UtcNow
            };

            string json = JsonSerializer.Serialize(payload);
            await File.WriteAllTextAsync(CommandFilePath, json);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"🟢 Slime received command...");
            Console.ResetColor();
        }

        private static void ShowSimpleDiff(string oldText, string newText)
        {
            var oldLines = oldText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var newLines = newText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            int max = Math.Max(oldLines.Length, newLines.Length);
            for (int i = 0; i < max; i++)
            {
                string? oldL = i < oldLines.Length ? oldLines[i] : null;
                string? newL = i < newLines.Length ? newLines[i] : null;

                if (oldL != newL)
                {
                    if (oldL != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"- {oldL}");
                    }
                    if (newL != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"+ {newL}");
                    }
                }
            }
            Console.ResetColor();
        }

        private static string CleanCodeBlock(string input)
        {
            var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();

            while (lines.Count > 0 && (lines[0].Trim().StartsWith("```") || lines[0].Trim().StartsWith("// FILENAME:")))
            {
                lines.RemoveAt(0);
            }

            while (lines.Count > 0 && lines[^1].Trim().StartsWith("```"))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return string.Join(Environment.NewLine, lines).Trim();
        }

        private static async Task<string> QueryAiDirectAsync(string prompt)
        {
            if (!File.Exists(SettingsPath)) return "Error: slime_settings.json not found.";

            try
            {
                var settings = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(SettingsPath));

                string provider = settings.TryGetProperty("SelectedProvider", out var provProp) ? provProp.GetString() ?? "Gemini" : "Gemini";
                string key = "";

                if (provider == "Claude" && settings.TryGetProperty("ClaudeKey", out var cProp))
                {
                    key = cProp.GetString() ?? "";
                }
                else if (settings.TryGetProperty("GeminiKey", out var gProp))
                {
                    key = gProp.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(key) || key == "Enter your Key here!")
                {
                    return $"Error: Missing API key for {provider} in settings.";
                }

                using var client = new HttpClient();
                string url = $"[https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=](https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=){key}";

                var body = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
                var response = await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    return candidates[0]
                           .GetProperty("content")
                           .GetProperty("parts")[0]
                           .GetProperty("text")
                           .GetString() ?? "";
                }

                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                {
                    return $"API Error: {errorProp.GetProperty("message").GetString()}";
                }

                return "Error: No response generated by AI.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}