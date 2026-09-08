using Browser.Models;
using Browser.Services.AI;
using Microsoft.Web.WebView2.Wpf;

namespace Browser.Services.AI
{
    /// <summary>
    /// Collects browser context information to attach to AI chat messages.
    /// Each context type can be individually enabled/disabled.
    /// The user always sees which context is active via UI badges.
    /// </summary>
    public class BrowserContextProvider
    {
        /// <summary>
        /// Represents collected browser context with visibility into what was gathered.
        /// </summary>
        public class BrowserContext
        {
            public string? CurrentUrl { get; set; }
            public string? PageTitle { get; set; }
            public string? SelectedText { get; set; }
            public string? PageContent { get; set; }
            public List<(string Title, string Url)>? OpenTabs { get; set; }

            /// <summary>
            /// Returns a human-readable summary of what context was included.
            /// </summary>
            public string GetContextSummary()
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(CurrentUrl)) parts.Add("URL");
                if (!string.IsNullOrEmpty(PageTitle)) parts.Add("Titel");
                if (!string.IsNullOrEmpty(SelectedText)) parts.Add("Markierter Text");
                if (!string.IsNullOrEmpty(PageContent)) parts.Add("Seiteninhalt");
                if (OpenTabs?.Count > 0) parts.Add($"{OpenTabs.Count} Tabs");
                return parts.Count > 0 ? string.Join(" · ", parts) : "";
            }

            /// <summary>
            /// Formats the context as a text block suitable for inclusion in a system/user message.
            /// </summary>
            public string FormatForPrompt()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[Browser-Kontext]");

                if (!string.IsNullOrEmpty(CurrentUrl))
                    sb.AppendLine($"URL: {CurrentUrl}");

                if (!string.IsNullOrEmpty(PageTitle))
                    sb.AppendLine($"Seitentitel: {PageTitle}");

                if (!string.IsNullOrEmpty(SelectedText))
                {
                    sb.AppendLine("Markierter Text:");
                    sb.AppendLine(SelectedText);
                }

                if (OpenTabs?.Count > 0)
                {
                    sb.AppendLine("Geöffnete Tabs:");
                    foreach (var (title, url) in OpenTabs)
                        sb.AppendLine($"  - {title} ({url})");
                }

                if (!string.IsNullOrEmpty(PageContent))
                {
                    // Truncate page content to a reasonable length
                    var truncated = PageContent.Length > 8000
                        ? PageContent[..8000] + "\n[... Inhalt gekürzt ...]"
                        : PageContent;
                    sb.AppendLine("Seiteninhalt:");
                    sb.AppendLine(truncated);
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Collects the browser context based on the current settings and active tab.
        /// </summary>
        /// <param name="settings">AI settings with context permissions.</param>
        /// <param name="activeTabUrl">URL of the active tab.</param>
        /// <param name="activeTabTitle">Title of the active tab.</param>
        /// <param name="activeWebView">The WebView2 control of the active tab (for JS injection).</param>
        /// <param name="allTabs">All open tabs for the tab list context.</param>
        public async Task<BrowserContext> CollectContextAsync(
            AiSettingsModel settings,
            string? activeTabUrl,
            string? activeTabTitle,
            WebView2? activeWebView,
            IEnumerable<(string Title, string Url)>? allTabs)
        {
            var context = new BrowserContext();

            if (settings.ContextUrl && !string.IsNullOrEmpty(activeTabUrl))
                context.CurrentUrl = activeTabUrl;

            if (settings.ContextTitle && !string.IsNullOrEmpty(activeTabTitle))
                context.PageTitle = activeTabTitle;

            if (settings.ContextSelection && activeWebView?.CoreWebView2 != null)
            {
                try
                {
                    var selection = await activeWebView.CoreWebView2.ExecuteScriptAsync(
                        "window.getSelection().toString()");
                    // ExecuteScript returns a JSON-encoded string, so we need to unwrap it
                    if (!string.IsNullOrEmpty(selection) && selection != "\"\"" && selection != "null")
                    {
                        context.SelectedText = System.Text.Json.JsonSerializer.Deserialize<string>(selection);
                    }
                }
                catch { /* WebView may not be initialized */ }
            }

            if (settings.ContextPage && activeWebView?.CoreWebView2 != null)
            {
                try
                {
                    var pageText = await activeWebView.CoreWebView2.ExecuteScriptAsync(
                        "document.body.innerText");
                    if (!string.IsNullOrEmpty(pageText) && pageText != "\"\"" && pageText != "null")
                    {
                        context.PageContent = System.Text.Json.JsonSerializer.Deserialize<string>(pageText);
                    }
                }
                catch { /* WebView may not be initialized */ }
            }

            if (settings.ContextTabs && allTabs != null)
            {
                context.OpenTabs = allTabs.ToList();
            }

            return context;
        }
    }
}
