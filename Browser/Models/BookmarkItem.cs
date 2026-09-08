using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Browser.Models
{
    /// <summary>
    /// Represents a saved bookmark.
    /// </summary>
    public class BookmarkItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _url = string.Empty;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Url
        {
            get => _url;
            set
            {
                _url = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FaviconUrl));
            }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string FaviconUrl
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_url)) return string.Empty;
                try
                {
                    if (Uri.TryCreate(_url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                    {
                        return $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=32";
                    }
                }
                catch { }
                return string.Empty;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
