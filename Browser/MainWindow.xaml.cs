using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Browser.Models;
using Browser.Resources;
using Browser.Services;
using Browser.Services.AI;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Browser.Views;

namespace Browser
{
    public partial class MainWindow : Window
    {
        private readonly TabManager _tabManager = new();
        private bool _isNavigating;
        private CoreWebView2Environment? _webViewEnvironment;
        private readonly SemaphoreSlim _envLock = new(1, 1);
        private CancellationTokenSource? _statusCts;

        private readonly bool _isIncognito;
        private string? _incognitoFolder;
        private bool _isFullscreen;
        private WindowState _previousWindowState;
        private bool _isSplitViewActive;
        private BrowserTab? _splitTab;

        private DispatcherTimer? _aiSuggestionDebounceTimer;
        private CancellationTokenSource? _aiSuggestionCts;
        private CancellationTokenSource? _translationCts;
        private PasswordEntry? _pendingPasswordEntry;

        public MainWindow() : this(false) { }

        public MainWindow(bool isIncognito)
        {
            _isIncognito = isIncognito;
            InitializeComponent();

            _aiSuggestionDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _aiSuggestionDebounceTimer.Tick += AiSuggestionDebounceTimer_Tick;

            DataContext = _tabManager;
            TabBar.ItemsSource = _tabManager.Tabs;

            _tabManager.ActiveTabChanged += OnActiveTabChanged;
            _tabManager.TabAdded += OnTabAdded;
            _tabManager.TabRemoved += OnTabRemoved;

            DownloadsList.ItemsSource = DownloadManager.Instance.Downloads;
            DownloadManager.Instance.ActiveCountChanged += UpdateDownloadBadge;
            DownloadManager.Instance.DownloadStarted += OnDownloadStarted;
            DownloadManager.Instance.DownloadCompleted += OnDownloadCompleted;
            UpdateDownloadBadge();

            HistoryList.ItemsSource = HistoryService.Instance.History;
            BookmarksList.ItemsSource = BookmarkService.Instance.Bookmarks;
            BookmarkService.Instance.BookmarksChanged += UpdateBookmarkStar;
            SpeedDialService.Instance.SpeedDialChanged += OnSpeedDialChanged;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            StateChanged += MainWindow_StateChanged;
            SourceInitialized += MainWindow_SourceInitialized;
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            if (_isIncognito)
            {
                IncognitoBadge.Visibility = Visibility.Visible;
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
            }
        }

        // ── Lifecycle ───────────────────────────────────────────

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Theme first (visual, must be on UI thread)
            ListThemePicker.ItemsSource = ThemeService.Themes;
            ThemeService.Instance.ApplySavedTheme();
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
            OnThemeChanged(ThemeService.Instance.CurrentTheme);

            // 2. Open tab immediately (visible to user first – fastest perceived startup)
            bool tabCreated = false;

            // Priority: URL passed from command line / clicked external link
            var initialUrl = App.InitialCommandLineUrl;
            if (!string.IsNullOrWhiteSpace(initialUrl) && !initialUrl.Equals("ACTIVATE", StringComparison.OrdinalIgnoreCase))
            {
                _tabManager.AddTab(initialUrl);
                tabCreated = true;
            }

            if (!tabCreated && !_isIncognito)
            {
                var settings = SettingsService.Instance.Settings;
                if (settings.StartupMode == 1 || settings.RestoreTabsOnStartup)
                {
                    var savedTabs = SessionService.Instance.LoadSession();
                    if (savedTabs.Count > 0)
                    {
                        foreach (var sTab in savedTabs)
                        {
                            var tab = _tabManager.AddTab(sTab.Url);
                            if (sTab.IsPinned) _tabManager.PinTab(tab);
                        }
                        tabCreated = true;
                    }
                }
            }
            if (!tabCreated)
            {
                _tabManager.AddTab(SettingsService.Instance.HomePage);
            }

            // 3. Secondary services in background (non-blocking, after tab is visible)
            InitializeAiAssistant();

            // Background DNS pre-warming for common domains (non-blocking)
            PerformanceService.Instance.PrewarmCommonDomains();

            // Background KI model prewarming (3s after start, non-blocking)
            _ = WebLlmBackend.PrewarmIfIdleAsync(3000);

            // Background Cloud / Google Drive Sync (non-blocking, debounced)
            if (!_isIncognito && (GoogleSyncService.Instance.IsAuthenticated ||
                (GoogleSyncService.Instance.Mode == SyncMode.GoogleDriveFolder && !string.IsNullOrEmpty(GoogleSyncService.Instance.LocalDriveFolderPath))))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    await GoogleSyncService.Instance.SynchronizeAsync(false);
                });
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        /// <summary>
        /// Handles URLs opened externally from other Windows applications while the browser is running.
        /// </summary>
        public void HandleExternalUrl(string url)
        {
            BringToFront();

            if (!string.IsNullOrWhiteSpace(url) && !url.Equals("ACTIVATE", StringComparison.OrdinalIgnoreCase))
            {
                _tabManager.AddTab(url);
            }
        }

        public void BringToFront()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void OnThemeChanged(ColorTheme theme)
        {
            if (AddressBarGlow != null)
            {
                AddressBarGlow.Color = theme.Primary;
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            SpeedDialService.Instance.SpeedDialChanged -= OnSpeedDialChanged;

            // Flush any pending debounced history writes before closing
            HistoryService.Instance.FlushSave();

            if (!_isIncognito)
            {
                SessionService.Instance.SaveSession(_tabManager.Tabs);
            }
            else if (!string.IsNullOrEmpty(_incognitoFolder) && Directory.Exists(_incognitoFolder))
            {
                try { Directory.Delete(_incognitoFolder, recursive: true); } catch { }
            }
        }

        private void OnSpeedDialChanged(List<SpeedDialItem> tiles)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var tilesJson = SpeedDialService.Instance.GetTilesJson();
                var msg = $"{{\"action\":\"update_speed_dial\",\"tiles\":{tilesJson}}}";
                foreach (var tab in _tabManager.Tabs)
                {
                    if (tab.IsInitialized && NavigationService.IsStartPage(tab.Url))
                    {
                        try
                        {
                            tab.WebView.CoreWebView2?.PostWebMessageAsJson(msg);
                        }
                        catch { }
                    }
                }
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MONITORINFO
        {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT rcMonitor = new RECT();
            public RECT rcWork = new RECT();
            public int dwFlags = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO();
                GetMonitorInfo(monitor, mi);
                var rcWork = mi.rcWork;
                var rcMonitor = mi.rcMonitor;

                if (_isFullscreen)
                {
                    mmi.ptMaxPosition.x = 0;
                    mmi.ptMaxPosition.y = 0;
                    mmi.ptMaxSize.x = Math.Abs(rcMonitor.right - rcMonitor.left);
                    mmi.ptMaxSize.y = Math.Abs(rcMonitor.bottom - rcMonitor.top);
                }
                else
                {
                    mmi.ptMaxPosition.x = Math.Abs(rcWork.left - rcMonitor.left);
                    mmi.ptMaxPosition.y = Math.Abs(rcWork.top - rcMonitor.top);
                    mmi.ptMaxSize.x = Math.Abs(rcWork.right - rcWork.left);
                    mmi.ptMaxSize.y = Math.Abs(rcWork.bottom - rcWork.top);
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            // Update maximize/restore icon
            if (WindowState == WindowState.Maximized)
            {
                MaximizeIcon.Text = "\uE923"; // Restore icon
                BtnMaximize.ToolTip = "Wiederherstellen";
                MainBorder.BorderThickness = new Thickness(0);
                BorderThickness = new Thickness(0);
            }
            else
            {
                MaximizeIcon.Text = "\uE922"; // Maximize icon
                BtnMaximize.ToolTip = "Maximieren";
                MainBorder.BorderThickness = new Thickness(1);
                BorderThickness = new Thickness(0);
            }
        }

        // ── Window chrome controls & Dragging ────────────────────

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private void DragWindow(MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                if (WindowState == WindowState.Maximized)
                {
                    var mousePos = PointToScreen(e.GetPosition(this));
                    double percent = e.GetPosition(this).X / Math.Max(ActualWidth, 1);
                    WindowState = WindowState.Normal;
                    Left = mousePos.X - (ActualWidth * percent);
                    Top = mousePos.Y - 15;
                }

                try
                {
                    ReleaseCapture();
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    SendMessage(helper.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
                catch
                {
                    try { DragMove(); } catch { }
                }
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            DragWindow(e);
        }

        private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (e.OriginalSource == ToolbarBorder || e.OriginalSource is DockPanel || (e.OriginalSource is StackPanel sp && sp.Name != "NavButtons"))
            {
                DragWindow(e);
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        // ── Keyboard shortcuts ──────────────────────────────────

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Ctrl+Shift combos first
            if (e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (e.Key == Key.T)
                {
                    _tabManager.ReopenLastClosedTab();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Tab)
                {
                    _tabManager.SwitchToPreviousTab();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D && _tabManager.ActiveTab != null)
                {
                    _tabManager.DuplicateTab(_tabManager.ActiveTab);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.N)
                {
                    new MainWindow(isIncognito: true).Show();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Delete)
                {
                    ClearBrowsingDataAsync();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.PageUp && _tabManager.ActiveTab != null)
                {
                    int idx = _tabManager.Tabs.IndexOf(_tabManager.ActiveTab);
                    if (idx > 0) _tabManager.MoveTab(idx, idx - 1);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.PageDown && _tabManager.ActiveTab != null)
                {
                    int idx = _tabManager.Tabs.IndexOf(_tabManager.ActiveTab);
                    if (idx >= 0 && idx < _tabManager.Tabs.Count - 1) _tabManager.MoveTab(idx, idx + 1);
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+M -> Mute / Unmute active tab
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.M)
            {
                if (_tabManager.ActiveTab?.WebView.CoreWebView2 != null)
                {
                    _tabManager.ActiveTab.WebView.CoreWebView2.IsMuted = !_tabManager.ActiveTab.WebView.CoreWebView2.IsMuted;
                    e.Handled = true;
                    return;
                }
            }

            // Shift+Escape -> Task-Manager
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Shift && e.Key == Key.Escape)
            {
                OpenTaskManager();
                e.Handled = true;
                return;
            }

            // Alt navigation shortcuts
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Alt)
            {
                if (e.SystemKey == Key.Left || e.Key == Key.Left)
                {
                    if (_tabManager.ActiveTab?.WebView.CoreWebView2?.CanGoBack == true)
                    {
                        _tabManager.ActiveTab.WebView.CoreWebView2.GoBack();
                        e.Handled = true;
                        return;
                    }
                }
                else if (e.SystemKey == Key.Right || e.Key == Key.Right)
                {
                    if (_tabManager.ActiveTab?.WebView.CoreWebView2?.CanGoForward == true)
                    {
                        _tabManager.ActiveTab.WebView.CoreWebView2.GoForward();
                        e.Handled = true;
                        return;
                    }
                }
                else if (e.SystemKey == Key.Home || e.Key == Key.Home)
                {
                    NavigateHome();
                    e.Handled = true;
                    return;
                }
            }

            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.N:
                        new MainWindow().Show();
                        e.Handled = true;
                        break;
                    case Key.S:
                        SaveCurrentPageAsync();
                        e.Handled = true;
                        break;
                    case Key.T:
                        _tabManager.AddTab();
                        e.Handled = true;
                        break;
                    case Key.W:
                        if (_tabManager.ActiveTab != null)
                        {
                            if (_tabManager.Tabs.Count == 1)
                                Close();
                            else
                                _tabManager.CloseTab(_tabManager.ActiveTab);
                        }
                        e.Handled = true;
                        break;
                    case Key.R:
                        if (_tabManager.ActiveTab?.WebView.CoreWebView2 != null)
                        {
                            _tabManager.ActiveTab.WebView.CoreWebView2.Reload();
                            e.Handled = true;
                        }
                        break;
                    case Key.Tab:
                        _tabManager.SwitchToNextTab();
                        e.Handled = true;
                        break;
                    case Key.L:
                        AddressBar.Focus();
                        AddressBar.SelectAll();
                        e.Handled = true;
                        break;
                    case Key.F:
                        ShowFindBar();
                        e.Handled = true;
                        break;
                    case Key.H:
                        BtnHistory_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Key.K:
                        ToggleQuickSearch();
                        e.Handled = true;
                        break;
                    case Key.D:
                        ToggleCurrentBookmark();
                        e.Handled = true;
                        break;
                    case Key.P:
                        if (_tabManager.ActiveTab?.WebView.CoreWebView2 != null)
                        {
                            _tabManager.ActiveTab.WebView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
                            e.Handled = true;
                        }
                        break;
                    case Key.OemPlus:
                    case Key.Add:
                        AdjustZoom(0.1);
                        e.Handled = true;
                        break;
                    case Key.OemMinus:
                    case Key.Subtract:
                        AdjustZoom(-0.1);
                        e.Handled = true;
                        break;
                    case Key.D0:
                    case Key.NumPad0:
                        AdjustZoom(0, reset: true);
                        e.Handled = true;
                        break;
                    case Key.Oem5:
                    case Key.OemBackslash:
                    case Key.Divide:
                        ToggleSplitView();
                        e.Handled = true;
                        break;
                }
            }

            if (e.Key == Key.Escape)
            {
                if (FindBar.Visibility == Visibility.Visible)
                {
                    HideFindBar();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F5 && _tabManager.ActiveTab?.WebView.CoreWebView2 != null)
            {
                _tabManager.ActiveTab.WebView.CoreWebView2.Reload();
                e.Handled = true;
            }
        }

        // ── Tab events ──────────────────────────────────────────

        private async void OnTabAdded(BrowserTab tab)
        {
            tab.WebView.Visibility = Visibility.Collapsed;
            tab.WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 20, 40);
            WebViewHost.Children.Add(tab.WebView);
            await InitializeTabWebView(tab);
        }

        private void OnTabRemoved(BrowserTab tab)
        {
            if (_isSplitViewActive && tab == _splitTab)
            {
                CloseSplitView();
            }
            else
            {
                WebViewHost.Children.Remove(tab.WebView);
            }
            SecondaryWebViewHost.Children.Remove(tab.WebView);

            // Remove CustomView (e.g. SettingsView) if present
            if (tab.CustomView != null)
            {
                WebViewHost.Children.Remove(tab.CustomView);
                tab.CustomView = null;
            }
        }

        private void OnActiveTabChanged(BrowserTab? tab)
        {
            if (tab == null) return;

            if (_isSplitViewActive)
            {
                if (_tabManager.Tabs.Count <= 1)
                {
                    CloseSplitView();
                }
                else if (tab == _splitTab)
                {
                    var prevPrimary = _tabManager.Tabs.FirstOrDefault(t => t != tab && WebViewHost.Children.Contains(t.WebView));
                    if (prevPrimary != null)
                    {
                        _splitTab.PropertyChanged -= SplitTab_PropertyChanged;

                        SecondaryWebViewHost.Children.Remove(tab.WebView);
                        WebViewHost.Children.Add(tab.WebView);
                        tab.WebView.Visibility = Visibility.Visible;

                        WebViewHost.Children.Remove(prevPrimary.WebView);
                        SecondaryWebViewHost.Children.Add(prevPrimary.WebView);
                        prevPrimary.WebView.Visibility = Visibility.Visible;

                        _splitTab = prevPrimary;
                        _splitTab.PropertyChanged += SplitTab_PropertyChanged;
                        UpdateSplitTabInfo();
                    }
                }
            }

            // Settings tabs show the CustomView, not the WebView
            if (tab.IsSettingsTab && tab.CustomView != null)
            {
                AddressBar.Text = "about:settings";
                Title = $"Einstellungen – {BrandingConfig.BrowserName}";
            }
            else if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                AddressBar.Text = NavigationService.GetDisplayUrl(tab.WebView.CoreWebView2.Source);
                Title = $"{tab.Title} – {BrandingConfig.BrowserName}";
                UpdateNavigationButtons();
            }
            else
            {
                AddressBar.Text = string.Empty;
                Title = BrandingConfig.BrowserName;
            }

            _isNavigating = tab.IsLoading;
            ReloadIcon.Text = _isNavigating ? "\uE711" : "\uE72C";
            UpdateBookmarkStar();
            UpdateShieldUI();
            UpdateSecurityIcon(tab.Url);

            if (tab.IsLoading)
                StartLoadingProgress();
            else
                ResetLoadingProgress();

            if (TranslatePageIcon != null)
                TranslatePageIcon.Foreground = (Brush)FindResource("TextSecondaryBrush");
            if (TranslationBanner != null)
                TranslationBanner.Visibility = Visibility.Collapsed;
        }

        private void UpdateSecurityIcon(string? url)
        {
            if (string.IsNullOrEmpty(url) || NavigationService.IsStartPage(url) || NavigationService.IsSettingsPage(url))
            {
                SecurityLogo.Visibility = Visibility.Visible;
                SecurityIcon.Visibility = Visibility.Collapsed;
                SecurityBadge.ToolTip = (!string.IsNullOrEmpty(url) && NavigationService.IsSettingsPage(url)) ? "Nexa Einstellungen (Klick für Website-Informationen)" : "Nexa Startseite (Klick für Website-Informationen)";
                return;
            }

            SecurityLogo.Visibility = Visibility.Collapsed;
            SecurityIcon.Visibility = Visibility.Visible;

            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                SecurityIcon.Text = "\uE72E";
                SecurityIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                SecurityBadge.ToolTip = "Sichere Verbindung (HTTPS) – Klick für Details & Cookies";
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                SecurityIcon.Text = "\uE7BA";
                SecurityIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                SecurityBadge.ToolTip = "Nicht sicher (HTTP) – Klick für Warnung & Details";
            }
            else
            {
                SecurityLogo.Visibility = Visibility.Visible;
                SecurityIcon.Visibility = Visibility.Collapsed;
                SecurityBadge.ToolTip = "Lokale Nexa-Seite (Klick für Website-Informationen)";
            }
        }

        private async void SecurityBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var tab = _tabManager.ActiveTab;
            if (tab == null) return;

            string url = tab.Url ?? "";
            bool isLocalOrStart = string.IsNullOrEmpty(url) || NavigationService.IsStartPage(url) || NavigationService.IsSettingsPage(url);

            if (isLocalOrStart)
            {
                TxtSecurityHostTitle.Text = NavigationService.IsSettingsPage(url) ? "Nexa Einstellungen" : "Nexa Startseite";
                TxtSecurityStatusSubtitle.Text = "Interne geschützte Systemseite";
                TxtSecurityStatusBadge.Text = "Intern";
                TxtSecurityStatusBadge.Foreground = (Brush)FindResource("AccentLightBrush");
                SecurityStatusBadgeBorder.Background = (Brush)FindResource("AccentDimBrush");
                SecurityHeaderIcon.Text = "\uE80F";
                SecurityHeaderIcon.Foreground = (Brush)FindResource("AccentLightBrush");
                SecurityHeaderIconBorder.Background = (Brush)FindResource("AccentDimBrush");

                TxtSecurityCertInfo.Text = "Lokal (System-Sicherheit)";
                TxtSecurityDetailsText.Text = "Diese Seite wird direkt aus der internen Nexa-Applikation geladen. Keine Daten verlassen deinen Computer.";

                TxtSecurityCookiesCount.Text = "0 Cookies";
                BtnDeleteSiteCookies.IsEnabled = false;
                TxtSecurityCookiesNotice.Visibility = Visibility.Collapsed;

                TxtSecurityShieldBlocked.Text = "Inaktiv (Intern)";

                ChkPermLocation.IsEnabled = false;
                ChkPermLocation.IsChecked = false;
                ChkPermCamera.IsEnabled = false;
                ChkPermCamera.IsChecked = false;
                ChkPermMicrophone.IsEnabled = false;
                ChkPermMicrophone.IsChecked = false;
                ChkPermNotifications.IsEnabled = false;
                ChkPermNotifications.IsChecked = false;

                SecurityPopup.IsOpen = true;
                return;
            }

            // Web Page
            string host = url;
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                    host = uri.Host;
            }
            catch { }

            TxtSecurityHostTitle.Text = host;

            bool isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (isHttps)
            {
                TxtSecurityStatusSubtitle.Text = "Verbindung ist sicher";
                TxtSecurityStatusBadge.Text = "Sicher";
                TxtSecurityStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                SecurityStatusBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x10, 0xB9, 0x81));
                SecurityHeaderIcon.Text = "\uE72E";
                SecurityHeaderIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                SecurityHeaderIconBorder.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x10, 0xB9, 0x81));

                TxtSecurityCertInfo.Text = "Gültig (TLS 1.3 / HTTPS)";
                TxtSecurityDetailsText.Text = "Deine Verbindung zu dieser Website ist verschlüsselt. Dritte können übertragene Daten (Passwörter, Cookies) nicht mitlesen.";
            }
            else
            {
                TxtSecurityStatusSubtitle.Text = "Verbindung ist unverschlüsselt";
                TxtSecurityStatusBadge.Text = "Nicht sicher";
                TxtSecurityStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                SecurityStatusBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0x9E, 0x0B));
                SecurityHeaderIcon.Text = "\uE7BA";
                SecurityHeaderIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                SecurityHeaderIconBorder.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0x9E, 0x0B));

                TxtSecurityCertInfo.Text = "Kein Zertifikat (Klartext)";
                TxtSecurityDetailsText.Text = "Warnung: Diese Website verwendet keine Verschlüsselung. Passwörter und persönliche Daten können im selben WLAN mitgeschnitten werden.";
            }

            // Shield count
            int blocked = AdBlockService.Instance.GetBlockedCount(tab.Id);
            TxtSecurityShieldBlocked.Text = $"{blocked} blockiert";

            // Load Cookies count
            TxtSecurityCookiesNotice.Visibility = Visibility.Collapsed;
            int cookieCount = 0;
            if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                try
                {
                    var cookies = await tab.WebView.CoreWebView2.CookieManager.GetCookiesAsync(tab.Url);
                    cookieCount = cookies.Count;
                }
                catch { }
            }
            TxtSecurityCookiesCount.Text = $"{cookieCount} Cookies aktiv";
            BtnDeleteSiteCookies.IsEnabled = cookieCount > 0;

            // Load Permissions for this host
            ChkPermLocation.IsEnabled = true;
            ChkPermCamera.IsEnabled = true;
            ChkPermMicrophone.IsEnabled = true;
            ChkPermNotifications.IsEnabled = true;

            ChkPermLocation.IsChecked = PermissionService.Instance.TryGetPermission(host, CoreWebView2PermissionKind.Geolocation, out var geoState) && geoState == CoreWebView2PermissionState.Allow;
            ChkPermCamera.IsChecked = PermissionService.Instance.TryGetPermission(host, CoreWebView2PermissionKind.Camera, out var camState) && camState == CoreWebView2PermissionState.Allow;
            ChkPermMicrophone.IsChecked = PermissionService.Instance.TryGetPermission(host, CoreWebView2PermissionKind.Microphone, out var micState) && micState == CoreWebView2PermissionState.Allow;
            ChkPermNotifications.IsChecked = PermissionService.Instance.TryGetPermission(host, CoreWebView2PermissionKind.Notifications, out var notifState) && notifState == CoreWebView2PermissionState.Allow;

            SecurityPopup.IsOpen = true;
        }

        private async void BtnDeleteSiteCookies_Click(object sender, RoutedEventArgs e)
        {
            var tab = _tabManager.ActiveTab;
            if (tab == null || !tab.IsInitialized || tab.WebView.CoreWebView2 == null || string.IsNullOrEmpty(tab.Url))
                return;

            try
            {
                var cookieManager = tab.WebView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync(tab.Url);
                int deletedCount = cookies.Count;
                foreach (var c in cookies)
                {
                    cookieManager.DeleteCookie(c);
                }

                TxtSecurityCookiesCount.Text = "0 Cookies";
                BtnDeleteSiteCookies.IsEnabled = false;
                TxtSecurityCookiesNotice.Text = $"✓ {deletedCount} Cookies gelöscht!";
                TxtSecurityCookiesNotice.Visibility = Visibility.Visible;
                ShowStatus($"✓ {deletedCount} Cookies für {TxtSecurityHostTitle.Text} gelöscht.");
                ScheduleHideStatus(3000);
            }
            catch
            {
                TxtSecurityCookiesNotice.Text = "Fehler beim Löschen der Cookies.";
                TxtSecurityCookiesNotice.Visibility = Visibility.Visible;
            }
        }

        private void PermToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox chk && chk.Tag is string tag && _tabManager.ActiveTab != null)
            {
                var tab = _tabManager.ActiveTab;
                if (string.IsNullOrEmpty(tab.Url)) return;

                string host = tab.Url;
                try
                {
                    if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                        host = uri.Host;
                }
                catch { }

                var newState = (chk.IsChecked == true) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;

                CoreWebView2PermissionKind kind = tag switch
                {
                    "Camera" => CoreWebView2PermissionKind.Camera,
                    "Microphone" => CoreWebView2PermissionKind.Microphone,
                    "Geolocation" => CoreWebView2PermissionKind.Geolocation,
                    "Notifications" => CoreWebView2PermissionKind.Notifications,
                    _ => CoreWebView2PermissionKind.UnknownPermission
                };

                if (kind != CoreWebView2PermissionKind.UnknownPermission)
                {
                    PermissionService.Instance.SetPermission(host, kind, newState);
                    try
                    {
                        if (tab.IsInitialized && tab.WebView.CoreWebView2 != null && Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri2))
                        {
                            tab.WebView.CoreWebView2.Profile?.SetPermissionStateAsync(kind, uri2.GetLeftPart(UriPartial.Authority), newState);
                        }
                    }
                    catch { }

                    string label = tag switch
                    {
                        "Camera" => "Kamera",
                        "Microphone" => "Mikrofon",
                        "Geolocation" => "Standort",
                        "Notifications" => "Benachrichtigungen",
                        _ => tag
                    };
                    ShowStatus($"{label}-Zugriff für {host}: {(chk.IsChecked == true ? "Erlaubt" : "Blockiert")}");
                    ScheduleHideStatus(3000);
                }
            }
        }

        // ── WebView2 initialization per tab ─────────────────────

        public async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
        {
            if (_webViewEnvironment != null) return _webViewEnvironment;

            await _envLock.WaitAsync();
            try
            {
                if (_webViewEnvironment == null)
                {
                    string userDataFolder;
                    if (_isIncognito)
                    {
                        _incognitoFolder = Path.Combine(Path.GetTempPath(), BrandingConfig.BrowserName + "_Incognito_" + Guid.NewGuid().ToString("N"));
                        userDataFolder = _incognitoFolder;
                    }
                    else
                    {
                        userDataFolder = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            BrandingConfig.BrowserName,
                            "WebView2Data");
                    }

                    Directory.CreateDirectory(userDataFolder);

                    var options = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments =
                            "--enable-gpu-rasterization " +
                            "--enable-zero-copy " +
                            "--enable-quic " +
                            "--enable-features=WebGPU,VaapiVideoDecoder,ParallelDownloading,CanvasOopif,BackForwardCache,PrefetchBrowserInitiatedTriggers " +
                            "--enable-unsafe-webgpu " +
                            "--disable-features=Translate,InterestFeedContentSuggestions,msEdgeShoppingAssist,msUndersideButton,msEdgeSplitWindow " +
                            "--force-webrtc-ip-handling-policy=default_public_interface_only " +
                            "--renderer-process-limit=16 " +
                            "--back-forward-cache " +
                            "--enable-lazy-image-loading " +
                            "--enable-lazy-frame-loading " +
                            "--disk-cache-size=524288000 " +
                            "--v8-cache-options=code"
                    };

                    _webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                        userDataFolder: userDataFolder,
                        options: options);
                }
                return _webViewEnvironment;
            }
            finally
            {
                _envLock.Release();
            }
        }

        private async Task InitializeTabWebView(BrowserTab tab)
        {
            try
            {
                var environment = await GetOrCreateEnvironmentAsync();
                await tab.WebView.EnsureCoreWebView2Async(environment);
                tab.IsInitialized = true;

                // Feature settings
                tab.WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                tab.WebView.CoreWebView2.Settings.AreDevToolsEnabled = SettingsService.Instance.EnableDevTools;
                tab.WebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                tab.WebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                try { tab.WebView.CoreWebView2.Settings.IsReputationCheckingRequired = false; } catch { }
                tab.WebView.ZoomFactor = SettingsService.Instance.DefaultZoom;

                // Audio playback & mute state tracking
                tab.WebView.CoreWebView2.IsDocumentPlayingAudioChanged += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        tab.IsPlayingAudio = tab.WebView.CoreWebView2.IsDocumentPlayingAudio;
                    });
                };

                tab.WebView.CoreWebView2.IsMutedChanged += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        tab.IsMuted = tab.WebView.CoreWebView2.IsMuted;
                    });
                };

                // Nexa Privacy Shield v2 (Cosmetic filter, Anti-Adblock Defuser, Cookie Dismissal, YouTube Skipper)
                await tab.WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockService.GetGlobalProtectionScript());

                // Nexa Speed Engine: Link hover DNS prefetch + CDN preconnect hints
                await tab.WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PerformanceService.GetLinkHoverPrefetchScript());
                await tab.WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PerformanceService.GetCdnPreconnectScript());

                // Nexa Password Vault (Automatic form detection & login interception)
                await tab.WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PasswordService.GetFormDetectionScript());
                tab.WebView.CoreWebView2.WebMessageReceived += OnWebViewWebMessageReceived;

                // AdBlocker & Privacy Shield filtering
                // Only filter request types that typically serve ads (Script, XHR, Fetch, Document, Other)
                // Skipping Image, Stylesheet, Font, Media for significant throughput gains
                tab.WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
                tab.WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
                tab.WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
                tab.WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
                tab.WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Other);
                tab.WebView.CoreWebView2.WebResourceRequested += (s, e) =>
                {
                    if (!AdBlockService.Instance.IsEnabled) return;

                    string? pageHost = null;
                    try
                    {
                        if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var pageUri))
                            pageHost = pageUri.Host;
                    }
                    catch { }

                    if (AdBlockService.Instance.ShouldBlock(e.Request.Uri, pageHost))
                    {
                        try
                        {
                            e.Response = tab.WebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by Nexa Shield", "");
                            AdBlockService.Instance.IncrementBlocked(tab.Id);
                            if (tab.IsActive)
                            {
                                Dispatcher.InvokeAsync(UpdateShieldUI);
                            }
                        }
                        catch { }
                    }
                };

                tab.WebView.CoreWebView2.NavigationStarting += (s, e) =>
                {
                    // Real-time Phishing & Malware Protection Guard
                    if (!string.IsNullOrEmpty(e.Uri) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    {
                        var check = MalwareProtectionService.Instance.CheckUrlSecurity(e.Uri);
                        if (check.IsThreat)
                        {
                            e.Cancel = true;
                            tab.IsLoading = false;
                            Dispatcher.InvokeAsync(() =>
                            {
                                tab.Title = $"🛑 {check.ThreatType}";
                                var warningHtml = MalwareProtectionService.GetBlockedWarningHtml(e.Uri, check.ThreatType, check.Reason);
                                tab.WebView.CoreWebView2.NavigateToString(warningHtml);
                                ShowStatus($"⚠️ Zugriff auf verdächtige Website blockiert: {check.ThreatType}");
                                ScheduleHideStatus(5000);
                            });
                            return;
                        }
                    }

                    tab.IsLoading = true;
                    AdBlockService.Instance.ResetTab(tab.Id);

                    // DNS pre-warm for the navigation target
                    PerformanceService.Instance.PrewarmUrl(e.Uri);

                    if (tab.IsActive)
                    {
                        _isNavigating = true;
                        ShowStatus(e.Uri);
                        ReloadIcon.Text = "\uE711";
                        UpdateNavigationButtons();
                        UpdateShieldUI();
                        StartLoadingProgress();

                        if (TranslatePageIcon != null)
                            TranslatePageIcon.Foreground = (Brush)FindResource("TextSecondaryBrush");
                        if (TranslationBanner != null)
                            TranslationBanner.Visibility = Visibility.Collapsed;
                        if (PasswordSaveBanner != null)
                            PasswordSaveBanner.Visibility = Visibility.Collapsed;
                    }
                };

                tab.WebView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    tab.IsLoading = false;
                    if (tab.IsActive)
                    {
                        _isNavigating = false;
                        ReloadIcon.Text = "\uE72C";
                        UpdateNavigationButtons();
                        HideStatus();
                        UpdateBookmarkStar();
                        UpdateShieldUI();
                        CompleteLoadingProgress();
                    }

                    if (!_isIncognito && e.IsSuccess && !string.IsNullOrEmpty(tab.Url))
                    {
                        HistoryService.Instance.AddEntry(tab.Url, tab.Title);
                    }

                    if (e.IsSuccess && !string.IsNullOrEmpty(tab.Url) && !tab.Url.StartsWith("about:"))
                    {
                        try
                        {
                            // Protection scripts are already registered via AddScriptToExecuteOnDocumentCreatedAsync
                            // and auto-inject on every navigation – no need to re-execute them here.

                            // Nexa Password Vault: In-place autofill if credentials are saved for this site
                            var creds = PasswordService.Instance.GetMatchingCredentials(tab.Url);
                            if (creds.Count > 0)
                            {
                                var best = creds[0];
                                await tab.WebView.CoreWebView2.ExecuteScriptAsync(PasswordService.GetAutofillScript(best.Username, best.Password));
                            }

                            // Nexa Speed Engine: Extract link hostnames from page and batch-prefetch DNS
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var hostsJson = await Dispatcher.InvokeAsync(async () =>
                                    {
                                        if (tab.WebView.CoreWebView2 == null) return "[]";
                                        return await tab.WebView.CoreWebView2.ExecuteScriptAsync(
                                            @"(function() {
                                                try {
                                                    const hosts = new Set();
                                                    document.querySelectorAll('a[href^=""http""]').forEach(a => {
                                                        try { hosts.add(new URL(a.href).hostname); } catch(e) {}
                                                    });
                                                    return JSON.stringify([...hosts].slice(0, 20));
                                                } catch(e) { return '[]'; }
                                            })()");
                                    }).Task.Unwrap();

                                    if (!string.IsNullOrEmpty(hostsJson) && hostsJson != "[]")
                                    {
                                        var hosts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(hostsJson.Trim('"').Replace("\\\"", "\""));
                                        if (hosts != null && hosts.Count > 0)
                                        {
                                            PerformanceService.Instance.PrewarmHostsBatch(hosts);
                                        }
                                    }
                                }
                                catch { }
                            });
                        }
                        catch { }
                    }
                };

                tab.WebView.CoreWebView2.SourceChanged += (s, e) =>
                {
                    tab.Url = tab.WebView.CoreWebView2.Source;
                    if (tab.IsActive)
                    {
                        AddressBar.Text = NavigationService.GetDisplayUrl(tab.Url);
                        UpdateBookmarkStar();
                        UpdateShieldUI();
                    }
                };

                tab.WebView.CoreWebView2.DocumentTitleChanged += (s, e) =>
                {
                    tab.Title = tab.WebView.CoreWebView2.DocumentTitle;
                    if (tab.IsActive)
                        Title = $"{tab.Title} – {BrandingConfig.BrowserName}";

                    if (!_isIncognito && !string.IsNullOrEmpty(tab.Url))
                    {
                        HistoryService.Instance.AddEntry(tab.Url, tab.Title);
                    }
                };

                tab.WebView.CoreWebView2.PermissionRequested += (s, e) =>
                {
                    try
                    {
                        var host = new Uri(e.Uri).Host;
                        if (PermissionService.Instance.TryGetPermission(host, e.PermissionKind, out var state))
                        {
                            e.State = state;
                        }
                        else
                        {
                            e.State = CoreWebView2PermissionState.Allow;
                            PermissionService.Instance.SetPermission(host, e.PermissionKind, CoreWebView2PermissionState.Allow);
                            ShowStatus($"Berechtigung erteilt: {e.PermissionKind} für {host}");
                            ScheduleHideStatus(3000);
                        }
                    }
                    catch
                    {
                        e.State = CoreWebView2PermissionState.Allow;
                    }
                };

                tab.WebView.CoreWebView2.FaviconChanged += async (s, e) =>
                {
                    try
                    {
                        using var stream = await tab.WebView.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                        if (stream != null && stream.Length > 0)
                        {
                            var memoryStream = new MemoryStream();
                            await stream.CopyToAsync(memoryStream);
                            memoryStream.Position = 0;

                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = memoryStream;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            Dispatcher.Invoke(() => tab.FaviconSource = bitmap);
                        }
                        else
                        {
                            Dispatcher.Invoke(() => tab.FaviconSource = null);
                        }
                    }
                    catch
                    {
                        Dispatcher.Invoke(() => tab.FaviconSource = null);
                    }
                };

                tab.WebView.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    Dispatcher.InvokeAsync(() => _tabManager.AddTab(e.Uri));
                };

                // Download management
                tab.WebView.CoreWebView2.DownloadStarting += (s, e) =>
                {
                    e.Handled = true;
                    try { tab.WebView.CoreWebView2.CloseDefaultDownloadDialog(); } catch { }

                    DownloadManager.Instance.RegisterDownload(e, msg =>
                    {
                        ShowStatus(msg);
                        ScheduleHideStatus(4000);
                    });
                };

                // Fullscreen detection for HTML5 video
                tab.WebView.CoreWebView2.ContainsFullScreenElementChanged += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (tab.WebView.CoreWebView2.ContainsFullScreenElement)
                        {
                            if (!_isFullscreen) ToggleFullscreen();
                        }
                        else
                        {
                            if (_isFullscreen) ToggleFullscreen();
                        }
                    });
                };

                // Context menu filtering (removes telemetry and Edge-specific bloat)
                tab.WebView.CoreWebView2.ContextMenuRequested += (s, e) =>
                {
                    var blockedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "share", "webCapture", "webSelect", "readAloud", "openInEdge", "bingSearch"
                    };

                    for (int i = e.MenuItems.Count - 1; i >= 0; i--)
                    {
                        var item = e.MenuItems[i];
                        if (blockedNames.Contains(item.Name))
                        {
                            e.MenuItems.RemoveAt(i);
                        }
                    }
                };

                // WebMessage handling for StartPage and internal page interactions
                tab.WebView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        var json = e.WebMessageAsJson;
                        if (string.IsNullOrEmpty(json)) return;

                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("action", out var actionProp))
                        {
                            var action = actionProp.GetString();
                            if (action == "save_speed_dial" && root.TryGetProperty("tiles", out var tilesProp))
                            {
                                var items = System.Text.Json.JsonSerializer.Deserialize<List<SpeedDialItem>>(tilesProp.GetRawText());
                                if (items != null)
                                {
                                    SpeedDialService.Instance.SaveTiles(items);
                                }
                            }
                            else if (action == "navigate" && root.TryGetProperty("url", out var urlProp))
                            {
                                var target = urlProp.GetString();
                                if (!string.IsNullOrEmpty(target))
                                {
                                    Dispatcher.InvokeAsync(() => NavigateTab(tab, target));
                                }
                            }
                            else if (action == "bypass_malware" && root.TryGetProperty("url", out var bypassUrlProp))
                            {
                                var target = bypassUrlProp.GetString();
                                if (!string.IsNullOrEmpty(target) && Uri.TryCreate(target, UriKind.Absolute, out var bUri))
                                {
                                    MalwareProtectionService.Instance.AddBypass(bUri.Host);
                                    Dispatcher.InvokeAsync(() => NavigateTab(tab, target));
                                }
                            }
                        }
                    }
                    catch { }
                };

                var targetUrl = !string.IsNullOrEmpty(tab.Url) ? tab.Url : SettingsService.Instance.HomePage;
                NavigateTab(tab, targetUrl);
            }
            catch (Exception ex)
            {
                ShowStatus($"Fehler: {ex.Message}");
            }
        }

        // ── Navigation ──────────────────────────────────────────

        private void NavigateTab(BrowserTab tab, string input)
        {
            var url = NavigationService.ResolveInput(input);
            if (string.IsNullOrEmpty(url)) return;

            // Handle about:settings or about:passwords as an in-tab settings page
            if (url == "about:settings")
            {
                ShowSettingsInTab(tab);
                return;
            }
            if (url == "about:passwords")
            {
                ShowSettingsInTab(tab, "Passwörter");
                return;
            }
            if (url == "about:sync")
            {
                ShowSettingsInTab(tab, "Sync");
                return;
            }

            // If navigating away from settings, remove the CustomView
            if (tab.CustomView != null)
            {
                RemoveSettingsFromTab(tab);
            }

            if (tab.WebView.CoreWebView2 == null) return;

            if (tab.IsActive && tab.CustomView == null)
            {
                tab.WebView.Visibility = Visibility.Visible;
            }

            if (url == "about:start")
            {
                tab.Title = "Neuer Tab";
                tab.WebView.CoreWebView2.NavigateToString(NavigationService.GetStartPageHtml());
            }
            else
            {
                tab.WebView.CoreWebView2.Navigate(url);
            }
        }

        /// <summary>
        /// Displays the SettingsView UserControl inside the given tab.
        /// </summary>
        private void ShowSettingsInTab(BrowserTab tab, string? initialTab = null)
        {
            // If already showing settings, just refresh
            if (tab.CustomView is SettingsView existingView)
            {
                tab.WebView.Visibility = Visibility.Collapsed;
                existingView.Visibility = Visibility.Visible;
                existingView.LoadCurrentSettings();
                if (!string.IsNullOrEmpty(initialTab))
                {
                    existingView.SelectTab(initialTab);
                }
                return;
            }

            var settingsView = new SettingsView();
            tab.CustomView = settingsView;
            bool isPasswords = initialTab?.Contains("Passwörter", StringComparison.OrdinalIgnoreCase) == true;
            bool isSync = initialTab?.Contains("Sync", StringComparison.OrdinalIgnoreCase) == true || initialTab?.Contains("Konto", StringComparison.OrdinalIgnoreCase) == true;
            tab.Url = isPasswords ? "about:passwords" : (isSync ? "about:sync" : "about:settings");
            tab.Title = isPasswords ? "Passwörter & Autofill" : (isSync ? "Konto & Sync" : "Einstellungen");

            // Hide WebView, show SettingsView
            tab.WebView.Visibility = Visibility.Collapsed;
            settingsView.Visibility = tab.IsActive ? Visibility.Visible : Visibility.Collapsed;

            if (!WebViewHost.Children.Contains(settingsView))
            {
                WebViewHost.Children.Add(settingsView);
            }

            if (!string.IsNullOrEmpty(initialTab))
            {
                settingsView.SelectTab(initialTab);
            }

            if (tab.IsActive)
            {
                AddressBar.Text = tab.Url;
                Title = $"{tab.Title} – {BrandingConfig.BrowserName}";
            }
        }

        /// <summary>
        /// Removes the SettingsView from a tab when navigating away.
        /// </summary>
        private void RemoveSettingsFromTab(BrowserTab tab)
        {
            if (tab.CustomView != null)
            {
                WebViewHost.Children.Remove(tab.CustomView);
                tab.CustomView = null;
            }

            // Show WebView again
            if (tab.IsActive)
            {
                tab.WebView.Visibility = Visibility.Visible;
            }
        }

        private void NavigateTo(string input)
        {
            var tab = _tabManager.ActiveTab;
            if (tab == null) return;
            NavigateTab(tab, input);
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_tabManager.ActiveTab?.WebView.CanGoBack == true)
                _tabManager.ActiveTab.WebView.GoBack();
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (_tabManager.ActiveTab?.WebView.CanGoForward == true)
                _tabManager.ActiveTab.WebView.GoForward();
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            var wv = _tabManager.ActiveTab?.WebView.CoreWebView2;
            if (wv == null) return;
            if (_isNavigating) wv.Stop(); else wv.Reload();
        }

        // ── Tab bar interactions ────────────────────────────────

        private void BtnNewTab_Click(object sender, RoutedEventArgs e)
        {
            _tabManager.AddTab();
        }

        private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is BrowserTab tab)
            {
                _tabManager.SetActiveTab(tab);
                e.Handled = true; // Prevent drag
            }
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tabId)
            {
                var tab = _tabManager.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    if (_tabManager.Tabs.Count == 1)
                        Close();
                    else
                        _tabManager.CloseTab(tab);
                }
            }
        }

        private void TabAudio_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is string tabId)
            {
                var tab = _tabManager.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab?.WebView.CoreWebView2 != null)
                {
                    tab.WebView.CoreWebView2.IsMuted = !tab.WebView.CoreWebView2.IsMuted;
                }
                e.Handled = true;
            }
        }

        private void Tab_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is BrowserTab tab)
            {
                ShowTabContextMenu(elem, tab);
                e.Handled = true;
            }
        }

        private void ShowTabContextMenu(FrameworkElement target, BrowserTab tab)
        {
            var menu = new ContextMenu
            {
                Style = TryFindResource("ModernContextMenuStyle") as Style
            };

            // Neuer Tab
            var itemNew = new MenuItem
            {
                Header = "Neuer Tab",
                InputGestureText = "Strg+T",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = "\uE710",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemNew.Click += (s, e) => _tabManager.AddTab();
            menu.Items.Add(itemNew);

            // Tab duplizieren
            var itemDuplicate = new MenuItem
            {
                Header = "Tab duplizieren",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = "\uE8C8",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemDuplicate.Click += (s, e) => _tabManager.DuplicateTab(tab);
            menu.Items.Add(itemDuplicate);

            // In geteilter Ansicht öffnen
            var itemSplit = new MenuItem
            {
                Header = (_isSplitViewActive && _splitTab == tab) ? "Geteilte Ansicht schließen" : "In geteilter Ansicht öffnen",
                InputGestureText = "Strg+\\",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = "\uE740",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemSplit.Click += (s, e) => ToggleSplitView(tab);
            menu.Items.Add(itemSplit);

            // Tab neu laden
            var itemReload = new MenuItem
            {
                Header = "Tab neu laden",
                InputGestureText = "Strg+R",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = "\uE72C",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemReload.Click += (s, e) => tab.WebView.CoreWebView2?.Reload();
            menu.Items.Add(itemReload);

            // Tab stummschalten / laut schalten
            var itemMute = new MenuItem
            {
                Header = tab.IsMuted ? "Stummschaltung aufheben" : "Tab stummschalten",
                InputGestureText = "Strg+M",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = tab.IsMuted ? "\uE74F" : "\uE767",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemMute.Click += (s, e) =>
            {
                if (tab.WebView.CoreWebView2 != null)
                {
                    tab.WebView.CoreWebView2.IsMuted = !tab.WebView.CoreWebView2.IsMuted;
                }
            };
            menu.Items.Add(itemMute);

            // Tab schlafen legen / aufwecken
            if (tab != _tabManager.ActiveTab)
            {
                var itemSleep = new MenuItem
                {
                    Header = tab.IsSleeping ? "Tab aufwecken" : "Tab in Ruhezustand versetzen (RAM schonen)",
                    Style = TryFindResource("ModernMenuItemStyle") as Style,
                    Icon = new TextBlock
                    {
                        Text = "\uEC46",
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 12,
                        Foreground = TryFindResource("TextSecondaryBrush") as Brush
                    }
                };
                itemSleep.Click += (s, e) =>
                {
                    if (tab.IsSleeping)
                        _tabManager.WakeTab(tab);
                    else
                        _tabManager.SleepTab(tab);
                };
                menu.Items.Add(itemSleep);
            }

            // Tab anheften / lösen
            var itemPin = new MenuItem
            {
                Header = tab.IsPinned ? "Tab lösen" : "Tab anheften",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = tab.IsPinned ? "\uE77A" : "\uE718",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemPin.Click += (s, e) => _tabManager.TogglePin(tab);
            menu.Items.Add(itemPin);

            // Tab verschieben
            int tabIndex = _tabManager.Tabs.IndexOf(tab);
            if (tabIndex > 0)
            {
                var itemMoveLeft = new MenuItem
                {
                    Header = "Tab nach links verschieben",
                    InputGestureText = "Strg+Umschalt+Bild↑",
                    Style = TryFindResource("ModernMenuItemStyle") as Style
                };
                itemMoveLeft.Click += (s, e) => _tabManager.MoveTab(tabIndex, tabIndex - 1);
                menu.Items.Add(itemMoveLeft);
            }
            if (tabIndex >= 0 && tabIndex < _tabManager.Tabs.Count - 1)
            {
                var itemMoveRight = new MenuItem
                {
                    Header = "Tab nach rechts verschieben",
                    InputGestureText = "Strg+Umschalt+Bild↓",
                    Style = TryFindResource("ModernMenuItemStyle") as Style
                };
                itemMoveRight.Click += (s, e) => _tabManager.MoveTab(tabIndex, tabIndex + 1);
                menu.Items.Add(itemMoveRight);
            }

            // Tab-Gruppe
            var itemGroup = new MenuItem
            {
                Header = "Tab-Gruppe",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                Icon = new TextBlock
                {
                    Text = "\uE71D",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };

            void AssignGroup(string name, string color)
            {
                tab.GroupName = name;
                tab.GroupColor = color;
            }

            var grpBlue = new MenuItem { Header = "🔵 Arbeit (Blau)", Style = TryFindResource("ModernMenuItemStyle") as Style };
            grpBlue.Click += (s, e) => AssignGroup("Arbeit", "#3B82F6");
            itemGroup.Items.Add(grpBlue);

            var grpGreen = new MenuItem { Header = "🟢 Recherche (Grün)", Style = TryFindResource("ModernMenuItemStyle") as Style };
            grpGreen.Click += (s, e) => AssignGroup("Recherche", "#10B981");
            itemGroup.Items.Add(grpGreen);

            var grpPurple = new MenuItem { Header = "🟣 Privat (Lila)", Style = TryFindResource("ModernMenuItemStyle") as Style };
            grpPurple.Click += (s, e) => AssignGroup("Privat", "#8B5CF6");
            itemGroup.Items.Add(grpPurple);

            var grpOrange = new MenuItem { Header = "🟠 Medien (Orange)", Style = TryFindResource("ModernMenuItemStyle") as Style };
            grpOrange.Click += (s, e) => AssignGroup("Medien", "#F97316");
            itemGroup.Items.Add(grpOrange);

            if (tab.IsGrouped)
            {
                itemGroup.Items.Add(new Separator { Style = TryFindResource("ModernMenuSeparator") as Style });
                var grpRemove = new MenuItem { Header = "Aus Gruppe entfernen", Style = TryFindResource("ModernMenuItemStyle") as Style };
                grpRemove.Click += (s, e) => { tab.GroupName = null; tab.GroupColor = null; };
                itemGroup.Items.Add(grpRemove);
            }
            menu.Items.Add(itemGroup);

            menu.Items.Add(new Separator { Style = TryFindResource("ModernMenuSeparator") as Style });

            // Tab schließen
            var itemClose = new MenuItem
            {
                Header = "Tab schließen",
                InputGestureText = "Strg+W",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                IsEnabled = !tab.IsPinned && _tabManager.Tabs.Count > 1,
                Icon = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemClose.Click += (s, e) => _tabManager.CloseTab(tab);
            menu.Items.Add(itemClose);

            // Andere Tabs schließen
            var itemCloseOthers = new MenuItem
            {
                Header = "Andere Tabs schließen",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                IsEnabled = _tabManager.Tabs.Count > 1
            };
            itemCloseOthers.Click += (s, e) => _tabManager.CloseOtherTabs(tab);
            menu.Items.Add(itemCloseOthers);

            // Rechts liegende Tabs schließen
            bool hasTabsToRight = tabIndex >= 0 && tabIndex < _tabManager.Tabs.Count - 1;
            var itemCloseRight = new MenuItem
            {
                Header = "Rechts liegende Tabs schließen",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                IsEnabled = hasTabsToRight
            };
            itemCloseRight.Click += (s, e) => _tabManager.CloseTabsToTheRight(tab);
            menu.Items.Add(itemCloseRight);

            menu.Items.Add(new Separator { Style = TryFindResource("ModernMenuSeparator") as Style });

            // Geschlossenen Tab wiederherstellen
            var itemReopen = new MenuItem
            {
                Header = "Geschlossenen Tab wiederherstellen",
                InputGestureText = "Strg+Umschalt+T",
                Style = TryFindResource("ModernMenuItemStyle") as Style,
                IsEnabled = _tabManager.HasRecentlyClosed,
                Icon = new TextBlock
                {
                    Text = "\uE777",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = TryFindResource("TextSecondaryBrush") as Brush
                }
            };
            itemReopen.Click += (s, e) => _tabManager.ReopenLastClosedTab();
            menu.Items.Add(itemReopen);

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            NavigateHome();
        }

        private void NavigateHome()
        {
            if (_tabManager.ActiveTab != null)
            {
                NavigateTab(_tabManager.ActiveTab, SettingsService.Instance.HomePage);
            }
        }

        // ── Downloads UI ────────────────────────────────────────

        private void UpdateDownloadBadge()
        {
            int count = DownloadManager.Instance.ActiveDownloadsCount;
            if (count > 0)
            {
                DownloadBadgeCount.Text = count.ToString();
                DownloadBadge.Visibility = Visibility.Visible;
            }
            else
            {
                DownloadBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void OnDownloadStarted(DownloadItem item)
        {
            Dispatcher.InvokeAsync(() =>
            {
                DownloadsPopup.IsOpen = true;
                UpdateDownloadBadge();
            });
        }

        private void OnDownloadCompleted(DownloadItem item)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Play completion chime
                try
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
                catch { }

                DownloadsPopup.IsOpen = true;
                UpdateDownloadBadge();
                ShowStatus($"✓ Download fertig: {item.FileName}");
                ScheduleHideStatus(7000);
            });
        }

        private void BtnDownloads_Click(object sender, RoutedEventArgs e)
        {
            DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
        }

        private void BtnClearDownloads_Click(object sender, RoutedEventArgs e)
        {
            DownloadManager.Instance.ClearHistory();
        }

        private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is DownloadItem item)
            {
                DownloadManager.Instance.CancelDownload(item);
            }
        }

        private void BtnOpenDownloadFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is DownloadItem item)
            {
                DownloadManager.Instance.OpenFile(item);
            }
        }

        private void BtnOpenDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is DownloadItem item)
            {
                DownloadManager.Instance.OpenFolder(item);
            }
        }

        private void BtnDiscardDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is DownloadItem item)
            {
                DownloadManager.Instance.CancelDownload(item);
                DownloadManager.Instance.RemoveDownload(item);
                ShowStatus($"Download '{item.FileName}' verworfen.");
                ScheduleHideStatus(3000);
            }
        }

        private void BtnKeepDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is DownloadItem item)
            {
                DownloadManager.Instance.KeepAndResume(item);
                ShowStatus($"Download '{item.FileName}' freigegeben.");
                ScheduleHideStatus(3000);
            }
        }

        private void BtnRemoveDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is DownloadItem item)
            {
                DownloadManager.Instance.RemoveDownload(item);
            }
        }

        // ── Address bar ─────────────────────────────────────────

        private void AddressBar_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!AddressBar.IsFocused) return;
            var text = AddressBar.Text?.Trim() ?? "";

            // Omnibox preconnect: start DNS resolution while user is still typing
            if (text.Length >= 4 && text.Contains('.'))
            {
                PerformanceService.Instance.PreconnectFromInput(text);
            }

            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                OmniboxSuggestionsPopup.IsOpen = false;
                return;
            }

            var suggestions = new List<OmniboxSuggestion>();

            // 1. Check for search shortcut prefix (e.g. "y lo-fi", "gh wpf")
            if (NavigationService.TryGetSearchShortcut(text, out var shortcutUrl, out var providerName, out var query))
            {
                suggestions.Add(new OmniboxSuggestion
                {
                    Title = $"Auf {providerName} suchen: \"{query}\"",
                    Url = shortcutUrl,
                    DisplayUrl = shortcutUrl,
                    Type = SuggestionType.SearchShortcut,
                    IconGlyph = "\uE721",
                    Badge = providerName,
                    BadgeBrush = (Brush)FindResource("AccentPrimaryBrush")
                });
            }

            // 2. Search in Bookmarks
            var bookmarks = BookmarkService.Instance.Bookmarks
                .Where(b => b.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(2);
            foreach (var b in bookmarks)
            {
                suggestions.Add(new OmniboxSuggestion
                {
                    Title = b.Title,
                    Url = b.Url,
                    DisplayUrl = b.Url,
                    Type = SuggestionType.Bookmark,
                    IconGlyph = "\uE735",
                    Badge = "Lesezeichen",
                    BadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
                });
            }

            // 3. Search in History
            var history = HistoryService.Instance.History
                .Where(h => h.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || h.Url.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(3);
            foreach (var h in history)
            {
                if (!suggestions.Any(s => s.Url.Equals(h.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    suggestions.Add(new OmniboxSuggestion
                    {
                        Title = h.Title,
                        Url = h.Url,
                        DisplayUrl = h.Url,
                        Type = SuggestionType.History,
                        IconGlyph = "\uE81C",
                        Badge = "Verlauf",
                        BadgeBrush = (Brush)FindResource("TextMutedBrush")
                    });
                }
            }

            // 4. Default web search suggestion
            var defaultSearchUrl = string.Format(SettingsService.Instance.SearchEngineUrl, Uri.EscapeDataString(text));
            suggestions.Add(new OmniboxSuggestion
            {
                Title = $"Websuche nach \"{text}\"",
                Url = defaultSearchUrl,
                DisplayUrl = defaultSearchUrl,
                Type = SuggestionType.WebSearch,
                IconGlyph = "\uE721",
                Badge = "Suche",
                BadgeBrush = (Brush)FindResource("AccentDimBrush")
            });

            ListSuggestions.ItemsSource = suggestions;
            ListSuggestions.SelectedIndex = -1;
            OmniboxSuggestionsPopup.IsOpen = suggestions.Count > 0;

            _aiSuggestionDebounceTimer?.Stop();
            _aiSuggestionCts?.Cancel();
            if (IsAiQuickAnswerCandidate(text))
            {
                _aiSuggestionDebounceTimer?.Start();
            }
        }

        private void AddressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                if (OmniboxSuggestionsPopup.IsOpen && ListSuggestions.Items.Count > 0)
                {
                    int next = ListSuggestions.SelectedIndex + 1;
                    if (next >= ListSuggestions.Items.Count) next = 0;
                    ListSuggestions.SelectedIndex = next;
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Up)
            {
                if (OmniboxSuggestionsPopup.IsOpen && ListSuggestions.Items.Count > 0)
                {
                    int prev = ListSuggestions.SelectedIndex - 1;
                    if (prev < 0) prev = ListSuggestions.Items.Count - 1;
                    ListSuggestions.SelectedIndex = prev;
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Enter)
            {
                OmniboxSuggestionsPopup.IsOpen = false;
                if (ListSuggestions.SelectedItem is OmniboxSuggestion selected)
                {
                    NavigateTo(selected.Url);
                }
                else
                {
                    NavigateTo(AddressBar.Text);
                }
                _tabManager.ActiveTab?.WebView.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (OmniboxSuggestionsPopup.IsOpen)
                {
                    OmniboxSuggestionsPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }

                if (_tabManager.ActiveTab?.WebView.CoreWebView2 != null)
                    AddressBar.Text = _tabManager.ActiveTab.WebView.CoreWebView2.Source;
                _tabManager.ActiveTab?.WebView.Focus();
                e.Handled = true;
            }
        }

        private void ListSuggestions_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ListSuggestions.SelectedItem is OmniboxSuggestion selected)
            {
                OmniboxSuggestionsPopup.IsOpen = false;
                NavigateTo(selected.Url);
                _tabManager.ActiveTab?.WebView.Focus();
            }
        }

        private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => AddressBar.SelectAll());
            // Accent border on focus
            AddressBarBorder.BorderBrush = (Brush)FindResource("AccentDimBrush");

            if (AddressBarGlow != null)
            {
                var anim = new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                AddressBarGlow.BeginAnimation(DropShadowEffect.OpacityProperty, anim);
            }
        }

        private void AddressBar_LostFocus(object sender, RoutedEventArgs e)
        {
            // Reset border
            AddressBarBorder.BorderBrush = (Brush)FindResource("BorderSubtleBrush");

            if (AddressBarGlow != null)
            {
                var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                AddressBarGlow.BeginAnimation(DropShadowEffect.OpacityProperty, anim);
            }

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(180);
                if (!AddressBar.IsFocused && !ListSuggestions.IsMouseOver)
                {
                    OmniboxSuggestionsPopup.IsOpen = false;
                }
            });
        }

        // ── Smart Omnibox AI Answers & Translation ────────────────

        private static readonly string[] QuestionStarters = new[]
        {
            "wer ", "wie ", "was ", "warum ", "wann ", "wo ", "kann ", "ist ", "welche ", "welcher ", "welches ",
            "definieren ", "erkläre ", "what ", "how ", "why ", "when ", "where ", "who ", "is ", "can "
        };

        private bool IsAiQuickAnswerCandidate(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 4) return false;
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                (text.Contains(".") && !text.Contains(" ")))
                return false;

            if (text.StartsWith("?", StringComparison.Ordinal) || 
                text.StartsWith("@ai", StringComparison.OrdinalIgnoreCase) || 
                text.StartsWith("ai:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (text.EndsWith("?"))
                return true;

            foreach (var q in QuestionStarters)
            {
                if (text.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private async void AiSuggestionDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _aiSuggestionDebounceTimer?.Stop();
            if (!AddressBar.IsFocused) return;

            var rawText = AddressBar.Text?.Trim() ?? "";
            if (!IsAiQuickAnswerCandidate(rawText)) return;

            var query = rawText;
            if (query.StartsWith("?", StringComparison.Ordinal)) query = query.TrimStart('?', ' ');
            else if (query.StartsWith("@ai", StringComparison.OrdinalIgnoreCase)) query = query.Substring(3).TrimStart();
            else if (query.StartsWith("ai:", StringComparison.OrdinalIgnoreCase)) query = query.Substring(3).TrimStart();

            if (string.IsNullOrWhiteSpace(query)) return;

            _aiSuggestionCts?.Cancel();
            _aiSuggestionCts = new CancellationTokenSource();
            var ct = _aiSuggestionCts.Token;

            try
            {
                var gemini = AiChatService.Instance.Backend as GeminiBackend ?? new GeminiBackend();
                var messages = new List<ChatMessage>
                {
                    new ChatMessage
                    {
                        Role = ChatMessageRole.System,
                        Content = "Du bist eine blitzschnelle Suchmaschinen-KI in der Adressleiste. Beantworte die Frage des Benutzers präzise in 1 bis maximal 2 kurzen Sätzen auf Deutsch. Verwende keine Aufzählungen und keine Markdown-Überschriften."
                    },
                    new ChatMessage
                    {
                        Role = ChatMessageRole.User,
                        Content = query
                    }
                };

                var answer = await gemini.ChatAsync(messages, ct);
                if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(answer)) return;

                Dispatcher.Invoke(() =>
                {
                    if (!AddressBar.IsFocused || !OmniboxSuggestionsPopup.IsOpen) return;
                    if (!string.Equals(AddressBar.Text?.Trim(), rawText, StringComparison.OrdinalIgnoreCase)) return;

                    var currentSuggestions = (ListSuggestions.ItemsSource as IEnumerable<OmniboxSuggestion>)?.ToList() ?? new List<OmniboxSuggestion>();
                    currentSuggestions.RemoveAll(s => s.Type == SuggestionType.AiQuickAnswer);

                    var defaultSearchUrl = string.Format(SettingsService.Instance.SearchEngineUrl, Uri.EscapeDataString(query));
                    var aiItem = new OmniboxSuggestion
                    {
                        Title = answer.Trim(),
                        Url = defaultSearchUrl,
                        DisplayUrl = "Gemini Direktantwort",
                        Type = SuggestionType.AiQuickAnswer,
                        IconGlyph = "\uE945",
                        Badge = "✨ Gemini",
                        BadgeBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6))
                    };

                    currentSuggestions.Insert(0, aiItem);
                    ListSuggestions.ItemsSource = currentSuggestions;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiQuickAnswer] Error: {ex.Message}");
            }
        }

        private async void BtnTranslatePage_Click(object sender, RoutedEventArgs e)
        {
            var activeTab = _tabManager.ActiveTab;
            if (activeTab?.WebView?.CoreWebView2 == null) return;

            TranslationBanner.Visibility = Visibility.Visible;
            TxtTranslationStatus.Text = "Seite wird ins Deutsche übersetzt...";

            _translationCts?.Cancel();
            _translationCts = new CancellationTokenSource();

            bool success = await TranslationService.Instance.TranslatePageAsync(
                activeTab.WebView.CoreWebView2,
                status =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtTranslationStatus.Text = status;
                    });
                },
                _translationCts.Token
            );

            if (success)
            {
                TranslatePageIcon.Foreground = (Brush)FindResource("AccentLightBrush");
            }
        }

        private async void BtnRevertTranslation_Click(object sender, RoutedEventArgs e)
        {
            var activeTab = _tabManager.ActiveTab;
            if (activeTab?.WebView?.CoreWebView2 != null)
            {
                await TranslationService.Instance.RevertAsync(activeTab.WebView.CoreWebView2);
            }
            TxtTranslationStatus.Text = "Originaltext wiederhergestellt.";
            TranslatePageIcon.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }

        private void BtnCloseTranslationBanner_Click(object sender, RoutedEventArgs e)
        {
            TranslationBanner.Visibility = Visibility.Collapsed;
        }

        // ── Password Vault & Autofill Banner ─────────────────────

        private void OnWebViewWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;

                if (raw.Contains("nexa_password_submitted"))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("action", out var actionProp) && actionProp.GetString() == "nexa_password_submitted")
                    {
                        string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string host = root.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
                        string user = root.TryGetProperty("username", out var usr) ? usr.GetString() ?? "" : "";
                        string pass = root.TryGetProperty("password", out var pwd) ? pwd.GetString() ?? "" : "";

                        if (!string.IsNullOrWhiteSpace(pass))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                ShowPasswordSavePrompt(url, host, user, pass);
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void ShowPasswordSavePrompt(string url, string host, string user, string pass)
        {
            if (string.IsNullOrWhiteSpace(pass)) return;

            if (string.IsNullOrWhiteSpace(host) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }

            _pendingPasswordEntry = new PasswordEntry
            {
                Title = !string.IsNullOrWhiteSpace(host) ? host : "Webseite",
                Url = url,
                Host = host,
                Username = user,
                Password = pass
            };

            string displayUser = string.IsNullOrWhiteSpace(user) ? "dieses Konto" : $"'{user}'";
            TxtPasswordBannerText.Text = $"Passwort für {displayUser} auf '{host}' im Nexa Tresor speichern?";
            PasswordSaveBanner.Visibility = Visibility.Visible;
        }

        private void BtnClosePasswordBanner_Click(object sender, RoutedEventArgs e)
        {
            PasswordSaveBanner.Visibility = Visibility.Collapsed;
            _pendingPasswordEntry = null;
        }

        private void BtnNeverSavePassword_Click(object sender, RoutedEventArgs e)
        {
            PasswordSaveBanner.Visibility = Visibility.Collapsed;
            _pendingPasswordEntry = null;
        }

        private void BtnSavePassword_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingPasswordEntry != null)
            {
                PasswordService.Instance.AddOrUpdate(_pendingPasswordEntry);
                ShowStatus($"✓ Passwort für '{_pendingPasswordEntry.Title}' sicher im Tresor gespeichert.");
                ScheduleHideStatus(3000);
                _pendingPasswordEntry = null;
            }
            PasswordSaveBanner.Visibility = Visibility.Collapsed;
        }

        // ── Helpers ─────────────────────────────────────────────

        private void UpdateNavigationButtons()
        {
            var tab = _tabManager.ActiveTab;
            BtnBack.IsEnabled = tab?.WebView.CanGoBack == true;
            BtnForward.IsEnabled = tab?.WebView.CanGoForward == true;
        }

        private void ShowStatus(string text)
        {
            _statusCts?.Cancel();
            StatusText.Text = text;
            StatusBar.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            _statusCts?.Cancel();
            StatusBar.Visibility = Visibility.Collapsed;
        }

        private async void ScheduleHideStatus(int delayMs)
        {
            _statusCts?.Cancel();
            _statusCts = new CancellationTokenSource();
            var token = _statusCts.Token;

            try
            {
                await Task.Delay(delayMs, token);
                if (!token.IsCancellationRequested)
                {
                    HideStatus();
                }
            }
            catch (TaskCanceledException)
            {
                // Status updated or window closed
            }
        }

        // ── Find in Page ────────────────────────────────────────

        private void ShowFindBar()
        {
            FindBar.Visibility = Visibility.Visible;
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        }

        private void HideFindBar()
        {
            FindBar.Visibility = Visibility.Collapsed;
            _tabManager.ActiveTab?.WebView.Focus();
        }

        private void BtnFindClose_Click(object sender, RoutedEventArgs e)
        {
            HideFindBar();
        }

        private void BtnFindNext_Click(object sender, RoutedEventArgs e)
        {
            FindNext(backwards: false);
        }

        private void BtnFindPrev_Click(object sender, RoutedEventArgs e)
        {
            FindNext(backwards: true);
        }

        private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool backwards = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                FindNext(backwards);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideFindBar();
                e.Handled = true;
            }
        }

        private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(FindTextBox.Text))
            {
                FindNext(backwards: false);
            }
        }

        private async void FindNext(bool backwards)
        {
            var text = FindTextBox.Text;
            if (string.IsNullOrEmpty(text) || _tabManager.ActiveTab?.WebView.CoreWebView2 == null) return;

            var escaped = text.Replace("\\", "\\\\").Replace("'", "\\'");
            var script = $"window.find('{escaped}', false, {backwards.ToString().ToLower()}, true, false, true, false)";
            try
            {
                await _tabManager.ActiveTab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch
            {
                // Ignore script error
            }
        }

        // ── Zoom ────────────────────────────────────────────────

        private void AdjustZoom(double delta, bool reset = false)
        {
            var webView = _tabManager.ActiveTab?.WebView;
            if (webView == null) return;

            if (reset)
                webView.ZoomFactor = 1.0;
            else
                webView.ZoomFactor = Math.Clamp(webView.ZoomFactor + delta, 0.25, 3.0);

            int percent = (int)Math.Round(webView.ZoomFactor * 100);
            ShowStatus($"Zoom: {percent}%");
            ScheduleHideStatus(1500);
        }

        // ── Bookmarks ───────────────────────────────────────────

        private void UpdateBookmarkStar()
        {
            var url = _tabManager.ActiveTab?.WebView.CoreWebView2?.Source ?? _tabManager.ActiveTab?.Url;
            bool isBookmarked = !string.IsNullOrEmpty(url) && BookmarkService.Instance.IsBookmarked(url);
            BookmarkStarIcon.Text = isBookmarked ? "\uE735" : "\uE734";
            BookmarkStarIcon.Foreground = isBookmarked
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
                : (Brush)FindResource("TextMutedBrush");
            BtnBookmarkStar.ToolTip = isBookmarked ? "Lesezeichen entfernen (Strg+D)" : "Lesezeichen hinzufügen (Strg+D)";
        }

        // ── Privacy Shield UI ────────────────────────────────────

        private void UpdateShieldUI()
        {
            bool isGlobalEnabled = AdBlockService.Instance.IsEnabled;

            if (ChkShieldGlobalToggle != null)
                ChkShieldGlobalToggle.IsChecked = isGlobalEnabled;

            if (ChkShieldYouTubeToggle != null)
                ChkShieldYouTubeToggle.IsChecked = SettingsService.Instance.Settings.EnableYouTubeAdBlocker;

            var activeBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
            var inactiveBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // Red
            var mutedBrush = (Brush)FindResource("TextSecondaryBrush");

            if (ToolbarAdBlockDot != null)
            {
                ToolbarAdBlockDot.Fill = isGlobalEnabled ? activeBrush : inactiveBrush;
            }

            if (TxtAdBlockStatusBadge != null)
            {
                TxtAdBlockStatusBadge.Text = isGlobalEnabled ? "Aktiv" : "Aus";
                TxtAdBlockStatusBadge.Foreground = isGlobalEnabled ? activeBrush : inactiveBrush;
            }

            if (ShieldHeaderIconBorder != null)
            {
                ShieldHeaderIconBorder.Background = isGlobalEnabled 
                    ? new SolidColorBrush(Color.FromArgb(0x28, 0x10, 0xB9, 0x81))
                    : new SolidColorBrush(Color.FromArgb(0x28, 0xEF, 0x44, 0x44));
            }
            if (ShieldHeaderIcon != null)
            {
                ShieldHeaderIcon.Foreground = isGlobalEnabled ? activeBrush : inactiveBrush;
            }

            var tab = _tabManager.ActiveTab;
            if (tab == null)
            {
                ShieldBadge.Visibility = Visibility.Collapsed;
                return;
            }

            int count = AdBlockService.Instance.GetBlockedCount(tab.Id);
            if (count > 0 && isGlobalEnabled)
            {
                ShieldBadgeText.Text = count > 99 ? "99+" : count.ToString();
                ShieldBadge.Visibility = Visibility.Visible;
                ShieldIcon.Foreground = (Brush)FindResource("AccentLightBrush");
            }
            else
            {
                ShieldBadge.Visibility = Visibility.Collapsed;
                ShieldIcon.Foreground = isGlobalEnabled ? mutedBrush : inactiveBrush;
            }

            string host = "Lokale Seite";
            try
            {
                if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                    host = uri.Host;
            }
            catch { }

            TxtShieldHost.Text = host;
            TxtShieldBlockedCount.Text = $"{count} Tracker & Werbung";
            ChkShieldSiteToggle.IsChecked = !AdBlockService.Instance.IsWhitelisted(host);
            ChkShieldSiteToggle.IsEnabled = isGlobalEnabled;
        }

        private void BtnShield_Click(object sender, RoutedEventArgs e)
        {
            if (sender is UIElement target)
            {
                ShieldPopup.PlacementTarget = target;
                ShieldPopup.HorizontalOffset = target == BtnToolbarAdBlock ? -250 : -230;
            }
            UpdateShieldUI();
            ShieldPopup.IsOpen = !ShieldPopup.IsOpen;
        }

        private void ChkShieldGlobalToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isEnabled = ChkShieldGlobalToggle.IsChecked ?? true;
            SettingsService.Instance.Settings.EnableAdBlocker = isEnabled;
            SettingsService.Instance.Save();

            UpdateShieldUI();

            var tab = _tabManager.ActiveTab;
            tab?.WebView?.CoreWebView2?.Reload();

            ShowStatus(isEnabled ? "AdBlocker aktiviert" : "AdBlocker deaktiviert");
            ScheduleHideStatus(2000);
        }

        private void ChkShieldYouTubeToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isEnabled = ChkShieldYouTubeToggle.IsChecked ?? true;
            SettingsService.Instance.Settings.EnableYouTubeAdBlocker = isEnabled;
            SettingsService.Instance.Save();

            var tab = _tabManager.ActiveTab;
            if (tab != null && tab.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                tab.WebView?.CoreWebView2?.Reload();
            }

            ShowStatus(isEnabled ? "YouTube Werbeblocker aktiviert" : "YouTube Werbeblocker deaktiviert");
            ScheduleHideStatus(2000);
        }

        private void BtnShieldReload_Click(object sender, RoutedEventArgs e)
        {
            ShieldPopup.IsOpen = false;
            _tabManager.ActiveTab?.WebView?.CoreWebView2?.Reload();
        }

        private void ChkShieldSiteToggle_Click(object sender, RoutedEventArgs e)
        {
            var tab = _tabManager.ActiveTab;
            if (tab == null) return;

            try
            {
                if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                {
                    bool isEnabledForSite = ChkShieldSiteToggle.IsChecked ?? true;
                    AdBlockService.Instance.SetWhitelisted(uri.Host, !isEnabledForSite);
                    tab.WebView.CoreWebView2?.Reload();
                }
            }
            catch { }
        }

        private void BtnBookmarkStar_Click(object sender, RoutedEventArgs e)
        {
            ToggleCurrentBookmark();
        }

        private void ToggleCurrentBookmark()
        {
            var tab = _tabManager.ActiveTab;
            if (tab == null) return;
            var url = tab.WebView.CoreWebView2?.Source ?? tab.Url;
            if (string.IsNullOrEmpty(url) || url.StartsWith("about:")) return;

            bool added = BookmarkService.Instance.ToggleBookmark(url, tab.Title);
            UpdateBookmarkStar();
            ShowStatus(added ? "Lesezeichen hinzugefügt" : "Lesezeichen entfernt");
            ScheduleHideStatus(2500);
        }

        private void BookmarkBarItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is BookmarkItem bookmark)
            {
                NavigateTo(bookmark.Url);
            }
        }

        // ── History UI ──────────────────────────────────────────

        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
            if (HistoryPopup.IsOpen)
            {
                HistorySearchBox.Text = string.Empty;
                HistoryList.ItemsSource = HistoryService.Instance.History;
                HistorySearchBox.Focus();
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Möchtest du wirklich den gesamten Verlauf löschen?", BrandingConfig.BrowserName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                HistoryService.Instance.ClearAll();
            }
        }

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = HistorySearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                HistoryList.ItemsSource = HistoryService.Instance.History;
            }
            else
            {
                HistoryList.ItemsSource = HistoryService.Instance.History
                    .Where(h => h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                h.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is HistoryEntry entry)
            {
                HistoryPopup.IsOpen = false;
                NavigateTo(entry.Url);
            }
        }

        private void BtnDeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is HistoryEntry entry)
            {
                HistoryService.Instance.DeleteEntry(entry);
            }
        }

        // ── Theme Picker ─────────────────────────────────────────

        private void BtnThemePicker_Click(object sender, RoutedEventArgs e)
        {
            ThemePopup.IsOpen = !ThemePopup.IsOpen;
        }

        private void ThemeOption_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ColorTheme theme)
            {
                ThemeService.Instance.ApplyTheme(theme.Id);
                ThemePopup.IsOpen = false;
                ShowStatus($"Theme '{theme.Name}' aktiviert.");
                ScheduleHideStatus(2500);
            }
        }

        // ── Gradient Loading Bar ─────────────────────────────────

        private void StartLoadingProgress()
        {
            if (LoadingBarContainer == null || LoadingBarProgress == null) return;

            LoadingBarContainer.Visibility = Visibility.Visible;
            LoadingBarContainer.Opacity = 1.0;
            LoadingBarProgress.BeginAnimation(WidthProperty, null);
            LoadingBarProgress.Width = 0;

            double containerWidth = LoadingBarContainer.ActualWidth > 0 ? LoadingBarContainer.ActualWidth : 800;
            double targetWidth = containerWidth * 0.72;

            var anim = new DoubleAnimation(0, targetWidth, TimeSpan.FromMilliseconds(550))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            LoadingBarProgress.BeginAnimation(WidthProperty, anim);
        }

        private void CompleteLoadingProgress()
        {
            if (LoadingBarContainer == null || LoadingBarProgress == null) return;
            if (LoadingBarContainer.Visibility != Visibility.Visible) return;

            double fullWidth = LoadingBarContainer.ActualWidth > 0 ? LoadingBarContainer.ActualWidth : 1200;
            double currentWidth = LoadingBarProgress.ActualWidth;

            var finishAnim = new DoubleAnimation(currentWidth, fullWidth, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            finishAnim.Completed += (s, e) =>
            {
                var fadeAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(220));
                fadeAnim.Completed += (s2, e2) =>
                {
                    ResetLoadingProgress();
                };
                LoadingBarContainer.BeginAnimation(OpacityProperty, fadeAnim);
            };
            LoadingBarProgress.BeginAnimation(WidthProperty, finishAnim);
        }

        private void ResetLoadingProgress()
        {
            if (LoadingBarContainer == null || LoadingBarProgress == null) return;
            LoadingBarContainer.BeginAnimation(OpacityProperty, null);
            LoadingBarProgress.BeginAnimation(WidthProperty, null);
            LoadingBarContainer.Visibility = Visibility.Collapsed;
            LoadingBarProgress.Width = 0;
            LoadingBarContainer.Opacity = 1.0;
        }

        // ── Main App Menu ───────────────────────────────────────

        private void BtnAppMenu_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = !AppMenuPopup.IsOpen;
        }

        private void MenuNewWindow_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            new MainWindow().Show();
        }

        private void MenuNewIncognito_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            new MainWindow(isIncognito: true).Show();
        }

        private void MenuSavePage_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            SaveCurrentPageAsync();
        }

        private void MenuPrint_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            if (_tabManager.ActiveTab?.WebView.CoreWebView2 != null)
            {
                _tabManager.ActiveTab.WebView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            }
        }

        private void MenuFind_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            ShowFindBar();
        }

        private void MenuToggleBookmarksBar_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            BookmarksBar.Visibility = BookmarksBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void MenuClearData_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            ClearBrowsingDataAsync();
        }

        private void MenuFullscreen_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            ToggleFullscreen();
        }

        private void MenuPip_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            TogglePictureInPictureAsync();
        }

        private void MenuTaskManager_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            OpenTaskManager();
        }

        private void MenuImport_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            int count = ImportService.PromptAndImportBookmarks();
            if (count > 0)
            {
                ShowStatus($"{count} Lesezeichen erfolgreich importiert");
                ScheduleHideStatus(3000);
            }
        }

        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            OpenSettings();
        }

        private void MenuPasswordVault_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            OpenPasswordVault();
        }

        private void MenuGoogleSync_Click(object sender, RoutedEventArgs e)
        {
            AppMenuPopup.IsOpen = false;
            OpenGoogleSync();
        }

        private void OpenTaskManager()
        {
            try
            {
                var tm = new Views.TaskManagerWindow(_tabManager) { Owner = this };
                tm.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Öffnen des Task-Managers: {ex.Message}",
                                BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenSettings()
        {
            // Check if a settings tab is already open; if so, switch to it
            var existingSettingsTab = _tabManager.Tabs.FirstOrDefault(t => t.IsSettingsTab);
            if (existingSettingsTab != null)
            {
                _tabManager.SetActiveTab(existingSettingsTab);
                // Refresh settings values
                if (existingSettingsTab.CustomView is SettingsView sv)
                    sv.LoadCurrentSettings();
                return;
            }

            // Create a new tab for settings
            var tab = _tabManager.AddTab("about:settings");
            ShowSettingsInTab(tab);
        }

        private void OpenPasswordVault()
        {
            var existingSettingsTab = _tabManager.Tabs.FirstOrDefault(t => t.IsSettingsTab);
            if (existingSettingsTab != null)
            {
                _tabManager.SetActiveTab(existingSettingsTab);
                if (existingSettingsTab.CustomView is SettingsView sv)
                {
                    sv.SelectTab("Passwörter");
                }
                return;
            }

            var tab = _tabManager.AddTab("about:passwords");
            ShowSettingsInTab(tab, "Passwörter");
        }

        private void OpenGoogleSync()
        {
            var existingSettingsTab = _tabManager.Tabs.FirstOrDefault(t => t.IsSettingsTab);
            if (existingSettingsTab != null)
            {
                _tabManager.SetActiveTab(existingSettingsTab);
                if (existingSettingsTab.CustomView is SettingsView sv)
                {
                    sv.SelectTab("Sync");
                }
                return;
            }

            var tab = _tabManager.AddTab("about:sync");
            ShowSettingsInTab(tab, "Sync");
        }

        private async void TogglePictureInPictureAsync()
        {
            var tab = _tabManager.ActiveTab;
            if (tab?.WebView.CoreWebView2 == null) return;

            var script = @"
                (async () => {
                    const video = document.querySelector('video');
                    if (!video) return 'no_video';
                    if (document.pictureInPictureElement) {
                        await document.exitPictureInPicture();
                        return 'exited';
                    } else if (document.pictureInPictureEnabled) {
                        await video.requestPictureInPicture();
                        return 'entered';
                    }
                    return 'not_supported';
                })();
            ";
            try
            {
                var result = await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
                if (result != null && result.Contains("entered"))
                {
                    ShowStatus("Picture-in-Picture aktiviert");
                    ScheduleHideStatus(2500);
                }
                else if (result != null && result.Contains("exited"))
                {
                    ShowStatus("Picture-in-Picture beendet");
                    ScheduleHideStatus(2500);
                }
                else if (result != null && result.Contains("no_video"))
                {
                    ShowStatus("Kein Video auf der Seite gefunden");
                    ScheduleHideStatus(2500);
                }
            }
            catch
            {
                // Ignore script errors
            }
        }

        // ── Advanced Actions ────────────────────────────────────

        private async void SaveCurrentPageAsync()
        {
            var tab = _tabManager.ActiveTab;
            if (tab?.WebView.CoreWebView2 == null) return;

            var safeTitle = string.Join("_", (tab.Title ?? "webseite").Split(Path.GetInvalidFileNameChars()));
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Webseite speichern unter",
                Filter = "HTML-Datei (*.html)|*.html|Alle Dateien (*.*)|*.*",
                FileName = $"{safeTitle}.html"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var html = await tab.WebView.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                    var unescaped = System.Text.Json.JsonSerializer.Deserialize<string>(html) ?? html;
                    await File.WriteAllTextAsync(sfd.FileName, unescaped);
                    ShowStatus($"Seite gespeichert: {Path.GetFileName(sfd.FileName)}");
                    ScheduleHideStatus(3000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Fehler beim Speichern: {ex.Message}", BrandingConfig.BrowserName, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void ClearBrowsingDataAsync()
        {
            if (MessageBox.Show("Möchtest du Browserdaten (Cookies, Cache, Speicher) löschen?",
                                BrandingConfig.BrowserName,
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try
                {
                    var tab = _tabManager.ActiveTab;
                    if (tab?.WebView.CoreWebView2 != null)
                    {
                        await tab.WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                    }
                    HistoryService.Instance.ClearAll();
                    ShowStatus("Browserdaten erfolgreich gelöscht.");
                    ScheduleHideStatus(3000);
                }
                catch (Exception ex)
                {
                    ShowStatus($"Fehler beim Löschen: {ex.Message}");
                }
            }
        }

        private void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;
            if (_isFullscreen)
            {
                _previousWindowState = WindowState;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                TabBarRow.Height = new GridLength(0);
                ToolbarRow.Height = new GridLength(0);
                BookmarksBar.Visibility = Visibility.Collapsed;
                ShowStatus("Vollbildmodus (F11 zum Beenden)");
                ScheduleHideStatus(2500);
            }
            else
            {
                WindowState = _previousWindowState;
                TabBarRow.Height = GridLength.Auto;
                ToolbarRow.Height = GridLength.Auto;
                BookmarksBar.Visibility = Visibility.Visible;
            }
        }

        // ── Split View ──────────────────────────────────────────

        private void BtnSplitScreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleSplitView();
        }

        private void BtnCloseSplit_Click(object sender, RoutedEventArgs e)
        {
            CloseSplitView();
        }

        private void MenuSplitView_Click(object sender, RoutedEventArgs e)
        {
            ToggleSplitView();
        }

        public void ToggleSplitView(BrowserTab? targetTab = null)
        {
            if (_isSplitViewActive)
            {
                if (targetTab != null && targetTab != _splitTab && targetTab != _tabManager.ActiveTab)
                {
                    CloseSplitView();
                    OpenSplitView(targetTab);
                }
                else
                {
                    CloseSplitView();
                }
            }
            else
            {
                OpenSplitView(targetTab);
            }
        }

        private void OpenSplitView(BrowserTab? targetTab = null)
        {
            var active = _tabManager.ActiveTab;
            if (targetTab == null || targetTab == active)
            {
                targetTab = _tabManager.Tabs.FirstOrDefault(t => t != active);
                if (targetTab == null)
                {
                    targetTab = _tabManager.AddTab(SettingsService.Instance.HomePage);
                    if (active != null)
                    {
                        _tabManager.SetActiveTab(active);
                    }
                }
            }

            if (targetTab == null || targetTab == active) return;

            _isSplitViewActive = true;
            _splitTab = targetTab;
            _splitTab.PropertyChanged += SplitTab_PropertyChanged;

            if (WebViewHost.Children.Contains(targetTab.WebView))
            {
                WebViewHost.Children.Remove(targetTab.WebView);
            }
            if (!SecondaryWebViewHost.Children.Contains(targetTab.WebView))
            {
                SecondaryWebViewHost.Children.Add(targetTab.WebView);
            }
            targetTab.WebView.Visibility = Visibility.Visible;

            UpdateSplitTabInfo();

            SplitColPrimary.Width = new GridLength(1, GridUnitType.Star);
            SplitColSplitter.Width = new GridLength(4);
            SplitColSecondary.Width = new GridLength(1, GridUnitType.Star);

            SplitResizer.Visibility = Visibility.Visible;
            SecondaryHost.Visibility = Visibility.Visible;

            BtnSplitScreen.Background = (Brush)FindResource("AccentDimBrush");
            SplitScreenIcon.Foreground = (Brush)FindResource("AccentLightBrush");
            BtnSplitScreen.ToolTip = "Geteilte Ansicht schließen (Strg+\\)";
            ShowStatus("Geteilte Ansicht aktiviert");
            ScheduleHideStatus(2000);
        }

        private void CloseSplitView()
        {
            if (!_isSplitViewActive) return;

            _isSplitViewActive = false;

            if (_splitTab != null)
            {
                _splitTab.PropertyChanged -= SplitTab_PropertyChanged;

                if (SecondaryWebViewHost.Children.Contains(_splitTab.WebView))
                {
                    SecondaryWebViewHost.Children.Remove(_splitTab.WebView);
                }
                if (!WebViewHost.Children.Contains(_splitTab.WebView))
                {
                    WebViewHost.Children.Add(_splitTab.WebView);
                }

                if (_splitTab != _tabManager.ActiveTab)
                {
                    _splitTab.WebView.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _splitTab.WebView.Visibility = Visibility.Visible;
                }

                _splitTab = null;
            }

            SecondaryHost.Visibility = Visibility.Collapsed;
            SplitResizer.Visibility = Visibility.Collapsed;
            SplitColSecondary.Width = new GridLength(0);
            SplitColSplitter.Width = new GridLength(0);
            SplitColPrimary.Width = new GridLength(1, GridUnitType.Star);

            BtnSplitScreen.ClearValue(BackgroundProperty);
            SplitScreenIcon.ClearValue(TextBlock.ForegroundProperty);
            BtnSplitScreen.ToolTip = "Geteilte Ansicht (Strg+\\)";
            ShowStatus("Geteilte Ansicht geschlossen");
            ScheduleHideStatus(1500);
        }

        private void SplitTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BrowserTab.Title) || e.PropertyName == nameof(BrowserTab.Url))
            {
                UpdateSplitTabInfo();
            }
        }

        private void UpdateSplitTabInfo()
        {
            if (_splitTab != null)
            {
                TxtSplitTitle.Text = string.IsNullOrWhiteSpace(_splitTab.Title) ? "Geteilte Ansicht" : _splitTab.Title;
                TxtSplitUrl.Text = NavigationService.GetDisplayUrl(_splitTab.Url);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // AI Assistant Integration
        // ════════════════════════════════════════════════════════════════

        private TaskCompletionSource<bool>? _codingAgentConfirmTcs;
        private bool _isAiSidebarOpen;
        private const double AiSidebarWidth = 380;

        private void InitializeAiAssistant()
        {
            // Bind items
            AiMessagesControl.ItemsSource = AiChatService.Instance.Messages;

            // Monitor collection changes to show/hide empty state and auto-scroll
            AiChatService.Instance.Messages.CollectionChanged += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    AiEmptyState.Visibility = AiChatService.Instance.Messages.Count == 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    AiChatScrollViewer.ScrollToEnd();
                });
            };

            // Monitor streaming state
            AiChatService.Instance.StreamingStateChanged += isStreaming =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (isStreaming)
                    {
                        AiSendButtonIcon.Text = "\uE71A"; // Stop icon
                        BtnAiSend.ToolTip = "Antwort stoppen";
                    }
                    else
                    {
                        AiSendButtonIcon.Text = "\uE724"; // Send icon
                        BtnAiSend.ToolTip = "Senden (Enter)";
                    }
                });
            };

            // Monitor backend status changes
            AiChatService.Instance.BackendStatusChanged += isOnline =>
            {
                Dispatcher.Invoke(() => UpdateAiBackendStatusUi(isOnline));
            };

            // Monitor WebLLM model loading progress
            AiChatService.Instance.ModelLoadProgressChanged += (progress, text) =>
            {
                Dispatcher.Invoke(() =>
                {
                    AiModelLoadingPanel.Visibility = Visibility.Visible;
                    AiModelProgressBar.Value = progress * 100;
                    AiModelLoadingStatus.Text = text;

                    if (progress >= 1.0)
                    {
                        AiModelLoadingTitle.Text = "KI-Modell bereit!";
                        AiModelLoadingStatus.Text = "Das Modell wurde erfolgreich geladen.";

                        // Auto-hide after a short delay
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(2)
                        };
                        timer.Tick += (s2, e2) =>
                        {
                            timer.Stop();
                            AiModelLoadingPanel.Visibility = Visibility.Collapsed;
                            UpdateAiBackendStatusUi(true);
                        };
                        timer.Start();
                    }
                });
            };

            // Hook CodingAgent confirmation event
            AiChatService.Instance.CodingAgent.ConfirmationRequired += OnCodingAgentConfirmationRequired;

            // Load initial context checkbox settings
            var aiSettings = SettingsService.Instance.GetAiSettings();
            ChkContextUrl.IsChecked = aiSettings.ContextUrl;
            ChkContextTitle.IsChecked = aiSettings.ContextTitle;
            ChkContextSelection.IsChecked = aiSettings.ContextSelection;
            ChkContextPage.IsChecked = aiSettings.ContextPage;
            ChkContextTabs.IsChecked = aiSettings.ContextTabs;
            UpdateActiveContextText();

            // Display the correct backend name
            var backendDisplayName = aiSettings.BackendType switch
            {
                "WebLLM" => "Integrierte KI",
                "Gemini" => "Gemini",
                "ChatGPT" => "ChatGPT",
                _ => aiSettings.BackendType
            };
            AiBackendNameText.Text = backendDisplayName;

            if (aiSettings.BackendType == "Gemini")
            {
                AiModelNameText.Text = string.IsNullOrEmpty(aiSettings.SelectedModel) ? "gemini-3.6-flash" : aiSettings.SelectedModel;
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Visible;
            }
            else if (aiSettings.BackendType == "ChatGPT")
            {
                AiModelNameText.Text = string.IsNullOrEmpty(aiSettings.SelectedModel) ? "gpt-4o-mini" : aiSettings.SelectedModel;
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Visible;
            }
            else if (aiSettings.BackendType == "WebLLM")
            {
                AiModelNameText.Text = "Qwen 0.5B";
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Visible;
            }
            else
            {
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Collapsed;
            }

            // Background backend ping (skip for WebLLM – it initializes lazily on first use)
            if (aiSettings.BackendType != "WebLLM")
            {
                Task.Run(async () =>
                {
                    await AiChatService.Instance.CheckBackendAsync();
                });
            }
            else
            {
                // For WebLLM, mark as "available" immediately (the engine loads on first message)
                UpdateAiBackendStatusUi(true);
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Escape: if QuickSearch is open, close it
            if (e.Key == Key.Escape && QuickSearchOverlay.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseQuickSearch();
                return;
            }

            // Ctrl+Shift+A: Toggle AI Sidebar
            if (e.Key == Key.A &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                ToggleAiSidebar();
                return;
            }

            // Ctrl+K or Ctrl+Shift+K: Toggle Quick Search
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ToggleQuickSearch();
                return;
            }
        }

        private void BtnAiChat_Click(object sender, RoutedEventArgs e)
        {
            ToggleAiSidebar();
        }

        private void BtnAiClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isAiSidebarOpen)
            {
                ToggleAiSidebar();
            }
        }

        private void ToggleAiSidebar()
        {
            _isAiSidebarOpen = !_isAiSidebarOpen;

            if (_isAiSidebarOpen)
            {
                AiSidebar.Visibility = Visibility.Visible;
            }

            if (SettingsService.Instance.EnableAnimations)
            {
                var anim = new DoubleAnimation
                {
                    From = _isAiSidebarOpen ? 0 : AiSidebarWidth,
                    To = _isAiSidebarOpen ? AiSidebarWidth : 0,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                };

                if (!_isAiSidebarOpen)
                {
                    anim.Completed += (s, e) =>
                    {
                        if (!_isAiSidebarOpen)
                        {
                            AiSidebar.Visibility = Visibility.Collapsed;
                        }
                    };
                }

                AiSidebar.BeginAnimation(WidthProperty, anim);
            }
            else
            {
                AiSidebar.Width = _isAiSidebarOpen ? AiSidebarWidth : 0;
                if (!_isAiSidebarOpen)
                {
                    AiSidebar.Visibility = Visibility.Collapsed;
                }
            }

            if (_isAiSidebarOpen)
            {
                Task.Run(async () =>
                {
                    try { await AiChatService.Instance.CheckBackendAsync(); } catch { }
                });
                Dispatcher.InvokeAsync(() => AiChatInput?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                var activeBrush = TryFindResource("AccentDimBrush") as Brush 
                                  ?? TryFindResource("BgPressedBrush") as Brush 
                                  ?? Brushes.Transparent;
                var activeIconBrush = TryFindResource("AccentLightBrush") as Brush 
                                      ?? Brushes.White;
                BtnAiChat.Background = activeBrush;
                AiChatIcon.Foreground = activeIconBrush;
            }
            else
            {
                BtnAiChat.ClearValue(BackgroundProperty);
                AiChatIcon.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        private void BtnAiToggleContext_Click(object sender, RoutedEventArgs e)
        {
            AiContextDrawer.Visibility = AiContextDrawer.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ContextChip_Click(object sender, RoutedEventArgs e)
        {
            var aiSettings = SettingsService.Instance.GetAiSettings();
            aiSettings.ContextUrl = ChkContextUrl.IsChecked == true;
            aiSettings.ContextTitle = ChkContextTitle.IsChecked == true;
            aiSettings.ContextSelection = ChkContextSelection.IsChecked == true;
            aiSettings.ContextPage = ChkContextPage.IsChecked == true;
            aiSettings.ContextTabs = ChkContextTabs.IsChecked == true;

            SettingsService.Instance.UpdateAiSettings(aiSettings);
            UpdateActiveContextText();
        }

        private void UpdateActiveContextText()
        {
            var parts = new List<string>();
            if (ChkContextUrl.IsChecked == true) parts.Add("URL");
            if (ChkContextTitle.IsChecked == true) parts.Add("Titel");
            if (ChkContextSelection.IsChecked == true) parts.Add("Auswahl");
            if (ChkContextPage.IsChecked == true) parts.Add("Seite");
            if (ChkContextTabs.IsChecked == true) parts.Add("Tabs");

            AiActiveContextText.Text = parts.Count > 0
                ? "🌐 " + string.Join(" · ", parts)
                : "🔒 Kein Kontext";
        }

        private void BtnAiNewChat_Click(object sender, RoutedEventArgs e)
        {
            AiChatService.Instance.NewChat();
            AiEmptyState.Visibility = Visibility.Visible;
        }

        private void AiBackendBadge_Click(object sender, MouseButtonEventArgs e)
        {
            var cm = new ContextMenu
            {
                PlacementTarget = sender as UIElement,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };

            var currentBackend = SettingsService.Instance.Settings.AiBackendType;

            var itemGemini = new MenuItem
            {
                Header = "✨ Google Gemini (Cloud - Schnell & kostenlos)",
                IsChecked = currentBackend == "Gemini",
                IsCheckable = true
            };
            itemGemini.Click += (s, ev) => SwitchAiBackend("Gemini", "https://generativelanguage.googleapis.com/v1beta");
            cm.Items.Add(itemGemini);

            var itemWebLlm = new MenuItem
            {
                Header = "🖥️ Integrierte lokale KI (Keine Installation nötig)",
                IsChecked = currentBackend == "WebLLM",
                IsCheckable = true
            };
            itemWebLlm.Click += (s, ev) => SwitchAiBackend("WebLLM", "https://ai.nexa/runner.html");
            cm.Items.Add(itemWebLlm);

            var itemChatGpt = new MenuItem
            {
                Header = "🤖 ChatGPT (OpenAI API)",
                IsChecked = currentBackend == "ChatGPT",
                IsCheckable = true
            };
            itemChatGpt.Click += (s, ev) => SwitchAiBackend("ChatGPT", "https://api.openai.com/v1");
            cm.Items.Add(itemChatGpt);

            cm.Items.Add(new Separator());

            var itemOllama = new MenuItem
            {
                Header = "Ollama (localhost:11434)",
                IsChecked = currentBackend == "Ollama",
                IsCheckable = true
            };
            itemOllama.Click += (s, ev) => SwitchAiBackend("Ollama", "http://localhost:11434");
            cm.Items.Add(itemOllama);

            var itemLmStudio = new MenuItem
            {
                Header = "LM Studio (localhost:1234)",
                IsChecked = currentBackend == "LMStudio",
                IsCheckable = true
            };
            itemLmStudio.Click += (s, ev) => SwitchAiBackend("LMStudio", "http://localhost:1234");
            cm.Items.Add(itemLmStudio);

            if (currentBackend == "Gemini" || currentBackend == "ChatGPT")
            {
                var itemChangeKey = new MenuItem
                {
                    Header = currentBackend == "Gemini" ? "🔑 Gemini API-Key ändern..." : "🔑 OpenAI API-Key ändern..."
                };
                itemChangeKey.Click += (s, ev) => PromptChangeApiKey(currentBackend);
                cm.Items.Add(itemChangeKey);
            }

            cm.Items.Add(new Separator());

            var itemCheck = new MenuItem
            {
                Header = "Verbindung jetzt prüfen"
            };
            itemCheck.Click += async (s, ev) =>
            {
                await AiChatService.Instance.CheckBackendAsync();
                ShowStatus(AiChatService.Instance.IsBackendAvailable
                    ? "KI: Online"
                    : "KI: Offline (Kein Server / ungültiger API-Key)");
                ScheduleHideStatus(3000);
            };
            cm.Items.Add(itemCheck);

            cm.IsOpen = true;
        }

        private void PromptChangeApiKey(string backendName)
        {
            var isGemini = backendName == "Gemini";
            var dialog = new Window
            {
                Title = isGemini ? "Google Gemini API-Key" : "OpenAI API-Key",
                Width = 480,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif")
            };

            var stack = new StackPanel { Margin = new Thickness(20) };

            var titleText = new TextBlock
            {
                Text = isGemini ? "✨ Google Gemini API-Schlüssel" : "🔑 OpenAI API-Schlüssel",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(titleText);

            var hintText = new TextBlock
            {
                Text = isGemini
                    ? "Gib deinen Google Gemini API-Key ein (kostenlos auf aistudio.google.com erhältlich)."
                    : "Gib deinen API-Key ein (sk-...). Falls kein Guthaben vorhanden ist, lade es unter platform.openai.com/settings/organization/billing auf.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(hintText);

            var currentKey = SettingsService.Instance.Settings.AiApiKey;
            var txtKey = new TextBox
            {
                Text = currentKey,
                Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16)
            };
            stack.Children.Add(txtKey);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnCancel = new Button
            {
                Content = "Abbrechen",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnCancel.Click += (s, e) => dialog.Close();

            var btnSave = new Button
            {
                Content = "Speichern",
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnSave.Click += async (s, e) =>
            {
                var newKey = txtKey.Text.Trim();
                if (!string.IsNullOrEmpty(newKey))
                {
                    var settings = SettingsService.Instance.GetAiSettings();
                    settings.ApiKey = newKey;
                    SettingsService.Instance.UpdateAiSettings(settings);

                    if (AiChatService.Instance.Backend is GeminiBackend geminiBackend)
                    {
                        geminiBackend.ApiKey = newKey;
                    }
                    else if (AiChatService.Instance.Backend is ChatGptBackend gpt)
                    {
                        gpt.ApiKey = newKey;
                    }

                    AiChatService.Instance.RefreshBackend();
                    await AiChatService.Instance.CheckBackendAsync();
                    ShowStatus(AiChatService.Instance.IsBackendAvailable
                        ? $"{backendName} API-Key gespeichert & verbunden!"
                        : "API-Key gespeichert (Prüfe Verbindung/Key)");
                    ScheduleHideStatus(3000);
                }
                dialog.Close();
            };

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnSave);
            stack.Children.Add(btnPanel);

            dialog.Content = stack;
            dialog.ShowDialog();
        }

        private void SwitchAiBackend(string backendType, string defaultUrl)
        {
            var aiSettings = SettingsService.Instance.GetAiSettings();
            aiSettings.BackendType = backendType;
            aiSettings.BackendUrl = defaultUrl;

            // Set default API key if not already stored
            if (backendType == "Gemini")
            {
                if (string.IsNullOrEmpty(aiSettings.ApiKey) || aiSettings.ApiKey.StartsWith("sk-"))
                {
                    aiSettings.ApiKey = GeminiBackend.DefaultApiKey;
                }
                aiSettings.SelectedModel = "gemini-3.6-flash";
            }
            else if (backendType == "ChatGPT" && string.IsNullOrEmpty(aiSettings.ApiKey))
            {
                aiSettings.ApiKey = ChatGptBackend.DefaultApiKey;
            }

            SettingsService.Instance.UpdateAiSettings(aiSettings);

            AiBackendNameText.Text = backendType switch
            {
                "WebLLM" => "Integrierte KI",
                "Gemini" => "Gemini",
                "ChatGPT" => "ChatGPT",
                _ => backendType
            };

            if (backendType == "Gemini")
            {
                AiModelNameText.Text = string.IsNullOrEmpty(aiSettings.SelectedModel) ? "gemini-3.6-flash" : aiSettings.SelectedModel;
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Visible;
            }
            else if (backendType == "ChatGPT")
            {
                AiModelNameText.Text = string.IsNullOrEmpty(aiSettings.SelectedModel) ? "gpt-4o-mini" : aiSettings.SelectedModel;
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Visible;
            }
            else if (backendType == "WebLLM")
            {
                AiModelNameText.Text = "Qwen 0.5B";
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Visible;
            }
            else
            {
                if (AiModelBadgeBorder != null) AiModelBadgeBorder.Visibility = Visibility.Collapsed;
            }

            AiChatService.Instance.RefreshBackend();

            if (backendType == "WebLLM")
            {
                UpdateAiBackendStatusUi(true);
            }
            else
            {
                Task.Run(async () =>
                {
                    await AiChatService.Instance.CheckBackendAsync();
                });
            }

            ShowStatus($"KI-Backend gewechselt zu {AiBackendNameText.Text}");
            ScheduleHideStatus(2000);
        }

        private void UpdateAiBackendStatusUi(bool isOnline)
        {
            var onlineBrush = TryFindResource("AiStatusOnlineBrush") as Brush 
                              ?? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            var offlineBrush = TryFindResource("AiStatusOfflineBrush") as Brush 
                               ?? new SolidColorBrush(Color.FromRgb(0x68, 0x68, 0x82));
            var brush = isOnline ? onlineBrush : offlineBrush;

            if (AiStatusDot != null) AiStatusDot.Fill = brush;
            if (AiSidebarStatusDot != null) AiSidebarStatusDot.Fill = brush;

            var backendName = SettingsService.Instance.Settings.AiBackendType;
            var statusText = isOnline ? "Bereit" : "Offline";
            if (BtnAiChat != null)
                BtnAiChat.ToolTip = $"Nexa KI ({backendName}: {statusText}) - Strg+Umschalt+A";
        }

        private void AiQuickPrompt_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is string prompt)
            {
                AiChatInput.Text = prompt;
                SendAiMessageAsync();
            }
        }

        private void AiChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    return;
                }

                e.Handled = true;
                SendAiMessageAsync();
            }
        }

        private void AiChatInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            AiInputPlaceholder.Visibility = string.IsNullOrEmpty(AiChatInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void BtnAiSend_Click(object sender, RoutedEventArgs e)
        {
            if (AiChatService.Instance.IsStreaming)
            {
                AiChatService.Instance.CancelStreaming();
            }
            else
            {
                SendAiMessageAsync();
            }
        }

        private async void SendAiMessageAsync()
        {
            var text = AiChatInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (AiChatService.Instance.IsStreaming) return;

            AiChatInput.Text = string.Empty;

            var isAvailable = await AiChatService.Instance.Backend.IsAvailableAsync();
            if (!isAvailable)
            {
                var backendName = AiChatService.Instance.Backend.Name;
                var backendUrl = AiChatService.Instance.Backend.BaseUrl;
                var isWebLlm = backendName == "WebLLM";
                AiChatService.Instance.Messages.Add(new ChatMessage
                {
                    Role = ChatMessageRole.User,
                    Content = text
                });
                AiChatService.Instance.Messages.Add(new ChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Content = isWebLlm
                        ? "⚠️ Das integrierte KI-Modell konnte nicht initialisiert werden.\n\n" +
                          "Bitte stelle sicher, dass eine Internetverbindung für den einmaligen Download der Modellgewichte besteht und deine Grafiktreiber WebGPU unterstützen."
                        : $"⚠️ Lokale KI nicht erreichbar\n\n" +
                          $"Es konnte keine Verbindung zu {backendName} unter {backendUrl} hergestellt werden.\n\n" +
                          $"Bitte stelle sicher, dass {backendName} auf deinem Computer gestartet ist:\n" +
                          $"• Ollama: Starte den Dienst mit 'ollama serve' oder 'ollama run llama3'\n" +
                          $"• LM Studio: Starte den Local Server in LM Studio auf Port 1234\n\n" +
                          $"Klicke oben auf das Backend-Badge ('{backendName}'), um das Backend zu wechseln."
                });
                UpdateAiBackendStatusUi(false);
                return;
            }

            UpdateAiBackendStatusUi(true);

            var aiSettings = SettingsService.Instance.GetAiSettings();
            var activeTab = _tabManager.ActiveTab;
            var allTabs = _tabManager.Tabs.Select(t => (t.Title, t.Url)).ToList();

            BrowserContextProvider.BrowserContext? context = null;
            try
            {
                context = await AiChatService.Instance.ContextProvider.CollectContextAsync(
                    aiSettings,
                    activeTab?.Url,
                    activeTab?.Title,
                    activeTab?.WebView,
                    allTabs);
            }
            catch
            {
                // Ignore context errors
            }

            await AiChatService.Instance.SendMessageAsync(text, context);
        }

        private async Task<bool> OnCodingAgentConfirmationRequired(CodingAgentService.CodingAction action)
        {
            return await Dispatcher.InvokeAsync(async () =>
            {
                if (!_isAiSidebarOpen)
                {
                    ToggleAiSidebar();
                }

                TxtConfirmationPrompt.Text = action.Description;
                AiConfirmationBar.Visibility = Visibility.Visible;

                _codingAgentConfirmTcs = new TaskCompletionSource<bool>();
                var result = await _codingAgentConfirmTcs.Task;

                AiConfirmationBar.Visibility = Visibility.Collapsed;
                return result;
            }).Task.Unwrap();
        }

        private void BtnConfirmAccept_Click(object sender, RoutedEventArgs e)
        {
            _codingAgentConfirmTcs?.TrySetResult(true);
        }

        private void BtnConfirmReject_Click(object sender, RoutedEventArgs e)
        {
            _codingAgentConfirmTcs?.TrySetResult(false);
        }

        // ── Model Selector ──────────────────────────────────────

        private static readonly (string Id, string Label, string Emoji)[] AiModels = new[]
        {
            ("SmolLM2-360M-Instruct-q4f16_1-MLC", "⚡ SmolLM2-360M (Blitzschnell)", "⚡"),
            ("Qwen2.5-0.5B-Instruct-q4f16_1-MLC", "⚖️ Qwen2.5-0.5B (Ausgewogen)", "⚖️"),
            ("SmolLM2-1.7B-Instruct-q4f16_1-MLC", "📚 SmolLM2-1.7B (Ausführlich)", "📚"),
            ("Llama-3.2-1B-Instruct-q4f16_1-MLC", "🧠 Llama-3.2-1B (Tiefgründig)", "🧠"),
        };

        private void AiModelBadge_Click(object sender, MouseButtonEventArgs e)
        {
            var backendType = SettingsService.Instance.Settings.AiBackendType;

            if (backendType == "Gemini")
            {
                var cmGemini = new ContextMenu
                {
                    PlacementTarget = sender as UIElement,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Top
                };

                var gemini = AiChatService.Instance.Backend as GeminiBackend;
                var currentModel = gemini?.SelectedModel ?? "gemini-3.6-flash";

                var geminiModels = new[]
                {
                    ("gemini-3.6-flash", "⚡ Gemini 3.6 Flash (Standard & Ultraschnell)"),
                    ("gemini-2.5-pro", "🧠 Gemini 2.5 Pro (Höchste Tiefe & Logik)"),
                    ("gemini-2.5-flash-lite", "💨 Gemini 2.5 Flash Lite (Leichtgewicht)"),
                    ("gemini-flash-latest", "🚀 Gemini Flash Latest")
                };

                foreach (var (mId, label) in geminiModels)
                {
                    var item = new MenuItem
                    {
                        Header = label,
                        IsChecked = currentModel == mId,
                        IsCheckable = true
                    };
                    var capturedId = mId;
                    item.Click += (s, ev) =>
                    {
                        if (gemini != null)
                        {
                            gemini.SelectedModel = capturedId;
                        }
                        var aiSettings = SettingsService.Instance.GetAiSettings();
                        aiSettings.SelectedModel = capturedId;
                        SettingsService.Instance.UpdateAiSettings(aiSettings);

                        AiModelNameText.Text = capturedId;
                        ShowStatus($"Gemini-Modell: {capturedId}");
                        ScheduleHideStatus(2000);
                    };
                    cmGemini.Items.Add(item);
                }

                cmGemini.IsOpen = true;
                return;
            }

            if (backendType == "ChatGPT")
            {
                var cmGpt = new ContextMenu
                {
                    PlacementTarget = sender as UIElement,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Top
                };

                var chatGpt = AiChatService.Instance.Backend as ChatGptBackend;
                var currentModel = chatGpt?.SelectedModel ?? "gpt-4o-mini";

                var gptModels = new[]
                {
                    ("gpt-4o-mini", "⚡ GPT-4o Mini (Standard, schnell & intelligent)"),
                    ("gpt-4o", "🧠 GPT-4o (Volle Leistung & Tiefe)"),
                    ("gpt-4-turbo", "🚀 GPT-4 Turbo"),
                    ("gpt-3.5-turbo", "💨 GPT-3.5 Turbo")
                };

                foreach (var (mId, label) in gptModels)
                {
                    var item = new MenuItem
                    {
                        Header = label,
                        IsChecked = currentModel == mId,
                        IsCheckable = true
                    };
                    var capturedId = mId;
                    item.Click += (s, ev) =>
                    {
                        if (chatGpt != null)
                        {
                            chatGpt.SelectedModel = capturedId;
                        }
                        var aiSettings = SettingsService.Instance.GetAiSettings();
                        aiSettings.SelectedModel = capturedId;
                        SettingsService.Instance.UpdateAiSettings(aiSettings);

                        AiModelNameText.Text = capturedId;
                        ShowStatus($"ChatGPT-Modell: {capturedId}");
                        ScheduleHideStatus(2000);
                    };
                    cmGpt.Items.Add(item);
                }

                cmGpt.IsOpen = true;
                return;
            }

            // WebLLM backend
            if (backendType != "WebLLM")
            {
                ShowStatus("Modellwahl ist für dieses Backend nicht verfügbar");
                ScheduleHideStatus(2000);
                return;
            }

            var cm = new ContextMenu
            {
                PlacementTarget = sender as UIElement,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top
            };

            var currentWebLlmModel = AiChatService.Instance.Backend.SelectedModel ?? "Qwen2.5-0.5B-Instruct-q4f16_1-MLC";

            foreach (var (id, label, emoji) in AiModels)
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsChecked = currentWebLlmModel == id,
                    IsCheckable = true
                };
                var modelId = id;
                var shortName = id.Split('-')[0] + " " + (id.Contains("360M") ? "360M" : id.Contains("0.5B") ? "0.5B" : id.Contains("1.7B") ? "1.7B" : "1B");
                item.Click += async (s, ev) =>
                {
                    AiModelNameText.Text = shortName;
                    ShowStatus($"Wechsle zu {shortName}...");

                    var backend = AiChatService.Instance.Backend as WebLlmBackend;
                    if (backend != null)
                    {
                        var success = await backend.SwitchModelAsync(modelId);
                        if (success)
                        {
                            ShowStatus($"Modell gewechselt: {shortName}");
                            ScheduleHideStatus(2000);
                        }
                        else
                        {
                            ShowStatus($"Modellwechsel fehlgeschlagen");
                            ScheduleHideStatus(3000);
                        }
                    }
                };
                cm.Items.Add(item);
            }

            cm.IsOpen = true;
        }

        // ── Smart Actions (1-Click KI Tasks) ────────────────────

        private async void AiSmartAction_Summarize(object sender, MouseButtonEventArgs e)
        {
            await ExecuteSmartActionAsync("summarize", "Fasse die aktuelle Seite zusammen...");
        }

        private async void AiSmartAction_Translate(object sender, MouseButtonEventArgs e)
        {
            await ExecuteSmartActionAsync("translate", "Übersetze den markierten Text...");
        }

        private async void AiSmartAction_ExplainCode(object sender, MouseButtonEventArgs e)
        {
            await ExecuteSmartActionAsync("explain_code", "Erkläre den markierten Code...");
        }

        private async Task ExecuteSmartActionAsync(string action, string displayText)
        {
            if (AiChatService.Instance.IsStreaming) return;

            // Ensure sidebar is open
            if (!_isAiSidebarOpen) ToggleAiSidebar();

            // Collect page/selection context
            var activeTab = _tabManager.ActiveTab;
            string content = "";

            if (activeTab?.WebView?.CoreWebView2 != null)
            {
                try
                {
                    // Try to get selected text first
                    var selection = await activeTab.WebView.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
                    if (!string.IsNullOrEmpty(selection) && selection != "\"\"" && selection != "null")
                    {
                        content = selection.Trim('"').Replace("\\n", "\n").Replace("\\t", "\t");
                    }
                    else
                    {
                        // Fall back to page text content
                        var pageText = await activeTab.WebView.CoreWebView2.ExecuteScriptAsync(
                            "document.body.innerText.substring(0, 4000)");
                        if (!string.IsNullOrEmpty(pageText) && pageText != "null")
                        {
                            content = pageText.Trim('"').Replace("\\n", "\n").Replace("\\t", "\t");
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                AiChatService.Instance.Messages.Add(new ChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Content = "⚠️ Kein Text gefunden. Bitte markiere Text auf der Seite oder navigiere zu einer Webseite."
                });
                return;
            }

            // Display the action as user message
            var userMsg = new ChatMessage
            {
                Role = ChatMessageRole.User,
                Content = displayText
            };
            AiChatService.Instance.Messages.Add(userMsg);

            // Create assistant placeholder for streaming
            var assistantMsg = new ChatMessage
            {
                Role = ChatMessageRole.Assistant,
                Content = "",
                IsStreaming = true
            };
            AiChatService.Instance.Messages.Add(assistantMsg);

            // Send as smart action through the normal chat path
            await AiChatService.Instance.SendSmartActionAsync(action, content, assistantMsg);
        }

        // ── Quick Search Overlay (Spotlight / Command Palette) ───────────────

        private string _quickSearchCategory = "all";

        private void BtnQuickSearch_Click(object sender, RoutedEventArgs e)
        {
            if (AppMenuPopup != null && AppMenuPopup.IsOpen) AppMenuPopup.IsOpen = false;
            ToggleQuickSearch();
        }

        public void ToggleQuickSearch()
        {
            if (QuickSearchOverlay.Visibility == Visibility.Visible)
            {
                CloseQuickSearch();
            }
            else
            {
                OpenQuickSearch();
            }
        }

        public void OpenQuickSearch()
        {
            // Close other flyouts if open
            if (HistoryPopup != null && HistoryPopup.IsOpen) HistoryPopup.IsOpen = false;
            if (DownloadsPopup != null && DownloadsPopup.IsOpen) DownloadsPopup.IsOpen = false;

            QuickSearchOverlay.Visibility = Visibility.Visible;
            QuickSearchInput.Text = string.Empty;
            QuickSearchPlaceholder.Visibility = Visibility.Visible;
            SetQuickSearchCategory("all");
            UpdateQuickSearchResults(string.Empty);

            QuickSearchInput.Focus();
        }

        public void CloseQuickSearch()
        {
            QuickSearchOverlay.Visibility = Visibility.Collapsed;
            // Refocus active webview or address bar
            if (_tabManager.ActiveTab?.WebView != null)
            {
                _tabManager.ActiveTab.WebView.Focus();
            }
            else
            {
                AddressBar?.Focus();
            }
        }

        private void QuickSearchBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CloseQuickSearch();
        }

        private void QuickSearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (QuickSearchPlaceholder != null)
            {
                QuickSearchPlaceholder.Visibility = string.IsNullOrEmpty(QuickSearchInput.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            UpdateQuickSearchResults(QuickSearchInput?.Text ?? string.Empty);
        }

        private void QuickSearchInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseQuickSearch();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down)
            {
                if (QuickSearchResults.Items.Count > 0)
                {
                    int next = Math.Min(QuickSearchResults.SelectedIndex + 1, QuickSearchResults.Items.Count - 1);
                    QuickSearchResults.SelectedIndex = next;
                    QuickSearchResults.ScrollIntoView(QuickSearchResults.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                if (QuickSearchResults.Items.Count > 0)
                {
                    int prev = Math.Max(QuickSearchResults.SelectedIndex - 1, 0);
                    QuickSearchResults.SelectedIndex = prev;
                    QuickSearchResults.ScrollIntoView(QuickSearchResults.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                ExecuteSelectedQuickSearchResult();
                e.Handled = true;
                return;
            }
        }

        private void QuickSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (QuickSearchResults.SelectedItem != null)
            {
                QuickSearchResults.ScrollIntoView(QuickSearchResults.SelectedItem);
            }
        }

        private void QuickSearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ExecuteSelectedQuickSearchResult();
        }

        private void QsFilter_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string category)
            {
                SetQuickSearchCategory(category);
                UpdateQuickSearchResults(QuickSearchInput.Text);
            }
        }

        private void SetQuickSearchCategory(string category)
        {
            _quickSearchCategory = category;

            var activeBg = (Brush)FindResource("AccentGlowBrush");
            var inactiveBg = (Brush)FindResource("BgElevatedBrush");
            var activeFg = (Brush)FindResource("AccentLightBrush");
            var inactiveFg = (Brush)FindResource("TextSecondaryBrush");

            // Reset all chips
            if (QsFilterAll != null) { QsFilterAll.Background = category == "all" ? activeBg : inactiveBg; QsFilterAllText.Foreground = category == "all" ? activeFg : inactiveFg; }
            if (QsFilterTabs != null) { QsFilterTabs.Background = category == "tabs" ? activeBg : inactiveBg; QsFilterTabsText.Foreground = category == "tabs" ? activeFg : inactiveFg; }
            if (QsFilterBookmarks != null) { QsFilterBookmarks.Background = category == "bookmarks" ? activeBg : inactiveBg; QsFilterBookmarksText.Foreground = category == "bookmarks" ? activeFg : inactiveFg; }
            if (QsFilterHistory != null) { QsFilterHistory.Background = category == "history" ? activeBg : inactiveBg; QsFilterHistoryText.Foreground = category == "history" ? activeFg : inactiveFg; }
            if (QsFilterActions != null) { QsFilterActions.Background = category == "actions" ? activeBg : inactiveBg; QsFilterActionsText.Foreground = category == "actions" ? activeFg : inactiveFg; }
        }

        private void ExecuteSelectedQuickSearchResult()
        {
            var item = QuickSearchResults.SelectedItem as QuickSearchResultItem;
            ExecuteQuickSearchResult(item);
        }

        private void ExecuteQuickSearchResult(QuickSearchResultItem? item)
        {
            if (item == null)
            {
                var query = QuickSearchInput.Text?.Trim();
                if (!string.IsNullOrEmpty(query))
                {
                    CloseQuickSearch();
                    if (_tabManager.ActiveTab != null)
                    {
                        NavigateTo(query);
                    }
                    else
                    {
                        _tabManager.AddTab(NavigationService.ResolveInput(query));
                    }
                }
                return;
            }

            CloseQuickSearch();

            switch (item.Category)
            {
                case QuickSearchResultCategory.Tab:
                    if (item.TargetTab != null && _tabManager.Tabs.Contains(item.TargetTab))
                    {
                        _tabManager.SetActiveTab(item.TargetTab);
                    }
                    break;

                case QuickSearchResultCategory.Bookmark:
                case QuickSearchResultCategory.History:
                case QuickSearchResultCategory.Search:
                    if (!string.IsNullOrEmpty(item.NavigationUrl))
                    {
                        if (_tabManager.ActiveTab != null)
                        {
                            NavigateTo(item.NavigationUrl);
                        }
                        else
                        {
                            _tabManager.AddTab(item.NavigationUrl);
                        }
                    }
                    break;

                case QuickSearchResultCategory.Action:
                    item.ExecuteAction?.Invoke();
                    break;
            }
        }

        private void UpdateQuickSearchResults(string query)
        {
            query = query?.Trim() ?? string.Empty;
            var list = new List<QuickSearchResultItem>();
            bool hasQuery = !string.IsNullOrEmpty(query);

            // 1. Offene Tabs
            if (_quickSearchCategory == "all" || _quickSearchCategory == "tabs")
            {
                foreach (var tab in _tabManager.Tabs)
                {
                    string title = string.IsNullOrWhiteSpace(tab.Title) ? "Neuer Tab" : tab.Title;
                    string url = tab.Url ?? string.Empty;

                    if (!hasQuery ||
                        title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        url.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new QuickSearchResultItem
                        {
                            Title = title,
                            Subtitle = url,
                            Icon = "📄",
                            Hint = tab == _tabManager.ActiveTab ? "Aktiver Tab" : "Tab wechseln",
                            Category = QuickSearchResultCategory.Tab,
                            TargetTab = tab
                        });
                    }
                }
            }

            // 2. Lesezeichen
            if (_quickSearchCategory == "all" || _quickSearchCategory == "bookmarks")
            {
                int count = 0;
                foreach (var b in BookmarkService.Instance.Bookmarks)
                {
                    string url = b.Url ?? string.Empty;
                    string title = string.IsNullOrWhiteSpace(b.Title) ? (!string.IsNullOrEmpty(url) ? url : "Lesezeichen") : b.Title;

                    if (!hasQuery ||
                        title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        url.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new QuickSearchResultItem
                        {
                            Title = title,
                            Subtitle = url,
                            Icon = "⭐",
                            Hint = "Lesezeichen",
                            Category = QuickSearchResultCategory.Bookmark,
                            NavigationUrl = url
                        });

                        count++;
                        if (!hasQuery && count >= 8) break;
                    }
                }
            }

            // 3. Browser-Aktionen / Befehle
            if (_quickSearchCategory == "all" || _quickSearchCategory == "actions")
            {
                var actions = GetBrowserQuickActions();
                foreach (var act in actions)
                {
                    if (!hasQuery ||
                        act.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        act.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        act.Hint.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(act);
                    }
                }
            }

            // 4. Verlauf
            if (_quickSearchCategory == "all" || _quickSearchCategory == "history")
            {
                int count = 0;
                foreach (var h in HistoryService.Instance.History)
                {
                    string url = h.Url ?? string.Empty;
                    string title = string.IsNullOrWhiteSpace(h.Title) ? (!string.IsNullOrEmpty(url) ? url : "Verlauf") : h.Title;

                    if (!hasQuery ||
                        title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        url.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new QuickSearchResultItem
                        {
                            Title = title,
                            Subtitle = $"{url} • {h.VisitedAt:dd.MM HH:mm}",
                            Icon = "🕐",
                            Hint = "Verlauf",
                            Category = QuickSearchResultCategory.History,
                            NavigationUrl = url
                        });

                        count++;
                        if (!hasQuery && count >= 8) break;
                        if (count >= 15) break;
                    }
                }
            }

            // 5. Fallback Web-Suche
            if (hasQuery && (_quickSearchCategory == "all" || _quickSearchCategory == "tabs"))
            {
                list.Add(new QuickSearchResultItem
                {
                    Title = $"\"{query}\" im Web suchen",
                    Subtitle = "Suche mit Standard-Suchmaschine",
                    Icon = "🌐",
                    Hint = "Enter",
                    Category = QuickSearchResultCategory.Search,
                    NavigationUrl = query
                });
            }

            QuickSearchResults.ItemsSource = list;
            QuickSearchResultCount.Text = $"{list.Count} Ergebnis{(list.Count == 1 ? "" : "se")}";

            if (list.Count > 0)
            {
                QuickSearchResults.SelectedIndex = 0;
            }
        }

        private List<QuickSearchResultItem> GetBrowserQuickActions()
        {
            return new List<QuickSearchResultItem>
            {
                new() { Title = "Neuer Tab", Subtitle = "Öffnet einen neuen Tab", Icon = "➕", Hint = "Strg+T", Category = QuickSearchResultCategory.Action, ExecuteAction = () => _tabManager.AddTab() },
                new() { Title = "Tab schließen", Subtitle = "Schließt den aktiven Tab", Icon = "✕", Hint = "Strg+W", Category = QuickSearchResultCategory.Action, ExecuteAction = () => { if (_tabManager.ActiveTab != null) _tabManager.CloseTab(_tabManager.ActiveTab); } },
                new() { Title = "Geschlossenen Tab wiederherstellen", Subtitle = "Öffnet den zuletzt geschlossenen Tab erneut", Icon = "↩️", Hint = "Strg+Umschalt+T", Category = QuickSearchResultCategory.Action, ExecuteAction = () => _tabManager.ReopenLastClosedTab() },
                new() { Title = "Tab duplizieren", Subtitle = "Erstellt eine Kopie des aktuellen Tabs", Icon = "📑", Hint = "Strg+Umschalt+D", Category = QuickSearchResultCategory.Action, ExecuteAction = () => { if (_tabManager.ActiveTab != null) _tabManager.DuplicateTab(_tabManager.ActiveTab); } },
                new() { Title = "Neues Fenster", Subtitle = "Öffnet ein neues Browser-Fenster", Icon = "🪟", Hint = "Strg+N", Category = QuickSearchResultCategory.Action, ExecuteAction = () => new MainWindow().Show() },
                new() { Title = "Neues Inkognito-Fenster", Subtitle = "Privates Surfen ohne Verlauf und Spuren", Icon = "🕶️", Hint = "Strg+Umschalt+N", Category = QuickSearchResultCategory.Action, ExecuteAction = () => new MainWindow(isIncognito: true).Show() },
                new() { Title = "Verlauf anzeigen", Subtitle = "Browser-Verlauf einsehen und durchsuchen", Icon = "🕐", Hint = "Strg+H", Category = QuickSearchResultCategory.Action, ExecuteAction = () => { HistoryPopup.IsOpen = true; } },
                new() { Title = "Downloads anzeigen", Subtitle = "Heruntergeladene Dateien anzeigen", Icon = "📥", Hint = "Strg+J", Category = QuickSearchResultCategory.Action, ExecuteAction = () => { DownloadsPopup.IsOpen = true; } },
                new() { Title = "Passwort-Tresor", Subtitle = "Gespeicherte Zugangsdaten verwalten & synchronisieren", Icon = "🔑", Hint = "about:passwords", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ShowSettingsInTab(_tabManager.ActiveTab ?? _tabManager.AddTab(), "Passwörter") },
                new() { Title = "Konto & Synchronisation", Subtitle = "Google Account verbinden und Passwörter sichern", Icon = "☁️", Hint = "about:sync", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ShowSettingsInTab(_tabManager.ActiveTab ?? _tabManager.AddTab(), "Sync") },
                new() { Title = "Einstellungen", Subtitle = "Browser-Konfiguration und Personalisierung", Icon = "⚙️", Hint = "about:settings", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ShowSettingsInTab(_tabManager.ActiveTab ?? _tabManager.AddTab()) },
                new() { Title = "KI-Assistent", Subtitle = "Nexa AI Seitenanalyse und Zusammenfassungen", Icon = "✨", Hint = "Strg+Umschalt+A", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ToggleAiSidebar() },
                new() { Title = "Browserdaten löschen", Subtitle = "Cache, Cookies und Chronik leeren", Icon = "🗑️", Hint = "Strg+Umschalt+Entf", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ClearBrowsingDataAsync() },
                new() { Title = "Vollbild umschalten", Subtitle = "Vollbildmodus aktivieren oder beenden", Icon = "⛶", Hint = "F11", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ToggleFullscreen() },
                new() { Title = "Split-Screen (Geteilter Bildschirm)", Subtitle = "Zwei Webseiten nebeneinander betrachten", Icon = "◫", Hint = "Strg+^", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ToggleSplitView() },
                new() { Title = "Seite neu laden", Subtitle = "Aktuelle Seite aktualisieren", Icon = "🔄", Hint = "F5", Category = QuickSearchResultCategory.Action, ExecuteAction = () => _tabManager.ActiveTab?.WebView.CoreWebView2?.Reload() },
                new() { Title = "Drucken", Subtitle = "Aktuelle Seite ausdrucken oder als PDF speichern", Icon = "🖨️", Hint = "Strg+P", Category = QuickSearchResultCategory.Action, ExecuteAction = () => _tabManager.ActiveTab?.WebView.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser) },
                new() { Title = "Lesezeichen hinzufügen / entfernen", Subtitle = "Aktuelle Seite zu Lesezeichen hinzufügen", Icon = "⭐", Hint = "Strg+D", Category = QuickSearchResultCategory.Action, ExecuteAction = () => ToggleCurrentBookmark() },
                new() { Title = "Zoom vergrößern", Subtitle = "Ansicht vergrößern (+10%)", Icon = "🔍+", Hint = "Strg++", Category = QuickSearchResultCategory.Action, ExecuteAction = () => AdjustZoom(0.1) },
                new() { Title = "Zoom verkleinern", Subtitle = "Ansicht verkleinern (-10%)", Icon = "🔍-", Hint = "Strg+-", Category = QuickSearchResultCategory.Action, ExecuteAction = () => AdjustZoom(-0.1) },
                new() { Title = "Zoom zurücksetzen", Subtitle = "Standardgröße wiederherstellen (100%)", Icon = "🔍", Hint = "Strg+0", Category = QuickSearchResultCategory.Action, ExecuteAction = () => AdjustZoom(0, reset: true) },
                new() { Title = "Task-Manager", Subtitle = "Browser-Ressourcen und Prozesse überwachen", Icon = "📊", Hint = "Umschalt+Esc", Category = QuickSearchResultCategory.Action, ExecuteAction = () => OpenTaskManager() }
            };
        }
    }
}
