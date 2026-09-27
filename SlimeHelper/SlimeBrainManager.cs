using System.IO;
using System.Text.Json;

namespace SlimeHelper
{
    // Döpt om för att undvika krock med din befintliga ChatMessage!
    public class SlimeMemoryMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class SlimeBrainManager
    {
        private readonly string _brainRootPath;
        private readonly string _userFolder;
        private readonly string _reposFolder;
        private readonly string _memoryFolder;
        private readonly string _profilePath;
        private readonly string _historyPath;

        // Använder det nya namnet här
        private Dictionary<string, List<SlimeMemoryMessage>> _chatHistory = new();

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public SlimeBrainManager()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _brainRootPath = Path.Combine(baseDir, "SlimeHelper", "SlimeBrain");

            _userFolder = Path.Combine(_brainRootPath, "user");
            _reposFolder = Path.Combine(_brainRootPath, "repos");
            _memoryFolder = Path.Combine(_brainRootPath, "memory");

            _profilePath = Path.Combine(_userFolder, "profile.json");
            _historyPath = Path.Combine(_memoryFolder, "chat_history.json");

            InitializeBrain();
            LoadHistory();
        }

        private void InitializeBrain()
        {
            Directory.CreateDirectory(_userFolder);
            Directory.CreateDirectory(_reposFolder);
            Directory.CreateDirectory(_memoryFolder);

            string repoMapPath = Path.Combine(_reposFolder, "repo_map.json");
            if (!File.Exists(repoMapPath))
            {
                File.WriteAllText(repoMapPath, "{\n  // Map #hashtags to paths here\n}");
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
                    // Använder det nya namnet här också
                    _chatHistory = JsonSerializer.Deserialize<Dictionary<string, List<SlimeMemoryMessage>>>(json) ?? new();
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

                // Behåll bara de senaste 10 meddelandena (5 tur-och-retur) per tråd
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

        public List<string> GetBoundPathsFromPrompt(string prompt)
        {
            var paths = new List<string>();
            string repoMapPath = Path.Combine(_reposFolder, "repo_map.json");

            try
            {
                if (File.Exists(repoMapPath))
                {
                    string json = File.ReadAllText(repoMapPath);
                    if (json.Trim().StartsWith("{") && !json.Contains("// Map"))
                    {
                        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (map != null)
                        {
                            var words = prompt.Split(new[] { ' ', '\n', '\r', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var word in words)
                            {
                                string tag = word.ToLowerInvariant();
                                if (tag.StartsWith("#") && map.TryGetValue(tag, out string? mappedPath))
                                {
                                    if (mappedPath != null && Directory.Exists(mappedPath) && !paths.Contains(mappedPath))
                                    {
                                        paths.Add(mappedPath);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return paths;
        }

        public void BindRepo(string tag, string path)
        {
            string repoMapPath = Path.Combine(_reposFolder, "repo_map.json");
            var map = new Dictionary<string, string>();

            try
            {
                if (File.Exists(repoMapPath))
                {
                    string json = File.ReadAllText(repoMapPath);
                    if (!json.Trim().StartsWith("{") || json.Contains("// Map")) json = "{}";
                    map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }

                map[tag] = path;
                File.WriteAllText(repoMapPath, JsonSerializer.Serialize(map, JsonOptions));
            }
            catch { }
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