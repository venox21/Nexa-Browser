using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Browser.Models
{
    /// <summary>
    /// Represents a saved login credential in the Nexa Password Vault.
    /// Compatible with Google Passwords / Chrome Passwords schema.
    /// </summary>
    public class PasswordEntry : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _url = string.Empty;
        private string _host = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _note = string.Empty;
        private bool _isRevealed;

        public Guid Id { get; set; } = Guid.NewGuid();

        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }

        public string Url
        {
            get => _url;
            set
            {
                if (_url != value)
                {
                    _url = value;
                    OnPropertyChanged();
                    UpdateHostFromUrl();
                }
            }
        }

        public string Host
        {
            get => _host;
            set { if (_host != value) { _host = value; OnPropertyChanged(); } }
        }

        public string Username
        {
            get => _username;
            set { if (_username != value) { _username = value; OnPropertyChanged(); } }
        }

        public string Password
        {
            get => _password;
            set
            {
                if (_password != value)
                {
                    _password = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPassword));
                    OnPropertyChanged(nameof(PasswordLengthMask));
                }
            }
        }

        public string Note
        {
            get => _note;
            set { if (_note != value) { _note = value; OnPropertyChanged(); } }
        }

        private int _breachCount;

        public int BreachCount
        {
            get => _breachCount;
            set
            {
                if (_breachCount != value)
                {
                    _breachCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsCompromised));
                    OnPropertyChanged(nameof(BreachBadgeText));
                    OnPropertyChanged(nameof(BreachBadgeVisibility));
                }
            }
        }

        public DateTime? LastSecurityCheck { get; set; }

        [JsonIgnore]
        public bool IsCompromised => _breachCount > 0;

        [JsonIgnore]
        public string BreachBadgeText => _breachCount > 0 ? $"⚠️ In {_breachCount:N0} Datenlecks gefunden" : "Sicher";

        [JsonIgnore]
        public System.Windows.Visibility BreachBadgeVisibility => _breachCount > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;

        [JsonIgnore]
        public bool IsRevealed
        {
            get => _isRevealed;
            set
            {
                if (_isRevealed != value)
                {
                    _isRevealed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPassword));
                    OnPropertyChanged(nameof(RevealIcon));
                }
            }
        }

        [JsonIgnore]
        public string DisplayPassword => _isRevealed ? _password : PasswordLengthMask;

        [JsonIgnore]
        public string PasswordLengthMask
        {
            get
            {
                int len = string.IsNullOrEmpty(_password) ? 8 : Math.Min(_password.Length, 16);
                return new string('•', Math.Max(len, 8));
            }
        }

        [JsonIgnore]
        public string RevealIcon => _isRevealed ? "\uED1A" : "\uE7B3"; // Hide / Show eye icon

        private void UpdateHostFromUrl()
        {
            if (string.IsNullOrWhiteSpace(_url)) return;
            try
            {
                var clean = _url.Trim();
                if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    clean = "https://" + clean;
                }
                if (Uri.TryCreate(clean, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                {
                    Host = uri.Host;
                    if (string.IsNullOrEmpty(Title))
                    {
                        var domain = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                            ? uri.Host.Substring(4)
                            : uri.Host;
                        Title = char.ToUpper(domain[0]) + domain.Substring(1);
                    }
                }
            }
            catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
