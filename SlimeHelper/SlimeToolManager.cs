using System.IO;
using System.Text.RegularExpressions;

namespace SlimeHelper
{
    public static class SlimeToolManager
    {
        // Mappar som aldrig ska visas i fillistan (brus / genererat)
        private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", "packages", "dist", "build", "out",
            "target", "__pycache__", "venv", ".venv", "coverage", "publish"
        };

        private const int ListMaxDepth = 4;
        private const int ListMaxEntries = 300;
        private const int ListMaxFilesPerDir = 15;

        private const int MaxReadsPerResponse = 5;
        private const int MaxReadChars = 20000;

        // --- SKYDD: känsliga filer och skrivförbjudna mappar ---
        // Mönster som aldrig får läsas eller skrivas (hemligheter, nycklar, inloggningsuppgifter).
        // Lägg till fler här vid behov. * fungerar som jokertecken.
        private static readonly string[] SensitiveFilePatterns =
        {
            ".env", ".env.*",
            "*.pem", "*.key", "*.pfx", "*.p12", "*.keystore", "*.jks",
            "id_rsa*", "id_dsa*", "id_ecdsa*", "id_ed25519*",
            "secrets.json", "secrets.yml", "secrets.yaml", "secrets.toml", "secrets.xml", "secrets.ini", "secrets.txt",
            "secret.json", "secret.yml", "secret.yaml",
            "*.secrets.json", "local.settings.json",
            "credentials", "credentials.json", "credentials.yml", "credentials.yaml", "credentials.xml", "credentials.ini", "credentials.txt",
            ".credentials", ".npmrc", ".netrc", ".pgpass", ".git-credentials"
        };

        // Mallfiler utan riktiga hemligheter får läsas
        private static readonly HashSet<string> SafeTemplateNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".env.example", ".env.sample", ".env.template", ".env.dist"
        };

        // Mappar vars innehåll aldrig får läsas
        private static readonly HashSet<string> SensitiveDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".ssh", ".aws", ".gnupg"
        };

        // Mappar där hon aldrig får skriva (git-interna filer och hooks körs som kod)
        private static readonly HashSet<string> NoWriteDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".githooks", ".ssh", ".aws", ".gnupg"
        };

        // Modellen härmar historikformatet ("System: ...", "Slime: ...") och hittar på egna turer.
        private static readonly Regex FakeTurnRegex =
            new(@"^[ \t]*(?:System|Slime|User):[ \t]|\[SYSTEM INJECTION", RegexOptions.Multiline);

        /// <summary>
        /// Tar bort ett inledande "Slime:" och klipper svaret vid första påhittade "System:/Slime:/User:"-tur,
        /// så att bara hennes egentliga svar (och riktiga verktygstaggar före det) behålls.
        /// </summary>
        public static string StripHallucinatedTurns(string response)
        {
            if (string.IsNullOrEmpty(response)) return response;

            response = Regex.Replace(response, @"^\s*Slime:[ \t]*", "");

            var m = FakeTurnRegex.Match(response);
            return m.Success ? response.Substring(0, m.Index).TrimEnd() : response;
        }

        public static string GetToolInstructions(string targetRepoPath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\n[AVAILABLE TOOLS - CRITICAL SYSTEM REQUIREMENT]");
            sb.AppendLine("You are an AUTONOMOUS AGENT. DO NOT tell the user to run bash/terminal commands (like 'touch' or 'echo'). DO NOT just provide code blocks for the user to copy-paste.");
            sb.AppendLine("You MUST execute actions yourself by outputting EXACTLY these tags in your response. The system will parse them and write the files automatically.");

            // NYTT: Stenhård regel för Obsidian och minnet!
            sb.AppendLine("\n*** CRITICAL RULE: OBSIDIAN & MEMORY ***");
            sb.AppendLine("If the user mentions 'Obsidian', 'memory', 'brain', or asks for a 'to-do list', they are ALWAYS referring to your internal knowledge base. You MUST use the [SAVE_NOTE] tool for these requests. NEVER use [WRITE_DOC] or [WRITE_CODE] for Obsidian or memory tasks.");

            sb.AppendLine("\n1. SAVE TO INTERNAL MEMORY (SlimeBrain / Obsidian):");
            sb.AppendLine("Use this to save personal notes, to-do lists, or remember things in your own brain.");
            sb.AppendLine("[SAVE_NOTE: filename.md]\n# content\n[/SAVE_NOTE]");

            if (!string.IsNullOrEmpty(targetRepoPath))
            {
                sb.AppendLine($"\n2. WRITE DOCUMENTATION TO WORKSPACE ({targetRepoPath}):");
                sb.AppendLine("Use this ONLY to create readmes, instructions, or markdown files directly in the user's project code.");
                sb.AppendLine("[WRITE_DOC: relative_path/filename.md]\n# content\n[/WRITE_DOC]");

                sb.AppendLine($"\n3. WRITE SOURCE CODE TO WORKSPACE ({targetRepoPath}):");
                sb.AppendLine("Use this ONLY to create or update source code files (.cs, .js, .ts, etc.) in the user's project.");
                sb.AppendLine("[WRITE_CODE: relative_path/filename.ext]\n// raw code content here\n[/WRITE_CODE]");

                sb.AppendLine($"\n4. READ FILE FROM WORKSPACE ({targetRepoPath}):");
                sb.AppendLine("To read an existing file's content before editing, use:");
                sb.AppendLine("[READ_FILE: relative_path/filename.ext]");
                sb.AppendLine("IMPORTANT: ALL paths are relative to the workspace root. NEVER prefix paths with the repo name or the #tag (the repo's README is simply README.md, not repo/README.md).");
                sb.AppendLine("You can read several files in ONE response by outputting several [READ_FILE: ...] tags (max 5 per response). Large files are truncated.");

                sb.AppendLine($"\n5. LIST FILES IN WORKSPACE ({targetRepoPath}):");
                sb.AppendLine("To see the project's folder/file structure, use this BEFORE guessing file names. Use '.' for the project root, or a relative folder path:");
                sb.AppendLine("[LIST_FILES: .]");
                sb.AppendLine("[LIST_FILES: src/Services]");
                sb.AppendLine("After you see the listing, use READ_FILE to open the files you actually need.");

                sb.AppendLine("\nPROTECTED FILES: secrets and credentials (.env files, private keys, certificates, credentials files) and .git internals can NEVER be read or written. Files marked (protected) in a listing are off-limits - do not try to open them. If the user asks for one, explain that it is protected.");
            }

            return sb.ToString();
        }

        public static string ProcessResponse(string response, string activeThread, string targetRepoPath, SlimeBrainManager brainManager, out string followUpPrompt)
        {
            string cleanResponse = response;
            followUpPrompt = "";

            // Verktyg 1 (Spara i hjärnan)
            cleanResponse = ExtractAndExecute(cleanResponse, "[SAVE_NOTE:", "[/SAVE_NOTE]", (filename, content) =>
            {
                brainManager.SaveRepoNote(activeThread, filename, content);
                return $"\n*(Jag sparade anteckningen: {filename} i mitt minne! 🧠)*\n";
            });

            if (!string.IsNullOrEmpty(targetRepoPath))
            {
                // Verktyg 2 (Skriva dokumentation)
                cleanResponse = ExtractAndExecute(cleanResponse, "[WRITE_DOC:", "[/WRITE_DOC]", (filename, content) =>
                {
                    if (!TryWriteFile(targetRepoPath, StripTagPrefix(targetRepoPath, filename, activeThread), content, out string error))
                        return $"\n*(Jag fick inte skriva {filename}: {error} 🔒)*\n";
                    return $"\n*(Jag skapade dokumentationen {filename} i ditt projekt! 📝)*\n";
                });

                // Verktyg 3 (Skriva källkod)
                cleanResponse = ExtractAndExecute(cleanResponse, "[WRITE_CODE:", "[/WRITE_CODE]", (filename, content) =>
                {
                    if (!TryWriteFile(targetRepoPath, StripTagPrefix(targetRepoPath, filename, activeThread), content, out string error))
                        return $"\n*(Jag fick inte skriva {filename}: {error} 🔒)*\n";
                    return $"\n*(Jag skrev källkod till filen {filename} i ditt projekt! 💻)*\n";
                });

                // Fallback: Om hon hittar på "CREATE_FILE" istället för våra officiella taggar
                cleanResponse = ExtractAndExecute(cleanResponse, "[CREATE_FILE:", "[/CREATE_FILE]", (filename, content) =>
                {
                    if (filename.StartsWith(activeThread, StringComparison.OrdinalIgnoreCase))
                    {
                        filename = filename.Substring(activeThread.Length).Trim('/', '\\');
                    }

                    if (!TryWriteFile(targetRepoPath, filename, content, out string error))
                        return $"\n*(Jag fick inte skriva {filename}: {error} 🔒)*\n";
                    return $"\n*(Jag skapade filen {filename} i ditt projekt! 📝)*\n";
                });

                // Verktyg 5 (Lista filer) - körs före READ_FILE så att båda kan användas i samma svar
                cleanResponse = ProcessListFiles(cleanResponse, targetRepoPath, activeThread, ref followUpPrompt);

                // Verktyg 4 (Läsa filer) - flera taggar per svar stöds
                cleanResponse = ProcessReadFiles(cleanResponse, targetRepoPath, activeThread, ref followUpPrompt);
            }

            return cleanResponse.Trim();
        }

        // --- READ_FILE (flera per svar) ---

        private static string ProcessReadFiles(string text, string basePath, string activeThread, ref string followUpPrompt)
        {
            const string tagStart = "[READ_FILE:";
            var injected = new List<string>();
            int reads = 0;

            int idx = text.IndexOf(tagStart);
            while (idx != -1)
            {
                int end = text.IndexOf("]", idx);
                if (end == -1) break;

                string filename = text.Substring(idx + tagStart.Length, end - (idx + tagStart.Length)).Trim();
                string tagBlock = text.Substring(idx, (end + 1) - idx);
                string uiMessage;

                if (reads >= MaxReadsPerResponse)
                {
                    injected.Add($"[SKIPPED: {filename} - only {MaxReadsPerResponse} files can be read per response. Ask for it again if you still need it.]");
                    uiMessage = $"\n*(Jag hann inte läsa {filename} den här gången - max {MaxReadsPerResponse} filer åt gången.)*\n";
                }
                else
                {
                    reads++;
                    string? fullPath = ResolveSafePath(basePath, StripTagPrefix(basePath, filename, activeThread));

                    if (fullPath != null && IsSensitivePath(basePath, fullPath))
                    {
                        injected.Add($"[ACCESS DENIED: {filename} is a protected file (secrets/credentials) and cannot be read. Tell the user that it is protected.]");
                        uiMessage = $"\n*(Jag får inte läsa {filename} - skyddad fil 🔒)*\n";
                    }
                    else if (fullPath != null && File.Exists(fullPath))
                    {
                        string content = File.ReadAllText(fullPath);
                        bool truncated = content.Length > MaxReadChars;
                        if (truncated) content = content.Substring(0, MaxReadChars);

                        injected.Add($"[FILE: {filename}]\n```\n{content}\n```" +
                                     (truncated ? $"\n(File truncated after {MaxReadChars} characters.)" : ""));
                        uiMessage = $"\n*(Jag läser filen {filename}... 📖)*\n";
                    }
                    else
                    {
                        injected.Add($"[FILE NOT FOUND: {filename} does not exist in {basePath}. Tell the user, or use LIST_FILES to find the right path.]");
                        uiMessage = $"\n*(Jag försökte läsa {filename}, men hittade den inte! ❌)*\n";
                    }
                }

                text = text.Replace(tagBlock, uiMessage);
                idx = text.IndexOf(tagStart);
            }

            if (injected.Count > 0)
            {
                string readFollowUp = $"[SYSTEM INJECTION: You asked to read {injected.Count} file(s). Here are the results:]\n\n"
                                      + string.Join("\n\n", injected)
                                      + "\n\nNow continue fulfilling the user's request based on this content.";

                followUpPrompt = string.IsNullOrEmpty(followUpPrompt)
                    ? readFollowUp
                    : followUpPrompt + "\n\n" + readFollowUp;
            }

            return text;
        }

        // --- LIST_FILES ---

        private static string ProcessListFiles(string text, string basePath, string activeThread, ref string followUpPrompt)
        {
            int listIndex = text.IndexOf("[LIST_FILES");
            if (listIndex == -1) return text;

            int endIndex = text.IndexOf("]", listIndex);
            if (endIndex == -1) return text;

            // Stödjer både [LIST_FILES: path] och [LIST_FILES]
            string inner = text.Substring(listIndex + "[LIST_FILES".Length, endIndex - (listIndex + "[LIST_FILES".Length));
            string relative = inner.TrimStart(':').Trim();
            if (string.IsNullOrEmpty(relative)) relative = ".";
            else relative = StripTagPrefix(basePath, relative, activeThread);

            string tagBlock = text.Substring(listIndex, (endIndex + 1) - listIndex);
            string? fullPath = ResolveSafePath(basePath, relative);
            string listFollowUp;
            string uiMessage;

            if (fullPath != null && Directory.Exists(fullPath))
            {
                string tree = BuildTree(fullPath);
                listFollowUp = $"[SYSTEM INJECTION: The user asked you to list {relative}. Here is the file tree (build folders and hidden folders are omitted):]\n\n```\n{tree}\n```\n\nNow continue fulfilling the user's request. Use READ_FILE to open specific files if you need their content.";
                uiMessage = $"\n*(Jag tittar i mappen {relative}... 📂)*\n";
            }
            else
            {
                listFollowUp = $"[SYSTEM INJECTION: You tried to list {relative}, but that folder does not exist in the workspace. Tell the user.]";
                uiMessage = $"\n*(Jag försökte lista {relative}, men hittade ingen sådan mapp! ❌)*\n";
            }

            followUpPrompt = string.IsNullOrEmpty(followUpPrompt)
                ? listFollowUp
                : followUpPrompt + "\n\n" + listFollowUp;

            return text.Replace(tagBlock, uiMessage);
        }

        private static string BuildTree(string rootPath)
        {
            var sb = new System.Text.StringBuilder();
            int count = 0;
            sb.AppendLine(Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");
            AppendTree(rootPath, 1, sb, ref count);

            if (count >= ListMaxEntries)
                sb.AppendLine($"... (listing truncated at {ListMaxEntries} entries - list a subfolder to see more)");

            return sb.ToString().TrimEnd();
        }

        private static void AppendTree(string dir, int depth, System.Text.StringBuilder sb, ref int count)
        {
            if (depth > ListMaxDepth || count >= ListMaxEntries) return;

            string indent = new string(' ', depth * 2);

            try
            {
                // Filer först (begränsat per mapp) så att t.ex. README/package.json i roten alltid syns,
                // och så att en mapp med hundratals filer inte äter hela budgeten.
                var files = Directory.GetFiles(dir)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var f in files.Take(ListMaxFilesPerDir))
                {
                    if (count >= ListMaxEntries) return;
                    string fname = Path.GetFileName(f);
                    sb.AppendLine($"{indent}{fname}{(IsSensitiveFileName(fname) ? " (protected)" : "")}");
                    count++;
                }
                if (files.Count > ListMaxFilesPerDir)
                {
                    sb.AppendLine($"{indent}... (+{files.Count - ListMaxFilesPerDir} more files)");
                }

                var dirs = Directory.GetDirectories(dir)
                    .Where(d =>
                    {
                        string name = Path.GetFileName(d);
                        return !IgnoredDirs.Contains(name)
                               && !(name.StartsWith(".") && !name.Equals(".github", StringComparison.OrdinalIgnoreCase))
                               && !IsLink(d);
                    })
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

                foreach (var d in dirs)
                {
                    if (count >= ListMaxEntries) return;
                    sb.AppendLine($"{indent}{Path.GetFileName(d)}/");
                    count++;
                    AppendTree(d, depth + 1, sb, ref count);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        /// <summary>
        /// AI:n skriver ibland repo-namnet/#taggen som om det vore en mapp ("slimefolder/README.md").
        /// Tar bort prefixet om det inte finns en riktig mapp med det namnet.
        /// "slimefolder" ensamt tolkas som repots rot.
        /// </summary>
        private static string StripTagPrefix(string basePath, string relative, string activeThread)
        {
            string cleaned = (relative ?? "").Trim().Replace('\\', '/').TrimStart('/');
            string tagName = (activeThread ?? "").TrimStart('#');

            if (string.IsNullOrEmpty(tagName) || tagName.Equals("General", StringComparison.OrdinalIgnoreCase))
                return cleaned;

            bool ExistsAsIs()
            {
                string? asIs = ResolveSafePath(basePath, cleaned);
                return asIs != null && (File.Exists(asIs) || Directory.Exists(asIs));
            }

            // Bara taggen/repo-namnet = roten
            if (cleaned.Equals(tagName, StringComparison.OrdinalIgnoreCase) ||
                cleaned.Equals("#" + tagName, StringComparison.OrdinalIgnoreCase))
            {
                return ExistsAsIs() ? cleaned : ".";
            }

            foreach (var prefix in new[] { "#" + tagName + "/", tagName + "/" })
            {
                if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !ExistsAsIs())
                {
                    string stripped = cleaned.Substring(prefix.Length);
                    return stripped.Length == 0 ? "." : stripped;
                }
            }

            return cleaned;
        }

        /// <summary>
        /// Löser en relativ sökväg mot basePath och returnerar null om resultatet hamnar utanför basePath.
        /// </summary>
        private static string? ResolveSafePath(string basePath, string relative)
        {
            try
            {
                char sep = Path.DirectorySeparatorChar;
                char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

                string baseFull = Path.GetFullPath(basePath).TrimEnd(seps) + sep;
                string cleaned = (relative ?? "").Trim().TrimStart('/', '\\');
                string full = Path.GetFullPath(Path.Combine(baseFull, cleaned));
                string fullWithSep = full.TrimEnd(seps) + sep;

                if (!fullWithSep.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) return null;

                // Symlänkar/junctions inuti projektet kan peka utanför det, så de släpps inte igenom
                if (HasLinkBelow(baseFull, full)) return null;

                return full;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Skriver en fil i projektet. Nekar sökvägar utanför projektet, länkar, känsliga filer och skrivförbjudna mappar.
        /// </summary>
        private static bool TryWriteFile(string basePath, string relativeFilename, string content, out string error)
        {
            error = "";

            string? fullPath = ResolveSafePath(basePath, relativeFilename);
            if (fullPath == null) { error = "sökvägen ligger utanför projektet"; return false; }
            if (Directory.Exists(fullPath)) { error = "sökvägen är en mapp"; return false; }
            if (IsSensitivePath(basePath, fullPath)) { error = "skyddad fil (hemligheter/nycklar)"; return false; }
            if (IsNoWritePath(basePath, fullPath)) { error = "skyddad mapp (.git, hooks m.fl.)"; return false; }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? basePath);
                File.WriteAllText(fullPath, content);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // --- Hjälpmetoder för skydd ---

        private static bool IsSensitiveFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || SafeTemplateNames.Contains(name)) return false;
            return SensitiveFilePatterns.Any(p => WildcardMatch(name, p));
        }

        private static bool IsSensitivePath(string basePath, string fullPath)
        {
            if (IsSensitiveFileName(Path.GetFileName(fullPath))) return true;
            return RelativeSegments(basePath, fullPath).Any(seg => SensitiveDirNames.Contains(seg));
        }

        private static bool IsNoWritePath(string basePath, string fullPath)
        {
            return RelativeSegments(basePath, fullPath).Any(seg => NoWriteDirNames.Contains(seg));
        }

        private static string[] RelativeSegments(string basePath, string fullPath)
        {
            string rel = Path.GetRelativePath(basePath, fullPath);
            return rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool WildcardMatch(string text, string pattern)
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
        }

        // Symlänk eller junction (OneDrive-platshållare räknas inte, de saknar LinkTarget)
        private static bool IsLink(string path)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                return info.LinkTarget != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLinkBelow(string baseFull, string full)
        {
            char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string stop = baseFull.TrimEnd(seps);
            string current = full.TrimEnd(seps);

            while (current.Length > stop.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && IsLink(current)) return true;

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length) break;
                current = parent;
            }
            return false;
        }

        private static string ExtractAndExecute(string text, string startTag, string endTag, Func<string, string, string> action)
        {
            string result = text;
            int startIndex = result.IndexOf(startTag);
            int safetyLimit = 0;

            while (startIndex != -1 && safetyLimit++ < 10)
            {
                int endHeaderIndex = result.IndexOf("]", startIndex);
                if (endHeaderIndex != -1)
                {
                    string filename = result.Substring(startIndex + startTag.Length, endHeaderIndex - (startIndex + startTag.Length)).Trim();
                    int endBlockIndex = result.IndexOf(endTag, endHeaderIndex);

                    if (endBlockIndex != -1)
                    {
                        string content = result.Substring(endHeaderIndex + 1, endBlockIndex - (endHeaderIndex + 1)).Trim();
                        string uiMessage = action(filename, content);
                        string fullBlock = result.Substring(startIndex, (endBlockIndex + endTag.Length) - startIndex);
                        result = result.Replace(fullBlock, uiMessage);
                        startIndex = result.IndexOf(startTag);
                        continue;
                    }
                }
                startIndex = result.IndexOf(startTag, startIndex + startTag.Length);
            }
            return result;
        }
    }
}