using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Browser.Resources;
using Browser.Services;

namespace Browser.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadCurrentSettings();
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

            ContentScrollViewer.ScrollToTop();
        }

        private void LoadCurrentSettings()
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

            // 4. Start
            if (settings.StartupMode == 1 || settings.RestoreTabsOnStartup)
                RadioStartRestore.IsChecked = true;
            else
                RadioStartNewTab.IsChecked = true;

            var currentHome = settings.HomePage?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(currentHome) || currentHome.Equals("about:start", StringComparison.OrdinalIgnoreCase))
            {
                RadioHomeNexa.IsChecked = true;
                PanelCustomHomeUrl.Visibility = Visibility.Collapsed;
                TxtHomePage.Text = "about:start";
            }
            else if (currentHome.Contains("google.", StringComparison.OrdinalIgnoreCase))
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
            RefreshPermissionsList();

            // 9. Über
            TxtAboutBrowserName.Text = BrandingConfig.BrowserName;
            TxtAppVersion.Text = $"v{BrandingConfig.BrowserVersion} (Release)";
            TxtDotNetVersion.Text = $".NET {Environment.Version}";
            ChkEnableDevTools.IsChecked = settings.EnableDevTools;

            try
            {
                TxtWebViewVersion.Text = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                TxtWebViewVersion.Text = "Installiert (System)";
            }
        }

        private void PopulateThemeSwatches()
        {
            ThemeSwatchesPanel.Children.Clear();
            var currentThemeId = ThemeService.Instance.CurrentTheme.Id;

            foreach (var theme in ThemeService.Themes)
            {
                var isSelected = string.Equals(theme.Id, currentThemeId, StringComparison.OrdinalIgnoreCase);

                var border = new Border
                {
                    Width = 38,
                    Height = 38,
                    CornerRadius = new CornerRadius(19),
                    Background = new System.Windows.Media.SolidColorBrush(theme.Primary),
                    Margin = new Thickness(0, 0, 10, 8),
                    Cursor = Cursors.Hand,
                    ToolTip = theme.Name,
                    BorderThickness = new Thickness(isSelected ? 3 : 1),
                    BorderBrush = isSelected
                        ? (TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White)
                        : (TryFindResource("BorderDefaultBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DimGray)
                };

                var checkmark = new TextBlock
                {
                    Text = "\uE73E",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed
                };
                border.Child = checkmark;

                var captureTheme = theme;
                border.MouseLeftButtonDown += (s, e) =>
                {
                    ThemeService.Instance.ApplyTheme(captureTheme.Id);
                    PopulateThemeSwatches();
                };

                ThemeSwatchesPanel.Children.Add(border);
            }
        }

        private void SelectZoomIndex(double zoom)
        {
            for (int i = 0; i < ComboDefaultZoom.Items.Count; i++)
            {
                if (ComboDefaultZoom.Items[i] is ComboBoxItem item && item.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    if (Math.Abs(val - zoom) < 0.05)
                    {
                        ComboDefaultZoom.SelectedIndex = i;
                        return;
                    }
                }
            }
            ComboDefaultZoom.SelectedIndex = 3; // 100%
        }

        private double GetSelectedZoom()
        {
            if (ComboDefaultZoom.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
            return 1.0;
        }

        private void RefreshPermissionsList()
        {
            ListSavedPermissions.Items.Clear();
            var perms = PermissionService.Instance.GetAllPermissions();
            if (perms.Count == 0)
            {
                ListSavedPermissions.Items.Add(new ListBoxItem
                {
                    Content = "Keine benutzerdefinierten Berechtigungen gespeichert.",
                    IsEnabled = false,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
                });
                return;
            }

            foreach (var kvp in perms)
            {
                var stateStr = kvp.Value == 1 ? "Erlaubt" : "Blockiert";
                var dock = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
                var btnDel = new Button
                {
                    Content = "\uE711",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 10,
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = (System.Windows.Media.Brush)FindResource("BgHoverBrush"),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = kvp.Key
                };
                btnDel.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is string key)
                    {
                        PermissionService.Instance.RemovePermission(key);
                        RefreshPermissionsList();
                    }
                };
                DockPanel.SetDock(btnDel, Dock.Right);
                dock.Children.Add(btnDel);

                var txt = new TextBlock
                {
                    Text = $"{kvp.Key} – {stateStr}",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
                };
                dock.Children.Add(txt);

                ListSavedPermissions.Items.Add(new ListBoxItem { Content = dock });
            }
        }

        private void BtnBrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Download-Ordner auswählen"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtDownloadFolder.Text = dialog.FolderName;
            }
        }

        private void BtnClearCookies_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Cookies und Websitedaten wurden für die aktive Sitzung zurückgesetzt.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Browser-Cache und temporäre Dateien wurden geleert.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Möchtest du den gesamten Browserverlauf wirklich löschen?", BrandingConfig.BrowserName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                HistoryService.Instance.ClearAll();
                MessageBox.Show("Verlauf erfolgreich gelöscht.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnResetAllPermissions_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Möchtest du alle gespeicherten Website-Berechtigungen zurücksetzen?", BrandingConfig.BrowserName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                PermissionService.Instance.ClearAllPermissions();
                RefreshPermissionsList();
                MessageBox.Show("Alle Website-Berechtigungen wurden zurückgesetzt.", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            var updateWin = new UpdateCheckWindow();
            try
            {
                updateWin.Owner = this;
            }
            catch { }
            updateWin.ShowDialog();

            TxtUpdateStatus.Text = $"{BrandingConfig.BrowserName} v{BrandingConfig.BrowserVersion} (GitHub geprüft um {DateTime.Now:HH:mm} Uhr).";
        }

        private void RadioHome_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelCustomHomeUrl == null || TxtHomePage == null) return;

            if (RadioHomeNexa?.IsChecked == true)
            {
                PanelCustomHomeUrl.Visibility = Visibility.Collapsed;
                TxtHomePage.Text = "about:start";
            }
            else if (RadioHomeGoogle?.IsChecked == true)
            {
                PanelCustomHomeUrl.Visibility = Visibility.Collapsed;
                TxtHomePage.Text = "https://www.google.com";
            }
            else if (RadioHomeCustom?.IsChecked == true)
            {
                PanelCustomHomeUrl.Visibility = Visibility.Visible;
                if (string.IsNullOrWhiteSpace(TxtHomePage.Text) ||
                    TxtHomePage.Text == "about:start" ||
                    TxtHomePage.Text.Contains("google.", StringComparison.OrdinalIgnoreCase))
                {
                    TxtHomePage.Text = "https://";
                }
                TxtHomePage.Focus();
                TxtHomePage.Select(TxtHomePage.Text.Length, 0);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Instance.Settings;

            // 1. Darstellung
            settings.Theme = ThemeService.Instance.CurrentTheme.Id;
            settings.DefaultZoom = GetSelectedZoom();
            settings.EnableAnimations = ChkEnableAnimations.IsChecked ?? true;

            // 2. Suche
            settings.SearchEngineUrl = ComboSearchEngine.SelectedIndex switch
            {
                1 => "https://duckduckgo.com/?q=",
                2 => "https://www.bing.com/search?q=",
                3 => "https://www.ecosia.org/search?q=",
                _ => BrandingConfig.DefaultSearchUrl
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

            SettingsService.Instance.Save();
            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
