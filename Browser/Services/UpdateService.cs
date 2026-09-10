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

    public class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    public class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
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

        private const string PrimaryVersionManifestUrl = "https://raw.githubusercontent.com/venox21/Nexa-Browser/refs/heads/main/version.json";
        private const string GitHubApiReleasesUrl = "https://api.github.com/repos/venox21/Nexa-Browser/releases?per_page=5";

        private readonly HttpClient _httpClient;

        private UpdateService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NexaBrowser/2.0 (Windows NT 10.0; Win64; x64)");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json, application/json, text/plain, */*");
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = BrandingConfig.BrowserVersion,
                LatestVersion = BrandingConfig.BrowserVersion
            };

            var localVer = ParseVersion(BrandingConfig.BrowserVersion);
            Version highestRemoteVer = localVer;
            bool foundAnyInfo = false;

            // 1. Fetch version.json from GitHub raw content with cache-buster timestamp
            try
            {
                var cacheBustUrl = $"{PrimaryVersionManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                using var req = new HttpRequestMessage(HttpMethod.Get, cacheBustUrl);
                req.Headers.Add("Cache-Control", "no-cache");

                using var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (info != null && !string.IsNullOrWhiteSpace(info.Version))
                    {
                        foundAnyInfo = true;
                        result.ReleaseName = info.ReleaseName;
                        result.DownloadUrl = !string.IsNullOrEmpty(info.DownloadUrl) ? info.DownloadUrl : info.RawSetupUrl;
                        result.Changelog = info.Changelog ?? new List<string>();

                        var v = ParseVersion(info.Version);
                        if (v > highestRemoteVer)
                        {
                            highestRemoteVer = v;
                            result.LatestVersion = info.Version;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Error fetching version.json: {ex.Message}");
            }

            // 2. Query GitHub Releases API for releases/tags
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, GitHubApiReleasesUrl);
                using var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var releases = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (releases != null)
                    {
                        foreach (var rel in releases)
                        {
                            var ver = ExtractVersion(rel.TagName);
                            if (ver == null || ver == new Version(0, 0, 0))
                            {
                                ver = ExtractVersion(rel.Name);
                            }

                            if (ver != null && ver > highestRemoteVer)
                            {
                                foundAnyInfo = true;
                                highestRemoteVer = ver;
                                result.LatestVersion = ver.ToString();
                                result.ReleaseName = !string.IsNullOrWhiteSpace(rel.Name) ? rel.Name : $"Nexa Browser v{ver}";

                                // Search for NexaSetup.exe asset
                                var setupAsset = rel.Assets?.Find(a => a.Name.Equals("NexaSetup.exe", StringComparison.OrdinalIgnoreCase));
                                if (setupAsset != null && !string.IsNullOrEmpty(setupAsset.BrowserDownloadUrl))
                                {
                                    result.DownloadUrl = setupAsset.BrowserDownloadUrl;
                                }
                                else if (!string.IsNullOrEmpty(rel.HtmlUrl))
                                {
                                    result.DownloadUrl = rel.HtmlUrl;
                                }

                                // Extract changelog lines from release body
                                if (!string.IsNullOrWhiteSpace(rel.Body))
                                {
                                    var lines = rel.Body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(l => l.Trim().TrimStart('-', '*', '•').Trim())
                                        .Where(l => !string.IsNullOrEmpty(l) && !l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                        .Take(6)
                                        .ToList();
                                    if (lines.Count > 0)
                                    {
                                        result.Changelog = lines;
                                    }
                                }
                            }
                            else if (!foundAnyInfo && rel.Assets != null)
                            {
                                // If version is same or tag is 'Installer', record setup URL
                                var setupAsset = rel.Assets.Find(a => a.Name.Equals("NexaSetup.exe", StringComparison.OrdinalIgnoreCase));
                                if (setupAsset != null && !string.IsNullOrEmpty(setupAsset.BrowserDownloadUrl) && string.IsNullOrEmpty(result.DownloadUrl))
                                {
                                    result.DownloadUrl = setupAsset.BrowserDownloadUrl;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Error querying GitHub releases: {ex.Message}");
            }

            if (foundAnyInfo)
            {
                result.IsSuccess = true;
                result.IsUpdateAvailable = highestRemoteVer > localVer;
                if (string.IsNullOrEmpty(result.LatestVersion))
                {
                    result.LatestVersion = highestRemoteVer.ToString();
                }
            }
            else
            {
                result.IsSuccess = false;
                result.ErrorMessage = "GitHub-Repository konnte nicht erreicht werden.";
                result.LatestVersion = BrandingConfig.BrowserVersion;
                result.IsUpdateAvailable = false;
            }

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
                    Arguments = "/silent /launch",
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

        private static Version? ExtractVersion(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(input, @"\b(\d+\.\d+(?:\.\d+)?)\b");
            if (match.Success && Version.TryParse(match.Groups[1].Value, out var parsed))
            {
                return parsed;
            }
            return null;
        }
    }
}
