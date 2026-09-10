using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Browser.Resources;
using Browser.Services;

namespace Browser.Views
{
    public partial class UpdateCheckWindow : Window
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private bool _isUpdateAvailable = false;
        private string _downloadUrl = string.Empty;
        private bool _isDownloading = false;

        public UpdateCheckWindow(bool isUpdateAvailable = false, string availableVersion = "2.0.0")
        {
            InitializeComponent();
            _isUpdateAvailable = isUpdateAvailable;
            _downloadUrl = "https://github.com/venox21/Nexa-Browser/releases/latest/download/NexaSetup.exe";

            TxtCheckedTimestamp.Text = $"Geprüft: Heute, {DateTime.Now:HH:mm} Uhr";
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Query GitHub online asynchronously
            try
            {
                var result = await UpdateService.Instance.CheckForUpdatesAsync();
                PanelChecking.Visibility = Visibility.Collapsed;

                if (result.IsUpdateAvailable)
                {
                    _isUpdateAvailable = true;
                    _downloadUrl = result.DownloadUrl;

                    PanelUpToDate.Visibility = Visibility.Collapsed;
                    PanelUpdateAvailable.Visibility = Visibility.Visible;

                    TxtUpgradePill.Text = $"v{BrandingConfig.BrowserVersion}  ➜  v{result.LatestVersion}";
                    if (result.Changelog.Count > 0)
                    {
                        TxtChangelogBrief.Text = string.Join("\n• ", result.Changelog);
                    }

                    BtnAction.Content = "Jetzt aktualisieren";
                    BtnSecondary.Visibility = Visibility.Visible;
                }
                else
                {
                    _isUpdateAvailable = false;
                    PanelUpdateAvailable.Visibility = Visibility.Collapsed;
                    PanelUpToDate.Visibility = Visibility.Visible;

                    if (!result.IsSuccess)
                    {
                        TxtCurrentVersionPill.Text = "Offline / GitHub nicht erreichbar";
                    }
                    else
                    {
                        TxtCurrentVersionPill.Text = $"Version {BrandingConfig.BrowserVersion} (Aktuell)";
                    }
                    BtnAction.Content = "Schließen";
                    BtnSecondary.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                // Fallback to up-to-date
                PanelChecking.Visibility = Visibility.Collapsed;
                PanelUpToDate.Visibility = Visibility.Visible;
                PanelUpdateAvailable.Visibility = Visibility.Collapsed;
                TxtCurrentVersionPill.Text = $"Version {BrandingConfig.BrowserVersion} (Aktuell)";
                BtnAction.Content = "Schließen";
            }
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int darkMode = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSecondary_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUpdateAvailable)
            {
                Close();
                return;
            }

            if (_isDownloading) return;
            _isDownloading = true;

            BtnAction.IsEnabled = false;
            BtnAction.Content = "Wird heruntergeladen...";
            PanelDownloadProgress.Visibility = Visibility.Visible;

            try
            {
                var progress = new Progress<double>(percent =>
                {
                    DownloadProgressBar.Value = percent;
                    TxtDownloadProgress.Text = $"Lade NexaSetup.exe von GitHub herunter... {(int)percent}%";
                });

                var installerPath = await UpdateService.Instance.DownloadInstallerAsync(_downloadUrl, progress);
                TxtDownloadProgress.Text = "Download abgeschlossen! Starte Installer...";

                await System.Threading.Tasks.Task.Delay(500);
                UpdateService.LaunchInstallerAndExit(installerPath);
            }
            catch (Exception ex)
            {
                PanelDownloadProgress.Visibility = Visibility.Collapsed;
                BtnAction.IsEnabled = true;
                BtnAction.Content = "Im Browser öffnen";

                var res = MessageBox.Show(
                    $"Automatischer Download konnte nicht abgeschlossen werden:\n{ex.Message}\n\nMöchtest du die GitHub-Releases-Seite öffnen?",
                    "Update-Download",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://github.com/venox21/Nexa-Browser/releases",
                            UseShellExecute = true
                        });
                        Close();
                    }
                    catch { }
                }
            }
            finally
            {
                _isDownloading = false;
            }
        }
    }
}
