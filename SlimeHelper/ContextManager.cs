namespace SlimeHelper
{
    public static class ContextManager
    {
        public static string BuildPromptWithContext(string userPrompt, SlimeSettings settings)
        {
            var todos = ObsidianService.GetUnfinishedTodos(settings.ObsidianVaultPath);
            string todoContext = todos.Count > 0
                ? $"Current unfinished tasks from Obsidian: {string.Join(", ", todos)}."
                : "No active tasks found.";

            return $"Context: {todoContext}\n\nUser says: {userPrompt}";
        }
    }
}
