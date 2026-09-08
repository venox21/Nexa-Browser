namespace Browser.Models
{
    /// <summary>
    /// Persistable settings for the AI assistant feature.
    /// </summary>
    public class AiSettingsModel
    {
        /// <summary>Active backend type: "WebLLM", "Ollama", "LMStudio", or "Custom".</summary>
        public string BackendType { get; set; } = "WebLLM";

        /// <summary>Backend API base URL. Default is the integrated WebLLM runner.</summary>
        public string BackendUrl { get; set; } = "https://ai.nexa/runner.html";

        /// <summary>Currently selected model name (e.g. "llama3", "codestral").</summary>
        public string SelectedModel { get; set; } = "";

        /// <summary>Custom system prompt for the AI. Empty uses the built-in default.</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>Maximum tokens for AI responses. 0 = unlimited/backend default.</summary>
        public int MaxTokens { get; set; } = 0;

        /// <summary>API key for cloud-based backends (e.g. OpenAI ChatGPT).</summary>
        public string ApiKey { get; set; } = "";

        // ── Context Permissions ──────────────────────────────────

        /// <summary>Allow the AI to see the current page URL.</summary>
        public bool ContextUrl { get; set; } = true;

        /// <summary>Allow the AI to see the current page title.</summary>
        public bool ContextTitle { get; set; } = true;

        /// <summary>Allow the AI to see the user's selected text.</summary>
        public bool ContextSelection { get; set; } = false;

        /// <summary>Allow the AI to see the full page content.</summary>
        public bool ContextPage { get; set; } = false;

        /// <summary>Allow the AI to see the list of open tabs.</summary>
        public bool ContextTabs { get; set; } = false;

        // ── Coding Agent ─────────────────────────────────────────

        /// <summary>Whether the coding agent feature is enabled.</summary>
        public bool CodingAgentEnabled { get; set; } = false;

        /// <summary>Working directory for coding agent file operations.</summary>
        public string CodingAgentWorkDir { get; set; } = "";
    }
}
