using System.Text.Json.Serialization;

namespace SlimeHelper
{
    public class NotebookRoot
    {
        [JsonPropertyName("cells")]
        public List<NotebookCell> Cells { get; set; } = new List<NotebookCell>();

        [JsonPropertyName("metadata")]
        public object Metadata { get; set; } = new object();

        [JsonPropertyName("nbformat")]
        public int NbFormat { get; set; } = 4;

        [JsonPropertyName("nbformat_minor")]
        public int NbFormatMinor { get; set; } = 2;
    }
}
