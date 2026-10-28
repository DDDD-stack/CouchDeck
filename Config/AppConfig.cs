using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchDeck.Config
{
    /// <summary>A game you add by hand: emulators, DRM-free indies, Game Pass shortcuts, Minecraft...</summary>
    public sealed class CustomGameEntry
    {
        public string Title { get; set; } = "";

        /// <summary>Full .exe path, a URI, or "shell:AppsFolder\PackageFamilyName!App" for Store games.</summary>
        public string Path { get; set; } = "";

        public string? Arguments { get; set; }
        public string? CoverImage { get; set; }

        /// <summary>
        /// True when CouchDeck guessed the title from a folder/exe name (added from the overlay).
        /// Such titles get replaced by the store's official name once cover art is matched.
        /// </summary>
        public bool AutoTitle { get; set; }
    }

    /// <summary>Per-game tweaks made from the overlay (Y on a game tile) or by hand.</summary>
    public sealed class GameOverride
    {
        public string? Title { get; set; }

        /// <summary>A local image file chosen by the user. Wins over any downloaded cover.</summary>
        public string? CoverImage { get; set; }

        public bool Favorite { get; set; }

        /// <summary>Replaces the detected .exe (folder-scanned games sometimes pick the wrong one).</summary>
        public string? LaunchTarget { get; set; }

        public string? Arguments { get; set; }
    }

    /// <summary>
    /// HeroArt: the game's wide banner across the top, full width, fading into the background.
    /// CoverPoster: the portrait cover large on the right, fading to the left.
    /// BlurredCover: an intentional colour wash from the cover.
    /// </summary>
    public enum BackdropMode { HeroArt, CoverPoster, BlurredCover, CustomImage, Solid }
    public enum GradientDirection { Diagonal, Horizontal, Vertical, ReverseDiagonal }
    public enum GraphicsQuality { Auto, Quality, Balanced, Performance }
    public enum LibraryLayout { Grid, Shelf }
    public enum TileTitleMode { OnFocus, Always, Never }
    public enum PageTransition { Slide, Fade, Zoom, None }
    public enum ClockMode { TwentyFourHour, TwelveHour, Off }
    public enum SortMode { RecentlyPlayed, Alphabetical, Store }

    /// <summary>
    /// Everything about how the overlay looks and moves. Edited live from the Customize page;
    /// every value can also be set by hand in config.json (colours as "#RRGGBB").
    /// </summary>
    public sealed class AppearanceSettings
    {
        // ----- Colours -----
        public string Preset { get; set; } = "Nebula";
        public string AccentColor { get; set; } = "#7C5CFF";
        public string SecondaryAccentColor { get; set; } = "#00D4FF";
        public string BackgroundColor { get; set; } = "#07080D";
        public string DangerColor { get; set; } = "#FF5A6A";

        public GradientDirection GradientDirection { get; set; } = GradientDirection.Diagonal;

        /// <summary>0-100. How opaque the glass panels and buttons are.</summary>
        public int PanelOpacity { get; set; } = 62;

        // ----- Background -----
        public BackdropMode Backdrop { get; set; } = BackdropMode.HeroArt;
        public string? CustomBackgroundImage { get; set; }
        public int BackdropBlur { get; set; } = 0;
        public int BackdropDim { get; set; } = 30;
        public int AuroraIntensity { get; set; } = 35;
        public bool AmbientMotion { get; set; } = true;
        public bool Vignette { get; set; } = true;

        // ----- Layout -----
        public LibraryLayout Layout { get; set; } = LibraryLayout.Grid;
        public int TileSize { get; set; } = 100;       // percent of 220x330
        public int TileSpacing { get; set; } = 14;
        public int CornerRadius { get; set; } = 16;
        public TileTitleMode TileTitles { get; set; } = TileTitleMode.OnFocus;
        public bool ShowStoreBadges { get; set; } = true;
        public bool ShowHero { get; set; } = true;
        public bool ShowHints { get; set; } = true;
        public bool ShowAddTile { get; set; } = true;
        public ClockMode Clock { get; set; } = ClockMode.TwentyFourHour;
        public bool ShowDate { get; set; } = true;

        // ----- Motion -----
        /// <summary>0 = off, 100 = normal, 200 = twice as slow (cinematic).</summary>
        public int AnimationSpeed { get; set; } = 100;
        public int FocusZoom { get; set; } = 110;      // percent
        public int FocusLift { get; set; } = 8;        // px
        public int GlowStrength { get; set; } = 70;
        public int DimUnfocused { get; set; } = 18;
        public bool ShineSweep { get; set; } = true;
        public bool CascadeEntrance { get; set; } = true;
        public PageTransition PageTransition { get; set; } = PageTransition.Slide;

        /// <summary>
        /// Auto picks Balanced on a capable GPU and Performance otherwise. Performance turns off the
        /// costly extras: moving aurora, blur, glow effects, light sweep, cascades.
        /// </summary>
        public GraphicsQuality Quality { get; set; } = GraphicsQuality.Auto;

        // ----- Text -----
        public string FontFamily { get; set; } = "Segoe UI Variable Display";
        public int UiScale { get; set; } = 100;
        public string TitleWeight { get; set; } = "Bold";
        public bool UppercaseTabs { get; set; } = false;
    }

    public sealed class SoundSettings
    {
        public bool Enabled { get; set; } = true;
        public int Volume { get; set; } = 60;

        /// <summary>"Soft", "Arcade", or the name of a folder in %AppData%\CouchDeck\sounds.</summary>
        public string Pack { get; set; } = "Soft";

        /// <summary>Ticks when moving around and changing settings.</summary>
        public bool NavigationSounds { get; set; } = true;
    }

    /// <summary>
    /// User settings, saved as %AppData%\CouchDeck\config.json. Most of it is editable from the
    /// overlay's Customize page; anything else can be edited by hand.
    /// </summary>
    public sealed class AppConfig
    {
        public const int CurrentVersion = 2;

        /// <summary>Bumped when defaults change in a way old configs should pick up (see ConfigStore.Migrate).</summary>
        public int ConfigVersion { get; set; }

        public bool StartWithWindows { get; set; } = true;

        /// <summary>Pixels per second at full stick tilt in mouse mode.</summary>
        public float MouseSpeed { get; set; } = 1800f;

        /// <summary>How long View + Menu must be held to toggle mouse mode.</summary>
        public int MouseModeChordHoldMs { get; set; } = 800;

        /// <summary>Delay between repeated moves while a direction is held.</summary>
        public int NavigationRepeatMs { get; set; } = 110;

        public List<CustomGameEntry> CustomGames { get; set; } = new();

        /// <summary>
        /// Folders scanned for games that no launcher knows about (portable / DRM-free installs).
        /// Each sub-folder counts as one game; its main .exe is detected automatically.
        /// </summary>
        public List<string> GameFolders { get; set; } = new();

        /// <summary>Game ids ("steam:228980", "epic:Fortnite") to never show.</summary>
        public HashSet<string> HiddenGameIds { get; set; } = new();

        /// <summary>Launch times recorded by CouchDeck, used for sorting stores that don't track it.</summary>
        public Dictionary<string, DateTime> LastLaunched { get; set; } = new();

        public Dictionary<string, GameOverride> GameOverrides { get; set; } = new();

        // ----- Library behaviour -----
        public SortMode Sort { get; set; } = SortMode.RecentlyPlayed;
        public bool FavoritesFirst { get; set; } = true;

        /// <summary>Search the web for cover art when a game has none (results are cached on disk).</summary>
        public bool AutoFetchCovers { get; set; } = true;

        /// <summary>Also download wide "hero" art used as the background behind the focused game.</summary>
        public bool DownloadHeroArt { get; set; } = true;

        /// <summary>Show the store's official name for games whose title was guessed from a folder name.</summary>
        public bool UseOfficialNames { get; set; } = true;

        /// <summary>When a game started from CouchDeck closes, open CouchDeck again (like a console going back home).</summary>
        public bool ReopenAfterGame { get; set; } = true;

        /// <summary>
        /// Optional, free from steamgriddb.com/profile/preferences/api. When set, cover search also
        /// uses SteamGridDB (more art, more alternatives for games that aren't on Steam).
        /// </summary>
        public string? SteamGridDbApiKey { get; set; }

        /// <summary>Optional. Lets Customize > Sound generate an AI sound pack with ElevenLabs.</summary>
        public string? ElevenLabsApiKey { get; set; }

        /// <summary>
        /// Optional, free from steamcommunity.com/dev/apikey (Customize > Steam can paste it). Lets the Library
        /// page list Steam games you own but haven't installed, and shows the achievements you earned after playing.
        /// </summary>
        public string? SteamWebApiKey { get; set; }

        /// <summary>Optional SteamID64 to read. Normally the account logged in to Steam is detected.</summary>
        public string? SteamId { get; set; }

        /// <summary>When you quit a Steam game, open CouchDeck and list the achievements you just earned.</summary>
        public bool ShowNewAchievements { get; set; } = true;

        public AppearanceSettings Appearance { get; set; } = new();

        public SoundSettings Sound { get; set; } = new();

        public GameOverride OverrideFor(string gameId)
        {
            if (!GameOverrides.TryGetValue(gameId, out GameOverride? entry))
            {
                entry = new GameOverride();
                GameOverrides[gameId] = entry;
            }

            return entry;
        }
    }

    public static class ConfigStore
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly object SaveLock = new();

        public static string FolderPath { get; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchDeck");

        public static string FilePath { get; } = System.IO.Path.Combine(FolderPath, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    AppConfig loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
                    loaded.Appearance ??= new AppearanceSettings();
                    loaded.Sound ??= new SoundSettings();
                    loaded.GameFolders ??= new List<string>();
                    loaded.GameOverrides ??= new Dictionary<string, GameOverride>();
                    loaded.CustomGames ??= new List<CustomGameEntry>();
                    loaded.HiddenGameIds ??= new HashSet<string>();
                    loaded.LastLaunched ??= new Dictionary<string, DateTime>();
                    Migrate(loaded);
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                // A typo in a hand-edited file shouldn't stop the app from starting.
                Debug.WriteLine($"Config unreadable, using defaults: {ex.Message}");
                return new AppConfig();
            }

            AppConfig fresh = new() { ConfigVersion = AppConfig.CurrentVersion };
            Save(fresh); // write an example file the user can edit
            return fresh;
        }

        private static void Migrate(AppConfig config)
        {
            if (config.ConfigVersion < 2)
            {
                // v2: background art is shown sharp and un-zoomed; the old blur/darkness defaults hid it.
                config.Appearance.BackdropBlur = 0;
                config.Appearance.BackdropDim = 30;
            }

            config.ConfigVersion = AppConfig.CurrentVersion;
        }

        public static void Save(AppConfig config)
        {
            try
            {
                string json;
                lock (SaveLock)
                {
                    json = JsonSerializer.Serialize(config, JsonOptions);
                    Directory.CreateDirectory(FolderPath);

                    // Write-then-rename so a crash mid-save never leaves a half-written config.
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, FilePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Config save failed: {ex.Message}");
            }
        }
    }
}
