using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NexaInstaller
{
    public partial class MainWindow : Window
    {
        // Windows 11 DWM Window Attributes
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private string _installedExePath = string.Empty;
        private string _targetInstallDir = string.Empty;
        private bool _isExistingInstall = false;
        private string _existingInstallPath = string.Empty;
        private string _existingVersion = string.Empty;
        private bool _isUninstallMode = false;

        private static readonly Version TargetVersion = new Version(2, 0, 0);
        private const string TargetVersionDisplay = "2.0.0";

        public MainWindow()
        {
            InitializeComponent();

            // Default path: %LocalAppData%\Programs\Nexa Browser
            _targetInstallDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Nexa Browser");

            TxtInstallPath.Text = _targetInstallDir;

            CheckForExistingInstallation();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            // Enable Windows 11 rounded corners and dark title bar
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int darkMode = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ── Installation Detection ──────────────────────────────────────────

        private void CheckForExistingInstallation()
        {
            try
            {
                // 1. Check default path
                var defaultExe = Path.Combine(_targetInstallDir, "Nexa.exe");
                if (File.Exists(defaultExe))
                {
                    _isExistingInstall = true;
                    _existingInstallPath = _targetInstallDir;
                }

                // 2. Check Windows Registry (Uninstall key)
                using var uninstKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexaBrowser");
                if (uninstKey != null)
                {
                    var loc = uninstKey.GetValue("InstallLocation") as string;
                    var ver = uninstKey.GetValue("DisplayVersion") as string;
                    if (!string.IsNullOrEmpty(ver))
                    {
                        _existingVersion = ver;
                    }

                    if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc) && File.Exists(Path.Combine(loc, "Nexa.exe")))
                    {
                        _isExistingInstall = true;
                        _existingInstallPath = loc;
                        _targetInstallDir = loc;
                        TxtInstallPath.Text = loc;
                    }
                }

                // Try reading FileVersion if not found in registry
                if (string.IsNullOrEmpty(_existingVersion) && !string.IsNullOrEmpty(_existingInstallPath) && File.Exists(Path.Combine(_existingInstallPath, "Nexa.exe")))
                {
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(Path.Combine(_existingInstallPath, "Nexa.exe"));
                        _existingVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "1.0.0";
                    }
                    catch { }
                }
            }
            catch { }

            UpdateUiForExistingInstallation();
        }

        private static Version ParseVersionString(string? verStr)
        {
            if (string.IsNullOrWhiteSpace(verStr)) return new Version(0, 0, 0);
            var cleaned = verStr.TrimStart('v', 'V').Trim();
            int dashIdx = cleaned.IndexOf('-');
            if (dashIdx > 0) cleaned = cleaned.Substring(0, dashIdx);
            int spaceIdx = cleaned.IndexOf(' ');
            if (spaceIdx > 0) cleaned = cleaned.Substring(0, spaceIdx);

            if (Version.TryParse(cleaned, out var parsed)) return parsed;
            var parts = cleaned.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int maj)) return new Version(maj, 0, 0);
            if (parts.Length == 2 && int.TryParse(parts[0], out int m1) && int.TryParse(parts[1], out int m2)) return new Version(m1, m2, 0);
            return new Version(0, 0, 0);
        }

        private void UpdateUiForExistingInstallation()
        {
            if (_isExistingInstall)
            {
                BorderExistingInstall.Visibility = Visibility.Visible;
                TxtDetectedPath.Text = _existingInstallPath;

                var installedVer = ParseVersionString(_existingVersion);

                if (installedVer < TargetVersion)
                {
                    // UPDATE AVAILABLE: Version goes up to 2.0!
                    TxtDetectedIcon.Text = "🚀";
                    TxtDetectedHeader.Text = "Update auf Version 2.0 verfügbar!";
                    TxtDetectedVersion.Text = $"v{_existingVersion} ➜ v2.0";
                    BorderExistingInstall.BorderBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
                    BorderExistingInstall.Background = new SolidColorBrush(Color.FromRgb(0x13, 0x17, 0x35));
                    ShadowExistingInstall.Color = Color.FromRgb(0x63, 0x66, 0xF1);

                    BtnWelcomeUpdate.Content = "🔄 Auf Version 2.0 aktualisieren";
                    BtnWelcomeUpdate.ToolTip = "Überschreibt die alte Version und aktualisiert auf Version 2.0";
                    BtnWelcomeReinstall.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // NO UPDATE AVAILABLE: Clean window state!
                    TxtDetectedIcon.Text = "✓";
                    TxtDetectedHeader.Text = "Nexa Browser ist auf dem neuesten Stand";
                    TxtDetectedVersion.Text = $"v{_existingVersion} (Aktuell)";
                    BorderExistingInstall.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    BorderExistingInstall.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x1B));
                    ShadowExistingInstall.Color = Color.FromRgb(0x10, 0xB9, 0x81);

                    BtnWelcomeUpdate.Content = "🚀 Browser starten";
                    BtnWelcomeUpdate.ToolTip = "Startet den installierten Nexa Browser v2.0";
                    BtnWelcomeReinstall.Visibility = Visibility.Visible;
                }

                PanelActionsExisting.Visibility = Visibility.Visible;
                PanelActionsNew.Visibility = Visibility.Collapsed;

                BtnCustomizeUpdate.Visibility = Visibility.Visible;
                BtnCustomizeUninstall.Visibility = Visibility.Visible;
            }
            else
            {
                BorderExistingInstall.Visibility = Visibility.Collapsed;
                PanelActionsExisting.Visibility = Visibility.Collapsed;
                PanelActionsNew.Visibility = Visibility.Visible;

                BtnCustomizeUpdate.Visibility = Visibility.Collapsed;
                BtnCustomizeUninstall.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtInstallPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtInstallPath.Text))
            {
                var path = TxtInstallPath.Text.Trim();
                _targetInstallDir = path;
                bool hasExe = File.Exists(Path.Combine(path, "Nexa.exe"));
                BtnCustomizeUpdate.Visibility = hasExe ? Visibility.Visible : Visibility.Collapsed;
                BtnCustomizeUninstall.Visibility = hasExe ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static void CloseRunningNexaProcesses()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("Nexa"))
                {
                    try
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(2000))
                        {
                            proc.Kill();
                            proc.WaitForExit(1000);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ── Wizard Page Switching ────────────────────────────────────────

        private void BtnGoCustomize_Click(object sender, RoutedEventArgs e)
        {
            PanelWelcome.Visibility = Visibility.Collapsed;
            PanelCustomize.Visibility = Visibility.Visible;
        }

        private void BtnBackToWelcome_Click(object sender, RoutedEventArgs e)
        {
            PanelCustomize.Visibility = Visibility.Collapsed;
            PanelWelcome.Visibility = Visibility.Visible;
        }

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Zielordner für Nexa Browser auswählen",
                InitialDirectory = !string.IsNullOrWhiteSpace(TxtInstallPath.Text) ? TxtInstallPath.Text : _targetInstallDir
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                _targetInstallDir = dialog.FolderName;
                TxtInstallPath.Text = _targetInstallDir;
            }
        }

        private async void BtnExpressInstall_Click(object sender, RoutedEventArgs e)
        {
            // Standard options
            ChkDesktopShortcut.IsChecked = true;
            ChkStartMenuShortcut.IsChecked = true;
            ChkRegisterDefaultBrowser.IsChecked = true;
            ChkLaunchAfterInstall.IsChecked = true;

            await StartInstallationAsync();
        }

        private async void BtnStartInstall_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtInstallPath.Text))
            {
                _targetInstallDir = TxtInstallPath.Text.Trim();
            }

            await StartInstallationAsync();
        }

        // ── Update Action (Overwriting existing version) ─────────────────────

        private void BtnWelcomeUpdate_Click(object sender, RoutedEventArgs e)
        {
            var installedVer = ParseVersionString(_existingVersion);
            if (installedVer >= TargetVersion)
            {
                // Already on v2.0 or higher: launch the browser directly!
                var exePath = Path.Combine(_existingInstallPath, "Nexa.exe");
                if (File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true
                        });
                        Close();
                        return;
                    }
                    catch { }
                }
            }

            // Otherwise run update to overwrite files and upgrade to Version 2.0!
            BtnUpdate_Click(sender, e);
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtInstallPath.Text))
            {
                _targetInstallDir = TxtInstallPath.Text.Trim();
            }

            _isUninstallMode = false;

            PanelWelcome.Visibility = Visibility.Collapsed;
            PanelCustomize.Visibility = Visibility.Collapsed;
            PanelInstalling.Visibility = Visibility.Visible;

            TxtInstallingTitle.Text = "Nexa Browser wird aktualisiert";

            try
            {
                await RunUpdateAsync();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Aktualisierung fehlgeschlagen";
                TxtDetail.Text = "Fehler: " + ex.Message;
                MessageBox.Show($"Fehler bei der Aktualisierung:\n\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                PanelInstalling.Visibility = Visibility.Collapsed;
                PanelWelcome.Visibility = Visibility.Visible;
            }
        }

        private async Task RunUpdateAsync()
        {
            SetProgress(5, "Aktualisierung wird vorbereitet...", "Schließe laufende Nexa-Instanzen...");
            await Task.Run(CloseRunningNexaProcesses);
            await Task.Delay(250);

            Directory.CreateDirectory(_targetInstallDir);

            SetProgress(15, "Browser-Dateien werden überschrieben...", "Extrahiere neue Dateien...");
            await Task.Delay(200);

            await Task.Run(() =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("NexaInstaller.Resources.app.zip");
                if (stream == null)
                {
                    throw new InvalidOperationException("Das eingebettete Installationspaket (app.zip) wurde nicht gefunden.");
                }

                using var archive = new ZipArchive(stream);
                int total = archive.Entries.Count;
                int current = 0;

                foreach (var entry in archive.Entries)
                {
                    current++;
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var destPath = Path.Combine(_targetInstallDir, entry.FullName);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    entry.ExtractToFile(destPath, overwrite: true);

                    if (current % 3 == 0 || current == total)
                    {
                        double p = 15 + (65.0 * current / Math.Max(1, total));
                        Dispatcher.Invoke(() =>
                        {
                            SetProgress(p, "Browser-Dateien werden überschrieben...", entry.Name);
                        });
                    }
                }
            });

            _installedExePath = Path.Combine(_targetInstallDir, "Nexa.exe");

            // Copy updated setup executable for uninstallation
            SetProgress(82, "Systemintegration wird aktualisiert...", "Aktualisiere Deinstallationsprogramm...");
            await Task.Delay(150);

            try
            {
                var currentExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                {
                    var destSetup = Path.Combine(_targetInstallDir, "NexaSetup.exe");
                    File.Copy(currentExe, destSetup, overwrite: true);
                }
            }
            catch { }

            var iconPath = Path.Combine(_targetInstallDir, "Assets", "nexa.ico");
            if (!File.Exists(iconPath)) iconPath = Path.Combine(_targetInstallDir, "nexa.ico");

            // Refresh shortcuts
            if (ChkDesktopShortcut.IsChecked == true)
            {
                SetProgress(88, "Verknüpfungen werden aktualisiert...", "Desktop-Verknüpfung...");
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var lnkPath = Path.Combine(desktop, "Nexa Browser.lnk");
                CreateWindowsShortcut(lnkPath, _installedExePath, _targetInstallDir, iconPath, "Nexa Browser");
            }

            if (ChkStartMenuShortcut.IsChecked == true)
            {
                SetProgress(92, "Verknüpfungen werden aktualisiert...", "Startmenü-Eintrag...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                Directory.CreateDirectory(startMenu);
                var lnkPath = Path.Combine(startMenu, "Nexa Browser.lnk");
                CreateWindowsShortcut(lnkPath, _installedExePath, _targetInstallDir, iconPath, "Nexa Browser");
            }

            SetProgress(96, "Windows 11 Registrierung...", "Aktualisiere Registry & Systemintegration...");
            await Task.Delay(150);
            RegisterInWindowsSystem(_targetInstallDir, iconPath, _installedExePath);

            if (ChkRegisterDefaultBrowser.IsChecked == true)
            {
                RegisterDefaultProtocols(_installedExePath);
            }

            SetProgress(100, "Aktualisierung erfolgreich abgeschlossen!", "Fertiggestellt");
            await Task.Delay(350);

            TxtFinishTitle.Text = "Aktualisierung auf Version 2.0 erfolgreich!";
            TxtFinishSubtitle.Text = "Nexa Browser wurde erfolgreich auf Version 2.0 aktualisiert.";
            TxtFinishIcon.Text = "✓";
            BorderFinishIcon.Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x2E, 0x24));
            BorderFinishIcon.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            BorderFinishLaunchCard.Visibility = Visibility.Visible;
            BtnFinish.Content = "Fertigstellen";

            PanelInstalling.Visibility = Visibility.Collapsed;
            PanelFinish.Visibility = Visibility.Visible;
        }

        // ── Uninstall Action (Deleting existing version) ─────────────────────

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            var pathToDelete = !string.IsNullOrWhiteSpace(TxtInstallPath.Text) ? TxtInstallPath.Text.Trim() : _targetInstallDir;

            var confirmResult = MessageBox.Show(
                $"Möchtest du Nexa Browser wirklich komplett von deinem Computer deinstallieren?\n\nInstallationsverzeichnis:\n{pathToDelete}",
                "Nexa Browser Deinstallation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            _isUninstallMode = true;

            PanelWelcome.Visibility = Visibility.Collapsed;
            PanelCustomize.Visibility = Visibility.Collapsed;
            PanelInstalling.Visibility = Visibility.Visible;

            TxtInstallingTitle.Text = "Nexa Browser wird deinstalliert";

            try
            {
                await RunUninstallAsync(pathToDelete);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Deinstallation fehlgeschlagen";
                TxtDetail.Text = "Fehler: " + ex.Message;
                MessageBox.Show($"Fehler bei der Deinstallation:\n\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                PanelInstalling.Visibility = Visibility.Collapsed;
                PanelWelcome.Visibility = Visibility.Visible;
            }
        }

        private async Task RunUninstallAsync(string installDir)
        {
            // Step 1: Close running processes
            SetProgress(15, "Laufende Prozesse beenden...", "Schließe Nexa Browser...");
            await Task.Run(CloseRunningNexaProcesses);
            await Task.Delay(250);

            // Step 2: Remove shortcuts
            SetProgress(35, "Verknüpfungen entfernen...", "Lösche Desktop- und Startmenü-Verknüpfungen...");
            await Task.Run(() =>
            {
                try
                {
                    var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Nexa Browser.lnk");
                    if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
                }
                catch { }

                try
                {
                    var startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Nexa Browser.lnk");
                    if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);
                }
                catch { }
            });
            await Task.Delay(200);

            // Step 3: Remove registry entries
            SetProgress(60, "Registrierung bereinigen...", "Entferne Windows-Systemeinträge...");
            await Task.Run(() =>
            {
                try
                {
                    using var uninstKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
                    uninstKey?.DeleteSubKeyTree("NexaBrowser", false);
                }
                catch { }

                try
                {
                    using var smiKey = Registry.CurrentUser.OpenSubKey(@"Software\Clients\StartMenuInternet", true);
                    smiKey?.DeleteSubKeyTree("NexaBrowser", false);
                }
                catch { }

                try
                {
                    using var regAppKey = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true);
                    regAppKey?.DeleteValue("NexaBrowser", false);
                }
                catch { }

                try
                {
                    using var classesKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
                    classesKey?.DeleteSubKeyTree("NexaHTML", false);
                }
                catch { }
            });
            await Task.Delay(200);

            // Step 4: Remove files
            SetProgress(80, "Dateien werden gelöscht...", "Entferne Programmdateien...");
            await Task.Run(() =>
            {
                if (Directory.Exists(installDir))
                {
                    var currentExe = Environment.ProcessPath ?? string.Empty;
                    bool isRunningFromInside = !string.IsNullOrEmpty(currentExe) && currentExe.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);

                    if (!isRunningFromInside)
                    {
                        try
                        {
                            Directory.Delete(installDir, recursive: true);
                        }
                        catch
                        {
                            var cmd = $"/c timeout /t 1 & rmdir /s /q \"{installDir}\"";
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = cmd,
                                WindowStyle = ProcessWindowStyle.Hidden,
                                CreateNoWindow = true
                            });
                        }
                    }
                    else
                    {
                        try
                        {
                            foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                            {
                                if (!file.Equals(currentExe, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { File.Delete(file); } catch { }
                                }
                            }
                        }
                        catch { }

                        var cmd = $"/c timeout /t 1 & rmdir /s /q \"{installDir}\"";
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = cmd,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        });
                    }
                }
            });

            SetProgress(100, "Deinstallation abgeschlossen!", "Erfolgreich entfernt");
            await Task.Delay(350);

            TxtFinishTitle.Text = "Deinstallation abgeschlossen";
            TxtFinishSubtitle.Text = "Nexa Browser wurde vollständig von deinem Computer entfernt.";
            TxtFinishIcon.Text = "🗑️";
            BorderFinishIcon.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x12, 0x16));
            BorderFinishIcon.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            BorderFinishLaunchCard.Visibility = Visibility.Collapsed;
            BtnFinish.Content = "Schließen";

            PanelInstalling.Visibility = Visibility.Collapsed;
            PanelFinish.Visibility = Visibility.Visible;
        }

        // ── Fresh Installation Logic ─────────────────────────────────────

        private async Task StartInstallationAsync()
        {
            _isUninstallMode = false;
            PanelWelcome.Visibility = Visibility.Collapsed;
            PanelCustomize.Visibility = Visibility.Collapsed;
            PanelInstalling.Visibility = Visibility.Visible;

            TxtInstallingTitle.Text = "Nexa Browser wird installiert";

            try
            {
                await RunInstallationAsync();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Installation fehlgeschlagen";
                TxtDetail.Text = "Fehler: " + ex.Message;
                MessageBox.Show($"Bei der Installation ist ein Fehler aufgetreten:\n\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                PanelInstalling.Visibility = Visibility.Collapsed;
                PanelCustomize.Visibility = Visibility.Visible;
            }
        }

        private async Task RunInstallationAsync()
        {
            SetProgress(5, "Installation wird vorbereitet...", "Schließe laufende Prozesse & erstelle Ordner...");
            await Task.Run(CloseRunningNexaProcesses);
            await Task.Delay(200);

            Directory.CreateDirectory(_targetInstallDir);

            // 1. Extract embedded app.zip
            SetProgress(15, "Browser-Dateien werden entpackt...", "Lese Archiv...");
            await Task.Delay(200);

            await Task.Run(() =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("NexaInstaller.Resources.app.zip");
                if (stream == null)
                {
                    throw new InvalidOperationException("Das eingebettete Installationspaket (app.zip) wurde nicht gefunden.");
                }

                using var archive = new ZipArchive(stream);
                int total = archive.Entries.Count;
                int current = 0;

                foreach (var entry in archive.Entries)
                {
                    current++;
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var destPath = Path.Combine(_targetInstallDir, entry.FullName);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    entry.ExtractToFile(destPath, overwrite: true);

                    // Update UI progress
                    if (current % 3 == 0 || current == total)
                    {
                        double p = 15 + (65.0 * current / Math.Max(1, total));
                        Dispatcher.Invoke(() =>
                        {
                            SetProgress(p, "Browser-Dateien werden entpackt...", entry.Name);
                        });
                    }
                }
            });

            _installedExePath = Path.Combine(_targetInstallDir, "Nexa.exe");

            // 2. Self-copy setup executable for clean uninstallation
            SetProgress(82, "Systemintegration wird eingerichtet...", "Kopiere Deinstallationsprogramm...");
            await Task.Delay(150);

            try
            {
                var currentExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                {
                    var destSetup = Path.Combine(_targetInstallDir, "NexaSetup.exe");
                    File.Copy(currentExe, destSetup, overwrite: true);
                }
            }
            catch { }

            // Locate icon
            var iconPath = Path.Combine(_targetInstallDir, "Assets", "nexa.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = Path.Combine(_targetInstallDir, "nexa.ico");
            }

            // 3. Desktop Shortcut
            if (ChkDesktopShortcut.IsChecked == true)
            {
                SetProgress(88, "Verknüpfungen werden erstellt...", "Desktop-Verknüpfung...");
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var lnkPath = Path.Combine(desktop, "Nexa Browser.lnk");
                CreateWindowsShortcut(lnkPath, _installedExePath, _targetInstallDir, iconPath, "Nexa Browser");
            }

            // 4. Start Menu Shortcut
            if (ChkStartMenuShortcut.IsChecked == true)
            {
                SetProgress(92, "Verknüpfungen werden erstellt...", "Startmenü-Eintrag...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                Directory.CreateDirectory(startMenu);
                var lnkPath = Path.Combine(startMenu, "Nexa Browser.lnk");
                CreateWindowsShortcut(lnkPath, _installedExePath, _targetInstallDir, iconPath, "Nexa Browser");
            }

            // 5. Register in Windows 11 Apps & Features + Standard Browser
            SetProgress(96, "Windows 11 Registrierung...", "Registriere Browser & Protokolle...");
            await Task.Delay(150);

            RegisterInWindowsSystem(_targetInstallDir, iconPath, _installedExePath);

            if (ChkRegisterDefaultBrowser.IsChecked == true)
            {
                RegisterDefaultProtocols(_installedExePath);
            }

            SetProgress(100, "Installation erfolgreich abgeschlossen!", "Fertiggestellt");
            await Task.Delay(350);

            TxtFinishTitle.Text = "Installation erfolgreich!";
            TxtFinishSubtitle.Text = "Nexa Browser wurde erfolgreich auf deinem Windows 11 PC eingerichtet.";
            TxtFinishIcon.Text = "✓";
            BorderFinishIcon.Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x2E, 0x24));
            BorderFinishIcon.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            BorderFinishLaunchCard.Visibility = Visibility.Visible;
            BtnFinish.Content = "Fertigstellen";

            // Switch to finish panel
            PanelInstalling.Visibility = Visibility.Collapsed;
            PanelFinish.Visibility = Visibility.Visible;
        }

        private void SetProgress(double percent, string status, string detail)
        {
            InstallProgressBar.Value = percent;
            TxtPercent.Text = $"{(int)percent}%";
            TxtStatus.Text = status;
            TxtDetail.Text = detail;
        }

        private void BtnFinish_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUninstallMode && ChkLaunchFinish.IsChecked == true && File.Exists(_installedExePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _installedExePath,
                        UseShellExecute = true
                    });
                }
                catch { }
            }

            Close();
        }

        // ── Native Windows Integration ───────────────────────────────────

        private static void CreateWindowsShortcut(string shortcutPath, string targetPath, string workingDir, string iconPath, string description)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        dynamic shortcut = shell.CreateShortcut(shortcutPath);
                        shortcut.TargetPath = targetPath;
                        shortcut.WorkingDirectory = workingDir;
                        shortcut.Description = description;
                        if (File.Exists(iconPath))
                        {
                            shortcut.IconLocation = $"{iconPath},0";
                        }
                        shortcut.Save();
                    }
                }
            }
            catch { }
        }

        private static void RegisterInWindowsSystem(string installDir, string iconPath, string exePath)
        {
            try
            {
                // 1. Windows 11 "Apps & Features" (Uninstall)
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexaBrowser"))
                {
                    if (key != null)
                    {
                        var uninstallerPath = Path.Combine(installDir, "NexaSetup.exe");
                        key.SetValue("DisplayName", "Nexa Browser");
                        key.SetValue("DisplayVersion", "2.0.0");
                        key.SetValue("Publisher", "Nexa");
                        key.SetValue("InstallLocation", installDir);
                        key.SetValue("UninstallString", $"\"{uninstallerPath}\" /uninstall");
                        key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /uninstall /silent");
                        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        if (File.Exists(iconPath))
                        {
                            key.SetValue("DisplayIcon", $"{iconPath},0");
                        }
                        else
                        {
                            key.SetValue("DisplayIcon", $"{exePath},0");
                        }

                        // Calculate estimated size in KB
                        try
                        {
                            long totalBytes = 0;
                            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                            {
                                totalBytes += new FileInfo(file).Length;
                            }
                            key.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
                        }
                        catch { }
                    }
                }

                // 2. Windows 11 StartMenuInternet & RegisteredApplications (Default Browser)
                using (var smiKey = Registry.CurrentUser.CreateSubKey(@"Software\Clients\StartMenuInternet\NexaBrowser"))
                {
                    if (smiKey != null)
                    {
                        smiKey.SetValue("", "Nexa Browser");

                        using var iconKey = smiKey.CreateSubKey("DefaultIcon");
                        iconKey?.SetValue("", $"{exePath},0");

                        using var shellKey = smiKey.CreateSubKey(@"shell\open\command");
                        shellKey?.SetValue("", $"\"{exePath}\"");

                        // Capabilities
                        using var capKey = smiKey.CreateSubKey("Capabilities");
                        if (capKey != null)
                        {
                            capKey.SetValue("ApplicationName", "Nexa Browser");
                            capKey.SetValue("ApplicationDescription", "Moderner, blitzschneller & KI-unterstützter Webbrowser für Windows 11");
                            capKey.SetValue("ApplicationIcon", $"{iconPath},0");

                            using var fileAssoc = capKey.CreateSubKey("FileAssociations");
                            fileAssoc?.SetValue(".htm", "NexaHTML");
                            fileAssoc?.SetValue(".html", "NexaHTML");
                            fileAssoc?.SetValue(".pdf", "NexaHTML");
                            fileAssoc?.SetValue(".svg", "NexaHTML");
                            fileAssoc?.SetValue(".xhtml", "NexaHTML");

                            using var urlAssoc = capKey.CreateSubKey("URLAssociations");
                            urlAssoc?.SetValue("http", "NexaHTML");
                            urlAssoc?.SetValue("https", "NexaHTML");
                            urlAssoc?.SetValue("nexa", "NexaHTML");
                        }
                    }
                }

                // 3. Register under RegisteredApplications so Windows 11 lists it
                using (var regAppKey = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                {
                    regAppKey?.SetValue("NexaBrowser", @"Software\Clients\StartMenuInternet\NexaBrowser\Capabilities");
                }

                // 4. ProgID: NexaHTML
                using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\NexaHTML"))
                {
                    if (progIdKey != null)
                    {
                        progIdKey.SetValue("", "Nexa HTML Document");
                        progIdKey.SetValue("AppUserModelID", "Nexa.Browser.App");

                        using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
                        iconKey?.SetValue("", $"{iconPath},0");

                        using var cmdKey = progIdKey.CreateSubKey(@"shell\open\command");
                        cmdKey?.SetValue("", $"\"{exePath}\" \"%1\"");
                    }
                }
            }
            catch { }
        }

        private static void RegisterDefaultProtocols(string exePath)
        {
            try
            {
                // Register user-choice protocol commands
                using var httpKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\http\shell\open\command");
                httpKey?.SetValue("", $"\"{exePath}\" \"%1\"");

                using var httpsKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\https\shell\open\command");
                httpsKey?.SetValue("", $"\"{exePath}\" \"%1\"");
            }
            catch { }
        }
    }
}
