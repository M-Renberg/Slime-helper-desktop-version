using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    public sealed record AiToolParameter(
        string Type,
        string Description,
        string[]? EnumValues = null);

    public sealed record AiToolDefinition(
        string Name,
        string Description,
        Dictionary<string, AiToolParameter> Properties,
        List<string> Required);

    public sealed record AiToolCall(
        string Id,
        string Name,
        Dictionary<string, string> Arguments);

    public sealed record AiToolResult(
        string CallId,
        string Name,
        string Result);

    public sealed record AiImageAttachment(
        string MimeType,
        string Base64Data,
        string? Label = null);

    public sealed class AiConversationTurn
    {
        public string Role { get; init; } = "user"; // "user", "assistant", or "tool"
        public string Text { get; init; } = "";
        public List<AiToolCall> ToolCalls { get; init; } = [];
        public List<AiToolResult> ToolResults { get; init; } = [];
        public List<AiImageAttachment> Images { get; init; } = [];

        public static AiConversationTurn User(string text, IEnumerable<AiImageAttachment>? images = null) =>
            new()
            {
                Role = "user",
                Text = text,
                Images = images != null ? images.ToList() : []
            };

        public static AiConversationTurn Assistant(string text, List<AiToolCall>? toolCalls = null) =>
            new() { Role = "assistant", Text = text, ToolCalls = toolCalls ?? [] };

        public static AiConversationTurn Tool(List<AiToolResult> results) =>
            new() { Role = "tool", ToolResults = results };
    }

    public sealed record AiAgentResponse(
        string Text,
        List<AiToolCall> ToolCalls,
        bool IsError = false);

    public interface IAiProvider
    {
        Task<string> GetResponseAsync(string prompt, string apiKey);

        Task<AiAgentResponse> GetAgentResponseAsync(
            string systemInstruction,
            IReadOnlyList<AiConversationTurn> turns,
            IReadOnlyList<AiToolDefinition>? tools,
            string apiKey,
            Action<string>? onTextDelta = null,
            CancellationToken cancellationToken = default);
    }

    internal static class AiMemoryStorage
    {
        public static readonly JsonSerializerOptions JsonIndentOptions = new() { WriteIndented = true };

        public static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        public static string ResolveMemoryPath()
        {
            try
            {
                string settingsPath = SlimeSettings.ResolveSettingsPath();
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
        public const string DefaultModel = "gemini-3.8-flash";

        public static readonly string[] FallbackModels =
        [
            "gemini-3.8-flash",
            "gemini-2.5-flash",
            "gemini-2.5-flash-lite",
            "gemini-2.0-flash"
        ];

        private static string? _preferredModel;
        private static DateTime _preferredModelUntil = DateTime.MinValue;
        private static SlimeMemory _memory = new();

        public GeminiProvider()
        {
            _memory = AiMemoryStorage.Load();
        }

        public static bool ShouldFallbackOnError(string? errorMessage, int statusCode = 0)
        {
            if (statusCode is 401 or 403) return false;
            string msg = errorMessage ?? "";
            if (msg.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("invalid x-api-key", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (statusCode is 404 or 429 or 500 or 502 or 503 or 504 or 529) return true;

            return msg.Contains("high demand", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Resource has been exhausted", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("is not supported", StringComparison.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<string> GetOrderedCandidateModels()
        {
            if (!string.IsNullOrEmpty(_preferredModel) && DateTime.UtcNow < _preferredModelUntil)
            {
                var ordered = new List<string> { _preferredModel };
                foreach (var m in FallbackModels)
                {
                    if (!string.Equals(m, _preferredModel, StringComparison.OrdinalIgnoreCase))
                        ordered.Add(m);
                }
                return ordered;
            }
            return FallbackModels;
        }

        private static void RecordWorkingModel(string model, bool wasFallback)
        {
            if (wasFallback)
            {
                _preferredModel = model;
                _preferredModelUntil = DateTime.UtcNow.AddMinutes(3);
            }
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

            var client = AiMemoryStorage.SharedHttpClient;

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

            string serializedBody = JsonSerializer.Serialize(requestBody);
            var candidateModels = GetOrderedCandidateModels();
            string lastError = "I received an empty response from Gemini.";

            for (int i = 0; i < candidateModels.Count; i++)
            {
                string model = candidateModels[i];
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                try
                {
                    using var content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(url, content);
                    var jsonResponse = await response.Content.ReadAsStringAsync();

                    using var doc = JsonDocument.Parse(jsonResponse);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        string msg = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                        if (i + 1 < candidateModels.Count && ShouldFallbackOnError(msg, (int)response.StatusCode))
                        {
                            lastError = "API Error: " + msg;
                            await Task.Delay(250);
                            continue;
                        }

                        if (msg.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
                        {
                            return "I'm a bit tired from all the thinking! Give me a minute to rest my slime-brain... 😴";
                        }
                        return "API Error: " + msg;
                    }

                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var parts = candidates[0].GetProperty("content").GetProperty("parts");
                        var sb = new StringBuilder();
                        foreach (var p in parts.EnumerateArray())
                        {
                            if (p.TryGetProperty("text", out var t) && t.GetString() is string s)
                                sb.Append(s);
                        }
                        if (sb.Length > 0)
                        {
                            RecordWorkingModel(model, wasFallback: i > 0);
                            return sb.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"I received a weird response I couldn't parse. Error: {ex.Message}";
                    if (i + 1 < candidateModels.Count)
                    {
                        await Task.Delay(250);
                        continue;
                    }
                }
            }

            return lastError;
        }

        public static string BuildRequestJson(
            string systemInstruction,
            IReadOnlyList<AiConversationTurn> turns,
            IReadOnlyList<AiToolDefinition>? tools)
        {
            var root = new JsonObject
            {
                ["system_instruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = systemInstruction }
                    }
                }
            };

            if (tools != null && tools.Count > 0)
            {
                var funcDecls = new JsonArray();
                foreach (var tool in tools)
                {
                    var propsObj = new JsonObject();
                    foreach (var kv in tool.Properties)
                    {
                        var pNode = new JsonObject
                        {
                            ["type"] = kv.Value.Type.ToUpperInvariant(),
                            ["description"] = kv.Value.Description
                        };
                        if (kv.Value.EnumValues is { Length: > 0 })
                        {
                            var enumArr = new JsonArray();
                            foreach (var v in kv.Value.EnumValues) enumArr.Add(v);
                            pNode["enum"] = enumArr;
                        }
                        propsObj[kv.Key] = pNode;
                    }

                    var reqArr = new JsonArray();
                    foreach (var r in tool.Required) reqArr.Add(r);

                    var fnNode = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "OBJECT",
                            ["properties"] = propsObj,
                            ["required"] = reqArr
                        }
                    };
                    funcDecls.Add(fnNode);
                }

                root["tools"] = new JsonArray
                {
                    new JsonObject { ["function_declarations"] = funcDecls }
                };
            }

            var contents = new JsonArray();
            foreach (var turn in turns)
            {
                if (turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                    turn.Role.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = new JsonArray();
                    if (!string.IsNullOrEmpty(turn.Text))
                    {
                        parts.Add(new JsonObject { ["text"] = turn.Text });
                    }
                    foreach (var tc in turn.ToolCalls)
                    {
                        var argsObj = new JsonObject();
                        foreach (var arg in tc.Arguments) argsObj[arg.Key] = arg.Value;
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = tc.Name,
                                ["args"] = argsObj
                            }
                        });
                    }
                    if (parts.Count > 0)
                    {
                        contents.Add(new JsonObject
                        {
                            ["role"] = "model",
                            ["parts"] = parts
                        });
                    }
                }
                else if (turn.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = new JsonArray();
                    foreach (var tr in turn.ToolResults)
                    {
                        parts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = tr.Name,
                                ["response"] = new JsonObject
                                {
                                    ["result"] = tr.Result
                                }
                            }
                        });
                    }
                    if (parts.Count > 0)
                    {
                        contents.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = parts
                        });
                    }
                }
                else
                {
                    var parts = new JsonArray();
                    foreach (var img in turn.Images)
                    {
                        if (!string.IsNullOrWhiteSpace(img.Base64Data))
                        {
                            parts.Add(new JsonObject
                            {
                                ["inline_data"] = new JsonObject
                                {
                                    ["mime_type"] = string.IsNullOrWhiteSpace(img.MimeType) ? "image/png" : img.MimeType,
                                    ["data"] = img.Base64Data
                                }
                            });
                        }
                    }
                    parts.Add(new JsonObject { ["text"] = turn.Text });

                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = parts
                    });
                }
            }

            root["contents"] = contents;
            return root.ToJsonString();
        }

        public static AiAgentResponse ParseResponseJson(string jsonResponse, Action<string>? onTextDelta = null)
        {
            var textAccumulator = new StringBuilder();
            var toolCalls = new List<AiToolCall>();

            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var err = ExtractCandidateFromElement(item, textAccumulator, toolCalls, onTextDelta);
                    if (err != null) return err;
                }
            }
            else
            {
                var err = ExtractCandidateFromElement(root, textAccumulator, toolCalls, onTextDelta);
                if (err != null) return err;
            }

            return new AiAgentResponse(textAccumulator.ToString(), toolCalls);
        }

        public static AiAgentResponse ParseSseLines(IEnumerable<string> lines, Action<string>? onTextDelta = null)
        {
            var textAccumulator = new StringBuilder();
            var toolCalls = new List<AiToolCall>();

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string payload = line[5..].Trim();
                if (string.IsNullOrEmpty(payload) || payload == "[DONE]") continue;

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var err = ExtractCandidateFromElement(doc.RootElement, textAccumulator, toolCalls, onTextDelta);
                    if (err != null) return err;
                }
                catch { }
            }

            return new AiAgentResponse(textAccumulator.ToString(), toolCalls);
        }

        private static AiAgentResponse? ExtractCandidateFromElement(
            JsonElement root,
            StringBuilder textAccumulator,
            List<AiToolCall> toolCalls,
            Action<string>? onTextDelta)
        {
            if (root.TryGetProperty("error", out var errorElement))
            {
                string msg = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                if (msg.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
                {
                    return new AiAgentResponse("I'm a bit tired from all the thinking! Give me a minute to rest my slime-brain...", [], IsError: true);
                }
                return new AiAgentResponse("API Error: " + msg, [], IsError: true);
            }

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var first = candidates[0];
                if (first.TryGetProperty("content", out var contentObj) &&
                    contentObj.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp) && textProp.GetString() is string delta && delta.Length > 0)
                        {
                            textAccumulator.Append(delta);
                            onTextDelta?.Invoke(delta);
                        }

                        if (part.TryGetProperty("functionCall", out var fnCall))
                        {
                            string fnName = fnCall.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            if (fnCall.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in argsElement.EnumerateObject())
                                {
                                    args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                        ? prop.Value.GetString() ?? ""
                                        : prop.Value.GetRawText();
                                }
                            }
                            if (!string.IsNullOrEmpty(fnName))
                            {
                                string callId = "call_" + Guid.NewGuid().ToString("N")[..8];
                                toolCalls.Add(new AiToolCall(callId, fnName, args));
                            }
                        }
                    }
                }
            }

            return null;
        }

        public async Task<AiAgentResponse> GetAgentResponseAsync(
            string systemInstruction,
            IReadOnlyList<AiConversationTurn> turns,
            IReadOnlyList<AiToolDefinition>? tools,
            string apiKey,
            Action<string>? onTextDelta = null,
            CancellationToken cancellationToken = default)
        {
            _memory = AiMemoryStorage.Load();
            string factsString = _memory.Facts.Count > 0 ? string.Join(", ", _memory.Facts.TakeLast(10)) : "";
            string memoryPrefix = $"User: {_memory.UserName}. Project: {_memory.CurrentProject}." +
                                  (!string.IsNullOrEmpty(factsString) ? $" Core Facts: {factsString}" : "");
            string fullSystem = string.IsNullOrWhiteSpace(systemInstruction)
                ? memoryPrefix
                : $"{systemInstruction}\n\n{memoryPrefix}";

            string bodyJson = BuildRequestJson(fullSystem, turns, tools);
            var client = AiMemoryStorage.SharedHttpClient;
            bool useStream = onTextDelta != null;
            var candidateModels = GetOrderedCandidateModels();
            AiAgentResponse lastErrorResp = new("I received an empty response from Gemini.", [], IsError: true);

            for (int i = 0; i < candidateModels.Count; i++)
            {
                string model = candidateModels[i];
                string endpoint = useStream
                    ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}"
                    : $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                    };

                    using var response = await client.SendAsync(
                        req,
                        useStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                        cancellationToken);

                    if (!useStream || !response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                        var parsed = ParseResponseJson(jsonResponse, onTextDelta);
                        if (parsed.IsError && i + 1 < candidateModels.Count && ShouldFallbackOnError(parsed.Text, (int)response.StatusCode))
                        {
                            lastErrorResp = parsed;
                            await Task.Delay(250, cancellationToken);
                            continue;
                        }
                        if (!parsed.IsError)
                        {
                            RecordWorkingModel(model, wasFallback: i > 0);
                        }
                        return parsed;
                    }

                    var textAccumulator = new StringBuilder();
                    var toolCalls = new List<AiToolCall>();
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    AiAgentResponse? streamErr = null;
                    while (!cancellationToken.IsCancellationRequested &&
                           await reader.ReadLineAsync(cancellationToken) is string line)
                    {
                        string trimmed = line.Trim();
                        if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                        string payload = trimmed[5..].Trim();
                        if (string.IsNullOrEmpty(payload) || payload == "[DONE]") continue;

                        using var doc = JsonDocument.Parse(payload);
                        streamErr = ExtractCandidateFromElement(doc.RootElement, textAccumulator, toolCalls, onTextDelta);
                        if (streamErr != null) break;
                    }

                    if (streamErr != null)
                    {
                        if (i + 1 < candidateModels.Count && textAccumulator.Length == 0 && ShouldFallbackOnError(streamErr.Text))
                        {
                            lastErrorResp = streamErr;
                            await Task.Delay(250, cancellationToken);
                            continue;
                        }
                        return streamErr;
                    }

                    RecordWorkingModel(model, wasFallback: i > 0);
                    return new AiAgentResponse(textAccumulator.ToString(), toolCalls);
                }
                catch (Exception ex)
                {
                    lastErrorResp = new AiAgentResponse($"I received a weird response I couldn't parse. Error: {ex.Message}", [], IsError: true);
                    if (i + 1 < candidateModels.Count && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(250, cancellationToken);
                        continue;
                    }
                }
            }

            return lastErrorResp;
        }

        public static List<ChatMessage> GetHistory() => [];
    }

    // Claude Provider
    public class ClaudeProvider : IAiProvider
    {
        public const string DefaultModel = "claude-5-5-sonnet";

        public static readonly string[] FallbackModels =
        [
            "claude-5-5-sonnet",
            "claude-3-7-sonnet-latest",
            "claude-3-5-sonnet-20241022",
            "claude-3-5-sonnet-20240620",
            "claude-3-5-haiku-latest"
        ];

        private static string? _preferredModel;
        private static DateTime _preferredModelUntil = DateTime.MinValue;
        private static SlimeMemory _memory = new();

        public ClaudeProvider()
        {
            _memory = AiMemoryStorage.Load();
        }

        public static IReadOnlyList<string> GetOrderedCandidateModels()
        {
            if (!string.IsNullOrEmpty(_preferredModel) && DateTime.UtcNow < _preferredModelUntil)
            {
                var ordered = new List<string> { _preferredModel };
                foreach (var m in FallbackModels)
                {
                    if (!string.Equals(m, _preferredModel, StringComparison.OrdinalIgnoreCase))
                        ordered.Add(m);
                }
                return ordered;
            }
            return FallbackModels;
        }

        private static void RecordWorkingModel(string model, bool wasFallback)
        {
            if (wasFallback)
            {
                _preferredModel = model;
                _preferredModelUntil = DateTime.UtcNow.AddMinutes(3);
            }
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

            var client = AiMemoryStorage.SharedHttpClient;
            var url = "https://api.anthropic.com/v1/messages";

            string factsString = _memory.Facts.Count > 0 ? string.Join(", ", _memory.Facts.TakeLast(10)) : "";
            string dynamicInstruction = $"You are a sassy anime slime assistant. User: {_memory.UserName}. " +
                                         $"Project: {_memory.CurrentProject}." +
                                         (!string.IsNullOrEmpty(factsString) ? $" Facts: {factsString}" : "");

            var candidateModels = GetOrderedCandidateModels();
            string lastError = "I received an empty response from Claude.";

            for (int i = 0; i < candidateModels.Count; i++)
            {
                string model = candidateModels[i];
                var requestBody = new
                {
                    model,
                    max_tokens = 4096,
                    system = dynamicInstruction,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    }
                };

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");

                    using var response = await client.SendAsync(req);
                    var jsonResponse = await response.Content.ReadAsStringAsync();

                    using var doc = JsonDocument.Parse(jsonResponse);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        string msg = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                        if (i + 1 < candidateModels.Count && GeminiProvider.ShouldFallbackOnError(msg, (int)response.StatusCode))
                        {
                            lastError = "API Error: " + msg;
                            await Task.Delay(250);
                            continue;
                        }
                        return "API Error: " + msg;
                    }

                    if (root.TryGetProperty("content", out var contentArray) && contentArray.GetArrayLength() > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var block in contentArray.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var t) && t.GetString() is string s)
                                sb.Append(s);
                        }
                        if (sb.Length > 0)
                        {
                            RecordWorkingModel(model, wasFallback: i > 0);
                            return sb.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"I received a weird response I couldn't parse. Error: {ex.Message}";
                    if (i + 1 < candidateModels.Count)
                    {
                        await Task.Delay(250);
                        continue;
                    }
                }
            }

            return lastError;
        }

        public static string BuildRequestJson(
            string systemInstruction,
            IReadOnlyList<AiConversationTurn> turns,
            IReadOnlyList<AiToolDefinition>? tools,
            bool stream = false,
            string? modelOverride = null)
        {
            var root = new JsonObject
            {
                ["model"] = string.IsNullOrWhiteSpace(modelOverride) ? DefaultModel : modelOverride,
                ["max_tokens"] = 4096,
                ["system"] = systemInstruction
            };

            if (stream)
            {
                root["stream"] = true;
            }

            if (tools != null && tools.Count > 0)
            {
                var toolsArr = new JsonArray();
                foreach (var tool in tools)
                {
                    var propsObj = new JsonObject();
                    foreach (var kv in tool.Properties)
                    {
                        var pNode = new JsonObject
                        {
                            ["type"] = kv.Value.Type.ToLowerInvariant(),
                            ["description"] = kv.Value.Description
                        };
                        if (kv.Value.EnumValues is { Length: > 0 })
                        {
                            var enumArr = new JsonArray();
                            foreach (var v in kv.Value.EnumValues) enumArr.Add(v);
                            pNode["enum"] = enumArr;
                        }
                        propsObj[kv.Key] = pNode;
                    }

                    var reqArr = new JsonArray();
                    foreach (var r in tool.Required) reqArr.Add(r);

                    toolsArr.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["input_schema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = propsObj,
                            ["required"] = reqArr
                        }
                    });
                }
                root["tools"] = toolsArr;
            }

            var messages = new JsonArray();
            foreach (var turn in turns)
            {
                if (turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var contentBlocks = new JsonArray();
                    if (!string.IsNullOrEmpty(turn.Text))
                    {
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = turn.Text
                        });
                    }
                    foreach (var tc in turn.ToolCalls)
                    {
                        var inputObj = new JsonObject();
                        foreach (var arg in tc.Arguments) inputObj[arg.Key] = arg.Value;
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = inputObj
                        });
                    }
                    if (contentBlocks.Count > 0)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = contentBlocks
                        });
                    }
                }
                else if (turn.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    var contentBlocks = new JsonArray();
                    foreach (var tr in turn.ToolResults)
                    {
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = tr.CallId,
                            ["content"] = tr.Result
                        });
                    }
                    if (contentBlocks.Count > 0)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = contentBlocks
                        });
                    }
                }
                else
                {
                    if (turn.Images.Count > 0)
                    {
                        var contentBlocks = new JsonArray();
                        foreach (var img in turn.Images)
                        {
                            if (!string.IsNullOrWhiteSpace(img.Base64Data))
                            {
                                contentBlocks.Add(new JsonObject
                                {
                                    ["type"] = "image",
                                    ["source"] = new JsonObject
                                    {
                                        ["type"] = "base64",
                                        ["media_type"] = string.IsNullOrWhiteSpace(img.MimeType) ? "image/png" : img.MimeType,
                                        ["data"] = img.Base64Data
                                    }
                                });
                            }
                        }
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = string.IsNullOrWhiteSpace(turn.Text) ? "Describe or analyze this image." : turn.Text
                        });
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = contentBlocks
                        });
                    }
                    else
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = turn.Text
                        });
                    }
                }
            }

            root["messages"] = messages;
            return root.ToJsonString();
        }

        public static AiAgentResponse ParseResponseJson(string jsonResponse, Action<string>? onTextDelta = null)
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                string msg = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                return new AiAgentResponse("API Error: " + msg, [], IsError: true);
            }

            var textAccumulator = new StringBuilder();
            var toolCalls = new List<AiToolCall>();

            if (root.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentArray.EnumerateArray())
                {
                    string type = block.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";
                    if (type == "text" && block.TryGetProperty("text", out var textProp) && textProp.GetString() is string text && text.Length > 0)
                    {
                        textAccumulator.Append(text);
                        onTextDelta?.Invoke(text);
                    }
                    else if (type == "tool_use")
                    {
                        string id = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? ("toolu_" + Guid.NewGuid().ToString("N")[..8]) : ("toolu_" + Guid.NewGuid().ToString("N")[..8]);
                        string name = block.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        if (block.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in inputEl.EnumerateObject())
                            {
                                args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                    ? prop.Value.GetString() ?? ""
                                    : prop.Value.GetRawText();
                            }
                        }

                        if (!string.IsNullOrEmpty(name))
                        {
                            toolCalls.Add(new AiToolCall(id, name, args));
                        }
                    }
                }
            }

            return new AiAgentResponse(textAccumulator.ToString(), toolCalls);
        }

        public static AiAgentResponse ParseSseLines(IEnumerable<string> lines, Action<string>? onTextDelta = null)
        {
            var state = new ClaudeSseState(onTextDelta);
            foreach (var rawLine in lines)
            {
                var err = state.ProcessLine(rawLine);
                if (err != null) return err;
            }
            return state.Finish();
        }

        private sealed class ClaudeSseState
        {
            private readonly StringBuilder _textAccumulator = new();
            private readonly List<AiToolCall> _toolCalls = [];
            private readonly Action<string>? _onTextDelta;
            private string? _activeToolId;
            private string? _activeToolName;
            private readonly StringBuilder _activeToolJson = new();

            public ClaudeSseState(Action<string>? onTextDelta)
            {
                _onTextDelta = onTextDelta;
            }

            private void FlushActiveTool()
            {
                if (!string.IsNullOrEmpty(_activeToolName))
                {
                    var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string rawJson = _activeToolJson.ToString().Trim();
                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        try
                        {
                            using var argDoc = JsonDocument.Parse(rawJson);
                            if (argDoc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in argDoc.RootElement.EnumerateObject())
                                {
                                    args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                        ? prop.Value.GetString() ?? ""
                                        : prop.Value.GetRawText();
                                }
                            }
                        }
                        catch { }
                    }
                    _toolCalls.Add(new AiToolCall(_activeToolId ?? ("toolu_" + Guid.NewGuid().ToString("N")[..8]), _activeToolName, args));
                }
                _activeToolId = null;
                _activeToolName = null;
                _activeToolJson.Clear();
            }

            public AiAgentResponse? ProcessLine(string rawLine)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
                string payload = line[5..].Trim();
                if (string.IsNullOrEmpty(payload) || payload == "[DONE]") return null;

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    string eventType = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

                    if (eventType == "error" && root.TryGetProperty("error", out var errObj))
                    {
                        string msg = errObj.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                        return new AiAgentResponse("API Error: " + msg, [], IsError: true);
                    }

                    if (eventType == "content_block_start" && root.TryGetProperty("content_block", out var cb))
                    {
                        FlushActiveTool();
                        string cbType = cb.TryGetProperty("type", out var cbt) ? cbt.GetString() ?? "" : "";
                        if (cbType == "tool_use")
                        {
                            _activeToolId = cb.TryGetProperty("id", out var idP) ? idP.GetString() : null;
                            _activeToolName = cb.TryGetProperty("name", out var nP) ? nP.GetString() : null;
                            if (cb.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object && inp.EnumerateObject().Any())
                            {
                                _activeToolJson.Append(inp.GetRawText());
                            }
                        }
                    }
                    else if (eventType == "content_block_delta" && root.TryGetProperty("delta", out var delta))
                    {
                        string dType = delta.TryGetProperty("type", out var dt) ? dt.GetString() ?? "" : "";
                        if (dType == "text_delta" && delta.TryGetProperty("text", out var txt) && txt.GetString() is string s && s.Length > 0)
                        {
                            _textAccumulator.Append(s);
                            _onTextDelta?.Invoke(s);
                        }
                        else if (dType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj) && pj.GetString() is string jsonPart)
                        {
                            _activeToolJson.Append(jsonPart);
                        }
                    }
                    else if (eventType == "content_block_stop" || eventType == "message_stop")
                    {
                        FlushActiveTool();
                    }
                }
                catch { }

                return null;
            }

            public AiAgentResponse Finish()
            {
                FlushActiveTool();
                return new AiAgentResponse(_textAccumulator.ToString(), _toolCalls);
            }
        }

        public async Task<AiAgentResponse> GetAgentResponseAsync(
            string systemInstruction,
            IReadOnlyList<AiConversationTurn> turns,
            IReadOnlyList<AiToolDefinition>? tools,
            string apiKey,
            Action<string>? onTextDelta = null,
            CancellationToken cancellationToken = default)
        {
            _memory = AiMemoryStorage.Load();
            string factsString = _memory.Facts.Count > 0 ? string.Join(", ", _memory.Facts.TakeLast(10)) : "";
            string memoryPrefix = $"User: {_memory.UserName}. Project: {_memory.CurrentProject}." +
                                  (!string.IsNullOrEmpty(factsString) ? $" Core Facts: {factsString}" : "");
            string fullSystem = string.IsNullOrWhiteSpace(systemInstruction)
                ? memoryPrefix
                : $"{systemInstruction}\n\n{memoryPrefix}";

            bool useStream = onTextDelta != null;
            var client = AiMemoryStorage.SharedHttpClient;
            var candidateModels = GetOrderedCandidateModels();
            AiAgentResponse lastErrorResp = new("I received an empty response from Claude.", [], IsError: true);

            for (int i = 0; i < candidateModels.Count; i++)
            {
                string model = candidateModels[i];
                string bodyJson = BuildRequestJson(fullSystem, turns, tools, stream: useStream, modelOverride: model);

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                    {
                        Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");

                    using var response = await client.SendAsync(
                        req,
                        useStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                        cancellationToken);

                    if (!useStream || !response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                        var parsed = ParseResponseJson(jsonResponse, onTextDelta);
                        if (parsed.IsError && i + 1 < candidateModels.Count && GeminiProvider.ShouldFallbackOnError(parsed.Text, (int)response.StatusCode))
                        {
                            lastErrorResp = parsed;
                            await Task.Delay(250, cancellationToken);
                            continue;
                        }
                        if (!parsed.IsError)
                        {
                            RecordWorkingModel(model, wasFallback: i > 0);
                        }
                        return parsed;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var sseState = new ClaudeSseState(onTextDelta);

                    AiAgentResponse? streamErr = null;
                    while (!cancellationToken.IsCancellationRequested &&
                           await reader.ReadLineAsync(cancellationToken) is string line)
                    {
                        streamErr = sseState.ProcessLine(line);
                        if (streamErr != null) break;
                    }

                    if (streamErr != null)
                    {
                        if (i + 1 < candidateModels.Count && GeminiProvider.ShouldFallbackOnError(streamErr.Text))
                        {
                            lastErrorResp = streamErr;
                            await Task.Delay(250, cancellationToken);
                            continue;
                        }
                        return streamErr;
                    }

                    RecordWorkingModel(model, wasFallback: i > 0);
                    return sseState.Finish();
                }
                catch (Exception ex)
                {
                    lastErrorResp = new AiAgentResponse($"I received a weird response I couldn't parse. Error: {ex.Message}", [], IsError: true);
                    if (i + 1 < candidateModels.Count && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(250, cancellationToken);
                        continue;
                    }
                }
            }

            return lastErrorResp;
        }

        public static List<ChatMessage> GetHistory() => [];
    }
}