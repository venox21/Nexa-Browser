using System.IO;
using System.Text.Json;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    public class SessionTabInfo
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
    }

    /// <summary>
    /// Manages persisting and restoring open tabs across sessions.
    /// </summary>
    public class SessionService
    {
        private static readonly Lazy<SessionService> _instance = new(() => new SessionService());
        public static SessionService Instance => _instance.Value;

        private readonly string _filePath;

        private SessionService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _filePath = Path.Combine(appFolder, "session.json");
        }

        public void SaveSession(IEnumerable<BrowserTab> tabs)
        {
            try
            {
                var list = tabs
                    .Where(t => !string.IsNullOrEmpty(t.Url) && !t.Url.StartsWith("about:"))
                    .Select(t => new SessionTabInfo
                    {
                        Url = t.Url,
                        Title = t.Title,
                        IsPinned = t.IsPinned
                    })
                    .ToList();

                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore session save errors
            }
        }

        public List<SessionTabInfo> LoadSession()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<List<SessionTabInfo>>(json) ?? new();
                }
            }
            catch
            {
                // Ignore load errors
            }
            return new();
        }

        public void ClearSession()
        {
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}
