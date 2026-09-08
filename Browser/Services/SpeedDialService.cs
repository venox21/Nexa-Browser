using System.IO;
using System.Text.Json;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Manages start page favorites (Speed-Dial) with persistent JSON storage.
    /// </summary>
    public class SpeedDialService
    {
        private static readonly Lazy<SpeedDialService> _instance = new(() => new SpeedDialService());
        public static SpeedDialService Instance => _instance.Value;

        private readonly string _filePath;
        private readonly object _lock = new();

        public List<SpeedDialItem> Tiles { get; private set; } = new();

        public event Action<List<SpeedDialItem>>? SpeedDialChanged;

        private SpeedDialService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _filePath = Path.Combine(appFolder, "speed_dial.json");

            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                if (File.Exists(_filePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_filePath);
                        var items = JsonSerializer.Deserialize<List<SpeedDialItem>>(json);
                        if (items != null && items.Count > 0)
                        {
                            Tiles = items;
                            return;
                        }
                    }
                    catch
                    {
                        // Fallback on corrupt file
                    }
                }

                Tiles = GetDefaultTiles();
                Save();
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(Tiles, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_filePath, json);
                }
                catch
                {
                    // Ignore write errors
                }
            }
        }

        public void SaveTiles(List<SpeedDialItem> tiles)
        {
            if (tiles == null) return;

            lock (_lock)
            {
                Tiles = new List<SpeedDialItem>(tiles);
                Save();
            }

            SpeedDialChanged?.Invoke(Tiles);
        }

        public string GetTilesJson()
        {
            lock (_lock)
            {
                return JsonSerializer.Serialize(Tiles);
            }
        }

        public static List<SpeedDialItem> GetDefaultTiles()
        {
            return new List<SpeedDialItem>
            {
                new() { Name = "YouTube", Url = "https://www.youtube.com", Color = "#dc2626", Icon = "▶" },
                new() { Name = "GitHub", Url = "https://github.com", Color = "#4f46e5", Icon = "⚡" },
                new() { Name = "Wikipedia", Url = "https://de.wikipedia.org", Color = "#0284c7", Icon = "W" },
                new() { Name = "Reddit", Url = "https://www.reddit.com", Color = "#ea580c", Icon = "R" },
                new() { Name = "ChatGPT", Url = "https://chatgpt.com", Color = "#10b981", Icon = "AI" },
                new() { Name = "Amazon", Url = "https://www.amazon.de", Color = "#d97706", Icon = "A" }
            };
        }
    }
}
