using System.IO;
using System.Text.Json;

namespace SlimeHelper
{
    public class SlimeMemoryMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class SlimeBrainManager
    {
        private string _brainRootPath = "";
        private string _userFolder = "";
        private string _reposFolder = "";
        private string _memoryFolder = "";
        private string _subjectsFolder = ""; // FIX 1: Lade till variabeln här!
        private string _profilePath = "";
        private string _historyPath = "";
        private string _threadSummariesPath = "";
        private string _memoryBankPath = "";

        public const int MaxHistoryMessagesPerThread = 24;

        private Dictionary<string, List<SlimeMemoryMessage>> _chatHistory = new();
        private Dictionary<string, string> _threadSummaries = new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // Tål kommentarer och trailing commas (gamla repo_map.json hade en //-kommentar)
        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private string RepoMapPath => Path.Combine(_reposFolder, "repo_map.json");

        // Används av testerna (SlimeHelper.Checks): lägger hela hjärnan i en egen mapp så att
        // tester aldrig rör den riktiga hjärnan eller Obsidian-valvet.
        private readonly string? _brainRootOverride;

        public SlimeBrainManager(string? brainRootOverride = null)
        {
            _brainRootOverride = brainRootOverride;

            string obsidianPath = "";
            if (_brainRootOverride == null)
            {
                string settingsPath = SlimeSettings.ResolveSettingsPath();
                try
                {
                    if (File.Exists(settingsPath))
                    {
                        var jsonDoc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                        if (jsonDoc.RootElement.TryGetProperty("ObsidianVaultPath", out var obsProp))
                        {
                            obsidianPath = obsProp.GetString() ?? "";
                        }
                    }
                }
                catch { }
            }

            SetupPaths(obsidianPath);
            InitializeBrain();
            LoadHistory();
        }

        public void RelocateToObsidian(string vaultPath)
        {
            // Ta med bindningarna från den gamla hjärnan, annars "försvinner" alla repos vid flytt
            var oldMap = LoadRepoMap();

            SetupPaths(vaultPath);
            InitializeBrain();

            if (oldMap.Count > 0)
            {
                var newMap = LoadRepoMap();
                foreach (var kv in oldMap)
                {
                    if (!newMap.ContainsKey(kv.Key)) newMap[kv.Key] = kv.Value;
                }
                SaveRepoMap(newMap);
            }

            LoadHistory();
        }

        private void SetupPaths(string obsidianVaultPath)
        {
            if (_brainRootOverride != null)
            {
                _brainRootPath = _brainRootOverride;
            }
            else if (!string.IsNullOrWhiteSpace(obsidianVaultPath) && Directory.Exists(obsidianVaultPath))
            {
                _brainRootPath = Path.Combine(obsidianVaultPath, "SlimeBrain");
            }
            else
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _brainRootPath = Path.Combine(baseDir, "SlimeHelper", "SlimeBrain");
            }

            _userFolder = Path.Combine(_brainRootPath, "user");
            _reposFolder = Path.Combine(_brainRootPath, "repos");
            _memoryFolder = Path.Combine(_brainRootPath, "memory");
            _subjectsFolder = Path.Combine(_brainRootPath, "Subjects");

            _profilePath = Path.Combine(_userFolder, "profile.json");
            _historyPath = Path.Combine(_memoryFolder, "chat_history.json");
            _threadSummariesPath = Path.Combine(_memoryFolder, "thread_summaries.json");
            _memoryBankPath = Path.Combine(_memoryFolder, "memory_bank.json");
        }

        private void InitializeBrain()
        {
            Directory.CreateDirectory(_userFolder);
            Directory.CreateDirectory(_reposFolder);
            Directory.CreateDirectory(_memoryFolder);
            Directory.CreateDirectory(_subjectsFolder);

            if (!File.Exists(RepoMapPath))
            {
                File.WriteAllText(RepoMapPath, "{}");
            }

            if (!File.Exists(_memoryBankPath) && !File.Exists(_profilePath))
            {
                SaveMemory(new SlimeMemory());
            }
            else if (!File.Exists(_memoryBankPath) && File.Exists(_profilePath))
            {
                // Migrate profile.json to memory_bank.json
                var mem = LoadMemory();
                SaveMemory(mem);
            }
        }

        // --- HISTORIK-HANTERING (Korttidsminne) ---

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    string json = File.ReadAllText(_historyPath);
                    _chatHistory = JsonSerializer.Deserialize<Dictionary<string, List<SlimeMemoryMessage>>>(json) ?? new();
                }
                else
                {
                    _chatHistory = new();
                }
            }
            catch { _chatHistory = new(); }

            try
            {
                if (!string.IsNullOrEmpty(_threadSummariesPath) && File.Exists(_threadSummariesPath))
                {
                    string sumJson = File.ReadAllText(_threadSummariesPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(sumJson);
                    _threadSummaries = loaded != null
                        ? new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _threadSummaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { _threadSummaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        private void AppendToThreadSummary(string threadId, SlimeMemoryMessage evicted)
        {
            if (evicted.Role.Equals("System", StringComparison.OrdinalIgnoreCase)) return;
            string clean = evicted.Role == "Slime"
                ? SlimeToolManager.RemoveStatusLines(evicted.Content)
                : evicted.Content;
            if (string.IsNullOrWhiteSpace(clean)) return;

            string oneLine = clean.Replace("\r", " ").Replace("\n", " ").Trim();
            if (oneLine.Length > 140) oneLine = oneLine[..137] + "...";
            string entry = $"{evicted.Role}: {oneLine}";

            if (_threadSummaries.TryGetValue(threadId, out var existing) && !string.IsNullOrWhiteSpace(existing))
            {
                string combined = existing + " | " + entry;
                if (combined.Length > 900)
                {
                    combined = "..." + combined[^890..];
                }
                _threadSummaries[threadId] = combined;
            }
            else
            {
                _threadSummaries[threadId] = entry;
            }
        }

        private void SaveThreadSummaries()
        {
            try
            {
                if (!string.IsNullOrEmpty(_threadSummariesPath))
                {
                    File.WriteAllText(_threadSummariesPath, JsonSerializer.Serialize(_threadSummaries, JsonOptions));
                }
            }
            catch { }
        }

        public string GetThreadSummary(string threadId)
        {
            return _threadSummaries.TryGetValue(threadId, out var sum) ? sum : "";
        }

        /// <summary>
        /// Compacts older messages in the thread into the rolling thread summary, keeping only the most recent keepRecent messages.
        /// </summary>
        public string CompactThreadHistory(string threadId, int keepRecent = 6)
        {
            if (!_chatHistory.TryGetValue(threadId, out var list) || list.Count <= keepRecent)
            {
                return GetThreadSummary(threadId);
            }

            int toRoll = list.Count - Math.Max(2, keepRecent);
            for (int i = 0; i < toRoll; i++)
            {
                AppendToThreadSummary(threadId, list[0]);
                list.RemoveAt(0);
            }

            try
            {
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(_chatHistory, JsonOptions));
                SaveThreadSummaries();
            }
            catch { }

            return GetThreadSummary(threadId);
        }

        public void SaveMessage(string threadId, string role, string content)
        {
            try
            {
                // Appens statusrader ("I edited X ✏️") sparas aldrig i hennes historik, annars härmar hon dem
                if (role == "Slime")
                {
                    content = SlimeToolManager.RemoveStatusLines(content);
                    if (string.IsNullOrWhiteSpace(content)) return;
                }

                if (!_chatHistory.ContainsKey(threadId))
                {
                    _chatHistory[threadId] = new List<SlimeMemoryMessage>();
                }

                _chatHistory[threadId].Add(new SlimeMemoryMessage { Role = role, Content = content });

                bool rolledAny = false;
                while (_chatHistory[threadId].Count > MaxHistoryMessagesPerThread)
                {
                    var evicted = _chatHistory[threadId][0];
                    _chatHistory[threadId].RemoveAt(0);
                    AppendToThreadSummary(threadId, evicted);
                    rolledAny = true;
                }

                File.WriteAllText(_historyPath, JsonSerializer.Serialize(_chatHistory, JsonOptions));
                if (rolledAny)
                {
                    SaveThreadSummaries();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save history: {ex.Message}");
            }
        }

        public string GetHistoryContext(string threadId, int maxMessages = 14)
        {
            var sb = new System.Text.StringBuilder();
            if (_threadSummaries.TryGetValue(threadId, out var summary) && !string.IsNullOrWhiteSpace(summary))
            {
                sb.AppendLine($"[Earlier Conversation Summary: {summary}]");
            }

            if (_chatHistory.ContainsKey(threadId) && _chatHistory[threadId].Count > 0)
            {
                var messages = _chatHistory[threadId].TakeLast(maxMessages);
                foreach (var msg in messages)
                {
                    // Städar även gammal historik som redan innehåller statusrader
                    string text = msg.Role == "Slime" ? SlimeToolManager.RemoveStatusLines(msg.Content) : msg.Content;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    sb.AppendLine($"{msg.Role}: {text}");
                }
            }

            return sb.ToString().Trim();
        }

        public IReadOnlyList<SlimeMemoryMessage> GetHistoryMessages(string threadId)
        {
            if (_chatHistory.TryGetValue(threadId, out var list))
            {
                return list.ToList();
            }
            return Array.Empty<SlimeMemoryMessage>();
        }

        public Dictionary<string, string> GetBoundRepos() => LoadRepoMap();

        public List<string> GetAllThreads()
        {
            var threads = new HashSet<string>(_chatHistory.Keys, StringComparer.OrdinalIgnoreCase);
            threads.Add("General");
            foreach (var tag in LoadRepoMap().Keys)
            {
                threads.Add(tag);
            }
            return threads.OrderBy(t => t.Equals("General", StringComparison.OrdinalIgnoreCase) ? "0" : t).ToList();
        }

        // --- REPO & FAKTA-HANTERING ---

        /// <summary>Gör om "Test", "#Test" och " #test " till "#test".</summary>
        public static string NormalizeTag(string tag)
        {
            tag = (tag ?? "").Trim().ToLowerInvariant();
            if (!tag.StartsWith("#")) tag = "#" + tag;
            return tag;
        }

        private Dictionary<string, string> LoadRepoMap()
        {
            try
            {
                if (File.Exists(RepoMapPath))
                {
                    string json = File.ReadAllText(RepoMapPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ReadOptions);
                    if (loaded != null)
                    {
                        // Skiftlägesokänslig + normaliserade nycklar
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in loaded) map[NormalizeTag(kv.Key)] = kv.Value;
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read repo_map.json: {ex.Message}");
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveRepoMap(Dictionary<string, string> map)
        {
            File.WriteAllText(RepoMapPath, JsonSerializer.Serialize(map, JsonOptions));
        }

        /// <summary>
        /// Hittar bundna repos som nämns i prompten, med eller utan inledande #
        /// ("#testrepo" och "testrepo" matchar båda). Returnerar tagg (t.ex. "#testrepo") + sökväg.
        /// </summary>
        public List<(string Tag, string Path)> GetBoundReposFromPrompt(string prompt)
        {
            var result = new List<(string Tag, string Path)>();
            if (string.IsNullOrWhiteSpace(prompt)) return result;

            var map = LoadRepoMap();
            if (map.Count == 0) return result;

            var words = prompt.Split(new[] { ' ', '\n', '\r', '\t', ',', '.', '?', '!', ':', ';', '(', ')', '"', '\'', '/', '\\' },
                                     StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                string tag = NormalizeTag(word); // lägger till # om det saknas, gemener

                if (map.TryGetValue(tag, out string? mappedPath)
                    && !string.IsNullOrEmpty(mappedPath)
                    && Directory.Exists(mappedPath)
                    && !result.Any(r => r.Path == mappedPath))
                {
                    result.Add((tag, mappedPath));
                }
            }
            return result;
        }

        public List<string> GetBoundPathsFromPrompt(string prompt)
        {
            return GetBoundReposFromPrompt(prompt).Select(r => r.Path).ToList();
        }

        /// <summary>
        /// Hittar det bundna repo som angiven mapp är roten för, eller ligger i. Längsta matchande sökväg vinner.
        /// Används av CLI:t: står du i en bunden mapp (eller en undermapp) vet Slime vilket repo du menar.
        /// </summary>
        public (string Tag, string Path)? FindBoundRepoForDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;

            char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string dirFull;
            try { dirFull = Path.GetFullPath(directory.Trim().Trim('"')).TrimEnd(seps); }
            catch { return null; }

            (string Tag, string Path)? best = null;

            foreach (var kv in LoadRepoMap())
            {
                if (string.IsNullOrWhiteSpace(kv.Value) || !Directory.Exists(kv.Value)) continue;

                string repoFull;
                try { repoFull = Path.GetFullPath(kv.Value).TrimEnd(seps); }
                catch { continue; }

                bool isSame = dirFull.Equals(repoFull, StringComparison.OrdinalIgnoreCase);
                bool isInside = dirFull.StartsWith(repoFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

                if ((isSame || isInside) && (best == null || repoFull.Length > best.Value.Path.Length))
                {
                    best = (kv.Key, repoFull);
                }
            }

            return best;
        }

        /// <summary>Binder en #tagg till en mapp. Returnerar Success=false med orsak om det misslyckas.</summary>
        public (bool Success, string Message) BindRepo(string tag, string path)
        {
            try
            {
                tag = NormalizeTag(tag);
                path = (path ?? "").Trim().Trim('"');

                if (tag == "#") return (false, "Empty tag");
                if (string.IsNullOrWhiteSpace(path)) return (false, "Empty path");

                string fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath)) return (false, $"Folder not found: {fullPath}");

                // 1. Uppdatera repo_map.json
                var map = LoadRepoMap();
                map[tag] = fullPath;
                SaveRepoMap(map);

                // 2. Skapa en dedikerad mapp för repot inuti hjärnan
                string cleanFolderName = tag.Replace("#", "").Trim();
                string newRepoDir = Path.Combine(_reposFolder, cleanFolderName);

                if (!Directory.Exists(newRepoDir))
                {
                    Directory.CreateDirectory(newRepoDir);

                    string initialNote = $"# Slime Notes for {tag}\nLinked path: {fullPath}\n\n- [ ] Initialized the project in brain.";
                    File.WriteAllText(Path.Combine(newRepoDir, "notes.md"), initialNote);
                }

                return (true, tag);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to bind repo: {ex.Message}");
                return (false, ex.Message);
            }
        }

        // FIX 2: Hanterar @Subjects, General, repos OCH undermappar!
        // Returnerar true bara om anteckningen faktiskt sparades (så att appen inte påstår något falskt)
        public bool SaveRepoNote(string tag, string filename, string content)
        {
            try
            {
                string baseDir;
                if (tag.StartsWith("@"))
                {
                    // Ämnen/Subjects hamnar i "Subjects/ämnetsnamn/"
                    string cleanSubject = tag.Replace("@", "").Trim();
                    baseDir = Path.Combine(_subjectsFolder, cleanSubject);
                }
                else if (tag.Equals("General", StringComparison.OrdinalIgnoreCase))
                {
                    baseDir = Path.Combine(_brainRootPath, "Notes");
                }
                else
                {
                    string cleanFolderName = tag.Replace("#", "").Trim();
                    baseDir = Path.Combine(_reposFolder, cleanFolderName);
                }

                // Tillåt AI:n att skapa undermappar via filnamnet (t.ex. "settings/info.md")
                string cleanRelativePath = filename.Replace('\\', '/').TrimStart('/');
                string fullPath = Path.GetFullPath(Path.Combine(baseDir, cleanRelativePath));

                // Säkerhet: Hoppa inte ur minnesmappen med ../
                // Jämförs med avslutande snedstreck, annars släpps syskonmappar med samma namnbörjan igenom ("pr" -> "pr2").
                string baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) return false;

                if (!fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    fullPath += ".md";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? baseDir);
                File.WriteAllText(fullPath, content.Trim());
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save repo note: {ex.Message}");
                return false;
            }
        }

        // FIX 3: Hämtar rätt hjärn-sökväg beroende på om det är ett @Subject eller #Repo
        public string GetRepoBrainPath(string tag)
        {
            string dir = "";
            if (tag.StartsWith("@"))
            {
                string cleanSubject = tag.Replace("@", "").Trim();
                dir = Path.Combine(_subjectsFolder, cleanSubject);
            }
            else
            {
                string cleanFolderName = tag.Replace("#", "").Trim();
                dir = Path.Combine(_reposFolder, cleanFolderName);
            }
            return Directory.Exists(dir) ? dir : "";
        }

        /// <summary>
        /// Returns the General Notes folder inside SlimeBrain (located in the Obsidian vault if linked, or LocalAppData otherwise).
        /// </summary>
        public string GetGeneralNotesFolder()
        {
            string dir = Path.Combine(_brainRootPath, "Notes");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Resolves the target directory where slime_notes.md should be read/written for a given thread.
        /// Bound #repos write to their repository root; @subjects write to their subject folder; General writes to SlimeBrain/Notes.
        /// </summary>
        public string ResolveTodoFolder(string? activeThread = null)
        {
            if (!string.IsNullOrWhiteSpace(activeThread))
            {
                if (activeThread.StartsWith("@"))
                {
                    string cleanSubject = activeThread.Replace("@", "").Trim();
                    string subjectDir = Path.Combine(_subjectsFolder, cleanSubject);
                    Directory.CreateDirectory(subjectDir);
                    return subjectDir;
                }

                if (!activeThread.Equals("General", StringComparison.OrdinalIgnoreCase))
                {
                    var repoMap = LoadRepoMap();
                    string normTag = NormalizeTag(activeThread);
                    if (repoMap.TryGetValue(normTag, out var repoPath) && Directory.Exists(repoPath))
                    {
                        return repoPath;
                    }
                }
            }

            return GetGeneralNotesFolder();
        }

        /// <summary>
        /// Returns all candidate directories to scan for open "- [ ] " To-Do items for the current thread.
        /// </summary>
        public string[] GetTodoScanFolders(string? activeThread = null, string? obsidianVaultPath = null)
        {
            var folders = new List<string>();
            string primary = ResolveTodoFolder(activeThread);
            if (!string.IsNullOrWhiteSpace(primary)) folders.Add(primary);

            string generalNotes = GetGeneralNotesFolder();
            if (!folders.Contains(generalNotes, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(generalNotes);
            }

            if (!string.IsNullOrWhiteSpace(activeThread) && !activeThread.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                string brainRepo = GetRepoBrainPath(activeThread);
                if (!string.IsNullOrWhiteSpace(brainRepo) && !folders.Contains(brainRepo, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(brainRepo);
                }
            }

            foreach (var repoPath in LoadRepoMap().Values)
            {
                if (!string.IsNullOrWhiteSpace(repoPath) && Directory.Exists(repoPath) && !folders.Contains(repoPath, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(repoPath);
                }
            }

            if (!string.IsNullOrWhiteSpace(obsidianVaultPath) && Directory.Exists(obsidianVaultPath) && !folders.Contains(obsidianVaultPath, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(obsidianVaultPath);
            }

            return folders.ToArray();
        }

        /// <summary>
        /// Sparar en säkerhetskopia av en fil i hjärnan innan Slime ändrar eller skriver över den.
        /// Kopiorna får ändelsen .bak så att de inte hamnar i anteckningssökningen. De 50 senaste per repo sparas.
        /// Returnerar false om kopian inte kunde skapas (då ska filen inte röras).
        /// </summary>
        public bool BackupFile(string tag, string basePath, string fullPath)
        {
            try
            {
                string cleanTag = tag.Replace("#", "").Trim();
                string dir = Path.Combine(_reposFolder, cleanTag, "backups");
                Directory.CreateDirectory(dir);

                string rel = Path.GetRelativePath(basePath, fullPath).Replace('\\', '_').Replace('/', '_');
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

                string target;
                int n = 0;
                do
                {
                    target = Path.Combine(dir, $"{stamp}{(n == 0 ? "" : "-" + n)}_{rel}.bak");
                    n++;
                } while (File.Exists(target));

                File.Copy(fullPath, target, false);
                try
                {
                    string relOriginal = Path.GetRelativePath(basePath, fullPath).Replace('\\', '/');
                    File.WriteAllText(target + ".meta", $"{fullPath}\n{relOriginal}");
                }
                catch { }

                var old = new DirectoryInfo(dir).GetFiles("*.bak")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(50);
                foreach (var f in old)
                {
                    try
                    {
                        f.Delete();
                        if (File.Exists(f.FullName + ".meta")) File.Delete(f.FullName + ".meta");
                    }
                    catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to back up file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores the most recently backed-up file for the given repository tag and removes the consumed backup.
        /// </summary>
        public (bool Success, string RestoredRelativePath, string Message) UndoLastFileChange(string tag, string? repoBasePath = null)
        {
            try
            {
                string cleanTag = (tag ?? "").Replace("#", "").Trim();
                if (string.IsNullOrWhiteSpace(cleanTag) || cleanTag.Equals("General", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "", "Switch to a bound #repo thread to undo code changes.");
                }

                string dir = Path.Combine(_reposFolder, cleanTag, "backups");
                if (!Directory.Exists(dir))
                {
                    return (false, "", $"No backups found for #{cleanTag}.");
                }

                var latestBak = new DirectoryInfo(dir).GetFiles("*.bak")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (latestBak == null)
                {
                    return (false, "", $"No backups available to undo in #{cleanTag}.");
                }

                string metaFile = latestBak.FullName + ".meta";
                string? targetFullPath = null;
                string restoredRel = "";

                if (File.Exists(metaFile))
                {
                    string[] lines = File.ReadAllLines(metaFile);
                    if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                    {
                        targetFullPath = lines[0].Trim();
                    }
                    if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
                    {
                        restoredRel = lines[1].Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(targetFullPath))
                {
                    string? basePath = repoBasePath;
                    if (string.IsNullOrWhiteSpace(basePath))
                    {
                        var map = LoadRepoMap();
                        map.TryGetValue(NormalizeTag(cleanTag), out basePath);
                    }

                    if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
                    {
                        return (false, "", $"Could not resolve workspace path for #{cleanTag}.");
                    }

                    string stem = Path.GetFileNameWithoutExtension(latestBak.Name);
                    int underscore = stem.IndexOf('_');
                    string flatRel = underscore >= 0 ? stem[(underscore + 1)..] : stem;
                    restoredRel = flatRel;
                    targetFullPath = Path.Combine(basePath, flatRel);
                }

                if (string.IsNullOrWhiteSpace(restoredRel))
                {
                    restoredRel = Path.GetFileName(targetFullPath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetFullPath)!);
                File.Copy(latestBak.FullName, targetFullPath, overwrite: true);

                try { latestBak.Delete(); } catch { }
                try { if (File.Exists(metaFile)) File.Delete(metaFile); } catch { }

                return (true, restoredRel, $"Restored '{restoredRel}' from backup.");
            }
            catch (Exception ex)
            {
                return (false, "", $"Undo failed: {ex.Message}");
            }
        }

        public SlimeMemory LoadMemory()
        {
            try
            {
                if (File.Exists(_memoryBankPath))
                {
                    string json = File.ReadAllText(_memoryBankPath);
                    return JsonSerializer.Deserialize<SlimeMemory>(json, ReadOptions) ?? new SlimeMemory();
                }

                if (File.Exists(_profilePath))
                {
                    string json = File.ReadAllText(_profilePath);
                    return JsonSerializer.Deserialize<SlimeMemory>(json, ReadOptions) ?? new SlimeMemory();
                }

                // Check temp memory if migration needed
                string tempMem = Path.Combine(Path.GetTempPath(), "slime_memory.json");
                if (File.Exists(tempMem))
                {
                    string json = File.ReadAllText(tempMem);
                    return JsonSerializer.Deserialize<SlimeMemory>(json, ReadOptions) ?? new SlimeMemory();
                }
            }
            catch { }
            return new SlimeMemory();
        }

        public void SaveMemory(SlimeMemory memory)
        {
            try
            {
                string json = JsonSerializer.Serialize(memory, JsonOptions);
                File.WriteAllText(_memoryBankPath, json);
                File.WriteAllText(_profilePath, json);
            }
            catch { }
        }

        /// <summary>
        /// Saves or updates a memory node in Slime's memory network.
        /// </summary>
        public (bool Success, string Message, MemoryNode? Node) RememberFact(
            string key,
            string content,
            MemoryCategory category = MemoryCategory.Fact,
            List<string>? tags = null,
            string? repoTag = null,
            int importance = 3)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(content))
                {
                    return (false, "Key and content cannot be empty", null);
                }

                var memory = LoadMemory();
                var node = new MemoryNode
                {
                    Key = key.Trim(),
                    Content = content.Trim(),
                    Category = category,
                    Tags = tags ?? new List<string>(),
                    RelatedRepoOrTag = string.IsNullOrWhiteSpace(repoTag) ? null : NormalizeTag(repoTag),
                    Importance = Math.Clamp(importance, 1, 5)
                };

                var saved = memory.AddOrUpdateNode(node);

                // Keep profile UserName in sync when saving a user_name / name fact
                if (key.Trim().ToLowerInvariant() is "user_name" or "username" or "name" or "owner_name")
                {
                    string candidate = content.Trim().Split('\n')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 60)
                    {
                        memory.UserName = candidate;
                    }
                }

                SaveMemory(memory);
                return (true, $"Remembered '{saved.Key}'", saved);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, null);
            }
        }

        /// <summary>
        /// Connects two memory nodes with a directed relation edge.
        /// </summary>
        public (bool Success, string Message) ConnectMemories(string fromKey, string toKey, string relationType = "relates_to")
        {
            try
            {
                var memory = LoadMemory();
                bool ok = memory.ConnectNodes(fromKey, toKey, relationType);
                if (ok)
                {
                    SaveMemory(memory);
                    return (true, $"Connected {fromKey} -> {relationType} -> {toKey}");
                }
                return (false, $"Could not connect nodes (one or both keys may not exist: '{fromKey}', '{toKey}')");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Forgets/removes a memory node from the memory network.
        /// </summary>
        public (bool Success, string Message) ForgetMemory(string key)
        {
            try
            {
                var memory = LoadMemory();
                bool ok = memory.RemoveNode(key);
                if (ok)
                {
                    SaveMemory(memory);
                    return (true, $"Forgot memory '{key}'");
                }
                return (false, $"Memory '{key}' was not found");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Searches the memory network for relevant nodes.
        /// </summary>
        public List<MemoryNode> FindMemories(string query, string? repoTag = null, int limit = 5)
        {
            var memory = LoadMemory();
            return memory.SearchNodes(query, repoTag, limit);
        }

        /// <summary>
        /// Generates a structured context block representing relevant memories and their network connections for a prompt.
        /// </summary>
        public string GetMemoryNetworkContext(string prompt, string? activeThread = null, int limit = 5)
        {
            var memory = LoadMemory();
            if (memory.Nodes.Count == 0 && memory.Facts.Count == 0) return "";

            string? repoTag = !string.IsNullOrWhiteSpace(activeThread) && activeThread.StartsWith("#") ? activeThread : null;
            var relevantNodes = memory.SearchNodes(prompt, repoTag, limit);

            if (relevantNodes.Count == 0)
            {
                relevantNodes = memory.Nodes
                    .Where(n => n.Importance >= 4 || (repoTag != null && string.Equals(n.RelatedRepoOrTag, NormalizeTag(repoTag), StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(n => n.Importance)
                    .Take(3)
                    .ToList();
            }

            if (relevantNodes.Count == 0 && memory.Facts.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Slime Memory Bank]");

            if (!string.IsNullOrWhiteSpace(memory.UserName) && memory.UserName != "Master")
            {
                sb.AppendLine($"User Name: {memory.UserName}");
            }

            var printedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in relevantNodes)
            {
                printedKeys.Add(node.Key);
                string tagStr = node.Tags.Count > 0 ? $" (Tags: {string.Join(", ", node.Tags)})" : "";
                string repoStr = !string.IsNullOrEmpty(node.RelatedRepoOrTag) ? $" [{node.RelatedRepoOrTag}]" : "";
                sb.AppendLine($"- [{node.Category}]{repoStr} {node.Key}: {node.Content}{tagStr}");

                foreach (var rel in node.Relations)
                {
                    var target = memory.Nodes.FirstOrDefault(n => string.Equals(n.Key, rel.TargetKey, StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                    {
                        sb.AppendLine($"  ↳ {rel.RelationType} -> {target.Key}: {target.Content}");
                        printedKeys.Add(target.Key);
                    }
                    else
                    {
                        sb.AppendLine($"  ↳ {rel.RelationType} -> {rel.TargetKey}");
                    }
                }
            }

            var remainingFacts = memory.Facts.Where(f => !relevantNodes.Any(n => n.Content == f)).Take(3).ToList();
            if (remainingFacts.Count > 0)
            {
                foreach (var f in remainingFacts)
                {
                    sb.AppendLine($"- [Fact] {f}");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}