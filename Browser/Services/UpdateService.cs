using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using Browser.Resources;

namespace Browser.Services
{
    public class UpdateInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("display_version")]
        public string DisplayVersion { get; set; } = string.Empty;

        [JsonPropertyName("release_name")]
        public string ReleaseName { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("raw_setup_url")]
        public string RawSetupUrl { get; set; } = string.Empty;

        [JsonPropertyName("repository_url")]
        public string RepositoryUrl { get; set; } = string.Empty;

        [JsonPropertyName("changelog")]
        public List<string> Changelog { get; set; } = new();
    }

    public class UpdateCheckResult
    {
        public bool IsSuccess { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string ReleaseName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public List<string> Changelog { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    public class UpdateService
    {
        private static readonly Lazy<UpdateService> _lazyInstance = new(() => new UpdateService());
        public static UpdateService Instance => _lazyInstance.Value;

        private const string PrimaryVersionManifestUrl = "https://raw.githubusercontent.com/venox21/Nexa-Browser/main/version.json";
        private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/venox21/Nexa-Browser/releases/latest";

        private readonly HttpClient _httpClient;

        private UpdateService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NexaBrowser/2.0 (Windows NT 10.0; Win64; x64)");
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = BrandingConfig.BrowserVersion
            };

            try
            {
                // 1. Fetch version.json from GitHub raw content
                var json = await _httpClient.GetStringAsync(PrimaryVersionManifestUrl);
                var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (info != null && !string.IsNullOrWhiteSpace(info.Version))
                {
                    result.IsSuccess = true;
                    result.LatestVersion = info.Version;
                    result.ReleaseName = info.ReleaseName;
                    result.DownloadUrl = !string.IsNullOrEmpty(info.DownloadUrl) ? info.DownloadUrl : info.RawSetupUrl;
                    result.Changelog = info.Changelog ?? new List<string>();

                    var localVer = ParseVersion(BrandingConfig.BrowserVersion);
                    var remoteVer = ParseVersion(info.Version);

                    result.IsUpdateAvailable = remoteVer > localVer;
                    return result;
                }
            }
            catch (Exception ex)
            {
                // Fallback: If network is offline or repo is brand new
                result.ErrorMessage = ex.Message;
            }

            // Return safe result without throwing
            result.LatestVersion = BrandingConfig.BrowserVersion;
            result.IsUpdateAvailable = false;
            return result;
        }

        public async Task<string> DownloadInstallerAsync(string downloadUrl, IProgress<double>? progress = null)
        {
            var tempDir = Path.GetTempPath();
            var targetExe = Path.Combine(tempDir, "NexaSetup_v2.0.exe");

            HttpResponseMessage? response = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        response.Dispose();
                        response = null;
                    }
                }
            }
            catch
            {
                response?.Dispose();
                response = null;
            }

            if (response == null)
            {
                // Fallback to raw repository setup exe
                var fallbackUrl = "https://raw.githubusercontent.com/venox21/Nexa-Browser/main/NexaSetup.exe";
                response = await _httpClient.GetAsync(fallbackUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
            }

            using (response)
            {
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(targetExe, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[16384];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    if (totalBytes > 0 && progress != null)
                    {
                        double p = (double)totalRead / totalBytes * 100.0;
                        progress.Report(p);
                    }
                }
            }

            return targetExe;
        }

        public static void LaunchInstallerAndExit(string installerPath)
        {
            if (File.Exists(installerPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });

                Application.Current.Dispatcher.Invoke(() =>
                {
                    Application.Current.Shutdown();
                });
            }
        }

        private static Version ParseVersion(string verStr)
        {
            if (string.IsNullOrWhiteSpace(verStr)) return new Version(0, 0, 0);
            var cleaned = verStr.TrimStart('v', 'V').Trim();
            int dashIdx = cleaned.IndexOf('-');
            if (dashIdx > 0) cleaned = cleaned.Substring(0, dashIdx);
            int spaceIdx = cleaned.IndexOf(' ');
            if (spaceIdx > 0) cleaned = cleaned.Substring(0, spaceIdx);

            if (Version.TryParse(cleaned, out var parsed)) return parsed;

            var parts = cleaned.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int m1)) return new Version(m1, 0, 0);
            if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b)) return new Version(a, b, 0);

            return new Version(0, 0, 0);
        }
    }
}
