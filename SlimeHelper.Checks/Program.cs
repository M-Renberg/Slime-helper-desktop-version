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
                MemoryNetworkTests(brain, work, brainRoot);
                HistoryTests(brain, work, brainRoot);
                ClaimTests(brain, work);
                EditAndFileTests(brain, work, brainRoot);
                SearchAndHelperTests(brain, work);
                await RunCheckTests(brain, work);
                await TrailTests(brain, work);
                ToolExecutionSpecialistTests(brain, work);
                await GitHubTests(work);
                WatcherTests(work);
                TodoAndFocusTests(brain, work);
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

        // ------------------------------------------------------------------ memory bank & network
        static void MemoryNetworkTests(SlimeBrainManager brain, string work, string brainRoot)
        {
            Console.WriteLine("\n== Memory Bank: REMEMBER, CONNECT_MEMORY, RECALL_MEMORY, FORGET_MEMORY");

            string repo = Path.Combine(work, "memnet");
            Directory.CreateDirectory(repo);
            brain.BindRepo("nettest", repo);

            // 1. REMEMBER tool
            SlimeToolManager.ResetModificationCount();
            string remResp = "[REMEMBER: code_formatting | UserPreference | #csharp, #formatting]\nAlways format code using 4 spaces indentation.\n[/REMEMBER]";
            string ui = SlimeToolManager.ProcessResponse(remResp, "#nettest", repo, brain, out string follow, out _);
            Check(ui.Contains("I saved this memory in my memory bank: code_formatting"), "REMEMBER tool succeeds and returns status line", ui);
            Check(SlimeToolManager.ModificationCount == 1, "saving a structured memory increments ModificationCount");

            string bankFile = Path.Combine(brainRoot, "memory", "memory_bank.json");
            Check(File.Exists(bankFile), "memory_bank.json was written to brain memory folder");

            var mem = brain.LoadMemory();
            var node = mem.GetNode("code_formatting");
            Check(node != null && node.Category == MemoryCategory.UserPreference && node.Tags.Contains("#csharp"), "node is saved with correct category and tags");

            // 2. Second node and CONNECT_MEMORY tool
            string secondNode = "[REMEMBER: compiler_target | Architecture | #dotnet]\nTarget framework is .NET 10.\n[/REMEMBER]";
            SlimeToolManager.ProcessResponse(secondNode, "#nettest", repo, brain, out _, out _);

            string connectResp = "[CONNECT_MEMORY: code_formatting -> relates_to -> compiler_target]";
            ui = SlimeToolManager.ProcessResponse(connectResp, "#nettest", repo, brain, out _, out _);
            Check(ui.Contains("I linked memories: code_formatting -> relates_to -> compiler_target"), "CONNECT_MEMORY tool connects nodes", ui);

            mem = brain.LoadMemory();
            node = mem.GetNode("code_formatting");
            Check(node != null && node.Relations.Any(r => r.TargetKey == "compiler_target" && r.RelationType == "relates_to"), "relation edge exists in memory network");

            // 3. RECALL_MEMORY tool
            string recallResp = "[RECALL_MEMORY: csharp]";
            ui = SlimeToolManager.ProcessResponse(recallResp, "#nettest", repo, brain, out follow, out _);
            Check(ui.Contains("I recalled") && ui.Contains("related memories for 'csharp'"), "RECALL_MEMORY returns recall status line", ui);
            Check(follow.Contains("RECALLED MEMORIES") && follow.Contains("code_formatting") && follow.Contains("↳ relates_to -> compiler_target"), "RECALL_MEMORY populates followUpPrompt with network context", follow);

            // 4. Memory Network Context builder
            string context = brain.GetMemoryNetworkContext("What is the code style for C#?", "#nettest");
            Check(context.Contains("[Slime Memory Bank]") && context.Contains("code_formatting") && context.Contains("compiler_target"), "GetMemoryNetworkContext extracts relevant nodes and relations");

            // 5. Status line stripping
            string stripped = SlimeToolManager.RemoveStatusLines("*(I saved this memory in my memory bank: test! 🧠)*\n*(I linked memories: a -> b -> c! 🧠)*\nHello Master");
            Check(stripped == "Hello Master", "memory bank status lines are cleanly stripped by RemoveStatusLines", stripped);

            // 6. FORGET_MEMORY tool
            SlimeToolManager.ResetModificationCount();
            string forgetResp = "[FORGET_MEMORY: code_formatting]";
            ui = SlimeToolManager.ProcessResponse(forgetResp, "#nettest", repo, brain, out _, out _);
            Check(ui.Contains("I forgot memory 'code_formatting'"), "FORGET_MEMORY removes node", ui);
            Check(SlimeToolManager.ModificationCount == 1, "forgetting a memory increments ModificationCount");

            mem = brain.LoadMemory();
            Check(mem.GetNode("code_formatting") == null, "forgotten node no longer exists in memory bank");
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
            string outside = "[EDIT_FILE: src/Calc.cs]\n[FIND]\n    }\n}\n[/FIND]\n[REPLACE]\n    }\n\n    public int Out() { return 1; }\n}\n[/REPLACE]\n[/EDIT_FILE]";
            Tool(outside, tag, root, brain, out var fOut, out _);
            Check(fOut.Contains("outside of any class or struct"), "placing method outside class is rejected (CS1519 prevention)", fOut);
            Check(!File.ReadAllText(Path.Combine(root, "src", "Calc.cs")).Contains("Out()"), "the file is untouched after placing method outside class");
            Check(SlimeToolManager.CheckCSharpMethodPlacement("namespace Demo;\npublic class C {}\npublic void Bad() {}") != null, "file-scoped namespace bad method detected");
            Check(SlimeToolManager.CheckCSharpMethodPlacement("namespace Demo;\npublic class C {\npublic void Good() {}\n}") == null, "file-scoped namespace valid method allowed");
            Check(SlimeToolManager.CheckCSharpMethodPlacement("namespace Demo\n{\npublic class C {}\npublic int X = 1;\n}") != null, "bad field outside class detected");
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
            Check(res.Contains("Current file content on disk for App/Program.cs"), "failing build includes file content on disk", Short(res, 700));
            Check(res.Contains("Never claim that the code builds"), "the result tells her not to claim success");

            // fix and rebuild: trusted, so no new prompt
            SlimeToolManager.ProcessResponse("[READ_FILE: App/Program.cs]", tag, repo, brain, out _, out _);
            SlimeToolManager.ProcessResponse("[EDIT_FILE: App/Program.cs]\n[FIND]\nConsole.WriteLine(\"hi\")\n[/FIND]\n[REPLACE]\nConsole.WriteLine(\"hi\");\n[/REPLACE]\n[/EDIT_FILE]", tag, repo, brain, out var ef, out _);
            Check(ef == "", "the fix is applied", ef);
            SlimeToolManager.ProcessResponse("[RUN_CHECK: build]", tag, repo, brain, out _, out checks);
            res = await SlimeToolManager.RunCheckRequestsAsync(checks, repo, tag, "");
            Check(res.Contains("SUCCEEDED") && prompts == 1, "the fixed build SUCCEEDED, still only one prompt", Short(res, 500));
            Check(!res.Contains("FAILED"), "the fixed build result does not contain FAILED", Short(res, 500));
            SlimeToolManager.ResetModificationCount();
            Check(SlimeToolManager.WriteFailureCount == 0 && SlimeToolManager.ModificationCount == 0, "ResetModificationCount resets WriteFailureCount and ModificationCount");

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

        // ------------------------------------------------------------------ github assistant
        static async Task GitHubTests(string work)
        {
            Console.WriteLine("\n== GitHub Assistant: service and watcher");

            // URL parsing
            var httpsRes = GitHubService.ParseGitHubUrl("https://github.com/octocat/Hello-World.git");
            Check(httpsRes == ("octocat", "Hello-World"), "HTTPS url parsed with .git", $"{httpsRes}");

            var httpsNoGit = GitHubService.ParseGitHubUrl("https://github.com/octocat/Hello-World");
            Check(httpsNoGit == ("octocat", "Hello-World"), "HTTPS url parsed without .git", $"{httpsNoGit}");

            var sshRes = GitHubService.ParseGitHubUrl("git@github.com:octocat/Hello-World.git");
            Check(sshRes == ("octocat", "Hello-World"), "SSH url parsed with .git", $"{sshRes}");

            var sshProtoRes = GitHubService.ParseGitHubUrl("ssh://git@github.com/octocat/Hello-World.git");
            Check(sshProtoRes == ("octocat", "Hello-World"), "ssh:// protocol url parsed", $"{sshProtoRes}");

            var tokenRes = GitHubService.ParseGitHubUrl("https://ghp_secretToken@github.com/slime-org/slime-repo.git");
            Check(tokenRes == ("slime-org", "slime-repo"), "Token embedded url parsed", $"{tokenRes}");

            var nonGithub = GitHubService.ParseGitHubUrl("https://gitlab.com/octocat/Hello-World.git");
            Check(nonGithub == null, "Non-github url rejected");

            var invalidUrl = GitHubService.ParseGitHubUrl("not-a-url");
            Check(invalidUrl == null, "Invalid url rejected");

            // Local folder git config discovery
            string mockRepo = Path.Combine(work, "mock_gh_repo");
            string gitDir = Path.Combine(mockRepo, ".git");
            Directory.CreateDirectory(gitDir);
            string gitConfig = "[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = git@github.com:myorg/myrepo.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";
            File.WriteAllText(Path.Combine(gitDir, "config"), gitConfig);

            var discovered = GitHubService.GetRepoInfo(mockRepo);
            Check(discovered == ("myorg", "myrepo"), "Local .git/config parsed for owner and repo", $"{discovered}");

            string subDir = Path.Combine(mockRepo, "src", "sub");
            Directory.CreateDirectory(subDir);
            var subDiscovered = GitHubService.GetRepoInfo(subDir);
            Check(subDiscovered == ("myorg", "myrepo"), "Subfolder inside git repo discovers parent repo info", $"{subDiscovered}");

            // PR validation
            var svc = new GitHubService();
            var (sameBranchSuccess, sameBranchMsg, _) = await svc.CreatePullRequestAsync("myorg", "myrepo", "test pr", "main", "main");
            Check(!sameBranchSuccess && sameBranchMsg.Contains("cannot be the same"), "PR creation rejects identical head and base branches", sameBranchMsg);

            var (emptyTitleSuccess, emptyTitleMsg, _) = await svc.CreatePullRequestAsync("myorg", "myrepo", "", "feature", "main");
            Check(!emptyTitleSuccess && emptyTitleMsg.Contains("empty"), "PR creation rejects empty title", emptyTitleMsg);

            // Token resolution
            Check(GitHubService.ResolveToken("explicit_pat_token") == "explicit_pat_token", "ResolveToken prioritizes explicit token");

            // Model deserialization
            string runJson = "{\"id\":12345,\"name\":\"CI\",\"head_branch\":\"main\",\"head_sha\":\"abcdef123456\",\"status\":\"completed\",\"conclusion\":\"failure\",\"display_title\":\"Fix compiler error\",\"html_url\":\"https://github.com/myorg/myrepo/actions/runs/12345\"}";
            var run = System.Text.Json.JsonSerializer.Deserialize<GitHubWorkflowRun>(runJson);
            Check(run != null && run.Id == 12345 && run.Conclusion == "failure" && run.DisplayTitle == "Fix compiler error", "GitHubWorkflowRun deserializes correctly", $"{run?.DisplayTitle}");

            string jobJson = "{\"id\":99887,\"run_id\":12345,\"name\":\"build-and-test\",\"status\":\"completed\",\"conclusion\":\"failure\"}";
            var job = System.Text.Json.JsonSerializer.Deserialize<GitHubJob>(jobJson);
            Check(job != null && job.Id == 99887 && job.Conclusion == "failure", "GitHubJob deserializes correctly", $"{job?.Name}");

            // Watcher lifecycle and repo setting
            using (var watcher = new GitHubWatcher(svc))
            {
                bool failureFired = false;
                bool successFired = false;
                watcher.OnWorkflowFailed += (r, log) => failureFired = true;
                watcher.OnWorkflowSucceeded += (r) => successFired = true;

                Check(!failureFired && !successFired, "Watcher event handlers wired correctly");

                watcher.SetActiveRepo(mockRepo);
                Check(watcher.CurrentRepo == ("myorg", "myrepo"), "GitHubWatcher parses active repo", $"{watcher.CurrentRepo}");

                // CheckNow on mock repo without crashing
                await watcher.CheckNowAsync();
                Check(true, "GitHubWatcher CheckNowAsync executes safely without throwing");
            }
            Check(true, "GitHubWatcher disposes cleanly");

            // Edge cases testing
            Console.WriteLine("\n== GitHub Assistant: edge cases");

            // 1. Missing or non-existent repo directory
            Check(GitHubService.GetRepoInfo(Path.Combine(work, "ghost_folder")) == null, "Non-existent directory returns null repo info");
            string plainFolder = Path.Combine(work, "plain_folder");
            Directory.CreateDirectory(plainFolder);
            Check(GitHubService.GetRepoInfo(plainFolder) == null, "Folder without .git returns null repo info");

            // 2. Repo with no remotes
            string noRemoteRepo = Path.Combine(work, "no_remote_repo");
            Directory.CreateDirectory(Path.Combine(noRemoteRepo, ".git"));
            File.WriteAllText(Path.Combine(noRemoteRepo, ".git", "config"), "[core]\n\trepositoryformatversion = 0\n");
            Check(GitHubService.GetRepoInfo(noRemoteRepo) == null, "Repo without remotes returns null repo info");

            // 3. Repo with non-GitHub remote (e.g., GitLab)
            string gitlabRepo = Path.Combine(work, "gitlab_repo");
            Directory.CreateDirectory(Path.Combine(gitlabRepo, ".git"));
            File.WriteAllText(Path.Combine(gitlabRepo, ".git", "config"), "[remote \"origin\"]\n\turl = git@gitlab.com:group/project.git\n");
            Check(GitHubService.GetRepoInfo(gitlabRepo) == null, "Repo with GitLab remote returns null repo info");

            // 4. Missing/invalid token handling
            var (missingTokenSuccess, missingTokenMsg, _) = await svc.CreatePullRequestAsync("myorg", "myrepo", "feature", "head-branch", "main", token: "");
            Check(!missingTokenSuccess && (missingTokenMsg.Contains("token") || missingTokenMsg.Contains("No GitHub token")),
                  "PR creation with missing token fails gracefully", missingTokenMsg);

            var (emptyHeadSuccess, emptyHeadMsg, _) = await svc.CreatePullRequestAsync("myorg", "myrepo", "feature", "", "main", token: "fake_token");
            Check(!emptyHeadSuccess && emptyHeadMsg.Contains("empty"), "PR creation with empty head branch is rejected", emptyHeadMsg);

            // 5. Watcher with no repo or non-GitHub repo
            using (var unconfiguredWatcher = new GitHubWatcher(svc))
            {
                unconfiguredWatcher.SetActiveRepo(plainFolder);
                Check(unconfiguredWatcher.CurrentRepo == null, "Watcher ignores folder without GitHub remote");
                await unconfiguredWatcher.CheckNowAsync();
                Check(true, "Watcher CheckNowAsync with unconfigured repo does not crash");
            }

            // 6. Graceful handling of empty or minimal workflow JSON responses
            string emptyRunJson = "{\"id\":0,\"name\":\"\",\"status\":\"\"}";
            var emptyRun = System.Text.Json.JsonSerializer.Deserialize<GitHubWorkflowRun>(emptyRunJson);
            Check(emptyRun != null && emptyRun.Id == 0 && emptyRun.Conclusion == null, "Minimal GitHubWorkflowRun deserializes safely");

            string emptyJobJson = "{\"id\":0,\"run_id\":0,\"name\":\"\",\"status\":\"\"}";
            var emptyJob = System.Text.Json.JsonSerializer.Deserialize<GitHubJob>(emptyJobJson);
            Check(emptyJob != null && emptyJob.Steps.Count == 0, "Minimal GitHubJob has initialized empty steps list");
        }

        // ------------------------------------------------------------------ watchers (ActivityWatcher & GlobalWordWatcher)
        static void WatcherTests(string work)
        {
            Console.WriteLine("\n== Watchers: GlobalWordWatcher and ActivityWatcher fixes");

            // 1. KeyCodeToChar digits and symbols
            Check(GlobalWordWatcher.KeyCodeToChar(49, false) == '1', "KeyCodeToChar maps '1'");
            Check(GlobalWordWatcher.KeyCodeToChar(49, true) == '!', "KeyCodeToChar maps Shift+1 to '!'");
            Check(GlobalWordWatcher.KeyCodeToChar(56, true) == '(', "KeyCodeToChar maps Shift+8 to '('");
            Check(GlobalWordWatcher.KeyCodeToChar(57, true) == ')', "KeyCodeToChar maps Shift+9 to ')'");
            Check(GlobalWordWatcher.KeyCodeToChar(186, false) == ';', "KeyCodeToChar maps ';' on OEM_1");
            Check(GlobalWordWatcher.KeyCodeToChar(188, true) == ';', "KeyCodeToChar maps Nordic Shift+, to ';'");
            Check(GlobalWordWatcher.KeyCodeToChar(190, true) == ':', "KeyCodeToChar maps Nordic Shift+. to ':'");

            // 2. MatchTrigger for multi-word and punctuation triggers that previously failed
            var nullTrigger = GlobalWordWatcher.MatchTrigger("null;", "return null;");
            Check(nullTrigger.HasValue && nullTrigger.Value.Status == "PUSH", "return null triggers PUSH (not missing PUSH_NEEDED)", $"{nullTrigger}");

            var loopTrigger = GlobalWordWatcher.MatchTrigger("while(true)", "while(true)");
            Check(loopTrigger.HasValue && loopTrigger.Value.Status == "ERROR", "while(true) triggers ERROR", $"{loopTrigger}");

            var forTrigger = GlobalWordWatcher.MatchTrigger(";", "for(;;)");
            Check(forTrigger.HasValue && forTrigger.Value.Status == "ERROR", "for(;;) triggers ERROR", $"{forTrigger}");

            var impTrigger = GlobalWordWatcher.MatchTrigger("!important", "color: red !important");
            Check(impTrigger.HasValue && impTrigger.Value.Status == "ANNOYED", "!important triggers ANNOYED", $"{impTrigger}");

            var leetTrigger = GlobalWordWatcher.MatchTrigger("1337", "1337");
            Check(leetTrigger.HasValue && leetTrigger.Value.Status == "STREAK", "1337 numeric word triggers STREAK", $"{leetTrigger}");

            var ipTrigger = GlobalWordWatcher.MatchTrigger("127.0.0.1", "http://127.0.0.1");
            Check(ipTrigger.HasValue && ipTrigger.Value.Status == "FUNNY", "127.0.0.1 triggers FUNNY", $"{ipTrigger}");

            var dropTrigger = GlobalWordWatcher.MatchTrigger("table", "drop table users");
            Check(dropTrigger.HasValue && dropTrigger.Value.Status == "POKE", "drop table multi-word phrase triggers POKE", $"{dropTrigger}");

            var selectTrigger = GlobalWordWatcher.MatchTrigger("*", "select * from");
            Check(selectTrigger.HasValue && selectTrigger.Value.Status == "STREAK", "select * multi-word phrase triggers STREAK", $"{selectTrigger}");

            // 3. SlimeResponses PickIdleChatter time-of-day reactions
            var lateNight = new DateTime(2026, 11, 16, 23, 30, 0);
            bool sawTiredLateNight = false;
            var testRng = new Random(42);
            for (int i = 0; i < 25; i++)
            {
                var (status, msg) = SlimeResponses.PickIdleChatter(lateNight, testRng);
                if (status == "TIRED" && msg.Contains("late"))
                {
                    sawTiredLateNight = true;
                    break;
                }
            }
            Check(sawTiredLateNight, "PickIdleChatter surfaces late-night TIRED reaction");

            // 4. Phase 1: Poke-Combo escalation and emote particles
            var poke1 = SlimeResponses.GetPokeComboReaction(1, "Default", testRng);
            Check(poke1.Status == "POKE" && poke1.Emote == "💧", "Poke streak 1 returns POKE and water droplet emote", $"{poke1}");

            var pokePink = SlimeResponses.GetPokeComboReaction(2, "Pink", testRng);
            Check(pokePink.Status == "POKE" && pokePink.Emote == "💖", "Poke streak 2 on Pink skin returns POKE and heart emote", $"{pokePink}");

            var poke3 = SlimeResponses.GetPokeComboReaction(3, "Girl", testRng);
            Check(poke3.Status == "ANNOYED" && poke3.Emote == "💢" && poke3.Impact > poke1.Impact, "Poke streak 3 escalates to ANNOYED with anger emote", $"{poke3}");

            var poke5 = SlimeResponses.GetPokeComboReaction(5, "Default", testRng);
            Check(poke5.Status == "SCARED" && poke5.Emote == "💫" && poke5.Impact > poke3.Impact, "Poke streak 5 escalates to SCARED with spinning dizzy emote", $"{poke5}");

            // 5. Phase 2: Contextual awareness (Active Window & High CPU load) + No emojis in spoken text
            Check(SlimeResponses.ClassifyActiveWindow("Code", "MainWindow.xaml.cs - Visual Studio Code") == "CODING", "VS Code classified as CODING");
            Check(SlimeResponses.ClassifyActiveWindow("devenv", "slime-girl - Microsoft Visual Studio") == "CODING", "Visual Studio classified as CODING");
            Check(SlimeResponses.ClassifyActiveWindow("Spotify", "Spotify Premium") == "MUSIC", "Spotify classified as MUSIC");
            Check(SlimeResponses.ClassifyActiveWindow("chrome", "YouTube Music - Google Chrome") == "MUSIC", "YouTube Music classified as MUSIC (not distraction)");
            Check(SlimeResponses.ClassifyActiveWindow("chrome", "Funny Cat Video - YouTube - Google Chrome") == "DISTRACTION", "YouTube video classified as DISTRACTION");
            Check(SlimeResponses.ClassifyActiveWindow("chrome", "r/programming - Reddit") == "DISTRACTION", "Reddit classified as DISTRACTION");
            Check(SlimeResponses.ClassifyActiveWindow("SlimeHelper", "SlimeHelper") == null, "SlimeHelper itself ignored by window classifier");

            // Expanded Phase 2 contexts (Gmail, Teams, Google Meet, Postman, Thunder Client, Vercel, GitHub, GitHub Actions, Bash, Terminal, Discord, Slack, DBeaver, MySQL, Browsers)
            Check(SlimeResponses.ClassifyActiveWindow("chrome", "Inbox (3) - Gmail - Google Chrome") == "EMAIL", "Gmail classified as EMAIL");
            Check(SlimeResponses.ClassifyActiveWindow("ms-teams", "Sprint Planning | Microsoft Teams") == "MEETING", "Teams classified as MEETING");
            Check(SlimeResponses.ClassifyActiveWindow("chrome", "Google Meet - abc-defg-hij") == "MEETING", "Google Meet classified as MEETING");
            Check(SlimeResponses.ClassifyActiveWindow("Postman", "POST /api/login - Postman") == "API_TESTING", "Postman classified as API_TESTING");
            Check(SlimeResponses.ClassifyActiveWindow("Code", "Thunder Client - Visual Studio Code") == "API_TESTING", "Thunder Client inside VS Code classified as API_TESTING");
            Check(SlimeResponses.ClassifyActiveWindow("dbeaver", "DBeaver 24.1 - localhost") == "DATABASE", "DBeaver classified as DATABASE");
            Check(SlimeResponses.ClassifyActiveWindow("mysqlworkbench", "MySQL Workbench") == "DATABASE", "MySQL Workbench classified as DATABASE");
            Check(SlimeResponses.ClassifyActiveWindow("brave", "Actions · myorg/myrepo · GitHub - Brave") == "GITHUB_ACTIONS", "GitHub Actions classified as GITHUB_ACTIONS");
            Check(SlimeResponses.ClassifyActiveWindow("firefox", "Pull requests · myorg/myrepo · GitHub - Mozilla Firefox") == "GITHUB", "GitHub PR page classified as GITHUB");
            Check(SlimeResponses.ClassifyActiveWindow("msedge", "Dashboard – Vercel - Microsoft Edge") == "DEPLOY_CLOUD", "Vercel classified as DEPLOY_CLOUD");
            Check(SlimeResponses.ClassifyActiveWindow("bash", "MINGW64:/c/Users/Mikae/repos") == "TERMINAL", "Bash classified as TERMINAL");
            Check(SlimeResponses.ClassifyActiveWindow("WindowsTerminal", "Windows PowerShell") == "TERMINAL", "Windows Terminal classified as TERMINAL");
            Check(SlimeResponses.ClassifyActiveWindow("Discord", "#general | Discord") == "CHAT_COMMS", "Discord classified as CHAT_COMMS");
            Check(SlimeResponses.ClassifyActiveWindow("slack", "Slack | dev-team") == "CHAT_COMMS", "Slack classified as CHAT_COMMS");
            Check(SlimeResponses.ClassifyActiveWindow("chrome", "MDN Web Docs - Google Chrome") == "BROWSER", "Generic Chrome docs tab classified as BROWSER");
            Check(SlimeResponses.ClassifyActiveWindow("firefox", "Stack Overflow - Mozilla Firefox") == "BROWSER", "Generic Firefox tab classified as BROWSER");
            Check(SlimeResponses.ClassifyActiveWindow("brave", "Localhost:3000 - Brave") == "BROWSER", "Generic Brave tab classified as BROWSER");
            Check(SlimeResponses.ClassifyActiveWindow("msedge", "Learn .NET - Microsoft Edge") == "BROWSER", "Generic Edge tab classified as BROWSER");

            var codingReaction = SlimeResponses.GetContextReaction("CODING", testRng);
            var musicReaction = SlimeResponses.GetContextReaction("MUSIC", testRng);
            var distractionReaction = SlimeResponses.GetContextReaction("DISTRACTION", testRng);
            var cpuReaction = SlimeResponses.GetContextReaction("HIGH_CPU", testRng);
            var meetingReaction = SlimeResponses.GetContextReaction("MEETING", testRng);
            var emailReaction = SlimeResponses.GetContextReaction("EMAIL", testRng);
            var chatReaction = SlimeResponses.GetContextReaction("CHAT_COMMS", testRng);
            var apiReaction = SlimeResponses.GetContextReaction("API_TESTING", testRng);
            var dbReaction = SlimeResponses.GetContextReaction("DATABASE", testRng);
            var ghActionsReaction = SlimeResponses.GetContextReaction("GITHUB_ACTIONS", testRng);
            var ghReaction = SlimeResponses.GetContextReaction("GITHUB", testRng);
            var deployReaction = SlimeResponses.GetContextReaction("DEPLOY_CLOUD", testRng);
            var termReaction = SlimeResponses.GetContextReaction("TERMINAL", testRng);
            var browserReaction = SlimeResponses.GetContextReaction("BROWSER", testRng);

            Check(codingReaction.HasValue && codingReaction.Value.Status == "NOTES", "CODING context returns NOTES reaction", $"{codingReaction}");
            Check(musicReaction.HasValue && musicReaction.Value.Status == "CUTE", "MUSIC context returns CUTE reaction", $"{musicReaction}");
            Check(distractionReaction.HasValue && distractionReaction.Value.Status == "FUNNY", "DISTRACTION context returns FUNNY reaction", $"{distractionReaction}");
            Check(cpuReaction.HasValue && cpuReaction.Value.Status == "TIRED", "HIGH_CPU context returns TIRED reaction", $"{cpuReaction}");
            Check(meetingReaction.HasValue && meetingReaction.Value.Status == "CUTE", "MEETING context returns CUTE reaction", $"{meetingReaction}");
            Check(emailReaction.HasValue && emailReaction.Value.Status == "NOTES", "EMAIL context returns NOTES reaction", $"{emailReaction}");
            Check(chatReaction.HasValue && chatReaction.Value.Status == "FUNNY", "CHAT_COMMS context returns FUNNY reaction", $"{chatReaction}");
            Check(apiReaction.HasValue && apiReaction.Value.Status == "THINKING", "API_TESTING context returns THINKING reaction", $"{apiReaction}");
            Check(dbReaction.HasValue && dbReaction.Value.Status == "WARNING", "DATABASE context returns WARNING reaction", $"{dbReaction}");
            Check(ghActionsReaction.HasValue && ghActionsReaction.Value.Status == "STREAK", "GITHUB_ACTIONS context returns STREAK reaction", $"{ghActionsReaction}");
            Check(ghReaction.HasValue && ghReaction.Value.Status == "NOTES", "GITHUB context returns NOTES reaction", $"{ghReaction}");
            Check(deployReaction.HasValue && deployReaction.Value.Status == "HURRAY", "DEPLOY_CLOUD context returns HURRAY reaction", $"{deployReaction}");
            Check(termReaction.HasValue && termReaction.Value.Status == "STREAK", "TERMINAL context returns STREAK reaction", $"{termReaction}");
            Check(browserReaction.HasValue && browserReaction.Value.Status == "THINKING", "BROWSER context returns THINKING reaction", $"{browserReaction}");

            static bool HasNoSurrogateEmojis(IEnumerable<string> phrases) =>
                phrases.All(p => !p.Any(char.IsSurrogate));

            Check(HasNoSurrogateEmojis(SlimeResponses.PokeDizzyResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.WindowShakeResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.CodingWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.MusicWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.DistractionWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.HighCpuResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.MeetingWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.EmailWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.ChatCommsWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.ApiTestingWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.DatabaseWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.GitHubActionsWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.GitHubWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.DeployCloudWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.TerminalWindowResponses) &&
                  HasNoSurrogateEmojis(SlimeResponses.BrowserWindowResponses),
                  "Spoken response arrays contain no emojis");
        }

        // ------------------------------------------------------------------ Phase 3: Unified Markdown To-Do (- [ ])
        static void TodoAndFocusTests(SlimeBrainManager brain, string work)
        {
            Console.WriteLine("\n== Phase 3: Unified Markdown To-Do (- [ ]) and SlimeBrain resolution");

            string generalFolder = brain.ResolveTodoFolder("General");
            Check(generalFolder.EndsWith(Path.Combine("brain", "Notes"), StringComparison.OrdinalIgnoreCase),
                  "General thread resolves To-Do folder to SlimeBrain/Notes (never bin/Debug)", generalFolder);

            string todoRepo = NewRepo(work, brain, "todorepo", out string todoTag);
            File.WriteAllText(Path.Combine(todoRepo, ".gitignore"), "bin/\nobj/\n");

            string resolvedRepo = brain.ResolveTodoFolder(todoTag);
            Check(string.Equals(resolvedRepo, todoRepo, StringComparison.OrdinalIgnoreCase),
                  "Bound repo thread resolves To-Do folder to repository root", resolvedRepo);

            // Add tasks in repo and General
            string repoNotesFile = NotesManager.AddTodo(resolvedRepo, "Fix authentication bug");
            NotesManager.AddTodo(resolvedRepo, "add Write integration tests");
            NotesManager.AddTodo(generalFolder, "Buy coffee beans");

            Check(File.Exists(repoNotesFile), "AddTodo creates slime_notes.md in target folder");
            Check(File.ReadAllText(Path.Combine(todoRepo, ".gitignore")).Contains("slime_notes.md"),
                  "AddTodo ensures slime_notes.md is added to .gitignore");

            string[] scanFolders = brain.GetTodoScanFolders(todoTag);
            var openTodos = NotesManager.ListUnfinishedTodos(scanFolders);

            Check(openTodos.Count == 3, "ListUnfinishedTodos finds repo + General tasks and ignores placeholders", $"count={openTodos.Count}");
            Check(openTodos[0].Index == 1 && openTodos[0].Text == "Fix authentication bug", "Task #1 matches first repo task", openTodos[0].Text);
            Check(openTodos[1].Index == 2 && openTodos[1].Text == "Write integration tests", "Task #2 strips leading 'add ' prefix", openTodos[1].Text);
            Check(openTodos[2].Index == 3 && openTodos[2].Text == "Buy coffee beans", "Task #3 includes General SlimeBrain task", openTodos[2].Text);

            // Complete task #1 (- [ ] -> - [x])
            var (doneOk, completedText, doneErr) = NotesManager.CompleteTodo(1, scanFolders);
            Check(doneOk && completedText == "Fix authentication bug", "CompleteTodo marks task #1 as done", doneErr);
            Check(File.ReadAllText(repoNotesFile).Contains("- [x] Fix authentication bug"),
                  "CompleteTodo rewrites '- [ ]' to '- [x]' in slime_notes.md");

            var remainingTodos = NotesManager.ListUnfinishedTodos(scanFolders);
            Check(remainingTodos.Count == 2 && remainingTodos[0].Text == "Write integration tests",
                  "Completed task is excluded from unfinished list and remaining items re-index cleanly");

            var obsidianList = ObsidianService.GetUnfinishedTodos("", 5, scanFolders);
            Check(obsidianList.Count == 2 && obsidianList.Contains("Buy coffee beans"),
                  "ObsidianService.GetUnfinishedTodos reads unified scan folders even when ObsidianVaultPath is empty");

            // Phase 4: Contextual & Memory-based Idle Chatter
            Console.WriteLine("\n== Phase 4: Memory-based and contextual IdleThoughts");
            var midDay = new DateTime(2026, 11, 18, 14, 0, 0); // Wednesday afternoon
            var rng4 = new Random(7);

            bool sawFocusChatter = false;
            for (int i = 0; i < 20; i++)
            {
                var (st, m) = SlimeResponses.PickContextualIdleChatter(
                    midDay, rng4,
                    focusStatus: (true, 18, "Refactor Auth"));
                if (st == "NOTES" && m.Contains("Refactor Auth") && m.Contains("18 min"))
                {
                    sawFocusChatter = true;
                    break;
                }
            }
            Check(sawFocusChatter, "PickContextualIdleChatter surfaces active Focus timer reminder");

            bool sawTodoChatter = false;
            for (int i = 0; i < 30; i++)
            {
                var (st, m) = SlimeResponses.PickContextualIdleChatter(
                    midDay, rng4,
                    openTodos: new[] { "Write integration tests" });
                if (st == "NOTES" && m.Contains("Write integration tests") && !m.Any(char.IsSurrogate))
                {
                    sawTodoChatter = true;
                    break;
                }
            }
            Check(sawTodoChatter, "PickContextualIdleChatter surfaces open To-Do reminder without emojis");

            bool sawMemoryChatter = false;
            var sampleNodes = new List<MemoryNode>
            {
                new MemoryNode
                {
                    Key = "wpf_arch",
                    Category = MemoryCategory.Architecture,
                    Content = "Use 60 FPS spring physics in UpdateMotion",
                    RelatedRepoOrTag = "#slime-girl"
                }
            };
            for (int i = 0; i < 30; i++)
            {
                var (st, m) = SlimeResponses.PickContextualIdleChatter(
                    midDay, rng4,
                    memoryNodes: sampleNodes);
                if (st == "THINKING" && m.Contains("#slime-girl") && m.Contains("UpdateMotion") && !m.Any(char.IsSurrogate))
                {
                    sawMemoryChatter = true;
                    break;
                }
            }
            Check(sawMemoryChatter, "PickContextualIdleChatter surfaces SlimeMemory node thoughts without emojis");

            bool sawRepoChatter = false;
            for (int i = 0; i < 40; i++)
            {
                var (st, m) = SlimeResponses.PickContextualIdleChatter(
                    midDay, rng4,
                    boundRepoTags: new[] { "#slime-girl" });
                if (st == "CUTE" && m.Contains("#slime-girl") && !m.Any(char.IsSurrogate))
                {
                    sawRepoChatter = true;
                    break;
                }
            }
            Check(sawRepoChatter, "PickContextualIdleChatter surfaces bound repository check-in without emojis");

            // Phase 5: Mood & Energy System (Tamagotchi feel)
            Console.WriteLine("\n== Phase 5: Mood and Energy system");
            var moodMem = new SlimeMemory();
            Check(moodMem.Mood == 80 && moodMem.Energy == 85 && moodMem.GetMoodLabel() == "Happy", "Default SlimeMemory starts Happy (80% Mood, 85% Energy)");
            Check(moodMem.GetBreathingSpeedMultiplier() > 1.0, "High mood & energy increases breathing speed multiplier");

            moodMem.ApplyEvent("TODO_DONE");
            Check(moodMem.Mood == 90 && moodMem.Energy == 89, "TODO_DONE boosts Mood and Energy", moodMem.GetStatusSummary());

            moodMem.ApplyEvent("CI_SUCCESS");
            Check(moodMem.Mood == 100 && moodMem.Energy == 94, "CI_SUCCESS boosts Mood and clamps at 100", moodMem.GetStatusSummary());

            for (int i = 0; i < 8; i++) moodMem.ApplyEvent("POKE_SPAM");
            Check(moodMem.Mood == 36 && moodMem.GetMoodLabel() == "Grumpy", "Repeated POKE_SPAM lowers Mood to Grumpy", moodMem.GetStatusSummary());

            moodMem.ApplyEvent("CI_FAILURE");
            Check(moodMem.Mood == 26 && moodMem.GetMoodLabel() == "Stressed", "CI_FAILURE drops Mood into Stressed state", moodMem.GetStatusSummary());

            moodMem.AdjustMoodAndEnergy(0, -40);
            Check(moodMem.Energy < 25 && moodMem.GetMoodLabel() == "Sleepy" && moodMem.GetBreathingSpeedMultiplier() < 1.0,
                  "Low Energy (< 25) enters Sleepy state and slows breathing speed multiplier", moodMem.GetStatusSummary());

            moodMem.ApplyEvent("REST");
            Check(moodMem.Energy >= 25, "REST event restores Energy", moodMem.GetStatusSummary());

            PaperDollTests(work);
        }

        // ------------------------------------------------------------------ Paper-Doll layer resolver & Girl assets
        static void PaperDollTests(string work)
        {
            Console.WriteLine("\n== Paper-Doll resolver & Girl skin layers");

            // 1. Natural numeric ordering (1..10) so frame 10 doesn't sort before frame 2
            var unordered = new[] { "frame-10.png", "frame-2.png", "frame-1.png", "frame-3.png" };
            var ordered = PaperDollResolver.OrderPathsNaturally(unordered);
            Check(ordered.SequenceEqual(new[] { "frame-1.png", "frame-2.png", "frame-3.png", "frame-10.png" }),
                  "OrderPathsNaturally sorts numeric suffixes 1, 2, 3, 10", string.Join(", ", ordered));

            // 2. Synthetic edge-case directory: moth-closed, Background Merged, warning-1..2 vs drops-1, slimemotion
            string synthState = Path.Combine(work, "PaperDollSynth");
            string synthLayers = Path.Combine(synthState, "Layers");
            Directory.CreateDirectory(synthLayers);
            foreach (var f in new[] { "Background Merged.png", "eyes-open.png", "moth-closed.png", "warning-1.png", "warning-2.png", "drops-1.png", "slimemotion-1-2.png", "slimemotion-1-1.png", "slimemotion-2-1.png" })
                File.WriteAllBytes(Path.Combine(synthLayers, f), Array.Empty<byte>());

            var synth = PaperDollResolver.ResolveLayers(synthState);
            Check(synth != null && Path.GetFileName(synth.Base) == "Background Merged.png", "Resolves Background Merged.png as Base");
            Check(synth != null && Path.GetFileName(synth.Eyes) == "eyes-open.png", "Resolves eyes-open.png as Eyes");
            Check(synth != null && Path.GetFileName(synth.MouthClosed) == "moth-closed.png", "Resolves moth-closed.png alias as MouthClosed");
            Check(synth != null && synth.Overlay != null && Path.GetFileName(synth.Overlay) == "drops-1.png", "Resolves single-frame numbered effect drops-1.png as static Overlay");
            Check(synth != null && synth.OverlayFrames.Select(Path.GetFileName).SequenceEqual(new[] { "warning-1.png", "warning-2.png" }),
                  "Resolves multi-frame warning-1..2 as ordered OverlayFrames");
            Check(synth != null && synth.MotionGroups.Count == 2
                  && synth.MotionGroups[0].Select(Path.GetFileName).SequenceEqual(new[] { "slimemotion-1-1.png", "slimemotion-1-2.png" })
                  && synth.MotionGroups[1].Select(Path.GetFileName).SequenceEqual(new[] { "slimemotion-2-1.png" }),
                  "Resolves slimemotion groups & frame order numerically");

            // 3. Verify all 18 Girl emote Layers folders if repo root is discoverable
            string? repoRoot = FindRepoRoot();
            if (repoRoot != null)
            {
                string girlDir = Path.Combine(repoRoot, "SlimeHelper", "Images", "Girl");
                var layerEmotes = Directory.GetDirectories(girlDir)
                    .Where(d => Directory.Exists(Path.Combine(d, "Layers")))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Check(layerEmotes.Count == 18, $"Found all 18 Girl Paper-Doll emote Layers folders (found {layerEmotes.Count})");

                foreach (var emoteDir in layerEmotes)
                {
                    string name = Path.GetFileName(emoteDir);
                    var resolved = PaperDollResolver.ResolveLayers(emoteDir);
                    Check(resolved != null && !string.IsNullOrEmpty(resolved.Base), $"Girl/{name}/Layers resolves Base");
                }

                var sleep = PaperDollResolver.ResolveLayers(Path.Combine(girlDir, "SLEEP"));
                Check(sleep != null && sleep.MotionGroups.Count == 3 && sleep.MotionGroups.All(g => g.Count == 2),
                      "Girl/SLEEP has 3 slimemotion groups with 2 frames each");

                var scared = PaperDollResolver.ResolveLayers(Path.Combine(girlDir, "SCARED"));
                Check(scared != null && scared.Eyes != null && scared.MouthClosed != null && scared.MouthOpen != null,
                      "Girl/SCARED resolves eyes-open, mouth-closed, and mouth-open");
            }
        }

        static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "slime-girl.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}