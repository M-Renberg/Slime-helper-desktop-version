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

    public class GeminiProvider : IAiProvider
    {
        private static readonly JsonSerializerOptions JsonIndentOptions = new() { WriteIndented = true };
        private static readonly List<ChatMessage> _history = [];
        private static readonly string MemoryFilePath = Path.Combine(Path.GetTempPath(), "slime_memory.json");
        private static SlimeMemory _memory = new();

        public GeminiProvider()
        {
            LoadMemory();
        }

        public async Task<string> GetResponseAsync(string prompt, string apiKey)
        {
            if (prompt.StartsWith("remember that ", StringComparison.OrdinalIgnoreCase))
            {
                string fact = prompt[14..].Trim();
                if (!_memory.Facts.Contains(fact))
                {
                    _memory.Facts.Add(fact);
                    SaveMemory();
                }
                return $"I'll keep that in my slime-core: '{fact}' ✨";
            }

            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent?key={apiKey}";

            string factsString = string.Join(", ", _memory.Facts);
            string dynamicInstruction = $"You are a sassy anime slime assistant. User: {_memory.UserName}. " +
                                         $"Project: {_memory.CurrentProject}. Facts: {factsString}";

            var newUserMessage = new ChatMessage
            {
                role = "user",
                parts = [new Part { text = prompt }]
            };

            var requestBody = new
            {
                system_instruction = new { parts = new[] { new { text = dynamicInstruction } } },
                contents = _history.Concat([newUserMessage]).ToArray()
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
                        AddToHistory(prompt, aiText);
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

        private static void LoadMemory()
        {
            if (File.Exists(MemoryFilePath))
            {
                try
                {
                    string json = File.ReadAllText(MemoryFilePath);
                    _memory = JsonSerializer.Deserialize<SlimeMemory>(json) ?? new SlimeMemory();
                }
                catch { _memory = new SlimeMemory(); }
            }
        }

        private static void SaveMemory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_memory, JsonIndentOptions);
                File.WriteAllText(MemoryFilePath, json);
            }
            catch (Exception ex) { Console.WriteLine("Save error: " + ex.Message); }
        }

        private static void AddToHistory(string userPrompt, string aiResponse)
        {
            _history.Add(new ChatMessage { role = "user", parts = [new Part { text = userPrompt }] });
            _history.Add(new ChatMessage { role = "model", parts = [new Part { text = aiResponse }] });

            if (_history.Count > 10)
            {
                _history.RemoveRange(0, 2);
            }
        }

        public static List<ChatMessage> GetHistory() => _history;
    }

    // Claude Provider
    public class ClaudeProvider : IAiProvider
    {
        private static readonly JsonSerializerOptions JsonIndentOptions = new() { WriteIndented = true };
        private static readonly List<ChatMessage> _history = [];
        private static readonly string MemoryFilePath = Path.Combine(Path.GetTempPath(), "slime_memory.json");
        private static SlimeMemory _memory = new();

        public ClaudeProvider()
        {
            LoadMemory();
        }

        public async Task<string> GetResponseAsync(string prompt, string apiKey)
        {
            if (prompt.StartsWith("remember that ", StringComparison.OrdinalIgnoreCase))
            {
                string fact = prompt[14..].Trim();
                if (!_memory.Facts.Contains(fact))
                {
                    _memory.Facts.Add(fact);
                    SaveMemory();
                }
                return $"I'll keep that in my slime-core: '{fact}' ✨";
            }

            using var client = new HttpClient();
            var url = "https://api.anthropic.com/v1/messages";

            string factsString = string.Join(", ", _memory.Facts);
            string dynamicInstruction = $"You are a sassy anime slime assistant. User: {_memory.UserName}. " +
                                         $"Project: {_memory.CurrentProject}. Facts: {factsString}";

            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var requestBody = new
            {
                model = "claude-3-5-sonnet-20240620",
                max_tokens = 1024,
                system = dynamicInstruction,
                messages = _history.Select(h => new
                {
                    role = h.role == "model" ? "assistant" : "user",
                    content = h.parts.Count > 0 ? h.parts[0].text : ""
                }).Concat([
                    new { role = "user", content = prompt }
                ]).ToArray()
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
                        AddToHistory(prompt, aiText);
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

        private static void LoadMemory()
        {
            if (File.Exists(MemoryFilePath))
            {
                try
                {
                    string json = File.ReadAllText(MemoryFilePath);
                    _memory = JsonSerializer.Deserialize<SlimeMemory>(json) ?? new SlimeMemory();
                }
                catch { _memory = new SlimeMemory(); }
            }
        }

        private static void SaveMemory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_memory, JsonIndentOptions);
                File.WriteAllText(MemoryFilePath, json);
            }
            catch (Exception ex) { Console.WriteLine("Save error: " + ex.Message); }
        }

        private static void AddToHistory(string userPrompt, string aiResponse)
        {
            _history.Add(new ChatMessage { role = "user", parts = [new Part { text = userPrompt }] });
            _history.Add(new ChatMessage { role = "model", parts = [new Part { text = aiResponse }] });

            if (_history.Count > 10)
            {
                _history.RemoveRange(0, 2);
            }
        }

        public static List<ChatMessage> GetHistory() => _history;
    }
}