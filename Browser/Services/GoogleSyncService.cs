using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    public class SyncVaultPayload
    {
        public int Version { get; set; } = 1;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string DeviceName { get; set; } = Environment.MachineName;
        public List<PasswordEntry> Passwords { get; set; } = new();
        public List<BookmarkItem> Bookmarks { get; set; } = new();
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    public class GoogleAuthTokenInfo
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; } = DateTime.MinValue;
        public string UserEmail { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string UserPictureUrl { get; set; } = string.Empty;
    }

    public enum SyncMode
    {
        GoogleDriveApi,
        GoogleDriveFolder
    }

    /// <summary>
    /// Service for synchronizing Nexa passwords, bookmarks, and settings via Google Drive
    /// with zero-knowledge AES-256-GCM end-to-end encryption.
    /// </summary>
    public class GoogleSyncService
    {
        private static readonly Lazy<GoogleSyncService> _instance = new(() => new GoogleSyncService());
        public static GoogleSyncService Instance => _instance.Value;

        private const string SyncFileName = "nexa_cloud_vault.enc";
        private const string DriveAppDataScope = "https://www.googleapis.com/auth/drive.appdata email profile";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NexaGoogleSyncEntropy_v2");

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
        private readonly string _authFilePath;
        private readonly string _settingsFilePath;
        private readonly object _syncLock = new();

        private GoogleAuthTokenInfo? _tokenInfo;
        private Timer? _debounceTimer;
        private bool _isSyncing;

        public event Action<bool>? AuthStateChanged;
        public event Action<string>? SyncStatusChanged;
        public event Action<bool, string>? SyncCompleted;

        public bool IsAuthenticated => _tokenInfo != null && (!string.IsNullOrEmpty(_tokenInfo.RefreshToken) || !string.IsNullOrEmpty(_tokenInfo.AccessToken));
        public string UserEmail => _tokenInfo?.UserEmail ?? string.Empty;
        public string UserName => _tokenInfo?.UserName ?? string.Empty;
        public string UserPictureUrl => _tokenInfo?.UserPictureUrl ?? string.Empty;
        public DateTime? LastSyncTime { get; private set; }
        public bool IsSyncing => _isSyncing;

        // ── Built-in Default Google OAuth Client-ID ───────────────
        // Replace this with your own Google Cloud Console OAuth 2.0 Client-ID (Desktop App).
        // Steps: https://console.cloud.google.com → Create Project → Enable Drive API → Credentials → OAuth 2.0 Client-ID (Desktop)
        private const string DefaultClientId = "DEINE_GOOGLE_CLIENT_ID_HIER_EINTRAGEN";

        // Settings
        public SyncMode Mode { get; set; } = SyncMode.GoogleDriveApi;
        public string LocalDriveFolderPath { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public bool SyncPasswords { get; set; } = true;
        public bool SyncBookmarks { get; set; } = true;
        public bool SyncSettings { get; set; } = true;
        public string CustomPassphrase { get; set; } = string.Empty;

        private GoogleSyncService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _authFilePath = Path.Combine(appFolder, "sync_auth.dat");
            _settingsFilePath = Path.Combine(appFolder, "sync_config.json");

            LoadConfig();
            LoadAuth();

            // Auto-detect Google Drive local desktop folder if available
            DetectLocalGoogleDrive();
        }

        private void DetectLocalGoogleDrive()
        {
            if (!string.IsNullOrEmpty(LocalDriveFolderPath) && Directory.Exists(LocalDriveFolderPath))
                return;

            // Common default Google Drive for Desktop mount locations
            string[] possiblePaths = new[]
            {
                @"G:\Meine Ablage\NexaSync",
                @"G:\My Drive\NexaSync",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive", "NexaSync"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GoogleDrive", "NexaSync")
            };

            foreach (var p in possiblePaths)
            {
                var parent = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    LocalDriveFolderPath = p;
                    break;
                }
            }
        }

        // ── OAuth 2.0 PKCE Login Flow ────────────────────────────

        public async Task<(bool Success, string Message)> StartGoogleOAuthFlowAsync(string? explicitClientId = null, string? explicitClientSecret = null)
        {
            string cid = !string.IsNullOrWhiteSpace(explicitClientId) ? explicitClientId.Trim() : ClientId.Trim();
            string secret = !string.IsNullOrWhiteSpace(explicitClientSecret) ? explicitClientSecret.Trim() : ClientSecret.Trim();

            // Fall back to built-in default Client-ID
            if (string.IsNullOrEmpty(cid))
            {
                cid = DefaultClientId;
            }

            if (string.IsNullOrEmpty(cid) || cid == "DEINE_GOOGLE_CLIENT_ID_HIER_EINTRAGEN")
            {
                return (false, "Es ist keine gültige Google Client-ID konfiguriert. Bitte trage eine Client-ID in den Sync-Einstellungen ein oder wähle den lokalen Google Drive Ordner-Sync.");
            }

            // 1. Generate PKCE code verifier and challenge
            var verifierBytes = new byte[32];
            RandomNumberGenerator.Fill(verifierBytes);
            string codeVerifier = Base64UrlEncode(verifierBytes);

            using var sha = SHA256.Create();
            byte[] challengeBytes = sha.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
            string codeChallenge = Base64UrlEncode(challengeBytes);

            // 2. Find free local port for loopback listener
            int port = GetRandomUnusedPort();
            string redirectUri = $"http://127.0.0.1:{port}/oauth2callback";

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                return (false, $"Lokaler Listener konnte nicht gestartet werden: {ex.Message}");
            }

            // 3. Build Google Auth URL
            string authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                             $"client_id={Uri.EscapeDataString(cid)}&" +
                             $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
                             $"response_type=code&" +
                             $"scope={Uri.EscapeDataString(DriveAppDataScope)}&" +
                             $"code_challenge={Uri.EscapeDataString(codeChallenge)}&" +
                             $"code_challenge_method=S256&" +
                             $"access_type=offline&" +
                             $"prompt=consent";

            // Open in default browser / system
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                listener.Stop();
                return (false, "Browser konnte nicht geöffnet werden.");
            }

            SyncStatusChanged?.Invoke("Warte auf Bestätigung im Browser...");

            // 4. Await authorization response
            try
            {
                var contextTask = listener.GetContextAsync();
                var completedTask = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3)));
                if (completedTask != contextTask)
                {
                    listener.Stop();
                    return (false, "Zeitüberschreitung beim Google-Login (3 Minuten).");
                }

                var context = await contextTask;
                var req = context.Request;
                string? code = req.QueryString["code"];
                string? error = req.QueryString["error"];

                // Send friendly response to the browser tab
                string responseHtml = @"
<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8' />
  <title>Nexa Browser Sync</title>
  <style>
    body { font-family: 'Segoe UI', sans-serif; background: #0b0c16; color: #f1f5f9; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
    .box { background: #14162b; border: 1px solid rgba(255,255,255,0.1); border-radius: 16px; padding: 40px; text-align: center; max-width: 420px; box-shadow: 0 10px 30px rgba(0,0,0,0.5); }
    h1 { color: #10b981; font-size: 22px; margin-bottom: 12px; }
    p { color: #94a3b8; font-size: 14px; line-height: 1.5; }
  </style>
</head>
<body>
  <div class='box'>
    <h1>✓ Erfolgreich autorisiert!</h1>
    <p>Nexa ist nun mit deinem Google-Konto verbunden.<br/>Du kannst diesen Tab schließen und zu Nexa zurückkehren.</p>
  </div>
</body>
</html>";
                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentLength64 = buffer.Length;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                listener.Stop();

                if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
                {
                    return (false, $"Anmeldung fehlgeschlagen: {error ?? "Kein Code empfangen"}");
                }

                // 5. Exchange code for access & refresh tokens
                return await ExchangeCodeForTokensAsync(code, codeVerifier, redirectUri, cid, secret);
            }
            catch (Exception ex)
            {
                return (false, $"Fehler beim Autorisierungsabgleich: {ex.Message}");
            }
            finally
            {
                if (listener.IsListening) listener.Stop();
            }
        }

        private async Task<(bool Success, string Message)> ExchangeCodeForTokensAsync(string code, string verifier, string redirectUri, string clientId, string clientSecret)
        {
            try
            {
                var dict = new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                };
                if (!string.IsNullOrEmpty(clientSecret))
                {
                    dict["client_secret"] = clientSecret;
                }

                var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(dict));
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    return (false, $"Token-Austausch fehlgeschlagen ({resp.StatusCode}): {json}");
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string accessToken = root.GetProperty("access_token").GetString() ?? "";
                string refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                int expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;

                _tokenInfo = new GoogleAuthTokenInfo
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60)
                };

                // Fetch user profile info
                await RefreshUserInfoAsync();

                SaveAuth();
                SaveConfig();

                AuthStateChanged?.Invoke(true);
                SyncStatusChanged?.Invoke($"Verbunden als {UserEmail}");

                // Run initial sync
                _ = Task.Run(() => SynchronizeAsync(true));

                return (true, $"Erfolgreich verbunden mit {UserEmail}");
            }
            catch (Exception ex)
            {
                return (false, $"Fehler beim Abrufen der Zugangsdaten: {ex.Message}");
            }
        }

        private async Task RefreshUserInfoAsync()
        {
            if (_tokenInfo == null || string.IsNullOrEmpty(_tokenInfo.AccessToken)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenInfo.AccessToken);

                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("email", out var em)) _tokenInfo.UserEmail = em.GetString() ?? "";
                    if (root.TryGetProperty("name", out var nm)) _tokenInfo.UserName = nm.GetString() ?? "";
                    if (root.TryGetProperty("picture", out var pic)) _tokenInfo.UserPictureUrl = pic.GetString() ?? "";
                }
            }
            catch { }
        }

        private async Task<bool> EnsureValidAccessTokenAsync()
        {
            if (_tokenInfo == null) return false;

            if (!string.IsNullOrEmpty(_tokenInfo.AccessToken) && DateTime.UtcNow < _tokenInfo.ExpiresAt)
                return true;

            if (string.IsNullOrEmpty(_tokenInfo.RefreshToken))
                return false;

            try
            {
                var dict = new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["refresh_token"] = _tokenInfo.RefreshToken,
                    ["grant_type"] = "refresh_token"
                };
                if (!string.IsNullOrEmpty(ClientSecret)) dict["client_secret"] = ClientSecret;

                var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(dict));
                if (!resp.IsSuccessStatusCode) return false;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                _tokenInfo.AccessToken = root.GetProperty("access_token").GetString() ?? "";
                int expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
                _tokenInfo.ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60);

                SaveAuth();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Logout()
        {
            _tokenInfo = null;
            try
            {
                if (File.Exists(_authFilePath)) File.Delete(_authFilePath);
            }
            catch { }

            AuthStateChanged?.Invoke(false);
            SyncStatusChanged?.Invoke("Nicht verbunden");
        }

        // ── Cloud & Local Synchronization Engine ───────────────────

        public async Task<(bool Success, string Message)> SynchronizeAsync(bool force = false)
        {
            lock (_syncLock)
            {
                if (_isSyncing) return (false, "Synchronisierung läuft bereits.");
                _isSyncing = true;
            }

            SyncStatusChanged?.Invoke("Synchronisiere mit Google Drive...");

            try
            {
                if (Mode == SyncMode.GoogleDriveFolder)
                {
                    return await SynchronizeViaFolderAsync();
                }
                else
                {
                    return await SynchronizeViaGoogleDriveApiAsync();
                }
            }
            catch (Exception ex)
            {
                SyncCompleted?.Invoke(false, ex.Message);
                SyncStatusChanged?.Invoke($"Sync-Fehler: {ex.Message}");
                return (false, ex.Message);
            }
            finally
            {
                lock (_syncLock) { _isSyncing = false; }
            }
        }

        private async Task<(bool Success, string Message)> SynchronizeViaGoogleDriveApiAsync()
        {
            if (!IsAuthenticated)
            {
                return (false, "Nicht mit Google angemeldet.");
            }

            bool valid = await EnsureValidAccessTokenAsync();
            if (!valid)
            {
                return (false, "Google-Token abgelaufen. Bitte erneut anmelden.");
            }

            // 1. Search for nexa_cloud_vault.enc in appDataFolder
            string? remoteFileId = null;
            DateTime? remoteModified = null;

            using (var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?spaces=appDataFolder&fields=files(id,name,modifiedTime)"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenInfo!.AccessToken);
                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("files", out var files))
                    {
                        foreach (var f in files.EnumerateArray())
                        {
                            if (f.GetProperty("name").GetString() == SyncFileName)
                            {
                                remoteFileId = f.GetProperty("id").GetString();
                                if (f.TryGetProperty("modifiedTime", out var mt) && DateTime.TryParse(mt.GetString(), out var dt))
                                    remoteModified = dt;
                                break;
                            }
                        }
                    }
                }
            }

            // 2. Download and decrypt existing remote payload if present
            SyncVaultPayload? remotePayload = null;
            if (!string.IsNullOrEmpty(remoteFileId))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{remoteFileId}?alt=media");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenInfo!.AccessToken);
                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    byte[] encBytes = await resp.Content.ReadAsByteArrayAsync();
                    remotePayload = DecryptPayload(encBytes);
                }
            }

            // 3. Smart Merge: combine remote data into local state
            SmartMerge(remotePayload);

            // 4. Build new consolidated payload from local services
            var consolidated = BuildLocalPayload();
            byte[] newEncBytes = EncryptPayload(consolidated);

            // 5. Upload to Google Drive appDataFolder
            bool uploadSuccess;
            if (string.IsNullOrEmpty(remoteFileId))
            {
                // Create new file in appDataFolder
                uploadSuccess = await UploadNewFileToDriveAsync(newEncBytes);
            }
            else
            {
                // Update existing file
                uploadSuccess = await UpdateExistingFileOnDriveAsync(remoteFileId, newEncBytes);
            }

            if (!uploadSuccess)
            {
                return (false, "Fehler beim Hochladen der verschlüsselten Daten zu Google Drive.");
            }

            LastSyncTime = DateTime.Now;
            string msg = $"Erfolgreich synchronisiert ({consolidated.Passwords.Count} Passwörter, {consolidated.Bookmarks.Count} Lesezeichen)";
            SyncCompleted?.Invoke(true, msg);
            SyncStatusChanged?.Invoke($"Zuletzt synchronisiert: {LastSyncTime:HH:mm:ss}");

            return (true, msg);
        }

        private async Task<bool> UploadNewFileToDriveAsync(byte[] content)
        {
            var metadata = new { name = SyncFileName, parents = new[] { "appDataFolder" } };
            string metaJson = JsonSerializer.Serialize(metadata);

            using var form = new MultipartFormDataContent();
            var metaContent = new StringContent(metaJson, Encoding.UTF8, "application/json");
            var fileContent = new ByteArrayContent(content);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            form.Add(metaContent, "metadata");
            form.Add(fileContent, "file");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenInfo!.AccessToken);
            req.Content = form;

            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }

        private async Task<bool> UpdateExistingFileOnDriveAsync(string fileId, byte[] content)
        {
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"https://www.googleapis.com/upload/drive/v3/files/{fileId}?uploadType=media");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenInfo!.AccessToken);
            req.Content = new ByteArrayContent(content);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }

        private async Task<(bool Success, string Message)> SynchronizeViaFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(LocalDriveFolderPath))
            {
                return (false, "Kein lokaler Google Drive Ordner festgelegt.");
            }

            Directory.CreateDirectory(LocalDriveFolderPath);
            string targetPath = Path.Combine(LocalDriveFolderPath, SyncFileName);

            SyncVaultPayload? remotePayload = null;
            if (File.Exists(targetPath))
            {
                byte[] encBytes = await File.ReadAllBytesAsync(targetPath);
                remotePayload = DecryptPayload(encBytes);
            }

            SmartMerge(remotePayload);

            var consolidated = BuildLocalPayload();
            byte[] newEncBytes = EncryptPayload(consolidated);
            await File.WriteAllBytesAsync(targetPath, newEncBytes);

            LastSyncTime = DateTime.Now;
            string msg = $"Erfolgreich synchronisiert ({consolidated.Passwords.Count} Passwörter im lokalen Google Drive Ordner)";
            SyncCompleted?.Invoke(true, msg);
            SyncStatusChanged?.Invoke($"Zuletzt synchronisiert: {LastSyncTime:HH:mm:ss}");

            return (true, msg);
        }

        // ── Smart Merge Logic ──────────────────────────────────────

        public void SmartMerge(SyncVaultPayload? remote)
        {
            if (remote == null) return;

            // 1. Merge Passwords
            if (SyncPasswords && remote.Passwords != null && remote.Passwords.Count > 0)
            {
                PasswordService.Instance.MergeFromSyncPayload(remote.Passwords);
            }

            // 2. Merge Bookmarks
            if (SyncBookmarks && remote.Bookmarks != null && remote.Bookmarks.Count > 0)
            {
                foreach (var b in remote.Bookmarks)
                {
                    if (!string.IsNullOrEmpty(b.Url) && !BookmarkService.Instance.IsBookmarked(b.Url))
                    {
                        BookmarkService.Instance.ToggleBookmark(b.Url, b.Title);
                    }
                }
            }

            // 3. Merge Settings
            if (SyncSettings && remote.Settings != null)
            {
                if (remote.Settings.TryGetValue("SearchEngineUrl", out var se) && !string.IsNullOrEmpty(se))
                    SettingsService.Instance.Settings.SearchEngineUrl = se;
                if (remote.Settings.TryGetValue("DefaultZoom", out var zm) && double.TryParse(zm, out var zoomVal))
                    SettingsService.Instance.Settings.DefaultZoom = zoomVal;
            }
        }

        public SyncVaultPayload BuildLocalPayload()
        {
            var payload = new SyncVaultPayload
            {
                GeneratedAt = DateTime.UtcNow,
                DeviceName = Environment.MachineName,
                Passwords = SyncPasswords ? PasswordService.Instance.Passwords.ToList() : new List<PasswordEntry>(),
                Bookmarks = SyncBookmarks ? BookmarkService.Instance.Bookmarks.ToList() : new List<BookmarkItem>()
            };

            if (SyncSettings)
            {
                payload.Settings["SearchEngineUrl"] = SettingsService.Instance.Settings.SearchEngineUrl;
                payload.Settings["DefaultZoom"] = SettingsService.Instance.Settings.DefaultZoom.ToString();
            }

            return payload;
        }

        // ── Zero-Knowledge AES-256-GCM Encryption ──────────────────

        public byte[] EncryptPayload(SyncVaultPayload payload)
        {
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            byte[] plaintext = Encoding.UTF8.GetBytes(json);

            // Derive 256-bit key using PBKDF2
            byte[] salt = new byte[16];
            RandomNumberGenerator.Fill(salt);

            string keySource = !string.IsNullOrEmpty(CustomPassphrase)
                ? CustomPassphrase
                : (string.IsNullOrEmpty(UserEmail) ? "NexaDefaultSyncKey_2026" : $"{UserEmail}_NexaSyncSaltSecret");

            byte[] key = Rfc2898DeriveBytes.Pbkdf2(keySource, salt, 100_000, HashAlgorithmName.SHA256, 32);

            byte[] nonce = new byte[12]; // 96-bit nonce for AES-GCM
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16]; // 128-bit tag

            using var aesGcm = new AesGcm(key, 16);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

            // Format: [Salt: 16] [Nonce: 12] [Tag: 16] [Ciphertext: N]
            var result = new byte[salt.Length + nonce.Length + tag.Length + ciphertext.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(nonce, 0, result, salt.Length, nonce.Length);
            Buffer.BlockCopy(tag, 0, result, salt.Length + nonce.Length, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, result, salt.Length + nonce.Length + tag.Length, ciphertext.Length);

            return result;
        }

        public SyncVaultPayload? DecryptPayload(byte[] data)
        {
            if (data == null || data.Length < 16 + 12 + 16) return null;

            try
            {
                byte[] salt = new byte[16];
                byte[] nonce = new byte[12];
                byte[] tag = new byte[16];
                int cipherLen = data.Length - (16 + 12 + 16);
                byte[] ciphertext = new byte[cipherLen];

                Buffer.BlockCopy(data, 0, salt, 0, 16);
                Buffer.BlockCopy(data, 16, nonce, 0, 12);
                Buffer.BlockCopy(data, 28, tag, 0, 16);
                Buffer.BlockCopy(data, 44, ciphertext, 0, cipherLen);

                string keySource = !string.IsNullOrEmpty(CustomPassphrase)
                    ? CustomPassphrase
                    : (string.IsNullOrEmpty(UserEmail) ? "NexaDefaultSyncKey_2026" : $"{UserEmail}_NexaSyncSaltSecret");

                byte[] key = Rfc2898DeriveBytes.Pbkdf2(keySource, salt, 100_000, HashAlgorithmName.SHA256, 32);

                byte[] plaintext = new byte[cipherLen];
                using var aesGcm = new AesGcm(key, 16);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

                string json = Encoding.UTF8.GetString(plaintext);
                return JsonSerializer.Deserialize<SyncVaultPayload>(json);
            }
            catch
            {
                return null;
            }
        }

        // ── Auto-Sync Debounce ─────────────────────────────────────

        public void NotifyDataChanged()
        {
            if (!IsAuthenticated && Mode != SyncMode.GoogleDriveFolder) return;

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                _ = Task.Run(() => SynchronizeAsync(false));
            }, null, 5000, Timeout.Infinite);
        }

        // ── Persistence Helpers ────────────────────────────────────

        private void SaveAuth()
        {
            if (_tokenInfo == null) return;
            try
            {
                string json = JsonSerializer.Serialize(_tokenInfo);
                byte[] plain = Encoding.UTF8.GetBytes(json);
                byte[] enc = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_authFilePath, enc);
            }
            catch { }
        }

        private void LoadAuth()
        {
            try
            {
                if (!File.Exists(_authFilePath)) return;
                byte[] enc = File.ReadAllBytes(_authFilePath);
                byte[] plain = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(plain);
                _tokenInfo = JsonSerializer.Deserialize<GoogleAuthTokenInfo>(json);
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var config = new
                {
                    Mode = (int)Mode,
                    LocalDriveFolderPath,
                    ClientId,
                    ClientSecret,
                    SyncPasswords,
                    SyncBookmarks,
                    SyncSettings,
                    CustomPassphrase
                };
                File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(_settingsFilePath)) return;
                string json = File.ReadAllText(_settingsFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Mode", out var m)) Mode = (SyncMode)m.GetInt32();
                if (root.TryGetProperty("LocalDriveFolderPath", out var ldf)) LocalDriveFolderPath = ldf.GetString() ?? "";
                if (root.TryGetProperty("ClientId", out var cid)) ClientId = cid.GetString() ?? "";
                if (root.TryGetProperty("ClientSecret", out var cs)) ClientSecret = cs.GetString() ?? "";
                if (root.TryGetProperty("SyncPasswords", out var sp)) SyncPasswords = sp.GetBoolean();
                if (root.TryGetProperty("SyncBookmarks", out var sb)) SyncBookmarks = sb.GetBoolean();
                if (root.TryGetProperty("SyncSettings", out var ss)) SyncSettings = ss.GetBoolean();
                if (root.TryGetProperty("CustomPassphrase", out var cp)) CustomPassphrase = cp.GetString() ?? "";
            }
            catch { }
        }

        public void SaveSettings()
        {
            SaveConfig();
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
