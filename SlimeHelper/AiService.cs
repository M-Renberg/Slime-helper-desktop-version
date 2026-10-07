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

            // Personan är kvar (kvick och lite snarkig), men själva innehållet hon ombeds ge (fillistor,
            // filinnehåll, kod) ska alltid levereras i sin helhet. Det är bara småpratet som ska vara kort.
            const string persona =
                "You are a witty, slightly snarky Slime assistant for a software developer. Stay in character, keep your chit-chat short and fun, " +
                "but ALWAYS deliver the information the user actually asked for in full (file lists, file contents, code, explanations). " +
                "Never replace requested content with a remark that you looked it up or listed it. " +
                "Never write lines that begin with \"System:\", \"Slime:\" or \"User:\" - the application adds those itself.";

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
    }
}