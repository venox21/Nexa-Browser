using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Browser.Models;
using Browser.Resources;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browser.Services.AI
{
    /// <summary>
    /// AI backend that runs a compact LLM entirely in-browser via WebLLM (WebGPU/WASM).
    /// No external software installation required – the model downloads into IndexedDB on first use.
    /// 
    /// Architecture:
    ///   C# (this class) ↔ hidden WebView2 ↔ runner.html ↔ @mlc-ai/web-llm ↔ WebGPU
    /// </summary>
    public class WebLlmBackend : IAiBackend
    {
        private WebView2? _webView;
        private bool _isReady;
        private bool _isPageLoaded;
        private string? _lastErrorMessage;
        private readonly Dispatcher _dispatcher;

        private TaskCompletionSource<bool>? _readyTcs;
        private TaskCompletionSource<bool>? _pageLoadTcs;
        private readonly object _initLock = new();
        private Task<bool>? _initTask;

        // For streaming: JS sends tokens asynchronously via postMessage into an async Channel
        private Channel<WebLlmMessage>? _tokenChannel;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // ── Events ──────────────────────────────────────────────
        /// <summary>Fired during model download/compilation with progress (0..1) and status text.</summary>
        public event Action<double, string>? DownloadProgressChanged;

        // ── IAiBackend Implementation ───────────────────────────
        public string Name => "WebLLM";
        public string Description => "Integrierte lokale KI (Keine Installation nötig)";
        public string BaseUrl { get; set; } = "https://ai.nexa/runner.html";
        public string? SelectedModel { get; set; }

        public WebLlmBackend()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            // WebLLM is built-in and does not require an external daemon/server like Ollama
            return Task.FromResult(true);
        }

        public Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var models = new List<string>
            {
                "Qwen2.5-0.5B-Instruct-q4f16_1-MLC",
                "Qwen2.5-0.5B-Instruct-q4f32_1-MLC",
                "SmolLM2-360M-Instruct-q4f16_1-MLC",
                "SmolLM2-1.7B-Instruct-q4f16_1-MLC",
                "Llama-3.2-1B-Instruct-q4f16_1-MLC"
            };
            return Task.FromResult(models);
        }

        public async IAsyncEnumerable<string> StreamChatAsync(
            List<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Ensure engine is ready
            if (!_isReady)
            {
                var ok = await InitializeAsync(ct);
                if (!ok || !_isReady)
                {
                    var err = !string.IsNullOrEmpty(_lastErrorMessage)
                        ? _lastErrorMessage
                        : "Das lokale KI-Modell konnte nicht initialisiert werden. Bitte prüfe deine Internetverbindung für den einmaligen Download und die WebGPU-Unterstützung deiner Grafikkarte.";
                    yield return $"[Fehler: {err}]";
                    yield break;
                }
            }

            _tokenChannel = Channel.CreateUnbounded<WebLlmMessage>();

            // Build the chat request
            var chatMessages = messages.Select(m => new
            {
                role = m.Role switch
                {
                    ChatMessageRole.System => "system",
                    ChatMessageRole.User => "user",
                    ChatMessageRole.Assistant => "assistant",
                    _ => "user"
                },
                content = m.Content
            }).ToArray();

            var request = new
            {
                type = "chat",
                messages = chatMessages,
                requestId = Guid.NewGuid().ToString()
            };

            var json = JsonSerializer.Serialize(request, _jsonOptions);

            // Send to WebView2 on UI thread
            await _dispatcher.InvokeAsync(() =>
            {
                _webView?.CoreWebView2?.PostWebMessageAsJson(json);
            });

            // Consume tokens asynchronously without blocking the WPF UI thread
            while (!ct.IsCancellationRequested)
            {
                WebLlmMessage? msg = null;
                try
                {
                    var hasItem = await _tokenChannel.Reader.WaitToReadAsync(ct);
                    if (!hasItem) break;
                    if (!_tokenChannel.Reader.TryRead(out msg)) continue;
                }
                catch (OperationCanceledException)
                {
                    AbortGeneration();
                    yield break;
                }

                if (msg == null) break;

                if (msg.Type == "token" && !string.IsNullOrEmpty(msg.Text))
                {
                    yield return msg.Text;
                }
                else if (msg.Type == "done")
                {
                    yield break;
                }
                else if (msg.Type == "error")
                {
                    yield return $"\n\n[Fehler: {msg.Error}]";
                    yield break;
                }
            }
        }

        public async Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            await foreach (var token in StreamChatAsync(messages, ct))
            {
                sb.Append(token);
            }
            return sb.ToString();
        }

        // ── Initialization ──────────────────────────────────────

        /// <summary>
        /// Creates the hidden WebView2 worker and loads the runner page.
        /// Thread-safe: multiple callers join the same initial task.
        /// </summary>
        public Task<bool> InitializeAsync(CancellationToken ct = default)
        {
            lock (_initLock)
            {
                if (_isReady) return Task.FromResult(true);
                if (_initTask != null) return _initTask;

                _initTask = InitializeInternalAsync(ct);
                return _initTask;
            }
        }

        private async Task<bool> InitializeInternalAsync(CancellationToken ct)
        {
            try
            {
                Log("Starting InitializeInternalAsync...");
                _lastErrorMessage = null;
                _pageLoadTcs = new TaskCompletionSource<bool>();
                _readyTcs = new TaskCompletionSource<bool>();

                // Create WebView2 on UI thread and attach to visual tree
                await _dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        if (_webView == null)
                        {
                            _webView = new WebView2
                            {
                                Width = 0,
                                Height = 0,
                                Focusable = false,
                                IsHitTestVisible = false
                            };

                            // Attach to MainWindow's hidden host so HWND is properly created
                            MainWindow? mw = Application.Current?.MainWindow as MainWindow;
                            if (mw != null && !mw.AiHiddenWebViewHost.Children.Contains(_webView))
                            {
                                mw.AiHiddenWebViewHost.Children.Add(_webView);
                                Log("Added _webView to MainWindow.AiHiddenWebViewHost");
                            }

                            // Share CoreWebView2Environment with MainWindow
                            CoreWebView2Environment? env = null;
                            if (mw != null)
                            {
                                env = await mw.GetOrCreateEnvironmentAsync();
                                Log("Got shared CoreWebView2Environment from MainWindow");
                            }

                            if (env == null)
                            {
                                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                                var userDataFolder = Path.Combine(appData, BrandingConfig.BrowserName, "WebView2Data");
                                Directory.CreateDirectory(userDataFolder);
                                var options = new CoreWebView2EnvironmentOptions
                                {
                                    AdditionalBrowserArguments = "--enable-features=WebGPU --enable-unsafe-webgpu"
                                };
                                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: options);
                                Log("Created fallback CoreWebView2Environment with WebGPU options");
                            }

                            await _webView.EnsureCoreWebView2Async(env);
                            Log("EnsureCoreWebView2Async completed");

                            // Set up virtual host mapping for the runner page
                            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                            var runnerDir = Path.Combine(baseDir, "Resources", "WebLLM");
                            if (!Directory.Exists(runnerDir))
                            {
                                runnerDir = Path.Combine(baseDir, "..", "..", "..", "Resources", "WebLLM");
                            }
                            runnerDir = Path.GetFullPath(runnerDir);
                            Log($"Setting virtual host ai.nexa to {runnerDir}");

                            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                                "ai.nexa",
                                runnerDir,
                                CoreWebView2HostResourceAccessKind.Allow
                            );

                            // Listen for navigation events
                            _webView.CoreWebView2.NavigationStarting += (s, e) => Log($"Navigating: {e.Uri}");
                            _webView.CoreWebView2.NavigationCompleted += (s, e) => Log($"NavigationCompleted: isSuccess={e.IsSuccess}, errorStatus={e.WebErrorStatus}, http={e.HttpStatusCode}");

                            // Listen for messages from JS
                            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                            // Navigate to the runner
                            Log("Navigating to https://ai.nexa/runner.html");
                            _webView.CoreWebView2.Navigate("https://ai.nexa/runner.html");
                        }
                        else if (!_isPageLoaded)
                        {
                            Log("Re-navigating to runner.html...");
                            _webView.CoreWebView2.Navigate("https://ai.nexa/runner.html");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"WebView2 init exception on UI thread: {ex}");
                        _lastErrorMessage = ex.Message;
                        _pageLoadTcs?.TrySetResult(false);
                        _readyTcs?.TrySetResult(false);
                    }
                }).Task.Unwrap();

                // Wait for the page to signal "loaded" (skip if already loaded)
                if (!_isPageLoaded)
                {
                    Log("Waiting for page load signal...");
                    var pageLoaded = await WaitWithTimeout(_pageLoadTcs!.Task, TimeSpan.FromSeconds(20), ct);
                    if (!pageLoaded && !_isPageLoaded)
                    {
                        Log("Page load timed out or failed");
                        if (string.IsNullOrEmpty(_lastErrorMessage))
                        {
                            _lastErrorMessage = "Die lokale Runner-Seite konnte nicht geladen werden.";
                        }
                        lock (_initLock) { _initTask = null; }
                        return false;
                    }
                }
                else
                {
                    Log("Runner page is already loaded.");
                }

                Log("Page ready. Sending init command to engine...");

                // Send init command to start loading the model
                var model = SelectedModel ?? "Qwen2.5-0.5B-Instruct-q4f16_1-MLC";
                var initMsg = JsonSerializer.Serialize(new { type = "init", model }, _jsonOptions);

                await _dispatcher.InvokeAsync(() =>
                {
                    _webView?.CoreWebView2?.PostWebMessageAsJson(initMsg);
                });

                // Wait for the model to be ready (can take a few minutes on first load)
                Log("Waiting for model to be ready...");
                var ready = await WaitWithTimeout(_readyTcs!.Task, TimeSpan.FromMinutes(10), ct);
                _isReady = ready;
                Log($"Model ready state: {ready}");
                if (!ready)
                {
                    lock (_initLock) { _initTask = null; }
                }
                return ready;
            }
            catch (Exception ex)
            {
                Log($"InitializeInternalAsync exception: {ex}");
                _lastErrorMessage = ex.Message;
                _isReady = false;
                lock (_initLock) { _initTask = null; }
                return false;
            }
        }

        // ── Message Handling (JS → C#) ──────────────────────────

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // Support both TryGetWebMessageAsString (when JS sends string) and WebMessageAsJson (when JS sends object)
                string? rawJson = null;
                try
                {
                    rawJson = e.TryGetWebMessageAsString();
                }
                catch { }

                if (string.IsNullOrEmpty(rawJson))
                {
                    rawJson = e.WebMessageAsJson;
                }

                if (string.IsNullOrEmpty(rawJson)) return;

                // If JS passed JSON.stringify, WebMessageAsJson may be a double-quoted JSON string literal
                if (rawJson.StartsWith("\"") && rawJson.EndsWith("\"") && rawJson.Length > 2)
                {
                    try
                    {
                        rawJson = JsonSerializer.Deserialize<string>(rawJson);
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(rawJson)) return;

                var msg = JsonSerializer.Deserialize<WebLlmMessage>(rawJson, _jsonOptions);
                if (msg == null) return;

                Log($"WebMessage received: type={msg.Type}, text={msg.Text}, error={msg.Error}, progress={msg.Progress}");

                // Any valid message proves page is loaded
                _isPageLoaded = true;

                switch (msg.Type)
                {
                    case "loaded":
                        _pageLoadTcs?.TrySetResult(true);
                        break;

                    case "progress":
                        DownloadProgressChanged?.Invoke(msg.Progress, msg.Text ?? "");
                        break;

                    case "ready":
                        _isReady = true;
                        _readyTcs?.TrySetResult(true);
                        break;

                    case "token":
                        _tokenChannel?.Writer.TryWrite(msg);
                        break;

                    case "done":
                        _tokenChannel?.Writer.TryWrite(msg);
                        _tokenChannel?.Writer.TryComplete();
                        break;

                    case "error":
                        _lastErrorMessage = msg.Error;
                        Log($"WebLLM JS Error: {msg.Error}");
                        _tokenChannel?.Writer.TryWrite(msg);
                        _tokenChannel?.Writer.TryComplete();
                        _readyTcs?.TrySetResult(false);
                        break;

                    case "pong":
                        // Health check response
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"OnWebMessageReceived parse error: {ex}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────

        private static void Log(string message)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexa");
                Directory.CreateDirectory(folder);
                var logFile = Path.Combine(folder, "webllm.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }

        private static async Task<bool> WaitWithTimeout(Task<bool> task, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(timeout, cts.Token);
            var completed = await Task.WhenAny(task, delayTask);
            if (completed == task)
            {
                cts.Cancel();
                return await task;
            }
            return false;
        }

        /// <summary>
        /// Sends an abort message to stop the current generation.
        /// </summary>
        public void AbortGeneration()
        {
            _dispatcher.InvokeAsync(() =>
            {
                var msg = JsonSerializer.Serialize(new { type = "abort" }, _jsonOptions);
                _webView?.CoreWebView2?.PostWebMessageAsJson(msg);
            });
        }

        /// <summary>
        /// Resets the chat context in the engine.
        /// </summary>
        public void ResetChat()
        {
            _dispatcher.InvokeAsync(() =>
            {
                var msg = JsonSerializer.Serialize(new { type = "reset" }, _jsonOptions);
                _webView?.CoreWebView2?.PostWebMessageAsJson(msg);
            });
        }

        /// <summary>Whether the engine has loaded the model and is ready for inference.</summary>
        public bool IsModelReady => _isReady;

        /// <summary>Whether the engine is currently loading the model.</summary>
        public bool IsModelLoading => _initTask != null && !_isReady;

        // ── Prewarming ──────────────────────────────────────────

        /// <summary>
        /// Silently pre-initializes the WebLLM engine in the background so the first
        /// user message gets an instant response (0 s latency). Safe to call multiple
        /// times – subsequent calls are no-ops if the engine is already loaded.
        /// </summary>
        public async Task PrewarmAsync(CancellationToken ct = default)
        {
            if (_isReady) return;
            Log("PrewarmAsync: starting background model load...");
            await InitializeAsync(ct);
            Log($"PrewarmAsync: done, ready={_isReady}");
        }

        /// <summary>
        /// Waits for a short idle period after browser start, then silently prewarms
        /// the model so the user perceives zero loading time on first KI interaction.
        /// </summary>
        public static async Task PrewarmIfIdleAsync(int delayMs = 3000)
        {
            await Task.Delay(delayMs);

            // Only prewarm if the user has WebLLM as the active backend
            var settings = SettingsService.Instance.Settings;
            if (settings.AiBackendType != "WebLLM") return;

            var backend = AiChatService.Instance.Backend as WebLlmBackend;
            if (backend == null) return;

            try
            {
                await backend.PrewarmAsync();
            }
            catch (Exception ex)
            {
                Log($"PrewarmIfIdleAsync failed (non-critical): {ex.Message}");
            }
        }

        // ── Model Switching ─────────────────────────────────────

        /// <summary>
        /// Switches to a different model at runtime without restarting the browser.
        /// The current engine context is destroyed and a new model is loaded.
        /// </summary>
        public async Task<bool> SwitchModelAsync(string modelId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return false;
            if (modelId == SelectedModel && _isReady) return true;

            Log($"SwitchModelAsync: switching to {modelId}...");

            SelectedModel = modelId;
            _isReady = false;
            _readyTcs = new TaskCompletionSource<bool>();

            // If the WebView isn't loaded yet, do a full init
            if (_webView?.CoreWebView2 == null || !_isPageLoaded)
            {
                lock (_initLock) { _initTask = null; }
                return await InitializeAsync(ct);
            }

            // Send model switch command to the running runner page
            var initMsg = JsonSerializer.Serialize(new { type = "init", model = modelId }, _jsonOptions);
            await _dispatcher.InvokeAsync(() =>
            {
                _webView?.CoreWebView2?.PostWebMessageAsJson(initMsg);
            });

            var ready = await WaitWithTimeout(_readyTcs.Task, TimeSpan.FromMinutes(10), ct);
            _isReady = ready;
            Log($"SwitchModelAsync: model={modelId}, ready={ready}");
            return ready;
        }

        // ── Internal Message DTO ────────────────────────────────

        private class WebLlmMessage
        {
            public string Type { get; set; } = "";
            public string? Text { get; set; }
            public string? Error { get; set; }
            public double Progress { get; set; }
            public string? Model { get; set; }
            public bool Ready { get; set; }
        }
    }
}
