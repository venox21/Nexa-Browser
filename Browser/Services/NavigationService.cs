using System.IO;
using System.Text.RegularExpressions;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Determines whether input is a URL or search query and builds the final navigation URI.
    /// Also handles the local start page.
    /// </summary>
    public static partial class NavigationService
    {
        [GeneratedRegex(@"^([a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}(/.*)?$")]
        private static partial Regex DomainPattern();

        [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*://")]
        private static partial Regex ProtocolPattern();

        [GeneratedRegex(@"^localhost(:\d+)?(/.*)?$")]
        private static partial Regex LocalhostPattern();

        [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(:\d+)?(/.*)?$")]
        private static partial Regex IpAddressPattern();

        /// <summary>
        /// Resolves user input to a navigable URI.
        /// </summary>
        public static string ResolveInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            input = input.Trim();

            // Internal pages
            if (input.Equals("about:start", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("about:home", StringComparison.OrdinalIgnoreCase))
                return "about:start";

            if (input.Equals("about:settings", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("about:preferences", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("chrome://settings", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("edge://settings", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("nexa://settings", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("settings", StringComparison.OrdinalIgnoreCase))
                return "about:settings";

            if (input.Equals("about:passwords", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("about:vault", StringComparison.OrdinalIgnoreCase))
                return "about:passwords";

            if (input.Equals("about:sync", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("about:account", StringComparison.OrdinalIgnoreCase))
                return "about:sync";

            // Search shortcut prefixes (e.g. 'y query' -> YouTube, 'gh query' -> GitHub)
            if (TryGetSearchShortcut(input, out var shortcutUrl, out _, out _))
                return shortcutUrl;

            if (ProtocolPattern().IsMatch(input))
                return input;

            if (DomainPattern().IsMatch(input))
                return $"https://{input}";

            if (LocalhostPattern().IsMatch(input))
                return $"http://{input}";

            if (IpAddressPattern().IsMatch(input))
                return $"http://{input}";

            return string.Format(SettingsService.Instance.SearchEngineUrl, Uri.EscapeDataString(input));
        }

        private static string? _cachedLogoDataUri;

        /// <summary>
        /// Gets the base64 data URI of the Nexa logo icon.
        /// </summary>
        public static string GetLogoDataUri()
        {
            if (_cachedLogoDataUri != null) return _cachedLogoDataUri;

            // 1. Try embedded pack URIs (assembly-qualified first)
            string[] packUris = new[]
            {
                "pack://application:,,,/Nexa;component/Assets/nexa_icon.png",
                "pack://application:,,,/Assets/nexa_icon.png"
            };

            foreach (var packUri in packUris)
            {
                try
                {
                    var uri = new Uri(packUri, UriKind.Absolute);
                    var streamInfo = System.Windows.Application.GetResourceStream(uri);
                    if (streamInfo != null)
                    {
                        using var ms = new MemoryStream();
                        streamInfo.Stream.CopyTo(ms);
                        _cachedLogoDataUri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                        return _cachedLogoDataUri;
                    }
                }
                catch { }
            }

            // 2. Try disk paths
            string[] diskPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "nexa_icon.png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "nexa_icon.png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nexa_icon.png")
            };

            foreach (var path in diskPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        _cachedLogoDataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
                        return _cachedLogoDataUri;
                    }
                }
                catch { }
            }

            return "";
        }

        /// <summary>
        /// Gets the start page HTML with branding placeholders replaced.
        /// </summary>
        public static string GetStartPageHtml()
        {
            string? html = null;

            // 1. Try embedded resource (pack URI with assembly name)
            string[] packUris = new[]
            {
                "pack://application:,,,/Nexa;component/Assets/startpage.html",
                "pack://application:,,,/Assets/startpage.html"
            };

            foreach (var packUri in packUris)
            {
                try
                {
                    var uri = new Uri(packUri, UriKind.Absolute);
                    var streamInfo = System.Windows.Application.GetResourceStream(uri);
                    if (streamInfo != null)
                    {
                        using var reader = new StreamReader(streamInfo.Stream);
                        html = reader.ReadToEnd();
                        if (!string.IsNullOrEmpty(html))
                            break;
                    }
                }
                catch { }
            }

            // 2. Try file from disk alongside exe or in project assets
            if (string.IsNullOrEmpty(html))
            {
                string[] diskPaths = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "startpage.html"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "startpage.html"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startpage.html")
                };

                foreach (var path in diskPaths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            html = File.ReadAllText(path);
                            if (!string.IsNullOrEmpty(html))
                                break;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(html))
                return GetFallbackStartPageHtml();

            html = html.Replace("{{BROWSER_NAME}}", BrandingConfig.BrowserName);
            html = html.Replace("{{SEARCH_URL}}", SettingsService.Instance.SearchEngineUrl);
            html = html.Replace("{{SPEED_DIAL_TILES}}", SpeedDialService.Instance.GetTilesJson());
            html = html.Replace("{{LOGO_DATA_URI}}", GetLogoDataUri());
            return html;
        }

        /// <summary>
        /// Checks if the given URL is the start page.
        /// </summary>
        public static bool IsStartPage(string url)
        {
            return url.Equals("about:start", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the given URL is the internal settings page.
        /// </summary>
        public static bool IsSettingsPage(string url)
        {
            return url.Equals("about:settings", StringComparison.OrdinalIgnoreCase) ||
                   url.Equals("about:passwords", StringComparison.OrdinalIgnoreCase) ||
                   url.Equals("about:sync", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts a clean display URL.
        /// </summary>
        public static string GetDisplayUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            if (IsStartPage(url))
                return string.Empty;

            if (IsSettingsPage(url))
                return url;

            return url;
        }

        /// <summary>
        /// Tries to resolve a prefix search shortcut (e.g. 'y cats' -> YouTube search).
        /// </summary>
        public static bool TryGetSearchShortcut(string input, out string targetUrl, out string providerName, out string query)
        {
            targetUrl = string.Empty;
            providerName = string.Empty;
            query = string.Empty;

            if (string.IsNullOrWhiteSpace(input)) return false;

            int spaceIdx = input.IndexOf(' ');
            if (spaceIdx <= 0) return false;

            string prefix = input.Substring(0, spaceIdx).ToLowerInvariant();
            query = input.Substring(spaceIdx + 1).Trim();
            if (string.IsNullOrWhiteSpace(query)) return false;

            string encoded = Uri.EscapeDataString(query);

            switch (prefix)
            {
                case "y":
                case "yt":
                    providerName = "YouTube";
                    targetUrl = $"https://www.youtube.com/results?search_query={encoded}";
                    return true;
                case "w":
                case "wiki":
                    providerName = "Wikipedia";
                    targetUrl = $"https://de.wikipedia.org/wiki/Special:Search?search={encoded}";
                    return true;
                case "gh":
                    providerName = "GitHub";
                    targetUrl = $"https://github.com/search?q={encoded}";
                    return true;
                case "r":
                    providerName = "Reddit";
                    targetUrl = $"https://www.reddit.com/search/?q={encoded}";
                    return true;
                case "g":
                    providerName = "Google";
                    targetUrl = $"https://www.google.com/search?q={encoded}";
                    return true;
                case "b":
                    providerName = "Bing";
                    targetUrl = $"https://www.bing.com/search?q={encoded}";
                    return true;
                case "d":
                case "ddg":
                    providerName = "DuckDuckGo";
                    targetUrl = $"https://duckduckgo.com/?q={encoded}";
                    return true;
                case "e":
                    providerName = "Ecosia";
                    targetUrl = $"https://www.ecosia.org/search?q={encoded}";
                    return true;
                case "a":
                case "amz":
                    providerName = "Amazon";
                    targetUrl = $"https://www.amazon.de/s?k={encoded}";
                    return true;
                default:
                    return false;
            }
        }

        private static string GetFallbackStartPageHtml()
        {
            var searchUrl = SettingsService.Instance.SearchEngineUrl;
            return $@"
<!DOCTYPE html>
<html lang=""de"">
<head>
  <meta charset=""UTF-8"" />
  <title>Neuer Tab</title>
  <style>
    * {{ margin: 0; padding: 0; box-sizing: border-box; }}
    body {{
      background: radial-gradient(circle at 50% 30%, #16182e 0%, #080812 100%);
      color: #e2e8f0;
      font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 20px;
      user-select: none;
    }}
    .brand {{
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 24px;
    }}
    .logo-orb {{
      width: 44px;
      height: 44px;
      border-radius: 12px;
      background: linear-gradient(135deg, #6366f1, #8b5cf6);
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 22px;
      font-weight: 800;
      color: #fff;
      box-shadow: 0 8px 24px rgba(99, 102, 241, 0.4);
    }}
    .brand-title {{
      font-size: 26px;
      font-weight: 700;
      letter-spacing: 0.5px;
      background: linear-gradient(135deg, #ffffff, #a5b4fc);
      -webkit-background-clip: text;
      -webkit-text-fill-color: transparent;
    }}
    .clock {{
      font-size: 56px;
      font-weight: 300;
      letter-spacing: -1px;
      color: #f8fafc;
      margin-bottom: 24px;
    }}
    .search-box {{
      position: relative;
      width: 100%;
      max-width: 560px;
    }}
    .search-input {{
      width: 100%;
      padding: 14px 20px 14px 44px;
      background: rgba(255, 255, 255, 0.05);
      border: 1px solid rgba(255, 255, 255, 0.12);
      border-radius: 28px;
      color: #fff;
      font-size: 15px;
      outline: none;
      box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
      transition: all 0.2s ease;
    }}
    .search-input:focus {{
      border-color: #6366f1;
      box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.25), 0 8px 24px rgba(0,0,0,0.4);
      background: rgba(255, 255, 255, 0.08);
    }}
    .search-icon {{
      position: absolute;
      left: 16px;
      top: 50%;
      transform: translateY(-50%);
      font-size: 16px;
      color: #94a3b8;
    }}
  </style>
</head>
<body>
  <div class=""brand"">
    <div class=""logo-orb"">N</div>
    <div class=""brand-title"">{BrandingConfig.BrowserName}</div>
  </div>
  <div class=""clock"" id=""clk"">12:00</div>
  <div class=""search-box"">
    <span class=""search-icon"">🔍</span>
    <input type=""text"" id=""inp"" class=""search-input"" placeholder=""Suchen oder Webadresse eingeben..."" autofocus />
  </div>
  <script>
    function updateClock() {{
      const d = new Date();
      document.getElementById('clk').innerText = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    }}
    updateClock();
    setInterval(updateClock, 1000);
    const inp = document.getElementById('inp');
    inp.addEventListener('keydown', e => {{
      if (e.key === 'Enter') {{
        const q = inp.value.trim();
        if (!q) return;
        const target = (q.startsWith('http://') || q.startsWith('https://') || q.includes('.')) 
          ? (q.includes('://') ? q : 'https://' + q) 
          : '{searchUrl}'.replace('{{0}}', encodeURIComponent(q));
        if (window.chrome && window.chrome.webview) {{
          window.chrome.webview.postMessage({{ action: 'navigate', url: target }});
        }} else {{
          window.location.href = target;
        }}
      }}
    }});
  </script>
</body>
</html>";
        }
    }
}
