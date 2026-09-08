using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace Browser.Models
{
    /// <summary>
    /// Represents a single browser tab with its own WebView2 instance.
    /// </summary>
    public class BrowserTab : INotifyPropertyChanged
    {
        private string _title = "Neuer Tab";
        private string _url = string.Empty;
        private bool _isActive;
        private bool _isLoading;
        private bool _isPinned;
        private ImageSource? _faviconSource;

        public BrowserTab()
        {
            Id = Guid.NewGuid().ToString();
            WebView = new WebView2();
        }

        /// <summary>Unique tab identifier.</summary>
        public string Id { get; }

        /// <summary>The WebView2 control for this tab.</summary>
        public WebView2 WebView { get; }

        /// <summary>Display title (from page or default).</summary>
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        /// <summary>Current URL.</summary>
        public string Url
        {
            get => _url;
            set
            {
                _url = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSettingsTab));
            }
        }

        /// <summary>Optional custom FrameworkElement (e.g. In-Tab Settings) instead of WebView2.</summary>
        public System.Windows.FrameworkElement? CustomView { get; set; }

        /// <summary>Whether this tab represents the internal settings page.</summary>
        public bool IsSettingsTab => Url.Equals("about:settings", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether this tab is the active/visible tab.</summary>
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); }
        }

        /// <summary>Whether the tab is currently loading a page.</summary>
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        /// <summary>Whether the tab is pinned to the left.</summary>
        public bool IsPinned
        {
            get => _isPinned;
            set { _isPinned = value; OnPropertyChanged(); }
        }

        /// <summary>Favicon image source for the tab.</summary>
        public ImageSource? FaviconSource
        {
            get => _faviconSource;
            set
            {
                _faviconSource = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFavicon));
            }
        }

        private string? _groupName;
        private string? _groupColor;

        /// <summary>Whether a valid favicon is available.</summary>
        public bool HasFavicon => _faviconSource != null;

        /// <summary>Group name for tab categorization (e.g. Work, Research).</summary>
        public string? GroupName
        {
            get => _groupName;
            set
            {
                _groupName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGrouped));
                OnPropertyChanged(nameof(GroupBadge));
            }
        }

        /// <summary>Group color in hex format (e.g. #3B82F6).</summary>
        public string? GroupColor
        {
            get => _groupColor;
            set
            {
                _groupColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GroupBrush));
            }
        }

        /// <summary>Whether this tab belongs to a tab group.</summary>
        public bool IsGrouped => !string.IsNullOrWhiteSpace(_groupName);

        /// <summary>Short badge text for group display.</summary>
        public string GroupBadge => _groupName ?? string.Empty;

        /// <summary>WPF Brush for the group color badge.</summary>
        public Brush GroupBrush
        {
            get
            {
                if (!string.IsNullOrEmpty(_groupColor))
                {
                    try
                    {
                        var color = (Color)ColorConverter.ConvertFromString(_groupColor);
                        return new SolidColorBrush(color);
                    }
                    catch
                    {
                        // Ignore parse failure
                    }
                }
                return Brushes.Transparent;
            }
        }

        private bool _isPlayingAudio;
        private bool _isMuted;

        /// <summary>Whether the tab is currently playing audio.</summary>
        public bool IsPlayingAudio
        {
            get => _isPlayingAudio;
            set
            {
                _isPlayingAudio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowAudioIndicator));
                OnPropertyChanged(nameof(AudioIconGlyph));
                OnPropertyChanged(nameof(AudioToolTip));
            }
        }

        /// <summary>Whether the tab is currently muted.</summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowAudioIndicator));
                OnPropertyChanged(nameof(AudioIconGlyph));
                OnPropertyChanged(nameof(AudioToolTip));
            }
        }

        /// <summary>Whether to show the audio/mute indicator on the tab.</summary>
        public bool ShowAudioIndicator => _isPlayingAudio || _isMuted;

        /// <summary>Segoe Fluent Icons glyph for audio state.</summary>
        public string AudioIconGlyph => _isMuted ? "\uE74F" : "\uE767";

        /// <summary>Tooltip for the audio icon button.</summary>
        public string AudioToolTip => _isMuted ? "Stummschaltung aufheben" : "Tab stummschalten";

        private bool _isSleeping;
        private DateTime _lastActiveTime = DateTime.UtcNow;

        /// <summary>Whether this tab is sleeping (suspended) to save RAM and CPU.</summary>
        public bool IsSleeping
        {
            get => _isSleeping;
            set
            {
                _isSleeping = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TabOpacity));
                OnPropertyChanged(nameof(ShowSleepIndicator));
                OnPropertyChanged(nameof(SleepToolTip));
            }
        }

        /// <summary>Last time this tab was active or clicked.</summary>
        public DateTime LastActiveTime
        {
            get => _lastActiveTime;
            set => _lastActiveTime = value;
        }

        /// <summary>Visual opacity of the tab (dimmed when sleeping).</summary>
        public double TabOpacity => _isSleeping ? 0.62 : 1.0;

        /// <summary>Whether to show the sleeping moon indicator.</summary>
        public bool ShowSleepIndicator => _isSleeping;

        /// <summary>Segoe Fluent Icons glyph for sleep state (\uEC46 Moon/Night).</summary>
        public string SleepIconGlyph => "\uEC46";

        /// <summary>Tooltip for sleeping tab.</summary>
        public string SleepToolTip => _isSleeping
            ? $"{Title} (💤 Im Ruhezustand – RAM geschont. Klicken zum Aufwecken)"
            : Title;

        /// <summary>Whether the CoreWebView2 has been initialized.</summary>
        public bool IsInitialized { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
