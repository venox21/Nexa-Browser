using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Browser.Models;

namespace Browser.Services.AI
{
    /// <summary>
    /// AI backend connecting to Google Gemini API (Cloud).
    /// Supports streaming responses via Server-Sent Events (SSE) and model discovery.
    /// </summary>
    public class GeminiBackend : IAiBackend
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public const string DefaultApiKey = "AQ.Ab8RN6KuHYazHWK-QWnsBaYV-Ia8BJUPHu28AQTcv_ic4JW5rg";

        public GeminiBackend()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        }

        public string Name => "Gemini";
        public string Description => "Google Gemini API (Cloud)";
        public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
        public string? SelectedModel { get; set; } = "gemini-3.7-flash";

        /// <summary>The Google Gemini API key.</summary>
        public string ApiKey { get; set; } = DefaultApiKey;

        private string EffectiveApiKey => !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : DefaultApiKey;

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(EffectiveApiKey)) return false;

            try
            {
                var response = await _http.GetAsync($"{BaseUrl}/models?key={EffectiveApiKey}", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var fallbackModels = new List<string>
            {
                "gemini-3.7-flash",
                "gemini-3.5-flash",
                "gemini-3.6-flash",
                "gemini-flash-lite-latest",
                "gemini-flash-latest"
            };

            try
            {
                var response = await _http.GetAsync($"{BaseUrl}/models?key={EffectiveApiKey}", ct);
                if (!response.IsSuccessStatusCode) return fallbackModels;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    var result = new List<string>();
                    foreach (var model in modelsArray.EnumerateArray())
                    {
                        var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        // Strip "models/" prefix if present
                        if (name.StartsWith("models/"))
                            name = name.Substring(7);

                        // Only include models that support content generation and are gemini text models
                        if (name.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
                        {
                            if (model.TryGetProperty("supportedGenerationMethods", out var methods))
                            {
                                bool canGen = false;
                                foreach (var method in methods.EnumerateArray())
                                {
                                    if (method.GetString() == "generateContent")
                                    {
                                        canGen = true;
                                        break;
                                    }
                                }
                                if (canGen) result.Add(name);
                            }
                        }
                    }

                    if (result.Count > 0)
                        return result;
                }
            }
            catch
            {
                // Fall back to predefined list
            }

            return fallbackModels;
        }

        public async IAsyncEnumerable<string> StreamChatAsync(
            List<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var primaryModel = !string.IsNullOrWhiteSpace(SelectedModel) ? SelectedModel : "gemini-3.7-flash";
            if (primaryModel.StartsWith("models/")) primaryModel = primaryModel.Substring(7);

            var key = EffectiveApiKey;

            // Candidate models for automatic fallback if a specific model is rate limited (429) or busy (503)
            var candidateModels = new List<string> { primaryModel };
            foreach (var alt in new[] { "gemini-3.7-flash", "gemini-3.5-flash", "gemini-flash-lite-latest", "gemini-3.6-flash" })
            {
                if (!candidateModels.Contains(alt, StringComparer.OrdinalIgnoreCase))
                    candidateModels.Add(alt);
            }

            // Separate system instruction from chat contents
            string? systemInstruction = null;
            var contents = new List<object>();

            foreach (var m in messages)
            {
                if (m.Role == ChatMessageRole.System)
                {
                    systemInstruction = string.IsNullOrEmpty(systemInstruction)
                        ? m.Content
                        : systemInstruction + "\n" + m.Content;
                }
                else
                {
                    var geminiRole = m.Role == ChatMessageRole.Assistant
                        ? "model"
                        : "user";

                    contents.Add(new
                    {
                        role = geminiRole,
                        parts = new object[]
                        {
                            new { text = m.Content }
                        }
                    });
                }
            }

            // If no user messages, cannot proceed
            if (contents.Count == 0)
            {
                yield return "[Fehler: Keine Nachrichten zum Senden vorhanden.]";
                yield break;
            }

            object requestBody;
            if (!string.IsNullOrEmpty(systemInstruction))
            {
                requestBody = new
                {
                    system_instruction = new
                    {
                        parts = new object[]
                        {
                            new { text = systemInstruction }
                        }
                    },
                    contents = contents.ToArray(),
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 4096
                    }
                };
            }
            else
            {
                requestBody = new
                {
                    contents = contents.ToArray(),
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 4096
                    }
                };
            }

            var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);

            HttpResponseMessage? response = null;
            string? lastErrorMsg = null;
            bool wasRateLimited = false;

            foreach (var candidateModel in candidateModels)
            {
                var url = $"{BaseUrl}/models/{candidateModel}:streamGenerateContent?alt=sse&key={key}";
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };

                try
                {
                    var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        response = resp;
                        break;
                    }

                    var statusCode = (int)resp.StatusCode;
                    var errorBody = await resp.Content.ReadAsStringAsync(ct);
                    string errorMsg = $"API-Fehler {statusCode}";

                    try
                    {
                        using var errorDoc = JsonDocument.Parse(errorBody);
                        if (errorDoc.RootElement.TryGetProperty("error", out var errObj) &&
                            errObj.TryGetProperty("message", out var msgProp))
                        {
                            errorMsg = msgProp.GetString() ?? errorMsg;
                        }
                    }
                    catch { }

                    lastErrorMsg = errorMsg;

                    // If rate-limited (429) or high demand (503) or model unavailable (404), try next model
                    if (statusCode == 429 || errorMsg.Contains("quota", StringComparison.OrdinalIgnoreCase))
                    {
                        wasRateLimited = true;
                        continue;
                    }

                    if (statusCode == 503 || statusCode == 404)
                    {
                        continue;
                    }

                    // For authorization/client errors, abort fallback
                    break;
                }
                catch (Exception ex)
                {
                    lastErrorMsg = ex.Message;
                    break;
                }
            }

            if (response == null)
            {
                if (wasRateLimited)
                {
                    yield return "⏳ **Google Gemini Anfragelimit erreicht** (Google Free Tier: 20 Anfragen/Minute).\n\n" +
                                 "• Bitte warte ca. 30–40 Sekunden für die nächste Anfrage.\n" +
                                 "• **Tipp:** Du kannst deinen eigenen kostenlosen API-Key unter **Einstellungen > KI** hinterlegen (kostenlos auf [aistudio.google.com](https://aistudio.google.com)).\n" +
                                 "• Alternativ kannst du in der KI-Sidebar auf **WebLLM** umschalten (komplett lokales, unbegrenztes KI-Modell ohne Limits).";
                }
                else
                {
                    yield return $"[Fehler: {lastErrorMsg ?? "Verbindung zu Gemini fehlgeschlagen"}]";
                }
                yield break;
            }

            // Parse SSE stream: lines start with "data: " followed by JSON
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                if (!line.StartsWith("data: ")) continue;

                var data = line.Substring(6).Trim();
                if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

                string? token = null;
                try
                {
                    using var lineDoc = JsonDocument.Parse(data);
                    var root = lineDoc.RootElement;

                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var content) &&
                            content.TryGetProperty("parts", out var parts))
                        {
                            var sb = new StringBuilder();
                            foreach (var part in parts.EnumerateArray())
                            {
                                // Skip internal thinking/reasoning parts
                                if (part.TryGetProperty("thought", out var isThought) && isThought.GetBoolean())
                                    continue;

                                if (part.TryGetProperty("text", out var textProp))
                                {
                                    var text = textProp.GetString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        sb.Append(text);
                                    }
                                }
                            }
                            if (sb.Length > 0)
                            {
                                token = sb.ToString();
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore non-json or malformed SSE frames
                    continue;
                }

                if (!string.IsNullOrEmpty(token))
                {
                    yield return token;
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
    }
}
