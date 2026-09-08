using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Browser.Services
{
    /// <summary>
    /// Ultra-fast in-page web translation engine (Chrome Translation Engine).
    /// Injects Google's native translation engine into the page to translate the
    /// entire DOM in real-time (~1 second) without any API-key costs or token limits.
    /// Supports instant 1-click revert to the original text.
    /// </summary>
    public class TranslationService
    {
        private static readonly Lazy<TranslationService> _instance = new(() => new TranslationService());
        public static TranslationService Instance => _instance.Value;

        /// <summary>
        /// JavaScript snippet to inject Google Translate Chrome Engine, configure German language,
        /// and backup original text nodes for instant 1-click restore.
        /// </summary>
        private const string GoogleTranslateInjectScript = @"
(() => {
    try {
        // 1. Clean Styling (hide Google banner and avoid pushing body down)
        if (!document.getElementById('__nexa_trans_style')) {
            const style = document.createElement('style');
            style.id = '__nexa_trans_style';
            style.textContent = `
                .goog-te-banner-frame, iframe.goog-te-banner-frame { display: none !important; }
                .skiptranslate { display: none !important; }
                body { top: 0px !important; position: static !important; }
                #goog-gt-tt { display: none !important; }
                .goog-text-highlight { background-color: transparent !important; box-shadow: none !important; }
            `;
            (document.head || document.documentElement).appendChild(style);
        }

        // 2. Backup original text nodes for instant 1-click restore
        if (!window.__nexa_nodes) {
            const ignore = new Set(['SCRIPT', 'STYLE', 'CODE', 'PRE', 'NOSCRIPT', 'SVG', 'TEXTAREA', 'INPUT', 'SELECT']);
            window.__nexa_nodes = [];
            window.__nexa_origs = [];
            function isVisible(el) {
                if (!el) return false;
                return !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
            }
            const walker = document.createTreeWalker(
                document.body || document.documentElement,
                NodeFilter.SHOW_TEXT,
                {
                    acceptNode: function(node) {
                        const p = node.parentElement;
                        if (!p || ignore.has(p.tagName) || p.isContentEditable || !isVisible(p)) return NodeFilter.FILTER_REJECT;
                        const t = node.nodeValue ? node.nodeValue.trim() : '';
                        return t.length >= 2 ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
                    }
                }
            );
            let n;
            while (n = walker.nextNode()) {
                window.__nexa_nodes.push(n);
                window.__nexa_origs.push(n.nodeValue);
            }
        }

        // 3. Set cookie for German translation
        const host = window.location.hostname;
        const cookieVal = '/auto/de';
        document.cookie = 'googtrans=' + cookieVal + '; path=/';
        if (host) {
            document.cookie = 'googtrans=' + cookieVal + '; path=/; domain=' + host;
            const parts = host.split('.');
            if (parts.length >= 2) {
                document.cookie = 'googtrans=' + cookieVal + '; path=/; domain=.' + parts.slice(-2).join('.');
            }
        }

        // 4. Create hidden widget container
        let el = document.getElementById('google_translate_element');
        if (!el) {
            el = document.createElement('div');
            el.id = 'google_translate_element';
            el.style.display = 'none';
            (document.body || document.documentElement).appendChild(el);
        }

        // 5. Trigger if already loaded
        const combo = document.querySelector('.goog-te-combo');
        if (combo) {
            combo.value = 'de';
            combo.dispatchEvent(new Event('change'));
            return JSON.stringify({ success: true, mode: 'combo_retriggered' });
        }

        // 6. Callback for initialization
        window.googleTranslateElementInit = function() {
            try {
                new google.translate.TranslateElement({
                    pageLanguage: 'auto',
                    includedLanguages: 'de',
                    layout: google.translate.TranslateElement.InlineLayout.SIMPLE,
                    autoDisplay: false
                }, 'google_translate_element');

                setTimeout(() => {
                    const c = document.querySelector('.goog-te-combo');
                    if (c) {
                        c.value = 'de';
                        c.dispatchEvent(new Event('change'));
                    }
                }, 100);
            } catch(e) {}
        };

        // 7. Inject Google script
        if (!document.getElementById('__nexa_gtrans_script')) {
            const s = document.createElement('script');
            s.id = '__nexa_gtrans_script';
            s.type = 'text/javascript';
            s.src = 'https://translate.google.com/translate_a/element.js?cb=googleTranslateElementInit';
            (document.head || document.documentElement).appendChild(s);
        }

        return JSON.stringify({ success: true, mode: 'script_injected' });
    } catch(e) {
        return JSON.stringify({ success: false, error: e.toString() });
    }
})();";

        /// <summary>
        /// JavaScript snippet to reset translation cookies and restore original texts.
        /// </summary>
        private const string GoogleTranslateRevertScript = @"
(() => {
    try {
        const host = window.location.hostname;
        document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
        if (host) {
            document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=' + host;
            const parts = host.split('.');
            if (parts.length >= 2) {
                document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=.' + parts.slice(-2).join('.');
            }
        }

        document.documentElement.classList.remove('translated-ltr');
        document.documentElement.classList.remove('translated-rtl');

        const combo = document.querySelector('.goog-te-combo');
        if (combo) {
            combo.value = '';
            combo.dispatchEvent(new Event('change'));
        }

        // Instant text restoration from original backup
        if (window.__nexa_nodes && window.__nexa_origs) {
            for (let i = 0; i < window.__nexa_nodes.length; i++) {
                const node = window.__nexa_nodes[i];
                const orig = window.__nexa_origs[i];
                if (node && orig !== undefined) {
                    node.nodeValue = orig;
                }
            }
        }
        return true;
    } catch(e) {
        return false;
    }
})();";

        /// <summary>
        /// Translates the currently loaded page in the given WebView2 to German using the ultra-fast Chrome engine.
        /// Reports progress through onStatusUpdate callback.
        /// </summary>
        public async Task<bool> TranslatePageAsync(
            CoreWebView2 webView,
            Action<string> onStatusUpdate,
            CancellationToken ct = default)
        {
            if (webView == null) return false;

            try
            {
                onStatusUpdate("Seite wird blitzschnell ins Deutsche übersetzt...");

                // Inject the Google Translate Chrome engine
                await webView.ExecuteScriptAsync(GoogleTranslateInjectScript);

                // Wait for translation to take effect (typically 300ms - 1200ms)
                var sw = Stopwatch.StartNew();
                bool isTranslated = false;

                while (sw.ElapsedMilliseconds < 3500 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(150, ct);

                    // Check if translation is applied (html.translated-ltr or combo set or font tags present)
                    string checkJson = await webView.ExecuteScriptAsync(@"
                        (() => {
                            if (document.documentElement.classList.contains('translated-ltr') ||
                                document.documentElement.classList.contains('translated-rtl')) {
                                return true;
                            }
                            if (document.querySelector('font[style*=""vertical-align""]') ||
                                document.querySelector('.goog-te-combo')) {
                                return true;
                            }
                            return false;
                        })();
                    ");

                    if (checkJson == "true")
                    {
                        isTranslated = true;
                        break;
                    }
                }

                onStatusUpdate(isTranslated
                    ? "✓ Seite erfolgreich ins Deutsche übersetzt."
                    : "✓ Übersetzung wird angewendet...");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                onStatusUpdate($"Übersetzungsfehler: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reverts translated page back to original text.
        /// </summary>
        public async Task RevertAsync(CoreWebView2 webView)
        {
            if (webView == null) return;
            try
            {
                await webView.ExecuteScriptAsync(GoogleTranslateRevertScript);
            }
            catch { }
        }
    }
}
