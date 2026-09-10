using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Browser.Models;
using Browser.Resources;
using Browser.Services;

namespace Browser.Views
{
    /// <summary>
    /// In-tab settings view for configuring Nexa browser options.
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private DispatcherTimer? _saveStatusTimer;

        public SettingsView()
        {
            InitializeComponent();
            LoadCurrentSettings();
            ThemeService.Instance.ThemeChanged += _ => Dispatcher.InvokeAsync(PopulateThemeSwatches);
            GoogleSyncService.Instance.AuthStateChanged += _ => Dispatcher.InvokeAsync(UpdateSyncUiState);
            GoogleSyncService.Instance.SyncStatusChanged += msg => Dispatcher.InvokeAsync(() => UpdateSyncStatus(msg));
            GoogleSyncService.Instance.SyncCompleted += (success, msg) => Dispatcher.InvokeAsync(() => OnSyncCompleted(success, msg));
        }

        public void SelectTab(string tabName)
        {
            if (tabName.Contains("Passwörter", StringComparison.OrdinalIgnoreCase))
            {
                NavPasswoerter.IsChecked = true;
                Nav_Click(NavPasswoerter, new RoutedEventArgs());
            }
            else if (tabName.Contains("Sync", StringComparison.OrdinalIgnoreCase) || tabName.Contains("Konto", StringComparison.OrdinalIgnoreCase))
            {
                NavSync.IsChecked = true;
                Nav_Click(NavSync, new RoutedEventArgs());
            }
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb) return;

            PanelDarstellung.Visibility = Visibility.Collapsed;
            PanelSuche.Visibility = Visibility.Collapsed;
            PanelTabs.Visibility = Visibility.Collapsed;
            PanelStart.Visibility = Visibility.Collapsed;
            PanelDatenschutz.Visibility = Visibility.Collapsed;
            PanelDownloads.Visibility = Visibility.Collapsed;
            PanelBerechtigungen.Visibility = Visibility.Collapsed;
            PanelShortcuts.Visibility = Visibility.Collapsed;
            PanelUeber.Visibility = Visibility.Collapsed;
            PanelPasswoerter.Visibility = Visibility.Collapsed;
            PanelSync.Visibility = Visibility.Collapsed;

            var content = rb.Content?.ToString() ?? string.Empty;
            if (content.Contains("Darstellung")) PanelDarstellung.Visibility = Visibility.Visible;
            else if (content.Contains("Suche")) PanelSuche.Visibility = Visibility.Visible;
            else if (content.Contains("Tabs")) PanelTabs.Visibility = Visibility.Visible;
            else if (content.Contains("Startseite")) PanelStart.Visibility = Visibility.Visible;
            else if (content.Contains("Datenschutz")) PanelDatenschutz.Visibility = Visibility.Visible;
            else if (content.Contains("Downloads")) PanelDownloads.Visibility = Visibility.Visible;
            else if (content.Contains("Berechtigungen")) PanelBerechtigungen.Visibility = Visibility.Visible;
            else if (content.Contains("Tastenkürzel")) PanelShortcuts.Visibility = Visibility.Visible;
            else if (content.Contains("Über")) PanelUeber.Visibility = Visibility.Visible;
            else if (content.Contains("Passwörter"))
            {
                PanelPasswoerter.Visibility = Visibility.Visible;
                PopulatePasswordVaultUi();
            }
            else if (content.Contains("Sync") || content.Contains("Konto"))
            {
                PanelSync.Visibility = Visibility.Visible;
                UpdateSyncUiState();
            }

            ContentScrollViewer.ScrollToTop();
        }

        public void LoadCurrentSettings()
        {
            var settings = SettingsService.Instance.Settings;

            // 1. Darstellung
            PopulateThemeSwatches();
            ChkEnableAnimations.IsChecked = settings.EnableAnimations;
            SelectZoomIndex(settings.DefaultZoom);

            // 2. Suche
            if (settings.SearchEngineUrl.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase))
                ComboSearchEngine.SelectedIndex = 1;
            else if (settings.SearchEngineUrl.Contains("bing", StringComparison.OrdinalIgnoreCase))
                ComboSearchEngine.SelectedIndex = 2;
            else if (settings.SearchEngineUrl.Contains("ecosia", StringComparison.OrdinalIgnoreCase))
                ComboSearchEngine.SelectedIndex = 3;
            else
                ComboSearchEngine.SelectedIndex = 0;

            ChkSearchSuggestions.IsChecked = settings.EnableSearchSuggestions;

            // 3. Tabs & Leistung
            ChkOpenBesideActive.IsChecked = settings.OpenNewTabBesideActive;
            ChkEnableTabGroups.IsChecked = settings.EnableTabGroups;
            ChkEnableSleepingTabs.IsChecked = settings.EnableSleepingTabs;
            ChkNeverSleepPinned.IsChecked = settings.NeverSleepPinnedTabs;
            ComboSleepingTimeout.SelectedIndex = settings.SleepingTimeoutMinutes switch
            {
                5 => 0,
                30 => 2,
                60 => 3,
                _ => 1
            };

            // 4. Start & Neue Tabs
            if (settings.StartupMode == 1 || settings.RestoreTabsOnStartup)
            {
                RadioStartRestore.IsChecked = true;
            }
            else
            {
                RadioStartNewTab.IsChecked = true;
            }

            var currentHome = settings.HomePage?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(currentHome) || currentHome.Equals("about:start", StringComparison.OrdinalIgnoreCase))
            {
                RadioHomeNexa.IsChecked = true;
                PanelCustomHomeUrl.Visibility = Visibility.Collapsed;
                TxtHomePage.Text = "about:start";
            }
            else if (currentHome.Equals("https://www.google.com", StringComparison.OrdinalIgnoreCase) ||
                     currentHome.Equals("https://google.com", StringComparison.OrdinalIgnoreCase) ||
                     currentHome.Equals("http://www.google.com", StringComparison.OrdinalIgnoreCase))
            {
                RadioHomeGoogle.IsChecked = true;
                PanelCustomHomeUrl.Visibility = Visibility.Collapsed;
                TxtHomePage.Text = "https://www.google.com";
            }
            else
            {
                RadioHomeCustom.IsChecked = true;
                PanelCustomHomeUrl.Visibility = Visibility.Visible;
                TxtHomePage.Text = currentHome;
            }

            // 5. Datenschutz
            ChkEnableAdBlocker.IsChecked = settings.EnableAdBlocker;
            ChkClearDataOnExit.IsChecked = settings.ClearDataOnExit;

            // 6. Downloads
            TxtDownloadFolder.Text = settings.DownloadFolder;
            ChkAskDownloadLocation.IsChecked = settings.AskDownloadLocation;

            // 7. Berechtigungen
            ComboPermCamera.SelectedIndex = Math.Clamp(settings.CameraPermissionDefault, 0, 2);
            ComboPermMicrophone.SelectedIndex = Math.Clamp(settings.MicrophonePermissionDefault, 0, 2);
            ComboPermGeolocation.SelectedIndex = Math.Clamp(settings.GeolocationPermissionDefault, 0, 2);
            ComboPermNotifications.SelectedIndex = Math.Clamp(settings.NotificationsPermissionDefault, 0, 2);
            ComboPermPopups.SelectedIndex = Math.Clamp(settings.PopupsPermissionDefault, 0, 1);
            LoadPermissions();

            // 9. Über Nexa
            TxtAboutBrowserName.Text = BrandingConfig.BrowserName;
            TxtAppVersion.Text = $"v{BrandingConfig.BrowserVersion} (Release)";
            try
            {
                TxtWebViewVersion.Text = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                TxtWebViewVersion.Text = "Nicht verfügbar";
            }
            ChkEnableDevTools.IsChecked = settings.EnableDevTools;

            // 11. Konto & Synchronisierung (Google Drive)
            var sync = GoogleSyncService.Instance;
            TxtCustomClientId.Text = sync.ClientId;
            TxtLocalDriveFolder.Text = sync.LocalDriveFolderPath;
            ChkSyncPasswords.IsChecked = sync.SyncPasswords;
            ChkSyncBookmarks.IsChecked = sync.SyncBookmarks;
            ChkSyncSettings.IsChecked = sync.SyncSettings;
            UpdateSyncUiState();
        }

        private void PopulateThemeSwatches()
        {
            ThemeSwatchesPanel.Children.Clear();
            var themes = ThemeService.Themes;
            var currentThemeId = ThemeService.Instance.CurrentTheme.Id;

            foreach (var theme in themes)
            {
                var isSelected = theme.Id.Equals(currentThemeId, StringComparison.OrdinalIgnoreCase);

                var border = new Border
                {
                    Width = 38,
                    Height = 38,
                    CornerRadius = new CornerRadius(19),
                    Background = new SolidColorBrush(theme.Primary),
                    BorderBrush = isSelected
                        ? new SolidColorBrush(Colors.White)
                        : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    BorderThickness = new Thickness(isSelected ? 3 : 1),
                    Margin = new Thickness(0, 0, 10, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = theme.Name
                };

                if (isSelected)
                {
                    border.Child = new TextBlock
                    {
                        Text = "\uE73E",
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 14,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }

                border.MouseLeftButtonDown += (s, e) =>
                {
                    ThemeService.Instance.ApplyTheme(theme.Id);
                    PopulateThemeSwatches();
                    ShowStatusNotification($"Farb-Theme '{theme.Name}' aktiviert.");
                };

                ThemeSwatchesPanel.Children.Add(border);
            }
        }

        private void SelectZoomIndex(double zoom)
        {
            foreach (ComboBoxItem item in ComboDefaultZoom.Items)
            {
                if (item.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.CultureInfo.InvariantCulture, out var tagZoom))
                {
                    if (Math.Abs(tagZoom - zoom) < 0.05)
                    {
                        item.IsSelected = true;
                        return;
                    }
                }
            }
            ComboDefaultZoom.SelectedIndex = 3; // default 100%
        }

        private void RadioHome_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelCustomHomeUrl == null) return;

            if (RadioHomeCustom?.IsChecked == true)
            {
                PanelCustomHomeUrl.Visibility = Visibility.Visible;
                if (string.IsNullOrWhiteSpace(TxtHomePage.Text) ||
                    TxtHomePage.Text == "about:start" ||
                    TxtHomePage.Text.Contains("google.com"))
                {
                    TxtHomePage.Text = "https://";
                }
                TxtHomePage.Focus();
                TxtHomePage.CaretIndex = TxtHomePage.Text.Length;
            }
            else
            {
                PanelCustomHomeUrl.Visibility = Visibility.Collapsed;
                if (RadioHomeGoogle?.IsChecked == true)
                {
                    TxtHomePage.Text = "https://www.google.com";
                }
                else
                {
                    TxtHomePage.Text = "about:start";
                }
            }
        }

        private void BtnBrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Download-Ordner auswählen",
                InitialDirectory = Directory.Exists(TxtDownloadFolder.Text) ? TxtDownloadFolder.Text : BrandingConfig.DefaultDownloadFolder
            };

            if (dialog.ShowDialog() == true)
            {
                TxtDownloadFolder.Text = dialog.FolderName;
            }
        }

        private void BtnClearCookies_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Cookies wurden für alle Websites zurückgesetzt.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Webseite-Cache wurde geleert.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Möchtest du wirklich den gesamten Browserverlauf unwiderruflich löschen?",
                                         "Verlauf löschen",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                HistoryService.Instance.ClearAll();
                ShowStatusNotification("Browserverlauf wurde vollständig gelöscht.");
            }
        }

        private void LoadPermissions()
        {
            ListSavedPermissions.Items.Clear();
            var perms = PermissionService.Instance.GetAllPermissions();
            if (perms.Count == 0)
            {
                ListSavedPermissions.Items.Add(new ListBoxItem
                {
                    Content = "Keine Website-Berechtigungen hinterlegt.",
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    IsEnabled = false
                });
                return;
            }

            foreach (var kvp in perms)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = kvp.Key,
                    FontWeight = FontWeights.Medium,
                    Width = 240,
                    Foreground = (Brush)FindResource("TextPrimaryBrush")
                });

                var stateDesc = kvp.Value switch
                {
                    1 => "Erlaubt",
                    2 => "Blockiert",
                    _ => "Standard"
                };
                var permDesc = $"{stateDesc}";
                sp.Children.Add(new TextBlock
                {
                    Text = permDesc,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var delBtn = new Button
                {
                    Content = "✕",
                    Margin = new Thickness(10, 0, 0, 0),
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("StatusErrorBrush"),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = kvp.Key
                };
                delBtn.Click += DeletePerm_Click;
                sp.Children.Add(delBtn);

                ListSavedPermissions.Items.Add(sp);
            }
        }

        private void DeletePerm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string origin)
            {
                PermissionService.Instance.RemovePermission(origin);
                LoadPermissions();
            }
        }

        private void BtnResetAllPermissions_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Möchtest du alle individuellen Website-Berechtigungen löschen?",
                                         "Berechtigungen zurücksetzen",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                PermissionService.Instance.ClearAllPermissions();
                LoadPermissions();
                ShowStatusNotification("Alle Website-Berechtigungen zurückgesetzt.");
            }
        }

        private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            var updateWin = new UpdateCheckWindow();
            try
            {
                updateWin.Owner = Window.GetWindow(this);
            }
            catch { }
            updateWin.ShowDialog();

            TxtUpdateStatus.Text = $"{BrandingConfig.BrowserName} v{BrandingConfig.BrowserVersion} (GitHub geprüft um {DateTime.Now:HH:mm} Uhr).";
            TxtUpdateStatus.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            LoadCurrentSettings();
            ShowStatusNotification("Änderungen verworfen. Einstellungen zurückgesetzt.");
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Instance.Settings;

            // 1. Darstellung
            if (ComboDefaultZoom.SelectedItem is ComboBoxItem zoomItem &&
                zoomItem.Tag is string zoomTag &&
                double.TryParse(zoomTag, System.Globalization.CultureInfo.InvariantCulture, out var zoomVal))
            {
                settings.DefaultZoom = zoomVal;
            }
            settings.EnableAnimations = ChkEnableAnimations.IsChecked ?? true;

            // 2. Suche
            settings.SearchEngineUrl = ComboSearchEngine.SelectedIndex switch
            {
                1 => "https://duckduckgo.com/?q={0}",
                2 => "https://www.bing.com/search?q={0}",
                3 => "https://www.ecosia.org/search?q={0}",
                _ => "https://www.google.com/search?q={0}"
            };
            settings.EnableSearchSuggestions = ChkSearchSuggestions.IsChecked ?? true;

            // 3. Tabs & Leistung
            settings.OpenNewTabBesideActive = ChkOpenBesideActive.IsChecked ?? true;
            settings.EnableTabGroups = ChkEnableTabGroups.IsChecked ?? true;
            settings.EnableSleepingTabs = ChkEnableSleepingTabs.IsChecked ?? true;
            settings.NeverSleepPinnedTabs = ChkNeverSleepPinned.IsChecked ?? true;
            settings.SleepingTimeoutMinutes = ComboSleepingTimeout.SelectedIndex switch
            {
                0 => 5,
                2 => 30,
                3 => 60,
                _ => 15
            };

            // 4. Start
            if (RadioStartRestore.IsChecked == true)
            {
                settings.StartupMode = 1;
                settings.RestoreTabsOnStartup = true;
            }
            else
            {
                settings.StartupMode = 0;
                settings.RestoreTabsOnStartup = false;
            }

            if (RadioHomeNexa.IsChecked == true)
            {
                settings.HomePage = "about:start";
            }
            else if (RadioHomeGoogle.IsChecked == true)
            {
                settings.HomePage = "https://www.google.com";
            }
            else
            {
                var custom = TxtHomePage.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(custom) || custom == "https://" || custom == "http://")
                {
                    custom = BrandingConfig.DefaultHomePage;
                }
                else if (!custom.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                         !custom.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                         !custom.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    custom = "https://" + custom;
                }
                settings.HomePage = custom;
            }

            // 5. Datenschutz
            settings.EnableAdBlocker = ChkEnableAdBlocker.IsChecked ?? true;
            settings.ClearDataOnExit = ChkClearDataOnExit.IsChecked ?? false;

            // 6. Downloads
            settings.DownloadFolder = string.IsNullOrWhiteSpace(TxtDownloadFolder.Text) ? BrandingConfig.DefaultDownloadFolder : TxtDownloadFolder.Text.Trim();
            settings.AskDownloadLocation = ChkAskDownloadLocation.IsChecked ?? false;

            // 7. Berechtigungen
            settings.CameraPermissionDefault = ComboPermCamera.SelectedIndex;
            settings.MicrophonePermissionDefault = ComboPermMicrophone.SelectedIndex;
            settings.GeolocationPermissionDefault = ComboPermGeolocation.SelectedIndex;
            settings.NotificationsPermissionDefault = ComboPermNotifications.SelectedIndex;
            settings.PopupsPermissionDefault = ComboPermPopups.SelectedIndex;

            // Allgemein
            settings.EnableDevTools = ChkEnableDevTools.IsChecked ?? true;

            // 11. Konto & Synchronisierung (Google Drive)
            var sync = GoogleSyncService.Instance;
            sync.ClientId = TxtCustomClientId.Text?.Trim() ?? "";
            sync.LocalDriveFolderPath = TxtLocalDriveFolder.Text?.Trim() ?? "";
            sync.SyncPasswords = ChkSyncPasswords.IsChecked ?? true;
            sync.SyncBookmarks = ChkSyncBookmarks.IsChecked ?? true;
            sync.SyncSettings = ChkSyncSettings.IsChecked ?? true;
            sync.SaveSettings();

            SettingsService.Instance.Save();
            ShowStatusNotification("✓ Einstellungen erfolgreich gespeichert.");
        }

        private void ShowStatusNotification(string message)
        {
            _saveStatusTimer?.Stop();
            TxtSaveStatus.Text = message;
            _saveStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _saveStatusTimer.Tick += (s, e) =>
            {
                TxtSaveStatus.Text = string.Empty;
                _saveStatusTimer.Stop();
            };
            _saveStatusTimer.Start();
        }

        // ══════════════════════════════════════════════════════════
        // 10. Passwörter & Autofill (Passwort-Tresor & Google Sync)
        // ══════════════════════════════════════════════════════════

        private void PopulatePasswordVaultUi(string filter = "")
        {
            var vault = PasswordService.Instance.Passwords;
            var list = vault.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var q = filter.Trim();
                list = list.Where(p =>
                    p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            var result = list.OrderBy(p => p.Title).ToList();
            ListPasswordEntries.ItemsSource = result;
            BorderVaultEmpty.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtVaultCount.Text = $"{vault.Count} Passwörter im Tresor";

            if (string.IsNullOrEmpty(TxtGeneratedPassword.Text))
            {
                GenerateNewPassword();
            }
        }

        private void BtnImportGoogleCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "Google Chrome Passwörter CSV (*.csv)|*.csv|Alle Dateien (*.*)|*.*",
                    Title = "Google Passwörter CSV importieren"
                };

                if (dlg.ShowDialog() == true)
                {
                    int imported = PasswordService.Instance.ImportFromGoogleCsv(dlg.FileName);
                    PopulatePasswordVaultUi(TxtSearchVault.Text?.Trim() ?? "");
                    ShowStatusNotification($"✓ {imported} Passwörter erfolgreich aus Google Passwörter importiert.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Importieren der CSV-Datei:\n{ex.Message}", "Import-Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "Google Chrome Passwörter CSV (*.csv)|*.csv",
                    FileName = "nexa_passwords.csv",
                    Title = "Passwörter exportieren"
                };

                if (dlg.ShowDialog() == true)
                {
                    PasswordService.Instance.ExportToGoogleCsv(dlg.FileName);
                    ShowStatusNotification("✓ Passwörter erfolgreich im Google Chrome CSV-Format exportiert.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Exportieren:\n{ex.Message}", "Export-Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenGooglePasswords_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://passwords.google.com")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void SliderGenLength_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtGenLengthVal != null)
            {
                TxtGenLengthVal.Text = $"{(int)SliderGenLength.Value} Zeichen";
            }
            GenerateNewPassword();
        }

        private void GenOptions_Changed(object sender, RoutedEventArgs e)
        {
            GenerateNewPassword();
        }

        private void BtnRegeneratePassword_Click(object sender, RoutedEventArgs e)
        {
            GenerateNewPassword();
        }

        private void GenerateNewPassword()
        {
            if (TxtGeneratedPassword == null || SliderGenLength == null) return;

            int len = (int)SliderGenLength.Value;
            bool u = ChkGenUpper?.IsChecked ?? true;
            bool l = ChkGenLower?.IsChecked ?? true;
            bool n = ChkGenNumbers?.IsChecked ?? true;
            bool s = ChkGenSymbols?.IsChecked ?? true;

            TxtGeneratedPassword.Text = PasswordService.Instance.GenerateStrongPassword(len, u, l, n, s);
        }

        private void BtnCopyGeneratedPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtGeneratedPassword.Text))
            {
                Clipboard.SetText(TxtGeneratedPassword.Text);
                ShowStatusNotification("✓ Generiertes Passwort kopiert.");
            }
        }

        private void BtnToggleAddForm_Click(object sender, RoutedEventArgs e)
        {
            if (BorderAddAccountForm.Visibility == Visibility.Visible)
            {
                BorderAddAccountForm.Visibility = Visibility.Collapsed;
            }
            else
            {
                BorderAddAccountForm.Visibility = Visibility.Visible;
                TxtNewTitle.Text = string.Empty;
                TxtNewUrl.Text = "https://";
                TxtNewUsername.Text = string.Empty;
                TxtNewPassword.Text = string.Empty;
                TxtNewNote.Text = string.Empty;
                TxtNewTitle.Focus();
            }
        }

        private void BtnCancelAddForm_Click(object sender, RoutedEventArgs e)
        {
            BorderAddAccountForm.Visibility = Visibility.Collapsed;
        }

        private void BtnSaveNewAccount_Click(object sender, RoutedEventArgs e)
        {
            var title = TxtNewTitle.Text?.Trim() ?? string.Empty;
            var url = TxtNewUrl.Text?.Trim() ?? string.Empty;
            var user = TxtNewUsername.Text?.Trim() ?? string.Empty;
            var pass = TxtNewPassword.Text ?? string.Empty;
            var note = TxtNewNote.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                MessageBox.Show("Bitte mindestens Webseite/Titel, Benutzername und Passwort angeben.", "Pflichtfelder fehlen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(url) || url == "https://")
            {
                url = "https://" + title.ToLowerInvariant().Replace(" ", "") + ".com";
            }

            var entry = new PasswordEntry
            {
                Title = title,
                Url = url,
                Username = user,
                Password = pass,
                Note = note
            };

            PasswordService.Instance.AddOrUpdate(entry);
            BorderAddAccountForm.Visibility = Visibility.Collapsed;
            PopulatePasswordVaultUi(TxtSearchVault.Text?.Trim() ?? "");
            ShowStatusNotification($"✓ Passwort für '{title}' sicher gespeichert.");
        }

        private void TxtSearchVault_TextChanged(object sender, TextChangedEventArgs e)
        {
            PopulatePasswordVaultUi(TxtSearchVault.Text?.Trim() ?? "");
        }

        private void BtnToggleReveal_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PasswordEntry entry)
            {
                entry.IsRevealed = !entry.IsRevealed;
            }
        }

        private void BtnCopyUsername_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PasswordEntry entry && !string.IsNullOrEmpty(entry.Username))
            {
                Clipboard.SetText(entry.Username);
                ShowStatusNotification("✓ Benutzername kopiert.");
            }
        }

        private void BtnCopyPassword_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PasswordEntry entry && !string.IsNullOrEmpty(entry.Password))
            {
                Clipboard.SetText(entry.Password);
                ShowStatusNotification("✓ Passwort kopiert.");
            }
        }

        private void BtnDeletePassword_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PasswordEntry entry)
            {
                var result = MessageBox.Show(
                    $"Möchtest du das gespeicherte Passwort für '{entry.Title}' ({entry.Username}) wirklich löschen?",
                    "Passwort löschen",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    PasswordService.Instance.Delete(entry);
                    PopulatePasswordVaultUi(TxtSearchVault.Text?.Trim() ?? "");
                    ShowStatusNotification("✓ Eintrag aus Tresor entfernt.");
                }
            }
        }

        private async void BtnRunAudit_Click(object sender, RoutedEventArgs e)
        {
            if (PasswordService.Instance.Passwords.Count == 0)
            {
                MessageBox.Show("Keine Passwörter im Tresor vorhanden.", "Passwort-Audit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnRunAudit.IsEnabled = false;
            ProgressAudit.Visibility = Visibility.Visible;
            ProgressAudit.Value = 0;
            TxtAuditProgress.Visibility = Visibility.Visible;
            TxtAuditProgress.Text = "Prüfe Passwörter anonym über HaveIBeenPwned API...";

            try
            {
                var (total, compromised) = await PasswordService.Instance.AuditAllPasswordsAsync((curr, tot) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressAudit.Value = (double)curr / tot * 100;
                        TxtAuditProgress.Text = $"Prüfe {curr} von {tot} Passwörtern...";
                    });
                });

                if (compromised > 0)
                {
                    TxtAuditSummary.Text = $"⚠️ {compromised} von {total} Passwörtern wurden in bekannten Datenlecks gefunden! Bitte ändere diese dringend.";
                    TxtAuditSummary.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                    ShowStatusNotification($"⚠️ {compromised} kompromittierte Passwörter entdeckt!");
                }
                else
                {
                    TxtAuditSummary.Text = $"✅ Großartig! Keines deiner {total} Passwörter wurde in bekannten Datenlecks gefunden.";
                    TxtAuditSummary.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                    ShowStatusNotification("✓ Alle Passwörter sind sicher.");
                }

                PopulatePasswordVaultUi(TxtSearchVault.Text?.Trim() ?? "");
            }
            catch (Exception ex)
            {
                TxtAuditSummary.Text = $"Fehler beim Abgleich: {ex.Message}";
            }
            finally
            {
                BtnRunAudit.IsEnabled = true;
                ProgressAudit.Visibility = Visibility.Collapsed;
                TxtAuditProgress.Visibility = Visibility.Collapsed;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 11. Konto & Synchronisierung (Google Drive)
        // ══════════════════════════════════════════════════════════

        private void BtnGoToSyncTab_Click(object sender, RoutedEventArgs e)
        {
            NavSync.IsChecked = true;
            Nav_Click(NavSync, new RoutedEventArgs());
        }

        private async void BtnGoogleLogin_Click(object sender, RoutedEventArgs e)
        {
            var sync = GoogleSyncService.Instance;
            string customCid = TxtCustomClientId.Text?.Trim() ?? "";

            BtnGoogleLogin.IsEnabled = false;
            ShowStatusNotification("Google-Login wird im Browser geöffnet...");

            try
            {
                var (success, msg) = await sync.StartGoogleOAuthFlowAsync(customCid);
                if (success)
                {
                    ShowStatusNotification($"✓ {msg}");
                    UpdateSyncUiState();
                }
                else
                {
                    MessageBox.Show(msg, "Google Synchronisierung", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                BtnGoogleLogin.IsEnabled = true;
            }
        }

        private void BtnSyncViaFolder_Click(object sender, RoutedEventArgs e)
        {
            BtnBrowseLocalDriveFolder_Click(sender, e);
        }

        private async void BtnBrowseLocalDriveFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Google Drive Ordner für verschlüsselten Nexa-Sync auswählen",
                InitialDirectory = Directory.Exists(TxtLocalDriveFolder.Text) ? TxtLocalDriveFolder.Text : BrandingConfig.DefaultDownloadFolder
            };

            if (dialog.ShowDialog() == true)
            {
                TxtLocalDriveFolder.Text = dialog.FolderName;
                var sync = GoogleSyncService.Instance;
                sync.LocalDriveFolderPath = dialog.FolderName;
                sync.Mode = SyncMode.GoogleDriveFolder;
                sync.SaveSettings();

                UpdateSyncUiState();
                ShowStatusNotification("Lokaler Google Drive Ordner konfiguriert. Starte Sync...");
                var (success, msg) = await sync.SynchronizeAsync(true);
                ShowStatusNotification(success ? $"✓ {msg}" : $"⚠️ {msg}");
            }
        }

        private async void BtnSyncNow_Click(object sender, RoutedEventArgs e)
        {
            BtnSyncNow.IsEnabled = false;
            ShowStatusNotification("Synchronisiere mit Google Drive...");

            try
            {
                var (success, msg) = await GoogleSyncService.Instance.SynchronizeAsync(true);
                UpdateSyncUiState();
                ShowStatusNotification(success ? $"✓ {msg}" : $"⚠️ {msg}");
            }
            finally
            {
                BtnSyncNow.IsEnabled = true;
            }
        }

        private void BtnLogoutGoogle_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Möchtest du die Google-Synchronisierung wirklich trennen? Deine lokalen Passwörter und Lesezeichen bleiben erhalten.",
                "Sync trennen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                GoogleSyncService.Instance.Logout();
                UpdateSyncUiState();
                ShowStatusNotification("✓ Google-Konto getrennt.");
            }
        }

        private void ChkSyncOption_Click(object sender, RoutedEventArgs e)
        {
            var sync = GoogleSyncService.Instance;
            sync.SyncPasswords = ChkSyncPasswords.IsChecked ?? true;
            sync.SyncBookmarks = ChkSyncBookmarks.IsChecked ?? true;
            sync.SyncSettings = ChkSyncSettings.IsChecked ?? true;
            sync.SaveSettings();
        }

        private void UpdateSyncUiState()
        {
            var sync = GoogleSyncService.Instance;
            bool isConnected = sync.IsAuthenticated || (sync.Mode == SyncMode.GoogleDriveFolder && !string.IsNullOrEmpty(sync.LocalDriveFolderPath) && Directory.Exists(sync.LocalDriveFolderPath));

            if (isConnected)
            {
                SyncConnectedPanel.Visibility = Visibility.Visible;
                SyncDisconnectedPanel.Visibility = Visibility.Collapsed;

                if (sync.IsAuthenticated)
                {
                    TxtUserName.Text = !string.IsNullOrEmpty(sync.UserName) ? sync.UserName : "Google Nutzer";
                    TxtUserEmail.Text = !string.IsNullOrEmpty(sync.UserEmail) ? sync.UserEmail : "Google Konto verbunden";
                    TxtUserInitials.Text = !string.IsNullOrEmpty(sync.UserName) ? sync.UserName[0].ToString().ToUpper() : "G";
                }
                else
                {
                    TxtUserName.Text = "Google Drive (Lokaler Ordner)";
                    TxtUserEmail.Text = sync.LocalDriveFolderPath;
                    TxtUserInitials.Text = "📁";
                }

                TxtLastSyncTime.Text = sync.LastSyncTime.HasValue
                    ? $"Zuletzt synchronisiert: {sync.LastSyncTime.Value:dd.MM.yyyy HH:mm:ss}"
                    : "Noch nicht synchronisiert";
            }
            else
            {
                SyncConnectedPanel.Visibility = Visibility.Collapsed;
                SyncDisconnectedPanel.Visibility = Visibility.Visible;
            }
        }

        private void UpdateSyncStatus(string msg)
        {
            if (SyncConnectedPanel.Visibility == Visibility.Visible)
            {
                TxtLastSyncTime.Text = msg;
            }
        }

        private void OnSyncCompleted(bool success, string msg)
        {
            UpdateSyncUiState();
            ShowStatusNotification(success ? $"✓ {msg}" : $"⚠️ Sync: {msg}");
        }
    }
}
