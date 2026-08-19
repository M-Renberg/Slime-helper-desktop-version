//var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent?key={apiKey}";
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
namespace SlimeHelper
{
    public class ChatMessage
    {
        public string role { get; set; }
        public List<Part> parts { get; set; }
    }

    public class Part
    {
        public string text { get; set; }
    }

    public interface IAiProvider
    {
        Task<string> GetResponseAsync(string prompt, string apiKey);
    }

    public class GeminiProvider : IAiProvider
    {
        private static List<ChatMessage> _history = new List<ChatMessage>();

        private static readonly string MemoryFilePath = Path.Combine(Path.GetTempPath(), "slime_memory.json");
        private static SlimeMemory _memory = new SlimeMemory();

        public GeminiProvider()
        {
            LoadMemory();
        }

        public async Task<string> GetResponseAsync(string prompt, string apiKey)
        {

            string lowerPrompt = prompt.ToLower();
            if (lowerPrompt.StartsWith("remember that "))
            {
                string fact = prompt.Substring(14).Trim();
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
                parts = new List<Part> { new Part { text = prompt } }
            };

            var requestBody = new
            {
                system_instruction = new { parts = new[] { new { text = dynamicInstruction } } },
                contents = _history.Concat(new[] { newUserMessage }).ToArray()
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
                    string msg = errorElement.GetProperty("message").GetString();
                    if (msg.Contains("Quota exceeded"))
                    {
                        return "I'm a bit tired from all the thinking! Give me a minute to rest my slime-brain... 😴";
                    }
                    return "API Error: " + msg;
                }

                var aiText = root.GetProperty("candidates")[0]
                                 .GetProperty("content")
                                 .GetProperty("parts")[0]
                                 .GetProperty("text")
                                 .GetString();

                AddToHistory(prompt, aiText);

                return aiText;
            }
            catch (Exception ex)
            {
                return $"I received a weird response I couldn't parse. Error: {ex.Message}";
            }
        }

        private void LoadMemory()
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

        private void SaveMemory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_memory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(MemoryFilePath, json);
            }
            catch (Exception ex) { Console.WriteLine("Save error: " + ex.Message); }
        }

        private void AddToHistory(string userPrompt, string aiResponse)
        {
            _history.Add(new ChatMessage { role = "user", parts = new List<Part> { new Part { text = userPrompt } } });
            _history.Add(new ChatMessage { role = "model", parts = new List<Part> { new Part { text = aiResponse } } });

            if (_history.Count > 10)
            {
                _history.RemoveRange(0, 2);
            }
        }

        public static List<ChatMessage> GetHistory() => _history;
    }

    //Claude!!

    public class ClaudeProvider : IAiProvider
    {
        private static List<ChatMessage> _history = new List<ChatMessage>();

        private static readonly string MemoryFilePath = Path.Combine(Path.GetTempPath(), "slime_memory.json");
        private static SlimeMemory _memory = new SlimeMemory();

        public ClaudeProvider()
        {
            LoadMemory();
        }
        public async Task<string> GetResponseAsync(string prompt, string apiKey)
        {
            string lowerPrompt = prompt.ToLower();
            if (lowerPrompt.StartsWith("remember that "))
            {
                string fact = prompt.Substring(14).Trim();
                if (!_memory.Facts.Contains(fact))
                {
                    _memory.Facts.Add(fact);
                    SaveMemory();
                }
                return $"I'll keep that in my slime-core: '{fact}' ✨";
            }

            using var client = new HttpClient();
            var url = $"https://api.anthropic.com/v1/messages";

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
                    content = h.parts[0].text
                }).Concat(new[] {
                    new { role = "user", content = prompt }
                }).ToArray()
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                // Claude returnerar texten i 'content[0].text'
                var aiText = root.GetProperty("content")[0]
                                 .GetProperty("text")
                                 .GetString();

                AddToHistory(prompt, aiText);
                return aiText;
            }
            catch (Exception ex)
            {
                return $"I received a weird response I couldn't parse. Error: {ex.Message}";
            }
        }

        private void LoadMemory()
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

        private void SaveMemory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_memory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(MemoryFilePath, json);
            }
            catch (Exception ex) { Console.WriteLine("Save error: " + ex.Message); }
        }

        private void AddToHistory(string userPrompt, string aiResponse)
        {
            _history.Add(new ChatMessage { role = "user", parts = new List<Part> { new Part { text = userPrompt } } });
            _history.Add(new ChatMessage { role = "model", parts = new List<Part> { new Part { text = aiResponse } } });

            if (_history.Count > 10)
            {
                _history.RemoveRange(0, 2);
            }
        }

        public static List<ChatMessage> GetHistory() => _history;
    }

}