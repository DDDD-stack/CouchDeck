using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using CouchDeck.Audio;
using CouchDeck.Config;
using CouchDeck.Covers;
using CouchDeck.Games;

namespace CouchDeck.UI
{
    public partial class OverlayWindow
    {
        private List<SettingItem> settings = new();
        private LibraryLayout? appliedLayout;
        private GameTileViewModel? previewTile;
        private string? soundGenerationStatus;

        private enum ColorSlot { Accent, GradientEnd, Background }

        // =====================================================================
        // The Customize page
        // =====================================================================

        private void BuildSettings()
        {
            AppConfig config = Config;
            AppearanceSettings a = config.Appearance;
            List<SettingItem> items = new();

            void Header(string label, string glyph) => items.Add(new HeaderSetting(label, glyph));
            T Look<T>(T item) where T : SettingItem { item.Changed = OnAppearanceSettingChanged; items.Add(item); return item; }
            T Library<T>(T item) where T : SettingItem { item.Changed = OnLibrarySettingChanged; items.Add(item); return item; }
            T Controls<T>(T item) where T : SettingItem { item.Changed = OnControllerSettingChanged; items.Add(item); return item; }
            T Sounds<T>(T item) where T : SettingItem { item.Changed = OnSoundSettingChanged; items.Add(item); return item; }
            SoundSettings sound = config.Sound;

            static string Percent(int v) => v + "%";

            // ----- Theme -----
            Header("Theme", "\uE790");
            Look(new ChoiceSetting<string>("Theme preset", "\uE771",
                () => ThemeManager.Presets.Select(p => new ChoiceOption<string>(p.Name, p.Name, p.Accent))
                    .Append(new ChoiceOption<string>("Custom", "Custom")).ToList(),
                () => a.Preset,
                v =>
                {
                    if (v == "Custom")
                    {
                        a.Preset = "Custom";
                    }
                    else
                    {
                        ThemeManager.ApplyPreset(a, v);
                    }
                },
                "A starting point. Make it yours below in Custom colours"));
            Look(new SliderSetting("Glass panels", "\uE81E", 0, 100, 5, () => a.PanelOpacity, v => a.PanelOpacity = v, Percent,
                "How solid buttons and panels are over the background art"));

            // ----- Custom colours: a colour table for each slot -----
            Header("Custom colours", "\uE790");
            items.Add(new ActionSetting("Accent colour", "\uE790", () => OpenColorPicker(ColorSlot.Accent),
                () => a.AccentColor.ToUpperInvariant(), "Start of the gradient: focus rings, glow, highlights",
                () => SwatchBrush(a.AccentColor)));
            items.Add(new ActionSetting("Gradient end colour", "\uE790", () => OpenColorPicker(ColorSlot.GradientEnd),
                () => a.SecondaryAccentColor.ToUpperInvariant(), "The other end of every gradient",
                () => SwatchBrush(a.SecondaryAccentColor)));
            items.Add(new ActionSetting("Background colour", "\uE7F4", () => OpenColorPicker(ColorSlot.Background),
                () => a.BackgroundColor.ToUpperInvariant(), "Behind everything; dark shades only",
                () => SwatchBrush(a.BackgroundColor)));
            Look(new ChoiceSetting<GradientDirection>("Gradient direction", "\uE8AB", () => Options.Enum(
                    (GradientDirection.Diagonal, "Diagonal"),
                    (GradientDirection.Horizontal, "Left to right"),
                    (GradientDirection.Vertical, "Top to bottom"),
                    (GradientDirection.ReverseDiagonal, "Reverse diagonal")),
                () => a.GradientDirection, v => a.GradientDirection = v));
            items.Add(new ActionSetting("Swap gradient colours", "\uE8AB", () =>
                {
                    (a.AccentColor, a.SecondaryAccentColor) = (a.SecondaryAccentColor, a.AccentColor);
                    a.Preset = "Custom";
                    OnAppearanceSettingChanged();
                }, null, null, () => TryFindResource("AccentGradientBrush") as Brush));

            // ----- Background -----
            Header("Background", "");
            Look(new ChoiceSetting<BackdropMode>("Background", "", () => Options.Enum(
                    (BackdropMode.HeroArt, "Game banner"),
                    (BackdropMode.CoverPoster, "Cover poster"),
                    (BackdropMode.BlurredCover, "Colour blur"),
                    (BackdropMode.CustomImage, "My own image"),
                    (BackdropMode.Solid, "Solid colour")),
                () => a.Backdrop, v => a.Backdrop = v,
                "Banner: the focused game's wide art, sharp and uncropped (games without one show their cover)"));
            Look(new ActionSetting("Choose background image", "",
                () => OpenBrowser(BrowseMode.Image, null, path =>
                {
                    a.CustomBackgroundImage = path;
                    a.Backdrop = BackdropMode.CustomImage;
                    OnAppearanceSettingChanged();
                    ShowToast("", "Background updated", Path.GetFileName(path));
                }),
                () => string.IsNullOrEmpty(a.CustomBackgroundImage) ? "None" : Path.GetFileName(a.CustomBackgroundImage)));
            Look(new SliderSetting("Background blur", "", 0, 100, 5, () => a.BackdropBlur, v => a.BackdropBlur = v, Percent,
                "0 = perfectly sharp art"));
            Look(new SliderSetting("Background darkness", "", 0, 100, 5, () => a.BackdropDim, v => a.BackdropDim = v, Percent,
                "Higher = calmer background, easier to read"));
            Look(new SliderSetting("Aurora glow", "", 0, 100, 5, () => a.AuroraIntensity, v => a.AuroraIntensity = v, Percent,
                "Soft accent-coloured light behind everything"));
            Look(new ToggleSetting("Ambient motion", "", () => a.AmbientMotion, v => a.AmbientMotion = v, "The aurora slowly drifts"));
            Look(new ToggleSetting("Vignette", "", () => a.Vignette, v => a.Vignette = v));

            // ----- Layout -----
            Header("Layout", "");
            Look(new ChoiceSetting<LibraryLayout>("Library layout", "", () => Options.Enum(
                    (LibraryLayout.Grid, "Grid"),
                    (LibraryLayout.Shelf, "Shelf")),
                () => a.Layout, v => a.Layout = v, "Shelf = big hero art with one row of games, console style"));
            Look(new SliderSetting("Tile size", "", 70, 160, 5, () => a.TileSize, v => a.TileSize = v, Percent));
            Look(new SliderSetting("Tile spacing", "", 0, 48, 2, () => a.TileSpacing, v => a.TileSpacing = v, v => v + " px"));
            Look(new SliderSetting("Corner roundness", "", 0, 36, 2, () => a.CornerRadius, v => a.CornerRadius = v, v => v + " px"));
            Look(new ChoiceSetting<TileTitleMode>("Names on tiles", "", () => Options.Enum(
                    (TileTitleMode.OnFocus, "On focus"),
                    (TileTitleMode.Always, "Always"),
                    (TileTitleMode.Never, "Never")),
                () => a.TileTitles, v => a.TileTitles = v));
            Look(new ToggleSetting("Store badges", "", () => a.ShowStoreBadges, v => a.ShowStoreBadges = v, "Steam / Epic / GOG / Local label on each tile"));
            Look(new ToggleSetting("Big game title", "", () => a.ShowHero, v => a.ShowHero = v, "Name of the focused game above the library"));
            Look(new ToggleSetting("\"Add a game\" tile", "", () => a.ShowAddTile, v => a.ShowAddTile = v)).Changed = () =>
            {
                OnAppearanceSettingChanged();
                RebuildTiles(lastFocusedGameId);
            };
            Look(new ToggleSetting("Button hints", "", () => a.ShowHints, v => a.ShowHints = v, "The A / B / X / Y bar at the bottom"));
            Look(new ChoiceSetting<ClockMode>("Clock", "", () => Options.Enum(
                    (ClockMode.TwentyFourHour, "24-hour"),
                    (ClockMode.TwelveHour, "12-hour"),
                    (ClockMode.Off, "Off")),
                () => a.Clock, v => a.Clock = v));
            Look(new ToggleSetting("Show date", "", () => a.ShowDate, v => a.ShowDate = v));

            // ----- Motion -----
            Header("Motion", "");
            Look(new ChoiceSetting<GraphicsQuality>("Graphics quality", "", () => Options.Enum(
                    (GraphicsQuality.Auto, "Auto"),
                    (GraphicsQuality.Quality, "Quality"),
                    (GraphicsQuality.Balanced, "Balanced"),
                    (GraphicsQuality.Performance, "Performance")),
                () => a.Quality, v => a.Quality = v,
                "Performance drops the moving glow, blur, light sweep and cascades for the smoothest feel"));
            Look(new ChoiceSetting<int>("Animation speed", "", () => new List<ChoiceOption<int>>
                {
                    new(0, "Off"), new(60, "Snappy"), new(100, "Normal"), new(150, "Relaxed"), new(220, "Cinematic")
                },
                () => a.AnimationSpeed, v => a.AnimationSpeed = v));
            Look(new SliderSetting("Focus zoom", "", 100, 130, 1, () => a.FocusZoom, v => a.FocusZoom = v, Percent,
                "How much the selected tile grows"));
            Look(new SliderSetting("Focus lift", "", 0, 24, 2, () => a.FocusLift, v => a.FocusLift = v, v => v + " px"));
            Look(new SliderSetting("Glow", "", 0, 100, 5, () => a.GlowStrength, v => a.GlowStrength = v, Percent));
            Look(new SliderSetting("Dim other tiles", "", 0, 60, 2, () => a.DimUnfocused, v => a.DimUnfocused = v, Percent));
            Look(new ToggleSetting("Shine sweep", "", () => a.ShineSweep, v => a.ShineSweep = v, "A glint runs across the cover you land on"));
            Look(new ToggleSetting("Tiles fly in", "", () => a.CascadeEntrance, v => a.CascadeEntrance = v, "Cascade when the overlay opens"));
            Look(new ChoiceSetting<PageTransition>("Page transition", "", () => Options.Enum(
                    (PageTransition.Slide, "Slide"),
                    (PageTransition.Fade, "Fade"),
                    (PageTransition.Zoom, "Zoom"),
                    (PageTransition.None, "None")),
                () => a.PageTransition, v => a.PageTransition = v));

            // ----- Text -----
            Header("Text", "");
            Look(new ChoiceSetting<string>("Font", "",
                () => ThemeManager.AvailableFonts.Select(f => new ChoiceOption<string>(f, f)).ToList(),
                () => a.FontFamily, v => a.FontFamily = v));
            Look(new SliderSetting("Interface size", "", 70, 150, 5, () => a.UiScale, v => a.UiScale = v, Percent,
                "Scale everything up for a big TV far away"));
            Look(new ChoiceSetting<string>("Title weight", "", () => new List<ChoiceOption<string>>
                {
                    new("Regular", "Regular"), new("SemiBold", "Semibold"), new("Bold", "Bold"), new("Black", "Black")
                },
                () => a.TitleWeight, v => a.TitleWeight = v));
            Look(new ToggleSetting("Uppercase tabs", "", () => a.UppercaseTabs, v => a.UppercaseTabs = v));

            // ----- Sound -----
            Header("Sound", "\uE767");
            Sounds(new ToggleSetting("Sound effects", "\uE767", () => sound.Enabled, v => sound.Enabled = v));
            Sounds(new SliderSetting("Volume", "\uE995", 0, 100, 5, () => sound.Volume, v => sound.Volume = v, Percent));
            Sounds(new ChoiceSetting<string>("Sound pack", "\uE8D6",
                () => SoundService.AvailablePacks().Select(pack => new ChoiceOption<string>(pack, pack)).ToList(),
                () => sound.Pack, v => sound.Pack = v,
                "Soft and Arcade are built in. Add your own as a folder in the sounds folder")).Changed = () =>
                {
                    OnSoundSettingChanged();
                    _ = PreviewSoundAsync(Sfx.Select);
                };
            Sounds(new ToggleSetting("Navigation ticks", "\uE7C9", () => sound.NavigationSounds, v => sound.NavigationSounds = v,
                "A soft tick when moving around and changing settings"));
            items.Add(new ActionSetting("Generate an AI sound pack", "\uE945", () => _ = GenerateSoundPackAsync(),
                () => soundGenerationStatus ?? (ElevenLabsKey() == null ? "Needs an ElevenLabs key" : "ElevenLabs"),
                "Creates a whole new pack from text prompts. Put ElevenLabsApiKey in config.json first"));
            items.Add(new ActionSetting("Open sounds folder", "\uE8B7", () =>
                {
                    Directory.CreateDirectory(SoundService.SoundsFolder);
                    Process.Start(new ProcessStartInfo(SoundService.SoundsFolder) { UseShellExecute = true });
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                }, () => "Custom packs",
                "One folder per pack, files named navigate, select, back, page, open, close, launch... (.wav or .mp3)"));

            // ----- Library -----
            Header("Library", "");
            items.Add(new ActionSetting("Add a game", "", () => OpenBrowser(BrowseMode.GameExe, lastBrowsePath, AddGameFromPath),
                description: "Pick any .exe. Cover art is found automatically."));
            items.Add(new ActionSetting("Add a games folder", "", () => OpenBrowser(BrowseMode.GamesFolder, lastBrowsePath, AddGamesFolder),
                () => config.GameFolders.Count == 0 ? "None yet" : $"{config.GameFolders.Count} scanned",
                "Every sub-folder is treated as a game: portable and DRM-free installs"));

            foreach (string folder in config.GameFolders.ToList())
            {
                items.Add(new ActionSetting(folder, "", () =>
                    Confirm("", "Stop scanning this folder?", folder + "\nGames in it disappear from CouchDeck. Nothing is deleted.", "Remove", () =>
                    {
                        config.GameFolders.Remove(folder);
                        host?.SaveConfig();
                        host?.Rescan();
                        RebuildSettings();
                    }), () => "Remove"));
            }

            Library(new ChoiceSetting<SortMode>("Sort games", "", () => Options.Enum(
                    (SortMode.RecentlyPlayed, "Recently played"),
                    (SortMode.Alphabetical, "A to Z"),
                    (SortMode.Store, "By store")),
                () => config.Sort, v => config.Sort = v));
            Library(new ToggleSetting("Favorites first", "", () => config.FavoritesFirst, v => config.FavoritesFirst = v));
            Library(new ToggleSetting("Find cover art automatically", "", () => config.AutoFetchCovers, v => config.AutoFetchCovers = v,
                "Searches the web when a game has no art; results are cached"));
            Library(new ToggleSetting("Download background art", "", () => config.DownloadHeroArt, v => config.DownloadHeroArt = v,
                "Wide art shown behind the focused game"));
            Library(new ToggleSetting("Use official game names", "", () => config.UseOfficialNames, v => config.UseOfficialNames = v,
                "\"Elden.Ring-CODEX\" shows as \"ELDEN RING\" once matched (applies on rescan)"));
            items.Add(new ActionSetting("Find missing cover art now", "", FindMissingArt,
                () => $"{allGames.Count(g => g.CoverImage == null)} without art"));
            items.Add(new ActionSetting("Re-download all art", "", () =>
                Confirm("", "Re-download all cover art?", "Deletes CouchDeck's art cache and searches again.", "Re-download", () =>
                {
                    host?.Covers.ClearCache();
                    host?.Rescan();
                    ShowToast("", "Re-downloading art", "Tiles fill in as covers arrive.");
                })));
            items.Add(new ActionSetting("Unhide games", "", () =>
                {
                    if (config.HiddenGameIds.Count == 0)
                    {
                        ShowToast("", "No hidden games", null);
                        return;
                    }

                    config.HiddenGameIds.Clear();
                    host?.SaveConfig();
                    host?.Rescan();
                    ShowToast("", "All games visible again", null);
                    RefreshSettings();
                },
                () => config.HiddenGameIds.Count == 0 ? "None hidden" : $"{config.HiddenGameIds.Count} hidden"));
            items.Add(new ActionSetting("Rescan library", "", RequestRescan));
            items.Add(new ActionSetting("SteamGridDB key", "", () =>
                    ShowToast("", "Optional: more cover choices", "Get a free key at steamgriddb.com, put it in config.json as SteamGridDbApiKey."),
                () => string.IsNullOrWhiteSpace(config.SteamGridDbApiKey) ? "Not set (optional)" : "Active"));

            // ----- Steam -----
            Header("Steam", "");
            items.Add(new ActionSetting("Steam account", "", ShowSteamSetup, () => host?.SteamStatus ?? "",
                "Shows the games you own but haven't installed (Library page) and the achievements you earn"));
            items.Add(new ToggleSetting("New achievements after playing", "", () => config.ShowNewAchievements,
                v => config.ShowNewAchievements = v,
                "When you quit a Steam game, CouchDeck opens and lists what you unlocked")
            {
                Changed = () =>
                {
                    host?.SaveConfig();
                    RefreshSettings();
                }
            });

            // ----- Controller -----
            Header("Controller", "");
            Controls(new SliderSetting("Mouse mode speed", "", 600, 4000, 100, () => (int)config.MouseSpeed, v => config.MouseSpeed = v));
            Controls(new SliderSetting("Mouse mode hold time", "", 300, 2000, 100, () => config.MouseModeChordHoldMs,
                v => config.MouseModeChordHoldMs = v, v => v + " ms", "Hold View + Menu this long to toggle mouse mode"));
            Controls(new ChoiceSetting<int>("Menu scroll speed", "", () => new List<ChoiceOption<int>>
                {
                    new(160, "Relaxed"), new(110, "Normal"), new(80, "Fast"), new(55, "Turbo")
                },
                () => config.NavigationRepeatMs, v => config.NavigationRepeatMs = v, "How fast a held direction repeats"));
            Controls(new ToggleSetting("Start with Windows", "", () => config.StartWithWindows, v => config.StartWithWindows = v));
            Controls(new ToggleSetting("Back to CouchDeck after a game", "", () => config.ReopenAfterGame, v => config.ReopenAfterGame = v,
                "Opens again when a game you started here closes"));

            // ----- Reset -----
            Header("Reset", "");
            items.Add(new ActionSetting("Reset look to defaults", "", () =>
                Confirm("", "Reset every look and motion setting?", "Your library, favorites and folders stay.", "Reset", () =>
                {
                    config.Appearance = new AppearanceSettings();
                    BuildSettings();
                    OnAppearanceSettingChanged();
                    ShowToast("", "Look reset to defaults", null);
                })));
            items.Add(new ActionSetting("Open config folder", "", () =>
            {
                Process.Start(new ProcessStartInfo(ConfigStore.FolderPath) { UseShellExecute = true });
                ResumeRequested?.Invoke(this, EventArgs.Empty);
            }, () => "config.json"));

            settings = items;
            SettingsList.ItemsSource = settings;
        }

        /// <summary>Rebuilds the list (e.g. after a folder was added) and puts focus back on the same row.</summary>
        private void RebuildSettings()
        {
            string? focusedLabel = FocusedItem<SettingItem>()?.Label;
            BuildSettings();

            if (focusedLabel == null || currentPage != OverlayPage.Customize || IsModalOpen)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                SettingItem? item = settings.FirstOrDefault(s => s.Label == focusedLabel);
                if (item != null && SettingsList.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container
                    && FindDescendant<Button>(container) is Button button)
                {
                    Keyboard.Focus(button);
                }
            }, DispatcherPriority.Loaded);
        }

        private void RefreshSettings()
        {
            foreach (SettingItem item in settings)
            {
                item.Refresh();
            }
        }

        private void OnSettingClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SettingItem item })
            {
                bool modalBefore = IsModalOpen;
                item.Activate();

                if (item.Kind == SettingKind.Action)
                {
                    if (!IsModalOpen && !modalBefore)
                    {
                        Sound(Sfx.Select); // dialogs play their own open sound
                    }
                }
                else
                {
                    Sound(item.Kind == SettingKind.Toggle && !item.IsOn ? Sfx.ChangeDown : Sfx.ChangeUp);
                }
            }
        }

        private void OnSoundSettingChanged()
        {
            host?.Sounds.Apply(Config.Sound);
            host?.SaveConfig();
            RefreshSettings();
        }

        /// <summary>Plays a sound once a newly picked pack has loaded (loading takes a moment).</summary>
        private async Task PreviewSoundAsync(Sfx sfx)
        {
            await Task.Delay(350);
            Sound(sfx);
        }

        private string? ElevenLabsKey()
        {
            string? key = Config.ElevenLabsApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
            }

            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        private async Task GenerateSoundPackAsync()
        {
            if (soundGenerationStatus != null || host == null)
            {
                return;
            }

            string? key = ElevenLabsKey();
            if (key == null)
            {
                ShowToast("\uE946", "ElevenLabs key needed",
                    "Add \"ElevenLabsApiKey\": \"your key\" to config.json (Customize > Reset > Open config folder), then try again.", Sfx.Error);
                return;
            }

            soundGenerationStatus = "Starting...";
            RefreshSettings();
            ShowToast("\uE945", "Generating your sound pack", "15 sounds with ElevenLabs. About a minute.");

            try
            {
                Progress<(int done, int total)> progress = new(p =>
                {
                    soundGenerationStatus = $"Generating {p.done} / {p.total}";
                    RefreshSettings();
                });

                string? error = await SoundService.GenerateElevenLabsPackAsync(key, progress, CancellationToken.None);
                if (error != null)
                {
                    ShowToast("\uE783", "Sound pack failed", error, Sfx.Error);
                    return;
                }

                Config.Sound.Pack = "ElevenLabs";
                host.Sounds.Reload();
                host.Sounds.Apply(Config.Sound);
                host.SaveConfig();
                ShowToast("\uE945", "AI sound pack ready", "Sound pack switched to ElevenLabs.");
                _ = PreviewSoundAsync(Sfx.Notify);
            }
            catch (Exception ex)
            {
                ShowToast("\uE783", "Sound pack failed", ex.Message, Sfx.Error);
            }
            finally
            {
                soundGenerationStatus = null;
                RebuildSettings();
            }
        }

        private static Brush? SwatchBrush(string hex)
        {
            SolidColorBrush brush = new(ThemeManager.ParseColor(hex, "#808080"));
            brush.Freeze();
            return brush;
        }

        // =====================================================================
        // Colour table: pick any colour with the D-pad
        // =====================================================================

        /// <summary>
        /// A grid of colours: hue left to right (24 steps + greys), light to dark top to bottom. Moving
        /// over a cell previews it on a big swatch and on the gradient; A applies, B cancels.
        /// The background slot gets a table of dark shades only, so text stays readable.
        /// </summary>
        private void OpenColorPicker(ColorSlot slot)
        {
            AppearanceSettings a = Config.Appearance;
            bool dark = slot == ColorSlot.Background;
            string currentHex = slot switch
            {
                ColorSlot.Accent => a.AccentColor,
                ColorSlot.GradientEnd => a.SecondaryAccentColor,
                _ => a.BackgroundColor
            };
            Color current = ThemeManager.ParseColor(currentHex, "#7C5CFF");

            const int hues = 24;
            double[] lightness = dark
                ? new[] { 0.22, 0.17, 0.135, 0.105, 0.08, 0.06, 0.04, 0.02 }
                : new[] { 0.86, 0.77, 0.68, 0.59, 0.51, 0.43, 0.35, 0.27 };
            double[] greys = dark
                ? new[] { 0.2, 0.15, 0.12, 0.09, 0.065, 0.045, 0.025, 0.0 }
                : new[] { 1.0, 0.86, 0.73, 0.6, 0.48, 0.36, 0.24, 0.12 };
            double saturation = dark ? 0.45 : 0.92;

            // Cells shrink if the interface is scaled up, so the table always fits.
            double available = Math.Max(600, UiRoot.ActualWidth - 240);
            double cell = Math.Clamp(available / (hues + 1) - 6, 24, 44);

            UniformGrid table = new() { Columns = hues + 1, HorizontalAlignment = HorizontalAlignment.Center };
            Button? nearest = null;
            double bestDistance = double.MaxValue;

            Border bigSwatch = new() { Width = 120, Height = 64, CornerRadius = new CornerRadius(18), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1.5) };
            Border gradient = new() { Width = 460, Height = 64, CornerRadius = new CornerRadius(18), Margin = new Thickness(16, 0, 0, 0) };
            TextBlock hex = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
            TextBlock help = new()
            {
                Text = "◀ ▶  colour     ▲ ▼  lighter / darker     A  apply     B  cancel",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(30, 0, 0, 0)
            };
            help.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");

            void Preview(Color color)
            {
                bigSwatch.Background = new SolidColorBrush(color);
                hex.Text = ThemeManager.ToHex(color);
                Color start = slot == ColorSlot.GradientEnd ? ThemeManager.ParseColor(a.AccentColor, "#7C5CFF") : color;
                Color end = slot == ColorSlot.Accent ? ThemeManager.ParseColor(a.SecondaryAccentColor, "#00D4FF") : color;
                if (slot == ColorSlot.Background)
                {
                    // Show the background with the current accents on top of it.
                    gradient.Background = new LinearGradientBrush(new GradientStopCollection
                    {
                        new(color, 0), new(color, 0.55),
                        new(ThemeManager.ParseColor(a.AccentColor, "#7C5CFF"), 0.78),
                        new(ThemeManager.ParseColor(a.SecondaryAccentColor, "#00D4FF"), 1)
                    }, 0);
                }
                else
                {
                    gradient.Background = new LinearGradientBrush(start, end, 0);
                }
            }

            void AddCell(Color color)
            {
                SolidColorBrush fill = new(color);
                fill.Freeze();
                Button button = new() { Background = fill, Tag = color, Width = cell, Height = cell };
                button.SetResourceReference(StyleProperty, "ColorCell");
                button.Click += (_, _) => Commit(color);
                table.Children.Add(button);

                double distance = Math.Pow(color.R - current.R, 2) + Math.Pow(color.G - current.G, 2) + Math.Pow(color.B - current.B, 2);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = button;
                }
            }

            for (int row = 0; row < lightness.Length; row++)
            {
                for (int h = 0; h < hues; h++)
                {
                    AddCell(ThemeManager.FromHsl(h * 360.0 / hues, saturation, lightness[row]));
                }

                AddCell(ThemeManager.FromHsl(0, 0, greys[row]));
            }

            table.GotKeyboardFocus += (_, e) =>
            {
                if (e.NewFocus is FrameworkElement { Tag: Color color })
                {
                    Preview(color);
                }
            };

            void Commit(Color color)
            {
                string value = ThemeManager.ToHex(color);
                switch (slot)
                {
                    case ColorSlot.Accent: a.AccentColor = value; break;
                    case ColorSlot.GradientEnd: a.SecondaryAccentColor = value; break;
                    default: a.BackgroundColor = value; break;
                }

                a.Preset = "Custom";
                CloseModal();
                OnAppearanceSettingChanged();
                ShowToast("\uE790", "Colour applied", value);
            }

            StackPanel previewRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 24) };
            previewRow.Children.Add(bigSwatch);
            previewRow.Children.Add(gradient);
            previewRow.Children.Add(hex);
            previewRow.Children.Add(help);

            StackPanel content = new();
            content.Children.Add(previewRow);
            content.Children.Add(table);
            Preview(current);

            string title = slot switch
            {
                ColorSlot.Accent => "Accent colour",
                ColorSlot.GradientEnd => "Gradient end colour",
                _ => "Background colour"
            };

            double width = Math.Min(UiRoot.ActualWidth - 60, (cell + 6) * (hues + 1) + 120);
            ShowCustomModal("\uE790", title, null, content, Math.Max(width, 700), null, () => nearest);
        }

        private void OnAppearanceSettingChanged()
        {
            ThemeManager.Apply(Config.Appearance);
            ApplyAppearance();
            RefreshSettings();
            host?.SaveConfig();
        }

        private void OnLibrarySettingChanged()
        {
            host?.SaveConfig();
            RefreshSettings();
            BuildFilterChips();
            RebuildTiles(lastFocusedGameId);
        }

        private void OnControllerSettingChanged()
        {
            host?.ApplyControllerSettings();
            host?.SaveConfig();
            RefreshSettings();
        }

        private void FindMissingArt()
        {
            if (host == null)
            {
                return;
            }

            List<Game> missing = allGames.Where(g => g.CoverImage == null).ToList();
            if (missing.Count == 0)
            {
                ShowToast("", "Every game has cover art", null);
                return;
            }

            host.Covers.EnqueueMissing(missing, force: true);
            ShowToast("", $"Searching art for {missing.Count} game{(missing.Count == 1 ? "" : "s")}", "Tiles update as covers arrive.");
        }

        // =====================================================================
        // Applying appearance
        // =====================================================================

        /// <summary>Pushes the current AppearanceSettings into everything that isn't a DynamicResource.</summary>
        private void ApplyAppearance()
        {
            AppearanceSettings a = ThemeManager.Current;

            double scale = Math.Clamp(a.UiScale, 60, 200) / 100.0;
            UiScale.ScaleX = scale;
            UiScale.ScaleY = scale;

            ApplyLayoutMode(a.Layout);

            HeroPanel.Visibility = a.ShowHero ? Visibility.Visible : Visibility.Collapsed;
            HeaderLogo.Source = AppIcon.CreateImage(
                ThemeManager.ParseColor(a.AccentColor, "#7C5CFF"), ThemeManager.ParseColor(a.SecondaryAccentColor, "#00D4FF"));
            HintsBar.Visibility = a.ShowHints ? Visibility.Visible : Visibility.Collapsed;
            Vignette.Visibility = a.Vignette ? Visibility.Visible : Visibility.Collapsed;

            foreach ((TextBlock tab, OverlayPage page) in Tabs())
            {
                string name = page.ToString();
                tab.Text = a.UppercaseTabs ? name.ToUpperInvariant() : name;
            }

            UpdateClock();

            // Background shading and glow
            double dim = Math.Clamp(a.BackdropDim, 0, 100) / 100.0;
            DimLayer.Opacity = dim * 0.8;
            Aurora.Opacity = Math.Clamp(a.AuroraIntensity, 0, 100) / 100.0;
            if (IsVisible)
            {
                StartAmbient();
            }

            backdropKey = null;
            if (a.Backdrop is BackdropMode.Solid or BackdropMode.CustomImage)
            {
                ShowNeutralBackdrop();
            }
            else if (FocusedTile() is GameTileViewModel tile)
            {
                ScheduleBackdrop(tile);
            }

            // Focus effects use the new zoom / glow / dim amounts
            foreach (Button button in FindDescendants<Button>(GamesList).Concat(FindDescendants<Button>(SystemContent)))
            {
                Fx.Refresh(button);
            }

            gamesScroller.EdgePadding = a.Layout == LibraryLayout.Shelf ? ThemeManager.TileWidth * 1.3 : 70;
            ReloadCovers();
            UpdatePreview();
            PositionTabIndicator(animate: false);
        }

        private void ApplyLayoutMode(LibraryLayout layout)
        {
            if (appliedLayout == layout)
            {
                return;
            }

            appliedLayout = layout;
            bool shelf = layout == LibraryLayout.Shelf;

            GamesList.ItemsPanel = (ItemsPanelTemplate)FindResource(shelf ? "ShelfPanel" : "GridPanel");
            GamesList.Margin = shelf ? new Thickness(14, 34, 80, 34) : new Thickness(10, 22, 10, 46);
            GamesList.HorizontalAlignment = shelf ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            GamesScroll.HorizontalScrollBarVisibility = shelf ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            GamesScroll.VerticalScrollBarVisibility = shelf ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            gamesScroller.Horizontal = shelf;
            gamesScroller.ScrollToStart();

            HeroRow.Height = shelf ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            LibraryRow.Height = shelf ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            HeroTitle.FontSize = shelf ? 84 : 58;
            HeroMeta.FontSize = shelf ? 20 : 17;
        }

        /// <summary>The sample tile on the Customize page, always shown in its focused state.</summary>
        private void UpdatePreview()
        {
            Game? sample = allGames.FirstOrDefault(g => g.CoverImage != null) ?? allGames.FirstOrDefault();
            if (sample == null)
            {
                sample = new Game { Id = "preview", Title = "Your game", Source = GameSource.Custom, LaunchTarget = "" };
            }

            if (previewTile?.Id != sample.Id)
            {
                previewTile = tileMap.TryGetValue(sample.Id, out GameTileViewModel? existing)
                    ? new GameTileViewModel(existing.Game, existing.IsFavorite)
                    : new GameTileViewModel(sample, false);
                PreviewTile.DataContext = previewTile;
            }
            else
            {
                previewTile.Game = sample;
            }

            _ = previewTile.LoadCoverAsync(CoverDecodeWidth());
            PreviewFontName.Text = $"{ThemeManager.Current.FontFamily}  ·  {ThemeManager.Current.Preset} theme";

            Dispatcher.BeginInvoke(() => Fx.ShowFocused(PreviewTile), DispatcherPriority.Loaded);
        }

        private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (T nested in FindDescendants<T>(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
