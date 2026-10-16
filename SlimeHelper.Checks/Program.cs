using System.Text;

namespace SlimeHelper.Checks
{
    public static class Program
    {
        static int passes = 0;
        static readonly List<string> failures = new();

        static void Check(bool ok, string name, string detail = "")
        {
            if (ok) { passes++; Console.WriteLine("  ok   " + name); }
            else
            {
                failures.Add(name);
                Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? "\n       " + detail.Replace("\n", "\n       ") : ""));
            }
        }

        static string Short(string s, int n = 400) => s.Length <= n ? s : s.Substring(0, n) + "...";
        static string Id() => Guid.NewGuid().ToString("N").Substring(0, 6);

        static string Tool(string response, string tag, string repo, SlimeBrainManager brain, out string follow, out List<SlimeToolManager.CheckRequest> checks)
            => SlimeToolManager.ProcessResponse(response, tag, repo, brain, out follow, out checks);

        static void WriteCrlfBom(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        }

        static string NewRepo(string work, SlimeBrainManager brain, string prefix, out string tag)
        {
            string dir = Path.Combine(work, prefix + Id());
            Directory.CreateDirectory(dir);
            tag = "#" + prefix + Id();
            var bound = brain.BindRepo(tag, dir);
            if (!bound.Success) throw new InvalidOperationException("could not bind test repo: " + bound.Message);
            return dir;
        }

        public static async Task<int> Main()
        {
            string work = Path.Combine(Path.GetTempPath(), "slime_checks_" + Id());
            string brainRoot = Path.Combine(work, "brain");
            Directory.CreateDirectory(work);
            Console.WriteLine($"Running on .NET {Environment.Version}. Work folder: {work}\n");

            try
            {
                var brain = new SlimeBrainManager(brainRoot);

                BrainTests(brain, work);
                MemoryTests(brain, work, brainRoot);
                HistoryTests(brain, work, brainRoot);
                ClaimTests(brain, work);
                EditAndFileTests(brain, work, brainRoot);
                SearchAndHelperTests(brain, work);
                await RunCheckTests(brain, work);
                await TrailTests(brain, work);
                ToolExecutionSpecialistTests(brain, work);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }

            Console.WriteLine($"\n{passes} passed, {failures.Count} failed.");
            foreach (var f in failures) Console.WriteLine("  - " + f);
            return failures.Count == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ brain
        static void BrainTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("== Brain: binding and lookup");
            string repoA = Path.Combine(work, "repoA");
            string sub = Path.Combine(repoA, "sub");
            Directory.CreateDirectory(sub);

            var bound = brain.BindRepo("MyRepo", repoA);
            Check(bound.Success && bound.Message == "#myrepo", "BindRepo normalises the tag (case and #)", bound.Message);
            Check(brain.GetBoundReposFromPrompt("read README in myrepo").Count == 1, "tag found without #");
            Check(brain.GetBoundReposFromPrompt("look at #MyRepo please").Count == 1, "tag found with # and other case");
            Check(brain.GetBoundReposFromPrompt("nothing to see here").Count == 0, "an unbound prompt finds nothing");

            var cwd = brain.FindBoundRepoForDirectory(sub);
            Check(cwd != null && cwd.Value.Tag == "#myrepo", "working directory inside a bound repo resolves to it");
            Check(brain.FindBoundRepoForDirectory(work) == null, "a folder outside every repo resolves to nothing");

            brain.BindRepo("inner", sub);
            Check(brain.FindBoundRepoForDirectory(sub)?.Tag == "#inner", "the innermost bound repo wins");

            var missing = brain.BindRepo("ghost", Path.Combine(work, "does-not-exist"));
            Check(!missing.Success && missing.Message.Contains("not found"), "binding a missing folder fails with a reason", missing.Message);
        }

        // ------------------------------------------------------------------ memory (SAVE_NOTE)
        static void MemoryTests(SlimeBrainManager brain, string work, string brainRoot)
        {
            Console.WriteLine("\n== Memory: SAVE_NOTE with folders, @Subjects and General");

            // two repos whose names share a prefix: "pfx" and "pfxsibling"
            string repoA = Path.Combine(work, "memA"); Directory.CreateDirectory(repoA);
            string repoB = Path.Combine(work, "memB"); Directory.CreateDirectory(repoB);
            brain.BindRepo("pfx", repoA);
            brain.BindRepo("pfxsibling", repoB);

            string Save(string tag, string note) =>
                SlimeToolManager.ProcessResponse($"[SAVE_NOTE: {note}]\n# body\n[/SAVE_NOTE]", tag, repoA, brain, out _, out _);

            string ui = Save("#pfx", "people/anna.md");
            Check(ui.Contains("I saved the note") && File.Exists(Path.Combine(brainRoot, "repos", "pfx", "people", "anna.md")), "a note in a sub-folder is saved (folder created)", ui);

            ui = Save("#pfx", "plain");
            Check(File.Exists(Path.Combine(brainRoot, "repos", "pfx", "plain.md")), ".md is added when missing", ui);

            ui = Save("@Anna", "likes.md");
            Check(File.Exists(Path.Combine(brainRoot, "Subjects", "Anna", "likes.md")), "an @Subject note goes to Subjects/<name>/", ui);

            ui = Save("General", "todo.md");
            Check(File.Exists(Path.Combine(brainRoot, "Notes", "todo.md")), "a General note goes to Notes/", ui);

            string sibling = Path.Combine(brainRoot, "repos", "pfxsibling", "pwned.md");
            ui = Save("#pfx", "../pfxsibling/pwned.md");
            Check(!File.Exists(sibling) && ui.Contains("couldn't save"), "a sibling folder with the same name prefix is NOT writable", ui);

            string up = Path.Combine(brainRoot, "user", "pwned2.md");
            ui = Save("#pfx", "../../user/pwned2.md");
            Check(!File.Exists(up) && ui.Contains("couldn't save"), "../ out of the memory folder is refused, and she is told", ui);

            string outside = Path.Combine(work, "outside.md");
            ui = Save("#pfx", outside);
            Check(!File.Exists(outside), "an absolute path outside the brain is never written", ui);
        }

        // ------------------------------------------------------------------ history hygiene
        static void HistoryTests(SlimeBrainManager brain, string work, string brainRoot)
        {
            Console.WriteLine("\n== History: the app's status lines never reach the model");

            string thread = "#hist" + Id();
            brain.SaveMessage(thread, "User", "add a method Greet()");
            brain.SaveMessage(thread, "Slime", "Ugh, fine.\n\n*(I edited src/A.cs: 1 change ✏️)*\n\n*(I sigh deeply)*\n\nThere. Done.");
            brain.SaveMessage(thread, "Slime", "*(I'm reading the file src/A.cs... 📖)*"); // nothing but a status line
            string ctx = brain.GetHistoryContext(thread);
            Check(!ctx.Contains("I edited") && ctx.Contains("There. Done."), "status lines are removed, her own words stay", ctx);
            Check(ctx.Contains("*(I sigh deeply)*"), "an ordinary flourish on its own line is kept", ctx);
            Check(ctx.Split('\n').Count(l => l.StartsWith("Slime:")) == 1, "a reply that was only a status line is not stored at all", ctx);

            string json = File.ReadAllText(Path.Combine(brainRoot, "memory", "chat_history.json"));
            Check(!json.Contains("I edited") && !json.Contains("I'm reading"), "the saved history file contains no status lines either");

            // history that was poisoned before this fix: Swedish lines, the old marker and an invented build line
            string root2 = Path.Combine(work, "brain_legacy");
            Directory.CreateDirectory(Path.Combine(root2, "memory"));
            var legacy = new Dictionary<string, object[]>
            {
                ["#old"] = new object[]
                {
                    new { Role = "User", Content = "x" },
                    new { Role = "Slime", Content = "A\n\n*(utfört: EDIT_FILE SlimeHelper/SlimeMemory.cs)*\n\n*(Jag ändrade SlimeHelper/SlimeMemory.cs: 1 ändring ✏️)*\n\n*(Jag läser filen X... 📖)*\n\n*(Bygger... 🛠️)*\n\nB" }
                }
            };
            File.WriteAllText(Path.Combine(root2, "memory", "chat_history.json"), System.Text.Json.JsonSerializer.Serialize(legacy));
            var brain2 = new SlimeBrainManager(root2);
            string ctx2 = brain2.GetHistoryContext("#old");
            Check(!ctx2.Contains("utfört") && !ctx2.Contains("Jag ") && !ctx2.Contains("Bygger") && ctx2.Contains("A") && ctx2.Contains("B"),
                  "old poisoned history (Swedish lines, old marker, invented build line) is cleaned when replayed", ctx2);
        }

        // ------------------------------------------------------------------ claims vs reality
        static void ClaimTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("\n== Claims: she must not say she changed something the app never wrote");

            // the actual false claims from the user's history, and ordinary talk that must NOT count
            string[] claims =
            {
                "There. I've *shoved* your `Greet2()` method in there. It returns \"Hej\", just like you wanted.",
                "There. I've removed that pesky semicolon from `Greet2()`.",
                "Ugh, fine. There. I've *finally* added your `Greet()` method to `SlimeMemory.cs`.",
                "I’ve updated the README.md for you.",
                "Done! I created a new folder for it and saved the note."
            };
            string[] innocent =
            {
                "You want me to read the README? Fine. Here are the first three lines.",
                "I tried to hide my disappointment.",
                "I couldn't edit SlimeMemory.cs: the file is protected.",
                "Sure, you could add a method Greet() to the class if you like.",
                "I've read the file. It has ten lines and one class.",
                "Honestly, I've never seen so much slime in one project."
            };
            Check(claims.All(SlimeToolManager.ClaimsChange), "claims of a change are recognised (incl. the real ones from the history)",
                  string.Join(" | ", claims.Where(x => !SlimeToolManager.ClaimsChange(x))));
            Check(!innocent.Any(SlimeToolManager.ClaimsChange), "ordinary talk is not mistaken for a claim",
                  string.Join(" | ", innocent.Where(SlimeToolManager.ClaimsChange)));

            // decisions: only when nothing was written, she has tools, and the user did not ask about the past
            string root = NewRepo(work, brain, "k", out string tag);
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A\n{\n    int x = 1;\n}\n");
            string claim = claims[0];

            SlimeToolManager.ResetModificationCount();
            Check(SlimeToolManager.ShouldCorrectFalseClaim(claim, tag, 1, "lägg till en metod Greet2()"), "a claim with nothing written -> she is corrected");
            Check(!SlimeToolManager.ShouldCorrectFalseClaim(claim, "General", 1, "add a method"), "no correction in General (she has no tools there)");
            Check(!SlimeToolManager.ShouldCorrectFalseClaim(claim, tag, 0, "add a method"), "only one correction per request");
            Check(!SlimeToolManager.ShouldCorrectFalseClaim(claim, tag, 1, "what did you change in A.cs?"), "a question about past work is answered truthfully, not corrected");
            Check(!SlimeToolManager.ShouldCorrectFalseClaim(claim, tag, 1, "Vad gjorde du med A.cs?"), "same in Swedish");
            Check(SlimeToolManager.HasUnbackedClaim(claim + "\n\n*(I edited A.cs: 1 change ✏️)*", "add x"), "a status line in the text does not count as proof (only real writes do)");

            // real writes are counted, refused ones are not
            Tool("[EDIT_FILE: A.cs]\n[FIND]\n    int x = 1;\n[/FIND]\n[REPLACE]\n    int x = 2;\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out _, out _);
            Check(SlimeToolManager.ModificationCount == 0, "a refused edit (file not read) does not count");
            Tool("[READ_FILE: A.cs]", tag, root, brain, out _, out _);
            Tool("[EDIT_FILE: A.cs]\n[FIND]\n    int x = 1;\n[/FIND]\n[REPLACE]\n    int x = 2;\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out _, out _);
            Check(SlimeToolManager.ModificationCount == 1, "a real edit counts");
            Check(!SlimeToolManager.HasUnbackedClaim(claim, "add a method"), "once something was really written, the claim is backed");
            Tool("[SAVE_NOTE: x/y.md]\n# n\n[/SAVE_NOTE]", tag, root, brain, out _, out _);
            Check(SlimeToolManager.ModificationCount == 2, "a saved note counts");
            Tool("[SAVE_NOTE: ../../x.md]\n# n\n[/SAVE_NOTE]", tag, root, brain, out _, out _);
            Check(SlimeToolManager.ModificationCount == 2, "a refused note does not count");

            // --- announcing an action instead of doing it ("Adding Greet2() to SlimeMemory.cs ...") ---
            string[] changeRequests =
            {
                "lägg till en metod Greet2() i SlimeMemory.cs som returnerar Hej",
                "'ta bort semikolonet efter return i Greet2 och bygg'",
                "remember the folder youre in right now C:\\Users\\x\\.antigravity its where we keep settings",
                "yeah start a memory folder called antigravity where we can save information about antigravity",
                "please add a method Greet() to the class"
            };
            string[] notRequests =
            {
                "vilka filer finns?", "var används AiService?", "how do I add a method to a class?", "hur lägger jag till en metod?",
                "what did you change in A.cs?", "läs README och sammanfatta", "explain what Greet does", "vad gör SlimeMemory?"
            };
            Check(changeRequests.All(SlimeToolManager.IsChangeRequest), "requests to change/save something are recognised (English and Swedish)",
                  string.Join(" | ", changeRequests.Where(x => !SlimeToolManager.IsChangeRequest(x))));
            Check(!notRequests.Any(SlimeToolManager.IsChangeRequest), "questions and read-only requests are not change requests",
                  string.Join(" | ", notRequests.Where(SlimeToolManager.IsChangeRequest)));

            SlimeToolManager.ResetModificationCount();
            string announcement = "Ugh, fine. Another method? Adding `Greet2()` to `SlimeMemory.cs` to return \"Hej\". Whatever you say, Master.";
            Check(!SlimeToolManager.ClaimsChange(announcement), "(\"Adding X...\" is not a past-tense claim, which is why the old check missed it)");
            Check(SlimeToolManager.CheckUnbacked(announcement, changeRequests[0]) == SlimeToolManager.UnbackedKind.Request, "an announcement with nothing written after a change request is caught");
            Check(SlimeToolManager.ShouldCorrectFalseClaim(announcement, tag, 1, changeRequests[0]), "...and she gets one chance to do it for real");
            Check(SlimeToolManager.CorrectionFor(announcement, changeRequests[0]).Contains("only talked about it"), "...with a correction that says she only talked about it");
            Check(SlimeToolManager.CorrectionFor(claim, "add x").Contains("did NOT write anything"), "a false claim gets the other correction");
            Check(SlimeToolManager.WarningFor(announcement, changeRequests[0], tag)!.Contains("Nothing was actually changed"), "if it still is not done, the reply ends with a plain warning");
            Check(SlimeToolManager.WarningFor("Creating 'antigravity' for you.", changeRequests[3], "General")!.Contains("no tools"), "in General the warning explains that there are no tools (use a repo or @subject)");
            Check(!SlimeToolManager.ShouldCorrectFalseClaim("Creating 'antigravity'.", "General", 1, changeRequests[3]), "...and General is not corrected (she cannot do it there)");
            Check(SlimeToolManager.WarningFor("The method is called Greet2.", "vad heter metoden?", tag) == null, "a plain question with a plain answer gets no warning");
            Check(SlimeToolManager.WarningFor(announcement, "what did you change in SlimeMemory.cs?", tag) == null, "questions about past work get no warning");
            Check(SlimeToolManager.NoteFor(announcement, changeRequests[0]).Contains("asked for a change"), "the history note says the requested change does NOT exist");

            // --- the instructions she sees ---
            string ins = SlimeToolManager.GetToolInstructions(root);
            Check(ins.Contains("MUST contain the tool blocks") && ins.Contains("does NOTHING"), "the instructions say that announcing a change without a tool block does nothing");
            Check(ins.Contains("EXAMPLE") && ins.Contains("[EDIT_FILE: Greeter.cs]") && ins.Contains("[RUN_CHECK: build]"), "the instructions contain a worked example (read, edit, build)");
            Check(SlimeToolManager.GetToolInstructions("").Contains("MUST contain a [SAVE_NOTE] block"), "memory-only threads (@subject) are told that a save request needs a SAVE_NOTE block");

            string warned = SlimeToolManager.RemoveStatusLines("hi\n\n" + SlimeToolManager.NothingChangedWarning);
            Check(!warned.Contains("Nothing was actually changed"), "the app's own warning line is never replayed to the model", warned);
            SlimeToolManager.ResetModificationCount();
        }

        // ------------------------------------------------------------------ edit / read / write
        static void EditAndFileTests(SlimeBrainManager brain, string work, string brainRoot)
        {
            string root = NewRepo(work, brain, "t", out string tag);

            string calc = "namespace Demo\n{\n    public class Calc\n    {\n        public int Add(int a, int b)\n        {\n            return a + b;\n        }\n    }\n}\n";
            WriteCrlfBom(Path.Combine(root, "src", "Calc.cs"), calc);
            File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1");
            File.WriteAllText(Path.Combine(root, "README.md"), "# Demo\nslime lives here\n");
            File.WriteAllText(Path.Combine(root, "big.txt"), string.Join("\n", Enumerable.Range(1, 5000).Select(i => "line " + i + " lorem ipsum dolor sit amet")));
            File.WriteAllText(Path.Combine(root, "small.txt"), "old content\n");

            Console.WriteLine("\n== EDIT_FILE: read before edit");
            string edit1 = "[EDIT_FILE: src/Calc.cs]\n[FIND]\n            return a + b;\n[/FIND]\n[REPLACE]\n            return a + b + 0;\n[/REPLACE]\n[/EDIT_FILE]";
            Tool(edit1, tag, root, brain, out var f1, out _);
            Check(f1.Contains("have not read"), "editing a file she has not read is refused", f1);
            Check(File.ReadAllText(Path.Combine(root, "src", "Calc.cs")).Contains("return a + b;"), "the file is untouched after the refusal");

            Tool("[READ_FILE: src/Calc.cs]", tag, root, brain, out var fr, out _);
            Check(fr.Contains("[FILE: src/Calc.cs (") && fr.Contains("return a + b;"), "READ_FILE returns the content", Short(fr));

            Console.WriteLine("\n== EDIT_FILE: success keeps BOM and CRLF, and makes a backup");
            string ui = Tool(edit1, tag, root, brain, out var f2, out _);
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "src", "Calc.cs"));
            string after = Encoding.UTF8.GetString(bytes);
            Check(ui.Contains("I edited src/Calc.cs: 1 change"), "status line is English and comes from the app", ui);
            Check(f2 == "", "no follow-up after a successful edit", f2);
            Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM preserved");
            Check(after.Contains("return a + b + 0;") && after.Contains("\r\n") && !after.Replace("\r\n", "").Contains("\n"), "CRLF preserved and content changed");

            string backupDir = Path.Combine(brainRoot, "repos", tag.TrimStart('#'), "backups");
            string[] baks = Directory.Exists(backupDir) ? Directory.GetFiles(backupDir, "*Calc.cs.bak") : Array.Empty<string>();
            Check(baks.Length == 1, "a backup was created in the brain", backupDir);
            string bak = baks.Length > 0 ? File.ReadAllText(baks[0]) : "";
            Check(bak.Contains("return a + b;") && !bak.Contains("+ 0"), "the backup holds the ORIGINAL file");

            Console.WriteLine("\n== EDIT_FILE: bad edits are rejected");
            string unbalanced = "[EDIT_FILE: src/Calc.cs]\n[FIND]\n    public class Calc\n[/FIND]\n[REPLACE]\n    public class Calc\n    {\n        public int X() { return 1; }\n[/REPLACE]\n[/EDIT_FILE]";
            Tool(unbalanced, tag, root, brain, out var f3, out _);
            Check(f3.Contains("unbalance the curly braces"), "unbalanced braces are rejected (the SlimeMemory.cs failure)", f3);
            Check(!File.ReadAllText(Path.Combine(root, "src", "Calc.cs")).Contains("X()"), "the file is untouched after the brace rejection");
            Tool("[EDIT_FILE: src/Calc.cs]\n[FIND]\nnot in the file\n[/FIND]\n[REPLACE]\nx\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var f4, out _);
            Check(f4.Contains("does not match the file exactly"), "a FIND that is not in the file is rejected", f4);
            Tool("[EDIT_FILE: src/Calc.cs]\n[FIND]\nint\n[/FIND]\n[REPLACE]\nlong\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var f5, out _);
            Check(f5.Contains("several places"), "a FIND that is not unique is rejected", f5);
            Tool("[EDIT_FILE: ../outside.txt]\n[FIND]\na\n[/FIND]\n[REPLACE]\nb\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var f6, out _);
            Check(f6.Contains("FAILED"), "a path outside the repo is rejected", f6);
            Tool("[EDIT_FILE: .env]\n[FIND]\nSECRET\n[/FIND]\n[REPLACE]\nX\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var f7, out _);
            Check(f7.Contains("protected file") && File.ReadAllText(Path.Combine(root, ".env")) == "SECRET=1", "editing .env is refused", f7);

            Console.WriteLine("\n== READ_FILE");
            Tool("[READ_FILE: big.txt | 2-3]", tag, root, brain, out var r1, out _);
            Check(r1.Contains("(lines 2-3 of 5000)") && r1.Contains("line 2 lorem") && !r1.Contains("line 4 lorem"), "line ranges work", Short(r1));
            Tool("[READ_FILE: big.txt]", tag, root, brain, out var r2, out _);
            Check(r2.Contains("Truncated after") && r2.Contains("start-end"), "a large file is truncated with a hint", Short(r2, 200));
            Tool("[READ_FILE: .env]", tag, root, brain, out var r3, out _);
            Check(r3.Contains("ACCESS DENIED") && !r3.Contains("SECRET=1"), ".env cannot be read", r3);
            Tool("[READ_FILE: " + tag + "/README.md]", tag, root, brain, out var r4, out _);
            Check(r4.Contains("slime lives here"), "the repo tag is stripped from a path", r4);

            Console.WriteLine("\n== WRITE_CODE guards");
            ui = Tool("[WRITE_CODE: small.txt]\nnew content\n[/WRITE_CODE]", tag, root, brain, out _, out _);
            Check(ui.Contains("I couldn't write small.txt") && File.ReadAllText(Path.Combine(root, "small.txt")).Contains("old content"), "overwriting a file she has not read is refused", ui);
            Tool("[READ_FILE: small.txt]", tag, root, brain, out _, out _);
            ui = Tool("[WRITE_CODE: small.txt]\nnew content\n[/WRITE_CODE]", tag, root, brain, out _, out _);
            Check(ui.Contains("I wrote source code to small.txt") && File.ReadAllText(Path.Combine(root, "small.txt")).Contains("new content"), "overwriting after reading works", ui);
            Tool("[READ_FILE: big.txt | 1-2]", tag, root, brain, out _, out _);
            ui = Tool("[WRITE_CODE: big.txt]\nx\n[/WRITE_CODE]", tag, root, brain, out _, out _);
            Check(ui.Contains("the file is large") && File.ReadAllText(Path.Combine(root, "big.txt")).Length > 1000, "overwriting a large file is refused", ui);
            ui = Tool("[WRITE_CODE: brand/new.cs]\nclass A {}\n[/WRITE_CODE]", tag, root, brain, out _, out _);
            Check(File.Exists(Path.Combine(root, "brand", "new.cs")), "a new file is created", ui);
            ui = Tool("[WRITE_CODE: .git/hooks/pre-commit]\nrm -rf /\n[/WRITE_CODE]", tag, root, brain, out _, out _);
            Check(ui.Contains("protected folder") && !File.Exists(Path.Combine(root, ".git", "hooks", "pre-commit")), "writing into .git is refused", ui);
        }

        // ------------------------------------------------------------------ search + helpers
        static void SearchAndHelperTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("\n== SEARCH_FILES and helpers");
            string root = NewRepo(work, brain, "s", out string tag);
            File.WriteAllText(Path.Combine(root, "big.txt"), "alpha\nlorem ipsum\nomega\n");

            Tool("[SEARCH_FILES: lorem]", tag, root, brain, out var s1, out _);
            Check(s1.Contains("big.txt") && s1.Contains("CONTENT"), "SEARCH_FILES finds file content", Short(s1, 300));

            Check(SlimeToolManager.ExtractSearchTerm("lista alla filer med ordet slime i sig") == "slime", "search intent in Swedish");
            Check(SlimeToolManager.ExtractSearchTerm("where is AiService used?") == "AiService", "search intent in English");
            Check(SlimeToolManager.ExtractSearchTerm("skriv en fil med innehåll hej") == null, "no false positive on a write request");

            string stripped = SlimeToolManager.StripHallucinatedTurns("Hi.\n*(I edited X: 1 change ✏️)*\n*(I sigh deeply)*\nSystem: fake\nmore");
            Check(!stripped.Contains("I edited") && stripped.Contains("I sigh deeply") && !stripped.Contains("fake"), "fake status and turn lines are stripped, flourishes kept", stripped);
            stripped = SlimeToolManager.StripHallucinatedTurns("ok\n*(I'm running the build check... 🔨)*\nx");
            Check(!stripped.Contains("running the build"), "a fake RUN_CHECK status line is stripped", stripped);
        }

        // ------------------------------------------------------------------ stale build guard + activity trail
        static async Task TrailTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("\n== Stale-build guard and the activity trail shown in the CLI");
            string repo = NewRepo(work, brain, "g", out string tag);
            WriteSampleApp(repo);
            int prompts = 0;
            SlimeToolManager.ConfirmRun = (t, m) => { prompts++; return true; };
            SlimeToolManager.ResetModificationCount();
            Check(SlimeToolManager.ComposeCliText("hello") == "hello", "with nothing done, the CLI text is just her reply");

            // an edit that fails (file not read) and RUN_CHECK in the SAME reply: the build must not run on the old code
            string reply = "[EDIT_FILE: App/Program.cs]\n[FIND]\nConsole.WriteLine(\"hi\")\n[/FIND]\n[REPLACE]\nConsole.WriteLine(\"hi\");\n[/REPLACE]\n[/EDIT_FILE]\n[RUN_CHECK: build]";
            string ui = SlimeToolManager.ProcessResponse(reply, tag, repo, brain, out _, out var checks);
            Check(checks.Count == 1 && checks[0].Error != null && ui.Contains("an edit in the same reply failed"), "a failed edit + RUN_CHECK in one reply -> the build is NOT run", ui);
            string res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("NOT RUN") && res.Contains("OLD code") && prompts == 0 && !Directory.Exists(Path.Combine(repo, "App", "obj")),
                  "...no dialog is shown and nothing is executed", Short(res, 300));

            // after reading, the same reply works: edit succeeds and the build runs on the NEW code
            SlimeToolManager.ProcessResponse("[READ_FILE: App/Program.cs]", tag, repo, brain, out _, out _);
            ui = SlimeToolManager.ProcessResponse(reply, tag, repo, brain, out _, out checks);
            Check(checks.Count == 1 && checks[0].Error == null, "after reading, a successful edit + RUN_CHECK in one reply -> the build runs", ui);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("SUCCEEDED") && prompts == 1, "...and it builds the edited code", Short(res, 300));

            // the trail: what really happened, in order, each step once
            string cli = SlimeToolManager.ComposeCliText("Done. The build is green.");
            int iFail = cli.IndexOf("I couldn't edit App/Program.cs");
            int iRead = cli.IndexOf("I'm reading the file App/Program.cs");
            int iEdit = cli.IndexOf("I edited App/Program.cs");
            int iBuild = cli.IndexOf("dotnet build App/App.csproj - SUCCEEDED");
            Check(iFail >= 0 && iFail < iRead && iRead < iEdit && iEdit < iBuild, "the CLI text lists every step in order (failed edit, read, edit, build result)", cli);
            Check(cli.IndexOf("I edited App/Program.cs") == cli.LastIndexOf("I edited App/Program.cs"), "no step is shown twice");
            Check(cli.TrimEnd().EndsWith("Done. The build is green."), "her own reply comes last", cli);
            Check(!SlimeToolManager.ComposeCliText("Hi.\n\n*(I edited App/Program.cs: 1 change ✏️)*\n\nBye.").Contains("Hi.\n\n*(I edited"), "status lines inside her reply are moved to the trail");
            SlimeToolManager.ResetModificationCount();
        }

        // ------------------------------------------------------------------ RUN_CHECK
        // Starts npm the same way Slime's RUN_CHECK does (through cmd.exe on Windows) and reports WHY it failed.
        static bool NpmAvailable(out string why)
        {
            why = "";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "npm",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (OperatingSystem.IsWindows())
                {
                    psi.ArgumentList.Add("/d");
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add("npm");
                }
                psi.ArgumentList.Add("--version");

                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardInput.Close();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(true); } catch { }
                    why = "'npm --version' did not finish within 30 seconds";
                    return false;
                }

                Task.WaitAll(stdout, stderr);
                if (p.ExitCode != 0)
                {
                    why = $"'npm --version' exited with code {p.ExitCode}: {(stderr.Result + " " + stdout.Result).Trim()}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static void WriteSampleApp(string repo)
        {
            // Same target framework as this program, no packages: builds offline.
            string tfm = "net" + Environment.Version.Major + ".0";
            Directory.CreateDirectory(Path.Combine(repo, "App"));
            File.WriteAllText(Path.Combine(repo, "NuGet.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /></packageSources></configuration>");
            File.WriteAllText(Path.Combine(repo, "App", "App.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{tfm}</TargetFramework><ImplicitUsings>enable</ImplicitUsings><UseAppHost>false</UseAppHost><_RequiresILLinkPack>false</_RequiresILLinkPack></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(repo, "App", "Program.cs"), "Console.WriteLine(\"hi\")\n"); // missing semicolon on purpose
        }

        static async Task RunCheckTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("\n== RUN_CHECK: a real dotnet build");
            string repo = NewRepo(work, brain, "c", out string tag);
            WriteSampleApp(repo);

            int prompts = 0; bool answer = true; string lastPrompt = "";
            SlimeToolManager.ConfirmRun = (title, msg) => { prompts++; lastPrompt = msg; return answer; };

            string ui = SlimeToolManager.ProcessResponse("Building.\n[RUN_CHECK: build]", tag, repo, brain, out _, out var checks);
            Check(checks.Count == 1 && checks[0].Kind == "build" && ui.Contains("running the build check"), "the RUN_CHECK tag becomes a request", ui);

            string res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(prompts == 1 && lastPrompt.Contains("dotnet build App/App.csproj"), "the user is asked once, with the exact command", lastPrompt);
            Check(res.Contains("FAILED") && res.Contains("error CS1002"), "a failing build is reported with the compiler error", Short(res, 700));
            Check(res.Contains("Never claim that the code builds"), "the result tells her not to claim success");

            // fix and rebuild: trusted, so no new prompt
            SlimeToolManager.ProcessResponse("[READ_FILE: App/Program.cs]", tag, repo, brain, out _, out _);
            SlimeToolManager.ProcessResponse("[EDIT_FILE: App/Program.cs]\n[FIND]\nConsole.WriteLine(\"hi\")\n[/FIND]\n[REPLACE]\nConsole.WriteLine(\"hi\");\n[/REPLACE]\n[/EDIT_FILE]", tag, repo, brain, out var ef, out _);
            Check(ef == "", "the fix is applied", ef);
            SlimeToolManager.ProcessResponse("[RUN_CHECK: build]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("SUCCEEDED") && prompts == 1, "the fixed build SUCCEEDED, still only one prompt", Short(res, 500));

            // editing a build file revokes the approval
            SlimeToolManager.ProcessResponse("[READ_FILE: App/App.csproj]", tag, repo, brain, out _, out _);
            SlimeToolManager.ProcessResponse("[EDIT_FILE: App/App.csproj]\n[FIND]\n<ImplicitUsings>enable</ImplicitUsings>\n[/FIND]\n[REPLACE]\n<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>\n[/REPLACE]\n[/EDIT_FILE]", tag, repo, brain, out _, out _);
            SlimeToolManager.ProcessResponse("[RUN_CHECK: build]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(prompts == 2 && res.Contains("SUCCEEDED"), "editing the .csproj makes her ask again", $"prompts={prompts}\n{Short(res, 300)}");

            Console.WriteLine("\n== RUN_CHECK: things that must NOT run");
            SlimeToolManager.ProcessResponse("[RUN_CHECK: test]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("NOT RUN") && res.Contains("no test command"), "test without a test project is NOT reported as passing", Short(res, 300));
            SlimeToolManager.ProcessResponse("[RUN_CHECK: lint]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("NOT RUN"), "lint with nothing to run is NOT RUN", Short(res, 300));
            SlimeToolManager.ProcessResponse("[RUN_CHECK: deploy]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("NOT RUN") && res.Contains("unknown check"), "an unknown check is refused (allowlist)", Short(res, 300));
            SlimeToolManager.ProcessResponse("[RUN_CHECK: build | ../../etc]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("NOT RUN") && res.Contains("outside"), "a target outside the repo is refused", Short(res, 300));

            // declined in a fresh repo: nothing may be executed
            string repo2 = NewRepo(work, brain, "d", out string tag2);
            WriteSampleApp(repo2);
            answer = false;
            SlimeToolManager.ProcessResponse("[RUN_CHECK: build]", tag2, repo2, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo2, tag2, "");
            Check(res.Contains("did not approve") && !Directory.Exists(Path.Combine(repo2, "App", "obj")), "when declined, nothing is executed", Short(res, 300));
            Check(res.Contains("do not try again"), "when declined, she is told not to retry");
            answer = true;

            // a repo shaped like slime-girl: .slnx + csproj + package.json
            string repo4 = NewRepo(work, brain, "e", out string tag4);
            Directory.CreateDirectory(Path.Combine(repo4, "Helper"));
            Directory.CreateDirectory(Path.Combine(repo4, "Cli"));
            Directory.CreateDirectory(Path.Combine(repo4, "web", "ext"));
            File.WriteAllText(Path.Combine(repo4, "Helper", "Helper.slnx"), "<Solution><Project Path=\"../Cli/Cli.csproj\" /></Solution>");
            File.WriteAllText(Path.Combine(repo4, "Helper", "Helper.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(repo4, "Cli", "Cli.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(repo4, "web", "ext", "package.json"), "{\"scripts\":{\"build\":\"echo b\",\"lint\":\"echo l\"}}");
            SlimeToolManager.ProcessResponse("[RUN_CHECK: build]", tag4, repo4, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo4, tag4, "");
            Check(res.Contains("several possible targets") && res.Contains("Helper/Helper.slnx") && res.Contains("web/ext/package.json")
                  && !res.Contains("[CHECK RESULT: dotnet") && !res.Contains("[CHECK RESULT: npm"),
                  "several build targets -> she is asked to pick one and nothing runs", Short(res, 500));

            if (NpmAvailable(out string npmWhy))
            {
                Console.WriteLine("\n== RUN_CHECK: npm scripts");
                string repo3 = NewRepo(work, brain, "n", out string tag3);
                File.WriteAllText(Path.Combine(repo3, "package.json"),
                    "{\"name\":\"x\",\"version\":\"1.0.0\",\"scripts\":{\"build\":\"node -e \\\"console.log('built ok')\\\"\",\"test\":\"node -e \\\"console.error('1 failing');process.exit(1)\\\"\"}}");
                SlimeToolManager.ProcessResponse("[RUN_CHECK: build][RUN_CHECK: test]", tag3, repo3, brain, out _, out checks);
                res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo3, tag3, "");
                Check(res.Contains("npm run build") && res.Contains("built ok") && res.Contains("SUCCEEDED"), "npm run build SUCCEEDED", Short(res, 500));
                Check(res.Contains("npm test") && res.Contains("FAILED") && res.Contains("1 failing"), "npm test FAILED with its output", Short(res, 800));
            }
            else
            {
                Console.WriteLine($"\n  SKIPPED: the npm checks could not run because npm could not be started: {npmWhy}");
            }
        }

        // ------------------------------------------------------------------ Tool execution specialist tests
        static void ToolExecutionSpecialistTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("\n== Tool Execution Specialist: subfolder paths, execution order, brace balance, whitespace tolerance");
            string root = NewRepo(work, brain, "t", out string tag);

            // 1. Subfolder path resolution fallback
            string subDir = Path.Combine(root, "src", "deep");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "SpecialService.cs"), "namespace Demo\n{\n    public class SpecialService\n    {\n        public int Value = 10;\n    }\n}\n");

            // Direct read using filename only
            Tool("[READ_FILE: SpecialService.cs]", tag, root, brain, out var r1, out _);
            Check(r1.Contains("public int Value = 10;"), "subfolder file is resolved and read using just filename", r1);

            // Direct edit using filename only
            string editResponse = Tool("[EDIT_FILE: SpecialService.cs]\n[FIND]\npublic int Value = 10;\n[/FIND]\n[REPLACE]\npublic int Value = 42;\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var e1, out _);
            Check(editResponse.Contains("I edited SpecialService.cs"), "subfolder file is edited using just filename", editResponse);
            Check(File.ReadAllText(Path.Combine(subDir, "SpecialService.cs")).Contains("Value = 42;"), "content in subfolder file was actually updated");

            // Ambiguous files report error with candidates
            string otherDir = Path.Combine(root, "other");
            Directory.CreateDirectory(otherDir);
            File.WriteAllText(Path.Combine(subDir, "Duplicate.cs"), "// deep");
            File.WriteAllText(Path.Combine(otherDir, "Duplicate.cs"), "// other");
            Tool("[READ_FILE: Duplicate.cs]", tag, root, brain, out var rAmb, out _);
            Check(rAmb.Contains("AMBIGUOUS FILE") && rAmb.Contains("src/deep/Duplicate.cs") && rAmb.Contains("other/Duplicate.cs"),
                  "multiple matching files in subfolders report ambiguity on READ_FILE", rAmb);

            Tool("[EDIT_FILE: Duplicate.cs]\n[FIND]\n// deep\n[/FIND]\n[REPLACE]\n// changed\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var eAmb, out _);
            Check(eAmb.Contains("multiple files named 'Duplicate.cs' were found"), "multiple matching files report ambiguity on EDIT_FILE", eAmb);

            // Guard against orphaned duplicate file in root when subfolder file exists
            string wDup = Tool("[WRITE_CODE: SpecialService.cs]\n// new in root\n[/WRITE_CODE]", tag, root, brain, out _, out _);
            Check(wDup.Contains("already exists at 'src/deep/SpecialService.cs'") && !File.Exists(Path.Combine(root, "SpecialService.cs")),
                  "WRITE_CODE does not create duplicate in root when file exists in subfolder", wDup);

            // Error message for missing file in TryEditFile
            Tool("[EDIT_FILE: TrulyMissing.cs]\n[FIND]\na\n[/FIND]\n[REPLACE]\nb\n[/REPLACE]\n[/EDIT_FILE]", tag, root, brain, out var eMiss, out _);
            Check(eMiss.Contains("the file was not found in the workspace (use SEARCH_FILES to find its location)") && !eMiss.Contains("create new files with WRITE_CODE"),
                  "missing file error in EDIT_FILE guides to SEARCH_FILES without mentioning WRITE_CODE", eMiss);

            // 2. Execution order in ProcessResponse: READ_FILE before EDIT_FILE in same response
            string freshFile = Path.Combine(root, "src", "Fresh.cs");
            File.WriteAllText(freshFile, "public class Fresh { public string Name = \"old\"; }\n");
            // Neither read nor edited yet. Run both in single response:
            string combo = "[READ_FILE: src/Fresh.cs]\n[EDIT_FILE: src/Fresh.cs]\n[FIND]\npublic string Name = \"old\";\n[/FIND]\n[REPLACE]\npublic string Name = \"new\";\n[/REPLACE]\n[/EDIT_FILE]";
            string comboResult = Tool(combo, tag, root, brain, out var comboFollow, out _);
            Check(comboResult.Contains("I edited src/Fresh.cs") && File.ReadAllText(freshFile).Contains("Name = \"new\";"),
                  "READ_FILE before EDIT_FILE in same response allows edit to succeed", comboResult);

            // 3. String- and comment-aware BraceBalance
            string braceFile = Path.Combine(root, "src", "Braces.cs");
            File.WriteAllText(braceFile, "public class Braces\n{\n    public void Run()\n    {\n    }\n}\n");
            Tool("[READ_FILE: src/Braces.cs]", tag, root, brain, out _, out _);
            string braceEdit = "[EDIT_FILE: src/Braces.cs]\n[FIND]\n    public void Run()\n    {\n    }\n[/FIND]\n[REPLACE]\n    public void Run()\n    {\n        // comment with { brace\n        /* multi-line { brace */\n        char c = '{';\n        string s = \"{ string }\";\n        string verbatim = @\"\\{ regex \\}\";\n        string raw = \"\"\" { raw } \"\"\";\n    }\n[/REPLACE]\n[/EDIT_FILE]";
            string braceRes = Tool(braceEdit, tag, root, brain, out var bFollow, out _);
            Check(braceRes.Contains("I edited src/Braces.cs") && File.ReadAllText(braceFile).Contains("raw"),
                  "braces in comments, chars, strings and raw literals do not trigger brace unbalance", braceRes + "\n" + bFollow);

            // 4. Whitespace tolerance in TryEditFile (tabs vs spaces and trailing whitespace)
            string wsFile = Path.Combine(root, "src", "Whitespace.cs");
            File.WriteAllText(wsFile, "\t\tpublic int Calculate()   \n\t\t{   \n\t\t\treturn 1;   \n\t\t}\n");
            Tool("[READ_FILE: src/Whitespace.cs]", tag, root, brain, out _, out _);
            // FIND has spaces and no trailing whitespace, REPLACE has spaces
            string wsEdit = "[EDIT_FILE: src/Whitespace.cs]\n[FIND]\n        public int Calculate()\n        {\n            return 1;\n        }\n[/FIND]\n[REPLACE]\n        public int Calculate()\n        {\n            return 2;\n        }\n[/REPLACE]\n[/EDIT_FILE]";
            string wsRes = Tool(wsEdit, tag, root, brain, out var wsFollow, out _);
            string updatedWs = File.ReadAllText(wsFile);
            Check(wsRes.Contains("I edited src/Whitespace.cs") && updatedWs.Contains("return 2;"),
                  "whitespace tolerance matches normalized lines and applies replacement", wsRes + "\n" + wsFollow);
            Check(updatedWs.StartsWith("\t\t"), "target indentation style (tabs) is preserved", updatedWs);

            // 5. Swedish intent / ChangeRequestRegex
            string[] swedishRequests =
            {
                "kan du lägga till en metod i klassen",
                "lägga in detta i filen",
                "gör en ny funktion",
                "kan du göra en hjälpklass",
                "skriva en ny testmetod",
                "ändra namnet på variabeln",
                "uppdatera konfigurationen",
                "implementera interfacet"
            };
            Check(swedishRequests.All(SlimeToolManager.IsChangeRequest),
                  "Swedish infinitive and imperative forms are recognised as change requests",
                  string.Join(" | ", swedishRequests.Where(x => !SlimeToolManager.IsChangeRequest(x))));
        }
    }
}