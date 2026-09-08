using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Browser.Services
{
    /// <summary>
    /// Imports bookmarks and data from standard browser export files.
    /// </summary>
    public static class ImportService
    {
        private static readonly Regex BookmarkRegex = new(
            @"<a\s+(?:[^>]*?\s+)?href=[""']([^""']+)[""'][^>]*>([^<]*)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static int ImportBookmarksFromHtml(string filePath)
        {
            if (!File.Exists(filePath)) return 0;

            var html = File.ReadAllText(filePath);
            var matches = BookmarkRegex.Matches(html);
            int count = 0;

            foreach (Match match in matches)
            {
                var url = match.Groups[1].Value.Trim();
                var title = match.Groups[2].Value.Trim();

                if (!string.IsNullOrEmpty(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!BookmarkService.Instance.IsBookmarked(url))
                    {
                        BookmarkService.Instance.ToggleBookmark(url, string.IsNullOrEmpty(title) ? url : title);
                        count++;
                    }
                }
            }

            return count;
        }

        public static int PromptAndImportBookmarks()
        {
            var ofd = new OpenFileDialog
            {
                Title = "Lesezeichen-HTML-Datei importieren (Chrome / Edge / Firefox)",
                Filter = "HTML-Lesezeichendatei (*.html;*.htm)|*.html;*.htm|Alle Dateien (*.*)|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                return ImportBookmarksFromHtml(ofd.FileName);
            }
            return 0;
        }
    }
}
