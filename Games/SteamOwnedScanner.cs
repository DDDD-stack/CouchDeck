using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CouchDeck.Config;

namespace CouchDeck.Games
{
    /// <summary>
    /// Steam games you own but haven't installed, for the Library page. Scans must stay offline, so this
    /// reads the list <see cref="SteamOwnedGames.RefreshAsync"/> saved from the Steam Web API.
    /// GameLibrary keeps the installed copy (SteamScanner) when both exist, so each game shows once:
    /// on Games once it's installed, on Library until then.
    /// </summary>
    public sealed class SteamOwnedScanner : IGameScanner
    {
        private readonly AppConfig config;

        public SteamOwnedScanner(AppConfig config)
        {
            this.config = config;
        }

        public string Name => "Steam library";

        public IEnumerable<Game> Scan()
        {
            List<Game> games = new();

            SteamOwnedGames.CacheFile? cache = SteamOwnedGames.Load();
            if (cache == null || SteamWebApi.KeyFrom(config) == null)
            {
                return games;
            }

            // Somebody else is logged in to Steam now: that list isn't theirs.
            string? steamId = SteamWebApi.FindSteamId(config);
            if (steamId != null && steamId != cache.SteamId)
            {
                return games;
            }

            string? steamPath = SteamScanner.FindSteamPath();
            foreach (OwnedSteamGame owned in cache.Games)
            {
                if (SteamScanner.ExcludedAppIds.Contains(owned.AppId) || SteamScanner.IsTool(owned.Name))
                {
                    continue;
                }

                games.Add(new Game
                {
                    Id = $"steam:{owned.AppId}",
                    Title = owned.Name,
                    Source = GameSource.Steam,
                    LaunchTarget = $"steam://rungameid/{owned.AppId}",
                    IsInstalled = false,
                    InstallTarget = $"steam://install/{owned.AppId}",
                    SteamAppId = owned.AppId,
                    LastPlayed = owned.LastPlayed,

                    // Steam caches library art for games you own too, which saves most downloads.
                    CoverImage = steamPath == null ? null : SteamScanner.FindCachedArt(steamPath, owned.AppId, "library_600x900"),
                    HeroImage = steamPath == null ? null : SteamScanner.FindCachedArt(steamPath, owned.AppId, "library_hero")
                });
            }

            return games;
        }
    }

    /// <summary>%AppData%\CouchDeck\steam-owned.json: the account's owned games, refreshed from the Web API.</summary>
    public static class SteamOwnedGames
    {
        public sealed class CacheFile
        {
            public string SteamId { get; set; } = "";
            public DateTime Fetched { get; set; }
            public List<OwnedSteamGame> Games { get; set; } = new();
        }

        private static readonly object FileLock = new();

        public static string CachePath { get; } = Path.Combine(ConfigStore.FolderPath, "steam-owned.json");

        public static CacheFile? Load()
        {
            try
            {
                lock (FileLock)
                {
                    return File.Exists(CachePath)
                        ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath))
                        : null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Owned Steam games cache unreadable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads the owned list and saves it. Returns whether it differs from the saved one (then the
        /// library needs a rescan) and how many games the account owns. Throws <see cref="SteamApiException"/>.
        /// </summary>
        public static async Task<(bool changed, int count)> RefreshAsync(SteamWebApi api, string steamId, CancellationToken token)
        {
            List<OwnedSteamGame> games = (await api.GetOwnedGamesAsync(steamId, token))
                .OrderBy(game => game.AppId, StringComparer.Ordinal)
                .ToList();

            CacheFile? old = Load();
            bool changed = old == null || old.SteamId != steamId || !old.Games.SequenceEqual(games);
            Save(new CacheFile { SteamId = steamId, Fetched = DateTime.UtcNow, Games = games });
            return (changed, games.Count);
        }

        public static void Clear()
        {
            try
            {
                lock (FileLock)
                {
                    File.Delete(CachePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Couldn't delete the owned Steam games cache: {ex.Message}");
            }
        }

        private static void Save(CacheFile cache)
        {
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(ConfigStore.FolderPath);
                    string temp = CachePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(cache));
                    File.Move(temp, CachePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Owned Steam games cache save failed: {ex.Message}");
            }
        }
    }
}
