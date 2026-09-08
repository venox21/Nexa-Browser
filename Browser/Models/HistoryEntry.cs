using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Browser.Models
{
    /// <summary>
    /// Represents a visited website in the browsing history.
    /// </summary>
    public class HistoryEntry : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _url = string.Empty;
        private DateTime _visitedAt = DateTime.Now;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        public DateTime VisitedAt
        {
            get => _visitedAt;
            set
            {
                _visitedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayDate));
                OnPropertyChanged(nameof(DisplayTime));
            }
        }

        public string DisplayTime => VisitedAt.ToString("HH:mm");
        public string DisplayDate => VisitedAt.ToString("dd.MM.yyyy");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
