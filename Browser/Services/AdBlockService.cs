using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// High-performance Ad & Tracker Blocker service (Nexa Privacy Shield).
    /// Filters known ad networks, trackers, and telemetry scripts.
    /// </summary>
    public class AdBlockService
    {
        private static readonly Lazy<AdBlockService> _lazyInstance = new(() => new AdBlockService());
        public static AdBlockService Instance => _lazyInstance.Value;

        private FrozenSet<string> _blockedDomains = FrozenSet<string>.Empty;
        private readonly HashSet<string> _whitelistedHosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _tabBlockedCounts = new();
        private readonly string _whitelistPath;

        public event Action<string, int>? BlockedCountChanged;

        private AdBlockService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, BrandingConfig.BrowserName);
            Directory.CreateDirectory(folder);
            _whitelistPath = Path.Combine(folder, "adblock_whitelist.json");

            LoadDefaultBlockedDomains();
            LoadWhitelist();
        }

        public bool IsEnabled => SettingsService.Instance.EnableAdBlocker;

        /// <summary>
        /// Checks whether a network request URL should be blocked.
        /// Uses zero-allocation Span-based lookups for maximum throughput.
        /// </summary>
        public bool ShouldBlock(string requestUrl, string? pageHost = null)
        {
            if (!IsEnabled) return false;

            if (!string.IsNullOrEmpty(pageHost) && _whitelistedHosts.Contains(pageHost))
                return false;

            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
                return false;

            var host = uri.Host;

            // Never block the main YouTube video playback streams
            if (host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Contains("/videoplayback"))
            {
                return false;
            }

            // Direct or parent domain match (FrozenSet O(1) lookup)
            if (IsDomainBlocked(host))
                return true;

            // YouTube specific ad and tracking requests (Span-based path checks)
            if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> pathSpan = uri.AbsolutePath.AsSpan();
                if (pathSpan.Contains("/pagead/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    pathSpan.Contains("/api/stats/ads".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    pathSpan.Contains("/ptracking".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    pathSpan.Contains("/youtubei/v1/player/ad_break".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    pathSpan.Contains("/ad_break".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Common URL path patterns for ad servers (Span-based, no ToLowerInvariant allocation)
            ReadOnlySpan<char> pathCheck = uri.AbsolutePath.AsSpan();
            if (pathCheck.Contains("/pagead/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/adservice/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/adsystem/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/adserver/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/ads/banner/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/tracking.js".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/analytics.js".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/gtm.js".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                pathCheck.Contains("/gtag/js".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the high-speed YouTube AdBlocker & in-stream ad skipper script.
        /// Injected into CoreWebView2 on document creation.
        /// </summary>
        public static string GetYouTubeAdBlockScript()
        {
            return @"
(function() {
    if (!window.location.hostname.includes('youtube.com')) return;

    // 1. Inject CSS to hide all ad elements, banners, and anti-adblock overlays
    const styleId = '__nexa_yt_adblock_css__';
    if (!document.getElementById(styleId)) {
        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = `
            /* Hide banner & feed ads */
            ytd-promoted-sparkles-web-renderer,
            ytd-promoted-video-renderer,
            ytd-in-feed-ad-layout-renderer,
            ytd-banner-promo-renderer,
            ytd-ad-slot-renderer,
            ytd-statement-banner-renderer,
            ytd-rich-item-renderer:has(ytd-ad-slot-renderer),
            ytd-rich-item-renderer:has(ytd-in-feed-ad-layout-renderer),
            #player-ads,
            .ytp-ad-module,
            .ytp-ad-overlay-container,
            .ytp-ad-overlay-slot,
            .ytp-ad-message-container,
            .ytp-ad-progress-list,
            ytd-action-companion-ad-renderer,
            #masthead-ad,
            #panels:has(ytd-ads-engagement-panel-content-renderer),
            /* Anti-adblock modal overlay */
            tp-yt-paper-dialog:has(ytd-enforcement-message-view-model),
            ytd-enforcement-message-view-model,
            tp-yt-iron-overlay-backdrop {
                display: none !important;
                visibility: hidden !important;
                opacity: 0 !important;
                pointer-events: none !important;
                height: 0 !important;
            }

            /* Guarantee YouTube masthead (Search bar, Logo, Profile) is always visible */
            #masthead-container,
            ytd-masthead,
            #masthead {
                display: flex !important;
                visibility: visible !important;
                opacity: 1 !important;
                pointer-events: auto !important;
            }
        `;
        (document.head || document.documentElement).appendChild(style);
    }

    let wasAdShowing = false;
    let savedVolume = 1.0;
    let savedMuted = false;

    // 2. Video Ad Fast-Forward & Auto-Skip Engine
    function checkAndSkipAds() {
        const video = document.querySelector('video');
        const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');

        // Check if an ad is currently playing
        const isAdShowing = player && (
            player.classList.contains('ad-showing') ||
            player.classList.contains('ad-interrupting') ||
            document.querySelector('.ytp-ad-player-overlay') ||
            document.querySelector('.ytp-ad-text') ||
            document.querySelector('.ytp-ad-preview-container') ||
            document.querySelector('.ytp-ad-skip-button-slot')
        );

        if (isAdShowing) {
            if (!wasAdShowing && video) {
                savedMuted = video.muted;
                savedVolume = video.volume;
                video.muted = true;
            }
            wasAdShowing = true;

            // Try player internal skipAd API
            try {
                if (player && typeof player.skipAd === 'function') {
                    player.skipAd();
                }
            } catch (e) {}

            // Fast forward video to end
            if (video) {
                try {
                    video.muted = true;
                    video.playbackRate = 16.0;
                    if (isFinite(video.duration) && video.duration > 0) {
                        video.currentTime = video.duration;
                    }
                } catch (e) {}
            }

            // Click any available skip button
            const skipSelectors = [
                '.ytp-ad-skip-button',
                '.ytp-ad-skip-button-modern',
                '.ytp-skip-ad-button',
                '.ytp-ad-skip-button-container button',
                '.ytp-ad-overlay-close-button',
                'button.ytp-ad-skip-button-modern',
                '.ytp-ad-skip-button-slot button',
                '.videoAdUiSkipButton'
            ];

            for (const sel of skipSelectors) {
                const btn = document.querySelector(sel);
                if (btn && typeof btn.click === 'function') {
                    btn.click();
                    break;
                }
            }
        } else if (wasAdShowing) {
            // Ad just finished, restore playback speed and sound
            wasAdShowing = false;
            if (video) {
                try {
                    video.playbackRate = 1.0;
                    video.muted = savedMuted;
                    if (!savedMuted && video.volume === 0 && savedVolume > 0) {
                        video.volume = savedVolume;
                    }
                    if (video.paused) {
                        video.play().catch(() => {});
                    }
                } catch (e) {}
            }
        }

        // 3. Dismiss Anti-Adblock Popup & Resume Playback
        const antiAdDialog = document.querySelector('ytd-enforcement-message-view-model, tp-yt-paper-dialog:has(ytd-enforcement-message-view-model)');
        if (antiAdDialog) {
            const closeBtn = antiAdDialog.querySelector('#dismiss-button, button');
            if (closeBtn) closeBtn.click();
            antiAdDialog.remove();

            const backdrops = document.querySelectorAll('tp-yt-iron-overlay-backdrop');
            backdrops.forEach(b => b.remove());

            if (video && video.paused) {
                video.play().catch(() => {});
            }
        }
    }

    // Run periodic check
    setInterval(checkAndSkipAds, 80);

    // Also observe DOM mutations for zero-latency response
    const observer = new MutationObserver(() => {
        checkAndSkipAds();
    });

    if (document.documentElement) {
        observer.observe(document.documentElement, {
            childList: true,
            subtree: true
        });
    } else {
        document.addEventListener('DOMContentLoaded', () => {
            observer.observe(document.documentElement, {
                childList: true,
                subtree: true
            });
        });
    }

    window.addEventListener('yt-navigate-finish', checkAndSkipAds);
})();
";
        }

        private bool IsDomainBlocked(string host)
        {
            // Direct match (FrozenSet O(1) lookup, no allocation)
            if (_blockedDomains.Contains(host)) return true;

            // Walk parent domains
            int dotIndex = host.IndexOf('.');
            while (dotIndex > 0 && dotIndex < host.Length - 1)
            {
                var parent = host.Substring(dotIndex + 1);
                if (_blockedDomains.Contains(parent))
                    return true;

                int nextDot = parent.IndexOf('.');
                if (nextDot < 0) break;
                dotIndex += 1 + nextDot;
            }

            return false;
        }

        public void IncrementBlocked(string tabId)
        {
            int count = _tabBlockedCounts.AddOrUpdate(tabId, 1, (_, current) => current + 1);
            BlockedCountChanged?.Invoke(tabId, count);
        }

        public int GetBlockedCount(string tabId)
        {
            return _tabBlockedCounts.TryGetValue(tabId, out int count) ? count : 0;
        }

        public void ResetTab(string tabId)
        {
            _tabBlockedCounts.TryRemove(tabId, out _);
            BlockedCountChanged?.Invoke(tabId, 0);
        }

        public bool IsWhitelisted(string? host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            return _whitelistedHosts.Contains(host);
        }

        public void SetWhitelisted(string host, bool whitelisted)
        {
            if (string.IsNullOrWhiteSpace(host)) return;

            if (whitelisted)
                _whitelistedHosts.Add(host);
            else
                _whitelistedHosts.Remove(host);

            SaveWhitelist();
        }

        /// <summary>
        /// Gets the combined global protection script (Cosmetic Filter, Anti-Adblock Defuser,
        /// Cookie Banner Auto-Dismiss, and YouTube Ad Skipper).
        /// </summary>
        public static string GetGlobalProtectionScript()
        {
            return $@"
// ── NEXA PRIVACY SHIELD v2 ENGINE ──
(function() {{
    {GetAntiAdblockDefuserScript()}
    {GetCosmeticFilterScript()}
    {GetCookieBannerBlockerScript()}
    {GetYouTubeAdBlockScript()}
    {GetWebRtcLeakProtectionScript()}
}})();
";
        }

        /// <summary>
        /// Prevents WebRTC STUN/TURN queries from leaking local LAN IP addresses (192.168.x, 10.x, etc.).
        /// </summary>
        public static string GetWebRtcLeakProtectionScript()
        {
            return @"
    // 5. Nexa WebRTC Leak Protection Guard
    try {
        if (window.RTCPeerConnection && !window.__nexa_webrtc_protected__) {
            window.__nexa_webrtc_protected__ = true;
            const OrigPeerConn = window.RTCPeerConnection;
            window.RTCPeerConnection = function(config, constraints) {
                const pc = new OrigPeerConn(config, constraints);
                if (pc.addEventListener) {
                    pc.addEventListener('icecandidate', function(e) {
                        if (e.candidate && e.candidate.candidate) {
                            if (/(192\.168\.|10\.\d+\.|172\.(1[6-9]|2\d|3[01])\.|\.local)/.test(e.candidate.candidate)) {
                                e.stopImmediatePropagation();
                            }
                        }
                    }, true);
                }
                return pc;
            };
            window.RTCPeerConnection.prototype = OrigPeerConn.prototype;
        }
    } catch(webrtcErr) {}
";
        }

        /// <summary>
        /// Injects universal CSS rules that hide ad banners, empty placeholders, and layout boxes.
        /// </summary>
        public static string GetCosmeticFilterScript()
        {
            return @"
    // 1. Cosmetic Element Hiding (Anti-Banner & Layout Collapsing)
    try {
        // YouTube has its own dedicated DOM cleaner (GetYouTubeAdBlockScript)
        if (window.location.hostname.includes('youtube.com')) return;

        const cssStyleId = '__nexa_cosmetic_adblock__';
        if (!document.getElementById(cssStyleId)) {
            const style = document.createElement('style');
            style.id = cssStyleId;
            style.textContent = `
                /* Google Ads & DFP */
                ins.adsbygoogle,
                [id^=""google_ads_""],
                [id^=""div-gpt-ad""],
                iframe[id^=""google_ads_iframe""],
                iframe[src*=""doubleclick.net""],
                iframe[src*=""googlesyndication.com""],
                /* Generic Ad Banners & Boxes (precise class and ID matching) */
                .ad-slot, .ad-banner, .ad-unit, .ad-box, .ad-wrapper, .ad-container,
                .ads-container, .ad-placement, .ad_wrapper, .advertisement,
                .ad-leaderboard, .ad-skyscraper, .ad-rectangle, .ad-module, .ad-placeholder,
                .ad-footer, .ad-sidebar, .ad-inline, .ad-native,
                .sponsored-post, .sponsored-content, .sponsored-ad,
                #ad-slot, #ad-container, #banner-ad, #ad-wrapper,
                /* Content Recommendation Widgets */
                .trc_related_container, .trc_rbox_container, .OUTBRAIN,
                .taboola, .taboola-placeholder, [data-widget-id*=""taboola""],
                .ligatus-container, .plistaList, .criteo-ad,
                [data-ad-slot], [data-ad-unit], [data-ad],
                /* Sticky & Interstitial Banners */
                .sticky-ad, .bottom-ad, .floating-ad, .ad-interstitial {
                    display: none !important;
                    visibility: hidden !important;
                    opacity: 0 !important;
                    pointer-events: none !important;
                    height: 0 !important;
                    max-height: 0 !important;
                    margin: 0 !important;
                    padding: 0 !important;
                    border: none !important;
                }
            `;
            (document.head || document.documentElement).appendChild(style);
        }
    } catch(e) {}
";
        }

        /// <summary>
        /// Neutralizes anti-adblock detection scripts and removes blocker wall overlays.
        /// </summary>
        public static string GetAntiAdblockDefuserScript()
        {
            return @"
    // 2. Anti-Adblock Defuser
    try {
        window.canRunAds = true;
        window.isAdblockActive = false;
        window.adblocker = false;
        window.hasAdBlocker = false;
        window.adBlockDetected = false;

        // Dummy Google AdSense object
        if (!window.adsbygoogle) {
            const dummy = [];
            dummy.loaded = true;
            dummy.push = function() { return 0; };
            window.adsbygoogle = dummy;
        }

        // Neutralize common anti-adblock detection libraries (FuckAdBlock, BlockAdBlock, etc.)
        const noop = function() { return this; };
        const dummyFab = function() {
            this.check = noop;
            this.clearEvent = noop;
            this.on = function(detected, fn) { if (!detected && typeof fn === 'function') fn(); return this; };
            this.onDetected = noop;
            this.onNotDetected = function(fn) { if (typeof fn === 'function') fn(); return this; };
            this.setOption = noop;
        };
        window.FuckAdBlock = dummyFab;
        window.BlockAdBlock = dummyFab;
        window.fuckAdBlock = new dummyFab();
        window.blockAdBlock = new dummyFab();

        // Dismiss blocker overlays and restore scrolling
        function cleanAntiAdblockModals() {
            if (window.location.hostname.includes('youtube.com')) return;

            const selectors = [
                '#adblock-modal', '.adblock-modal', '.adblock-wall',
                '.adblock-overlay', '[class*=""adblock-warning""]',
                '[id*=""adblock-overlay""]', '.sp-message-open',
                '.tp-backdrop', '.tp-modal', '[id*=""sp_message_container""]'
            ];
            for (const sel of selectors) {
                document.querySelectorAll(sel).forEach(el => el.remove());
            }
            if (document.body && document.body.style.overflow === 'hidden') {
                document.body.style.overflow = 'auto';
            }
            if (document.documentElement && document.documentElement.style.overflow === 'hidden') {
                document.documentElement.style.overflow = 'auto';
            }
        }
        setInterval(cleanAntiAdblockModals, 1000);
    } catch(e) {}
";
        }

        /// <summary>
        /// Automatically dismisses or hides annoying GDPR/Cookie consent popups ("I don't care about cookies").
        /// </summary>
        public static string GetCookieBannerBlockerScript()
        {
            return @"
    // 3. Cookie & GDPR Banner Auto-Dismiss Engine
    try {
        const cookieSelectors = [
            '#onetrust-consent-sdk', '#onetrust-banner-sdk',
            '#CybotCookiebotDialog', '#CybotCookiebotDialogBodyUnderlay',
            '#didomi-host', '#didomi-notice',
            '#usercentrics-root',
            '.qc-cmp2-container',
            '#borlabs-cookie-box', '.borlabs-cookie-preference',
            '#trustarc-banner', '.truste_box_overlay',
            '.cmplz-cookiebanner', '.klaro',
            '.cookie-banner', '#cookie-banner', '.cookie-consent', '#cookie-consent',
            '.cookie-notice', '#cookie-notice',
            '.cc-banner', '.cc-window', '#gdpr-banner', '.gdpr-banner'
        ];

        const clickTargets = [
            '#onetrust-reject-all-handler',
            '#CybotCookiebotDialogBodyButtonDecline',
            '#didomi-notice-disagree-button',
            '.qc-cmp2-summary-buttons button[mode=""secondary""]',
            'button[data-cookiefirst-action=""reject""]',
            'button[id*=""reject"" i]',
            'button[class*=""reject"" i]',
            'button[aria-label*=""ablehnen"" i]',
            'button[aria-label*=""reject"" i]',
            '#onetrust-accept-btn-handler',
            '#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll',
            '#didomi-notice-agree-button',
            '.cmplz-accept',
            'button[id*=""accept-all"" i]',
            'button[class*=""accept-all"" i]'
        ];

        function dismissCookies() {
            for (const sel of clickTargets) {
                const btn = document.querySelector(sel);
                if (btn && btn.offsetParent !== null) {
                    try { btn.click(); } catch(e) {}
                    break;
                }
            }

            for (const sel of cookieSelectors) {
                const els = document.querySelectorAll(sel);
                els.forEach(el => {
                    el.style.setProperty('display', 'none', 'important');
                    el.style.setProperty('visibility', 'hidden', 'important');
                    el.style.setProperty('pointer-events', 'none', 'important');
                });
            }

            if (document.body) {
                if (document.body.style.overflow === 'hidden' || document.body.style.position === 'fixed') {
                    document.body.style.overflow = 'auto';
                    document.body.style.position = 'static';
                }
            }
            if (document.documentElement && document.documentElement.style.overflow === 'hidden') {
                document.documentElement.style.overflow = 'auto';
            }
        }

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', dismissCookies);
        } else {
            dismissCookies();
        }

        const observer = new MutationObserver(function() {
            dismissCookies();
        });
        if (document.documentElement) {
            observer.observe(document.documentElement, { childList: true, subtree: true });
            setTimeout(() => observer.disconnect(), 10000);
        }

        // Nexa WebRTC Leak Protection Guard (Suppresses private LAN and unproxied host IP leakage)
        try {
            if (window.RTCPeerConnection) {
                const OrigPeerConn = window.RTCPeerConnection;
                window.RTCPeerConnection = function(config, constraints) {
                    const pc = new OrigPeerConn(config, constraints);
                    if (pc.addEventListener) {
                        pc.addEventListener('icecandidate', function(e) {
                            if (e.candidate && e.candidate.candidate) {
                                if (/(192\.168\.|10\.\d+\.|172\.(1[6-9]|2\d|3[01])\.|\.local)/.test(e.candidate.candidate)) {
                                    e.stopImmediatePropagation();
                                }
                            }
                        }, true);
                    }
                    return pc;
                };
                window.RTCPeerConnection.prototype = OrigPeerConn.prototype;
            }
        } catch(webrtcErr) {}
    } catch(e) {}
";
        }

        private void LoadDefaultBlockedDomains()
        {
            var list = new[]
            {
                // Google Ads, Marketing & Analytics
                "doubleclick.net",
                "googleadservices.com",
                "googlesyndication.com",
                "google-analytics.com",
                "analytics.google.com",
                "adservice.google.com",
                "pagead2.googlesyndication.com",
                "ad.doubleclick.net",
                "stats.g.doubleclick.net",
                "www.google-analytics.com",
                "ssl.google-analytics.com",

                // Major International Ad Networks & Exchanges (EasyList)
                "adnxs.com",
                "criteo.com",
                "criteo.net",
                "taboola.com",
                "outbrain.com",
                "rubiconproject.com",
                "pubmatic.com",
                "openx.net",
                "casalemedia.com",
                "smartadserver.com",
                "amazon-adsystem.com",
                "adform.net",
                "advertising.com",
                "bidswitch.net",
                "sovrn.com",
                "moatads.com",
                "yieldmo.com",
                "adroll.com",
                "appnexus.com",
                "zedo.com",
                "adsterra.com",
                "popads.net",
                "propellerads.com",
                "trafficfactory.biz",
                "revcontent.com",
                "mgid.com",
                "media.net",
                "inmobi.com",
                "adcolony.com",
                "chartbeat.com",
                "scorecardresearch.com",
                "quantserve.com",
                "hotjar.com",
                "segment.com",
                "optimizely.com",
                "clicktale.net",
                "crazyegg.com",
                "clarity.ms",
                "teads.tv",
                "exponential.com",
                "sharethrough.com",
                "flashtalking.com",
                "contextweb.com",
                "mathtag.com",
                "bluekai.com",
                "demdex.net",
                "exelator.com",
                "eyeota.net",
                "rlcdn.com",
                "tapad.com",
                "krxd.net",
                "semasio.net",
                "zemanta.com",
                "agkn.com",
                "adtarget.me",
                "adition.net",
                "adtech.de",

                // EasyList Germany (Regional Top Ad & Tracking Networks)
                "ioam.de",
                "ivwbox.de",
                "plista.com",
                "ligatus.com",
                "yieldlab.net",
                "adscale.de",
                "stroeer.de",
                "stroeerdigitalgroup.de",
                "adition.com",
                "meetrics.net",
                "wemfbox.ch",
                "oms.eu",
                "adspirit.de",
                "contative.com",
                "yoc.com",
                "united-internet-media.de",

                // Social & Telemetry Trackers (EasyPrivacy)
                "connect.facebook.net",
                "facebook.net",
                "analytics.twitter.com",
                "ads-twitter.com",
                "analytics.tiktok.com",
                "bat.bing.com",
                "yandex.ru",
                "mc.yandex.ru",
                "branch.io",
                "appsflyer.com",
                "adjust.com",
                "kochava.com",
                "newrelic.com",
                "nr-data.net",
                "sentry.io",
                "mixpanel.com",
                "heapanalytics.com",
                "fullstory.com",
                "mouseflow.com",
                "inspectlet.com"
            };

            // Build immutable FrozenSet for optimal read-heavy lookup performance
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list)
            {
                set.Add(d);
            }
            _blockedDomains = set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }

        private void LoadWhitelist()
        {
            try
            {
                if (File.Exists(_whitelistPath))
                {
                    var json = File.ReadAllText(_whitelistPath);
                    var items = JsonSerializer.Deserialize<List<string>>(json);
                    if (items != null)
                    {
                        foreach (var item in items)
                            _whitelistedHosts.Add(item);
                    }
                }
            }
            catch
            {
                // Ignore load errors
            }
        }

        private void SaveWhitelist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_whitelistedHosts.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_whitelistPath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
