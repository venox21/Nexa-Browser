using System.IO;
using System.Text.Json;
using Browser.Models;
using Browser.Resources;

namespace Browser.Services
{
    /// <summary>
    /// User preferences and configuration service.
    /// Persists settings to %LocalAppData%/[BROWSERNAME]/settings.json.
    /// </summary>
    public class SettingsService
    {
        private static readonly Lazy<SettingsService> _lazyInstance = new(() => new SettingsService());
        public static SettingsService Instance => _lazyInstance.Value;

        private readonly string _settingsFilePath;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public class SettingsModel
        {
            // 1. Darstellung
            public double DefaultZoom { get; set; } = 1.0;
            public bool EnableAnimations { get; set; } = true;

            // 2. Suche
            public string SearchEngineUrl { get; set; } = BrandingConfig.DefaultSearchUrl;
            public bool EnableSearchSuggestions { get; set; } = true;

            // 3. Tabs & Leistung
            public bool OpenNewTabBesideActive { get; set; } = true;
            public bool EnableTabGroups { get; set; } = true;
            public bool EnableSleepingTabs { get; set; } = true;
            public int SleepingTimeoutMinutes { get; set; } = 15;
            public bool NeverSleepPinnedTabs { get; set; } = true;

            // 4. Start
            public int StartupMode { get; set; } = 0; // 0: Startseite, 1: Letzte Session, 2: Eigene URL
            public string HomePage { get; set; } = BrandingConfig.DefaultHomePage;
            public string NewTabPage { get; set; } = "about:start";
            public bool RestoreTabsOnStartup { get; set; } = false;

            // 5. Datenschutz
            public bool ClearDataOnExit { get; set; } = false;
            public bool EnableAdBlocker { get; set; } = true;
            public bool EnableYouTubeAdBlocker { get; set; } = true;

            // 6. Downloads
            public string DownloadFolder { get; set; } = BrandingConfig.DefaultDownloadFolder;
            public bool AskDownloadLocation { get; set; } = false;

            // 7. Website-Berechtigungen (Standardverhalten)
            public int CameraPermissionDefault { get; set; } = 0; // 0: Fragen, 1: Erlauben, 2: Blockieren
            public int MicrophonePermissionDefault { get; set; } = 0;
            public int GeolocationPermissionDefault { get; set; } = 0;
            public int NotificationsPermissionDefault { get; set; } = 0;
            public int PopupsPermissionDefault { get; set; } = 0;

            // Allgemein
            public string Theme { get; set; } = "Dark";
            public bool EnableDevTools { get; set; } = true;

            // ── KI-Assistent ────────────────────────────────────
            public string AiBackendType { get; set; } = "Gemini";
            public string AiBackendUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
            public string AiSelectedModel { get; set; } = "gemini-3.7-flash";
            public string AiSystemPrompt { get; set; } = "";
            public string AiApiKey { get; set; } = "AQ.Ab8RN6KuHYazHWK-QWnsBaYV-Ia8BJUPHu28AQTcv_ic4JW5rg";
            public bool AiContextUrl { get; set; } = true;
            public bool AiContextTitle { get; set; } = true;
            public bool AiContextSelection { get; set; } = false;
            public bool AiContextPage { get; set; } = false;
            public bool AiContextTabs { get; set; } = false;
            public bool AiCodingAgentEnabled { get; set; } = false;
            public string AiCodingAgentWorkDir { get; set; } = "";
        }

        public SettingsModel Settings { get; private set; }

        public event Action<SettingsModel>? SettingsChanged;

        private SettingsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, BrandingConfig.BrowserName);
            Directory.CreateDirectory(folder);
            _settingsFilePath = Path.Combine(folder, "settings.json");

            Settings = LoadSettings();
        }

        public double DefaultZoom => Settings.DefaultZoom;
        public bool EnableAnimations => Settings.EnableAnimations;
        public string SearchEngineUrl => Settings.SearchEngineUrl;
        public bool EnableSearchSuggestions => Settings.EnableSearchSuggestions;
        public bool OpenNewTabBesideActive => Settings.OpenNewTabBesideActive;
        public bool EnableTabGroups => Settings.EnableTabGroups;
        public int StartupMode => Settings.StartupMode;
        public string HomePage => Settings.HomePage;
        public string NewTabPage => Settings.NewTabPage;
        public bool RestoreTabsOnStartup => Settings.RestoreTabsOnStartup;
        public bool ClearDataOnExit => Settings.ClearDataOnExit;
        public bool EnableAdBlocker => Settings.EnableAdBlocker;
        public string DownloadFolder => Settings.DownloadFolder;
        public bool AskDownloadLocation => Settings.AskDownloadLocation;
        public string Theme => Settings.Theme;
        public bool EnableDevTools => Settings.EnableDevTools;

        /// <summary>
        /// Saves current settings to disk and triggers SettingsChanged.
        /// </summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                File.WriteAllText(_settingsFilePath, json);
                SettingsChanged?.Invoke(Settings);
            }
            catch
            {
                // Silently ignore write failures on temporary lock
            }
        }

        /// <summary>
        /// Gets an AiSettingsModel representation of current settings.
        /// </summary>
        public AiSettingsModel GetAiSettings()
        {
            return new AiSettingsModel
            {
                BackendType = Settings.AiBackendType,
                BackendUrl = Settings.AiBackendUrl,
                SelectedModel = Settings.AiSelectedModel,
                SystemPrompt = Settings.AiSystemPrompt,
                ApiKey = Settings.AiApiKey,
                ContextUrl = Settings.AiContextUrl,
                ContextTitle = Settings.AiContextTitle,
                ContextSelection = Settings.AiContextSelection,
                ContextPage = Settings.AiContextPage,
                ContextTabs = Settings.AiContextTabs,
                CodingAgentEnabled = Settings.AiCodingAgentEnabled,
                CodingAgentWorkDir = Settings.AiCodingAgentWorkDir
            };
        }

        /// <summary>
        /// Updates AI-specific settings and saves.
        /// </summary>
        public void UpdateAiSettings(AiSettingsModel model)
        {
            Settings.AiBackendType = model.BackendType;
            Settings.AiBackendUrl = model.BackendUrl;
            Settings.AiSelectedModel = model.SelectedModel;
            Settings.AiSystemPrompt = model.SystemPrompt;
            Settings.AiApiKey = model.ApiKey;
            Settings.AiContextUrl = model.ContextUrl;
            Settings.AiContextTitle = model.ContextTitle;
            Settings.AiContextSelection = model.ContextSelection;
            Settings.AiContextPage = model.ContextPage;
            Settings.AiContextTabs = model.ContextTabs;
            Settings.AiCodingAgentEnabled = model.CodingAgentEnabled;
            Settings.AiCodingAgentWorkDir = model.CodingAgentWorkDir;
            Save();
        }

        private SettingsModel LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var model = JsonSerializer.Deserialize<SettingsModel>(json);
                    if (model != null)
                    {
                        if (string.IsNullOrWhiteSpace(model.SearchEngineUrl))
                            model.SearchEngineUrl = BrandingConfig.DefaultSearchUrl;
                        if (string.IsNullOrWhiteSpace(model.HomePage))
                            model.HomePage = BrandingConfig.DefaultHomePage;
                        if (string.IsNullOrWhiteSpace(model.DownloadFolder))
                            model.DownloadFolder = BrandingConfig.DefaultDownloadFolder;
                        if (string.IsNullOrWhiteSpace(model.AiBackendType) || model.AiBackendType == "ChatGPT")
                        {
                            model.AiBackendType = "Gemini";
                            model.AiBackendUrl = "https://generativelanguage.googleapis.com/v1beta";
                            model.AiSelectedModel = "gemini-3.7-flash";
                            model.AiApiKey = "AQ.Ab8RN6KuHYazHWK-QWnsBaYV-Ia8BJUPHu28AQTcv_ic4JW5rg";
                        }
                        if (model.AiSelectedModel == "gemini-3.6-flash" || string.IsNullOrWhiteSpace(model.AiSelectedModel))
                        {
                            model.AiSelectedModel = "gemini-3.7-flash";
                        }
                        if (string.IsNullOrWhiteSpace(model.AiBackendUrl) && model.AiBackendType == "WebLLM")
                            model.AiBackendUrl = "https://ai.nexa/runner.html";
                        if (string.IsNullOrWhiteSpace(model.AiApiKey) || model.AiApiKey.StartsWith("sk-"))
                            model.AiApiKey = "AQ.Ab8RN6KuHYazHWK-QWnsBaYV-Ia8BJUPHu28AQTcv_ic4JW5rg";
                        return model;
                    }
                }
            }
            catch
            {
                // Fall back to default
            }

            var defaultSettings = new SettingsModel();
            return defaultSettings;
        }
    }
}
