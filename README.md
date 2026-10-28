# CouchDeck

A controller-first overlay for Windows. Press the **Xbox button** (or the **PS button**) from the couch
and get one full-screen page with all your games plus the Windows controls you'd normally have to get up for.

Works with Xbox controllers (and anything that shows up as one) and with **PlayStation controllers**
(DualShock 4, DualSense, DualSense Edge) over USB or Bluetooth, with no DS4Windows needed.

Stack: **C# / .NET 8 / WPF**, plus NAudio for low-latency sound mixing. Chosen because everything this needs is native Windows API
(XInput, SendInput, window focus, registry) and C# calls those directly. A web/Electron UI would
need a native helper for all of it anyway.

---

## Install

**Easiest:** download `CouchDeck.exe` from the [latest release](https://github.com/DDDD-stack/CouchDeck/releases/latest), put it somewhere it
can stay (e.g. `Documents\CouchDeck`) and double-click it. One file, no .NET needed. It isn't code-signed,
so Windows may say *"Windows protected your PC"*: **More info → Run anyway**.

**From the code:**

```
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

Builds one self-contained `CouchDeck.exe` (no .NET needed on the PC), installs it to
`%LocalAppData%\Programs\CouchDeck`, adds a **desktop shortcut**, registers it to **start with
Windows in the background** (tray only, waiting for the Xbox button) and starts it now.
Run it again after changing the code to update; `-NoLaunch` skips starting it, `-Uninstall`
removes it (settings, covers and sounds in `%AppData%\CouchDeck` are kept).

The desktop shortcut opens the overlay; if CouchDeck is already running in the background it
tells that copy to open instead of starting a second one.

## Run it (development)

1. Install the **.NET 8 SDK** (or Visual Studio 2022 with ".NET desktop development").
2. Open `CouchDeck.csproj` in Visual Studio / Rider and press Run, or from a terminal in this folder:
   ```
   dotnet run
   ```
3. Plug in or pair the controller and press the Xbox / PS button.

Started by hand, it opens immediately. Started with Windows (it registers itself, see config),
it stays in the tray until you press the Xbox button.

### Two settings you MUST change, or the Xbox button opens something else

| Who grabs the button | Where to turn it off |
|---|---|
| Xbox Game Bar | Settings → Gaming → Xbox Game Bar → turn off **"Open Xbox Game Bar using this button on a controller"** |
| Steam (when running) | Steam → Settings → Controller → turn off **"Guide Button Focuses Steam"** |

### PlayStation controllers

Pair it (hold PS + Create until the light flashes, then Windows → Bluetooth → Add device) or plug it in,
and press the PS button. Buttons work by position: **✕ = A, ○ = B, □ = X, △ = Y**, L1/R1 = LB/RB,
Create = View, Options = Menu, and the hints at the bottom switch to ✕ ○ □ △ while you use one.

- **DS4Windows, DualSenseX or Steam turning it into an Xbox controller** is fine: CouchDeck notices the
  copy and listens to only one of them, so a press never counts twice.
- If DS4Windows hides the controller (HidHide or "exclusive mode"), CouchDeck simply uses the Xbox copy.
- No rumble on PlayStation controllers (the "mouse mode on/off" buzz is Xbox only for now).

---

## What it looks like

- **Games**: the focused game's art sits behind everything: its wide banner across the top at its
  real aspect (sharp, uncropped, fading into the background) or, for games without one, its cover
  as a poster on the right. Its name is shown big above the library, and the tile zooms, lifts,
  glows and gets a light sweep. Tiles cascade in when the overlay opens. Grid layout, or **Shelf**
  (one console-style row under a big hero). Only installed games are here.
- **Library**: Steam games you own but haven't installed. **A** installs (see [Steam](#steam-library-install--uninstall-achievements)).
  Shown when Steam is installed. Games that are downloading show the percentage on their tile.
- **Back home after playing**: when a game you started from CouchDeck closes, CouchDeck opens again,
  like a console going back to its home screen (Customize → Controller → *Back to CouchDeck after a game*).
- **Windows**: open apps with their real icons (Alt+Tab replacement).
- **System**: volume, display mode, mouse mode, keyboard, power.
- **Customize**: every look and behaviour setting, changed live with a preview (see below).

Sharpness: covers are decoded at exactly the size they're shown (tile size × screen scaling ×
focus zoom) and drawn with high-quality scaling; Steam covers are fetched at 2× (1200×1800).
Text uses layout rounding and per-monitor DPI, so a 4K TV at 200% stays crisp.

Smoothness: tiles avoid the things that make WPF re-render in layers every frame (rounded clips,
opacity on panels); every glow/shadow/blur is cached as a bitmap; the ambient glow runs at
20 fps (30 on Quality); background art is decoded at screen size (capped by quality) on a worker
thread. Measured on a 144 Hz screen while moving focus every 110 ms: ~130 fps, p99 frame 18 ms.
**Customize → Motion → Graphics quality**: Auto (default) picks Balanced on a real GPU and
Performance otherwise; Performance drops the moving glow, blur, light sweep and cascades.

## Sound

UI sounds for moving, selecting, going back, switching pages, dialogs, favorites, errors,
notifications, opening/closing the overlay and launching a game. Mixed (a tick never cuts off a
chime), ~60 ms latency, loudness-matched across packs. Customize → Sound:

- **Packs**: *Soft* (warm, modern console) and *Arcade* (8-bit) are synthesized at startup, no files.
- **Your own**: make a folder in `%AppData%\CouchDeck\sounds\<PackName>\` with any of
  `navigate, bump, select, back, page, modal-open, modal-close, change-up, change-down, open, close,
  launch, favorite, error, notify` as .wav or .mp3. Missing ones fall back to Soft; leading silence
  is trimmed and levels are matched automatically.
- **AI pack (ElevenLabs)**: put `"ElevenLabsApiKey": "..."` in config.json (or set the
  `ELEVENLABS_API_KEY` environment variable), then Customize → Sound → *Generate an AI sound pack*.
  15 sounds are generated from text prompts (`SoundService.ElevenLabsPrompts`) into
  `sounds\ElevenLabs` and selected. The pack is only swapped in when all 15 succeed.
- Volume, on/off, and a separate switch for navigation ticks.

The audio device is opened off the UI thread and paused a few seconds after the overlay hides
(an active stream would stop Windows from sleeping); after 10 idle minutes it's released.

---

## Controls

**Overlay open**

| Button | Action |
|---|---|
| Xbox | Close overlay, back to the game |
| D-pad / left stick | Move (hold to scroll fast). On Customize: ◀ ▶ change the value |
| A | Select / launch. Library: install |
| B | Back / close overlay |
| LB / RB | Previous / next page (Games, Library, Windows, System, Customize) |
| X | Games / Library: favorite. Windows: close the selected window |
| Y | Games / Library: game options (cover, exe, uninstall, hide...). Windows: force quit (asks first) |
| View | Games: cycle the filter (All, Favorites, Steam, Epic, Local, ...) |
| Menu | Jump to Customize (and back) |

**Mouse mode** (hold **View + Menu** for ~1 s anywhere, or System → Mouse mode). Controller buzzes on toggle.
It turns off by itself when you start a game from CouchDeck, so your presses go to the game only.

| Input | Action |
|---|---|
| Left stick | Cursor (hold RT for slow/precise) |
| Right stick | Scroll |
| A / B | Left click (hold = drag) / right click |
| X | On-screen keyboard |
| Y | Enter |
| LB / RB | Escape / Backspace |
| D-pad | Arrow keys |

Keyboard works too, for testing at the desk: arrows, Enter/Space (A), Esc/Backspace (B),
PageUp/PageDown or Q/E (LB/RB), X, Y, Tab (View), F1 or M (Menu).

---

## Cover art, found automatically

Any game without art gets it from the web, once, and cached in `%AppData%\CouchDeck\covers`:

1. **Launcher art first**: Steam's local library cache; the Epic launcher's catalog cache
   (official portrait + wide art, works for Epic-only games like Fortnite).
2. **Web search** for everything else (games in game folders, games added by hand, GOG):
   the folder/exe name is cleaned up (`Elden.Ring.v1.02.3-CODEX` → `Elden Ring`,
   `Hollow_Knight_v1.5.78_(64bit)` → `Hollow Knight`, `[FitGirl Repack]`, versions, `MULTi10`,
   edition words... are stripped), searched on the Steam store, and the best title match wins.
   Steam's asset API is used to get the exact file names (newer games use hashed URLs that a
   guessed URL would 404 on).
3. Wide **background art** is downloaded too and shown behind the focused game.
4. Optional: a free **SteamGridDB** API key (`SteamGridDbApiKey` in config.json, get one at
   steamgriddb.com → Preferences → API) adds community art and many alternatives per game.

Tiles update live while art downloads (a "Finding cover art" chip shows progress). Wrong cover?
Press **Y** on the game → *Try a different cover* (next match/design), *Search the web again*, or
*Use my own image*. Names guessed from folders are replaced by the official name once matched
(Customize → Library → *Use official game names*).

Nothing is searched twice: results, including "not found" (retried after 3 days), are stored in
`covers\index.json`.

## Adding games

- **Game folders** (Customize → Library → *Add a games folder*, or the **+ Add a game** tile):
  every sub-folder of e.g. `D:\Games` becomes a game, now and in future. The main .exe is picked
  automatically: installers, redistributables, crash reporters, anti-cheat helpers and tools are
  skipped, a name that matches the folder wins, shallow beats deep (Unreal launcher stubs).
  Wrong exe? **Y → Change which .exe starts it**.
- **Single game**: *Add a game* → pick the .exe (or a .lnk/.url shortcut).

Both use a built-in file browser you drive with the controller (A open, B up a level), no mouse needed.

---

## Steam: library, install / uninstall, achievements

Installed Steam games are found without any setup. Connect your Steam account to also get:

- **Library page**: every game you own but haven't installed, kept off the Games page so it stays
  uncluttered. **A** (or **Y → Install**) opens Steam's own install window. While it downloads, the tile
  shows the percentage and a progress bar (also "Paused", "Installing", and "Updating" on installed games),
  downloads are listed first, and **A** opens Steam's downloads page to pause or cancel. When Steam
  finishes, the game moves to Games by itself (no rescan needed).
- **Uninstall**: **Y → Uninstall** on an installed Steam game opens Steam's "Uninstall?" prompt. The
  game goes back to Library.
- **New achievements after playing**: Steam shows its own popup when you unlock something in-game.
  When you quit, CouchDeck opens and lists everything you unlocked in that session (name, description,
  icon, "12 of 50 unlocked"). CouchDeck doesn't count anything itself: it reads your unlocks from your
  Steam account. Turn it off in Customize → Steam.

**Setup (once, from the couch):** Customize → Steam → Steam account → *Get a free Steam Web API key*.
That opens steamcommunity.com/dev/apikey. Any domain name works (e.g. `localhost`). Copy the key (mouse
mode helps), come back, *Paste key from clipboard*. Or put `"SteamWebApiKey": "..."` in config.json, or
set the `STEAM_API_KEY` environment variable.

- Every user uses **their own key**. Never commit one to the repository.
- CouchDeck only **reads** from Steam: owned games and achievements. The key stays in your config.json.
- The account is detected from the Steam client (the one logged in, or the last one). Set `"SteamId"`
  (SteamID64) in config.json to override.
- The owned list works with a private profile, because the key belongs to the same account. If achievements
  don't show, set Steam profile → Privacy → **Game details** to Public.
- Steam's install/uninstall windows are Steam's: use Steam's controller support or mouse mode there.
- The owned list is refreshed at start, on *Rescan games*, and every 6 hours when the overlay opens.

**How it works:** `SteamWebApi` calls the official Web API (`GetOwnedGames`, `GetPlayerAchievements`,
`GetSchemaForGame`) and caches owned games in `%AppData%\CouchDeck\steam-owned.json`; `SteamOwnedScanner`
turns that cache into Library tiles (scans themselves stay offline). Install/uninstall are the
`steam://install/<appid>` and `steam://uninstall/<appid>` links. `SteamInstallWatcher` watches the
appmanifest files to notice when an install finishes, and reads the download progress from them
(`BytesDownloaded` / `BytesToDownload`, then `BytesStaged` / `BytesToStage`) every 2 s while one runs.
`AchievementWatcher` polls
`HKCU\Software\Valve\Steam\RunningAppID` (which game is running) every 3 s. When a game starts it
remembers what's unlocked; when it stops, it asks again, and the difference is your new achievements.

### Help wanted: other launchers

Only Steam is done, on purpose: it's the only store with an official API for this. If you know a clean way
for Epic, GOG, EA, Ubisoft or Xbox, the hooks are already there:

- **Owned, not installed**: return `Game` objects with `IsInstalled = false` and an `InstallTarget` from a
  scanner (`IGameScanner`). They appear on the Library page with that store's filter chip. Scanners must
  stay offline: fetch online in the background and cache, like `SteamOwnedGames`.
- **Uninstall**: set `UninstallTarget` on installed games. The Y menu shows *Uninstall* automatically.
- **Achievements**: raise an `AchievementSummary` (see `AchievementWatcher`) and the same "new achievements"
  screen shows it.

---

## Customize page

Everything is live, saved to config.json, and resettable (Reset → *Reset look to defaults*).

| Section | Settings |
|---|---|
| Theme | 6 presets (Nebula, Xbox, Cobalt, Synthwave, Crimson, Mono), glass panel opacity |
| Custom colours | accent (gradient start), gradient end, background: each opens a **colour table** (24 hues × 8 shades + greys; background gets dark shades only) you move through with the D-pad, with a live gradient preview; gradient direction; swap colours |
| Background | game banner / cover poster / colour blur / your own image / solid; blur (0 = sharp); darkness; aurora glow; ambient motion; vignette |
| Layout | grid or shelf, tile size, spacing, corner roundness, names on tiles (focus/always/never), store badges, big title, "+ Add" tile, button hints, clock (24h/12h/off), date |
| Motion | graphics quality (auto/quality/balanced/performance), animation speed (off → cinematic), focus zoom, lift, glow, dim other tiles, light sweep, tile cascade, page transition (slide/fade/zoom/none) |
| Text | font (installed ones offered), interface size 70-150%, title weight, uppercase tabs |
| Sound | on/off, volume, pack, navigation ticks, generate an AI pack, open sounds folder |
| Library | add game / games folder, remove folders, sort, favorites first, auto cover art, background art, official names, find missing art, re-download all art, unhide games, rescan |
| Steam | connect your account (get key, paste key, reload owned games, disconnect), new achievements after playing |
| Controller | mouse speed, mouse-mode hold time, menu repeat speed, start with Windows, back to CouchDeck after a game |

Any exact colour can also be set by hand in config.json (`"AccentColor": "#12AB34"`).

---

## Project layout

```
CouchDeck/
├─ App.xaml(.cs)            Entry point. Tray icon, controller routing, show/hide overlay, focus juggling,
│                           IOverlayHost (config save, rescan, live controller settings), second-launch signal
├─ Assets/                  CouchDeck.ico (exe/shortcut icon) and logo.png, generated from UI/AppIcon.cs:
│                           CouchDeck.exe --export-icon Assets
├─ tools/install.ps1        Publish single-file exe, install, desktop shortcut, start with Windows
├─ app.manifest             DPI awareness for 4K TVs, asInvoker
├─ Input/
│  ├─ XInputNative.cs       XInput P/Invoke, incl. hidden ordinal 100 that reports the Xbox button
│  ├─ PadButton.cs          Button flags, PadState snapshot
│  ├─ PlayStationPads.cs    DualShock 4 / DualSense over USB + Bluetooth, read as HID -> same state as XInput
│  ├─ ControllerService.cs  120 Hz background polling -> pressed/released/held-chord events, auto-repeat,
│  │                        drops the duplicate when a PlayStation pad also shows up as an Xbox one
│  ├─ MouseModeController.cs Controller-as-mouse
│  └─ UiNavigator.cs        D-pad -> focus movement by layout position (rows/columns, animations ignored), A -> click
├─ Games/
│  ├─ IGameScanner.cs       One per store. Add a store = add a class
│  ├─ SteamScanner.cs       libraryfolders.vdf + appmanifest_*.acf, local cover + hero art
│  ├─ SteamWebApi.cs        Official Steam Web API with the user's key: owned games, achievements, icons
│  ├─ SteamOwnedScanner.cs  Owned-but-not-installed games (Library page) from the steam-owned.json cache
│  ├─ SteamInstallWatcher.cs Finished installs / uninstalls -> tiles move between Games and Library; download %
│  ├─ AchievementWatcher.cs Which game is running -> achievements earned in that session
│  ├─ GameExitWatcher.cs    The game started from CouchDeck closed -> open CouchDeck again
│  ├─ EpicScanner.cs        Manifests\*.item + catalog cache art
│  ├─ GogScanner.cs         HKLM\...\GOG.com\Games
│  ├─ XboxScanner.cs        STUB (Game Pass / Store) - how-to inside
│  ├─ CustomGamesScanner.cs Hand-added games from config.json
│  ├─ FolderGamesScanner.cs Game folders: one game per sub-folder, ExeFinder picks the main .exe
│  ├─ TitleCleaner.cs       Folder/release names -> titles, fuzzy title matching
│  ├─ GameLibrary.cs        Runs scanners in parallel, merges, de-duplicates, applies per-game overrides
│  ├─ GameLauncher.cs       Starts games (store URIs or exe), finds a game's running process
│  └─ VdfParser.cs          Tiny Valve KeyValues parser
├─ Covers/
│  ├─ CoverArtService.cs    Background queue: search -> download -> cache -> live tile update
│  └─ ArtClients.cs         Steam store search + asset API, SteamGridDB (optional)
├─ WindowsControl/          user32/dwmapi/powrprof, SendInput, window list, system actions, autostart
├─ Audio/
│  ├─ SoundService.cs       Mixer + output device, packs, custom files, ElevenLabs generation
│  └─ SoundSynth.cs         The built-in Soft and Arcade packs, synthesized at startup
├─ Config/AppConfig.cs      %AppData%\CouchDeck\config.json (incl. Appearance, Sound, GameOverrides)
└─ UI/
   ├─ OverlayWindow.xaml    Layout: backdrop, header + tabs, pages, dialogs, toasts
   ├─ OverlayWindow.xaml.cs Pages, input, dialogs, toasts
   ├─ OverlayWindow.Library.cs   Tiles (Games + Library pages), filters, hero, backdrop crossfade, per-game menu, adding games
   ├─ OverlayWindow.Steam.cs     Steam account setup, the "new achievements" summary
   ├─ OverlayWindow.Customize.cs Customize page and applying appearance
   ├─ Theme.xaml            Control templates (tiles, cards, pills, rows), all DynamicResource
   ├─ ThemeManager.cs       AppearanceSettings -> live resources, presets, palettes
   ├─ Motion.cs             Animations (focus zoom/lift/glow/shine, entrances), smooth scrolling
   ├─ ImageLoader.cs        Off-thread DPI-exact image decoding, 256 px shell icons
   ├─ SettingsModel.cs      Setting rows (choice/toggle/slider/action)
   ├─ FileBrowser.cs        Controller-friendly file/folder picker
   ├─ ViewModels.cs         Tile / window / hint / filter / dialog view models
   └─ AppIcon.cs            The logo (couch + A B X Y) as vectors: exe icon, tray, header in theme colours
```

**How the Xbox button works:** Windows doesn't send controller buttons to apps as keystrokes,
so there's no "hotkey" to register. `ControllerService` polls XInput on a background thread
the whole time (~0.1% CPU). The public `XInputGetState` hides the Xbox button, so it calls the
undocumented export at ordinal 100 (`XInputGetStateEx`), which Steam and DS4Windows also use.

**Why the focus code is weird:** Windows blocks background apps from stealing focus, and a
controller press doesn't count as user input. `WindowManager.ForceForeground` attaches to the
foreground window's input queue for a moment so `SetForegroundWindow` is allowed.

---

## config.json

`%AppData%\CouchDeck\config.json` is created on first run (tray menu → Open config folder).
Most of it is edited from the Customize page; the rest by hand. Restart CouchDeck (or System →
Rescan games) after editing by hand.

```json
{
  "StartWithWindows": true,
  "MouseSpeed": 1800,
  "MouseModeChordHoldMs": 800,
  "NavigationRepeatMs": 110,
  "CustomGames": [
    { "Title": "Minecraft", "Path": "C:\\XboxGames\\Minecraft Launcher\\Content\\Minecraft.exe" },
    { "Title": "Some Game Pass game", "Path": "shell:AppsFolder\\<PackageFamilyName>!<AppId>",
      "CoverImage": "C:\\Covers\\game.jpg" }
  ],
  "GameFolders": [ "D:\\Games" ],
  "HiddenGameIds": [ "steam:431960" ],
  "GameOverrides": {
    "folder:d:\\games\\elden ring": { "Title": "Elden Ring", "Favorite": true, "LaunchTarget": "D:\\Games\\Elden Ring\\Game\\eldenring.exe" }
  },
  "Sort": "RecentlyPlayed",
  "FavoritesFirst": true,
  "AutoFetchCovers": true,
  "DownloadHeroArt": true,
  "UseOfficialNames": true,
  "ReopenAfterGame": true,
  "SteamGridDbApiKey": null,
  "SteamWebApiKey": null,
  "SteamId": null,
  "ShowNewAchievements": true,
  "Appearance": { "Preset": "Nebula", "AccentColor": "#7C5CFF", "Layout": "Grid", "TileSize": 100, "...": "..." }
}
```

For a Game Pass game, get the `shell:AppsFolder\...` target by opening `shell:AppsFolder` in Explorer,
right-clicking the game → Create shortcut, and copying the shortcut's target.

---

## Known limitations (be aware before testing)

- **The game behind the overlay still sees the controller.** XInput isn't focus-aware. Most games
  ignore input when they lose focus, some don't. A real fix needs a driver-level hider like HidHide.
  The other way round is handled: if a game takes the screen back while CouchDeck is open, CouchDeck
  steps aside instead of also acting on your presses.
- **Exclusive fullscreen games** may minimise when the overlay appears. Borderless/windowed
  fullscreen (the default in most modern games) is fine.
- **Games running as administrator or with kernel anti-cheat** can't be clicked/typed into by a
  non-admin CouchDeck, and "Force quit" may fail on them. Run CouchDeck as admin if that matters.
- **Game Pass / Microsoft Store games** aren't scanned yet (see `XboxScanner.cs`); add them by hand.
- **Cover search needs internet** the first time; without it tiles show the game's .exe icon and
  the search is retried later.
- Games that aren't on Steam (and not Epic) need a SteamGridDB key for automatic art, or
  *Use my own image*.

## Roadmap (rough order)

1. Built-in controller keyboard in the overlay (Steam-style), also for renaming games
2. Xbox / Game Pass scanner
3. Switch audio output (laptop speakers ↔ TV over HDMI)
4. "Running" badge on game tiles, controller battery level, search
5. Per-game sound on launch (game's own theme)

## Making a release

Push a version tag; GitHub builds `CouchDeck.exe` from that commit and publishes the release
(`.github/workflows/release.yml`). The tag's message is the release notes:

```
git tag -a v2.1.0 --cleanup=verbatim -F notes.md
git push origin v2.1.0
```

## License

Free and open source under the [MIT License](LICENSE): use it, change it, share it. Pull requests welcome.
