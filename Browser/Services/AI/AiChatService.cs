using System.Collections.ObjectModel;
using System.Text;
using Browser.Models;

namespace Browser.Services.AI
{
    /// <summary>
    /// Central service for the AI chat feature.
    /// Manages conversations, backend selection, and streaming.
    /// Follows the existing singleton pattern (like ThemeService, SettingsService).
    /// </summary>
    public class AiChatService
    {
        private static readonly Lazy<AiChatService> _lazyInstance = new(() => new AiChatService());
        public static AiChatService Instance => _lazyInstance.Value;

        private IAiBackend _backend;
        private CancellationTokenSource? _streamCts;
        private readonly BrowserContextProvider _contextProvider = new();

        public AiChatService()
        {
            Messages = new ObservableCollection<ChatMessage>();
            _backend = CreateBackendFromSettings();
            WireWebLlmEvents();
        }

        /// <summary>The current conversation messages.</summary>
        public ObservableCollection<ChatMessage> Messages { get; }

        /// <summary>The active backend instance.</summary>
        public IAiBackend Backend => _backend;

        /// <summary>The browser context provider.</summary>
        public BrowserContextProvider ContextProvider => _contextProvider;

        /// <summary>The coding agent service.</summary>
        public CodingAgentService CodingAgent { get; } = new();

        /// <summary>Whether a streaming response is currently in progress.</summary>
        public bool IsStreaming { get; private set; }

        /// <summary>Whether the backend is currently available.</summary>
        public bool IsBackendAvailable { get; private set; }

        // ── Events ───────────────────────────────────────────────

        /// <summary>Fired when a new message is added.</summary>
        public event Action<ChatMessage>? MessageAdded;

        /// <summary>Fired when the streaming state changes.</summary>
        public event Action<bool>? StreamingStateChanged;

        /// <summary>Fired when backend availability changes.</summary>
        public event Action<bool>? BackendStatusChanged;

        // ── Backend Management ───────────────────────────────────

        /// <summary>
        /// Checks the backend availability and fires an event on change.
        /// </summary>
        public async Task CheckBackendAsync()
        {
            try
            {
                var available = await _backend.IsAvailableAsync();
                if (available != IsBackendAvailable)
                {
                    IsBackendAvailable = available;
                    BackendStatusChanged?.Invoke(available);
                }
            }
            catch
            {
                if (IsBackendAvailable)
                {
                    IsBackendAvailable = false;
                    BackendStatusChanged?.Invoke(false);
                }
            }
        }



        /// <summary>
        /// Gets the list of available models from the current backend.
        /// </summary>
        public async Task<List<string>> GetAvailableModelsAsync()
        {
            return await _backend.GetAvailableModelsAsync();
        }

        // ── Chat Operations ──────────────────────────────────────

        private const string DefaultSystemPrompt =
            "Du bist Nexa KI, ein hilfreicher Assistent integriert in den Nexa Browser. " +
            "Antworte präzise, freundlich und auf Deutsch, sofern der Benutzer nicht eine andere Sprache wählt. " +
            "Wenn du Browser-Kontext erhältst, nutze ihn um relevantere Antworten zu geben. " +
            "Formatiere Code-Blöcke mit Markdown-Backticks.";

        /// <summary>
        /// Sends a user message and streams the AI response.
        /// </summary>
        /// <param name="userMessage">The user's message text.</param>
        /// <param name="context">Optional browser context to include.</param>
        public async Task SendMessageAsync(string userMessage, BrowserContextProvider.BrowserContext? context = null)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return;
            if (IsStreaming) return;

            // Build the user message content with optional context
            var fullContent = userMessage;
            string? contextInfo = null;

            if (context != null)
            {
                var contextText = context.FormatForPrompt();
                if (!string.IsNullOrEmpty(contextText) && contextText != "[Browser-Kontext]\n")
                {
                    fullContent = $"{contextText}\n---\n{userMessage}";
                    contextInfo = context.GetContextSummary();
                }
            }

            // Add user message
            var userMsg = new ChatMessage
            {
                Role = ChatMessageRole.User,
                Content = userMessage, // Display the original message, not the context-enriched version
                ContextInfo = contextInfo
            };
            Messages.Add(userMsg);
            MessageAdded?.Invoke(userMsg);

            // Create assistant message placeholder for streaming
            var assistantMsg = new ChatMessage
            {
                Role = ChatMessageRole.Assistant,
                Content = "",
                IsStreaming = true
            };
            Messages.Add(assistantMsg);
            MessageAdded?.Invoke(assistantMsg);

            IsStreaming = true;
            StreamingStateChanged?.Invoke(true);
            _streamCts = new CancellationTokenSource();

            try
            {
                // Build the full message list for the API
                var apiMessages = BuildApiMessages(fullContent);

                var sb = new StringBuilder();
                await foreach (var token in _backend.StreamChatAsync(apiMessages, _streamCts.Token))
                {
                    sb.Append(token);
                    assistantMsg.Content = sb.ToString();
                }

                assistantMsg.IsStreaming = false;
            }
            catch (OperationCanceledException)
            {
                assistantMsg.IsStreaming = false;
                if (string.IsNullOrEmpty(assistantMsg.Content))
                    assistantMsg.Content = "[Antwort abgebrochen]";
            }
            catch (Exception ex)
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.Content = $"[Fehler: {ex.Message}]";
            }
            finally
            {
                IsStreaming = false;
                StreamingStateChanged?.Invoke(false);
                _streamCts?.Dispose();
                _streamCts = null;
            }
        }

        /// <summary>
        /// Cancels the current streaming response.
        /// </summary>
        public void CancelStreaming()
        {
            _streamCts?.Cancel();
        }

        /// <summary>
        /// Starts a new conversation (clears all messages).
        /// </summary>
        public void NewChat()
        {
            CancelStreaming();
            Messages.Clear();
        }

        /// <summary>
        /// Executes a smart action (summarize, translate, explain_code) with the given content.
        /// Uses the WebLLM smart_action pipeline for optimized task-specific prompts.
        /// </summary>
        public async Task SendSmartActionAsync(string action, string content, ChatMessage assistantMsg)
        {
            IsStreaming = true;
            StreamingStateChanged?.Invoke(true);
            _streamCts = new CancellationTokenSource();

            try
            {
                // For WebLLM, use the dedicated smart_action message
                // For other backends, use a regular chat message with the action prompt
                var actionPrompts = new Dictionary<string, string>
                {
                    ["summarize"] = "Fasse den folgenden Text knapp und präzise zusammen. Nutze Aufzählungszeichen für die Kernpunkte:\n\n",
                    ["translate"] = "Übersetze den folgenden Text. Wenn er auf Deutsch ist, ins Englische. Sonst ins Deutsche:\n\n",
                    ["explain_code"] = "Erkläre den folgenden Code verständlich und präzise:\n\n"
                };

                var promptPrefix = actionPrompts.GetValueOrDefault(action, "");
                var messages = new List<ChatMessage>
                {
                    new() { Role = ChatMessageRole.System, Content = "Du bist Nexa KI, ein hilfreicher Assistent." },
                    new() { Role = ChatMessageRole.User, Content = promptPrefix + content }
                };

                var sb = new StringBuilder();
                await foreach (var token in _backend.StreamChatAsync(messages, _streamCts.Token))
                {
                    sb.Append(token);
                    assistantMsg.Content = sb.ToString();
                }

                assistantMsg.IsStreaming = false;
            }
            catch (OperationCanceledException)
            {
                assistantMsg.IsStreaming = false;
                if (string.IsNullOrEmpty(assistantMsg.Content))
                    assistantMsg.Content = "[Antwort abgebrochen]";
            }
            catch (Exception ex)
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.Content = $"[Fehler: {ex.Message}]";
            }
            finally
            {
                IsStreaming = false;
                StreamingStateChanged?.Invoke(false);
                _streamCts?.Dispose();
                _streamCts = null;
            }
        }

        // ── Internal Helpers ─────────────────────────────────────

        private List<ChatMessage> BuildApiMessages(string lastUserContent)
        {
            var settings = SettingsService.Instance.Settings;
            var systemPrompt = string.IsNullOrWhiteSpace(settings.AiSystemPrompt)
                ? DefaultSystemPrompt
                : settings.AiSystemPrompt;

            var apiMessages = new List<ChatMessage>
            {
                new() { Role = ChatMessageRole.System, Content = systemPrompt }
            };

            // Add all previous messages (excluding the last user message which we'll replace with the context-enriched version)
            for (int i = 0; i < Messages.Count - 2; i++) // -2 because we already added user + assistant placeholder
            {
                var msg = Messages[i];
                if (msg.Role != ChatMessageRole.System) // Skip any existing system messages
                {
                    apiMessages.Add(new ChatMessage
                    {
                        Role = msg.Role,
                        Content = msg.Content
                    });
                }
            }

            // Add the context-enriched user message
            apiMessages.Add(new ChatMessage
            {
                Role = ChatMessageRole.User,
                Content = lastUserContent
            });

            return apiMessages;
        }

        /// <summary>Fired during WebLLM model download/compilation with progress (0..1) and status text.</summary>
        public event Action<double, string>? ModelLoadProgressChanged;

        /// <summary>Whether the WebLLM backend is currently loading the model.</summary>
        public bool IsModelLoading => (_backend as WebLlmBackend)?.IsModelLoading ?? false;

        /// <summary>Whether the WebLLM model has finished loading and is ready.</summary>
        public bool IsModelReady => (_backend as WebLlmBackend)?.IsModelReady ?? true;

        private static IAiBackend CreateBackendFromSettings()
        {
            var settings = SettingsService.Instance.Settings;
            IAiBackend backend = settings.AiBackendType switch
            {
                "Gemini" => new GeminiBackend { ApiKey = !string.IsNullOrWhiteSpace(settings.AiApiKey) ? settings.AiApiKey : GeminiBackend.DefaultApiKey },
                "ChatGPT" => new ChatGptBackend { ApiKey = !string.IsNullOrWhiteSpace(settings.AiApiKey) ? settings.AiApiKey : ChatGptBackend.DefaultApiKey },
                "LMStudio" => new LMStudioBackend(),
                "Ollama" => new OllamaBackend(),
                _ => new WebLlmBackend()
            };

            if (!string.IsNullOrEmpty(settings.AiBackendUrl))
                backend.BaseUrl = settings.AiBackendUrl;

            if (!string.IsNullOrEmpty(settings.AiSelectedModel))
                backend.SelectedModel = settings.AiSelectedModel;

            return backend;
        }

        /// <summary>
        /// Switches to a different backend based on current settings and wires up events.
        /// </summary>
        public void RefreshBackend()
        {
            _backend = CreateBackendFromSettings();
            WireWebLlmEvents();
        }

        private void WireWebLlmEvents()
        {
            if (_backend is WebLlmBackend webLlm)
            {
                webLlm.DownloadProgressChanged += (progress, text) =>
                {
                    ModelLoadProgressChanged?.Invoke(progress, text);
                };
            }
        }
    }
}
