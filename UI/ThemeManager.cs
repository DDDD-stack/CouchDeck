using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CouchDeck.Config;

namespace CouchDeck.UI
{
    /// <summary>A named colour scheme. Picking one on the Customize page sets the three base colours.</summary>
    public sealed record ThemePreset(string Name, string Accent, string SecondaryAccent, string Background);

    /// <summary>
    /// Turns <see cref="AppearanceSettings"/> into application resources. Every brush, size and
    /// font in the XAML is a DynamicResource, so calling <see cref="Apply"/> restyles the whole
    /// overlay live, no restart needed. Values used by code (animations) are read from
    /// <see cref="Current"/>.
    /// </summary>
    public static class ThemeManager
    {
        public const double BaseTileWidth = 220;
        public const double BaseTileHeight = 330;

        /// <summary>A short list of starting points. Anything else: Customize > Custom colours (colour table).</summary>
        public static readonly IReadOnlyList<ThemePreset> Presets = new List<ThemePreset>
        {
            new("Nebula", "#7C5CFF", "#00D4FF", "#07080D"),
            new("Xbox", "#3FD26B", "#0E7A0D", "#060B07"),
            new("Cobalt", "#2F7BFF", "#00C2FF", "#040914"),
            new("Synthwave", "#FF2E97", "#FFB800", "#0D0221"),
            new("Crimson", "#FF3B4E", "#FF8A3D", "#0E0507"),
            new("Mono", "#FFFFFF", "#8A8F98", "#000000")
        };

        public static readonly IReadOnlyList<string> CandidateFonts = new List<string>
        {
            "Segoe UI Variable Display", "Segoe UI", "Bahnschrift", "Aptos", "Inter", "Montserrat", "Poppins",
            "Roboto", "Arial Rounded MT Bold", "Trebuchet MS", "Verdana", "Century Gothic", "Franklin Gothic Medium",
            "Cascadia Code", "Consolas", "Georgia", "Impact"
        };

        private static List<string>? installedFonts;

        public static AppearanceSettings Current { get; private set; } = new();

        /// <summary>1 = normal speed, 0 = animations off, 2 = twice as slow.</summary>
        public static double SpeedFactor => Math.Clamp(Current.AnimationSpeed, 0, 300) / 100.0;

        public static bool AnimationsEnabled => Current.AnimationSpeed > 0;

        /// <summary>
        /// The quality actually in effect. Auto = Balanced on a GPU with full hardware rendering
        /// (render tier 2), Performance otherwise (old/integrated GPUs, remote desktop).
        /// </summary>
        public static GraphicsQuality EffectiveQuality => Current.Quality != GraphicsQuality.Auto
            ? Current.Quality
            : (RenderCapability.Tier >> 16) >= 2 ? GraphicsQuality.Balanced : GraphicsQuality.Performance;

        /// <summary>Widest background art to decode, by quality (big textures cost upload time on every change).</summary>
        public static int MaxBackdropPixels => EffectiveQuality switch
        {
            GraphicsQuality.Quality => 3840,
            GraphicsQuality.Balanced => 2560,
            _ => 1920
        };

        public static double TileWidth => BaseTileWidth * Current.TileSize / 100.0;
        public static double TileHeight => BaseTileHeight * Current.TileSize / 100.0;

        /// <summary>Fonts from <see cref="CandidateFonts"/> that are installed, plus whatever the config names.</summary>
        public static IReadOnlyList<string> AvailableFonts
        {
            get
            {
                installedFonts ??= CandidateFonts
                    .Where(name => Fonts.SystemFontFamilies.Any(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (!installedFonts.Contains(Current.FontFamily, StringComparer.OrdinalIgnoreCase))
                {
                    return installedFonts.Append(Current.FontFamily).ToList();
                }

                return installedFonts;
            }
        }

        public static void ApplyPreset(AppearanceSettings settings, string name)
        {
            ThemePreset? preset = Presets.FirstOrDefault(p => p.Name == name);
            settings.Preset = name;
            if (preset == null)
            {
                return;
            }

            settings.AccentColor = preset.Accent;
            settings.SecondaryAccentColor = preset.SecondaryAccent;
            settings.BackgroundColor = preset.Background;
        }

        public static void Apply(AppearanceSettings settings)
        {
            Current = settings;
            ResourceDictionary r = Application.Current.Resources;

            Color accent = ParseColor(settings.AccentColor, "#7C5CFF");
            Color accent2 = ParseColor(settings.SecondaryAccentColor, "#00D4FF");
            Color background = ParseColor(settings.BackgroundColor, "#07080D");
            Color danger = ParseColor(settings.DangerColor, "#FF5A6A");
            double panelAlpha = Math.Clamp(settings.PanelOpacity, 0, 100) / 100.0;

            // Surfaces are the background lifted towards white, slightly tinted with the accent.
            Color surface = Mix(Mix(background, Colors.White, 0.09), accent, 0.04);
            Color surfaceHigh = Mix(Mix(background, Colors.White, 0.17), accent, 0.10);

            r["AccentColor"] = accent;
            r["Accent2Color"] = accent2;
            r["BackgroundColor"] = background;

            r["BackgroundBrush"] = Frozen(new SolidColorBrush(background));
            r["SurfaceBrush"] = Frozen(new SolidColorBrush(WithAlpha(surface, 0.18 + panelAlpha * 0.8)));
            r["SurfaceSolidBrush"] = Frozen(new SolidColorBrush(Mix(background, surface, 0.85)));
            r["SurfaceHighBrush"] = Frozen(new SolidColorBrush(WithAlpha(surfaceHigh, 0.35 + panelAlpha * 0.65)));
            r["StrokeBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
            r["TextBrush"] = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA)));
            r["SubtleTextBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xE6, 0xEA, 0xF2)));
            r["FaintTextBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xE6, 0xEA, 0xF2)));
            r["AccentBrush"] = Frozen(new SolidColorBrush(accent));
            r["Accent2Brush"] = Frozen(new SolidColorBrush(accent2));
            r["AccentSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(accent, 0.22)));
            r["OnAccentBrush"] = Frozen(new SolidColorBrush(Luminance(Mix(accent, accent2, 0.5)) > 0.62 ? Color.FromRgb(0x0A, 0x0B, 0x10) : Colors.White));
            r["DangerBrush"] = Frozen(new SolidColorBrush(danger));
            r["ScrimBrush"] = Frozen(new SolidColorBrush(WithAlpha(background, 0.72)));

            (Point start, Point end) = GradientPoints(settings.GradientDirection);
            r["AccentGradientBrush"] = Frozen(new LinearGradientBrush(accent, accent2, start, end));
            r["AccentGlowColor"] = accent;

            // Readability shading over the background art. Only where text sits (header strip, left
            // edge, bottom), so the art itself stays clearly visible in the middle and top right.
            r["TopShadeBrush"] = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new(WithAlpha(background, 0.78), 0),
                new(WithAlpha(background, 0.0), 0.2)
            }, 90));
            r["SideShadeBrush"] = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new(WithAlpha(background, 0.82), 0),
                new(WithAlpha(background, 0.30), 0.38),
                new(WithAlpha(background, 0.0), 0.62)
            }, 0));
            r["BottomShadeBrush"] = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new(WithAlpha(background, 0.0), 0.42),
                new(WithAlpha(background, 0.72), 0.78),
                new(WithAlpha(background, 0.95), 1)
            }, 90));
            r["VignetteBrush"] = Frozen(new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, 0, 0, 0), 0.55),
                new(Color.FromArgb(0xA0, 0, 0, 0), 1.0)
            }) { RadiusX = 0.75, RadiusY = 0.85 });

            r["AuroraBrush1"] = Frozen(Blob(accent));
            r["AuroraBrush2"] = Frozen(Blob(accent2));
            r["AuroraBrush3"] = Frozen(Blob(Mix(accent, accent2, 0.5)));

            // Tile placeholder when a game has no art yet: a soft accent gradient, not a grey box.
            r["TilePlaceholderBrush"] = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new(Mix(surfaceHigh, accent, 0.30), 0),
                new(Mix(surface, accent2, 0.12), 1)
            }, 60));

            // Sizes
            double radius = Math.Clamp(settings.CornerRadius, 0, 40);
            r["TileWidth"] = TileWidth;
            r["TileHeight"] = TileHeight;
            r["TileMargin"] = new Thickness(Math.Clamp(settings.TileSpacing, 0, 60) / 2.0);
            r["TileRadius"] = new CornerRadius(radius);
            r["TileBottomRadius"] = new CornerRadius(0, 0, radius, radius);
            r["TileRadiusValue"] = radius;
            r["TileGlowRadius"] = new CornerRadius(radius + 6);
            r["PanelRadius"] = new CornerRadius(Math.Min(radius * 1.25 + 4, 36));
            r["ButtonRadius"] = new CornerRadius(radius * 0.9 + 2);
            r["PillRadius"] = new CornerRadius(32);

            // Text
            r["UiFont"] = new FontFamily(settings.FontFamily + ", Segoe UI Variable Display, Segoe UI");
            r["TitleWeight"] = settings.TitleWeight switch
            {
                "Regular" => FontWeights.Normal,
                "SemiBold" => FontWeights.SemiBold,
                "Black" => FontWeights.Black,
                _ => FontWeights.Bold
            };

            r["BadgeVisibility"] = settings.ShowStoreBadges ? Visibility.Visible : Visibility.Collapsed;
        }

        // =====================================================================
        // Colour helpers
        // =====================================================================

        private static (Point start, Point end) GradientPoints(GradientDirection direction) => direction switch
        {
            GradientDirection.Horizontal => (new Point(0, 0.5), new Point(1, 0.5)),
            GradientDirection.Vertical => (new Point(0.5, 0), new Point(0.5, 1)),
            GradientDirection.ReverseDiagonal => (new Point(1, 0), new Point(0, 1)),
            _ => (new Point(0, 0), new Point(1, 1))
        };

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>HSL (h 0-360, s/l 0-1) to RGB.</summary>
        public static Color FromHsl(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360 / 360.0;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            double Channel(double t)
            {
                t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
                return t < 1 / 6.0 ? p + (q - p) * 6 * t
                    : t < 0.5 ? q
                    : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6
                    : p;
            }

            return Color.FromRgb(
                (byte)Math.Round(Channel(h + 1 / 3.0) * 255),
                (byte)Math.Round(Channel(h) * 255),
                (byte)Math.Round(Channel(h - 1 / 3.0) * 255));
        }

        public static Color ParseColor(string? hex, string fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? fallback : hex.Trim());
            }
            catch
            {
                return (Color)ColorConverter.ConvertFromString(fallback);
            }
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        public static Color WithAlpha(Color color, double alpha) =>
            Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

        private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        private static RadialGradientBrush Blob(Color color) => new(new GradientStopCollection
        {
            new(WithAlpha(color, 0.55), 0),
            new(WithAlpha(color, 0.18), 0.45),
            new(WithAlpha(color, 0), 1)
        });

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
