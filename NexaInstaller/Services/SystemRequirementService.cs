using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace NexaInstaller.Services
{
    /// <summary>
    /// Checks and satisfies system requirements (WebView2, disk space, Windows version, default browser settings).
    /// </summary>
    public static class SystemRequirementService
    {
        private const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>
        /// Checks if Microsoft Edge WebView2 Evergreen Runtime is installed on the system.
        /// </summary>
        public static bool IsWebView2Installed()
        {
            try
            {
                // 1. Check 64-bit / WOW6432Node in HKLM
                using (var key64 = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"))
                {
                    var pv = key64?.GetValue("pv") as string;
                    if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return true;
                }

                // 2. Check 32-bit native in HKLM
                using (var key32 = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"))
                {
                    var pv = key32?.GetValue("pv") as string;
                    if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return true;
                }

                // 3. Check current user in HKCU
                using (var keyUser = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"))
                {
                    var pv = keyUser?.GetValue("pv") as string;
                    if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemRequirementService] Error checking WebView2: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Downloads and silently installs Microsoft WebView2 Evergreen Runtime if missing.
        /// </summary>
        public static async Task<bool> EnsureWebView2RuntimeAsync(Action<string>? statusCallback = null)
        {
            if (IsWebView2Installed())
            {
                return true;
            }

            statusCallback?.Invoke("Microsoft WebView2 Runtime wird heruntergeladen...");
            var tempSetupPath = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");

            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "NexaInstaller/2.0");
                    var bytes = await client.GetByteArrayAsync(WebView2BootstrapperUrl);
                    await File.WriteAllBytesAsync(tempSetupPath, bytes);
                }

                statusCallback?.Invoke("Microsoft WebView2 Runtime wird eingerichtet...");
                var psi = new ProcessStartInfo
                {
                    FileName = tempSetupPath,
                    Arguments = "/silent /install",
                    UseShellExecute = true
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    return proc.ExitCode == 0 || IsWebView2Installed();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemRequirementService] Error installing WebView2: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempSetupPath))
                    {
                        File.Delete(tempSetupPath);
                    }
                }
                catch { }
            }

            return IsWebView2Installed();
        }

        /// <summary>
        /// Checks if the target drive has sufficient free disk space (default 250 MB).
        /// </summary>
        public static bool HasEnoughDiskSpace(string targetDir, long requiredBytes = 250L * 1024 * 1024)
        {
            try
            {
                var fullPath = Path.GetFullPath(targetDir);
                var root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        return drive.AvailableFreeSpace >= requiredBytes;
                    }
                }
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Checks if the current Windows OS version is Windows 10 (Build 17763+) or Windows 11.
        /// </summary>
        public static bool IsSupportedWindowsVersion()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT &&
                   (Environment.OSVersion.Version.Major > 10 ||
                    (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 17763));
        }

        /// <summary>
        /// Opens the native Windows 10 / 11 Default Apps settings page for Web Browsers.
        /// </summary>
        public static void OpenDefaultBrowserSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:defaultapps?category=webbrowser",
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ms-settings:defaultapps",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }
    }
}
