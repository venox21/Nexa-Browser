using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace Browser.Models
{
    public enum DownloadStatus
    {
        InProgress,
        Completed,
        Cancelled,
        Interrupted
    }

    public enum DownloadRiskLevel
    {
        Safe,
        Warning
    }

    /// <summary>
    /// Represents a downloaded file or ongoing download operation.
    /// </summary>
    public class DownloadItem : INotifyPropertyChanged
    {
        private long _receivedBytes;
        private long _totalBytes;
        private DownloadStatus _status = DownloadStatus.InProgress;
        private string _errorMessage = string.Empty;
        private DownloadRiskLevel _riskLevel = DownloadRiskLevel.Safe;
        private string _riskReason = string.Empty;
        private bool _isWarningDismissed;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public DownloadRiskLevel RiskLevel
        {
            get => _riskLevel;
            set { _riskLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsHighRisk)); OnPropertyChanged(nameof(ShowWarningPrompt)); }
        }

        public string RiskReason
        {
            get => _riskReason;
            set { _riskReason = value; OnPropertyChanged(); }
        }

        public bool IsWarningDismissed
        {
            get => _isWarningDismissed;
            set { _isWarningDismissed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowWarningPrompt)); }
        }

        public bool IsHighRisk => _riskLevel == DownloadRiskLevel.Warning;

        public bool ShowWarningPrompt => IsHighRisk && !_isWarningDismissed;

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility WarningBadgeVisibility => ShowWarningPrompt ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public DateTime StartTime { get; set; } = DateTime.Now;

        public long ReceivedBytes
        {
            get => _receivedBytes;
            set
            {
                _receivedBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public long TotalBytes
        {
            get => _totalBytes;
            set
            {
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public bool IsInProgress => Status == DownloadStatus.InProgress;
        public bool IsCompleted => Status == DownloadStatus.Completed;
        public bool IsInterrupted => Status == DownloadStatus.Interrupted;
        public bool IsCancelled => Status == DownloadStatus.Cancelled;

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility InProgressVisibility => IsInProgress ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility CompletedVisibility => IsCompleted ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public DownloadStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(IsInProgress));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsInterrupted));
                OnPropertyChanged(nameof(IsCancelled));
                OnPropertyChanged(nameof(InProgressVisibility));
                OnPropertyChanged(nameof(CompletedVisibility));
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public int ProgressPercent
        {
            get => TotalBytes > 0 ? Math.Clamp((int)((ReceivedBytes * 100) / TotalBytes), 0, 100) : 0;
            set { }
        }

        public string ProgressText
        {
            get
            {
                if (TotalBytes > 0)
                    return $"{FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes)} ({ProgressPercent}%)";
                return FormatBytes(ReceivedBytes);
            }
        }

        public string StatusText => Status switch
        {
            DownloadStatus.InProgress => TotalBytes > 0 
                ? $"{ProgressPercent}% • {FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes)}" 
                : $"{FormatBytes(ReceivedBytes)} heruntergeladen...",
            DownloadStatus.Completed => $"✓ Fertig heruntergeladen ({FormatBytes(ReceivedBytes > 0 ? ReceivedBytes : TotalBytes)})",
            DownloadStatus.Cancelled => "Download abgebrochen",
            DownloadStatus.Interrupted => string.IsNullOrEmpty(ErrorMessage) ? "Download fehlgeschlagen" : $"Fehlgeschlagen: {ErrorMessage}",
            _ => string.Empty
        };

        public bool CanCancel => Status == DownloadStatus.InProgress;
        public bool CanOpen => Status == DownloadStatus.Completed;

        [System.Text.Json.Serialization.JsonIgnore]
        public CoreWebView2DownloadOperation? Operation { get; set; }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.#} {sizes[order]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
