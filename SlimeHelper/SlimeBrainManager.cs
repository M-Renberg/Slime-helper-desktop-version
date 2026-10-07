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

        public SlimeBrainManager()
        {
            string settingsPath = Path.Combine(Path.GetTempPath(), "slime_settings.json");
            string obsidianPath = "";
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
            if (!string.IsNullOrWhiteSpace(obsidianVaultPath) && Directory.Exists(obsidianVaultPath))
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

            _profilePath = Path.Combine(_userFolder, "profile.json");
            _historyPath = Path.Combine(_memoryFolder, "chat_history.json");
        }

        private void InitializeBrain()
        {
            Directory.CreateDirectory(_userFolder);
            Directory.CreateDirectory(_reposFolder);
            Directory.CreateDirectory(_memoryFolder);

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
                sb.AppendLine($"{msg.Role}: {msg.Content}");
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

        public void SaveRepoNote(string tag, string filename, string content)
        {
            try
            {
                string cleanFolderName = tag.Replace("#", "").Trim();
                string repoDir = Path.Combine(_reposFolder, cleanFolderName);

                if (!Directory.Exists(repoDir))
                {
                    Directory.CreateDirectory(repoDir);
                }

                if (!filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    filename += ".md";
                }

                foreach (var c in Path.GetInvalidFileNameChars())
                {
                    filename = filename.Replace(c.ToString(), "");
                }

                File.WriteAllText(Path.Combine(repoDir, filename), content.Trim());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save repo note: {ex.Message}");
            }
        }

        public string GetRepoBrainPath(string tag)
        {
            string cleanFolderName = tag.Replace("#", "").Trim();
            string repoDir = Path.Combine(_reposFolder, cleanFolderName);
            return Directory.Exists(repoDir) ? repoDir : "";
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