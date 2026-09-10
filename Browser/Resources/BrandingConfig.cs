namespace Browser.Resources
{
    /// <summary>
    /// Central branding configuration.
    /// Change the browser name, version, and defaults here.
    /// </summary>
    public static class BrandingConfig
    {
        public const string BrowserName = "Nexa";
        public const string BrowserVersion = "2.0.3";
        public const string BrowserDisplayVersion = "v2.0.3";

        /// <summary>
        /// Default search engine URL template. {0} is replaced with the search query.
        /// </summary>
        public const string DefaultSearchUrl = "https://www.google.com/search?q={0}";

        /// <summary>
        /// Default homepage URL. "about:start" loads the local start page.
        /// </summary>
        public const string DefaultHomePage = "about:start";

        /// <summary>
        /// Default download folder path.
        /// </summary>
        public static string DefaultDownloadFolder =>
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "Downloads");
    }
}
