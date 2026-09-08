using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Browser.Models;

namespace Browser.Services.AI
{
    /// <summary>
    /// AI backend implementation for Ollama (https://ollama.ai).
    /// Communicates via Ollama's REST API on localhost:11434.
    /// Supports streaming via NDJSON (newline-delimited JSON).
    /// </summary>
    public class OllamaBackend : IAiBackend
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public OllamaBackend()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        public string Name => "Ollama";
        public string Description => "Lokaler KI-Server über Ollama (Standard: localhost:11434)";
        public string BaseUrl { get; set; } = "http://localhost:11434";
        public string? SelectedModel { get; set; }

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _http.GetAsync($"{BaseUrl}/api/tags", ct);
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
                var response = await _http.GetAsync($"{BaseUrl}/api/tags", ct);
                if (!response.IsSuccessStatusCode) return models;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    foreach (var model in modelsArray.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var nameProp))
                        {
                            models.Add(nameProp.GetString() ?? "");
                        }
                    }
                }
            }
            catch
            {
                // Server not reachable or invalid response
            }
            return models;
        }

        public async IAsyncEnumerable<string> StreamChatAsync(
            List<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var model = SelectedModel;
            if (string.IsNullOrEmpty(model))
            {
                var available = await GetAvailableModelsAsync(ct);
                model = available.FirstOrDefault() ?? "llama3";
            }

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
                stream = true
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/chat")
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

                using var lineDoc = JsonDocument.Parse(line);
                var root = lineDoc.RootElement;

                // Ollama streams: {"message":{"role":"assistant","content":"token"},"done":false}
                if (root.TryGetProperty("message", out var msgProp) &&
                    msgProp.TryGetProperty("content", out var contentProp))
                {
                    var token = contentProp.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        yield return token;
                    }
                }

                if (root.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
                {
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
    }
}
