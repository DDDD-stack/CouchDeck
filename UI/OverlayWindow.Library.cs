using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CouchDeck.Audio;
using CouchDeck.Config;
using CouchDeck.Covers;
using CouchDeck.Games;
using CouchDeck.Input;

namespace CouchDeck.UI
{
    public partial class OverlayWindow
    {
        private const string FilterAll = "all";
        private const string FilterFavorites = "fav";

        private readonly ObservableCollection<object> tiles = new();
        private readonly ObservableCollection<FilterChipViewModel> filterChips = new();
        private readonly Dictionary<string, GameTileViewModel> tileMap = new();
        private readonly AddTileViewModel addTile = new();
        private List<Game> allGames = new();
        private HashSet<string> knownGameIds = new();
        private HashSet<string> knownInstalledIds = new();
        private bool hasLoadedGames;
        private bool scanCompleted;
        private string filterKey = FilterAll;
        private string? lastFocusedGameId;
        private string? lastBrowsePath;
        private DispatcherTimer? coverReloadTimer;

        // Games (installed) and Library (owned, not installed) share the grid; each keeps its own filter and focus.
        private OverlayPage tilesPage = OverlayPage.Games;
        private readonly Dictionary<OverlayPage, (string filter, string? focus)> gridState = new();
        private readonly bool steamInstalled = SteamScanner.FindSteamPath() != null;

        private readonly List<AchievementSummary> pendingAchievements = new();
        private IReadOnlyDictionary<string, SteamDownload> downloads = new Dictionary<string, SteamDownload>();

        // Backdrop crossfade
        private readonly DispatcherTimer backdropTimer;
        private GameTileViewModel? backdropPending;
        private string? backdropKey;
        private bool backdropAIsActive;
        private int backdropVersion;

        // =====================================================================
        // Called by App
        // =====================================================================

        public void SetGames(IReadOnlyList<Game> scanned)
        {
            List<Game> fresh = scanned.ToList();

            // Only installed games count as "new" (a game installed from Library included), not a freshly loaded Steam list.
            List<Game> added = hasLoadedGames ? fresh.Where(g => g.IsInstalled && !knownInstalledIds.Contains(g.Id)).ToList() : new List<Game>();

            allGames = fresh;
            knownGameIds = fresh.Select(g => g.Id).ToHashSet();
            knownInstalledIds = fresh.Where(g => g.IsInstalled).Select(g => g.Id).ToHashSet();
            scanCompleted = true;

            foreach (Game game in fresh)
            {
                if (tileMap.TryGetValue(game.Id, out GameTileViewModel? vm))
                {
                    vm.Game = game;
                }
                else
                {
                    tileMap[game.Id] = new GameTileViewModel(game, IsFavorite(game.Id));
                }
            }

            foreach (string stale in tileMap.Keys.Where(id => !knownGameIds.Contains(id)).ToList())
            {
                tileMap.Remove(stale);
            }

            ApplyDownloads();
            UpdateLibraryTab();
            BuildFilterChips();
            RebuildTiles(lastFocusedGameId);

            if (added.Count == 1)
            {
                ShowToast("\uE7FC", "New game: " + added[0].Title, CoverHint(added[0]), Sfx.Notify);
            }
            else if (added.Count > 1)
            {
                ShowToast("\uE7FC", $"{added.Count} new games found", string.Join(", ", added.Take(3).Select(g => g.Title)) + (added.Count > 3 ? "..." : ""), Sfx.Notify);
            }

            hasLoadedGames = true;
            UpdatePreview();
        }

        /// <summary>CoverArtService found art (or an official name) for a game.</summary>
        public void ApplyArt(ArtUpdate update)
        {
            int index = allGames.FindIndex(g => g.Id == update.GameId);
            if (index < 0)
            {
                return;
            }

            Game old = allGames[index];
            Game updated = old with
            {
                CoverImage = update.CoverPath ?? old.CoverImage,
                HeroImage = update.HeroPath ?? old.HeroImage,
                Title = update.OfficialTitle ?? old.Title
            };
            allGames[index] = updated;

            if (tileMap.TryGetValue(update.GameId, out GameTileViewModel? vm))
            {
                vm.Game = updated;
                _ = vm.LoadCoverAsync(CoverDecodeWidth());

                if (lastFocusedGameId == vm.Id && IsGridPage(currentPage))
                {
                    UpdateHero(vm, force: true);
                    backdropKey = null;
                    ScheduleBackdrop(vm);
                }
            }
        }

        public void SetArtQueue(int pending)
        {
            ArtChipText.Text = $"Finding cover art · {pending}";
            ArtChip.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateArtSpinner();
        }

        private void UpdateArtSpinner()
        {
            bool spin = IsVisible && ArtChip.Visibility == Visibility.Visible && ThemeManager.AnimationsEnabled;
            if (spin && !artSpinning)
            {
                ArtSpin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else if (!spin && artSpinning)
            {
                ArtSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            }

            artSpinning = spin;
        }

        // =====================================================================
        // Tiles
        // =====================================================================

        private bool IsFavorite(string id) =>
            Config.GameOverrides.TryGetValue(id, out GameOverride? o) && o.Favorite;

        private void RebuildTiles(string? focusId)
        {
            List<Game> visible = FilterAndSort(allGames).ToList();

            tiles.Clear();
            foreach (Game game in visible)
            {
                GameTileViewModel vm = tileMap[game.Id];
                vm.IsFavorite = IsFavorite(game.Id);
                tiles.Add(vm);
            }

            if (ThemeManager.Current.ShowAddTile && filterKey == FilterAll && tilesPage == OverlayPage.Games)
            {
                tiles.Add(addTile);
            }

            // With the "+ Add a game" tile showing, the empty library explains itself through the hero text.
            GamesStatus.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GamesStatus.Text = EmptyGridText();

            LoadCovers();

            if (IsVisible && IsGridPage(currentPage) && !IsModalOpen)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!FocusTile(focusId))
                    {
                        gamesScroller.ScrollToStart();
                        UiNavigator.FocusFirstIn(GamesList);
                    }
                }, DispatcherPriority.Loaded);
            }
        }

        private string EmptyGridText()
        {
            if (!scanCompleted)
            {
                return "Scanning for games...";
            }

            if (filterKey != FilterAll)
            {
                return "Nothing here. Press View to change the filter.";
            }

            if (tilesPage == OverlayPage.Games)
            {
                return "No games found yet. Add some from Customize > Library.";
            }

            return SteamWebApi.KeyFrom(Config) == null
                ? "Steam games you own but haven't installed show up here.\nConnect your Steam account in Customize > Steam."
                : "Nothing to install right now.\nSteam: " + (host?.SteamStatus ?? "");
        }

        /// <summary>Games page: installed games. Library page: owned but not installed.</summary>
        private bool OnShownPage(Game game) => game.IsInstalled == (tilesPage == OverlayPage.Games);

        /// <summary>Points the shared grid at Games or Library, restoring that page's own filter and focus.</summary>
        private void ShowTilesFor(OverlayPage page)
        {
            if (page == tilesPage)
            {
                return;
            }

            gridState[tilesPage] = (filterKey, lastFocusedGameId);
            tilesPage = page;
            (filterKey, lastFocusedGameId) = gridState.TryGetValue(page, out (string filter, string? focus) state) ? state : (FilterAll, null);

            BuildFilterChips();
            RebuildTiles(lastFocusedGameId);
            UpdateHero(null, force: true); // replaced as soon as a tile takes focus
        }

        /// <summary>The Library tab is only there when there's something it could show.</summary>
        private void UpdateLibraryTab()
        {
            bool available = steamInstalled || allGames.Any(g => !g.IsInstalled);
            LibraryTab.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

            if (!available && currentPage == OverlayPage.Library)
            {
                ShowPage(OverlayPage.Games, focus: IsVisible, direction: -1, animate: false);
            }
        }

        private IEnumerable<Game> FilterAndSort(IEnumerable<Game> games)
        {
            games = games.Where(OnShownPage);
            games = filterKey switch
            {
                FilterAll => games,
                FilterFavorites => games.Where(g => IsFavorite(g.Id)),
                _ => games.Where(g => g.SourceLabel == filterKey)
            };

            // Library: downloads in progress first, so the game you just started installing is easy to find.
            IOrderedEnumerable<Game> ordered = games.OrderBy(g => tilesPage == OverlayPage.Library && IsDownloading(g) ? 0 : 1);
            if (Config.FavoritesFirst)
            {
                ordered = ordered.ThenBy(g => IsFavorite(g.Id) ? 0 : 1);
            }

            return Config.Sort switch
            {
                SortMode.Alphabetical => ordered.ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
                SortMode.Store => ordered.ThenBy(g => g.SourceLabel).ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => ordered.ThenByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            };
        }

        private void BuildFilterChips()
        {
            List<Game> pageGames = allGames.Where(OnShownPage).ToList();

            filterChips.Clear();
            filterChips.Add(new FilterChipViewModel(FilterAll, "All games", pageGames.Count));

            int favorites = pageGames.Count(g => IsFavorite(g.Id));
            if (favorites > 0)
            {
                filterChips.Add(new FilterChipViewModel(FilterFavorites, "Favorites", favorites));
            }

            foreach (IGrouping<string, Game> group in pageGames.GroupBy(g => g.SourceLabel).OrderByDescending(g => g.Count()))
            {
                filterChips.Add(new FilterChipViewModel(group.Key, group.Key, group.Count()));
            }

            if (filterChips.All(chip => chip.Key != filterKey))
            {
                filterKey = FilterAll;
            }

            foreach (FilterChipViewModel chip in filterChips)
            {
                chip.IsActive = chip.Key == filterKey;
            }

            FilterBar.Visibility = filterChips.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CycleFilter()
        {
            if (filterChips.Count < 2)
            {
                return;
            }

            int index = filterChips.ToList().FindIndex(chip => chip.Key == filterKey);
            filterKey = filterChips[(index + 1) % filterChips.Count].Key;
            foreach (FilterChipViewModel chip in filterChips)
            {
                chip.IsActive = chip.Key == filterKey;
            }

            RebuildTiles(null);
            Dispatcher.BeginInvoke(CascadeTiles, DispatcherPriority.Loaded);
        }

        /// <summary>Visible tiles fly in one after another.</summary>
        private void CascadeTiles()
        {
            if (!ThemeManager.Current.CascadeEntrance || !ThemeManager.AnimationsEnabled
                || ThemeManager.EffectiveQuality == GraphicsQuality.Performance)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                int order = 0;
                double viewportHeight = GamesScroll.ViewportHeight;
                double viewportWidth = GamesScroll.ViewportWidth;

                for (int i = 0; i < tiles.Count; i++)
                {
                    if (GamesList.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement container)
                    {
                        continue;
                    }

                    Point position;
                    try
                    {
                        position = container.TranslatePoint(new Point(0, 0), GamesScroll);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    bool onScreen = position.Y < viewportHeight && position.Y > -ThemeManager.TileHeight
                        && position.X < viewportWidth && position.X > -ThemeManager.TileWidth;
                    if (!onScreen)
                    {
                        container.Opacity = 1;
                        continue;
                    }

                    Motion.Entrance(container, Math.Min(order, 16) * 24, offsetY: 30, fromScale: 0.96, ms: 420);
                    order++;
                }
            }, DispatcherPriority.Loaded);
        }

        private bool FocusTile(string? id)
        {
            if (id == null || !tileMap.TryGetValue(id, out GameTileViewModel? vm) || !tiles.Contains(vm))
            {
                return false;
            }

            if (GamesList.ItemContainerGenerator.ContainerFromItem(vm) is not DependencyObject container
                || FindDescendant<Button>(container) is not Button button)
            {
                return false;
            }

            Keyboard.Focus(button);
            button.BringIntoView();
            return true;
        }

        private GameTileViewModel? FocusedTile() =>
            FocusedItem<GameTileViewModel>() ?? (lastFocusedGameId != null && tileMap.TryGetValue(lastFocusedGameId, out GameTileViewModel? vm) ? vm : null);

        private void OnLibraryFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is not FrameworkElement element)
            {
                return;
            }

            if (element.DataContext is GameTileViewModel vm)
            {
                lastFocusedGameId = vm.Id;
                UpdateHero(vm);
                ScheduleBackdrop(vm);
            }
            else if (element.DataContext is AddTileViewModel)
            {
                UpdateHero(null);
            }
        }

        private int CoverDecodeWidth()
        {
            AppearanceSettings a = ThemeManager.Current;
            double scale = ImageLoader.DpiScale(this) * Math.Clamp(a.UiScale, 60, 200) / 100.0 * Math.Max(1, a.FocusZoom / 100.0);
            return (int)Math.Ceiling(ThemeManager.TileWidth * scale);
        }

        private void LoadCovers()
        {
            int width = CoverDecodeWidth();
            foreach (GameTileViewModel vm in tiles.OfType<GameTileViewModel>())
            {
                _ = vm.LoadCoverAsync(width);
            }
        }

        /// <summary>Debounced: tile-size sliders fire many times a second.</summary>
        private void ReloadCovers()
        {
            coverReloadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
            {
                coverReloadTimer!.Stop();
                LoadCovers();
                UpdatePreview();
            }, Dispatcher);

            coverReloadTimer.Stop();
            coverReloadTimer.Start();
        }

        // =====================================================================
        // Hero: the focused game's name, big
        // =====================================================================

        private void UpdateHero(GameTileViewModel? vm, bool force = false)
        {
            string title = vm?.Title ?? "Add a game";
            string source = vm?.SourceLabel ?? "New";
            string meta = vm == null
                ? tilesPage == OverlayPage.Library ? "Owned, not installed. A installs it." : "Pick an .exe or a whole folder. Cover art is found automatically."
                : StateText(vm) + Played(vm.Game.LastPlayed) + (vm.IsFavorite ? "   ·   Favorite" : "");

            // Same game, new details (download progress ticking): just the text, no entrance animation.
            if (!force && HeroTitle.Text == title && HeroSource.Text == source.ToUpperInvariant())
            {
                HeroMeta.Text = meta;
                return;
            }

            HeroTitle.Text = title;
            HeroSource.Text = source.ToUpperInvariant();
            HeroMeta.Text = meta;

            if (ThemeManager.Current.ShowHero)
            {
                Motion.Entrance(HeroPanel, 0, offsetX: 0, offsetY: 14, ms: 380);
            }
        }

        /// <summary>"Downloading 45%   ·   ", "Not installed   ·   " or nothing (installed, not updating).</summary>
        private static string StateText(GameTileViewModel vm) =>
            vm.IsDownloading ? $"{vm.DownloadLabel} {vm.DownloadPercent}   ·   "
            : vm.Game.IsInstalled ? ""
            : "Not installed   ·   ";

        private static string Played(DateTime? when)
        {
            if (when is not DateTime time)
            {
                return "Not played yet";
            }

            if (time.Date == DateTime.Today)
            {
                return "Played today";
            }

            if (time.Date == DateTime.Today.AddDays(-1))
            {
                return "Played yesterday";
            }

            int days = (int)(DateTime.Today - time.Date).TotalDays;
            return days < 45 ? $"Played {days} days ago" : $"Last played {time:MMM d, yyyy}";
        }

        private string? CoverHint(Game game) =>
            game.CoverImage == null && Config.AutoFetchCovers ? "Looking for cover art..." : null;

        // =====================================================================
        // Backdrop: the focused game's art behind everything
        // =====================================================================

        /// <summary>How a piece of art is placed behind the UI.</summary>
        private enum ArtLayout
        {
            Banner,  // wide hero art across the top at its real aspect, fading into the background
            Poster,  // portrait cover full-height on the right, fading to the left
            Wash,    // the cover reduced to soft colour (intentionally blurry)
            Fill,    // custom image filling the screen
            None
        }

        // Fades for banner (bottom) and poster (left). Relative to the image itself, so they fit any size.
        private static readonly Brush BannerMask = FrozenMask(new Point(0, 0), new Point(0, 1), (1, 0), (1, 0.5), (0, 1));
        private static readonly Brush PosterMask = FrozenMask(new Point(0, 0), new Point(1, 0), (0, 0), (1, 0.45), (1, 1));

        private static Brush FrozenMask(Point start, Point end, params (double alpha, double offset)[] stops)
        {
            LinearGradientBrush brush = new() { StartPoint = start, EndPoint = end };
            foreach ((double alpha, double offset) in stops)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 255), 0, 0, 0), offset));
            }

            brush.Freeze();
            return brush;
        }

        private void ScheduleBackdrop(GameTileViewModel vm)
        {
            backdropPending = vm;
            backdropTimer.Stop();
            backdropTimer.Start();
        }

        private async Task ShowBackdropAsync(GameTileViewModel? vm)
        {
            AppearanceSettings a = ThemeManager.Current;
            if (vm == null || a.Backdrop is BackdropMode.Solid or BackdropMode.CustomImage || !IsGridPage(currentPage))
            {
                return;
            }

            // Banner wants the wide hero art; a game without one shows its cover as a poster instead.
            ArtLayout layout = a.Backdrop switch
            {
                BackdropMode.HeroArt when IsLocalImage(vm.Game.HeroImage) => ArtLayout.Banner,
                BackdropMode.BlurredCover => ArtLayout.Wash,
                _ => ArtLayout.Poster
            };
            string? path = layout == ArtLayout.Banner ? vm.Game.HeroImage : vm.Game.CoverImage;
            if (!IsLocalImage(path))
            {
                layout = ArtLayout.None;
                path = null;
            }

            string key = $"{layout}|{path}|{a.BackdropBlur}|{ThemeManager.EffectiveQuality}";
            if (key == backdropKey)
            {
                return;
            }

            backdropKey = key;
            int version = ++backdropVersion;

            // Decode at the size it's shown (capped by quality: huge textures make every change hitch).
            (int width, int height) = layout switch
            {
                ArtLayout.Banner => (Math.Min(ScreenPixels().width, ThemeManager.MaxBackdropPixels), 0),
                ArtLayout.Poster => (0, Math.Min(ScreenPixels().height, ThemeManager.MaxBackdropPixels)),
                ArtLayout.Wash => (64, 0), // tiny: the GPU stretch does most of the blurring for free
                _ => (0, 0)
            };

            BitmapSource? image = path == null ? null : await ImageLoader.LoadAsync(path, width, height);
            if (version != backdropVersion)
            {
                return; // focus moved on while decoding
            }

            SwapBackdrop(image, layout, BlurFor(layout));
        }

        private static double BlurFor(ArtLayout layout)
        {
            bool cheap = ThemeManager.EffectiveQuality == GraphicsQuality.Performance;
            return layout switch
            {
                ArtLayout.Wash => cheap ? 0 : 36,
                ArtLayout.None => 0,
                _ => cheap ? 0 : Math.Clamp(ThemeManager.Current.BackdropBlur, 0, 100) * 0.3
            };
        }

        /// <summary>For pages other than Games, and for the custom-image / solid modes.</summary>
        private void ShowNeutralBackdrop()
        {
            AppearanceSettings a = ThemeManager.Current;
            if (a.Backdrop == BackdropMode.Solid)
            {
                if (backdropKey != "solid")
                {
                    backdropKey = "solid";
                    SwapBackdrop(null, ArtLayout.None, 0);
                }
            }
            else if (a.Backdrop == BackdropMode.CustomImage)
            {
                _ = ShowCustomBackdropAsync();
            }
        }

        private async Task ShowCustomBackdropAsync()
        {
            AppearanceSettings a = ThemeManager.Current;
            string key = $"custom|{a.CustomBackgroundImage}|{a.BackdropBlur}|{ThemeManager.EffectiveQuality}";
            if (key == backdropKey)
            {
                return;
            }

            backdropKey = key;
            int version = ++backdropVersion;
            BitmapSource? image = await ImageLoader.LoadAsync(a.CustomBackgroundImage,
                Math.Min(ScreenPixels().width, ThemeManager.MaxBackdropPixels), 0);
            if (version == backdropVersion)
            {
                SwapBackdrop(image, ArtLayout.Fill, BlurFor(ArtLayout.Fill));
            }
        }

        private void SwapBackdrop(ImageSource? image, ArtLayout layout, double blur)
        {
            Grid incoming = backdropAIsActive ? BackdropLayerB : BackdropLayerA;
            Grid outgoing = backdropAIsActive ? BackdropLayerA : BackdropLayerB;
            Image art = backdropAIsActive ? BackdropImageB : BackdropImageA;
            backdropAIsActive = !backdropAIsActive;

            art.Source = image;
            bool blurred = blur > 0.5;
            double bleed = blurred ? blur * 1.5 : 0; // push the blur's soft edges off screen

            switch (layout)
            {
                case ArtLayout.Banner:
                    // Real aspect, full width, top aligned: nothing is cropped or zoomed.
                    bool wideEnough = image is BitmapSource b && b.PixelWidth / (double)b.PixelHeight >= ActualWidth / Math.Max(1, ActualHeight);
                    art.Stretch = wideEnough ? Stretch.Uniform : Stretch.UniformToFill;
                    art.HorizontalAlignment = HorizontalAlignment.Stretch;
                    art.VerticalAlignment = VerticalAlignment.Top;
                    art.OpacityMask = BannerMask;
                    art.Margin = new Thickness(-bleed, -bleed, -bleed, 0);
                    break;
                case ArtLayout.Poster:
                    art.Stretch = Stretch.Uniform;
                    art.HorizontalAlignment = HorizontalAlignment.Right;
                    art.VerticalAlignment = VerticalAlignment.Stretch;
                    art.OpacityMask = PosterMask;
                    art.Margin = new Thickness(0, -bleed, -bleed, -bleed);
                    break;
                default:
                    art.Stretch = Stretch.UniformToFill;
                    art.HorizontalAlignment = HorizontalAlignment.Stretch;
                    art.VerticalAlignment = VerticalAlignment.Stretch;
                    art.OpacityMask = null;
                    art.Margin = new Thickness(-bleed);
                    break;
            }

            art.Effect = blurred ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } : null;

            // The layer becomes one cached bitmap: mask and blur are rendered once, the crossfade is free.
            incoming.CacheMode = new BitmapCache(blurred || layout == ArtLayout.Wash ? 0.5 : 1) { SnapsToDevicePixels = true };

            Panel.SetZIndex(incoming, 1);
            Panel.SetZIndex(outgoing, 0);

            bool cheap = ThemeManager.EffectiveQuality == GraphicsQuality.Performance;
            double fade = cheap ? 200 : 420;
            Motion.To(incoming, OpacityProperty, image == null ? 0 : 1, fade, Motion.EaseOut, from: 0);
            Motion.To(outgoing, OpacityProperty, 0, fade, Motion.EaseInOut, delayMs: 60, completed: () =>
            {
                // Release the old art (and its cache) once it's invisible.
                if (outgoing.Opacity <= 0.01)
                {
                    ((Image)outgoing.Children[0]).Source = null;
                    outgoing.CacheMode = null;
                }
            });

            // A small settle instead of the old zoom: the art stays at its real size.
            (_, TranslateTransform move) = Motion.Transforms(incoming);
            if (!cheap && image != null)
            {
                Motion.To(move, TranslateTransform.YProperty, 0, 600, Motion.Gentle, from: layout == ArtLayout.Banner ? -14 : 0);
                Motion.To(move, TranslateTransform.XProperty, 0, 600, Motion.Gentle, from: layout == ArtLayout.Poster ? 24 : 0);
            }
            else
            {
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.X = move.Y = 0;
            }
        }

        private static bool IsLocalImage(string? path) =>
            !string.IsNullOrEmpty(path) && !path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

        private (int width, int height) ScreenPixels()
        {
            double scale = ImageLoader.DpiScale(this);
            double width = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
            double height = ActualHeight > 0 ? ActualHeight : SystemParameters.PrimaryScreenHeight;
            return ((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
        }

        // =====================================================================
        // Ambient aurora drift
        // =====================================================================

        private void StartAmbient()
        {
            StopAmbient();
            GraphicsQuality quality = ThemeManager.EffectiveQuality;
            if (!ThemeManager.Current.AmbientMotion || !ThemeManager.AnimationsEnabled
                || ThemeManager.Current.AuroraIntensity == 0 || quality == GraphicsQuality.Performance)
            {
                return;
            }

            // The blobs are huge and move slowly: 20-30 fps looks identical to 60 and costs a third.
            int fps = quality == GraphicsQuality.Quality ? 30 : 20;
            Drift(Blob1, 260, 160, 19, 23, fps);
            Drift(Blob2, 220, 180, 25, 17, fps);
            Drift(Blob3, 300, 120, 29, 21, fps);
        }

        private void StopAmbient()
        {
            foreach (UIElement blob in new UIElement[] { Blob1, Blob2, Blob3 })
            {
                (ScaleTransform scale, TranslateTransform move) = Motion.Transforms(blob);
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }

        private static void Drift(UIElement blob, double dx, double dy, double secondsX, double secondsY, int fps)
        {
            (ScaleTransform scale, TranslateTransform move) = Motion.Transforms(blob);

            DoubleAnimation Loop(double from, double to, double seconds)
            {
                DoubleAnimation loop = new(from, to, new Duration(TimeSpan.FromSeconds(seconds)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = Motion.EaseInOut
                };
                Timeline.SetDesiredFrameRate(loop, fps);
                return loop;
            }

            move.BeginAnimation(TranslateTransform.XProperty, Loop(-dx / 2, dx / 2, secondsX));
            move.BeginAnimation(TranslateTransform.YProperty, Loop(-dy / 2, dy / 2, secondsY));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(0.9, 1.12, secondsY * 1.3));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(0.9, 1.12, secondsY * 1.3));
        }

        // =====================================================================
        // Per-game actions
        // =====================================================================

        private void OnGameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: GameTileViewModel vm })
            {
                if (vm.Game.IsInstalled)
                {
                    LaunchRequested?.Invoke(this, vm.Game); // App plays the launch sound
                }
                else if (vm.IsDownloading)
                {
                    DownloadsRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    RequestInstall(vm.Game);
                }
            }
        }

        private void RequestInstall(Game game)
        {
            if (game.InstallTarget == null)
            {
                ShowToast("\uE946", "Can't install this from here", $"Install it in {game.SourceLabel}.", Sfx.Error);
                return;
            }

            InstallRequested?.Invoke(this, game); // App hides the overlay so the store's install window is in front
        }

        private void OnAddTileClicked(object sender, RoutedEventArgs e) => ShowAddMenu();

        private void ToggleFavorite(GameTileViewModel vm)
        {
            GameOverride entry = Config.OverrideFor(vm.Id);
            entry.Favorite = !entry.Favorite;
            vm.IsFavorite = entry.Favorite;
            host?.SaveConfig();

            Sound(entry.Favorite ? Sfx.Favorite : Sfx.ChangeDown);
            ShowToast(entry.Favorite ? "\uE735" : "\uE734", entry.Favorite ? "Added to favorites" : "Removed from favorites", vm.Title);
            BuildFilterChips();
            UpdateHero(vm, force: true);

            if (filterKey == FilterFavorites && !entry.Favorite)
            {
                RebuildTiles(null);
            }
        }

        private void ShowGameMenu(GameTileViewModel vm)
        {
            Game game = vm.Game;
            GameOverride? custom = Config.GameOverrides.TryGetValue(game.Id, out GameOverride? o) ? o : null;
            ModalItem primary = game.IsInstalled
                ? new("\uE768", "Play", () =>
                {
                    CloseModal(restoreFocus: false);
                    LaunchRequested?.Invoke(this, game);
                })
                : vm.IsDownloading
                    ? new("\uE896", "Show in Steam downloads", () =>
                    {
                        CloseModal(restoreFocus: false);
                        DownloadsRequested?.Invoke(this, EventArgs.Empty);
                    }, "Pause or cancel it there")
                    : new("\uE896", "Install", () =>
                    {
                        CloseModal(restoreFocus: false);
                        RequestInstall(game);
                    }, $"{game.SourceLabel} asks where to install it");
            List<ModalItem> items = new()
            {
                primary,
                new(vm.IsFavorite ? "\uE735" : "\uE734", vm.IsFavorite ? "Remove from favorites" : "Add to favorites", () =>
                {
                    CloseModal();
                    ToggleFavorite(vm);
                })
            };

            if (host != null)
            {
                (int index, int count, string? matched) = host.Covers.CandidateInfo(game.Id);
                string detail = count > 1
                    ? $"Showing option {index + 1} of {count}" + (matched != null ? $"  ·  matched \"{matched}\"" : "")
                    : "Searches the web for other cover art";

                items.Add(new ModalItem("\uE8B9", "Try a different cover", () => _ = NextCoverAsync(vm), detail));
                items.Add(new ModalItem("\uE721", "Search the web again", () =>
                {
                    CloseModal();
                    host.Covers.Refetch(game);
                    ShowToast("\uE721", "Searching for " + game.Title, "The tile updates as soon as art is found.");
                }, "Forget the current match and look it up from scratch"));
            }

            items.Add(new ModalItem("\uEB9F", "Use my own image...", () =>
                OpenBrowser(BrowseMode.Image, null, path => SetCustomCover(vm, path)), "Pick a .jpg / .png from this PC"));

            if (!string.IsNullOrWhiteSpace(custom?.CoverImage))
            {
                items.Add(new ModalItem("\uE7A7", "Go back to the automatic cover", () =>
                {
                    custom!.CoverImage = null;
                    host?.SaveConfig();
                    CloseModal();
                    host?.Rescan();
                }));
            }

            bool isLocalExe = game.Source is GameSource.Folder or GameSource.Custom
                && game.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (isLocalExe)
            {
                items.Add(new ModalItem("\uE7AC", "Change which .exe starts it", () =>
                    OpenBrowser(BrowseMode.GameExe, game.InstallDir, path => SetExecutable(vm, path)), game.LaunchTarget));
            }

            if (!string.IsNullOrEmpty(game.InstallDir) && Directory.Exists(game.InstallDir))
            {
                items.Add(new ModalItem("\uE8B7", "Open install folder", () =>
                {
                    CloseModal(restoreFocus: false);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{game.InstallDir}\"") { UseShellExecute = true });
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                }, game.InstallDir));
            }

            if (game.Source == GameSource.Custom)
            {
                items.Add(new ModalItem("\uE74D", "Remove from library", () =>
                    Confirm("\uE74D", $"Remove {game.Title}?", "Only removes it from CouchDeck. Nothing is deleted from disk.", "Remove", () =>
                    {
                        Config.CustomGames.RemoveAll(c => string.Equals(c.Path, game.LaunchTarget, StringComparison.OrdinalIgnoreCase));
                        host?.SaveConfig();
                        host?.Rescan();
                    }), isDanger: true));
            }

            if (game.UninstallTarget != null)
            {
                items.Add(new ModalItem("\uE74D", "Uninstall", () =>
                {
                    CloseModal(restoreFocus: false);
                    UninstallRequested?.Invoke(this, game);
                }, $"{game.SourceLabel} asks you to confirm. It stays in Library to install again", isDanger: true));
            }

            items.Add(new ModalItem("\uED1A", "Hide from library", () =>
                Confirm("\uED1A", $"Hide {game.Title}?", "Bring it back any time from Customize > Library > Unhide.", "Hide", () => HideGame(vm)),
                "Nothing is deleted", isDanger: true));

            ShowModal("\uE7FC", game.Title, $"{game.SourceLabel}   ·   {StateText(vm)}{Played(game.LastPlayed)}", items);
        }

        private async Task NextCoverAsync(GameTileViewModel vm)
        {
            CloseModal();
            if (host == null)
            {
                return;
            }

            ShowToast("\uE8B9", "Finding another cover...", vm.Title);
            try
            {
                bool found = await host.Covers.NextCandidateAsync(vm.Game);
                (int index, int count, string? matched) = host.Covers.CandidateInfo(vm.Id);
                if (found)
                {
                    ShowToast("\uE8B9", $"Cover {index + 1} of {count}", matched);
                }
                else
                {
                    ShowToast("\uE783", "No other covers found", "Use \"Use my own image\" to pick one from this PC.", Sfx.Error);
                }
            }
            catch (Exception ex)
            {
                ShowToast("\uE783", "Cover search failed", ex.Message);
            }
        }

        private void SetCustomCover(GameTileViewModel vm, string path)
        {
            Config.OverrideFor(vm.Id).CoverImage = path;
            host?.SaveConfig();
            ApplyArt(new ArtUpdate(vm.Id, path, null, null));
            ShowToast("\uEB9F", "Cover updated", vm.Title);
        }

        private void SetExecutable(GameTileViewModel vm, string path)
        {
            Config.OverrideFor(vm.Id).LaunchTarget = path;
            host?.SaveConfig();

            int index = allGames.FindIndex(g => g.Id == vm.Id);
            if (index >= 0)
            {
                allGames[index] = allGames[index] with { LaunchTarget = path };
                vm.Game = allGames[index];
            }

            ShowToast("\uE7AC", "Executable changed", Path.GetFileName(path));
        }

        private void HideGame(GameTileViewModel vm)
        {
            int position = tiles.IndexOf(vm);
            string? next = tiles.Skip(position + 1).OfType<GameTileViewModel>().FirstOrDefault()?.Id
                ?? tiles.Take(Math.Max(0, position)).OfType<GameTileViewModel>().LastOrDefault()?.Id;

            Config.HiddenGameIds.Add(vm.Id);
            host?.SaveConfig();

            allGames.RemoveAll(g => g.Id == vm.Id);
            knownGameIds.Remove(vm.Id);
            tileMap.Remove(vm.Id);
            lastFocusedGameId = next;

            BuildFilterChips();
            RebuildTiles(next);
            ShowToast("\uED1A", "Hidden: " + vm.Title, "Customize > Library > Unhide brings it back.");
        }

        // =====================================================================
        // Adding games
        // =====================================================================

        private void ShowAddMenu()
        {
            ShowModal("\uE710", "Add games", "Anything you add gets cover art from the web automatically.", new List<ModalItem>
            {
                new("\uE7FC", "Add a single game", () => OpenBrowser(BrowseMode.GameExe, lastBrowsePath, AddGameFromPath),
                    "Pick its .exe (or a shortcut)"),
                new("\uE8B7", "Add a games folder", () => OpenBrowser(BrowseMode.GamesFolder, lastBrowsePath, AddGamesFolder),
                    "Every sub-folder becomes a game, including ones you add later"),
                new("\uE72C", "Rescan library", () =>
                {
                    CloseModal();
                    RequestRescan();
                }, "Picks up newly installed games")
            });
        }

        /// <summary>Controller-friendly file picker inside a modal. A opens, B goes up a level.</summary>
        private void OpenBrowser(BrowseMode mode, string? start, Action<string> picked)
        {
            FileBrowser browser = new(mode, start);
            string glyph = mode switch
            {
                BrowseMode.Image => "\uEB9F",
                BrowseMode.GamesFolder => "\uE8B7",
                _ => "\uE7FC"
            };

            void Render(string? focusPath = null)
            {
                List<BrowserEntry> entries = browser.Entries();
                List<ModalItem> items = entries
                    .Select(entry => new ModalItem(entry.Glyph, entry.Label, () => Select(entry), entry.Detail))
                    .ToList();

                int focus = focusPath == null ? -1 : entries.FindIndex(e => string.Equals(e.Path, focusPath, StringComparison.OrdinalIgnoreCase));
                if (focus < 0)
                {
                    // Skip "Up one level" so A immediately opens the first folder.
                    focus = entries.FindIndex(e => e.Kind is BrowserEntryKind.Folder or BrowserEntryKind.File or BrowserEntryKind.Drive);
                }

                string location = browser.CurrentPath ?? "This PC";
                ShowModal(glyph, browser.Title, location + "\n" + browser.Hint, items, Back, Math.Max(0, focus));
            }

            void Back()
            {
                Sound(Sfx.Back);
                string? from = browser.CurrentPath;
                if (!browser.GoUp())
                {
                    CloseModal();
                    return;
                }

                Render(from);
            }

            void Select(BrowserEntry entry)
            {
                switch (entry.Kind)
                {
                    case BrowserEntryKind.Up:
                        Back();
                        break;
                    case BrowserEntryKind.UseFolder:
                        lastBrowsePath = Directory.GetParent(entry.Path.TrimEnd('\\'))?.FullName ?? entry.Path;
                        CloseModal();
                        picked(entry.Path);
                        break;
                    case BrowserEntryKind.Drive:
                    case BrowserEntryKind.Folder:
                        browser.Open(entry.Path);
                        Render();
                        break;
                    case BrowserEntryKind.File:
                        lastBrowsePath = browser.CurrentPath;
                        CloseModal();
                        picked(entry.Path);
                        break;
                }
            }

            Render();
        }

        private void AddGameFromPath(string path)
        {
            if (Config.CustomGames.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast("\uE946", "Already in your library", Path.GetFileName(path));
                return;
            }

            string title = GuessTitle(path);
            Config.CustomGames.Add(new CustomGameEntry { Title = title, Path = path, AutoTitle = true });
            host?.SaveConfig();

            lastFocusedGameId = $"custom:{title.ToLowerInvariant()}";
            filterKey = FilterAll;
            host?.Rescan();
        }

        private void AddGamesFolder(string path)
        {
            if (Config.GameFolders.Any(f => string.Equals(f.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast("\uE946", "That folder is already scanned", path);
                return;
            }

            Config.GameFolders.Add(path);
            host?.SaveConfig();
            ShowToast("\uE8B7", "Scanning " + Path.GetFileName(path.TrimEnd('\\')), "New games appear with cover art as they're found.");
            host?.Rescan();
            RebuildSettings();
        }

        private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "bin32", "binaries", "win64", "win32", "x64", "x86", "game", "games", "content", "release",
            "shipping", "retail", "data", "app", "client", "common", "windows", "windowsnoeditor", "build", "launcher",
            "program files", "program files (x86)", "desktop", "downloads", "documents", "system", "exe", "executable"
        };

        /// <summary>D:\Games\Elden.Ring-CODEX\Game\eldenring.exe -> "Elden Ring" (skips generic folder names).</summary>
        public static string GuessTitle(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string fileTitle = Path.GetFileNameWithoutExtension(path)
                .Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase)
                .Replace("-Shipping", "", StringComparison.OrdinalIgnoreCase);

            if (extension is ".lnk" or ".url" or ".bat" or ".cmd")
            {
                return TitleCleaner.Clean(fileTitle);
            }

            // Ancestor folders, nearest first, excluding the drive root.
            List<DirectoryInfo> ancestors = new();
            for (DirectoryInfo? d = Path.GetDirectoryName(path) is string dir ? new DirectoryInfo(dir) : null;
                 d?.Parent != null && ancestors.Count < 6;
                 d = d.Parent)
            {
                ancestors.Add(d);
            }

            // 1. A folder named like the exe: "Hades II v0.9 [DODI]\Ship\Hades2.exe" -> "Hades II"
            (DirectoryInfo? folder, double score) best = ancestors
                .Where(d => !GenericFolders.Contains(d.Name))
                .Select(d => ((DirectoryInfo?)d, TitleCleaner.Similarity(TitleCleaner.Clean(d.Name), fileTitle)))
                .OrderByDescending(x => x.Item2)
                .FirstOrDefault();
            if (best.folder != null && best.score >= 0.6)
            {
                return TitleCleaner.Clean(best.folder.Name);
            }

            // 2. Unreal layout: <Game>\<Project>\Binaries\Win64\Game-Win64-Shipping.exe -> <Game>
            int binaries = ancestors.FindIndex(d => d.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase));
            if (binaries >= 0 && binaries + 2 < ancestors.Count)
            {
                return TitleCleaner.Clean(ancestors[binaries + 2].Name);
            }

            // 3. Nearest folder that isn't "bin", "Game", "x64"...
            DirectoryInfo? named = ancestors.FirstOrDefault(d => !GenericFolders.Contains(d.Name) && d.Name.Length > 1);
            return TitleCleaner.Clean(named?.Name ?? fileTitle);
        }
    }
}
