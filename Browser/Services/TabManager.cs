using System.Collections.ObjectModel;
using Browser.Models;

namespace Browser.Services
{
    /// <summary>
    /// Manages browser tabs: create, close, switch, and recently closed tabs.
    /// </summary>
    public class TabManager
    {
        private readonly Stack<(string Url, string Title)> _recentlyClosed = new();
        private const int MaxRecentlyClosed = 10;
        private readonly System.Windows.Threading.DispatcherTimer _sleepTimer;

        public TabManager()
        {
            Tabs = new ObservableCollection<BrowserTab>();
            _sleepTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _sleepTimer.Tick += OnSleepTimerTick;
            _sleepTimer.Start();
        }

        /// <summary>All open tabs.</summary>
        public ObservableCollection<BrowserTab> Tabs { get; }

        /// <summary>The currently active tab.</summary>
        public BrowserTab? ActiveTab { get; private set; }

        /// <summary>Fired when the active tab changes.</summary>
        public event Action<BrowserTab?>? ActiveTabChanged;

        /// <summary>Fired when a tab is added.</summary>
        public event Action<BrowserTab>? TabAdded;

        /// <summary>Fired when a tab is about to be removed.</summary>
        public event Action<BrowserTab>? TabRemoved;

        /// <summary>
        /// Creates a new tab and makes it active.
        /// </summary>
        public BrowserTab AddTab(string? url = null)
        {
            var tab = new BrowserTab();
            if (!string.IsNullOrEmpty(url))
            {
                tab.Url = url;
            }

            Tabs.Add(tab);
            TabAdded?.Invoke(tab);
            SetActiveTab(tab);
            return tab;
        }

        /// <summary>
        /// Closes a tab. If it was active, activates an adjacent tab.
        /// </summary>
        public void CloseTab(BrowserTab tab, bool force = false)
        {
            if (tab.IsPinned && !force) return;

            if (Tabs.Count <= 1)
            {
                // Don't close the last tab – could optionally close the window
                return;
            }

            int index = Tabs.IndexOf(tab);
            if (index < 0) return;

            // Save to recently closed
            if (!string.IsNullOrEmpty(tab.Url))
            {
                _recentlyClosed.Push((tab.Url, tab.Title));
                if (_recentlyClosed.Count > MaxRecentlyClosed)
                {
                    // Trim old entries (Stack doesn't support direct trimming, so we rebuild)
                    var items = _recentlyClosed.Take(MaxRecentlyClosed).ToArray();
                    _recentlyClosed.Clear();
                    foreach (var item in items.Reverse())
                        _recentlyClosed.Push(item);
                }
            }

            TabRemoved?.Invoke(tab);
            Tabs.RemoveAt(index);

            // If the closed tab was active, activate the nearest tab
            if (tab.IsActive && Tabs.Count > 0)
            {
                int newIndex = Math.Min(index, Tabs.Count - 1);
                SetActiveTab(Tabs[newIndex]);
            }

            // Dispose the WebView2
            tab.WebView.Dispose();
        }

        /// <summary>
        /// Sets the given tab as the active tab.
        /// </summary>
        public void SetActiveTab(BrowserTab tab)
        {
            if (ActiveTab == tab) return;

            // Deactivate previous
            if (ActiveTab != null)
            {
                ActiveTab.IsActive = false;
                ActiveTab.WebView.Visibility = System.Windows.Visibility.Collapsed;
                if (ActiveTab.CustomView != null)
                {
                    ActiveTab.CustomView.Visibility = System.Windows.Visibility.Collapsed;
                }
                ActiveTab.LastActiveTime = DateTime.UtcNow;
            }

            // If target tab is sleeping, wake it up immediately
            if (tab.IsSleeping)
            {
                WakeTab(tab);
            }

            // Activate new
            tab.IsActive = true;
            tab.LastActiveTime = DateTime.UtcNow;
            if (tab.CustomView != null)
            {
                tab.WebView.Visibility = System.Windows.Visibility.Collapsed;
                tab.CustomView.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                tab.WebView.Visibility = System.Windows.Visibility.Visible;
            }
            ActiveTab = tab;

            ActiveTabChanged?.Invoke(tab);
        }

        /// <summary>
        /// Switches to the next tab (wraps around). Used for Ctrl+Tab.
        /// </summary>
        public void SwitchToNextTab()
        {
            if (Tabs.Count <= 1 || ActiveTab == null) return;
            int index = Tabs.IndexOf(ActiveTab);
            int next = (index + 1) % Tabs.Count;
            SetActiveTab(Tabs[next]);
        }

        /// <summary>
        /// Switches to the previous tab (wraps around). Used for Ctrl+Shift+Tab.
        /// </summary>
        public void SwitchToPreviousTab()
        {
            if (Tabs.Count <= 1 || ActiveTab == null) return;
            int index = Tabs.IndexOf(ActiveTab);
            int prev = (index - 1 + Tabs.Count) % Tabs.Count;
            SetActiveTab(Tabs[prev]);
        }

        /// <summary>
        /// Reopens the most recently closed tab. Used for Ctrl+Shift+T.
        /// </summary>
        public BrowserTab? ReopenLastClosedTab()
        {
            if (_recentlyClosed.Count == 0) return null;

            var (url, _) = _recentlyClosed.Pop();
            return AddTab(url);
        }

        /// <summary>
        /// Pins a tab and moves it to the pinned section at the beginning.
        /// </summary>
        public void PinTab(BrowserTab tab)
        {
            if (tab.IsPinned) return;
            tab.IsPinned = true;

            int targetIndex = 0;
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].IsPinned && Tabs[i] != tab)
                    targetIndex = i + 1;
            }

            int currentIndex = Tabs.IndexOf(tab);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Tabs.Move(currentIndex, targetIndex);
            }
        }

        /// <summary>
        /// Unpins a tab and moves it after the pinned section.
        /// </summary>
        public void UnpinTab(BrowserTab tab)
        {
            if (!tab.IsPinned) return;
            tab.IsPinned = false;

            int targetIndex = 0;
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].IsPinned && Tabs[i] != tab)
                    targetIndex = i + 1;
            }

            int currentIndex = Tabs.IndexOf(tab);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Tabs.Move(currentIndex, targetIndex);
            }
        }

        /// <summary>
        /// Toggles pinned state of a tab.
        /// </summary>
        public void TogglePin(BrowserTab tab)
        {
            if (tab.IsPinned)
                UnpinTab(tab);
            else
                PinTab(tab);
        }

        /// <summary>
        /// Duplicates a tab with its current URL and inserts it right next to it.
        /// </summary>
        public BrowserTab DuplicateTab(BrowserTab sourceTab)
        {
            string url = sourceTab.WebView.CoreWebView2?.Source ?? sourceTab.Url;
            var newTab = new BrowserTab
            {
                Url = url,
                Title = sourceTab.Title,
                IsPinned = sourceTab.IsPinned
            };

            int index = Tabs.IndexOf(sourceTab);
            int insertIndex = index >= 0 ? index + 1 : Tabs.Count;
            Tabs.Insert(insertIndex, newTab);
            TabAdded?.Invoke(newTab);
            SetActiveTab(newTab);
            return newTab;
        }

        /// <summary>
        /// Moves a tab from one index to another in the collection.
        /// </summary>
        public void MoveTab(int fromIndex, int toIndex)
        {
            if (fromIndex >= 0 && fromIndex < Tabs.Count && toIndex >= 0 && toIndex < Tabs.Count && fromIndex != toIndex)
            {
                Tabs.Move(fromIndex, toIndex);
            }
        }

        /// <summary>
        /// Closes all tabs except the specified one (pinned tabs are also preserved).
        /// </summary>
        public void CloseOtherTabs(BrowserTab keepTab)
        {
            var toClose = Tabs.Where(t => t != keepTab && !t.IsPinned).ToList();
            foreach (var tab in toClose)
            {
                CloseTab(tab, force: true);
            }
            SetActiveTab(keepTab);
        }

        /// <summary>
        /// Closes all unpinned tabs to the right of the specified tab.
        /// </summary>
        public void CloseTabsToTheRight(BrowserTab tab)
        {
            int index = Tabs.IndexOf(tab);
            if (index < 0) return;

            var toClose = Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList();
            foreach (var t in toClose)
            {
                CloseTab(t, force: true);
            }
        }

        /// <summary>
        /// Whether there are recently closed tabs to reopen.
        /// </summary>
        public bool HasRecentlyClosed => _recentlyClosed.Count > 0;

        // ── Tab Sleeping (RAM & Resource Saver) ──────────────────

        private int _sleepTimerTickCount;

        private void OnSleepTimerTick(object? sender, EventArgs e)
        {
            var settings = SettingsService.Instance.Settings;
            if (!settings.EnableSleepingTabs || settings.SleepingTimeoutMinutes <= 0) return;

            // Dynamically scale timeout: if 5 or more tabs are open, sleep tabs after 2 minutes to aggressively save RAM
            var effectiveMinutes = Tabs.Count >= 5
                ? Math.Min(settings.SleepingTimeoutMinutes, 2)
                : settings.SleepingTimeoutMinutes;

            var cutoff = DateTime.UtcNow.AddMinutes(-effectiveMinutes);
            bool didSleep = false;

            foreach (var tab in Tabs)
            {
                if (tab == ActiveTab) continue;
                if (tab.IsSleeping) continue;
                if (tab.IsPlayingAudio) continue;
                if (tab.IsLoading) continue;
                if (tab.IsPinned && settings.NeverSleepPinnedTabs) continue;

                if (tab.LastActiveTime < cutoff)
                {
                    SleepTab(tab);
                    didSleep = true;
                }
            }

            // Periodically trim process memory (~every 60 seconds or after sleeping tabs)
            _sleepTimerTickCount++;
            if (didSleep || _sleepTimerTickCount % 4 == 0)
            {
                PerformanceService.Instance.TrimProcessMemory();
            }
        }

        /// <summary>
        /// Puts a background tab to sleep to release RAM and CPU resources.
        /// </summary>
        public async void SleepTab(BrowserTab tab)
        {
            if (tab == ActiveTab || tab.IsSleeping) return;

            tab.IsSleeping = true;
            try
            {
                if (tab.WebView.CoreWebView2 != null)
                {
                    await tab.WebView.CoreWebView2.TrySuspendAsync();
                }
            }
            catch
            {
                // CoreWebView2 suspend fallback
            }
        }

        /// <summary>
        /// Wakes up a sleeping tab immediately when focused or clicked.
        /// </summary>
        public void WakeTab(BrowserTab tab)
        {
            if (!tab.IsSleeping) return;

            tab.IsSleeping = false;
            tab.LastActiveTime = DateTime.UtcNow;
            try
            {
                tab.WebView.CoreWebView2?.Resume();
            }
            catch
            {
                // Resume fallback
            }
        }
    }
}
