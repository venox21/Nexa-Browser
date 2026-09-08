using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Manages browser history with JSON persistence and search.
    /// Uses debounced writes to reduce disk I/O during rapid navigation.
    /// </summary>
    public class HistoryService
    {
        private static readonly Lazy<HistoryService> _instance = new(() => new HistoryService());
        public static HistoryService Instance => _instance.Value;

        private readonly string _historyFilePath;
        private readonly object _lock = new();
        private const int MaxEntries = 2000;

        // Debounced save: coalesce writes during rapid navigation
        private System.Threading.Timer? _saveTimer;
        private volatile bool _isDirty;

        public ObservableCollection<HistoryEntry> History { get; } = new();

        private HistoryService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _historyFilePath = Path.Combine(appFolder, "history.json");

            Load();
        }

        public void AddEntry(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;

            var displayTitle = string.IsNullOrWhiteSpace(title) ? url : title;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                // If top item is identical URL, just update title and timestamp
                if (History.Count > 0 && History[0].Url.Equals(url, StringComparison.OrdinalIgnoreCase))
                {
                    History[0].Title = displayTitle;
                    History[0].VisitedAt = DateTime.Now;
                }
                else
                {
                    History.Insert(0, new HistoryEntry
                    {
                        Url = url,
                        Title = displayTitle,
                        VisitedAt = DateTime.Now
                    });

                    if (History.Count > MaxEntries)
                    {
                        History.RemoveAt(History.Count - 1);
                    }
                }

                // Debounced save: wait 2 seconds before writing to disk
                ScheduleDebouncedSave();
            });
        }

        /// <summary>
        /// Schedules a debounced save after 2 seconds. Resets the timer on each call
        /// to coalesce rapid navigation events into a single disk write.
        /// </summary>
        private void ScheduleDebouncedSave()
        {
            _isDirty = true;
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ => FlushSave(), null, 2000, Timeout.Infinite);
        }

        /// <summary>
        /// Immediately flushes pending history changes to disk.
        /// Called by the debounce timer and on application shutdown.
        /// </summary>
        public void FlushSave()
        {
            if (!_isDirty) return;
            _isDirty = false;
            try
            {
                Application.Current?.Dispatcher.Invoke(() => Save());
            }
            catch
            {
                // Dispatcher may be unavailable during shutdown
                Save();
            }
        }

        public void DeleteEntry(HistoryEntry entry)
        {
            History.Remove(entry);
            Save();
        }

        public void ClearAll()
        {
            History.Clear();
            Save();
        }

        public void ClearRange(DateTime from, DateTime to)
        {
            var toRemove = History.Where(e => e.VisitedAt >= from && e.VisitedAt <= to).ToList();
            foreach (var item in toRemove)
            {
                History.Remove(item);
            }
            Save();
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var list = History.Take(MaxEntries).ToList();
                    var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_historyFilePath, json);
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
                if (File.Exists(_historyFilePath))
                {
                    var json = File.ReadAllText(_historyFilePath);
                    var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            History.Add(item);
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
