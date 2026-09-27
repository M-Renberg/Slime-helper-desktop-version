using System.IO;
using System.Text.Json;

namespace SlimeHelper
{
    public class SlimeBrainManager
    {
        private readonly string _brainRootPath;
        private readonly string _userFolder;
        private readonly string _reposFolder;
        private readonly string _memoryFolder;
        private readonly string _profilePath;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public SlimeBrainManager()
        {
            // Placerar hjärnan i AppData så den inte ligger och skräpar i dina vanliga dokument
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _brainRootPath = Path.Combine(baseDir, "SlimeHelper", "SlimeBrain");

            _userFolder = Path.Combine(_brainRootPath, "user");
            _reposFolder = Path.Combine(_brainRootPath, "repos");
            _memoryFolder = Path.Combine(_brainRootPath, "memory");

            _profilePath = Path.Combine(_userFolder, "profile.json");

            InitializeBrain();
        }

        private void InitializeBrain()
        {
            // 1. Skapar huvudstrukturen om den saknas
            Directory.CreateDirectory(_userFolder);
            Directory.CreateDirectory(_reposFolder);
            Directory.CreateDirectory(_memoryFolder);

            // 2. Skapar en tom repomapning för framtida hashtag-bindningar
            string repoMapPath = Path.Combine(_reposFolder, "repo_map.json");
            if (!File.Exists(repoMapPath))
            {
                File.WriteAllText(repoMapPath, "{\n  // Map #hashtags to paths here\n}");
            }

            // 3. Skapar en initial användarprofil kopplad till SlimeMemory om den saknas
            if (!File.Exists(_profilePath))
            {
                SaveMemory(new SlimeMemory());
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load Slime memory: {ex.Message}");
            }
            return new SlimeMemory();
        }

        public void SaveMemory(SlimeMemory memory)
        {
            try
            {
                string json = JsonSerializer.Serialize(memory, JsonOptions);
                File.WriteAllText(_profilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save Slime memory: {ex.Message}");
            }
        }

        public string GetRepoFolderPath(string repoName)
        {
            string path = Path.Combine(_reposFolder, repoName);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
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
                    if (!json.Trim().StartsWith("{") || json.Contains("// Map"))
                    {
                        json = "{}";
                    }
                    map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }

                map[tag] = path;
                File.WriteAllText(repoMapPath, JsonSerializer.Serialize(map, JsonOptions));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to bind repo: {ex.Message}");
            }
        }

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
                            // Hitta alla ord i prompten och rensa bort skiljetecken
                            var words = prompt.Split(new[] { ' ', '\n', '\r', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var word in words)
                            {
                                string tag = word.ToLowerInvariant();
                                if (tag.StartsWith("#") && map.TryGetValue(tag, out string mappedPath))
                                {
                                    if (Directory.Exists(mappedPath) && !paths.Contains(mappedPath))
                                    {
                                        paths.Add(mappedPath);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse tags: {ex.Message}");
            }

            return paths;
        }

        public string BrainRoot => _brainRootPath;
        public string MemoryFolder => _memoryFolder;
    }
}
