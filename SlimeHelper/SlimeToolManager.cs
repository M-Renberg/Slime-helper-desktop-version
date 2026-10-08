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

        // Sökning (SEARCH_FILES)
        private const int SearchMaxResultsPerKind = 40;
        private const int SearchMaxLinesPerFile = 3;
        private const int SearchMaxFilesScanned = 3000;
        private const long SearchMaxFileBytes = 500_000;

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svgz",
            ".wav", ".mp3", ".mp4", ".mov", ".avi", ".ogg",
            ".zip", ".7z", ".gz", ".tar", ".rar", ".vsix", ".nupkg",
            ".dll", ".exe", ".pdb", ".so", ".dylib", ".class", ".jar", ".bin", ".dat",
            ".pdf", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".db", ".sqlite"
        };

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

        // Tydliga sökfrågor på svenska/engelska där söktermen går att plocka ut säkert.
        // Används för att söka åt henne direkt i stället för att hoppas att modellen väljer rätt verktyg.
        private static readonly Regex[] SearchIntentPatterns =
        {
            // Svenska
            new(@"\bmed\s+ordet\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\bordet\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\bmed\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})[""'`]?\s+i\s+sig\b", RegexOptions.IgnoreCase),
            new(@"\b(?:innehåller|nämner|refererar\s+till)\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\bvar\s+(?:används|anropas|definieras|refereras)\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\banvändningar\s+av\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),

            // Engelska
            new(@"\b(?:with|has|have)\s+the\s+word\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\bthe\s+word\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\bwith\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})[""'`]?\s+in\s+(?:it|them)\b", RegexOptions.IgnoreCase),
            new(@"\b(?:containing|contains?|mentioning|mentions?)\s+(?:the\s+)?[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase),
            new(@"\bwhere\s+is\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})[""'`]?\s+(?:used|called|defined|referenced)", RegexOptions.IgnoreCase),
            new(@"\busages?\s+of\s+[""'`]?(?<t>[^\s""'`,.?!]{2,})", RegexOptions.IgnoreCase)
        };

        private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "en", "ett", "att", "som", "och", "för", "from", "till", "något", "någon", "några", "that", "this", "det", "den", "ordet", "word"
        };

        /// <summary>
        /// Returnerar söktermen om prompten tydligt ber om en sökning ("filer med ordet X i sig", "var används X",
        /// "files containing X"). Annars null.
        /// </summary>
        public static string? ExtractSearchTerm(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return null;

            foreach (var pattern in SearchIntentPatterns)
            {
                var m = pattern.Match(prompt);
                if (!m.Success) continue;

                string term = m.Groups["t"].Value.Trim();
                if (term.Length >= 2 && !SearchStopWords.Contains(term)) return term;
            }
            return null;
        }

        /// <summary>Kör samma sökning som SEARCH_FILES-verktyget (namn + innehåll).</summary>
        public static string RunSearch(string basePath, string term) => SearchWorkspace(basePath, term);

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

                sb.AppendLine($"\n6. SEARCH THE WORKSPACE ({targetRepoPath}):");
                sb.AppendLine("To find files by NAME or CONTENT (e.g. 'every file mentioning X', 'where is Y used'), use this. It searches all file names and all text file contents (case-insensitive) and returns matching paths with the matching line. NEVER guess which files contain something - search for it:");
                sb.AppendLine("[SEARCH_FILES: text to find]");
                sb.AppendLine("A directory listing (LIST_FILES) is limited and does not show file contents, so it cannot answer 'which files contain X'.");

                sb.AppendLine("\nWHICH TOOL TO USE (the user may write in any language, e.g. Swedish):");
                sb.AppendLine("- Show the folder structure, or what a folder contains -> LIST_FILES.");
                sb.AppendLine("- 'which files contain / mention / use / have the word X in them', 'list all files with X in them', 'filer med ordet X i sig', 'var används X', 'hitta X' -> SEARCH_FILES with X as the text. NEVER answer these from a LIST_FILES result: a listing only shows names, not contents.");
                sb.AppendLine("- Open one specific file -> READ_FILE.");

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

                // Verktyg 6 (Söka efter filnamn och innehåll)
                cleanResponse = ProcessSearchFiles(cleanResponse, targetRepoPath, activeThread, ref followUpPrompt);

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

        // --- SEARCH_FILES ---

        private static string ProcessSearchFiles(string text, string basePath, string activeThread, ref string followUpPrompt)
        {
            const string tagStart = "[SEARCH_FILES";
            int handled = 0;
            int idx = text.IndexOf(tagStart);

            while (idx != -1 && handled < 3)
            {
                int end = text.IndexOf("]", idx);
                if (end == -1) break;

                string query = text.Substring(idx + tagStart.Length, end - (idx + tagStart.Length))
                                   .TrimStart(':').Trim().Trim('"', '\'', '`');
                if (query.Length > 100) query = query.Substring(0, 100);

                string tagBlock = text.Substring(idx, (end + 1) - idx);
                handled++;

                string followUp;
                string uiMessage;

                if (string.IsNullOrWhiteSpace(query))
                {
                    followUp = "[SYSTEM INJECTION: Your SEARCH_FILES call had no search text. Tell the user, or try again with the text to search for.]";
                    uiMessage = "\n*(Jag försökte söka men glömde vad jag skulle leta efter ❌)*\n";
                }
                else
                {
                    string results = SearchWorkspace(basePath, query);
                    followUp = $"[SYSTEM INJECTION: Search results for \"{query}\" in the workspace:]\n\n```\n{results}\n```\n\n" +
                               "Now continue fulfilling the user's request. Show the user the matching files from these results. " +
                               "Use the exact paths shown. If a section ends with '... (+N more)', tell the user that N more exist that you were not shown. Never add entries that are not in the results.";
                    uiMessage = $"\n*(Jag söker efter \"{query}\" i projektet... 🔍)*\n";
                }

                followUpPrompt = string.IsNullOrEmpty(followUpPrompt) ? followUp : followUpPrompt + "\n\n" + followUp;
                text = text.Replace(tagBlock, uiMessage);
                idx = text.IndexOf(tagStart);
            }

            return text;
        }

        private static bool IsSkippedDir(string name)
        {
            return IgnoredDirs.Contains(name)
                   || (name.StartsWith(".") && !name.Equals(".github", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Söker i filnamn och textinnehåll (skiftlägesokänsligt). Hoppar över byggmappar, dolda mappar, länkar,
        /// binärfiler, stora filer och skyddade filer (deras innehåll läses aldrig).
        /// </summary>
        private static string SearchWorkspace(string rootPath, string query)
        {
            var nameHits = new List<string>();
            var contentHits = new List<string>();
            int nameTotal = 0, contentTotal = 0, scanned = 0;
            bool scanLimitHit = false;

            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0 && !scanLimitHit)
            {
                string dir = pending.Pop();

                string[] files;
                string[] subDirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    subDirs = Directory.GetDirectories(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (++scanned > SearchMaxFilesScanned) { scanLimitHit = true; break; }

                    string name = Path.GetFileName(f);
                    string rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                    bool sensitive = IsSensitiveFileName(name);

                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        nameTotal++;
                        if (nameHits.Count < SearchMaxResultsPerKind)
                            nameHits.Add(sensitive ? rel + " (protected)" : rel);
                    }

                    if (sensitive || IsLink(f)) continue;

                    if (TryFindInFile(f, query, out int matches, out List<string> matchLines))
                    {
                        contentTotal++;
                        if (contentHits.Count < SearchMaxResultsPerKind)
                        {
                            var entry = new System.Text.StringBuilder();
                            entry.Append($"{rel} ({matches} match{(matches == 1 ? "" : "es")})");
                            foreach (var ml in matchLines) entry.Append("\n      " + ml);
                            if (matches > matchLines.Count) entry.Append($"\n      ... (+{matches - matchLines.Count} more matches in this file)");
                            contentHits.Add(entry.ToString());
                        }
                    }
                }

                // Läggs på stacken i omvänd ordning så att mapparna besöks i bokstavsordning
                foreach (var d in subDirs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsSkippedDir(Path.GetFileName(d)) || IsLink(d)) continue;
                    pending.Push(d);
                }
            }

            var sb = new System.Text.StringBuilder();
            string rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            sb.AppendLine($"Search for \"{query}\" in {rootName}/ ({Math.Min(scanned, SearchMaxFilesScanned)} files scanned; build folders, hidden folders, binary files and protected files' contents are skipped)");

            sb.AppendLine();
            sb.AppendLine($"Files whose NAME contains it ({nameTotal}):");
            if (nameHits.Count == 0) sb.AppendLine("  (none)");
            foreach (var h in nameHits) sb.AppendLine("  " + h);
            if (nameTotal > nameHits.Count) sb.AppendLine($"  ... (+{nameTotal - nameHits.Count} more)");

            sb.AppendLine();
            sb.AppendLine($"Files whose CONTENT contains it ({contentTotal}):");
            if (contentHits.Count == 0) sb.AppendLine("  (none)");
            foreach (var h in contentHits) sb.AppendLine("  " + h);
            if (contentTotal > contentHits.Count) sb.AppendLine($"  ... (+{contentTotal - contentHits.Count} more)");

            if (scanLimitHit)
            {
                sb.AppendLine();
                sb.AppendLine($"(The search stopped after {SearchMaxFilesScanned} files. Use a more specific search text to narrow it down.)");
            }

            return sb.ToString().TrimEnd();
        }

        private static bool TryFindInFile(string path, string query, out int matches, out List<string> matchLines)
        {
            matches = 0;
            matchLines = new List<string>();

            try
            {
                if (BinaryExtensions.Contains(Path.GetExtension(path))) return false;

                long length = new FileInfo(path).Length;
                if (length == 0 || length > SearchMaxFileBytes) return false;
                if (LooksBinary(path)) return false;

                int lineNo = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNo++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                    matches++;
                    if (matchLines.Count < SearchMaxLinesPerFile)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length > 100) trimmed = trimmed.Substring(0, 100) + "...";
                        matchLines.Add($"line {lineNo}: {trimmed}");
                    }
                }

                return matches > 0;
            }
            catch
            {
                return false;
            }
        }

        // En fil med NUL-byte i början räknas som binär
        private static bool LooksBinary(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[4096];
                int read = fs.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == 0) return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
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