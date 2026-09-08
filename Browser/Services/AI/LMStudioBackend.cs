using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Browser.Models;

namespace Browser.Services.AI
{
    /// <summary>
    /// AI backend implementation for LM Studio and any OpenAI-compatible local server.
    /// Communicates via the OpenAI-compatible /v1/chat/completions endpoint.
    /// Default: localhost:1234.
    /// </summary>
    public class LMStudioBackend : IAiBackend
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public LMStudioBackend()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        public string Name => "LM Studio";
        public string Description => "Lokaler KI-Server über LM Studio / OpenAI-kompatible API (Standard: localhost:1234)";
        public string BaseUrl { get; set; } = "http://localhost:1234";
        public string? SelectedModel { get; set; }

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _http.GetAsync($"{BaseUrl}/v1/models", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var models = new List<string>();
            try
            {
                var response = await _http.GetAsync($"{BaseUrl}/v1/models", ct);
                if (!response.IsSuccessStatusCode) return models;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var dataArray))
                {
                    foreach (var model in dataArray.EnumerateArray())
                    {
                        if (model.TryGetProperty("id", out var idProp))
                        {
                            models.Add(idProp.GetString() ?? "");
                        }
                    }
                }
            }
            catch
            {
                // Server not reachable
            }
            return models;
        }

        public async IAsyncEnumerable<string> StreamChatAsync(
            List<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var requestBody = new
            {
                model = SelectedModel ?? "default",
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
                max_tokens = -1
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            {
                Content = httpContent
            };

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrEmpty(line)) continue;

                // SSE format: "data: {...}" or "data: [DONE]"
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];

                if (data == "[DONE]") yield break;

                using var lineDoc = JsonDocument.Parse(data);
                var root = lineDoc.RootElement;

                // OpenAI format: {"choices":[{"delta":{"content":"token"}}]}
                if (root.TryGetProperty("choices", out var choices))
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var contentProp))
                        {
                            var token = contentProp.GetString();
                            if (!string.IsNullOrEmpty(token))
                            {
                                yield return token;
                            }
                        }
                    }
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
