using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Browser.Services
{
    public class ColorTheme
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Color Primary { get; set; }
        public Color Light { get; set; }
        public Color Dim { get; set; }
        public Color Glow { get; set; }
        public bool IsOled { get; set; } = false;

        public Brush PrimaryBrush => new SolidColorBrush(Primary);
    }

    /// <summary>
    /// Manages accent color themes for Nexa with instant live updating.
    /// </summary>
    public class ThemeService
    {
        private static readonly Lazy<ThemeService> _lazyInstance = new(() => new ThemeService());
        public static ThemeService Instance => _lazyInstance.Value;

        public static readonly List<ColorTheme> Themes = new()
        {
            new ColorTheme
            {
                Id = "Indigo",
                Name = "Nexa Violett",
                Primary = Color.FromRgb(0x63, 0x66, 0xF1),
                Light = Color.FromRgb(0x81, 0x8C, 0xF8),
                Dim = Color.FromRgb(0x4F, 0x46, 0xE5),
                Glow = Color.FromArgb(0x33, 0x63, 0x66, 0xF1)
            },
            new ColorTheme
            {
                Id = "Blue",
                Name = "Electric Cyan",
                Primary = Color.FromRgb(0x06, 0xB6, 0xD4),
                Light = Color.FromRgb(0x38, 0xBD, 0xF8),
                Dim = Color.FromRgb(0x02, 0x84, 0xC7),
                Glow = Color.FromArgb(0x33, 0x06, 0xB6, 0xD4)
            },
            new ColorTheme
            {
                Id = "Emerald",
                Name = "Cyberpunk Emerald",
                Primary = Color.FromRgb(0x10, 0xB9, 0x81),
                Light = Color.FromRgb(0x34, 0xD3, 0x99),
                Dim = Color.FromRgb(0x05, 0x96, 0x69),
                Glow = Color.FromArgb(0x33, 0x10, 0xB9, 0x81)
            },
            new ColorTheme
            {
                Id = "Amber",
                Name = "Solar Gold",
                Primary = Color.FromRgb(0xF5, 0x9E, 0x0B),
                Light = Color.FromRgb(0xFB, 0xBF, 0x24),
                Dim = Color.FromRgb(0xD9, 0x77, 0x06),
                Glow = Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B)
            },
            new ColorTheme
            {
                Id = "Rose",
                Name = "Sunset Rose",
                Primary = Color.FromRgb(0xEC, 0x48, 0x99),
                Light = Color.FromRgb(0xF4, 0x72, 0xB6),
                Dim = Color.FromRgb(0xDB, 0x27, 0x77),
                Glow = Color.FromArgb(0x33, 0xEC, 0x48, 0x99)
            },
            new ColorTheme
            {
                Id = "OLED",
                Name = "OLED Pure Black",
                Primary = Color.FromRgb(0xE2, 0xE8, 0xF0),
                Light = Color.FromRgb(0xF8, 0xFA, 0xFC),
                Dim = Color.FromRgb(0x94, 0xA3, 0xB8),
                Glow = Color.FromArgb(0x40, 0x38, 0xBD, 0xF8),
                IsOled = true
            }
        };

        public event Action<ColorTheme>? ThemeChanged;

        public ColorTheme CurrentTheme { get; private set; } = Themes[0];

        public void ApplySavedTheme()
        {
            var savedId = SettingsService.Instance.Settings.Theme;
            ApplyTheme(savedId, saveSetting: false);
        }

        public void ApplyTheme(string themeId, bool saveSetting = true)
        {
            var theme = Themes.FirstOrDefault(t => t.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
            CurrentTheme = theme;

            if (Application.Current != null)
            {
                UpdateColorResource("AccentPrimary", theme.Primary);
                UpdateColorResource("AccentLight", theme.Light);
                UpdateColorResource("AccentDim", theme.Dim);
                UpdateColorResource("AccentGlow", theme.Glow);
                UpdateColorResource("BorderAccent", theme.Primary);

                UpdateBrushResource("AccentPrimaryBrush", theme.Primary);
                UpdateBrushResource("AccentBrush", theme.Primary);
                UpdateBrushResource("AccentLightBrush", theme.Light);
                UpdateBrushResource("AccentDimBrush", theme.Dim);
                UpdateBrushResource("AccentGlowBrush", theme.Glow);

                // Handle OLED Pitch-Black vs Normal Dark Palettes
                if (theme.IsOled)
                {
                    UpdateColorResource("BgDeepest", Color.FromRgb(0x00, 0x00, 0x00));
                    UpdateColorResource("BgDeep", Color.FromRgb(0x03, 0x03, 0x03));
                    UpdateColorResource("BgBase", Color.FromRgb(0x07, 0x07, 0x07));
                    UpdateColorResource("BgSurface", Color.FromRgb(0x10, 0x10, 0x10));
                    UpdateColorResource("BgElevated", Color.FromRgb(0x18, 0x18, 0x18));

                    UpdateBrushResource("BgDeepestBrush", Color.FromRgb(0x00, 0x00, 0x00));
                    UpdateBrushResource("BgDeepBrush", Color.FromRgb(0x03, 0x03, 0x03));
                    UpdateBrushResource("BgBaseBrush", Color.FromRgb(0x07, 0x07, 0x07));
                    UpdateBrushResource("BgSurfaceBrush", Color.FromRgb(0x10, 0x10, 0x10));
                    UpdateBrushResource("BgElevatedBrush", Color.FromRgb(0x18, 0x18, 0x18));
                }
                else
                {
                    UpdateColorResource("BgDeepest", Color.FromRgb(0x08, 0x08, 0x12));
                    UpdateColorResource("BgDeep", Color.FromRgb(0x0C, 0x0C, 0x18));
                    UpdateColorResource("BgBase", Color.FromRgb(0x11, 0x11, 0x22));
                    UpdateColorResource("BgSurface", Color.FromRgb(0x17, 0x17, 0x2C));
                    UpdateColorResource("BgElevated", Color.FromRgb(0x1F, 0x1F, 0x38));

                    UpdateBrushResource("BgDeepestBrush", Color.FromRgb(0x08, 0x08, 0x12));
                    UpdateBrushResource("BgDeepBrush", Color.FromRgb(0x0C, 0x0C, 0x18));
                    UpdateBrushResource("BgBaseBrush", Color.FromRgb(0x11, 0x11, 0x22));
                    UpdateBrushResource("BgSurfaceBrush", Color.FromRgb(0x17, 0x17, 0x2C));
                    UpdateBrushResource("BgElevatedBrush", Color.FromRgb(0x1F, 0x1F, 0x38));
                }
            }

            if (saveSetting)
            {
                SettingsService.Instance.Settings.Theme = theme.Id;
                SettingsService.Instance.Save();
            }

            ThemeChanged?.Invoke(theme);
        }

        private static void UpdateColorResource(string key, Color color)
        {
            try
            {
                Application.Current.Resources[key] = color;
            }
            catch { }
        }

        private static void UpdateBrushResource(string key, Color color)
        {
            try
            {
                if (Application.Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
                {
                    brush.Color = color;
                }
                else
                {
                    Application.Current.Resources[key] = new SolidColorBrush(color);
                }
            }
            catch { }
        }
    }
}
