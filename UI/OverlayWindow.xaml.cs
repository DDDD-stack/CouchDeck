using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CouchDeck.Audio;
using CouchDeck.Config;
using CouchDeck.Games;
using CouchDeck.Input;
using CouchDeck.WindowsControl;

namespace CouchDeck.UI
{
    public enum OverlayPage
    {
        Games,
        Library, // owned but not installed; same grid as Games
        Windows,
        System,
        Customize
    }

    /// <summary>
    /// The full-screen couch UI. Displays things and reports what the user picked (via events);
    /// App.xaml.cs decides what actually happens (focus juggling, launching, hiding).
    /// Library edits and the Customize page go through <see cref="IOverlayHost"/>.
    ///
    /// Split over three files:
    ///   OverlayWindow.xaml.cs            pages, input, dialogs, toasts (this file)
    ///   OverlayWindow.Library.cs         game tiles (Games + Library pages), filters, hero, backdrop art,
    ///                                    per-game menu, the "new achievements" summary
    ///   OverlayWindow.Customize.cs       Customize page and applying appearance settings
    ///
    /// Keyboard works too, for testing at the desk: arrows, Enter, Esc, PageUp/PageDown, X, Y, Tab, F1.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private readonly ObservableCollection<WindowRowViewModel> openWindows = new();
        private readonly ObservableCollection<HintViewModel> hints = new();
        private bool playStationButtons;
        private readonly DispatcherTimer clockTimer;

        private IOverlayHost? host;
        private OverlayPage currentPage = OverlayPage.Games;
        private OpenWindow? nowPlaying;
        private bool tabsPositioned;

        // Modal state
        private Action? modalBack;
        private IInputElement? focusBeforeModal;

        // Smooth scrolling for every list
        private readonly SmoothScroller gamesScroller;
        private readonly SmoothScroller windowsScroller;
        private readonly SmoothScroller systemScroller;
        private readonly SmoothScroller settingsScroller;
        private readonly SmoothScroller modalScroller;

        public event EventHandler? ResumeRequested;
        public event EventHandler<IntPtr>? SwitchToWindowRequested;
        public event EventHandler<Game>? LaunchRequested;
        public event EventHandler<Game>? InstallRequested;
        public event EventHandler<Game>? UninstallRequested;

        /// <summary>A game Steam is already downloading was picked: show Steam's downloads page (pause / cancel).</summary>
        public event EventHandler? DownloadsRequested;

        /// <summary>Hide without giving focus back: something we just opened (a browser) should stay in front.</summary>
        public event EventHandler? HideRequested;

        public event EventHandler? MouseModeToggleRequested;
        public event EventHandler? RescanRequested;
        public event EventHandler? ExitRequested;

        public OverlayWindow()
        {
            InitializeComponent();

            GamesList.ItemsSource = tiles;
            WindowsList.ItemsSource = openWindows;
            HintsBar.ItemsSource = hints;
            FilterBar.ItemsSource = filterChips;

            gamesScroller = SmoothScroller.Attach(GamesScroll, GamesList);
            windowsScroller = SmoothScroller.Attach(WindowsScroll, WindowsList);
            systemScroller = SmoothScroller.Attach(SystemPage, SystemContent);
            settingsScroller = SmoothScroller.Attach(SettingsScroll, SettingsList);
            modalScroller = SmoothScroller.Attach(ModalScroll, ModalItems);
            modalScroller.EdgePadding = 20;

            GamesList.GotKeyboardFocus += OnLibraryFocus;

            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            clockTimer.Tick += (_, _) => UpdateClock();

            backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            backdropTimer.Tick += (_, _) =>
            {
                backdropTimer.Stop();
                _ = ShowBackdropAsync(backdropPending);
            };

            PreviewKeyDown += OnPreviewKeyDown;
            DpiChanged += (_, _) => ReloadCovers();
            TabStrip.SizeChanged += (_, _) => PositionTabIndicator(animate: false);

            ShowPage(OverlayPage.Games, focus: false, direction: 0);
        }

        /// <summary>Called once by App after construction.</summary>
        public void Initialize(IOverlayHost overlayHost)
        {
            host = overlayHost;
            BuildSettings();
            ApplyAppearance();
            UpdateLibraryTab();
        }

        private AppConfig Config => host?.Config ?? fallbackConfig;
        private readonly AppConfig fallbackConfig = new();

        public bool IsModalOpen => ModalLayer.Visibility == Visibility.Visible;

        // =====================================================================
        // Called by App
        // =====================================================================

        /// <summary>Call right after the window becomes visible and has focus.</summary>
        public void OnOpened(OpenWindow? previousApp)
        {
            nowPlaying = previousApp;
            NowPlayingBar.Visibility = previousApp != null ? Visibility.Visible : Visibility.Collapsed;
            NowPlayingTitle.Text = previousApp?.Title ?? string.Empty;

            CloseModal(restoreFocus: false);
            UpdateClock();
            clockTimer.Start();
            StartAmbient();

            if (currentPage == OverlayPage.Windows)
            {
                RefreshOpenWindows();
            }

            // Whole UI eases in: fade + a slight zoom-out, then the tiles cascade.
            Motion.Entrance(UiRoot, 0, offsetY: 0, fromScale: 1.02, ms: 320);
            Motion.To(Backdrop, OpacityProperty, 1, 380, Motion.EaseOut, from: 0);
            LoadCovers(); // no-op unless the screen (DPI) changed

            ShowPage(currentPage, focus: true, direction: 0, animate: false);
            if (IsGridPage(currentPage))
            {
                CascadeTiles();
            }

            PositionTabIndicator(animate: false);

            if (NowPlayingBar.Visibility == Visibility.Visible)
            {
                PulseLiveDot();
            }

            UpdateArtSpinner();

            if (pendingAchievements.Count > 0)
            {
                Dispatcher.BeginInvoke(() => _ = ShowPendingAchievementsAsync(), DispatcherPriority.Loaded);
            }
        }

        public void OnClosed()
        {
            clockTimer.Stop();
            StopAmbient();

            // Endless animations keep WPF's clock ticking even while hidden.
            LiveDot.BeginAnimation(OpacityProperty, null);
            ArtSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            artSpinning = false;
        }

        private bool artSpinning;

        public void SetMouseModeIndicator(bool enabled)
        {
            MouseModeChip.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The last button press came from a PlayStation controller (true) or an Xbox one: hints follow it.</summary>
        public void SetButtonStyle(bool playStation)
        {
            if (playStation != playStationButtons)
            {
                playStationButtons = playStation;
                UpdateHints();
            }
        }

        /// <summary>Entry point for controller input while the overlay is visible (already on the UI thread).</summary>
        public void HandleButton(PadButton button)
        {
            if (IsModalOpen)
            {
                switch (button)
                {
                    case PadButton.DPadUp: Move(FocusNavigationDirection.Up); break;
                    case PadButton.DPadDown: Move(FocusNavigationDirection.Down); break;
                    case PadButton.DPadLeft: Move(FocusNavigationDirection.Left); break;
                    case PadButton.DPadRight: Move(FocusNavigationDirection.Right); break;
                    case PadButton.A: UiNavigator.ActivateFocused(); break;
                    case PadButton.B: (modalBack ?? (() => CloseModal()))(); break;
                    case PadButton.Y: CloseModal(); break;
                }

                return;
            }

            // Customize page: left/right change the focused setting instead of moving focus.
            if (currentPage == OverlayPage.Customize
                && button is PadButton.DPadLeft or PadButton.DPadRight
                && FocusedItem<SettingItem>() is SettingItem setting)
            {
                string before = setting.ValueText;
                setting.Step(button == PadButton.DPadRight ? +1 : -1);
                bool changed = setting.ValueText != before;
                Sound(!changed ? Sfx.Bump : button == PadButton.DPadRight ? Sfx.ChangeUp : Sfx.ChangeDown);
                return;
            }

            switch (button)
            {
                case PadButton.DPadUp:
                    Move(FocusNavigationDirection.Up);
                    break;
                case PadButton.DPadDown:
                    Move(FocusNavigationDirection.Down);
                    break;
                case PadButton.DPadLeft:
                    Move(FocusNavigationDirection.Left);
                    break;
                case PadButton.DPadRight:
                    Move(FocusNavigationDirection.Right);
                    break;
                case PadButton.A:
                    UiNavigator.ActivateFocused();
                    break;
                case PadButton.B:
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case PadButton.LeftShoulder:
                    Sound(Sfx.Page);
                    ShowPage(PreviousPage(currentPage), focus: true, direction: -1);
                    break;
                case PadButton.RightShoulder:
                    Sound(Sfx.Page);
                    ShowPage(NextPage(currentPage), focus: true, direction: +1);
                    break;
                case PadButton.X:
                    OnContextAction();
                    break;
                case PadButton.Y:
                    OnSecondaryContextAction();
                    break;
                case PadButton.Back when IsGridPage(currentPage):
                    Sound(Sfx.ChangeUp);
                    CycleFilter();
                    break;
                case PadButton.Start:
                    Sound(Sfx.Page);
                    ShowPage(currentPage == OverlayPage.Customize ? OverlayPage.Games : OverlayPage.Customize, focus: true,
                        direction: currentPage == OverlayPage.Customize ? -1 : +1);
                    break;
            }
        }

        /// <summary>Moves focus; a tick when it moved, a soft bump at the edge.</summary>
        private void Move(FocusNavigationDirection direction)
        {
            FrameworkElement scope = IsModalOpen ? ModalPanel : MainContent;
            Sound(UiNavigator.Move(direction, scope) ? Sfx.Navigate : Sfx.Bump);
        }

        private void Sound(Sfx sfx) => host?.Sounds.Play(sfx);

        // =====================================================================
        // Pages and tabs
        // =====================================================================

        private void ShowPage(OverlayPage page, bool focus, int direction, bool animate = true)
        {
            bool changed = page != currentPage;
            currentPage = page;

            // Games and Library share one grid; switching between them swaps what it shows.
            if (IsGridPage(page))
            {
                ShowTilesFor(page);
            }

            GamesPage.Visibility = IsGridPage(page) ? Visibility.Visible : Visibility.Collapsed;
            WindowsPage.Visibility = page == OverlayPage.Windows ? Visibility.Visible : Visibility.Collapsed;
            SystemPage.Visibility = page == OverlayPage.System ? Visibility.Visible : Visibility.Collapsed;
            CustomizePage.Visibility = page == OverlayPage.Customize ? Visibility.Visible : Visibility.Collapsed;

            foreach ((TextBlock tab, OverlayPage tabPage) in Tabs())
            {
                tab.SetResourceReference(TextBlock.ForegroundProperty, tabPage == page ? "OnAccentBrush" : "SubtleTextBrush");
            }

            if (page == OverlayPage.Windows)
            {
                RefreshOpenWindows();
            }

            if (page == OverlayPage.Customize)
            {
                RefreshSettings();
                UpdatePreview();
            }

            // The backdrop shows the focused game's art; other pages get the calm version.
            Motion.To(PageDim, OpacityProperty, IsGridPage(page) ? 0 : 0.5, 420, Motion.EaseOut);
            if (!IsGridPage(page))
            {
                ShowNeutralBackdrop();
            }
            else if (FocusedTile() is GameTileViewModel focusedTile)
            {
                ScheduleBackdrop(focusedTile);
            }

            UpdateHints();
            PositionTabIndicator(animate: changed && animate);

            if (changed && animate)
            {
                FrameworkElement element = CurrentPageElement();
                switch (ThemeManager.Current.PageTransition)
                {
                    case PageTransition.Slide:
                        Motion.Entrance(element, 0, offsetX: 80 * Math.Sign(direction == 0 ? 1 : direction), offsetY: 0, ms: 440);
                        break;
                    case PageTransition.Fade:
                        Motion.Entrance(element, 0, offsetX: 0, offsetY: 0, ms: 320);
                        break;
                    case PageTransition.Zoom:
                        Motion.Entrance(element, 0, offsetX: 0, offsetY: 0, fromScale: 0.95, ms: 420);
                        break;
                }

            }

            if (focus)
            {
                FocusPage(page);
            }
        }

        private void FocusPage(OverlayPage page)
        {
            if (IsGridPage(page) && FocusTile(lastFocusedGameId))
            {
                return;
            }

            UiNavigator.FocusFirstIn(CurrentPageElement());
        }

        private static bool IsGridPage(OverlayPage page) => page is OverlayPage.Games or OverlayPage.Library;

        private IEnumerable<(TextBlock tab, OverlayPage page)> Tabs()
        {
            yield return (GamesTab, OverlayPage.Games);
            yield return (LibraryTab, OverlayPage.Library);
            yield return (WindowsTab, OverlayPage.Windows);
            yield return (SystemTab, OverlayPage.System);
            yield return (CustomizeTab, OverlayPage.Customize);
        }

        /// <summary>Slides the glowing pill behind the active tab.</summary>
        private void PositionTabIndicator(bool animate)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TextBlock tab = Tabs().First(t => t.page == currentPage).tab;
                if (tab.ActualWidth <= 0)
                {
                    return;
                }

                Point position = tab.TranslatePoint(new Point(0, 0), TabHost);
                if (!animate || !tabsPositioned)
                {
                    TabIndicatorMove.BeginAnimation(TranslateTransform.XProperty, null);
                    TabIndicator.BeginAnimation(WidthProperty, null);
                    TabIndicatorMove.X = position.X;
                    TabIndicator.Width = tab.ActualWidth;
                    tabsPositioned = true;
                    return;
                }

                Motion.To(TabIndicatorMove, TranslateTransform.XProperty, position.X, 420, Motion.Spring);
                Motion.To(TabIndicator, WidthProperty, tab.ActualWidth, 420, Motion.Spring);
            }, DispatcherPriority.Loaded);
        }

        private FrameworkElement CurrentPageElement() => currentPage switch
        {
            OverlayPage.Games or OverlayPage.Library => GamesPage,
            OverlayPage.Windows => WindowsPage,
            OverlayPage.System => SystemPage,
            _ => CustomizePage
        };

        /// <summary>The Library tab only exists with Steam installed (or something else listing uninstalled games).</summary>
        private bool IsPageAvailable(OverlayPage page) => page != OverlayPage.Library || LibraryTab.Visibility == Visibility.Visible;

        private OverlayPage NextPage(OverlayPage page)
        {
            do
            {
                page = page == OverlayPage.Customize ? OverlayPage.Games : page + 1;
            }
            while (!IsPageAvailable(page));

            return page;
        }

        private OverlayPage PreviousPage(OverlayPage page)
        {
            do
            {
                page = page == OverlayPage.Games ? OverlayPage.Customize : page - 1;
            }
            while (!IsPageAvailable(page));

            return page;
        }

        private void RefreshOpenWindows()
        {
            openWindows.Clear();
            foreach (OpenWindow window in WindowManager.GetOpenWindows())
            {
                openWindows.Add(new WindowRowViewModel(window));
            }

            WindowsEmpty.Visibility = openWindows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateClock()
        {
            AppearanceSettings settings = ThemeManager.Current;
            ClockPanel.Visibility = settings.Clock == ClockMode.Off ? Visibility.Collapsed : Visibility.Visible;
            ClockText.Text = DateTime.Now.ToString(settings.Clock == ClockMode.TwelveHour ? "h:mm tt" : "HH:mm");
            DateText.Text = DateTime.Now.ToString("dddd, MMM d");
            DateText.Visibility = settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateHints()
        {
            hints.Clear();

            void Add(HintButton button, string label) => hints.Add(new HintViewModel(button, label, playStationButtons));

            if (IsModalOpen)
            {
                Add(HintButton.A, "Select");
                Add(HintButton.B, "Back");
                Add(HintButton.Y, "Close");
                return;
            }

            switch (currentPage)
            {
                case OverlayPage.Games:
                case OverlayPage.Library:
                    Add(HintButton.A, currentPage == OverlayPage.Library ? "Install" : "Play");
                    Add(HintButton.X, "Favorite");
                    Add(HintButton.Y, "Options");
                    Add(HintButton.View, "Filter");
                    Add(HintButton.Menu, "Customize");
                    break;
                case OverlayPage.Windows:
                    Add(HintButton.A, "Switch to");
                    Add(HintButton.X, "Close window");
                    Add(HintButton.Y, "Force quit");
                    break;
                case OverlayPage.Customize:
                    Add(HintButton.DPad, "Change");
                    Add(HintButton.A, "Select");
                    Add(HintButton.Menu, "Library");
                    break;
                default:
                    Add(HintButton.A, "Select");
                    break;
            }

            Add(HintButton.Bumpers, "Switch page");
            Add(HintButton.B, "Back to game");
        }

        // =====================================================================
        // X / Y context buttons
        // =====================================================================

        /// <summary>X button.</summary>
        private void OnContextAction()
        {
            if (currentPage == OverlayPage.Windows && FocusedItem<WindowRowViewModel>() is WindowRowViewModel row)
            {
                WindowManager.RequestClose(row.Window.Handle);
                ShowToast("", "Closing " + row.ProcessName, null);
                _ = RefreshOpenWindowsLaterAsync();
            }
            else if (IsGridPage(currentPage) && FocusedItem<GameTileViewModel>() is GameTileViewModel tile)
            {
                ToggleFavorite(tile);
            }
        }

        /// <summary>Give the app a moment to close (or show its "save changes?" prompt), then refresh the list.</summary>
        private async System.Threading.Tasks.Task RefreshOpenWindowsLaterAsync()
        {
            await System.Threading.Tasks.Task.Delay(700);
            RefreshOpenWindows();
            UiNavigator.FocusFirstIn(WindowsPage);
        }

        /// <summary>Y button.</summary>
        private void OnSecondaryContextAction()
        {
            if (currentPage == OverlayPage.Windows && FocusedItem<WindowRowViewModel>() is WindowRowViewModel row)
            {
                Confirm("", $"Force quit {row.ProcessName}?", "Unsaved progress will be lost.", "Force quit", () =>
                {
                    WindowManager.KillProcess(row.Window.ProcessId);
                    RefreshOpenWindows();
                    UiNavigator.FocusFirstIn(WindowsPage);
                });
            }
            else if (IsGridPage(currentPage) && FocusedItem<GameTileViewModel>() is GameTileViewModel tile)
            {
                ShowGameMenu(tile);
            }
        }

        private static T? FocusedItem<T>() where T : class
        {
            return (Keyboard.FocusedElement as FrameworkElement)?.DataContext as T;
        }

        // =====================================================================
        // Click handlers (A button or mouse)
        // =====================================================================

        private void OnWindowClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: WindowRowViewModel row })
            {
                Sound(Sfx.Select);
                SwitchToWindowRequested?.Invoke(this, row.Window.Handle);
            }
        }

        private void OnResumeClicked(object sender, RoutedEventArgs e)
        {
            ResumeRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnCloseCurrentClicked(object sender, RoutedEventArgs e)
        {
            if (nowPlaying == null)
            {
                return;
            }

            // Polite close, then bring the game forward so you can answer its "Quit?" prompt.
            WindowManager.RequestClose(nowPlaying.Handle);
            ResumeRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnKillCurrentClicked(object sender, RoutedEventArgs e)
        {
            if (nowPlaying is not OpenWindow target)
            {
                return;
            }

            Confirm("", $"Force quit {target.Title}?", "Unsaved progress will be lost.", "Force quit", () =>
            {
                WindowManager.KillProcess(target.ProcessId);
                nowPlaying = null;
                NowPlayingBar.Visibility = Visibility.Collapsed;
                ShowPage(currentPage, focus: true, direction: 0, animate: false);
            });
        }

        private void OnSystemActionClicked(object sender, RoutedEventArgs e)
        {
            string action = (sender as Button)?.CommandParameter as string ?? string.Empty;
            Sound(Sfx.Select);

            switch (action)
            {
                case "VolumeDown": SystemActions.VolumeDown(); break;
                case "VolumeUp": SystemActions.VolumeUp(); break;
                case "Mute": SystemActions.ToggleMute(); break;
                case "PlayPause": SystemActions.PlayPause(); break;

                case "DisplayTv": SystemActions.SetDisplayMode(DisplayMode.SecondScreenOnly); break;
                case "DisplayLaptop": SystemActions.SetDisplayMode(DisplayMode.PcScreenOnly); break;
                case "DisplayDuplicate": SystemActions.SetDisplayMode(DisplayMode.Duplicate); break;
                case "DisplayExtend": SystemActions.SetDisplayMode(DisplayMode.Extend); break;

                case "MouseMode":
                    MouseModeToggleRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "Keyboard":
                    // There's nothing to type into in CouchDeck: back to the app behind, keyboard on top of it.
                    SystemActions.ShowOnScreenKeyboard();
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "BigPicture":
                    SystemActions.OpenSteamBigPicture();
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "Rescan":
                    RequestRescan();
                    break;

                case "Sleep": Confirm("", "Put the PC to sleep?", null, "Sleep", SystemActions.Sleep); break;
                case "Lock": SystemActions.Lock(); break;
                case "Restart": Confirm("", "Restart the PC?", null, "Restart", SystemActions.Restart); break;
                case "Shutdown": Confirm("", "Shut down the PC?", null, "Shut down", SystemActions.Shutdown); break;
                case "ExitApp":
                    Confirm("", "Exit CouchDeck?", "The Xbox / PS button will stop working until you start it again.", "Exit",
                        () => ExitRequested?.Invoke(this, EventArgs.Empty));
                    break;
            }
        }

        private void RequestRescan()
        {
            GamesStatus.Text = "Scanning for games...";
            ShowToast("", "Rescanning your library", null);
            RescanRequested?.Invoke(this, EventArgs.Empty);
        }

        // =====================================================================
        // Modal: confirm dialogs, menus, file browser
        // =====================================================================

        private void ShowModal(string glyph, string title, string? subtitle, IReadOnlyList<ModalItem> items,
            Action? onBack = null, int focusIndex = 0)
        {
            bool wasOpen = IsModalOpen;
            if (!wasOpen)
            {
                focusBeforeModal = Keyboard.FocusedElement;
            }

            ModalIcon.Text = glyph;
            ModalTitle.Text = title;
            ModalSubtitle.Text = subtitle ?? string.Empty;
            ModalSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            ModalItems.ItemsSource = items;
            ModalScroll.Visibility = Visibility.Visible;
            ModalCustom.Content = null;
            ModalCustom.Visibility = Visibility.Collapsed;
            ModalBox.Width = 900;
            modalBack = onBack;
            modalScroller.ScrollToStart();

            OpenModalLayer(wasOpen);
            if (wasOpen)
            {
                Motion.Entrance(ModalItems, 0, offsetY: 12, ms: 260);
            }

            UpdateHints();

            Dispatcher.BeginInvoke(() =>
            {
                List<Button> buttons = ModalItems.Items.Cast<object>()
                    .Select(item => ModalItems.ItemContainerGenerator.ContainerFromItem(item))
                    .Select(container => container == null ? null : FindDescendant<Button>(container))
                    .Where(button => button != null)
                    .Cast<Button>()
                    .ToList();

                if (buttons.Count > 0)
                {
                    Button target = buttons[Math.Clamp(focusIndex, 0, buttons.Count - 1)];
                    Keyboard.Focus(target);
                    target.BringIntoView();
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>A dialog with any content (the colour table). Focus goes to <paramref name="focusTarget"/>.</summary>
        private void ShowCustomModal(string glyph, string title, string? subtitle, UIElement content, double width,
            Action? onBack, Func<IInputElement?> focusTarget)
        {
            bool wasOpen = IsModalOpen;
            if (!wasOpen)
            {
                focusBeforeModal = Keyboard.FocusedElement;
            }

            ModalIcon.Text = glyph;
            ModalTitle.Text = title;
            ModalSubtitle.Text = subtitle ?? string.Empty;
            ModalSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            ModalItems.ItemsSource = null;
            ModalScroll.Visibility = Visibility.Collapsed;
            ModalCustom.Content = content;
            ModalCustom.Visibility = Visibility.Visible;
            ModalBox.Width = width;
            modalBack = onBack;

            OpenModalLayer(wasOpen);
            UpdateHints();

            Dispatcher.BeginInvoke(() =>
            {
                if (focusTarget() is IInputElement target)
                {
                    Keyboard.Focus(target);
                }
            }, DispatcherPriority.Loaded);
        }

        private void OpenModalLayer(bool wasOpen)
        {
            if (wasOpen)
            {
                return;
            }

            Sound(Sfx.ModalOpen);
            ModalLayer.Visibility = Visibility.Visible;
            Main.IsEnabled = false; // nothing behind the dialog can take focus

            Motion.To(ModalScrim, OpacityProperty, 1, 220, Motion.EaseOut, from: 0);
            Motion.Entrance(ModalBox, 0, offsetY: 24, fromScale: 0.95, ms: 340);

            // Blur what's behind, once: the blurred page is cached as a (half-res) bitmap, so the
            // dialog's own animations don't re-run a full-screen blur every frame.
            if (ThemeManager.EffectiveQuality != GraphicsQuality.Performance)
            {
                MainContent.Effect = new BlurEffect { Radius = 12, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                Main.CacheMode = new BitmapCache(0.5);
            }
        }

        private void CloseModal(bool restoreFocus = true)
        {
            if (!IsModalOpen)
            {
                return;
            }

            if (restoreFocus)
            {
                Sound(Sfx.ModalClose);
            }

            modalBack = null;
            ModalLayer.Visibility = Visibility.Collapsed;
            ModalItems.ItemsSource = null;
            ModalCustom.Content = null;
            Main.IsEnabled = true;
            MainContent.Effect = null;
            Main.CacheMode = null;
            UpdateHints();

            if (!restoreFocus)
            {
                return;
            }

            IInputElement? previous = focusBeforeModal;
            focusBeforeModal = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (previous is UIElement element && element.IsVisible && element.Focusable && element.IsEnabled)
                {
                    Keyboard.Focus(element);
                }
                else
                {
                    FocusPage(currentPage);
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Cancel is first and focused: a stray A press must never shut the PC down.</summary>
        private void Confirm(string glyph, string message, string? detail, string yesLabel, Action onYes)
        {
            ShowModal(glyph, message, detail, new List<ModalItem>
            {
                new("", "Cancel", () => CloseModal()),
                new(glyph, yesLabel, () =>
                {
                    CloseModal();
                    onYes();
                }, isDanger: true)
            });
        }

        private void OnModalItemClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ModalItem item })
            {
                Sound(item.IsDanger ? Sfx.Back : Sfx.Select);
                item.OnSelect();
            }
        }

        // =====================================================================
        // Toasts
        // =====================================================================

        public void ShowToast(string glyph, string title, string? message, Sfx? sound = null)
        {
            if (sound is Sfx sfx && IsVisible)
            {
                Sound(sfx);
            }

            Border toast = new()
            {
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(20, 16, 24, 16),
                Margin = new Thickness(0, 10, 0, 0),
                BorderThickness = new Thickness(1)
            };
            toast.SetResourceReference(Border.BackgroundProperty, "SurfaceSolidBrush");
            toast.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");

            Grid content = new();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());

            Border iconHost = new()
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(13),
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconHost.SetResourceReference(Border.BackgroundProperty, "AccentGradientBrush");
            TextBlock icon = new() { Text = glyph, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center };
            icon.SetResourceReference(StyleProperty, "IconText");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
            iconHost.Child = icon;
            content.Children.Add(iconHost);

            StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(message))
            {
                TextBlock body = new() { Text = message, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                body.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
                text.Children.Add(body);
            }

            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            toast.Child = content;

            while (ToastHost.Children.Count >= 3)
            {
                ToastHost.Children.RemoveAt(0);
            }

            ToastHost.Children.Add(toast);
            Motion.Entrance(toast, 0, offsetX: 60, offsetY: 0, ms: 420);

            DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3.6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Motion.To(toast, OpacityProperty, 0, 300, Motion.EaseOut, completed: () => ToastHost.Children.Remove(toast));
            };
            timer.Start();
        }

        private void PulseLiveDot()
        {
            if (!ThemeManager.AnimationsEnabled)
            {
                LiveDot.BeginAnimation(OpacityProperty, null);
                return;
            }

            DoubleAnimation pulse = new(1, 0.25, new Duration(TimeSpan.FromSeconds(1.1)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseInOut
            };
            Timeline.SetDesiredFrameRate(pulse, 20); // a slow pulse doesn't need 60 fps
            LiveDot.BeginAnimation(OpacityProperty, pulse);
        }

        // =====================================================================
        // Keyboard fallback (for testing at the desk without a controller)
        // =====================================================================

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            PadButton? button = e.Key switch
            {
                Key.Up => PadButton.DPadUp,
                Key.Down => PadButton.DPadDown,
                Key.Left => PadButton.DPadLeft,
                Key.Right => PadButton.DPadRight,
                Key.Enter or Key.Space => PadButton.A,
                Key.Escape or Key.Back => PadButton.B,
                Key.PageUp or Key.Q => PadButton.LeftShoulder,
                Key.PageDown or Key.E => PadButton.RightShoulder,
                Key.X or Key.Delete => PadButton.X,
                Key.Y or Key.Apps => PadButton.Y,
                Key.Tab => PadButton.Back,
                Key.F1 or Key.M => PadButton.Start,
                _ => null
            };

            if (button is PadButton pressed)
            {
                HandleButton(pressed);
                e.Handled = true;
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                T? nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
