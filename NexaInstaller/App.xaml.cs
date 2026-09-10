using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using NexaInstaller.Services;

namespace NexaInstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                bool isSilent = e.Args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                                                a.Equals("-silent", StringComparison.OrdinalIgnoreCase) ||
                                                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                                                a.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
                                                a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                                                a.Equals("/qn", StringComparison.OrdinalIgnoreCase));

                PerformUninstall(isSilent);
                Shutdown(0);
                return;
            }

            // Winget / Silent install support
            if (e.Args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("-silent", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("/qn", StringComparison.OrdinalIgnoreCase) ||
                                a.Equals("/passive", StringComparison.OrdinalIgnoreCase)))
            {
                int exitCode = PerformSilentInstall(e.Args);
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);
        }

        private static int PerformSilentInstall(string[] args)
        {
            try
            {
                // Parse custom install path if provided: /dir="..." or /installpath="..." or /D="..."
                string targetDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Nexa Browser");

                foreach (var arg in args)
                {
                    if (arg.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        targetDir = arg.Substring(5).Trim('\"', '\'');
                    }
                    else if (arg.StartsWith("/installpath=", StringComparison.OrdinalIgnoreCase))
                    {
                        targetDir = arg.Substring(13).Trim('\"', '\'');
                    }
                    else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                    {
                        targetDir = arg.Substring(3).Trim('\"', '\'');
                    }
                }

                bool noShortcuts = args.Any(a => a.Equals("/no-shortcuts", StringComparison.OrdinalIgnoreCase) ||
                                                 a.Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase) ||
                                                 a.Equals("/noshortcuts", StringComparison.OrdinalIgnoreCase));

                bool noImport = args.Any(a => a.Equals("/no-import", StringComparison.OrdinalIgnoreCase) ||
                                             a.Equals("--no-import", StringComparison.OrdinalIgnoreCase) ||
                                             a.Equals("/noimport", StringComparison.OrdinalIgnoreCase));

                // 1. Check Disk Space
                if (!SystemRequirementService.HasEnoughDiskSpace(targetDir))
                {
                    Console.Error.WriteLine("Error: Insufficient disk space (min 250 MB required).");
                    return 2;
                }

                // 2. Ensure WebView2 Runtime
                if (!SystemRequirementService.IsWebView2Installed())
                {
                    SystemRequirementService.EnsureWebView2RuntimeAsync().GetAwaiter().GetResult();
                }

                // 3. Close running processes
                NexaInstaller.MainWindow.CloseRunningNexaProcesses();

                // 4. Extract embedded app.zip
                Directory.CreateDirectory(targetDir);
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("NexaInstaller.Resources.app.zip"))
                {
                    if (stream == null)
                    {
                        Console.Error.WriteLine("Error: Embedded resource app.zip not found.");
                        return 1;
                    }

                    using var archive = new ZipArchive(stream);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var destPath = Path.Combine(targetDir, entry.FullName);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }

                var installedExePath = Path.Combine(targetDir, "Nexa.exe");

                // 3. Self-copy setup executable for uninstallation
                try
                {
                    var currentExe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                    {
                        var destSetup = Path.Combine(targetDir, "NexaSetup.exe");
                        File.Copy(currentExe, destSetup, overwrite: true);
                    }
                }
                catch { }

                var iconPath = Path.Combine(targetDir, "Assets", "nexa.ico");
                if (!File.Exists(iconPath)) iconPath = Path.Combine(targetDir, "nexa.ico");

                // 4. Shortcuts
                if (!noShortcuts)
                {
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var lnkDesktop = Path.Combine(desktop, "Nexa Browser.lnk");
                    NexaInstaller.MainWindow.CreateWindowsShortcut(lnkDesktop, installedExePath, targetDir, iconPath, "Nexa Browser");

                    var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                    Directory.CreateDirectory(startMenu);
                    var lnkStartMenu = Path.Combine(startMenu, "Nexa Browser.lnk");
                    NexaInstaller.MainWindow.CreateWindowsShortcut(lnkStartMenu, installedExePath, targetDir, iconPath, "Nexa Browser");
                }

                // 5. Windows Registry
                NexaInstaller.MainWindow.RegisterInWindowsSystem(targetDir, iconPath, installedExePath);
                NexaInstaller.MainWindow.RegisterDefaultProtocols(installedExePath);

                // 6. 1-Click Bookmark Import
                if (!noImport)
                {
                    try
                    {
                        var bookmarks = BookmarkImporter.DetectExistingBookmarks(out _);
                        if (bookmarks.Count > 0)
                        {
                            BookmarkImporter.ImportToNexa(bookmarks);
                        }
                    }
                    catch { }
                }

                // 7. Auto-Launch if requested
                if (args.Any(a => a.Equals("/launch", StringComparison.OrdinalIgnoreCase) ||
                                 a.Equals("--launch", StringComparison.OrdinalIgnoreCase) ||
                                 a.Equals("-launch", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = installedExePath,
                            WorkingDirectory = targetDir,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }

                return 0; // Success
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Silent install error: " + ex.Message);
                return 1;
            }
        }

        private static void PerformUninstall(bool isSilent)
        {
            if (!isSilent)
            {
                var result = MessageBox.Show(
                    "Möchtest du Nexa Browser wirklich von deinem Computer deinstallieren?",
                    "Nexa Browser Deinstallation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            try
            {
                // 1. Close any running Nexa instances
                try
                {
                    foreach (var proc in Process.GetProcessesByName("Nexa"))
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(1500))
                        {
                            proc.Kill();
                        }
                    }
                }
                catch { }

                // 2. Remove Shortcuts
                var desktopLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "Nexa Browser.lnk");
                if (File.Exists(desktopLnk))
                {
                    try { File.Delete(desktopLnk); } catch { }
                }

                var startMenuLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs",
                    "Nexa Browser.lnk");
                if (File.Exists(startMenuLnk))
                {
                    try { File.Delete(startMenuLnk); } catch { }
                }

                // 3. Remove Registry Entries
                // Apps & Features (Uninstall key)
                using (var uninstKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
                {
                    uninstKey?.DeleteSubKeyTree("NexaBrowser", false);
                }

                // StartMenuInternet (Default browser)
                using (var smiKey = Registry.CurrentUser.OpenSubKey(@"Software\Clients\StartMenuInternet", true))
                {
                    smiKey?.DeleteSubKeyTree("NexaBrowser", false);
                }

                // RegisteredApplications
                using (var regAppKey = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                {
                    regAppKey?.DeleteValue("NexaBrowser", false);
                }

                // ProgID
                using (var classesKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true))
                {
                    classesKey?.DeleteSubKeyTree("NexaHTML", false);
                }

                // 4. Remove installation files
                var installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Nexa Browser");

                if (Directory.Exists(installDir))
                {
                    // Execute self-deleting command via cmd
                    var cmd = $"/c timeout /t 1 & rmdir /s /q \"{installDir}\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = cmd,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    });
                }

                if (!isSilent)
                {
                    MessageBox.Show(
                        "Nexa Browser wurde erfolgreich von deinem PC entfernt.",
                        "Deinstallation abgeschlossen",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (!isSilent)
                {
                    MessageBox.Show(
                        "Fehler bei der Deinstallation: " + ex.Message,
                        "Fehler",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
    }
}
