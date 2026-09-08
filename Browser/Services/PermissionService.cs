using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Stores website permission decisions (Camera, Microphone, Geolocation, etc.).
    /// </summary>
    public class PermissionService
    {
        private static readonly Lazy<PermissionService> _instance = new(() => new PermissionService());
        public static PermissionService Instance => _instance.Value;

        private readonly string _filePath;
        private readonly Dictionary<string, int> _permissions = new();
        private readonly object _lock = new();

        private PermissionService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _filePath = Path.Combine(appFolder, "permissions.json");

            Load();
        }

        public bool TryGetPermission(string host, CoreWebView2PermissionKind kind, out CoreWebView2PermissionState state)
        {
            lock (_lock)
            {
                var key = $"{host.ToLowerInvariant()}:{kind}";
                if (_permissions.TryGetValue(key, out int rawState))
                {
                    state = (CoreWebView2PermissionState)rawState;
                    return true;
                }
            }

            state = CoreWebView2PermissionState.Default;
            return false;
        }

        public void SetPermission(string host, CoreWebView2PermissionKind kind, CoreWebView2PermissionState state)
        {
            lock (_lock)
            {
                var key = $"{host.ToLowerInvariant()}:{kind}";
                _permissions[key] = (int)state;
                Save();
            }
        }

        public Dictionary<string, int> GetAllPermissions()
        {
            lock (_lock)
            {
                return new Dictionary<string, int>(_permissions);
            }
        }

        public bool RemovePermission(string key)
        {
            lock (_lock)
            {
                if (_permissions.Remove(key))
                {
                    Save();
                    return true;
                }
                return false;
            }
        }

        public void ClearAllPermissions()
        {
            lock (_lock)
            {
                _permissions.Clear();
                Save();
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_permissions, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                            _permissions[kvp.Key] = kvp.Value;
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
