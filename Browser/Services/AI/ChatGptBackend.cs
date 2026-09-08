using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Browser.Models;

namespace Browser.Services.AI
{
    /// <summary>
    /// AI backend for the OpenAI / ChatGPT API.
    /// Connects to https://api.openai.com/v1 using an API key.
    /// Supports streaming via SSE (Server-Sent Events).
    /// </summary>
    public class ChatGptBackend : IAiBackend
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public ChatGptBackend()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public string Name => "ChatGPT";
        public string Description => "OpenAI ChatGPT API (Cloud)";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string? SelectedModel { get; set; } = "gpt-4o-mini";

        public const string DefaultApiKey = "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";

        /// <summary>The OpenAI API key (sk-...).</summary>
        public string ApiKey { get; set; } = DefaultApiKey;

        private void EnsureAuthHeader()
        {
            var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : DefaultApiKey;
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", key);
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : DefaultApiKey;
            if (string.IsNullOrWhiteSpace(key)) return false;

            try
            {
                EnsureAuthHeader();
                var response = await _http.GetAsync($"{BaseUrl}/models", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var models = new List<string>
            {
                // Pre-populated with common models so the list works even offline
                "gpt-4o-mini",
                "gpt-4o",
                "gpt-4-turbo",
                "gpt-3.5-turbo"
            };

            try
            {
                EnsureAuthHeader();
                var response = await _http.GetAsync($"{BaseUrl}/models", ct);
                if (!response.IsSuccessStatusCode) return models;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var dataArray))
                {
                    var apiModels = new List<string>();
                    foreach (var model in dataArray.EnumerateArray())
                    {
                        if (model.TryGetProperty("id", out var idProp))
                        {
                            var id = idProp.GetString() ?? "";
                            // Only show chat-capable models
                            if (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
                                id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                                id.StartsWith("o3", StringComparison.OrdinalIgnoreCase))
                            {
                                apiModels.Add(id);
                            }
                        }
                    }

                    if (apiModels.Count > 0)
                    {
                        apiModels.Sort();
                        return apiModels;
                    }
                }
            }
            catch
            {
                // Return default list on error
            }

            return models;
        }

        public async IAsyncEnumerable<string> StreamChatAsync(
            List<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                yield return "[Fehler: Kein OpenAI API-Key konfiguriert. Bitte in den Einstellungen hinterlegen.]";
                yield break;
            }

            EnsureAuthHeader();

            var model = SelectedModel ?? "gpt-4o-mini";

            var requestBody = new
            {
                model,
                messages = messages.Select(m => new
                {
                    role = m.Role switch
                    {
                        ChatMessageRole.System => "system",
                        ChatMessageRole.User => "user",
                        ChatMessageRole.Assistant => "assistant",
                        _ => "user"
                    },
                    content = m.Content
                }).ToArray(),
                stream = true,
                temperature = 0.7,
                max_tokens = 4096
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
            {
                Content = httpContent
            };

            var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : DefaultApiKey;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            HttpResponseMessage? response = null;
            string? initError = null;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    string errorMsg = $"API-Fehler {(int)response.StatusCode}";

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

                    initError = $"[Fehler: {errorMsg}]";
                }
            }
            catch (Exception ex)
            {
                initError = $"[Fehler: Verbindung zu OpenAI fehlgeschlagen: {ex.Message}]";
            }

            if (initError != null)
            {
                yield return initError;
                yield break;
            }

            if (response == null)
            {
                yield break;
            }

            // Parse SSE stream: each line starts with "data: " followed by JSON
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                // SSE format: "data: {...}" or "data: [DONE]"
                if (!line.StartsWith("data: ")) continue;

                var data = line.Substring(6).Trim();
                if (data == "[DONE]") yield break;
                if (string.IsNullOrEmpty(data)) continue;

                string? token = null;
                try
                {
                    using var lineDoc = JsonDocument.Parse(data);
                    var root = lineDoc.RootElement;

                    if (root.TryGetProperty("choices", out var choices))
                    {
                        foreach (var choice in choices.EnumerateArray())
                        {
                            if (choice.TryGetProperty("delta", out var delta) &&
                                delta.TryGetProperty("content", out var contentProp))
                            {
                                token = contentProp.GetString();
                            }
                        }
                    }
                }
                catch
                {
                    // Skip malformed SSE lines
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
