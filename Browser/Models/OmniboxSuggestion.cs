using System.Windows.Media;

namespace Browser.Models
{
    public enum SuggestionType
    {
        History,
        Bookmark,
        SearchShortcut,
        WebSearch,
        AiQuickAnswer
    }

    public class OmniboxSuggestion
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string DisplayUrl { get; set; } = string.Empty;
        public SuggestionType Type { get; set; }
        public string IconGlyph { get; set; } = "\uE721";
        public string? Badge { get; set; }
        public Brush? BadgeBrush { get; set; }
        public bool HasBadge => !string.IsNullOrEmpty(Badge);
        public System.Windows.Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        public bool IsAiQuickAnswer => Type == SuggestionType.AiQuickAnswer;
        public System.Windows.Visibility AiVisibility => IsAiQuickAnswer ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility StandardVisibility => IsAiQuickAnswer ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
}
