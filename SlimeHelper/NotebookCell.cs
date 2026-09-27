using System.Text.Json.Serialization;

namespace SlimeHelper
{
    public class NotebookCell
    {
        [JsonPropertyName("cell_type")]
        public string CellType { get; set; } = string.Empty; // Fixat här!

        [JsonPropertyName("metadata")]
        public object Metadata { get; set; } = new object();

        [JsonPropertyName("source")]
        public List<string> Source { get; set; } = new List<string>();

        [JsonPropertyName("outputs")]
        public List<object> Outputs { get; set; } = new List<object>();
    }
}