using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Browser.Services;

namespace Browser
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var logFolder = Path.Combine(appData, "Nexa");
                    Directory.CreateDirectory(logFolder);
                    var logPath = Path.Combine(logFolder, "crash.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now}] {args.Exception}\n\n");
                }
                catch { }
            };

            EnsureWebView2Loader();
            base.OnStartup(e);

            // Pre-initialize all singleton services in parallel on a background thread
            // so they're warm and ready by the time the main window needs them
            Task.Run(() =>
            {
                _ = SettingsService.Instance;
                _ = AdBlockService.Instance;
                _ = HistoryService.Instance;
                _ = BookmarkService.Instance;
                _ = PerformanceService.Instance;
                _ = MalwareProtectionService.Instance;
            });

            ThemeService.Instance.ApplySavedTheme();
        }

        private static void EnsureWebView2Loader()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var directDll = Path.Combine(baseDir, "WebView2Loader.dll");
                if (File.Exists(directDll))
                {
                    return;
                }

                // If not found alongside executable, extract embedded WebView2Loader.dll to %LocalAppData%\Nexa\bin\
                var localBin = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Nexa",
                    "bin");
                Directory.CreateDirectory(localBin);
                var targetDll = Path.Combine(localBin, "WebView2Loader.dll");

                if (!File.Exists(targetDll) || new FileInfo(targetDll).Length == 0)
                {
                    System.Windows.Resources.StreamResourceInfo? streamInfo = null;
                    try
                    {
                        streamInfo = GetResourceStream(new Uri("pack://application:,,,/Nexa;component/Assets/WebView2Loader.dll", UriKind.Absolute))
                                  ?? GetResourceStream(new Uri("pack://application:,,,/Assets/WebView2Loader.dll", UriKind.Absolute));
                    }
                    catch { }

                    if (streamInfo != null)
                    {
                        using var fileStream = File.Create(targetDll);
                        streamInfo.Stream.CopyTo(fileStream);
                    }
                }

                SetDllDirectory(localBin);
            }
            catch { }
        }
    }
}
