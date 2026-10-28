using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CouchDeck.Config;
using Microsoft.Win32;

namespace CouchDeck.Games
{
    /// <summary>Something Steam said no to. The message is short enough to show in the overlay.</summary>
    public sealed class SteamApiException : Exception
    {
        public SteamApiException(string message, bool retryable = false) : base(message)
        {
            Retryable = retryable;
        }

        /// <summary>True for network trouble (worth another try). False for a bad key or a private profile.</summary>
        public bool Retryable { get; }
    }

    public sealed record OwnedSteamGame(string AppId, string Name, DateTime? LastPlayed);

    public sealed class SteamAchievement
    {
        public required string ApiName { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public bool Achieved { get; init; }

        /// <summary>UTC. Null while locked.</summary>
        public DateTime? UnlockTime { get; init; }

        /// <summary>Local copy of the icon, once downloaded.</summary>
        public string? IconPath { get; set; }
    }

    public sealed record PlayerAchievements(string GameName, IReadOnlyList<SteamAchievement> Achievements);

    /// <summary>
    /// The official Steam Web API (api.steampowered.com), read-only, with the user's own free key from
    /// steamcommunity.com/dev/apikey. Every user brings their own key; never put one in the source.
    ///   IPlayerService/GetOwnedGames            every game on the account (Library page)
    ///   ISteamUserStats/GetPlayerAchievements   which achievements are unlocked, and when
    ///   ISteamUserStats/GetSchemaForGame        achievement icons
    /// Owned games work with a private profile as long as the key belongs to the same account.
    /// Achievements may need Steam profile > Privacy > Game details set to Public.
    /// </summary>
    public sealed class SteamWebApi : IDisposable
    {
        private const string Api = "https://api.steampowered.com/";
        private const long SteamId64Base = 76561197960265728;
        private static readonly Regex KeyFormat = new("^[0-9A-Fa-f]{32}$");

        private readonly AppConfig config;
        private readonly HttpClient http;

        public SteamWebApi(AppConfig config)
        {
            this.config = config;
            http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CouchDeck/2.0");
        }

        public static string IconFolder { get; } = Path.Combine(ConfigStore.FolderPath, "achievements");

        /// <summary>SteamWebApiKey in config.json, or the STEAM_API_KEY environment variable.</summary>
        public string? Key => KeyFrom(config);

        /// <summary>The account to read: SteamId in config.json, else whoever is logged in to Steam.</summary>
        public string? SteamId => FindSteamId(config);

        public static string? KeyFrom(AppConfig config)
        {
            string? key = config.SteamWebApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = Environment.GetEnvironmentVariable("STEAM_API_KEY");
            }

            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        public static bool LooksLikeKey(string? text) => text != null && KeyFormat.IsMatch(text.Trim());

        public static string? FindSteamId(AppConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.SteamId))
            {
                return config.SteamId.Trim();
            }

            try
            {
                // While Steam runs: the logged-in account's 32-bit id. SteamID64 = base + account id.
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int account && account != 0)
                {
                    return (SteamId64Base + (uint)account).ToString();
                }

                // Steam closed: the account that logged in last. Older clients mark it "MostRecent" "1";
                // newer ones only keep a "timestamp" per account.
                string? steamPath = SteamScanner.FindSteamPath();
                string file = steamPath == null ? "" : Path.Combine(steamPath, "config", "loginusers.vdf");
                if (File.Exists(file) && VdfParser.GetObject(VdfParser.Parse(File.ReadAllText(file)), "users") is Dictionary<string, object> users)
                {
                    List<(string steamId, Dictionary<string, object> user)> accounts = users
                        .Where(entry => entry.Value is Dictionary<string, object>)
                        .Select(entry => (entry.Key, (Dictionary<string, object>)entry.Value))
                        .ToList();

                    (string steamId, Dictionary<string, object> user) latest = accounts
                        .OrderByDescending(a => VdfParser.GetString(a.user, "MostRecent") == "1")
                        .ThenByDescending(a => long.TryParse(VdfParser.GetString(a.user, "Timestamp"), out long time) ? time : 0)
                        .FirstOrDefault();
                    return latest.steamId;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam account lookup failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>Steam's own language ("english", "german", ...), so names match what Steam shows.</summary>
        private static string Language() =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "Language", null) is string language && language.Length > 0
                ? language
                : "english";

        // =====================================================================
        // Calls
        // =====================================================================

        public async Task<List<OwnedSteamGame>> GetOwnedGamesAsync(string steamId, CancellationToken token)
        {
            using JsonDocument json = await GetAsync(
                $"IPlayerService/GetOwnedGames/v1/?steamid={Uri.EscapeDataString(steamId)}&include_appinfo=true&include_played_free_games=true&format=json",
                token);

            List<OwnedSteamGame> result = new();
            if (!json.RootElement.TryGetProperty("response", out JsonElement response) || !response.TryGetProperty("game_count", out _))
            {
                // Steam answers {} for a private profile read with somebody else's key.
                throw new SteamApiException("No games returned. Is the key from this Steam account?");
            }

            if (!response.TryGetProperty("games", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement game in games.EnumerateArray())
            {
                string? appId = game.TryGetProperty("appid", out JsonElement id) ? id.ToString() : null;
                string? name = GetString(game, "name");
                if (appId == null || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                DateTime? lastPlayed = game.TryGetProperty("rtime_last_played", out JsonElement last) && last.TryGetInt64(out long unix) && unix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
                    : null;
                result.Add(new OwnedSteamGame(appId, name, lastPlayed));
            }

            return result;
        }

        /// <summary>Every achievement of the game with its unlock state. Null when the game has none.</summary>
        public async Task<PlayerAchievements?> GetPlayerAchievementsAsync(string steamId, string appId, CancellationToken token)
        {
            using JsonDocument json = await GetAsync(
                $"ISteamUserStats/GetPlayerAchievements/v1/?steamid={Uri.EscapeDataString(steamId)}&appid={Uri.EscapeDataString(appId)}&l={Language()}",
                token);

            if (!json.RootElement.TryGetProperty("playerstats", out JsonElement stats))
            {
                throw new SteamApiException("Unexpected answer from Steam", retryable: true);
            }

            if (stats.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
            {
                string error = GetString(stats, "error") ?? "Steam couldn't read the achievements";
                if (error.Contains("no stats", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                throw new SteamApiException(error.Contains("not public", StringComparison.OrdinalIgnoreCase)
                    ? "Achievements need Game details set to Public"
                    : error);
            }

            if (!stats.TryGetProperty("achievements", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<SteamAchievement> achievements = new();
            foreach (JsonElement item in list.EnumerateArray())
            {
                string? apiName = GetString(item, "apiname");
                if (apiName == null)
                {
                    continue;
                }

                long unlocked = item.TryGetProperty("unlocktime", out JsonElement time) && time.TryGetInt64(out long unix) ? unix : 0;
                achievements.Add(new SteamAchievement
                {
                    ApiName = apiName,
                    Name = GetString(item, "name") ?? apiName,
                    Description = GetString(item, "description"),
                    Achieved = item.TryGetProperty("achieved", out JsonElement achieved) && achieved.TryGetInt32(out int flag) && flag == 1,
                    UnlockTime = unlocked > 0 ? DateTimeOffset.FromUnixTimeSeconds(unlocked).UtcDateTime : null
                });
            }

            return new PlayerAchievements(GetString(stats, "gameName") ?? "", achievements);
        }

        /// <summary>Achievement api name -> icon url.</summary>
        public async Task<Dictionary<string, string>> GetAchievementIconsAsync(string appId, CancellationToken token)
        {
            using JsonDocument json = await GetAsync($"ISteamUserStats/GetSchemaForGame/v2/?appid={Uri.EscapeDataString(appId)}&l={Language()}", token);

            Dictionary<string, string> icons = new(StringComparer.Ordinal);
            if (json.RootElement.TryGetProperty("game", out JsonElement game)
                && game.TryGetProperty("availableGameStats", out JsonElement stats)
                && stats.TryGetProperty("achievements", out JsonElement list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (GetString(item, "name") is string name && GetString(item, "icon") is string icon)
                    {
                        icons[name] = icon;
                    }
                }
            }

            return icons;
        }

        /// <summary>Saves an icon under %AppData%\CouchDeck\achievements\&lt;appid&gt;\. Null when the download failed.</summary>
        public async Task<string?> DownloadIconAsync(string url, string appId, string apiName, CancellationToken token)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string folder = Path.Combine(IconFolder, appId);
            string path = Path.Combine(folder, string.Concat(apiName.Select(c => invalid.Contains(c) ? '_' : c)) + ".jpg");
            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url, token);
                Directory.CreateDirectory(folder);
                string temp = path + ".part";
                await File.WriteAllBytesAsync(temp, bytes, token);
                File.Move(temp, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Debug.WriteLine($"Achievement icon download failed: {ex.Message}");
                return null;
            }
        }

        private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken token)
        {
            string key = Key ?? throw new SteamApiException("No Steam Web API key yet");

            // The key travels in the query string: never log this url.
            string url = Api + pathAndQuery + "&key=" + Uri.EscapeDataString(key);
            try
            {
                using HttpResponseMessage response = await http.GetAsync(url, token);
                string body = await response.Content.ReadAsStringAsync(token);
                try
                {
                    // Steam also answers 400/403 with JSON (no stats, private profile); callers read those.
                    return JsonDocument.Parse(body);
                }
                catch (JsonException)
                {
                    throw response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SteamApiException("Steam rejected the key"),
                        HttpStatusCode.TooManyRequests => new SteamApiException("Steam is busy, try again later", retryable: true),
                        _ => new SteamApiException($"Steam answered {(int)response.StatusCode}", retryable: (int)response.StatusCode >= 500)
                    };
                }
            }
            catch (HttpRequestException)
            {
                throw new SteamApiException("Can't reach Steam", retryable: true);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                throw new SteamApiException("Steam didn't answer", retryable: true);
            }
        }

        private static string? GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        public void Dispose() => http.Dispose();
    }
}
