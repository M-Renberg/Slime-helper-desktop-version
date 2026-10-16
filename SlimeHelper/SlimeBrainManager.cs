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

        private Dictionary<string, List<SlimeMemoryMessage>> _chatHistory = new();

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
                string settingsPath = Path.Combine(Path.GetTempPath(), "slime_settings.json");
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

            if (!File.Exists(_profilePath))
            {
                SaveMemory(new SlimeMemory());
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

                if (_chatHistory[threadId].Count > 10)
                {
                    _chatHistory[threadId].RemoveAt(0);
                }

                File.WriteAllText(_historyPath, JsonSerializer.Serialize(_chatHistory, JsonOptions));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save history: {ex.Message}");
            }
        }

        public string GetHistoryContext(string threadId, int maxMessages = 6)
        {
            if (!_chatHistory.ContainsKey(threadId) || _chatHistory[threadId].Count == 0)
                return "";

            var messages = _chatHistory[threadId].TakeLast(maxMessages);
            var sb = new System.Text.StringBuilder();

            foreach (var msg in messages)
            {
                // Städar även gammal historik som redan innehåller statusrader
                string text = msg.Role == "Slime" ? SlimeToolManager.RemoveStatusLines(msg.Content) : msg.Content;
                if (string.IsNullOrWhiteSpace(text)) continue;

                sb.AppendLine($"{msg.Role}: {text}");
            }
            return sb.ToString().Trim();
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

                var old = new DirectoryInfo(dir).GetFiles("*.bak")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(50);
                foreach (var f in old)
                {
                    try { f.Delete(); } catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to back up file: {ex.Message}");
                return false;
            }
        }

        public SlimeMemory LoadMemory()
        {
            try
            {
                if (File.Exists(_profilePath))
                {
                    string json = File.ReadAllText(_profilePath);
                    return JsonSerializer.Deserialize<SlimeMemory>(json) ?? new SlimeMemory();
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
                File.WriteAllText(_profilePath, json);
            }
            catch { }
        }
    }
}