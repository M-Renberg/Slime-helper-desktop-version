using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        private const int MaxReadRangeLines = 400;
        private const int MaxEditBlocksPerResponse = 5;
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
        private static readonly Regex AppStatusLineRegex =
            new(@"^[ \t]*\*\((?:utfört:|Jag |Bygger|⚠ Nothing was|(?=[^\r\n]*(?:📝|💻|🧠|🔒|❌|✏|📖|🔍|📂|🔨|🛠|this time))I(?: (?:saved|created|wrote|edited|tried|couldn't)|'m (?:reading|searching|looking|running|not allowed)))[^\r\n]*\)\*[ \t]*\r?$\n?", RegexOptions.Multiline);

        /// <summary>
        /// Tar bort appens statusrader ("*(I edited X ✏️)*" och de gamla svenska varianterna) ur en text.
        /// Används på historiken: modellen får aldrig se dem, annars skriver hon egna i stället för verktygsblock.
        /// </summary>
        public static string RemoveStatusLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = AppStatusLineRegex.Replace(text, "");
            cleaned = Regex.Replace(cleaned, @"(\r?\n){3,}", "\n\n");
            return cleaned.Trim();
        }

        // --- Kontroll av påståenden: hon får inte säga att hon ändrat något som appen aldrig skrev ---

        /// <summary>Antal lyckade skrivningar (fil ändrad/skapad, anteckning sparad) sedan senaste ResetModificationCount.</summary>
        public static int ModificationCount { get; private set; }

        public static void ResetModificationCount()
        {
            ModificationCount = 0;
            lock (Activity) { Activity.Clear(); }
        }

        /// <summary>Antal misslyckade ändringar/skrivningar (används för att inte bygga kod som ändringen aldrig nådde).</summary>
        public static int WriteFailureCount { get; private set; }

        // Appens egna statusrader (och bygg-resultat) under den här frågan, i tidsordning. Visas i CLI:t.
        private static readonly List<string> Activity = new();

        private static void AddActivity(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (Activity) { Activity.Add(line.Trim()); }
        }

        private static string SummarizeCheckResult(string result)
        {
            const string prefix = "[CHECK RESULT: ";
            string header = result;
            if (result.StartsWith(prefix))
            {
                int end = result.IndexOf(']');
                header = end > prefix.Length ? result.Substring(prefix.Length, end - prefix.Length) : result.Substring(prefix.Length);
            }
            int dot = header.IndexOf(". ");
            if (dot > 0) header = header.Substring(0, dot);
            return $"*(🔨 {header.Trim()})*";
        }

        /// <summary>
        /// Texten som visas i CLI:t: hela händelsekedjan under frågan (läste, ändrade, byggde, resultat) följt av hennes svar.
        /// Hennes svar får bara med sina egna ord; appens statusrader flyttas till kedjan så att inget visas två gånger.
        /// </summary>
        public static string ComposeCliText(string cleanResponse)
        {
            List<string> lines;
            lock (Activity) { lines = Activity.ToList(); }
            if (lines.Count == 0) return cleanResponse;

            const int maxLines = 30;
            if (lines.Count > maxLines)
            {
                int cut = lines.Count - maxLines;
                lines = lines.Skip(cut).ToList();
                lines.Insert(0, $"*(... {cut} earlier steps not shown)*");
            }

            string prose = RemoveStatusLines(cleanResponse);
            return string.Join("\n", lines) + (prose.Length > 0 ? "\n\n" + prose : "");
        }

        private static readonly Regex ClaimVerbRegex = new(
            @"\bI(?:['’]ve| have)?\s+(?:(?:just|finally|now|already|successfully|also)\s+)*(?:added|removed|deleted|changed|edited|created|updated|written|wrote|fixed|replaced|renamed|inserted|shoved|put|moved|modified|implemented|refactored|appended|saved|stored|rewrote|patched|tweaked)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex ClaimObjectRegex = new(
            @"(?:`[^`]+`|\b\w[\w.\-/]*\.(?:cs|js|ts|tsx|jsx|json|md|txt|xaml|csproj|py|java|html|css|yml|yaml|sql|sh|ps1)\b|\b(?:method|function|class|file|code|line|lines|semicolon|variable|property|field|test|tests|comment|import|using|bug|note|folder|memory)\b)",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// True om texten (hennes egna ord, utan appens statusrader) påstår att hon ändrat, skapat eller sparat något,
        /// t.ex. "I've added your Greet() method to SlimeMemory.cs". Kräver både ett "I added"-verb och något kod-/filrelaterat i samma mening.
        /// </summary>
        public static bool ClaimsChange(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (var raw in Regex.Split(text, @"(?<=[.!?])\s+|\r?\n"))
            {
                string sentence = raw.Replace("*", "");
                if (ClaimVerbRegex.IsMatch(sentence) && ClaimObjectRegex.IsMatch(sentence)) return true;
            }
            return false;
        }

        // Frågor om det som redan gjorts ("what did you change?") ska hon få svara sanningsenligt på utan att räknas som lögn
        private static readonly Regex AboutPastActionsRegex = new(
            @"^\W*(?:what (?:did|have) you|did you|have you|how did you|why did you|show me what you|vad (?:gjorde|har) du|gjorde du|har du|hur gjorde du|varför gjorde du)\b",
            RegexOptions.IgnoreCase);

        public static bool IsAboutPastActions(string prompt)
        {
            return !string.IsNullOrWhiteSpace(prompt) && AboutPastActionsRegex.IsMatch(prompt);
        }

        // --- Begäran om ändring: utgår från det DU bad om, inte från hennes ordval ---
        // (hon kan skriva "Adding X to Y..." eller "Creating 'folder'..." och inte göra något alls)

        private static readonly Regex ChangeRequestRegex = new(
            @"\b(?:add|create|make|write|edit|change|modify|update|fix|remove|delete|rename|replace|insert|implement|refactor|append|save|remember|store|move|start|set\s+up|lägg\s+till|lägga\s+till|lägg\s+in|lägga\s+in|gör|göra|ändra|byt|byta|skapa|skriv|skriva|fixa|rätta|ta\s+bort|radera|uppdatera|flytta|spara|kom\s+ihåg|implementera|döp\s+om|starta)\b",
            RegexOptions.IgnoreCase);

        // Frågor och rådfrågningar ("how do I add...", "vad gör...") är inte en begäran om att hon ska ändra något
        private static readonly Regex QuestionStartRegex = new(
            @"^\W*(?:how|why|what|which|when|where|who|should|is|are|does|do|hur|varför|vad|vilken|vilket|vilka|när|var|ska|bör|finns|är)\b",
            RegexOptions.IgnoreCase);

        /// <summary>True om prompten ber henne ändra, skapa eller spara något (och inte är en fråga eller en fråga om det som redan gjorts).</summary>
        public static bool IsChangeRequest(string prompt)
        {
            return !string.IsNullOrWhiteSpace(prompt)
                   && ChangeRequestRegex.IsMatch(prompt)
                   && !QuestionStartRegex.IsMatch(prompt)
                   && !IsAboutPastActions(prompt);
        }

        public enum UnbackedKind { None, Claim, Request }

        /// <summary>
        /// Claim: hon påstår en ändring som appen inte skrev. Request: du bad om en ändring och appen skrev ingenting.
        /// None: allt stämmer, eller frågan gällde det som redan gjorts.
        /// </summary>
        public static UnbackedKind CheckUnbacked(string cleanResponse, string prompt)
        {
            if (ModificationCount != 0 || IsAboutPastActions(prompt)) return UnbackedKind.None;
            if (ClaimsChange(RemoveStatusLines(cleanResponse))) return UnbackedKind.Claim;
            if (IsChangeRequest(prompt)) return UnbackedKind.Request;
            return UnbackedKind.None;
        }

        /// <summary>Hon påstår en ändring (eller har inte gjort den du bad om), men appen har inte skrivit något under den här frågan.</summary>
        public static bool HasUnbackedClaim(string cleanResponse, string prompt)
        {
            return CheckUnbacked(cleanResponse, prompt) != UnbackedKind.None;
        }

        /// <summary>Ska hon få en rättelse och en ny chans? (Bara om hon har verktyg, alltså inte i General.)</summary>
        public static bool ShouldCorrectFalseClaim(string cleanResponse, string activeThread, int correctionsLeft, string prompt)
        {
            return correctionsLeft > 0 && activeThread != "General" && HasUnbackedClaim(cleanResponse, prompt);
        }

        /// <summary>Rättelsen som skickas till henne: olika text beroende på om hon påstod något eller bara pratade.</summary>
        public static string CorrectionFor(string cleanResponse, string prompt)
        {
            return CheckUnbacked(cleanResponse, prompt) == UnbackedKind.Request ? UnfulfilledRequestCorrection : FalseClaimCorrection;
        }

        /// <summary>Varningen som läggs till i svaret när det fortfarande inte finns någon verklig ändring bakom. Null om allt stämmer.</summary>
        public static string? WarningFor(string cleanResponse, string prompt, string activeThread)
        {
            if (CheckUnbacked(cleanResponse, prompt) == UnbackedKind.None) return null;
            return activeThread == "General" ? NothingChangedNoToolsWarning : NothingChangedWarning;
        }

        /// <summary>Anteckningen som hamnar i historiken, så att hon inte tror att ändringen finns.</summary>
        public static string NoteFor(string cleanResponse, string prompt)
        {
            return CheckUnbacked(cleanResponse, prompt) == UnbackedKind.Request ? NothingChangedRequestNote : NothingChangedNote;
        }

        public const string FalseClaimCorrection =
            "[SYSTEM INJECTION: You said you changed, created or saved something, but the application did NOT write anything during this request, so NOTHING changed. " +
            "Status lines are written by the application, never by you. Do the work now by outputting the tool blocks (READ_FILE first if you have not read the file, " +
            "then EDIT_FILE or WRITE_CODE, or SAVE_NOTE for memory). If you cannot or should not make the change, say so honestly. " +
            "Do not claim that anything is done until the application confirms it.]";

        public const string UnfulfilledRequestCorrection =
            "[SYSTEM INJECTION: The user asked you to change, create or save something, but the application did NOT write anything during this request. You only talked about it, so NOTHING changed. " +
            "Do the work now by outputting the tool blocks (READ_FILE first if you have not read the file, then EDIT_FILE or WRITE_CODE, or SAVE_NOTE for memory). " +
            "Never announce an action instead of doing it. If you cannot or should not make the change, say so plainly and explain why.]";

        public const string NothingChangedNoToolsWarning =
            "*(⚠ Nothing was saved or changed: the General chat has no tools. Mention a repo or an @subject so I can work with files and memory.)*";

        public const string NothingChangedRequestNote =
            "[SYSTEM NOTE: The user asked for a change, but the application wrote NOTHING. That change does NOT exist.]";

        public const string NothingChangedWarning =
            "*(⚠ Nothing was actually changed: no file or note was written during this request.)*";

        public const string NothingChangedNote =
            "[SYSTEM NOTE: Your last reply claimed a change, but the application wrote NOTHING. That change does NOT exist.]";

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

            // Statusrader som "*(Jag ändrade X ✏️)*" skrivs bara av appen efter att ett verktyg faktiskt körts.
            // Skriver modellen en själv (härmar historiken) tas den bort, så att den aldrig ser ut som ett bevis.
            response = AppStatusLineRegex.Replace(response, "");

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
            sb.AppendLine("Use this to save personal notes, to-do lists, or information about Subjects (@).");
            sb.AppendLine("You can freely CREATE FOLDERS in your memory by including them in the path!");
            sb.AppendLine("[SAVE_NOTE: folder_name/filename.md]\n# content\n[/SAVE_NOTE]");
            sb.AppendLine("A request to remember or save something MUST contain a [SAVE_NOTE] block. Saying that you saved it without the block saves nothing.");

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
                sb.AppendLine("For a large file, read just a part by adding a line range (max 400 lines): [READ_FILE: relative_path/filename.ext | 100-200]. SEARCH_FILES gives you line numbers to aim at.");

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
                sb.AppendLine("- Change part of an existing file (add, replace or delete code) -> EDIT_FILE.");
                sb.AppendLine("- Create a NEW file -> WRITE_CODE (code) or WRITE_DOC (documentation).");
                sb.AppendLine("- Compile, test or lint the project (verify your work) -> RUN_CHECK.");

                sb.AppendLine($"\n7. EDIT AN EXISTING FILE ({targetRepoPath}):");
                sb.AppendLine("To change part of an existing file use find-and-replace blocks. This is the PREFERRED way to modify code. You may put several FIND/REPLACE pairs in one EDIT_FILE block; they are applied in order, all or nothing.");
                sb.AppendLine("[EDIT_FILE: relative_path/filename.ext]");
                sb.AppendLine("[FIND]");
                sb.AppendLine("exact existing text copied from the file (a few unique lines)");
                sb.AppendLine("[/FIND]");
                sb.AppendLine("[REPLACE]");
                sb.AppendLine("the new text");
                sb.AppendLine("[/REPLACE]");
                sb.AppendLine("[/EDIT_FILE]");

                sb.AppendLine($"\n8. VERIFY YOUR WORK ({targetRepoPath}):");
                sb.AppendLine("To compile or test the project (this runs the project's own build/test commands), use one of:");
                sb.AppendLine("[RUN_CHECK: build]");
                sb.AppendLine("[RUN_CHECK: test]");
                sb.AppendLine("[RUN_CHECK: lint]");
                sb.AppendLine("If the repo has several projects, name one: [RUN_CHECK: build | relative/path/to/Project.sln-or-folder]. Supported: .NET (dotnet build / dotnet test) and Node (npm run build / npm test / npm run lint, only when the script exists). Nothing else can be run. The user may be asked to approve it, and you get the errors back to fix.");

                sb.AppendLine("\nCODING RULES:");
                sb.AppendLine("- When the user asks you to change, create or save something, your reply MUST contain the tool blocks that do it. Announcing it (\"Adding X to Y...\", \"Creating a folder...\") without a tool block does NOTHING - the application only acts on tool blocks.");
                sb.AppendLine("- You MUST read a file with READ_FILE before you edit or overwrite it (use a line range for big files). Edits to files you have not read are rejected. Never guess what a file contains.");
                sb.AppendLine("- FIND must match the file EXACTLY (characters and indentation) and must be unique in the file, so include enough surrounding lines. Never put line numbers or ``` fences inside FIND or REPLACE.");
                sb.AppendLine("- Keep edits small and focused. Do not rewrite whole files, and follow the existing code style and indentation.");
                sb.AppendLine("- To INSERT code, FIND one existing line next to the insertion point and REPLACE it with that same line plus the new code. Never move or repeat the class's or method's own { } braces: an edit that makes the { } unbalanced is rejected.");
                sb.AppendLine("- Use WRITE_CODE only to create NEW files. Existing files larger than 20000 characters cannot be overwritten - use EDIT_FILE.");
                sb.AppendLine("- Do not paste the code again in your normal reply. Briefly say what you changed and why.");
                sb.AppendLine("- If an edit fails you will be told why. Read the file again and retry with corrected FIND text.");
                sb.AppendLine("- Every file you change is backed up automatically.");
                sb.AppendLine("- After you change code, verify it with RUN_CHECK build (and RUN_CHECK test when the project has tests). If it fails, read the errors, fix them with EDIT_FILE and run the check again. Stop after 3 failed attempts and tell the user what is still failing. Never claim that the code builds or that tests pass unless a CHECK RESULT says SUCCEEDED. Skip the check for documentation-only changes.");
                sb.AppendLine("\nEXAMPLE. The user asks: add a method Hello() that returns \"hi\" to Greeter.cs. Your first reply is one short sentence plus the read block:");
                sb.AppendLine("[READ_FILE: Greeter.cs]");
                sb.AppendLine("After the file contents come back, you reply with one short sentence plus the edit and the build check (no code in the sentence):");
                sb.AppendLine("[EDIT_FILE: Greeter.cs]");
                sb.AppendLine("[FIND]");
                sb.AppendLine("    public string Name { get; set; }");
                sb.AppendLine("[/FIND]");
                sb.AppendLine("[REPLACE]");
                sb.AppendLine("    public string Name { get; set; }");
                sb.AppendLine("");
                sb.AppendLine("    public string Hello()");
                sb.AppendLine("    {");
                sb.AppendLine("        return \"hi\";");
                sb.AppendLine("    }");
                sb.AppendLine("[/REPLACE]");
                sb.AppendLine("[/EDIT_FILE]");
                sb.AppendLine("[RUN_CHECK: build]");
                sb.AppendLine("");
                sb.AppendLine("- NEVER claim that you read, created or changed a file unless you output the tool block for it in THIS response. Status lines like *(I edited ...)* or *(I read ...)* are written by the application only - never write them yourself.");

                sb.AppendLine("\nPROTECTED FILES: secrets and credentials (.env files, private keys, certificates, credentials files) and .git internals can NEVER be read or written. Files marked (protected) in a listing are off-limits - do not try to open them. If the user asks for one, explain that it is protected.");
            }

            return sb.ToString();
        }

        public static string ProcessResponse(string response, string activeThread, string targetRepoPath, SlimeBrainManager brainManager, out string followUpPrompt)
        {
            return ProcessResponse(response, activeThread, targetRepoPath, brainManager, out followUpPrompt, out _);
        }

        /// <summary>
        /// Som ovan, men returnerar också RUN_CHECK-begäranden. De körs asynkront av anroparen
        /// (RunCheckRequestsAsync) så att UI-tråden inte fryser under bygget.
        /// </summary>
        public static string ProcessResponse(string response, string activeThread, string targetRepoPath, SlimeBrainManager brainManager, out string followUpPrompt, out List<CheckRequest> checks)
        {
            string cleanResponse = response;
            followUpPrompt = "";
            checks = new List<CheckRequest>();
            int failuresBefore = WriteFailureCount;

            // Verktyg 1 (Spara i hjärnan)
            cleanResponse = ExtractAndExecute(cleanResponse, "[SAVE_NOTE:", "[/SAVE_NOTE]", (filename, content) =>
            {
                if (!brainManager.SaveRepoNote(activeThread, filename, content))
                    return $"\n*(I couldn't save the note {filename}: that path is not allowed 🔒)*\n";
                ModificationCount++;
                return $"\n*(I saved the note: {filename} in my memory! 🧠)*\n";
            });

            if (!string.IsNullOrEmpty(targetRepoPath))
            {
                // Verktyg 2 (Skriva dokumentation)
                cleanResponse = ExtractAndExecute(cleanResponse, "[WRITE_DOC:", "[/WRITE_DOC]", (filename, content) =>
                {
                    if (!TryWriteFile(targetRepoPath, StripTagPrefix(targetRepoPath, filename, activeThread), content, brainManager, activeThread, out string error))
                    { WriteFailureCount++; return $"\n*(I couldn't write {filename}: {error} 🔒)*\n"; }
                    return $"\n*(I created the documentation {filename} in your project! 📝)*\n";
                });

                // Verktyg 3 (Skriva källkod)
                cleanResponse = ExtractAndExecute(cleanResponse, "[WRITE_CODE:", "[/WRITE_CODE]", (filename, content) =>
                {
                    if (!TryWriteFile(targetRepoPath, StripTagPrefix(targetRepoPath, filename, activeThread), content, brainManager, activeThread, out string error))
                    { WriteFailureCount++; return $"\n*(I couldn't write {filename}: {error} 🔒)*\n"; }
                    return $"\n*(I wrote source code to {filename} in your project! 💻)*\n";
                });

                // Fallback: Om hon hittar på "CREATE_FILE" istället för våra officiella taggar
                cleanResponse = ExtractAndExecute(cleanResponse, "[CREATE_FILE:", "[/CREATE_FILE]", (filename, content) =>
                {
                    if (filename.StartsWith(activeThread, StringComparison.OrdinalIgnoreCase))
                    {
                        filename = filename.Substring(activeThread.Length).Trim('/', '\\');
                    }

                    if (!TryWriteFile(targetRepoPath, filename, content, brainManager, activeThread, out string error))
                    { WriteFailureCount++; return $"\n*(I couldn't write {filename}: {error} 🔒)*\n"; }
                    return $"\n*(I created the file {filename} in your project! 📝)*\n";
                });

                // Tool 5 (List files) - runs before READ_FILE so both can be used in the same response
                cleanResponse = ProcessListFiles(cleanResponse, targetRepoPath, activeThread, ref followUpPrompt);

                // Tool 6 (Search files by name and content)
                cleanResponse = ProcessSearchFiles(cleanResponse, targetRepoPath, activeThread, ref followUpPrompt);

                // Tool 4 (Read files) - runs before EDIT_FILE so reading and editing in the same response succeeds
                cleanResponse = ProcessReadFiles(cleanResponse, targetRepoPath, activeThread, ref followUpPrompt);

                // Tool 7 (Edit existing file with FIND/REPLACE)
                cleanResponse = ProcessEditFiles(cleanResponse, targetRepoPath, activeThread, brainManager, ref followUpPrompt);

                // Tool 8 (Build/test/lint) - run asynchronously by caller, only tags read here
                // If an edit failed in this response, disk code is not what was intended: do not build it
                bool editFailedHere = WriteFailureCount > failuresBefore;
                cleanResponse = ProcessCheckTags(cleanResponse, checks, editFailedHere);
            }

            // Allt appen själv skrev i det här svaret (modellens egna påhitt är redan bortrensade) sparas i händelsekedjan
            foreach (Match m in AppStatusLineRegex.Matches(cleanResponse)) AddActivity(m.Value);

            return cleanResponse.Trim();
        }

        // --- Läs-före-ändra: hon får inte ändra eller skriva över en fil hon inte har läst ---

        private static readonly Dictionary<string, DateTime> RecentlyRead = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ReadValidity = TimeSpan.FromMinutes(30);

        private static void MarkRead(string fullPath)
        {
            lock (RecentlyRead) { RecentlyRead[fullPath] = DateTime.UtcNow; }
        }

        private static bool HasReadRecently(string fullPath)
        {
            lock (RecentlyRead)
            {
                return RecentlyRead.TryGetValue(fullPath, out var t) && DateTime.UtcNow - t < ReadValidity;
            }
        }

        // --- Klammerkontroll: en ändring får inte göra { } obalanserade i kodfiler ---

        private static readonly HashSet<string> BraceCheckExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".java", ".kt", ".c", ".cpp", ".h", ".hpp",
            ".go", ".rs", ".php", ".swift", ".css", ".scss", ".json"
        };

        /// <summary>
        /// Calculates the net balance of curly braces { - }, ignoring braces inside
        /// comments (single-line // and multi-line /* */), character literals ('{'),
        /// and string literals ("...", @"...", $"", and raw string literals """...""").
        /// </summary>
        private static int BraceBalance(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int n = 0;
            int len = text.Length;
            int i = 0;

            while (i < len)
            {
                char c = text[i];

                // Single-line comment: // ...
                if (c == '/' && i + 1 < len && text[i + 1] == '/')
                {
                    i += 2;
                    while (i < len && text[i] != '\n') i++;
                    continue;
                }

                // Multi-line comment: /* ... */
                if (c == '/' && i + 1 < len && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < len && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    if (i + 1 < len) i += 2;
                    else i = len;
                    continue;
                }

                // Character literal: '...'
                if (c == '\'')
                {
                    i++;
                    while (i < len && text[i] != '\'')
                    {
                        if (text[i] == '\\' && i + 1 < len) i += 2;
                        else if (text[i] == '\n') break;
                        else i++;
                    }
                    if (i < len && text[i] == '\'') i++;
                    continue;
                }

                // String literals: raw string literals ("""..."""), verbatim (@"..."),
                // interpolated ($"..."), or standard string literals ("...")
                if (c == '$' || c == '@' || c == '"')
                {
                    int p = i;
                    int dollars = 0;
                    bool hasAt = false;

                    while (p < len && (text[p] == '$' || text[p] == '@'))
                    {
                        if (text[p] == '$') dollars++;
                        else if (text[p] == '@') hasAt = true;
                        p++;
                    }

                    if (p < len && text[p] == '"')
                    {
                        // Check for raw string literals: 3 or more double quotes
                        int quoteCount = 0;
                        while (p + quoteCount < len && text[p + quoteCount] == '"') quoteCount++;

                        if (quoteCount >= 3)
                        {
                            p += quoteCount;
                            while (p < len)
                            {
                                if (text[p] == '"')
                                {
                                    int matchQuotes = 0;
                                    while (p + matchQuotes < len && text[p + matchQuotes] == '"') matchQuotes++;
                                    if (matchQuotes >= quoteCount)
                                    {
                                        p += matchQuotes;
                                        break;
                                    }
                                    p += matchQuotes;
                                }
                                else
                                {
                                    p++;
                                }
                            }
                            i = p;
                            continue;
                        }

                        // Verbatim string literal: @"..." or $@"...""
                        if (hasAt)
                        {
                            p++; // Skip opening quote
                            while (p < len)
                            {
                                if (text[p] == '"')
                                {
                                    if (p + 1 < len && text[p + 1] == '"')
                                    {
                                        p += 2; // Escaped quote ""
                                    }
                                    else
                                    {
                                        p++; // Closing quote
                                        break;
                                    }
                                }
                                else
                                {
                                    p++;
                                }
                            }
                            i = p;
                            continue;
                        }

                        // Regular or interpolated string: "..." or $"..."
                        p++; // Skip opening quote
                        while (p < len)
                        {
                            if (text[p] == '\\' && p + 1 < len)
                            {
                                p += 2; // Skip escaped character
                            }
                            else if (text[p] == '"')
                            {
                                p++; // Closing quote
                                break;
                            }
                            else if (text[p] == '\n')
                            {
                                break;
                            }
                            else
                            {
                                p++;
                            }
                        }
                        i = p;
                        continue;
                    }
                }

                // Template literal (JS/TS): `...`
                if (c == '`')
                {
                    i++;
                    while (i < len)
                    {
                        if (text[i] == '\\' && i + 1 < len) i += 2;
                        else if (text[i] == '`') { i++; break; }
                        else i++;
                    }
                    continue;
                }

                if (c == '{') n++;
                else if (c == '}') n--;

                i++;
            }

            return n;
        }

        // --- READ_FILE (flera per svar, med valfritt radintervall) ---

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

                // Radintervall: [READ_FILE: path | 100-200]
                string requested = filename;
                int rangeFrom = 0, rangeTo = 0;
                int bar = filename.IndexOf('|');
                if (bar >= 0)
                {
                    requested = filename.Substring(0, bar).Trim();
                    var rm = Regex.Match(filename.Substring(bar + 1), @"(\d+)\s*-\s*(\d+)");
                    if (rm.Success)
                    {
                        int.TryParse(rm.Groups[1].Value, out rangeFrom);
                        int.TryParse(rm.Groups[2].Value, out rangeTo);
                    }
                }
                bool ranged = rangeFrom > 0 && rangeTo >= rangeFrom;

                if (reads >= MaxReadsPerResponse)
                {
                    injected.Add($"[SKIPPED: {requested} - only {MaxReadsPerResponse} files can be read per response. Ask for it again if you still need it.]");
                    uiMessage = $"\n*(I couldn't read {requested} this time - max {MaxReadsPerResponse} files at a time.)*\n";
                }
                else
                {
                    reads++;
                    string stripped = StripTagPrefix(basePath, requested, activeThread);
                    string? directPath = ResolveSafePath(basePath, stripped);
                    string? fullPath = directPath;

                    if (directPath != null && File.Exists(directPath))
                    {
                        fullPath = directPath;
                    }
                    else if (directPath != null)
                    {
                        string? uniqueMatch = FindUniqueMatchingFile(basePath, stripped, out string? ambigMsg);
                        if (ambigMsg != null)
                        {
                            injected.Add($"[AMBIGUOUS FILE: {ambigMsg}]");
                            uiMessage = $"\n*(I tried to read {requested}, but {ambigMsg} ❌)*\n";
                            text = text.Replace(tagBlock, uiMessage);
                            idx = text.IndexOf(tagStart);
                            continue;
                        }
                        if (uniqueMatch != null)
                        {
                            fullPath = uniqueMatch;
                        }
                    }

                    if (fullPath != null && IsSensitivePath(basePath, fullPath))
                    {
                        injected.Add($"[ACCESS DENIED: {requested} is a protected file (secrets/credentials) and cannot be read. Tell the user that it is protected.]");
                        uiMessage = $"\n*(I'm not allowed to read {requested} - protected file 🔒)*\n";
                    }
                    else if (fullPath != null && File.Exists(fullPath))
                    {
                        MarkRead(fullPath);
                        if (directPath != null && !directPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            MarkRead(directPath);
                        }
                        string normalized = File.ReadAllText(fullPath).Replace("\r\n", "\n");
                        string[] allLines = normalized.Split('\n');
                        int totalLines = allLines.Length;

                        if (ranged && rangeFrom > totalLines)
                        {
                            injected.Add($"[FILE: {requested}] has only {totalLines} lines, so the requested range {rangeFrom}-{rangeTo} is past the end of the file.");
                            uiMessage = $"\n*(I tried to read lines {rangeFrom}-{rangeTo} of {requested}, but the file only has {totalLines} lines ❌)*\n";
                        }
                        else
                        {
                            string content = normalized;
                            string header = $"[FILE: {requested} ({totalLines} lines)]";
                            string rangeInfo = "";

                            if (ranged)
                            {
                                int a = Math.Max(1, rangeFrom);
                                int b = Math.Min(totalLines, Math.Min(rangeTo, a + MaxReadRangeLines - 1));
                                content = string.Join("\n", allLines.Skip(a - 1).Take(b - a + 1));
                                header = $"[FILE: {requested} (lines {a}-{b} of {totalLines})]";
                                rangeInfo = $" (lines {a}-{b})";
                            }

                            bool truncated = content.Length > MaxReadChars;
                            if (truncated) content = content.Substring(0, MaxReadChars);

                            injected.Add($"{header}\n```\n{content}\n```" +
                                         (truncated ? $"\n(Truncated after {MaxReadChars} characters. The file has {totalLines} lines - read other parts with [READ_FILE: {requested} | start-end]. To change a large file use EDIT_FILE, never rewrite it.)" : ""));
                            uiMessage = $"\n*(I'm reading the file {requested}{rangeInfo}... 📖)*\n";
                        }
                    }
                    else
                    {
                        injected.Add($"[FILE NOT FOUND: {requested} does not exist in {basePath}. Tell the user, or use LIST_FILES to find the right path.]");
                        uiMessage = $"\n*(I tried to read {requested}, but couldn't find it! ❌)*\n";
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

        // --- EDIT_FILE (FIND/REPLACE på befintliga filer) ---

        private static readonly Regex EditBlockRegex =
            new(@"\[EDIT_FILE:\s*(?<path>[^\]\r\n]+?)\s*\](?<body>.*?)\[/EDIT_FILE\]", RegexOptions.Singleline);

        private static readonly Regex FindReplaceRegex =
            new(@"\[FIND\]\r?\n?(?<find>.*?)\r?\n?\[/FIND\]\s*\[REPLACE\]\r?\n?(?<replace>.*?)\r?\n?\[/REPLACE\]", RegexOptions.Singleline);

        private static string ProcessEditFiles(string text, string basePath, string activeThread, SlimeBrainManager brainManager, ref string followUpPrompt)
        {
            var followUps = new List<string>();
            int handled = 0;

            string result = EditBlockRegex.Replace(text, m =>
            {
                string filename = m.Groups["path"].Value.Trim();

                if (handled++ >= MaxEditBlocksPerResponse)
                {
                    followUps.Add($"[SYSTEM INJECTION: EDIT_FILE for {filename} was SKIPPED - only {MaxEditBlocksPerResponse} edit blocks are processed per response. Send it again.]");
                    WriteFailureCount++;
                    return $"\n*(I couldn't edit {filename} this time.)*\n";
                }

                var edits = new List<(string Find, string Replace)>();
                foreach (Match fr in FindReplaceRegex.Matches(m.Groups["body"].Value))
                {
                    edits.Add((fr.Groups["find"].Value, fr.Groups["replace"].Value));
                }

                if (edits.Count == 0)
                {
                    followUps.Add($"[SYSTEM INJECTION: EDIT_FILE for {filename} had no valid [FIND]...[/FIND] followed by [REPLACE]...[/REPLACE], so NOTHING was changed. Use exactly that format.]");
                    WriteFailureCount++;
                    return $"\n*(I tried to edit {filename}, but the format was wrong ❌)*\n";
                }

                string rel = StripTagPrefix(basePath, filename, activeThread);
                if (TryEditFile(basePath, rel, edits, brainManager, activeThread, out string error))
                {
                    return $"\n*(I edited {filename}: {edits.Count} change{(edits.Count == 1 ? "" : "s")} ✏️)*\n";
                }

                followUps.Add($"[SYSTEM INJECTION: EDIT_FILE on {filename} FAILED and NOTHING was changed: {error}. Read the relevant part of the file again (READ_FILE, optionally with a line range) and retry with FIND text copied exactly from the file.]");
                WriteFailureCount++;
                return $"\n*(I couldn't edit {filename}: {error} ❌)*\n";
            });

            if (followUps.Count > 0)
            {
                string combined = string.Join("\n\n", followUps);
                followUpPrompt = string.IsNullOrEmpty(followUpPrompt) ? combined : followUpPrompt + "\n\n" + combined;
            }

            return result;
        }

        /// <summary>
        /// Tillämpar FIND/REPLACE på en befintlig fil. Allt eller inget: misslyckas en ändring skrivs ingenting.
        /// Bevarar BOM och radslut (CRLF/LF), och säkerhetskopierar filen först.
        /// </summary>
        private static bool TryEditFile(string basePath, string relative, List<(string Find, string Replace)> edits,
                                        SlimeBrainManager brain, string tag, out string error)
        {
            error = "";

            string? directPath = ResolveSafePath(basePath, relative);
            if (directPath == null) { error = "the path is outside the project"; return false; }

            string? fullPath = directPath;
            if (!File.Exists(directPath))
            {
                string? uniqueMatch = FindUniqueMatchingFile(basePath, relative, out string? ambigMsg);
                if (ambigMsg != null)
                {
                    error = ambigMsg;
                    return false;
                }
                if (uniqueMatch != null)
                {
                    fullPath = uniqueMatch;
                }
                else
                {
                    error = "the file was not found in the workspace (use SEARCH_FILES to find its location)";
                    return false;
                }
            }

            if (IsNoWritePath(basePath, fullPath)) { error = "protected folder (.git, hooks, etc.)"; return false; }
            if (IsSensitivePath(basePath, fullPath)) { error = "protected file (secrets/keys)"; return false; }
            if (!HasReadRecently(fullPath) && (directPath == null || !HasReadRecently(directPath)))
            {
                error = "you have not read this file yet - read it with READ_FILE first so the FIND text is based on what is actually in it";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                int skip = hasBom ? 3 : 0;

                string original;
                try
                {
                    original = new System.Text.UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
                }
                catch (System.Text.DecoderFallbackException)
                {
                    error = "the file is not UTF-8 and cannot be edited safely";
                    return false;
                }

                bool crlf = original.Contains("\r\n");
                string content = original.Replace("\r\n", "\n");

                for (int i = 0; i < edits.Count; i++)
                {
                    string find = edits[i].Find.Replace("\r\n", "\n");
                    string replacement = edits[i].Replace.Replace("\r\n", "\n");
                    string label = edits.Count > 1 ? $"change {i + 1}: " : "";

                    if (find.Length == 0) { error = label + "FIND is empty"; return false; }

                    int first = content.IndexOf(find, StringComparison.Ordinal);
                    if (first >= 0)
                    {
                        if (content.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0)
                        {
                            error = label + "the FIND text appears in several places - include more surrounding lines so it becomes unique";
                            return false;
                        }

                        content = content.Substring(0, first) + replacement + content.Substring(first + find.Length);
                    }
                    else
                    {
                        // Fallback: line-based whitespace-normalized match
                        string[] contentLines = content.Split('\n');
                        string[] findLines = find.Split('\n');

                        string NormLine(string s) => s.Replace("\t", "    ").TrimEnd();

                        int k = findLines.Length;
                        var matchIndices = new List<int>();

                        for (int start = 0; start <= contentLines.Length - k; start++)
                        {
                            bool matches = true;
                            for (int j = 0; j < k; j++)
                            {
                                if (NormLine(contentLines[start + j]) != NormLine(findLines[j]))
                                {
                                    matches = false;
                                    break;
                                }
                            }
                            if (matches)
                            {
                                matchIndices.Add(start);
                            }
                        }

                        if (matchIndices.Count == 0)
                        {
                            for (int start = 0; start <= contentLines.Length - k; start++)
                            {
                                bool matches = true;
                                for (int j = 0; j < k; j++)
                                {
                                    if (contentLines[start + j].Trim() != findLines[j].Trim())
                                    {
                                        matches = false;
                                        break;
                                    }
                                }
                                if (matches)
                                {
                                    matchIndices.Add(start);
                                }
                            }
                        }

                        if (matchIndices.Count == 0)
                        {
                            error = label + "the FIND text does not match the file exactly (check characters, indentation and line breaks)";
                            return false;
                        }
                        if (matchIndices.Count > 1)
                        {
                            error = label + "the FIND text appears in several places - include more surrounding lines so it becomes unique";
                            return false;
                        }

                        int matchedStart = matchIndices[0];
                        string[] targetLines = contentLines.Skip(matchedStart).Take(k).ToArray();
                        string[] replacementLines = replacement.Split('\n');

                        var adjustedReplacement = AdjustReplacementIndentation(targetLines, findLines, replacementLines);

                        var newContentLines = new List<string>();
                        newContentLines.AddRange(contentLines.Take(matchedStart));
                        newContentLines.AddRange(adjustedReplacement);
                        newContentLines.AddRange(contentLines.Skip(matchedStart + k));

                        content = string.Join("\n", newContentLines);
                    }
                }

                string result = crlf ? content.Replace("\n", "\r\n") : content;
                if (result == original) { error = "the edit would not change anything in the file"; return false; }

                if (BraceCheckExtensions.Contains(Path.GetExtension(fullPath)))
                {
                    int before = BraceBalance(original);
                    int after = BraceBalance(content);
                    if (before != after)
                    {
                        error = $"the edit would unbalance the curly braces {{ }} (net difference {after - before}). " +
                                "Include whole blocks in FIND/REPLACE, and never add a { without its matching }. Do not move or repeat the class's or method's own braces";
                        return false;
                    }
                }

                if (!brain.BackupFile(tag, basePath, fullPath))
                {
                    error = "could not make a backup, so the file was left untouched";
                    return false;
                }

                File.WriteAllText(fullPath, result, new System.Text.UTF8Encoding(hasBom));
                MarkRead(fullPath);
                if (directPath != null && !directPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    MarkRead(directPath);
                }
                NoteFileChanged(basePath, fullPath);
                ModificationCount++;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // --- RUN_CHECK (bygg, test, lint) ---

        public sealed class CheckRequest
        {
            public string Kind { get; set; } = "";
            public string? Target { get; set; }
            public string? Error { get; set; }
        }

        private sealed class CheckPlan
        {
            public string Tool { get; set; } = "";
            public string FileName { get; set; } = "";
            public List<string> Args { get; set; } = new();
            public string WorkDir { get; set; } = "";
            public string Target { get; set; } = "";
            public string Description { get; set; } = "";
        }

        private const int MaxChecksPerResponse = 2;
        private const int CheckTimeoutSeconds = 120;
        private const int CheckOutputMaxChars = 6000;

        private static readonly char[] DirSeps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        /// <summary>
        /// Sätts av MainWindow och visar en Ja/Nej-dialog (titel, text). Utan den körs ingenting.
        /// </summary>
        public static Func<string, string, bool>? ConfirmRun { get; set; }

        // Repos där användaren godkänt körning. Godkännandet gäller tills appen startas om,
        // eller tills hon ändrar en byggfil (då frågar vi igen).
        private static readonly HashSet<string> TrustedRepos = new(StringComparer.OrdinalIgnoreCase);
        private static bool _checkRunning;

        private static readonly HashSet<string> BuildDefinitionExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx", ".proj",
            ".cmd", ".bat", ".ps1", ".sh", ".gradle"
        };

        private static readonly HashSet<string> BuildDefinitionNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", ".npmrc", "nuget.config", "global.json", "Makefile",
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"
        };

        private static string NormalizeRepoKey(string basePath) => Path.GetFullPath(basePath).TrimEnd(DirSeps);

        // Ändrar hon en fil som styr bygget/testerna måste användaren godkänna körning igen.
        private static void NoteFileChanged(string basePath, string fullPath)
        {
            string name = Path.GetFileName(fullPath);
            bool isBuildDefinition = BuildDefinitionNames.Contains(name)
                                     || BuildDefinitionExtensions.Contains(Path.GetExtension(name))
                                     || name.Contains(".config.", StringComparison.OrdinalIgnoreCase);
            if (isBuildDefinition)
            {
                lock (TrustedRepos) { TrustedRepos.Remove(NormalizeRepoKey(basePath)); }
            }
        }

        private static readonly Regex RunCheckRegex =
            new(@"\[RUN_CHECK:\s*(?<kind>[A-Za-z_]+)\s*(?:\|\s*(?<target>[^\]\r\n]*?))?\s*\]", RegexOptions.IgnoreCase);

        private static string ProcessCheckTags(string text, List<CheckRequest> checks, bool editFailedInThisReply = false)
        {
            int handled = 0;

            return RunCheckRegex.Replace(text, m =>
            {
                string kind = m.Groups["kind"].Value.ToLowerInvariant();
                string target = m.Groups["target"].Success ? m.Groups["target"].Value.Trim() : "";

                if (editFailedInThisReply)
                {
                    handled++;
                    checks.Add(new CheckRequest
                    {
                        Kind = kind,
                        Error = "an edit or write in this same reply FAILED, so the code on disk is not what you intended and a build would only check the OLD code. Fix the edit first (READ_FILE, then EDIT_FILE), and only then run the check"
                    });
                    return $"\n*(I couldn't run the {kind} check: an edit in the same reply failed ❌)*\n";
                }

                if (handled++ >= MaxChecksPerResponse)
                {
                    checks.Add(new CheckRequest { Kind = kind, Error = $"only {MaxChecksPerResponse} checks can be run per response" });
                    return $"\n*(I couldn't run the {kind} check this time.)*\n";
                }

                if (kind != "build" && kind != "test" && kind != "lint")
                {
                    checks.Add(new CheckRequest { Kind = kind, Error = $"unknown check '{kind}' (use build, test or lint)" });
                    return $"\n*(I couldn't run '{kind}' - unknown check ❌)*\n";
                }

                checks.Add(new CheckRequest { Kind = kind, Target = string.IsNullOrWhiteSpace(target) ? null : target });
                return $"\n*(I'm running the {kind} check... 🔨)*\n";
            });
        }

        /// <summary>
        /// Kör de begärda kontrollerna (asynkront, UI-tråden blockeras inte) och returnerar uppföljningstexten
        /// till modellen med resultatet. Anropas på UI-tråden eftersom bekräftelsedialogen visas där.
        /// </summary>
        public static async Task<string> RunCheckRequestsAsync(List<CheckRequest> requests, string basePath, string activeThread, string existingFollowUp)
        {
            if (requests == null || requests.Count == 0) return existingFollowUp;

            var results = new List<string>();
            foreach (var req in requests)
            {
                string result = await RunSingleCheckAsync(req, basePath, activeThread);
                AddActivity(SummarizeCheckResult(result));
                results.Add(result);
            }

            string combined = string.Join("\n\n", results) +
                "\n\nNow continue fulfilling the user's request. If a check FAILED, read the errors, fix them (READ_FILE, then EDIT_FILE) and run the check again - " +
                "but stop after 3 failed attempts and tell the user what is still failing. If it SUCCEEDED, tell the user. " +
                "Never claim that the code builds or that tests pass unless a CHECK RESULT says SUCCEEDED.";

            return string.IsNullOrEmpty(existingFollowUp) ? combined : existingFollowUp + "\n\n" + combined;
        }

        private static async Task<string> RunSingleCheckAsync(CheckRequest req, string basePath, string activeThread)
        {
            if (req.Error != null) return $"[CHECK RESULT: NOT RUN - {req.Error}]";

            var plan = ResolveCheckPlan(basePath, activeThread, req.Kind, req.Target, out string planError);
            if (plan == null) return $"[CHECK RESULT: NOT RUN ({req.Kind}) - {planError}]";

            string repoKey = NormalizeRepoKey(basePath);
            bool trusted;
            lock (TrustedRepos) { trusted = TrustedRepos.Contains(repoKey); }

            if (!trusted)
            {
                string message = $"Slime wants to run:\n\n    {plan.Description}\n\nin {repoKey}\n\n" +
                                 "This executes code from the repo (build scripts, tests). Allow it?\n\n" +
                                 "Yes = allow, and don't ask again for this repo until Slime restarts or she edits a build file.\n" +
                                 "No = don't run it.";

                bool allowed = false;
                try { allowed = ConfirmRun?.Invoke("Slime wants to run a check", message) ?? false; }
                catch { allowed = false; }

                if (!allowed)
                {
                    return $"[CHECK RESULT: NOT RUN - the user did not approve running \"{plan.Description}\". Tell the user it was not run, and do not try again unless they ask.]";
                }

                lock (TrustedRepos) { TrustedRepos.Add(repoKey); }
            }

            if (_checkRunning) return "[CHECK RESULT: NOT RUN - another check is already running. Try again in a moment.]";

            _checkRunning = true;
            try
            {
                var (exitCode, output, timedOut, seconds) = await RunProcessAsync(plan, CheckTimeoutSeconds);
                string formatted = FormatCheckOutput(plan, req.Kind, output, basePath);

                string status = timedOut
                    ? $"TIMED OUT after {CheckTimeoutSeconds}s (the process was killed)"
                    : exitCode == 0 ? "SUCCEEDED" : $"FAILED (exit code {exitCode})";

                return $"[CHECK RESULT: {plan.Description} - {status} in {seconds:0.0}s]\n```\n{formatted}\n```";
            }
            finally
            {
                _checkRunning = false;
            }
        }

        private static CheckPlan? ResolveCheckPlan(string basePath, string activeThread, string kind, string? target, out string error)
        {
            error = "";
            string root = NormalizeRepoKey(basePath);
            List<CheckPlan> plans;

            if (!string.IsNullOrWhiteSpace(target))
            {
                string? full = ResolveSafePath(basePath, StripTagPrefix(basePath, target, activeThread));
                if (full == null) { error = "the target is outside the project"; return null; }

                if (File.Exists(full))
                {
                    string name = Path.GetFileName(full);
                    string ext = Path.GetExtension(full).ToLowerInvariant();
                    plans = new List<CheckPlan>();

                    if (ext is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj")
                    {
                        if (kind == "lint") { error = "lint is only available for package.json projects"; return null; }
                        plans.Add(DotnetPlan(kind, full, root));
                    }
                    else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!PackageHasScript(full, kind)) { error = $"that package.json has no \"{kind}\" script"; return null; }
                        plans.Add(NodePlan(kind, full, root));
                    }
                    else
                    {
                        error = "the target must be a .sln/.slnx/.csproj file, a package.json, or a folder";
                        return null;
                    }
                }
                else if (Directory.Exists(full))
                {
                    plans = DiscoverPlans(full, root, kind);
                }
                else
                {
                    error = $"the target '{target}' does not exist";
                    return null;
                }
            }
            else
            {
                plans = DiscoverPlans(root, root, kind);
            }

            if (plans.Count == 0)
            {
                error = $"no {kind} command found. Supported: .NET solutions/projects (dotnet build/test) and package.json scripts (npm run build / npm test / npm run lint)";
                return null;
            }
            if (plans.Count > 1)
            {
                error = "several possible targets: " + string.Join("; ", plans.Select(p => p.Target)) +
                        $". Name one with [RUN_CHECK: {kind} | relative/path]";
                return null;
            }

            return plans[0];
        }

        private static List<CheckPlan> DiscoverPlans(string searchRoot, string repoRoot, string kind)
        {
            var plans = new List<CheckPlan>();

            // .NET
            if (kind == "build" || kind == "test")
            {
                var solutions = EnumerateFilesLimited(searchRoot, 3, n => n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                                                                             || n.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
                var projects = EnumerateFilesLimited(searchRoot, 4, n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                                                                           || n.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                                                                           || n.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));

                if (kind == "build")
                {
                    foreach (var t in solutions.Count > 0 ? solutions : projects) plans.Add(DotnetPlan("build", t, repoRoot));
                }
                else
                {
                    // Utan testprojekt finns inget att köra (annars skulle "dotnet test" lura henne att allt gått bra)
                    var testProjects = projects.Where(IsTestProject).ToList();
                    if (testProjects.Count > 0)
                    {
                        foreach (var t in solutions.Count > 0 ? solutions : testProjects) plans.Add(DotnetPlan("test", t, repoRoot));
                    }
                }
            }

            // Node
            var packages = EnumerateFilesLimited(searchRoot, 2, n => n.Equals("package.json", StringComparison.OrdinalIgnoreCase));
            string? rootPackage = packages.FirstOrDefault(pk => string.Equals(Path.GetDirectoryName(pk), searchRoot.TrimEnd(DirSeps), StringComparison.OrdinalIgnoreCase));

            if (rootPackage != null && PackageHasScript(rootPackage, kind))
            {
                plans.Add(NodePlan(kind, rootPackage, repoRoot));
            }
            else
            {
                foreach (var pk in packages)
                {
                    if (pk != rootPackage && PackageHasScript(pk, kind)) plans.Add(NodePlan(kind, pk, repoRoot));
                }
            }

            return plans;
        }

        private static CheckPlan DotnetPlan(string kind, string targetFull, string repoRoot)
        {
            string rel = Path.GetRelativePath(repoRoot, targetFull).Replace('\\', '/');

            // -nodeReuse:false och UseSharedCompilation=false så att inga bakgrundsprocesser blir kvar efter bygget
            var args = new List<string> { kind, targetFull, "--nologo", "-v:q", "-nodeReuse:false", "-p:UseSharedCompilation=false" };
            if (kind == "test")
            {
                args.Add("--logger");
                args.Add("console;verbosity=normal");
            }

            return new CheckPlan
            {
                Tool = "dotnet",
                FileName = "dotnet",
                Args = args,
                WorkDir = Path.GetDirectoryName(targetFull) ?? repoRoot,
                Target = rel,
                Description = $"dotnet {kind} {rel}"
            };
        }

        private static CheckPlan NodePlan(string kind, string packageJsonFull, string repoRoot)
        {
            string dir = Path.GetDirectoryName(packageJsonFull) ?? repoRoot;
            string relDir = Path.GetRelativePath(repoRoot, dir).Replace('\\', '/');
            string where = relDir == "." ? "the repo root" : relDir;

            // På Windows är npm ett .cmd-skript (eller en shim, t.ex. Volta). Att starta "npm.cmd" direkt går inte alltid,
            // men cmd.exe hittar rätt oavsett hur npm är installerat. Argumenten är fasta (kind är redan validerad).
            var npmArgs = kind == "test" ? new List<string> { "test" } : new List<string> { "run", kind };
            bool windows = OperatingSystem.IsWindows();

            return new CheckPlan
            {
                Tool = "npm",
                FileName = windows ? "cmd.exe" : "npm",
                Args = windows ? new List<string> { "/d", "/c", "npm" }.Concat(npmArgs).ToList() : npmArgs,
                WorkDir = dir,
                Target = Path.GetRelativePath(repoRoot, packageJsonFull).Replace('\\', '/'),
                Description = (kind == "test" ? "npm test" : $"npm run {kind}") + $" in {where}"
            };
        }

        private static bool PackageHasScript(string packageJsonPath, string script)
        {
            try
            {
                var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath), options);
                return doc.RootElement.TryGetProperty("scripts", out var scripts)
                       && scripts.ValueKind == JsonValueKind.Object
                       && scripts.TryGetProperty(script, out var value)
                       && value.ValueKind == JsonValueKind.String
                       && !string.IsNullOrWhiteSpace(value.GetString());
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTestProject(string projectPath)
        {
            try
            {
                string text = File.ReadAllText(projectPath);
                return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static List<string> EnumerateFilesLimited(string root, int maxDepth, Func<string, bool> fileNameFilter)
        {
            var result = new List<string>();
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();

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

                foreach (var f in files)
                {
                    if (fileNameFilter(Path.GetFileName(f))) result.Add(f);
                }

                if (depth >= maxDepth) continue;

                foreach (var d in subDirs)
                {
                    if (IsSkippedDir(Path.GetFileName(d)) || IsLink(d)) continue;
                    stack.Push((d, depth + 1));
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static async Task<(int ExitCode, string Output, bool TimedOut, double Seconds)> RunProcessAsync(CheckPlan plan, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = plan.FileName,
                WorkingDirectory = plan.WorkDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (var a in plan.Args) psi.ArgumentList.Add(a);

            // Icke-interaktivt: inga väntelägen, ingen telemetri, inga bakgrundsprocesser som hänger kvar
            psi.Environment["CI"] = "true";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

            var sb = new System.Text.StringBuilder();
            var gate = new object();
            const int outputCap = 2_000_000;

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) { if (sb.Length < outputCap) sb.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) { if (sb.Length < outputCap) sb.AppendLine(e.Data); } };

            var watch = Stopwatch.StartNew();

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return (-1, $"could not start '{plan.FileName}': {ex.Message}. Is it installed and on PATH?", false, 0);
            }

            try { process.StandardInput.Close(); } catch { } // inget ska vänta på inmatning
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Vänta med HasExited i stället för WaitForExit, så att vi inte fastnar på kvarlevande barnprocesser som håller rören öppna
            bool timedOut = false;
            while (!process.HasExited)
            {
                if (watch.Elapsed.TotalSeconds > timeoutSeconds)
                {
                    timedOut = true;
                    try { process.Kill(entireProcessTree: true); } catch { }
                    break;
                }
                await Task.Delay(200);
            }

            await Task.Delay(400); // låt sista utdata hinna komma

            string output;
            lock (gate) { output = sb.ToString(); }

            int exitCode = -1;
            try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

            return (exitCode, output, timedOut, watch.Elapsed.TotalSeconds);
        }

        private static string FormatCheckOutput(CheckPlan plan, string kind, string raw, string basePath)
        {
            string text = (raw ?? "").Replace("\r\n", "\n");

            // Kortare sökvägar: gör dem relativa till repot så att hon kan använda dem direkt i READ_FILE/EDIT_FILE
            string prefix = NormalizeRepoKey(basePath) + Path.DirectorySeparatorChar;
            text = Regex.Replace(text, Regex.Escape(prefix), "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, Regex.Escape(prefix.Replace('\\', '/')), "", RegexOptions.IgnoreCase);

            var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            var sb = new System.Text.StringBuilder();
            bool keepEnd = true;

            if (plan.Tool == "dotnet")
            {
                // MSBuild lägger på "[projekt.csproj]" sist på varje rad och rapporterar ofta samma fel flera gånger
                var errors = lines
                    .Where(l => Regex.IsMatch(l, @"\berror\s+[A-Za-z]+\d+"))
                    .Select(l => Regex.Replace(l, @"\s*\[[^\]]+\.(?:csproj|fsproj|vbproj|sln|slnx)\]\s*$", ""))
                    .Distinct()
                    .ToList();
                int warnings = lines.Count(l => Regex.IsMatch(l, @"\bwarning\s+[A-Za-z]+\d+"));

                if (errors.Count > 0)
                {
                    keepEnd = false;
                    sb.AppendLine($"Errors ({errors.Count}):");
                    foreach (var e in errors.Take(30)) sb.AppendLine(e);
                    if (errors.Count > 30) sb.AppendLine($"... (+{errors.Count - 30} more errors)");
                }

                if (warnings > 0) sb.AppendLine($"({warnings} warnings, not shown)");

                if (errors.Count == 0)
                {
                    // Test-sammanfattningen och misslyckade tester ligger sist
                    var tail = lines.Where(l => !Regex.IsMatch(l, @"\bwarning\s+[A-Za-z]+\d+")).TakeLast(80);
                    foreach (var l in tail) sb.AppendLine(l);
                }
            }
            else
            {
                foreach (var l in lines.TakeLast(100)) sb.AppendLine(l);
            }

            string result = sb.ToString().TrimEnd();
            if (result.Length == 0) return "(no output)";

            if (result.Length > CheckOutputMaxChars)
            {
                result = keepEnd
                    ? "... (earlier output omitted)\n" + result.Substring(result.Length - CheckOutputMaxChars)
                    : result.Substring(0, CheckOutputMaxChars) + "\n... (truncated)";
            }

            return result;
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
                    uiMessage = "\n*(I tried to search but forgot what to look for ❌)*\n";
                }
                else
                {
                    string results = SearchWorkspace(basePath, query);
                    followUp = $"[SYSTEM INJECTION: Search results for \"{query}\" in the workspace:]\n\n```\n{results}\n```\n\n" +
                               "Now continue fulfilling the user's request. Show the user the matching files from these results. " +
                               "Use the exact paths shown. If a section ends with '... (+N more)', tell the user that N more exist that you were not shown. Never add entries that are not in the results.";
                    uiMessage = $"\n*(I'm searching the project for \"{query}\"... 🔍)*\n";
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
                uiMessage = $"\n*(I'm looking in the folder {relative}... 📂)*\n";
            }
            else
            {
                listFollowUp = $"[SYSTEM INJECTION: You tried to list {relative}, but that folder does not exist in the workspace. Tell the user.]";
                uiMessage = $"\n*(I tried to list {relative}, but there is no such folder! ❌)*\n";
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
        /// Finds all files in basePath whose filename matches the specified name,
        /// skipping ignored directories (bin, obj, node_modules, etc.), sensitive directories,
        /// hidden directories, and symlinks.
        /// </summary>
        public static List<string> FindMatchingFiles(string basePath, string filename)
        {
            var matches = new List<string>();
            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(filename) || !Directory.Exists(basePath))
                return matches;

            var stack = new Stack<string>();
            stack.Push(Path.GetFullPath(basePath));

            while (stack.Count > 0)
            {
                string dir = stack.Pop();
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

                foreach (var f in files)
                {
                    if (IsLink(f)) continue;
                    string fname = Path.GetFileName(f);
                    if (fname.Equals(filename, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(Path.GetFullPath(f));
                    }
                }

                foreach (var d in subDirs)
                {
                    string dname = Path.GetFileName(d);
                    if (IgnoredDirs.Contains(dname) ||
                        SensitiveDirNames.Contains(dname) ||
                        NoWriteDirNames.Contains(dname) ||
                        (dname.StartsWith(".") && !dname.Equals(".github", StringComparison.OrdinalIgnoreCase)) ||
                        IsLink(d))
                    {
                        continue;
                    }
                    stack.Push(d);
                }
            }

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            return matches;
        }

        /// <summary>
        /// Finds a unique matching file in the workspace if the relative path does not exist directly.
        /// Returns null and sets ambiguousMessage if multiple candidates exist.
        /// </summary>
        public static string? FindUniqueMatchingFile(string basePath, string relative, out string? ambiguousMessage)
        {
            ambiguousMessage = null;
            string? direct = ResolveSafePath(basePath, relative);
            if (direct == null) return null;

            if (File.Exists(direct))
                return direct;

            string fileName = Path.GetFileName(relative);
            if (string.IsNullOrEmpty(fileName))
                return null;

            var matches = FindMatchingFiles(basePath, fileName);
            if (matches.Count == 1)
            {
                return matches[0];
            }
            if (matches.Count > 1)
            {
                string candidates = string.Join(", ", matches.Select(m => Path.GetRelativePath(basePath, m).Replace('\\', '/')));
                ambiguousMessage = $"multiple files named '{fileName}' were found in the workspace ({candidates}) - specify the exact relative path";
                return null;
            }

            return null;
        }

        private static string GetLeadingWhitespace(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }

        private static List<string> AdjustReplacementIndentation(string[] targetLines, string[] findLines, string[] replacementLines)
        {
            string targetFirstIndent = targetLines.Length > 0 ? GetLeadingWhitespace(targetLines[0]) : "";
            string findFirstIndent = findLines.Length > 0 ? GetLeadingWhitespace(findLines[0]) : "";

            bool targetPrefersTabs = targetFirstIndent.Contains('\t');
            int tabSize = 4;

            var result = new List<string>();

            foreach (var rLine in replacementLines)
            {
                if (string.IsNullOrWhiteSpace(rLine))
                {
                    result.Add("");
                    continue;
                }

                string rIndent = GetLeadingWhitespace(rLine);
                string rContent = rLine.Substring(rIndent.Length);

                string finalIndent;
                if (findFirstIndent.Length > 0 && rIndent.StartsWith(findFirstIndent))
                {
                    string relativeIndent = rIndent.Substring(findFirstIndent.Length);
                    finalIndent = targetFirstIndent + relativeIndent;
                }
                else if (findFirstIndent.Length == 0 && targetFirstIndent.Length > 0)
                {
                    finalIndent = targetFirstIndent + rIndent;
                }
                else
                {
                    finalIndent = rIndent;
                }

                if (targetPrefersTabs)
                {
                    int spaces = 0;
                    var sbIndent = new System.Text.StringBuilder();
                    foreach (char ch in finalIndent)
                    {
                        if (ch == '\t')
                        {
                            if (spaces > 0)
                            {
                                sbIndent.Append(new string('\t', (spaces + tabSize - 1) / tabSize));
                                spaces = 0;
                            }
                            sbIndent.Append('\t');
                        }
                        else if (ch == ' ')
                        {
                            spaces++;
                            if (spaces == tabSize)
                            {
                                sbIndent.Append('\t');
                                spaces = 0;
                            }
                        }
                    }
                    if (spaces > 0) sbIndent.Append(new string(' ', spaces));
                    finalIndent = sbIndent.ToString();
                }
                else if (targetFirstIndent.Length > 0 && !targetPrefersTabs)
                {
                    finalIndent = finalIndent.Replace("\t", new string(' ', tabSize));
                }

                result.Add(finalIndent + rContent);
            }

            return result;
        }

        /// <summary>
        /// Skriver en fil i projektet. Nekar sökvägar utanför projektet, länkar, känsliga filer och skrivförbjudna mappar.
        /// Befintliga filer säkerhetskopieras först, och BOM och radslut bevaras. Stora befintliga filer får inte skrivas
        /// över i sin helhet (hon ser bara första delen av dem), de ska ändras med EDIT_FILE.
        /// </summary>
        private static bool TryWriteFile(string basePath, string relativeFilename, string content,
                                         SlimeBrainManager brain, string tag, out string error)
        {
            error = "";

            string? fullPath = ResolveSafePath(basePath, relativeFilename);
            if (fullPath == null) { error = "the path is outside the project"; return false; }
            if (Directory.Exists(fullPath)) { error = "the path is a folder"; return false; }
            if (IsNoWritePath(basePath, fullPath)) { error = "protected folder (.git, hooks, etc.)"; return false; }
            if (IsSensitivePath(basePath, fullPath)) { error = "protected file (secrets/keys)"; return false; }

            try
            {
                bool hasBom = false;
                bool crlf = false;

                if (File.Exists(fullPath))
                {
                    if (!HasReadRecently(fullPath))
                    {
                        error = "the file already exists. Read it with READ_FILE first so you do not overwrite content you have not seen (and prefer EDIT_FILE)";
                        return false;
                    }

                    if (new FileInfo(fullPath).Length > MaxReadChars)
                    {
                        error = $"the file is large (over {MaxReadChars} characters), use EDIT_FILE to change parts of it instead of overwriting the whole file";
                        return false;
                    }

                    byte[] head = new byte[3];
                    using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        int n = fs.Read(head, 0, 3);
                        hasBom = n == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                    }
                    crlf = File.ReadAllText(fullPath).Contains("\r\n");

                    if (!brain.BackupFile(tag, basePath, fullPath))
                    {
                        error = "could not make a backup, so the file was left untouched";
                        return false;
                    }
                }
                else
                {
                    // Prevent creating an orphaned duplicate file in the root if a file with the same name exists in a subfolder
                    string fileName = Path.GetFileName(relativeFilename);
                    var matches = string.IsNullOrEmpty(fileName) ? new List<string>() : FindMatchingFiles(basePath, fileName);
                    if (matches.Count == 1)
                    {
                        string existingRel = Path.GetRelativePath(basePath, matches[0]).Replace('\\', '/');
                        error = $"a file named '{fileName}' already exists at '{existingRel}'. Use EDIT_FILE to modify it, or specify '{existingRel}' if you intend to overwrite it";
                        return false;
                    }
                    if (matches.Count > 1)
                    {
                        string candidates = string.Join(", ", matches.Select(m => Path.GetRelativePath(basePath, m).Replace('\\', '/')));
                        error = $"multiple files named '{fileName}' already exist in the workspace ({candidates}). Specify the full relative path";
                        return false;
                    }
                }

                if (crlf) content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? basePath);
                File.WriteAllText(fullPath, content, new System.Text.UTF8Encoding(hasBom));
                MarkRead(fullPath);
                NoteFileChanged(basePath, fullPath);
                ModificationCount++;
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