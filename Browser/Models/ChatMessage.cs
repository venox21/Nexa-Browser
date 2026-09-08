using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Browser.Models
{
    /// <summary>
    /// Represents a single message in an AI chat conversation.
    /// </summary>
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _content = string.Empty;
        private bool _isStreaming;

        /// <summary>The role of the message sender.</summary>
        public ChatMessageRole Role { get; set; }

        /// <summary>The text content of the message.</summary>
        public string Content
        {
            get => _content;
            set { _content = value; OnPropertyChanged(); }
        }

        /// <summary>When the message was created.</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>Whether this message is currently being streamed (AI response in progress).</summary>
        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(); }
        }

        /// <summary>Description of the browser context that was attached to this message (if any).</summary>
        public string? ContextInfo { get; set; }

        /// <summary>Whether this message has attached browser context.</summary>
        public bool HasContext => !string.IsNullOrEmpty(ContextInfo);

        /// <summary>Whether this is a user message.</summary>
        public bool IsUser => Role == ChatMessageRole.User;

        /// <summary>Whether this is an assistant message.</summary>
        public bool IsAssistant => Role == ChatMessageRole.Assistant;

        /// <summary>Whether this is a system message.</summary>
        public bool IsSystem => Role == ChatMessageRole.System;

        /// <summary>Display-friendly timestamp.</summary>
        public string DisplayTime => Timestamp.ToString("HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// The role of a chat message sender.
    /// </summary>
    public enum ChatMessageRole
    {
        System,
        User,
        Assistant
    }
}
