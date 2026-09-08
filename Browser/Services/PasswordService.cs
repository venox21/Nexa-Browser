using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// Service for managing user passwords in a secure, DPAPI-encrypted vault.
    /// Provides Google Passwords CSV import/export, form detection, autofill, and password generator.
    /// </summary>
    public class PasswordService
    {
        private static readonly Lazy<PasswordService> _instance = new(() => new PasswordService());
        public static PasswordService Instance => _instance.Value;

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NexaBrowserVaultEntropy_v1");

        private readonly string _vaultFilePath;
        private readonly object _lock = new();

        public ObservableCollection<PasswordEntry> Passwords { get; } = new();

        public event Action? PasswordsChanged;

        private PasswordService()
        {
            var appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandingConfig.BrowserName);
            Directory.CreateDirectory(appFolder);
            _vaultFilePath = Path.Combine(appFolder, "vault.dat");

            LoadVault();
        }

        public void AddOrUpdate(PasswordEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Url)) return;

            lock (_lock)
            {
                var existing = Passwords.FirstOrDefault(p =>
                    p.Id == entry.Id ||
                    (p.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase) &&
                     p.Username.Equals(entry.Username, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    existing.Title = !string.IsNullOrWhiteSpace(entry.Title) ? entry.Title : existing.Title;
                    existing.Url = entry.Url;
                    existing.Host = entry.Host;
                    existing.Username = entry.Username;
                    existing.Password = entry.Password;
                    existing.Note = entry.Note;
                    existing.LastModified = DateTime.Now;
                }
                else
                {
                    Passwords.Add(entry);
                }

                SaveVault();
            }

            PasswordsChanged?.Invoke();
            GoogleSyncService.Instance.NotifyDataChanged();
        }

        public void Delete(PasswordEntry entry)
        {
            if (entry == null) return;

            lock (_lock)
            {
                Passwords.Remove(entry);
                SaveVault();
            }

            PasswordsChanged?.Invoke();
            GoogleSyncService.Instance.NotifyDataChanged();
        }

        public void MergeFromSyncPayload(List<PasswordEntry> remoteEntries)
        {
            if (remoteEntries == null || remoteEntries.Count == 0) return;

            bool changed = false;
            lock (_lock)
            {
                foreach (var remote in remoteEntries)
                {
                    if (string.IsNullOrWhiteSpace(remote.Url) || string.IsNullOrWhiteSpace(remote.Password)) continue;

                    var local = Passwords.FirstOrDefault(p =>
                        p.Id == remote.Id ||
                        (p.Host.Equals(remote.Host, StringComparison.OrdinalIgnoreCase) &&
                         p.Username.Equals(remote.Username, StringComparison.OrdinalIgnoreCase)));

                    if (local == null)
                    {
                        Passwords.Add(new PasswordEntry
                        {
                            Id = remote.Id,
                            Url = remote.Url,
                            Title = remote.Title,
                            Host = remote.Host,
                            Username = remote.Username,
                            Password = remote.Password,
                            Note = remote.Note,
                            CreatedAt = remote.CreatedAt,
                            LastModified = remote.LastModified,
                            BreachCount = remote.BreachCount,
                            LastSecurityCheck = remote.LastSecurityCheck
                        });
                        changed = true;
                    }
                    else if (remote.LastModified > local.LastModified)
                    {
                        local.Title = remote.Title;
                        local.Url = remote.Url;
                        local.Host = remote.Host;
                        local.Password = remote.Password;
                        local.Note = remote.Note;
                        local.LastModified = remote.LastModified;
                        local.BreachCount = remote.BreachCount;
                        local.LastSecurityCheck = remote.LastSecurityCheck;
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveVault();
                }
            }

            if (changed)
            {
                PasswordsChanged?.Invoke();
            }
        }

        public List<PasswordEntry> GetMatchingCredentials(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return new List<PasswordEntry>();

            string host = string.Empty;
            try
            {
                var clean = url.Trim();
                if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    clean = "https://" + clean;
                }
                if (Uri.TryCreate(clean, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                {
                    host = uri.Host.ToLowerInvariant();
                }
            }
            catch { }

            if (string.IsNullOrEmpty(host)) return new List<PasswordEntry>();

            lock (_lock)
            {
                return Passwords.Where(p =>
                {
                    var pHost = p.Host.ToLowerInvariant();
                    return pHost == host ||
                           host.EndsWith("." + pHost, StringComparison.OrdinalIgnoreCase) ||
                           pHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }
        }

        public int ImportFromGoogleCsv(string csvPath)
        {
            if (!File.Exists(csvPath)) return 0;

            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2) return 0;

            var headerLine = lines[0];
            var headers = ParseCsvLine(headerLine).Select(h => h.Trim().ToLowerInvariant()).ToList();

            int nameIndex = headers.FindIndex(h => h == "name" || h == "title" || h == "website");
            int urlIndex = headers.FindIndex(h => h == "url" || h == "origin");
            int userIndex = headers.FindIndex(h => h == "username" || h == "user" || h == "email" || h == "login");
            int passIndex = headers.FindIndex(h => h == "password" || h == "pass");
            int noteIndex = headers.FindIndex(h => h == "note" || h == "notes");

            if (urlIndex == -1 || passIndex == -1) return 0;

            int count = 0;
            lock (_lock)
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var fields = ParseCsvLine(line);
                    if (fields.Count <= Math.Max(urlIndex, passIndex)) continue;

                    var url = fields[urlIndex].Trim();
                    var pass = fields[passIndex];
                    var user = (userIndex >= 0 && userIndex < fields.Count) ? fields[userIndex].Trim() : string.Empty;
                    var name = (nameIndex >= 0 && nameIndex < fields.Count) ? fields[nameIndex].Trim() : string.Empty;
                    var note = (noteIndex >= 0 && noteIndex < fields.Count) ? fields[noteIndex].Trim() : string.Empty;

                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(pass)) continue;

                    var entry = new PasswordEntry
                    {
                        Url = url,
                        Title = name,
                        Username = user,
                        Password = pass,
                        Note = note,
                        CreatedAt = DateTime.Now,
                        LastModified = DateTime.Now
                    };

                    var existing = Passwords.FirstOrDefault(p =>
                        p.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase) &&
                        p.Username.Equals(entry.Username, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        existing.Password = entry.Password;
                        existing.Url = entry.Url;
                        existing.Title = !string.IsNullOrWhiteSpace(entry.Title) ? entry.Title : existing.Title;
                        existing.Note = entry.Note;
                        existing.LastModified = DateTime.Now;
                    }
                    else
                    {
                        Passwords.Add(entry);
                    }

                    count++;
                }

                SaveVault();
            }

            PasswordsChanged?.Invoke();
            return count;
        }

        public void ExportToGoogleCsv(string csvPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("name,url,username,password,note");

            lock (_lock)
            {
                foreach (var p in Passwords)
                {
                    var name = EscapeCsv(p.Title);
                    var url = EscapeCsv(p.Url);
                    var user = EscapeCsv(p.Username);
                    var pass = EscapeCsv(p.Password);
                    var note = EscapeCsv(p.Note);
                    sb.AppendLine($"{name},{url},{user},{pass},{note}");
                }
            }

            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        }

        public string GenerateStrongPassword(
            int length = 16,
            bool useUpper = true,
            bool useLower = true,
            bool useNumbers = true,
            bool useSymbols = true)
        {
            if (length < 6) length = 6;
            if (length > 64) length = 64;

            const string uppers = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string lowers = "abcdefghijkmnopqrstuvwxyz";
            const string numbers = "23456789";
            const string symbols = "!@#$%^&*()-_=+[]{}|;:,.<>?";

            var charPool = new StringBuilder();
            var mandatoryChars = new List<char>();

            if (useUpper) { charPool.Append(uppers); mandatoryChars.Add(uppers[RandomNumberGenerator.GetInt32(uppers.Length)]); }
            if (useLower) { charPool.Append(lowers); mandatoryChars.Add(lowers[RandomNumberGenerator.GetInt32(lowers.Length)]); }
            if (useNumbers) { charPool.Append(numbers); mandatoryChars.Add(numbers[RandomNumberGenerator.GetInt32(numbers.Length)]); }
            if (useSymbols) { charPool.Append(symbols); mandatoryChars.Add(symbols[RandomNumberGenerator.GetInt32(symbols.Length)]); }

            if (charPool.Length == 0) charPool.Append(lowers + numbers);

            var poolStr = charPool.ToString();
            var result = new List<char>(mandatoryChars);

            while (result.Count < length)
            {
                result.Add(poolStr[RandomNumberGenerator.GetInt32(poolStr.Length)]);
            }

            // Shuffle
            for (int i = result.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (result[i], result[j]) = (result[j], result[i]);
            }

            return new string(result.ToArray());
        }

        private static readonly System.Net.Http.HttpClient _hibpHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        /// <summary>
        /// Checks a password against HaveIBeenPwned database using the k-Anonymity model.
        /// Neither the password nor the full hash is ever sent over the network.
        /// </summary>
        public async Task<int> CheckPasswordBreachCountAsync(string password, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(password)) return 0;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(password);
                byte[] hashBytes = SHA1.HashData(bytes);
                string fullHash = Convert.ToHexString(hashBytes); // 40 hex chars, e.g. 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8

                string prefix = fullHash.Substring(0, 5);
                string suffix = fullHash.Substring(5);

                var url = $"https://api.pwnedpasswords.com/range/{prefix}";
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Nexa-Browser-Security/1.0");

                var response = await _hibpHttpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return 0;

                var body = await response.Content.ReadAsStringAsync(ct);
                using var reader = new StringReader(body);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        var returnedSuffix = line.Substring(0, colon).Trim();
                        if (string.Equals(returnedSuffix, suffix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(line.Substring(colon + 1).Trim(), out int count))
                            {
                                return count;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Network error or offline
            }

            return 0;
        }

        /// <summary>
        /// Audits all passwords in the vault against HaveIBeenPwned and saves breach statistics.
        /// </summary>
        public async Task<(int TotalAudited, int CompromisedCount)> AuditAllPasswordsAsync(Action<int, int>? progress = null, System.Threading.CancellationToken ct = default)
        {
            List<PasswordEntry> copy;
            lock (_lock)
            {
                copy = Passwords.ToList();
            }

            int compromised = 0;
            int total = copy.Count;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = copy[i];
                int count = await CheckPasswordBreachCountAsync(entry.Password, ct);
                entry.BreachCount = count;
                entry.LastSecurityCheck = DateTime.Now;

                if (count > 0) compromised++;
                progress?.Invoke(i + 1, total);
            }

            SaveVault();
            PasswordsChanged?.Invoke();
            return (total, compromised);
        }

        public static string GetFormDetectionScript()
        {
            return @"
(() => {
    if (window.__nexa_pwd_attached) return;
    window.__nexa_pwd_attached = true;

    function handleSubmission(form) {
        try {
            const pwdInput = form.querySelector('input[type=""password""]');
            if (!pwdInput || !pwdInput.value) return;

            // Find matching username input (preceding text/email/tel or with name/autocomplete user)
            const inputs = Array.from(form.querySelectorAll('input:not([type=""hidden""]):not([type=""submit""]):not([type=""button""])'));
            const pwdIndex = inputs.indexOf(pwdInput);

            let userInput = null;
            // First check autocomplete
            userInput = form.querySelector('input[autocomplete*=""username""], input[autocomplete*=""email""]');
            if (!userInput && pwdIndex > 0) {
                // Find nearest input before password
                for (let i = pwdIndex - 1; i >= 0; i--) {
                    const inp = inputs[i];
                    const type = (inp.type || 'text').toLowerCase();
                    if (type === 'text' || type === 'email' || type === 'tel') {
                        userInput = inp;
                        break;
                    }
                }
            }

            const username = userInput ? userInput.value.trim() : '';
            const password = pwdInput.value;

            if (password && password.length >= 2) {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({
                        action: 'nexa_password_submitted',
                        url: window.location.href,
                        host: window.location.hostname,
                        username: username,
                        password: password
                    });
                }
            }
        } catch(e) {}
    }

    document.addEventListener('submit', (e) => {
        if (e.target && e.target.tagName === 'FORM') {
            handleSubmission(e.target);
        }
    }, true);

    // Also catch submit buttons
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('button[type=""submit""], input[type=""submit""], button');
        if (btn && btn.form) {
            handleSubmission(btn.form);
        }
    }, true);
})();";
        }

        public static string GetAutofillScript(string username, string password)
        {
            var userJson = JsonSerializer.Serialize(username);
            var passJson = JsonSerializer.Serialize(password);

            return $@"
(() => {{
    try {{
        const userVal = {userJson};
        const passVal = {passJson};

        const pwdInputs = document.querySelectorAll('input[type=""password""]');
        if (pwdInputs.length === 0) return false;

        const pwdInput = pwdInputs[0];
        const form = pwdInput.form || document;
        const inputs = Array.from(form.querySelectorAll('input:not([type=""hidden""])'));
        const pwdIndex = inputs.indexOf(pwdInput);

        let userInput = null;
        if (pwdIndex > 0) {{
            for (let i = pwdIndex - 1; i >= 0; i--) {{
                const inp = inputs[i];
                const type = (inp.type || 'text').toLowerCase();
                if (type === 'text' || type === 'email' || type === 'tel') {{
                    userInput = inp;
                    break;
                }}
            }}
        }}

        function fireEvents(el, val) {{
            el.focus();
            el.value = val;
            el.dispatchEvent(new Event('input', {{ bubbles: true }}));
            el.dispatchEvent(new Event('change', {{ bubbles: true }}));
            el.blur();
        }}

        if (userInput && userVal) {{
            fireEvents(userInput, userVal);
        }}

        if (pwdInput && passVal) {{
            fireEvents(pwdInput, passVal);
        }}

        return true;
    }} catch(e) {{
        return false;
    }}
}})();";
        }

        private void SaveVault()
        {
            lock (_lock)
            {
                try
                {
                    var list = Passwords.ToList();
                    var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = false });
                    var plainBytes = Encoding.UTF8.GetBytes(json);
                    var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(_vaultFilePath, encryptedBytes);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PasswordVault] Save error: {ex.Message}");
                }
            }
        }

        private void LoadVault()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_vaultFilePath)) return;

                    var encryptedBytes = File.ReadAllBytes(_vaultFilePath);
                    if (encryptedBytes.Length == 0) return;

                    var plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
                    var json = Encoding.UTF8.GetString(plainBytes);
                    var list = JsonSerializer.Deserialize<List<PasswordEntry>>(json);

                    if (list != null)
                    {
                        Passwords.Clear();
                        foreach (var item in list)
                        {
                            Passwords.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PasswordVault] Load error: {ex.Message}");
                }
            }
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            result.Add(cur.ToString());
            return result;
        }

        private static string EscapeCsv(string? val)
        {
            if (string.IsNullOrEmpty(val)) return "\"\"";
            var escaped = val.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }
    }
}
