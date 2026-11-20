namespace SlimeHelper
{
    public static class AiService
    {
        public const string DefaultPersona =
            "You are a witty, slightly snarky Slime assistant for a software developer. Stay in character, keep your chit-chat short and fun, " +
            "but ALWAYS deliver the information the user actually asked for in full (file lists, file contents, code, explanations). " +
            "Never replace requested content with a remark that you looked it up or listed it. " +
            "Never write lines that begin with \"System:\", \"Slime:\" or \"User:\" - the application adds those itself. " +
            "Never use emojis in what you say.";

        public static async Task<string> AskSlime(string userPrompt, IAiProvider provider, string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey) ||
                apiKey == "Enter your key here!" ||
                apiKey == "Enter your Key here!")
            {
                return "My brain is empty! Please set my API Key in the settings menu first.";
            }

            // Keep persona snappy while delivering requested content in full.
            const string persona =
                DefaultPersona + " Always reply in English, even if the user writes in another language.";

            string fullPrompt = $"{persona}{Environment.NewLine}{Environment.NewLine}{userPrompt}";

            try
            {
                return await provider.GetResponseAsync(fullPrompt, apiKey);
            }
            catch (Exception ex)
            {
                return $"Ugh, my brain hurts... (Error: {ex.Message})";
            }
        }

        public static async Task<AiAgentResponse> AskSlimeAgentAsync(
            string systemContext,
            IReadOnlyList<AiConversationTurn> turns,
            IReadOnlyList<AiToolDefinition>? tools,
            IAiProvider provider,
            string apiKey,
            Action<string>? onTextDelta = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(apiKey) ||
                apiKey == "Enter your key here!" ||
                apiKey == "Enter your Key here!")
            {
                return new AiAgentResponse("My brain is empty! Please set my API Key in the settings menu first.", [], IsError: true);
            }

            string fullSystem = string.IsNullOrWhiteSpace(systemContext)
                ? DefaultPersona
                : $"{DefaultPersona}\n\n{systemContext}";

            try
            {
                return await provider.GetAgentResponseAsync(fullSystem, turns, tools, apiKey, onTextDelta, cancellationToken);
            }
            catch (Exception ex)
            {
                return new AiAgentResponse($"Ugh, my brain hurts... (Error: {ex.Message})", [], IsError: true);
            }
        }
    }
}