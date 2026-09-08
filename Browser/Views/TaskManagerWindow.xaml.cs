using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Browser.Models;
using Browser.Services;

namespace Browser.Views
{
    public class TaskManagerItem
    {
        public BrowserTab Tab { get; set; } = null!;
        public string Title => Tab.Title;
        public string Url => Tab.Url;
        public string Status => Tab.IsSleeping ? "💤 Ruhezustand (RAM frei)" : (Tab.IsActive ? "Aktiv" : (Tab.IsPinned ? "Angeheftet" : "Hintergrund"));
    }

    public partial class TaskManagerWindow : Window
    {
        private readonly TabManager _tabManager;
        private readonly ObservableCollection<TaskManagerItem> _items = new();

        public TaskManagerWindow(TabManager tabManager)
        {
            _tabManager = tabManager;
            InitializeComponent();
            TasksDataGrid.ItemsSource = _items;
            RefreshItems();
        }

        private void RefreshItems()
        {
            _items.Clear();
            foreach (var tab in _tabManager.Tabs)
            {
                _items.Add(new TaskManagerItem { Tab = tab });
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshItems();
        }

        private void BtnEndTab_Click(object sender, RoutedEventArgs e)
        {
            if (TasksDataGrid.SelectedItem is TaskManagerItem item)
            {
                _tabManager.CloseTab(item.Tab, force: true);
                RefreshItems();
            }
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
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
