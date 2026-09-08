using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Manages file downloads, track progress, persistence, and shell actions.
    /// </summary>
    public class DownloadManager
    {
        private static readonly Lazy<DownloadManager> _instance = new(() => new DownloadManager());
        public static DownloadManager Instance => _instance.Value;

        private readonly string _historyFilePath;
        private readonly object _saveLock = new();

        public ObservableCollection<DownloadItem> Downloads { get; } = new();

        public event Action? ActiveCountChanged;
        public event Action<DownloadItem>? DownloadStarted;
        public event Action<DownloadItem>? DownloadCompleted;

        public int ActiveDownloadsCount => Downloads.Count(d => d.Status == DownloadStatus.InProgress);

        private DownloadManager()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _historyFilePath = Path.Combine(appFolder, "downloads.json");

            LoadHistory();
        }

        public DownloadItem RegisterDownload(CoreWebView2DownloadStartingEventArgs e, Action<string>? onStatusUpdate = null)
        {
            var targetFolder = SettingsService.Instance.DownloadFolder;
            try
            {
                Directory.CreateDirectory(targetFolder);
            }
            catch { }

            var suggested = Path.GetFileName(e.ResultFilePath ?? "download");
            var (risk, reason) = EvaluateFileRisk(suggested, e.DownloadOperation?.Uri);

            var item = new DownloadItem
            {
                FileName = Path.GetFileName(e.ResultFilePath ?? "download"),
                FilePath = e.ResultFilePath ?? string.Empty,
                Uri = e.DownloadOperation?.Uri ?? string.Empty,
                TotalBytes = (long)(e.DownloadOperation?.TotalBytesToReceive ?? 0),
                ReceivedBytes = 0,
                Status = DownloadStatus.InProgress,
                Operation = e.DownloadOperation,
                RiskLevel = risk,
                RiskReason = reason
            };

            Application.Current?.Dispatcher.Invoke(() =>
            {
                Downloads.Insert(0, item);
                ActiveCountChanged?.Invoke();
                DownloadStarted?.Invoke(item);
            });

            onStatusUpdate?.Invoke($"Download gestartet: {item.FileName}");

            var op = e.DownloadOperation;
            if (op == null) return item;

            op.BytesReceivedChanged += (s, args) =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    item.ReceivedBytes = (long)op.BytesReceived;
                    if (op.TotalBytesToReceive.HasValue && op.TotalBytesToReceive.Value > 0)
                    {
                        item.TotalBytes = (long)op.TotalBytesToReceive.Value;
                    }
                });
            };

            op.StateChanged += (s, args) =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    switch (op.State)
                    {
                        case CoreWebView2DownloadState.Completed:
                            item.Status = DownloadStatus.Completed;
                            item.ReceivedBytes = item.TotalBytes > 0 ? item.TotalBytes : (long)op.BytesReceived;
                            onStatusUpdate?.Invoke($"✓ Download abgeschlossen: {item.FileName}");
                            SaveHistory();
                            DownloadCompleted?.Invoke(item);
                            break;

                        case CoreWebView2DownloadState.Interrupted:
                            item.Status = DownloadStatus.Interrupted;
                            item.ErrorMessage = op.InterruptReason.ToString();
                            onStatusUpdate?.Invoke($"Download abgebrochen: {item.FileName}");
                            SaveHistory();
                            break;
                    }
                    ActiveCountChanged?.Invoke();
                });
            };

            return item;
        }

        public void KeepAndResume(DownloadItem item)
        {
            if (item == null) return;
            item.IsWarningDismissed = true;

            try
            {
                if (item.Operation != null && item.Operation.CanResume)
                {
                    item.Operation.Resume();
                }
            }
            catch { }

            // Check if file is already on disk and complete
            if (File.Exists(item.FilePath))
            {
                try
                {
                    var fileInfo = new FileInfo(item.FilePath);
                    if (fileInfo.Length > 0 && (item.TotalBytes <= 0 || fileInfo.Length >= item.TotalBytes || item.Status == DownloadStatus.Completed))
                    {
                        item.Status = DownloadStatus.Completed;
                        item.ReceivedBytes = fileInfo.Length;
                        DownloadCompleted?.Invoke(item);
                    }
                }
                catch { }
            }
            SaveHistory();
        }

        public void CancelDownload(DownloadItem item)
        {
            try
            {
                if (item.Status == DownloadStatus.InProgress && item.Operation != null)
                {
                    item.Operation.Cancel();
                    item.Status = DownloadStatus.Cancelled;
                    ActiveCountChanged?.Invoke();
                    SaveHistory();
                }
            }
            catch
            {
                // Ignore cancel failures
            }
        }

        public void OpenFile(DownloadItem item)
        {
            if (File.Exists(item.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.FilePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Datei konnte nicht geöffnet werden: {ex.Message}", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show("Die Datei wurde verschoben oder gelöscht.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        public void OpenFolder(DownloadItem item)
        {
            try
            {
                if (File.Exists(item.FilePath))
                {
                    Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
                }
                else if (Directory.Exists(Path.GetDirectoryName(item.FilePath)))
                {
                    Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(item.FilePath)}\"");
                }
            }
            catch
            {
                // Ignore explorer errors
            }
        }

        public void RemoveDownload(DownloadItem item)
        {
            if (item == null) return;
            Downloads.Remove(item);
            ActiveCountChanged?.Invoke();
            SaveHistory();
        }

        public void ClearHistory()
        {
            var nonActive = Downloads.Where(d => d.Status != DownloadStatus.InProgress).ToList();
            foreach (var item in nonActive)
            {
                Downloads.Remove(item);
            }
            SaveHistory();
        }

        private void SaveHistory()
        {
            lock (_saveLock)
            {
                try
                {
                    var completed = Downloads
                        .Where(d => d.Status != DownloadStatus.InProgress)
                        .Take(50)
                        .ToList();

                    var json = JsonSerializer.Serialize(completed, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_historyFilePath, json);
                }
                catch
                {
                    // Ignore save errors
                }
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    var json = File.ReadAllText(_historyFilePath);
                    var items = JsonSerializer.Deserialize<List<DownloadItem>>(json);
                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            Downloads.Add(item);
                        }
                    }
                }
            }
            catch
            {
                // Ignore load errors
            }
        }
        private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".scr", ".pif", ".com",
            ".iso", ".img", ".vhd", ".vhdx", ".reg", ".jar", ".hta", ".cpl", ".msc"
        };

        public static (DownloadRiskLevel Risk, string Reason) EvaluateFileRisk(string fileName, string? uri = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return (DownloadRiskLevel.Safe, "");

            // Whitelist official Nexa installers and releases
            if (fileName.StartsWith("NexaSetup", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Nexa-Browser", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("NexaBrowser", StringComparison.OrdinalIgnoreCase))
            {
                return (DownloadRiskLevel.Safe, "");
            }

            if (!string.IsNullOrEmpty(uri) && (
                uri.Contains("github.com/venox21/Nexa-Browser", StringComparison.OrdinalIgnoreCase) ||
                (uri.Contains("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && uri.Contains("Nexa", StringComparison.OrdinalIgnoreCase))))
            {
                return (DownloadRiskLevel.Safe, "");
            }

            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            // Check double extension trick: e.g. "Rechnung.pdf.exe"
            var nameWithoutLastExt = Path.GetFileNameWithoutExtension(fileName);
            var secondExt = Path.GetExtension(nameWithoutLastExt).ToLowerInvariant();
            if (!string.IsNullOrEmpty(secondExt) && DangerousExtensions.Contains(ext))
            {
                return (DownloadRiskLevel.Warning, $"Verdächtige Doppel-Dateiendung ({secondExt}{ext}) – Möglicher Versuch zur Verschleierung von Schadsoftware.");
            }

            if (DangerousExtensions.Contains(ext))
            {
                return (DownloadRiskLevel.Warning, $"Ausführbare Datei ({ext}) – Bitte prüfe, ob du dieser Datei vertraust.");
            }

            return (DownloadRiskLevel.Safe, "");
        }
    }
}
