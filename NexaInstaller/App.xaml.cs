using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

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
                                                a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

                PerformUninstall(isSilent);
                Shutdown();
                return;
            }

            base.OnStartup(e);
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
