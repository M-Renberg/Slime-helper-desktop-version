using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SlimeHelper
{
    public class ChatMessage
    {
        public string role { get; set; } = "";
        public List<Part> parts { get; set; } = [];
    }

    public class Part
    {
        public string text { get; set; } = "";
    }

    public interface IAiProvider
    {
        Task<string> GetResponseAsync(string prompt, string apiKey);
    }

    internal static class AiMemoryStorage
    {
        public static readonly JsonSerializerOptions JsonIndentOptions = new() { WriteIndented = true };

        public static string ResolveMemoryPath()
        {
            try
            {
                string settingsPath = Path.Combine(Path.GetTempPath(), "slime_settings.json");
                if (File.Exists(settingsPath))
                {
                    var jsonDoc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (jsonDoc.RootElement.TryGetProperty("ObsidianVaultPath", out var obsProp))
                    {
                        string obsPath = obsProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(obsPath) && Directory.Exists(obsPath))
                        {
                            string obsBrain = Path.Combine(obsPath, "SlimeBrain", "memory", "memory_bank.json");
                            if (File.Exists(obsBrain)) return obsBrain;
                        }
                    }
                }

                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string brainPath = Path.Combine(baseDir, "SlimeHelper", "SlimeBrain", "memory", "memory_bank.json");
                if (File.Exists(brainPath)) return brainPath;
            }
            catch { }

            return Path.Combine(Path.GetTempPath(), "slime_memory.json");
        }

        public static SlimeMemory Load()
        {
            string path = ResolveMemoryPath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<SlimeMemory>(json) ?? new SlimeMemory();
                }
                catch { }
            }
            return new SlimeMemory();
        }

        public static void Save(SlimeMemory memory)
        {
            try
            {
                string json = JsonSerializer.Serialize(memory, JsonIndentOptions);
                string mainPath = ResolveMemoryPath();
                string? dir = Path.GetDirectoryName(mainPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(mainPath, json);

                // Keep temp copy in sync as fallback
                string tempPath = Path.Combine(Path.GetTempPath(), "slime_memory.json");
                if (!string.Equals(mainPath, tempPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(tempPath, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Save error: " + ex.Message);
            }
        }
    }

    public class GeminiProvider : IAiProvider
    {
        private static SlimeMemory _memory = new();

        public GeminiProvider()
        {
            _memory = AiMemoryStorage.Load();
        }

        public async Task<string> GetResponseAsync(string prompt, string apiKey)
        {
            if (prompt.StartsWith("remember that ", StringComparison.OrdinalIgnoreCase))
            {
                string fact = prompt[14..].Trim();
                _memory.AddOrUpdateNode(new MemoryNode
                {
                    Key = "fact_" + Guid.NewGuid().ToString("N")[..6],
                    Content = fact,
                    Category = MemoryCategory.Fact,
                    Importance = 3
                });
                AiMemoryStorage.Save(_memory);
                return $"I'll keep that in my slime-core: '{fact}' ✨";
            }

            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent?key={apiKey}";

            string factsString = _memory.Facts.Count > 0 ? string.Join(", ", _memory.Facts.TakeLast(10)) : "";
            string dynamicInstruction = $"You are a sassy anime slime assistant. User: {_memory.UserName}. " +
                                         $"Project: {_memory.CurrentProject}." +
                                         (!string.IsNullOrEmpty(factsString) ? $" Facts: {factsString}" : "");

            var newUserMessage = new ChatMessage
            {
                role = "user",
                parts = [new Part { text = prompt }]
            };

            var requestBody = new
            {
                system_instruction = new { parts = new[] { new { text = dynamicInstruction } } },
                contents = new[] { newUserMessage }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    string msg = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                    if (msg.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
                    {
                        return "I'm a bit tired from all the thinking! Give me a minute to rest my slime-brain... 😴";
                    }
                    return "API Error: " + msg;
                }

                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    string? aiText = candidates[0]
                                     .GetProperty("content")
                                     .GetProperty("parts")[0]
                                     .GetProperty("text")
                                     .GetString();

                    if (!string.IsNullOrEmpty(aiText))
                    {
                        return aiText;
                    }
                }

                return "I received an empty response from Gemini.";
            }
            catch (Exception ex)
            {
                return $"I received a weird response I couldn't parse. Error: {ex.Message}";
            }
        }

        public static List<ChatMessage> GetHistory() => [];
    }

    // Claude Provider
    public class ClaudeProvider : IAiProvider
    {
        private static SlimeMemory _memory = new();

        public ClaudeProvider()
        {
            _memory = AiMemoryStorage.Load();
        }

        public async Task<string> GetResponseAsync(string prompt, string apiKey)
        {
            if (prompt.StartsWith("remember that ", StringComparison.OrdinalIgnoreCase))
            {
                string fact = prompt[14..].Trim();
                _memory.AddOrUpdateNode(new MemoryNode
                {
                    Key = "fact_" + Guid.NewGuid().ToString("N")[..6],
                    Content = fact,
                    Category = MemoryCategory.Fact,
                    Importance = 3
                });
                AiMemoryStorage.Save(_memory);
                return $"I'll keep that in my slime-core: '{fact}' ✨";
            }

            using var client = new HttpClient();
            var url = "https://api.anthropic.com/v1/messages";

            string factsString = _memory.Facts.Count > 0 ? string.Join(", ", _memory.Facts.TakeLast(10)) : "";
            string dynamicInstruction = $"You are a sassy anime slime assistant. User: {_memory.UserName}. " +
                                         $"Project: {_memory.CurrentProject}." +
                                         (!string.IsNullOrEmpty(factsString) ? $" Facts: {factsString}" : "");

            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var requestBody = new
            {
                model = "claude-3-5-sonnet-20240620",
                max_tokens = 4096,
                system = dynamicInstruction,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    string msg = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                    return "API Error: " + msg;
                }

                if (root.TryGetProperty("content", out var contentArray) && contentArray.GetArrayLength() > 0)
                {
                    string? aiText = contentArray[0]
                                     .GetProperty("text")
                                     .GetString();

                    if (!string.IsNullOrEmpty(aiText))
                    {
                        return aiText;
                    }
                }

                return "I received an empty response from Claude.";
            }
            catch (Exception ex)
            {
                return $"I received a weird response I couldn't parse. Error: {ex.Message}";
            }
        }

        public static List<ChatMessage> GetHistory() => [];
    }
}