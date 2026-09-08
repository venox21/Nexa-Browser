using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NexaInstaller.Services
{
    public class ImportedBookmark
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public static class BookmarkImporter
    {
        public static List<ImportedBookmark> DetectExistingBookmarks(out List<string> detectedBrowserNames)
        {
            var results = new List<ImportedBookmark>();
            detectedBrowserNames = new List<string>();

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. Google Chrome
            var chromePath = Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Bookmarks");
            if (File.Exists(chromePath))
            {
                var chromeItems = ExtractFromChromiumBookmarks(chromePath);
                if (chromeItems.Count > 0)
                {
                    results.AddRange(chromeItems);
                    detectedBrowserNames.Add("Google Chrome");
                }
            }

            // 2. Microsoft Edge
            var edgePath = Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Bookmarks");
            if (File.Exists(edgePath))
            {
                var edgeItems = ExtractFromChromiumBookmarks(edgePath);
                if (edgeItems.Count > 0)
                {
                    results.AddRange(edgeItems);
                    detectedBrowserNames.Add("Microsoft Edge");
                }
            }

            // 3. Brave Browser
            var bravePath = Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\User Data\Default\Bookmarks");
            if (File.Exists(bravePath))
            {
                var braveItems = ExtractFromChromiumBookmarks(bravePath);
                if (braveItems.Count > 0)
                {
                    results.AddRange(braveItems);
                    detectedBrowserNames.Add("Brave");
                }
            }

            // Deduplicate by URL
            var unique = results
                .GroupBy(b => b.Url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return unique;
        }

        private static List<ImportedBookmark> ExtractFromChromiumBookmarks(string filePath)
        {
            var items = new List<ImportedBookmark>();
            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("roots", out var roots))
                {
                    foreach (var rootProp in roots.EnumerateObject())
                    {
                        ExtractNodes(rootProp.Value, items);
                    }
                }
            }
            catch { }
            return items;
        }

        private static void ExtractNodes(JsonElement element, List<ImportedBookmark> list)
        {
            if (element.ValueKind != JsonValueKind.Object) return;

            if (element.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "url")
            {
                var name = element.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = element.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url) && (url.StartsWith("http://") || url.StartsWith("https://")))
                {
                    list.Add(new ImportedBookmark
                    {
                        Title = string.IsNullOrWhiteSpace(name) ? url : name,
                        Url = url
                    });
                }
            }

            if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    ExtractNodes(child, list);
                }
            }
        }

        public static int ImportToNexa(List<ImportedBookmark> bookmarksToImport)
        {
            if (bookmarksToImport == null || bookmarksToImport.Count == 0) return 0;

            try
            {
                var appFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Nexa");
                Directory.CreateDirectory(appFolder);
                var targetFile = Path.Combine(appFolder, "bookmarks.json");

                var existingList = new List<ImportedBookmark>();
                if (File.Exists(targetFile))
                {
                    try
                    {
                        var existingJson = File.ReadAllText(targetFile);
                        var parsed = JsonSerializer.Deserialize<List<ImportedBookmark>>(existingJson);
                        if (parsed != null) existingList = parsed;
                    }
                    catch { }
                }

                int addedCount = 0;
                var existingUrls = new HashSet<string>(
                    existingList.Select(b => b.Url.TrimEnd('/')),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var item in bookmarksToImport)
                {
                    if (existingUrls.Add(item.Url.TrimEnd('/')))
                    {
                        existingList.Add(item);
                        addedCount++;
                    }
                }

                if (addedCount > 0)
                {
                    var serialized = JsonSerializer.Serialize(existingList, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(targetFile, serialized);
                }

                return addedCount;
            }
            catch
            {
                return 0;
            }
        }
    }
}
