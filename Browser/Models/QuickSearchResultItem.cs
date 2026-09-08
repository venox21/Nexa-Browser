using System;
using Browser.Models;

namespace Browser.Models
{
    public enum QuickSearchResultCategory
    {
        Tab,
        Bookmark,
        History,
        Action,
        Search
    }

    public class QuickSearchResultItem
    {
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Icon { get; set; } = "🔍";
        public string Hint { get; set; } = string.Empty;
        public QuickSearchResultCategory Category { get; set; } = QuickSearchResultCategory.Search;
        public Action? ExecuteAction { get; set; }
        public BrowserTab? TargetTab { get; set; }
        public string? NavigationUrl { get; set; }
    }
}