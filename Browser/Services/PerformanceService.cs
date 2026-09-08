using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Browser.Services
{
    /// <summary>
    /// Turbo-performance service for the Nexa Browser:
    /// - Background asynchronous DNS pre-resolving (DNS pre-warming)
    /// - Link hover prefetch (DNS resolve on mouseover for instant navigation)
    /// - CDN preconnect hints injection
    /// - Omnibox preconnect (DNS prefetch while typing in address bar)
    /// - Intelligent memory trimming on idle
    /// </summary>
    public class PerformanceService
    {
        private static readonly Lazy<PerformanceService> _lazyInstance = new(() => new PerformanceService());
        public static PerformanceService Instance => _lazyInstance.Value;

        private readonly ConcurrentDictionary<string, byte> _resolvedHosts = new(StringComparer.OrdinalIgnoreCase);

        // Top domains pre-warmed on browser startup
        private static readonly string[] DefaultPrewarmDomains = new[]
        {
            "google.com",
            "www.google.com",
            "youtube.com",
            "www.youtube.com",
            "wikipedia.org",
            "github.com",
            "reddit.com",
            "duckduckgo.com",
            "facebook.com",
            "twitter.com",
            "x.com",
            "amazon.de",
            "stackoverflow.com",
            "chatgpt.com",
            "linkedin.com"
        };

        // Common CDN/resource domains to preconnect on every page
        private static readonly string[] CommonCdnDomains = new[]
        {
            "fonts.googleapis.com",
            "fonts.gstatic.com",
            "cdn.jsdelivr.net",
            "cdnjs.cloudflare.com",
            "ajax.googleapis.com",
            "unpkg.com",
            "www.gstatic.com",
            "i.ytimg.com"
        };

        private PerformanceService()
        {
        }

        /// <summary>
        /// Starts asynchronous pre-warming of frequent domains on startup.
        /// All domains are resolved in parallel for maximum speed.
        /// </summary>
        public void PrewarmCommonDomains()
        {
            Task.Run(async () =>
            {
                // Resolve all domains in parallel instead of sequentially
                var tasks = DefaultPrewarmDomains.Select(d => PrewarmHostAsync(d));
                await Task.WhenAll(tasks);
            });
        }

        /// <summary>
        /// Pre-resolves DNS for a given host or URL so the subsequent navigation has 0ms DNS latency.
        /// </summary>
        public void PrewarmUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var host = uri.Host;
                if (!string.IsNullOrEmpty(host) && !_resolvedHosts.ContainsKey(host))
                {
                    Task.Run(() => PrewarmHostAsync(host));
                }
            }
        }

        /// <summary>
        /// Pre-resolves DNS for a raw input string (from the address bar).
        /// Extracts the domain from partial URLs or domain-like text and prefetches DNS.
        /// Called while the user is typing to prepare for Enter.
        /// </summary>
        public void PreconnectFromInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length < 4) return;

            string? host = null;

            // If it looks like a URL, extract the host
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }
            // If it looks like a domain (contains a dot, no spaces)
            else if (input.Contains('.') && !input.Contains(' '))
            {
                host = input.Trim().TrimEnd('/');
                // Remove path if present
                int slashIdx = host.IndexOf('/');
                if (slashIdx > 0) host = host.Substring(0, slashIdx);
            }

            if (!string.IsNullOrEmpty(host) && !_resolvedHosts.ContainsKey(host))
            {
                Task.Run(() => PrewarmHostAsync(host));
            }
        }

        /// <summary>
        /// Batch-prefetches DNS for multiple hostnames extracted from page links.
        /// Used after NavigationCompleted to prefetch outgoing link targets.
        /// </summary>
        public void PrewarmHostsBatch(IEnumerable<string> hosts)
        {
            Task.Run(async () =>
            {
                var tasks = hosts
                    .Where(h => !string.IsNullOrEmpty(h) && !_resolvedHosts.ContainsKey(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20) // Limit to 20 to avoid flooding DNS
                    .Select(h => PrewarmHostAsync(h));
                await Task.WhenAll(tasks);
            });
        }

        private async Task PrewarmHostAsync(string host)
        {
            if (_resolvedHosts.ContainsKey(host)) return;

            try
            {
                // Trigger Windows DNS client cache
                await Dns.GetHostAddressesAsync(host);
                _resolvedHosts.TryAdd(host, 1);
            }
            catch
            {
                // Silently ignore DNS pre-resolve errors
            }
        }

        /// <summary>
        /// Gets the JavaScript to inject on every page that prefetches DNS for links
        /// when the user hovers over them. This gives ~200-400ms head start before click.
        /// </summary>
        public static string GetLinkHoverPrefetchScript()
        {
            return @"
(function() {
    if (window.__nexa_link_prefetch_installed__) return;
    window.__nexa_link_prefetch_installed__ = true;

    const prefetched = new Set();
    let hoverTimer = null;

    function prefetchLink(href) {
        try {
            const url = new URL(href, window.location.origin);
            const host = url.hostname;
            if (prefetched.has(host)) return;
            if (host === window.location.hostname) return;
            prefetched.add(host);

            // Method 1: <link rel='dns-prefetch'> (works in all browsers)
            const dns = document.createElement('link');
            dns.rel = 'dns-prefetch';
            dns.href = url.protocol + '//' + host;
            document.head.appendChild(dns);

            // Method 2: <link rel='preconnect'> (opens TCP+TLS ahead of time)
            const preconn = document.createElement('link');
            preconn.rel = 'preconnect';
            preconn.href = url.protocol + '//' + host;
            preconn.crossOrigin = 'anonymous';
            document.head.appendChild(preconn);
        } catch(e) {}
    }

    // Hover-triggered prefetch with 80ms debounce
    document.addEventListener('pointerover', function(e) {
        const link = e.target.closest('a[href]');
        if (!link) return;
        const href = link.href;
        if (!href || href.startsWith('javascript:') || href.startsWith('#')) return;

        clearTimeout(hoverTimer);
        hoverTimer = setTimeout(() => prefetchLink(href), 80);
    }, { passive: true });

    // Also prefetch links visible in viewport (Intersection Observer)
    if (window.IntersectionObserver) {
        const io = new IntersectionObserver((entries) => {
            for (const entry of entries) {
                if (entry.isIntersecting && entry.target.href) {
                    prefetchLink(entry.target.href);
                    io.unobserve(entry.target);
                }
            }
        }, { rootMargin: '200px' });

        // Observe links after page settles (1s delay)
        setTimeout(() => {
            const links = document.querySelectorAll('a[href^=""http""]');
            const limit = Math.min(links.length, 30);
            for (let i = 0; i < limit; i++) {
                io.observe(links[i]);
            }
        }, 1000);
    }
})();
";
        }

        /// <summary>
        /// Gets the JavaScript that injects preconnect hints for common CDN domains
        /// so external resources (fonts, JS libraries) load faster.
        /// </summary>
        public static string GetCdnPreconnectScript()
        {
            var hints = string.Join("\n", CommonCdnDomains.Select(d =>
                $"        addHint('https://{d}');"));

            return $@"
(function() {{
    if (window.__nexa_cdn_preconnect__) return;
    window.__nexa_cdn_preconnect__ = true;

    function addHint(origin) {{
        try {{
            const link = document.createElement('link');
            link.rel = 'preconnect';
            link.href = origin;
            link.crossOrigin = 'anonymous';
            document.head.appendChild(link);
        }} catch(e) {{}}
    }}

{hints}
}})();
";
        }

        /// <summary>
        /// Trims working set memory when the browser is idle.
        /// </summary>
        public void TrimProcessMemory()
        {
            Task.Run(() =>
            {
                try
                {
                    GC.Collect(1, GCCollectionMode.Optimized, false);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
                    }
                }
                catch { }
            });
        }

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);
    }
}
