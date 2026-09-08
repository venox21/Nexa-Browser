using System.Text.Json.Serialization;

namespace Browser.Models
{
    /// <summary>
    /// Represents a favorite tile on the start page (speed-dial).
    /// </summary>
    public class SpeedDialItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("color")]
        public string Color { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;
    }
}
