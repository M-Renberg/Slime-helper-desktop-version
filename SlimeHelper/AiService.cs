namespace SlimeHelper
{
    public static class AiService
    {
        public static async Task<string> AskSlime(string userPrompt, IAiProvider provider, string apiKey)
        {

            if (string.IsNullOrEmpty(apiKey) ||
                apiKey == "Enter your key here!")
            {
                return "My brain is empty! Please set my API Key in the settings menu first.";
            }

            string fullPrompt = $"You are a witty, slightly snarky Slime assistant for a software developer. Keep your answers concise, helpful, and fun: {userPrompt}";

            try
            {
                return await provider.GetResponseAsync(fullPrompt, apiKey);
            }
            catch (Exception ex)
            {
                return $"Ugh, my brain hurts... (Error: {ex.Message})";
            }
        }
    }
}