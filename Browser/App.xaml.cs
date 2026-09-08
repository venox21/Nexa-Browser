using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Browser.Services;

namespace Browser
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static Mutex? _instanceMutex;
        private static CancellationTokenSource? _pipeCts;
        private const string MutexName = "NexaBrowser_SingleInstance_Mutex_Fabian";
        private const string PipeName = "NexaBrowser_SingleInstance_Pipe_Fabian";

        public static string? InitialCommandLineUrl { get; private set; }

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

            // 1. Single-Instance & URL check
            var requestedUrl = ExtractUrlFromArgs(e.Args) ?? ExtractUrlFromArgs(Environment.GetCommandLineArgs());

            bool isFirstInstance;
            _instanceMutex = new Mutex(true, MutexName, out isFirstInstance);

            if (!isFirstInstance)
            {
                // Another instance is already running: forward URL and exit immediately
                SendUrlToRunningInstance(requestedUrl ?? "ACTIVATE");
                Environment.Exit(0);
                return;
            }

            InitialCommandLineUrl = requestedUrl;

            // Start background pipe listener for external URLs opened while running
            StartNamedPipeListener();

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

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeCts?.Cancel();
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        private static void StartNamedPipeListener()
        {
            _pipeCts = new CancellationTokenSource();
            var token = _pipeCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var message = await reader.ReadLineAsync(token);

                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            var trimmed = message.Trim();
                            Current?.Dispatcher?.Invoke(() =>
                            {
                                if (Current.MainWindow is MainWindow mw)
                                {
                                    mw.HandleExternalUrl(trimmed);
                                }
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        await Task.Delay(200, token);
                    }
                }
            }, token);
        }

        private static void SendUrlToRunningInstance(string message)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1200);

                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.WriteLine(message);
                writer.Flush();
            }
            catch { }
        }

        public static string? ExtractUrlFromArgs(string[]? args)
        {
            if (args == null || args.Length == 0) return null;

            foreach (var rawArg in args)
            {
                if (string.IsNullOrWhiteSpace(rawArg)) continue;
                var arg = rawArg.Trim('"', '\'', ' ');

                // Ignore executable name itself
                if (arg.EndsWith("Nexa.exe", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith("Nexa.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Ignore flags
                if (arg.StartsWith("-") || arg.StartsWith("/"))
                {
                    continue;
                }

                // Check local file
                if (File.Exists(arg))
                {
                    try
                    {
                        return new Uri(Path.GetFullPath(arg)).AbsoluteUri;
                    }
                    catch { }
                }

                // Check URI
                if (Uri.TryCreate(arg, UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme == Uri.UriSchemeHttp ||
                        uri.Scheme == Uri.UriSchemeHttps ||
                        uri.Scheme == Uri.UriSchemeFile ||
                        uri.Scheme.Equals("nexa", StringComparison.OrdinalIgnoreCase))
                    {
                        return arg;
                    }
                }

                // Domain without protocol
                if (arg.Contains(".") && !arg.Contains(" ") && !arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return "https://" + arg;
                }
            }

            return null;
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
