using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Manages bookmarks with JSON persistence.
    /// </summary>
    public class BookmarkService
    {
        private static readonly Lazy<BookmarkService> _instance = new(() => new BookmarkService());
        public static BookmarkService Instance => _instance.Value;

        private readonly string _filePath;
        private readonly object _lock = new();

        public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();

        public event Action? BookmarksChanged;

        private BookmarkService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _filePath = Path.Combine(appFolder, "bookmarks.json");

            Load();
        }

        public bool IsBookmarked(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Bookmarks.Any(b => b.Url.TrimEnd('/').Equals(url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }

        public bool ToggleBookmark(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return false;

            var existing = Bookmarks.FirstOrDefault(b => b.Url.TrimEnd('/').Equals(url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Bookmarks.Remove(existing);
                Save();
                BookmarksChanged?.Invoke();
                GoogleSyncService.Instance.NotifyDataChanged();
                return false;
            }
            else
            {
                var item = new BookmarkItem
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? url : title,
                    CreatedAt = DateTime.Now
                };
                Bookmarks.Add(item);
                Save();
                BookmarksChanged?.Invoke();
                GoogleSyncService.Instance.NotifyDataChanged();
                return true;
            }
        }

        public void RemoveBookmark(BookmarkItem bookmark)
        {
            Bookmarks.Remove(bookmark);
            Save();
            BookmarksChanged?.Invoke();
            GoogleSyncService.Instance.NotifyDataChanged();
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(Bookmarks.ToList(), new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_filePath, json);
                }
                catch
                {
                    // Ignore save errors
                }
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var list = JsonSerializer.Deserialize<List<BookmarkItem>>(json);
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            Bookmarks.Add(item);
                        }
                    }
                }
            }
            catch
            {
                // Ignore load errors
            }
        }
    }
}
