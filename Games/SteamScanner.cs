using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CouchDeck.Games
{
    /// <summary>
    /// Steam keeps one "library folder" per drive. Each one has steamapps\appmanifest_&lt;appid&gt;.acf
    /// per installed game. We read:
    ///   HKCU\Software\Valve\Steam\SteamPath            -> Steam install folder
    ///   steamapps\libraryfolders.vdf                   -> every library folder
    ///   &lt;library&gt;\steamapps\appmanifest_*.acf          -> appid, name, installdir, LastPlayed
    /// Launch with steam://rungameid/&lt;appid&gt; so Steam handles DRM, updates, cloud saves and overlay.
    /// </summary>
    public sealed class SteamScanner : IGameScanner
    {
        private const int StateFullyInstalled = 4;

        // Tools that get appmanifests but aren't games.
        internal static readonly HashSet<string> ExcludedAppIds = new()
        {
            "228980",  // Steamworks Common Redistributables
            "250820",  // SteamVR
            "1070560", // Steam Linux Runtime
            "1391110", // Steam Linux Runtime - Soldier
            "1628350"  // Steam Linux Runtime - Sniper
        };

        public string Name => "Steam";

        public IEnumerable<Game> Scan()
        {
            List<Game> games = new();

            try
            {
                string? steamPath = FindSteamPath();
                if (steamPath == null)
                {
                    return games;
                }

                foreach (string library in FindLibraryFolders(steamPath))
                {
                    string steamApps = Path.Combine(library, "steamapps");
                    if (!Directory.Exists(steamApps))
                    {
                        continue;
                    }

                    foreach (string manifestPath in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                    {
                        Game? game = ReadManifest(manifestPath, steamApps, steamPath);
                        if (game != null)
                        {
                            games.Add(game);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam scan failed: {ex}");
            }

            return games;
        }

        internal static string? FindSteamPath()
        {
            string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            path ??= Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;

            // SteamPath uses forward slashes: c:/program files (x86)/steam
            return path != null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }

        /// <summary>The Steam game running right now: HKCU\Software\Valve\Steam\RunningAppID (0 = none).</summary>
        internal static string? RunningAppId() =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0) is int appId && appId > 0
                ? appId.ToString()
                : null;

        /// <summary>Every library's steamapps folder (where the appmanifests are). Empty when Steam isn't installed.</summary>
        internal static List<string> SteamAppsFolders()
        {
            string? steamPath = FindSteamPath();
            return steamPath == null
                ? new List<string>()
                : FindLibraryFolders(steamPath).Select(library => Path.Combine(library, "steamapps")).Where(Directory.Exists).ToList();
        }

        /// <summary>
        /// Same rule as the scan: fully installed = StateFlags has bit 4. Null when the file can't be read
        /// right now (Steam is writing it); false when it's gone (uninstalled).
        /// </summary>
        internal static bool? IsFullyInstalled(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return false;
                }

                Dictionary<string, object>? app = VdfParser.GetObject(VdfParser.Parse(File.ReadAllText(manifestPath)), "AppState");
                return app != null
                    && int.TryParse(VdfParser.GetString(app, "StateFlags"), out int flags)
                    && (flags & StateFullyInstalled) != 0;
            }
            catch (IOException)
            {
                return null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IEnumerable<string> FindLibraryFolders(string steamPath)
        {
            HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase) { steamPath };

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
            {
                return folders;
            }

            Dictionary<string, object> root = VdfParser.Parse(File.ReadAllText(vdfPath));
            Dictionary<string, object>? libraries = VdfParser.GetObject(root, "libraryfolders");
            if (libraries == null)
            {
                return folders;
            }

            // Entries are "0", "1", ... each with a "path" value.
            foreach (object entry in libraries.Values)
            {
                if (entry is Dictionary<string, object> library && VdfParser.GetString(library, "path") is string path)
                {
                    folders.Add(path);
                }
            }

            return folders;
        }

        private static Game? ReadManifest(string manifestPath, string steamApps, string steamPath)
        {
            try
            {
                Dictionary<string, object> root = VdfParser.Parse(File.ReadAllText(manifestPath));
                Dictionary<string, object>? app = VdfParser.GetObject(root, "AppState");
                if (app == null)
                {
                    return null;
                }

                string? appId = VdfParser.GetString(app, "appid");
                string? name = VdfParser.GetString(app, "name");
                string? installDir = VdfParser.GetString(app, "installdir");

                if (appId == null || name == null || ExcludedAppIds.Contains(appId) || IsTool(name))
                {
                    return null;
                }

                // Skip games that are still downloading / not fully installed.
                if (int.TryParse(VdfParser.GetString(app, "StateFlags"), out int flags) && (flags & StateFullyInstalled) == 0)
                {
                    return null;
                }

                DateTime? lastPlayed = null;
                if (long.TryParse(VdfParser.GetString(app, "LastPlayed"), out long unix) && unix > 0)
                {
                    lastPlayed = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                }

                return new Game
                {
                    Id = $"steam:{appId}",
                    Title = name,
                    Source = GameSource.Steam,
                    LaunchTarget = $"steam://rungameid/{appId}",
                    InstallDir = installDir == null ? null : Path.Combine(steamApps, "common", installDir),
                    CoverImage = FindCachedArt(steamPath, appId, "library_600x900"),
                    HeroImage = FindCachedArt(steamPath, appId, "library_hero"),
                    SteamAppId = appId,
                    LastPlayed = lastPlayed,
                    UninstallTarget = $"steam://uninstall/{appId}"
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Bad Steam manifest {manifestPath}: {ex.Message}");
                return null;
            }
        }

        internal static bool IsTool(string name) =>
            name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Steam caches library art locally once you've seen the game in your library.
        /// Older layout: librarycache\&lt;appid&gt;_library_600x900.jpg
        /// Newer layout: librarycache\&lt;appid&gt;\library_600x900.jpg (sometimes one folder deeper)
        /// Prefers the _2x (1200x1800) file for sharpness. Returns null when nothing is cached;
        /// CoverArtService then downloads the art (newer games use hashed CDN paths a guess can't hit).
        /// </summary>
        internal static string? FindCachedArt(string steamPath, string appId, string baseName)
        {
            string cache = Path.Combine(steamPath, "appcache", "librarycache");

            string oldLayout = Path.Combine(cache, $"{appId}_{baseName}.jpg");
            if (File.Exists(oldLayout))
            {
                return oldLayout;
            }

            string appFolder = Path.Combine(cache, appId);
            if (!Directory.Exists(appFolder))
            {
                return null;
            }

            List<string> found = Directory
                .EnumerateFiles(appFolder, $"{baseName}*.jpg", SearchOption.AllDirectories)
                .ToList();

            return found.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith("_2x", StringComparison.OrdinalIgnoreCase))
                ?? found.FirstOrDefault();
        }
    }
}
