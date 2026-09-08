using Browser.Models;

namespace Browser.Services.AI
{
    /// <summary>
    /// Abstraction for a local AI inference backend.
    /// Implementations can wrap Ollama, LM Studio, or any OpenAI-compatible local server.
    /// </summary>
    public interface IAiBackend
    {
        /// <summary>Human-readable name of this backend (e.g. "Ollama", "LM Studio").</summary>
        string Name { get; }

        /// <summary>Short description of this backend.</summary>
        string Description { get; }

        /// <summary>The base URL this backend connects to.</summary>
        string BaseUrl { get; set; }

        /// <summary>The currently selected model name.</summary>
        string? SelectedModel { get; set; }

        /// <summary>
        /// Checks whether the backend server is reachable and ready.
        /// </summary>
        Task<bool> IsAvailableAsync(CancellationToken ct = default);

        /// <summary>
        /// Retrieves the list of available models from the backend.
        /// </summary>
        Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default);

        /// <summary>
        /// Sends a chat request and streams the response token by token.
        /// </summary>
        /// <param name="messages">The full conversation history to send.</param>
        /// <param name="ct">Cancellation token to abort the stream.</param>
        /// <returns>An async enumerable of response text chunks.</returns>
        IAsyncEnumerable<string> StreamChatAsync(List<ChatMessage> messages, CancellationToken ct = default);

        /// <summary>
        /// Sends a chat request and returns the complete response (non-streaming fallback).
        /// </summary>
        Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct = default);
    }
}
