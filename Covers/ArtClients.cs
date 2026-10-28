using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CouchDeck.Games;

namespace CouchDeck.Covers
{
    /// <summary>One store entry that matched a title search, with the art we can download for it.</summary>
    public sealed class ArtMatch
    {
        public string Title { get; set; } = "";
        public string Provider { get; set; } = "";
        public string? SteamAppId { get; set; }
        public double Score { get; set; }

        /// <summary>Portrait covers, best first (2x before 1x).</summary>
        public List<string> CoverUrls { get; set; } = new();

        /// <summary>Wide hero/background art, best first.</summary>
        public List<string> HeroUrls { get; set; } = new();

        /// <summary>Landscape capsule, used as a last resort when there's no portrait art.</summary>
        public List<string> LandscapeUrls { get; set; } = new();
    }

    /// <summary>
    /// Steam's public store endpoints. No API key needed.
    ///   storesearch                     title -> app ids
    ///   IStoreBrowseService/GetItems    app id -> exact asset file names (newer games use hashed paths,
    ///                                   so guessing ".../library_600x900.jpg" returns 404 for them)
    /// </summary>
    public sealed class SteamArtClient
    {
        private const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/";
        private const string AssetBaseFallback = "https://shared.fastly.steamstatic.com/store_item_assets/";

        private static readonly string[] NotTheGame =
        {
            "soundtrack", "demo", "playtest", "dedicated server", "sdk", "editor", "dlc", "season pass", "artbook",
            "art book", "wallpaper", "bonus content", "upgrade", "expansion pass", "character pack", "costume", "skin"
        };

        private readonly HttpClient http;
        private readonly SemaphoreSlim searchGate = new(1, 1);
        private DateTime lastSearch = DateTime.MinValue;

        public SteamArtClient(HttpClient http)
        {
            this.http = http;
        }

        /// <summary>
        /// Searches the Steam store for <paramref name="term"/>. Results are ranked against the term and
        /// against <paramref name="fullTitle"/> (edition words included), so "Mafia II Definitive Edition"
        /// prefers the Definitive Edition over the classic one.
        /// </summary>
        public async Task<List<ArtMatch>> SearchAsync(string term, CancellationToken token, string? fullTitle = null)
        {
            List<(string name, string appId)> hits = await StoreSearchAsync(term, token);

            List<(string name, string appId, double score)> ranked = hits
                .Select(hit => (hit.name, hit.appId, score: Math.Max(Rank(term, hit.name), fullTitle == null ? 0 : Rank(fullTitle, hit.name))))
                .Where(hit => hit.score >= 0.5)
                .OrderByDescending(hit => hit.score)
                .Take(5)
                .ToList();

            List<ArtMatch> matches = new();
            if (ranked.Count == 0)
            {
                return matches;
            }

            Dictionary<string, ArtMatch> assets = await GetAssetsAsync(ranked.Select(r => r.appId), token);
            foreach ((string name, string appId, double score) in ranked)
            {
                if (assets.TryGetValue(appId, out ArtMatch? match))
                {
                    match.Title = name;
                    match.Score = score;
                    matches.Add(match);
                }
            }

            return matches;
        }

        /// <summary>Art for a known app id (Steam games we found locally).</summary>
        public async Task<ArtMatch?> ForAppAsync(string appId, CancellationToken token)
        {
            Dictionary<string, ArtMatch> assets = await GetAssetsAsync(new[] { appId }, token);
            return assets.TryGetValue(appId, out ArtMatch? match) ? match : null;
        }

        private static double Rank(string wanted, string candidate)
        {
            double score = TitleCleaner.Similarity(wanted, candidate);

            string lowerWanted = wanted.ToLowerInvariant();
            string lowerCandidate = candidate.ToLowerInvariant();
            foreach (string word in NotTheGame)
            {
                if (lowerCandidate.Contains(word) && !lowerWanted.Contains(word))
                {
                    score -= 0.35;
                }
            }

            return score;
        }

        private async Task<List<(string name, string appId)>> StoreSearchAsync(string term, CancellationToken token)
        {
            // Be polite: storesearch rate-limits bursts. One search at a time, >= 350 ms apart.
            await searchGate.WaitAsync(token);
            try
            {
                TimeSpan wait = lastSearch.AddMilliseconds(350) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token);
                }

                string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc=us&l=english";
                using HttpResponseMessage response = await http.GetAsync(url, token);
                lastSearch = DateTime.UtcNow;

                if (!response.IsSuccessStatusCode)
                {
                    return new List<(string, string)>();
                }

                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                List<(string, string)> result = new();
                if (json.RootElement.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        string? name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                        string? id = item.TryGetProperty("id", out JsonElement i) ? i.ToString() : null;
                        if (name != null && id != null)
                        {
                            result.Add((name, id));
                        }
                    }
                }

                return result;
            }
            finally
            {
                searchGate.Release();
            }
        }

        private async Task<Dictionary<string, ArtMatch>> GetAssetsAsync(IEnumerable<string> appIds, CancellationToken token)
        {
            Dictionary<string, ArtMatch> result = new();
            string ids = string.Join(",", appIds.Select(id => $"{{\"appid\":{id}}}"));
            string input = $"{{\"ids\":[{ids}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
            string url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input);

            try
            {
                using HttpResponseMessage response = await http.GetAsync(url, token);
                if (!response.IsSuccessStatusCode)
                {
                    return result;
                }

                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!json.RootElement.TryGetProperty("response", out JsonElement root)
                    || !root.TryGetProperty("store_items", out JsonElement items))
                {
                    return result;
                }

                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("appid", out JsonElement appIdElement)
                        || !item.TryGetProperty("assets", out JsonElement assets)
                        || !assets.TryGetProperty("asset_url_format", out JsonElement formatElement))
                    {
                        continue;
                    }

                    string appId = appIdElement.ToString();
                    string format = formatElement.GetString() ?? "";

                    ArtMatch match = new()
                    {
                        Provider = "Steam",
                        SteamAppId = appId,
                        Title = item.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : ""
                    };

                    AddAsset(match.CoverUrls, assets, format, "library_capsule_2x", "library_capsule");
                    AddAsset(match.HeroUrls, assets, format, "library_hero_2x", "library_hero");
                    AddAsset(match.LandscapeUrls, assets, format, "main_capsule_2x", "main_capsule", "header_2x", "header");

                    result[appId] = match;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Steam GetItems failed: {ex.Message}");
            }

            return result;
        }

        private static void AddAsset(List<string> target, JsonElement assets, string format, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (assets.TryGetProperty(key, out JsonElement file) && file.GetString() is string fileName && fileName.Length > 0)
                {
                    string path = format.Replace("${FILENAME}", fileName);
                    target.Add(AssetBase + path);
                    target.Add(AssetBaseFallback + path);
                }
            }
        }
    }

    /// <summary>
    /// SteamGridDB (optional, needs a free API key in config.json -> SteamGridDbApiKey).
    /// Has community art for games that aren't on Steam and many alternatives per game.
    /// </summary>
    public sealed class SteamGridDbClient
    {
        private const string Api = "https://www.steamgriddb.com/api/v2/";
        private readonly HttpClient http;
        private readonly Func<string?> apiKey;

        public SteamGridDbClient(HttpClient http, Func<string?> apiKey)
        {
            this.http = http;
            this.apiKey = apiKey;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey());

        public async Task<List<ArtMatch>> SearchAsync(string title, CancellationToken token)
        {
            List<ArtMatch> matches = new();
            if (!IsConfigured)
            {
                return matches;
            }

            using JsonDocument? search = await GetJsonAsync($"search/autocomplete/{Uri.EscapeDataString(title)}", token);
            if (search == null || !search.RootElement.TryGetProperty("data", out JsonElement data))
            {
                return matches;
            }

            List<(int id, string name, double score)> ranked = data.EnumerateArray()
                .Select(game => (id: game.GetProperty("id").GetInt32(), name: game.GetProperty("name").GetString() ?? ""))
                .Select(game => (game.id, game.name, score: TitleCleaner.Similarity(title, game.name)))
                .Where(game => game.score >= 0.5)
                .OrderByDescending(game => game.score)
                .Take(3)
                .ToList();

            foreach ((int id, string name, double score) in ranked)
            {
                ArtMatch match = new() { Provider = "SteamGridDB", Title = name, Score = score };
                match.CoverUrls.AddRange(await GetUrlsAsync($"grids/game/{id}?dimensions=600x900&types=static&nsfw=false&humor=false", token));
                match.HeroUrls.AddRange(await GetUrlsAsync($"heroes/game/{id}?types=static&nsfw=false&humor=false", token));
                if (match.CoverUrls.Count > 0)
                {
                    matches.Add(match);
                }
            }

            return matches;
        }

        private async Task<List<string>> GetUrlsAsync(string path, CancellationToken token)
        {
            using JsonDocument? json = await GetJsonAsync(path, token);
            if (json == null || !json.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return data.EnumerateArray()
                .Select(item => item.TryGetProperty("url", out JsonElement url) ? url.GetString() : null)
                .Where(url => !string.IsNullOrEmpty(url))
                .Take(6)
                .Cast<string>()
                .ToList();
        }

        private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken token)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, Api + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey());
                using HttpResponseMessage response = await http.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"SteamGridDB {path} failed: {ex.Message}");
                return null;
            }
        }
    }
}
