using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CouchDeck.Audio;
using CouchDeck.Config;
using CouchDeck.Covers;
using CouchDeck.Games;
using CouchDeck.Input;
using CouchDeck.UI;
using CouchDeck.WindowsControl;

namespace CouchDeck
{
    /// <summary>
    /// Composition root. Starts hidden (tray icon only), listens to the controller in the
    /// background, and shows/hides the overlay when the Xbox button is pressed.
    ///
    /// Flow:
    ///   Xbox button (overlay hidden)  -> remember the app in front, show overlay, take focus
    ///   Xbox button / B (overlay up)  -> hide overlay, give focus back to that app
    ///   A on a game                   -> launch it, hide overlay
    ///   Hold View + Menu (anywhere)   -> toggle mouse mode
    ///   Library scan                  -> games without art are handed to CoverArtService,
    ///                                    tiles update live as covers download
    ///   Steam (optional Web API key)  -> owned games fill the Library page; A there asks Steam to
    ///                                    install; quitting a game lists the achievements just earned
    /// </summary>
    public partial class App : Application, IOverlayHost
    {
        private const PadButton MouseModeChord = PadButton.Back | PadButton.Start;

        private const string ShowSignalName = @"Local\CouchDeck.Show";

        /// <summary>How often the owned Steam games are reloaded (when the overlay opens).</summary>
        private static readonly TimeSpan SteamResyncAfter = TimeSpan.FromHours(6);

        private Mutex? singleInstance;
        private EventWaitHandle? showSignal;
        private AppConfig config = new();
        private ControllerService? controller;
        private MouseModeController? mouseMode;
        private GameLibrary? library;
        private GameLauncher? launcher;
        private CoverArtService? covers;
        private SteamWebApi? steamApi;
        private AchievementWatcher? achievements;
        private SteamInstallWatcher? steamInstalls;
        private GameExitWatcher? gameExits;
        private bool steamSyncing;
        private string? steamError;
        private int steamOwnedCount;
        private DateTime lastSteamSync = DateTime.MinValue;
        private DateTime lastSteamAttempt = DateTime.MinValue;
        private readonly SoundService sounds = new();
        private OverlayWindow? overlay;
        private IntPtr overlayHandle;
        private System.Windows.Forms.NotifyIcon? trayIcon;
        private DispatcherTimer? saveTimer;

        /// <summary>The window that was in front when the overlay opened (usually the game).</summary>
        private IntPtr previousForeground;

        AppConfig IOverlayHost.Config => config;
        CoverArtService IOverlayHost.Covers => covers!;
        SoundService IOverlayHost.Sounds => sounds;
        string IOverlayHost.SteamStatus => DescribeSteam();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Build helper: CouchDeck.exe --export-icon <folder> writes the .ico / logo and exits.
            int export = Array.IndexOf(e.Args, "--export-icon");
            if (export >= 0)
            {
                AppIcon.Export(export + 1 < e.Args.Length ? e.Args[export + 1] : "Assets");
                Shutdown();
                return;
            }

            bool startedInBackground = Array.Exists(e.Args, arg => arg == "--background");

            singleInstance = new Mutex(true, @"Local\CouchDeck.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                // Already running (e.g. started with Windows). Opened from the desktop shortcut?
                // Then ask the running copy to show itself instead of silently doing nothing.
                if (!startedInBackground)
                {
                    try
                    {
                        using EventWaitHandle running = EventWaitHandle.OpenExisting(ShowSignalName);
                        running.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // the other copy is still starting up
                    }
                }

                Shutdown();
                return;
            }

            ListenForShowSignal();

            config = ConfigStore.Load();
            StartupRegistration.Apply(config.StartWithWindows);
            ThemeManager.Apply(config.Appearance);
            sounds.Apply(config.Sound); // synthesizes the pack in the background

            library = new GameLibrary(config);
            launcher = new GameLauncher(config);
            covers = new CoverArtService(config);

            // Covers download on worker threads -> hop to the UI thread.
            covers.ArtUpdated += (_, update) => Dispatcher.BeginInvoke(() => overlay?.ApplyArt(update));
            covers.QueueChanged += (_, pending) => Dispatcher.BeginInvoke(() => overlay?.SetArtQueue(pending));

            steamApi = new SteamWebApi(config);
            steamOwnedCount = SteamOwnedGames.Load()?.Games.Count ?? 0;
            achievements = new AchievementWatcher(steamApi);
            achievements.Earned += (_, summary) => Dispatcher.BeginInvoke(() => OnAchievementsEarned(summary));
            steamInstalls = new SteamInstallWatcher();
            steamInstalls.InstallsChanged += (_, _) => Dispatcher.BeginInvoke(() => _ = RescanGamesAsync());
            steamInstalls.DownloadsChanged += (_, downloads) => Dispatcher.BeginInvoke(() => overlay?.SetDownloads(downloads));
            gameExits = new GameExitWatcher();
            gameExits.GameClosed += (_, _) => Dispatcher.BeginInvoke(OnGameClosed);

            CreateOverlay();
            CreateTrayIcon();
            StartController();

            _ = StartupScanAsync();

            // Started by double-click (not by Windows at login)? Open straight away so it's obvious it works.
            if (!startedInBackground)
            {
                ShowOverlay();
            }
        }

        // =====================================================================
        // Setup
        // =====================================================================

        private void CreateOverlay()
        {
            overlay = new OverlayWindow();
            overlay.Icon = AppIcon.Render(256);
            overlay.Initialize(this);

            // Create the HWND now without showing the window, so the first Xbox press is instant.
            overlayHandle = new WindowInteropHelper(overlay).EnsureHandle();

            overlay.ResumeRequested += (_, _) => HideOverlay(returnFocusTo: previousForeground);
            overlay.SwitchToWindowRequested += (_, hWnd) => HideOverlay(returnFocusTo: hWnd);
            overlay.LaunchRequested += (_, game) => LaunchGame(game);
            overlay.InstallRequested += (_, game) => RunStoreAction(game.InstallTarget);
            overlay.UninstallRequested += (_, game) => RunStoreAction(game.UninstallTarget);
            overlay.DownloadsRequested += (_, _) => RunStoreAction("steam://open/downloads");
            overlay.HideRequested += (_, _) => HideOverlay(returnFocusTo: IntPtr.Zero);
            overlay.MouseModeToggleRequested += (_, _) =>
            {
                mouseMode?.SetEnabled(!mouseMode.IsEnabled);
                if (mouseMode?.IsEnabled == true)
                {
                    HideOverlay(returnFocusTo: previousForeground); // mouse mode is for driving other apps
                }
            };
            overlay.RescanRequested += (_, _) => RescanEverything();
            overlay.ExitRequested += (_, _) => ExitApp();
        }

        private void StartController()
        {
            controller = new ControllerService
            {
                LeftStickAsDPad = false,
                RepeatIntervalMs = config.NavigationRepeatMs
            };
            mouseMode = new MouseModeController(controller) { MaxSpeed = config.MouseSpeed };

            controller.RegisterChord(MouseModeChord, config.MouseModeChordHoldMs);

            // All controller events arrive on the polling thread -> hop to the UI thread.
            controller.ButtonPressed += (_, args) => Dispatcher.BeginInvoke(() => OnButtonPressed(args));
            controller.ChordHeld += (_, args) => Dispatcher.BeginInvoke(() => OnChordHeld(args.controllerIndex, args.chord));
            controller.ConnectionChanged += (_, args) =>
                Debug.WriteLine($"Controller {args.controllerIndex} {(args.connected ? "connected" : "disconnected")}");

            mouseMode.EnabledChanged += (_, enabled) => Dispatcher.BeginInvoke(() => overlay?.SetMouseModeIndicator(enabled));

            controller.Start();
        }

        private void CreateTrayIcon()
        {
            System.Windows.Forms.ContextMenuStrip menu = new();
            menu.Items.Add("Open CouchDeck", null, (_, _) => ShowOverlay());
            menu.Items.Add("Rescan games", null, (_, _) => RescanEverything());
            menu.Items.Add("Open config folder", null, (_, _) =>
                Process.Start(new ProcessStartInfo(ConfigStore.FolderPath) { UseShellExecute = true }));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = AppIcon.ToTrayIcon(AppIcon.Render(32)) ?? System.Drawing.SystemIcons.Application,
                Text = "CouchDeck - press the Xbox or PS button",
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += (_, _) => ShowOverlay();
        }

        /// <summary>A second launch (desktop shortcut) signals this event; the running copy opens the overlay.</summary>
        private void ListenForShowSignal()
        {
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            Thread listener = new(() =>
            {
                while (showSignal.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (overlay?.IsVisible == false)
                        {
                            ShowOverlay();
                        }
                    });
                }
            })
            {
                IsBackground = true,
                Name = "CouchDeck show signal"
            };
            listener.Start();
        }

        // =====================================================================
        // IOverlayHost (library edits and the Customize page)
        // =====================================================================

        /// <summary>Debounced: sliders on the Customize page change values many times a second.</summary>
        public void SaveConfig()
        {
            saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
            {
                saveTimer!.Stop();
                ConfigStore.Save(config);
            }, Dispatcher);

            saveTimer.Stop();
            saveTimer.Start();
        }

        public void Rescan() => _ = RescanGamesAsync();

        public void RefreshSteamLibrary() => _ = SyncSteamLibraryAsync();

        public void ApplyControllerSettings()
        {
            if (controller != null)
            {
                controller.RepeatIntervalMs = config.NavigationRepeatMs;
                controller.SetChordHold(MouseModeChord, config.MouseModeChordHoldMs);
            }

            if (mouseMode != null)
            {
                mouseMode.MaxSpeed = config.MouseSpeed;
            }

            StartupRegistration.Apply(config.StartWithWindows);
        }

        // =====================================================================
        // Controller routing
        // =====================================================================

        private void OnButtonPressed(PadButtonEventArgs args)
        {
            if (overlay == null)
            {
                return;
            }

            overlay.SetButtonStyle(args.IsPlayStation);

            if (args.Button == PadButton.Guide && !args.IsRepeat)
            {
                if (overlay.IsVisible)
                {
                    HideOverlay(returnFocusTo: previousForeground);
                }
                else
                {
                    ShowOverlay();
                }

                return;
            }

            // While hidden, the game owns the controller. We only listen for Guide and the chord.
            if (!overlay.IsVisible)
            {
                return;
            }

            // Open but not in front: something took the screen back. Games read the controller themselves, so acting
            // here too would make every press do two things (A in the game also "pressed" whatever CouchDeck had selected).
            if (NativeMethods.GetForegroundWindow() != overlayHandle)
            {
                if (WindowManager.IsFullScreenAppInFront())
                {
                    HideOverlay(returnFocusTo: IntPtr.Zero, playSound: false); // a game: it's the game's press
                    return;
                }

                WindowManager.ForceForeground(overlayHandle); // e.g. a Steam window popped up over us: take the screen back
            }

            overlay.HandleButton(args.Button);
        }

        private void OnChordHeld(int controllerIndex, PadButton chord)
        {
            if (chord == MouseModeChord && overlay?.IsVisible == false)
            {
                mouseMode?.Toggle(controllerIndex);
            }
        }

        // =====================================================================
        // Overlay show / hide
        // =====================================================================

        private void ShowOverlay()
        {
            if (overlay == null || controller == null || mouseMode == null)
            {
                return;
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != overlayHandle)
            {
                previousForeground = foreground;
            }

            // While the overlay is up: stick navigates menus, mouse mode pauses.
            controller.LeftStickAsDPad = true;
            mouseMode.IsSuspended = true;

            sounds.Wake(); // opens the audio device in the background

            overlay.Show();
            WindowManager.ForceForeground(overlayHandle);
            sounds.Play(Sfx.Open);
            overlay.Activate();
            overlay.OnOpened(DescribePreviousApp());

            // Bought something on the phone? The Library page catches up every few hours.
            if (DateTime.UtcNow - lastSteamSync > SteamResyncAfter && DateTime.UtcNow - lastSteamAttempt > TimeSpan.FromMinutes(2))
            {
                _ = SyncSteamLibraryAsync();
            }
        }

        private void HideOverlay(IntPtr returnFocusTo, bool playSound = true)
        {
            if (overlay == null || controller == null || mouseMode == null)
            {
                return;
            }

            if (playSound && overlay.IsVisible)
            {
                sounds.Play(Sfx.Close);
            }

            overlay.Hide();
            overlay.OnClosed();
            sounds.Idle(); // releases the audio device after the close sound, so Windows can sleep

            controller.LeftStickAsDPad = false; // don't fire arrow keys while mouse mode moves the cursor
            mouseMode.IsSuspended = false;

            if (returnFocusTo != IntPtr.Zero)
            {
                WindowManager.ForceForeground(returnFocusTo);
            }
        }

        /// <summary>The "Now playing" bar. Ignore the desktop and shell windows - nothing to resume there.</summary>
        private OpenWindow? DescribePreviousApp()
        {
            OpenWindow? window = WindowManager.Describe(previousForeground);
            if (window == null || string.IsNullOrWhiteSpace(window.Title))
            {
                return null;
            }

            bool isShell = window.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                && (window.Title == "Program Manager" || window.Title.Length == 0);

            return isShell ? null : window;
        }

        // =====================================================================
        // Games
        // =====================================================================

        private void LaunchGame(Game game)
        {
            if (launcher == null)
            {
                return;
            }

            sounds.Play(Sfx.Launch);

            // Mouse mode turns presses into clicks and keys (X opens the on-screen keyboard): never while playing.
            mouseMode?.SetEnabled(false);

            // Hide first and don't refocus the old app; the new game will take the foreground itself.
            HideOverlay(returnFocusTo: IntPtr.Zero, playSound: false);

            if (!launcher.Launch(game))
            {
                sounds.Play(Sfx.Error);
                trayIcon?.ShowBalloonTip(3000, "CouchDeck", $"Couldn't start {game.Title}", System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            gameExits?.Watch(game);
        }

        /// <summary>The game started from here closed: come back like a console returning home.</summary>
        private void OnGameClosed()
        {
            // Not over another game (or this one, if it only looked closed), and not while mouse mode drives the desktop.
            if (overlay == null || overlay.IsVisible || !config.ReopenAfterGame || mouseMode?.IsEnabled == true
                || SteamScanner.RunningAppId() != null || WindowManager.IsFullScreenAppInFront())
            {
                return;
            }

            ShowOverlay();
        }

        /// <summary>
        /// Install / uninstall: Steam opens its own window (drive picker, "Uninstall X?"). Let it come to the
        /// front and get out of its way. SteamInstallWatcher moves the tile between Games and Library once Steam is done.
        /// </summary>
        private void RunStoreAction(string? target)
        {
            if (target == null)
            {
                return;
            }

            sounds.Play(Sfx.Select);

            // Only allowed while we're the foreground app, so before hiding.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            HideOverlay(returnFocusTo: IntPtr.Zero, playSound: false);

            if (!GameLauncher.OpenStoreAction(target))
            {
                sounds.Play(Sfx.Error);
                trayIcon?.ShowBalloonTip(3000, "CouchDeck", "Couldn't reach the store. Is it installed?", System.Windows.Forms.ToolTipIcon.Warning);
            }
        }

        /// <summary>The local scan first (instant, includes the saved Steam list), then the owned list online.</summary>
        private async System.Threading.Tasks.Task StartupScanAsync()
        {
            await RescanGamesAsync();
            await SyncSteamLibraryAsync();
        }

        /// <summary>The user asked for a rescan: local stores and the Steam account.</summary>
        private void RescanEverything()
        {
            _ = RescanGamesAsync();
            _ = SyncSteamLibraryAsync();
        }

        private async System.Threading.Tasks.Task RescanGamesAsync()
        {
            if (library == null || overlay == null || covers == null)
            {
                return;
            }

            List<Game> scanned = await library.ScanAsync();

            // Hidden games count as installed: nothing to move for them.
            steamInstalls?.SetInstalled(scanned
                .Where(game => game.Source == GameSource.Steam && game.IsInstalled && game.SteamAppId != null)
                .Select(game => game.SteamAppId!)
                .Concat(config.HiddenGameIds.Where(id => id.StartsWith("steam:")).Select(id => id["steam:".Length..])));

            // Art downloaded on earlier runs is applied instantly; anything missing gets searched (installed games first).
            List<Game> games = scanned.Select(covers.ApplyCached).ToList();
            overlay.SetGames(games); // await resumes on the UI thread
            covers.EnqueueMissing(games.OrderBy(game => game.IsInstalled ? 0 : 1));
        }

        // =====================================================================
        // Steam account: owned games and achievements
        // =====================================================================

        /// <summary>Reloads the owned Steam games from the account. Rescans only when the list changed.</summary>
        private async System.Threading.Tasks.Task SyncSteamLibraryAsync()
        {
            if (steamApi == null || steamSyncing)
            {
                return;
            }

            steamError = null;
            string? steamId = steamApi.SteamId;
            if (steamApi.Key == null || steamId == null)
            {
                steamError = steamApi.Key != null ? "Log in to Steam once, then reload" : null;
                overlay?.OnSteamStatusChanged();
                return;
            }

            steamSyncing = true;
            lastSteamAttempt = DateTime.UtcNow;
            overlay?.OnSteamStatusChanged();
            try
            {
                (bool changed, int count) = await SteamOwnedGames.RefreshAsync(steamApi, steamId, CancellationToken.None);
                steamOwnedCount = count;
                lastSteamSync = DateTime.UtcNow;
                if (changed)
                {
                    await RescanGamesAsync();
                }
            }
            catch (SteamApiException ex)
            {
                steamError = ex.Message;
            }
            finally
            {
                steamSyncing = false;
                overlay?.OnSteamStatusChanged();
            }
        }

        private string DescribeSteam()
        {
            if (steamApi?.Key == null)
            {
                return "Not connected";
            }

            if (steamSyncing)
            {
                return "Loading your games...";
            }

            return steamError
                ?? achievements?.LastError
                ?? (steamOwnedCount == 1 ? "1 game owned" : $"{steamOwnedCount} games owned");
        }

        /// <summary>A Steam game closed and the account shows achievements unlocked while it ran.</summary>
        private void OnAchievementsEarned(AchievementSummary summary)
        {
            if (overlay == null || !config.ShowNewAchievements)
            {
                return;
            }

            overlay.QueueAchievements(summary); // shown now if the overlay is up, else when it opens

            // Back from the game: open like a console returning home. Not over another game or in mouse mode.
            if (!overlay.IsVisible && achievements?.IsGameRunning != true && mouseMode?.IsEnabled != true
                && !WindowManager.IsFullScreenAppInFront())
            {
                ShowOverlay();
            }
        }

        // =====================================================================
        // Shutdown
        // =====================================================================

        private void ExitApp()
        {
            mouseMode?.SetEnabled(false);
            controller?.Dispose();
            covers?.Dispose();
            achievements?.Dispose();
            steamInstalls?.Dispose();
            gameExits?.Dispose();
            steamApi?.Dispose();
            sounds.Dispose();

            if (saveTimer?.IsEnabled == true)
            {
                saveTimer.Stop();
                ConfigStore.Save(config);
            }

            if (trayIcon != null)
            {
                trayIcon.Visible = false; // otherwise a ghost icon stays until you hover it
                trayIcon.Dispose();
            }

            singleInstance?.ReleaseMutex();
            Shutdown();
        }
    }
}
