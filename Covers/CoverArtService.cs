using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CouchDeck.Config;
using CouchDeck.Games;

namespace CouchDeck.Covers
{
    /// <summary>A piece of art we could use for a game: one design, possibly from several mirrors.</summary>
    public sealed class CoverCandidate
    {
        public string Title { get; set; } = "";
        public string Provider { get; set; } = "";
        public double Score { get; set; }
        public List<string> Urls { get; set; } = new();
        public List<string> HeroUrls { get; set; } = new();
    }

    /// <summary>What we know about one game's art. Saved in %AppData%\CouchDeck\covers\index.json.</summary>
    public sealed class CoverRecord
    {
        public string? CoverPath { get; set; }
        public string? HeroPath { get; set; }
        public string? MatchedTitle { get; set; }
        public double MatchScore { get; set; }
        public string? SearchedFor { get; set; }
        public List<CoverCandidate> Candidates { get; set; } = new();
        public int CandidateIndex { get; set; }
        public bool NotFound { get; set; }
        public DateTime LastAttempt { get; set; }

        /// <summary>Picked with "Next cover": wins over the launcher's own art.</summary>
        public bool UserChosen { get; set; }

        /// <summary>The game wasn't installed yet, so its (big) background art was skipped. Fetched once it is.</summary>
        public bool HeroSkipped { get; set; }
    }

    public sealed record ArtUpdate(string GameId, string? CoverPath, string? HeroPath, string? OfficialTitle);

    /// <summary>
    /// Finds, downloads and caches cover art for every game that doesn't have any: games found in
    /// game folders, games added by hand, Epic/GOG games, and Steam games Steam hasn't cached yet.
    ///
    /// Flow per game: clean up the title -> search Steam store (and SteamGridDB if a key is set)
    /// -> rank results by title similarity -> download the best portrait cover (+ wide hero art)
    /// -> cache on disk -> raise <see cref="ArtUpdated"/> so the tile updates live.
    /// Results (including "nothing found") are remembered, so each game hits the network once.
    /// </summary>
    public sealed class CoverArtService : IDisposable
    {
        private const int Workers = 3;
        private static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(3);

        private readonly AppConfig config;
        private readonly HttpClient http;
        private readonly SteamArtClient steam;
        private readonly SteamGridDbClient gridDb;
        private readonly Channel<(Game game, bool force)> queue = Channel.CreateUnbounded<(Game, bool)>();
        private readonly ConcurrentDictionary<string, byte> inFlight = new();
        private readonly CancellationTokenSource shutdown = new();
        private readonly object indexLock = new();
        private readonly Dictionary<string, CoverRecord> index;
        private int pendingCount;

        public CoverArtService(AppConfig config)
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
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CouchDeck/2.0 (+cover art lookup)");

            steam = new SteamArtClient(http);
            gridDb = new SteamGridDbClient(http, () => config.SteamGridDbApiKey);

            Directory.CreateDirectory(CacheFolder);
            index = LoadIndex();

            for (int i = 0; i < Workers; i++)
            {
                _ = Task.Run(WorkerLoop);
            }
        }

        public static string CacheFolder { get; } = Path.Combine(ConfigStore.FolderPath, "covers");
        private static string IndexPath => Path.Combine(CacheFolder, "index.json");

        /// <summary>Raised on a background thread whenever a game's art changes.</summary>
        public event EventHandler<ArtUpdate>? ArtUpdated;

        /// <summary>Raised on a background thread: number of games still waiting for art.</summary>
        public event EventHandler<int>? QueueChanged;

        public int Pending => Volatile.Read(ref pendingCount);

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>Fills in art we already have on disk. Cheap, call it on every scan result.</summary>
        public Game ApplyCached(Game game)
        {
            CoverRecord? record = GetRecord(game.Id);
            if (record == null)
            {
                return game;
            }

            bool userPickedFile = config.GameOverrides.TryGetValue(game.Id, out GameOverride? custom)
                && !string.IsNullOrWhiteSpace(custom.CoverImage);
            bool useRecord = IsLocalFile(record.CoverPath)
                && !userPickedFile
                && (record.UserChosen || !IsLocalFile(game.CoverImage));

            return game with
            {
                CoverImage = useRecord ? record.CoverPath : game.CoverImage,
                HeroImage = !IsLocalFile(game.HeroImage) && IsLocalFile(record.HeroPath) ? record.HeroPath : game.HeroImage,
                Title = OfficialTitleFor(game, record) ?? game.Title
            };
        }

        /// <summary>Queues every game that is missing art. Already-tried games are skipped unless forced.</summary>
        public void EnqueueMissing(IEnumerable<Game> games, bool force = false)
        {
            if (!config.AutoFetchCovers && !force)
            {
                return;
            }

            foreach (Game game in games)
            {
                if (force || NeedsWork(game))
                {
                    Enqueue(game, force);
                }
            }
        }

        /// <summary>Forget what we found and search again from scratch.</summary>
        public void Refetch(Game game)
        {
            lock (indexLock)
            {
                if (index.Remove(game.Id, out CoverRecord? old))
                {
                    DeleteFiles(old);
                }
            }

            Enqueue(game with { CoverImage = IsDownloaded(game.CoverImage) ? null : game.CoverImage, HeroImage = null }, force: true);
        }

        /// <summary>Switch to the next candidate (a different design, or a different match if the first was wrong).</summary>
        public async Task<bool> NextCandidateAsync(Game game)
        {
            CoverRecord? record = GetRecord(game.Id);
            if (record == null || record.Candidates.Count == 0)
            {
                record = await SearchAsync(game, shutdown.Token);
                if (record.Candidates.Count == 0)
                {
                    return false;
                }

                record.CandidateIndex = -1;
            }

            int count = record.Candidates.Count;
            if (count == 1 && record.CandidateIndex == 0)
            {
                return false; // nothing else to show (a SteamGridDB key adds alternatives)
            }

            for (int step = 1; step <= count; step++)
            {
                int next = (record.CandidateIndex + step) % count;
                record.UserChosen = true;
                if (await ApplyCandidateAsync(game, record, next, shutdown.Token))
                {
                    return true;
                }
            }

            return false;
        }

        public (int index, int count, string? title) CandidateInfo(string gameId)
        {
            CoverRecord? record = GetRecord(gameId);
            return record == null ? (0, 0, null) : (record.CandidateIndex, record.Candidates.Count, record.MatchedTitle);
        }

        /// <summary>Deletes every downloaded image and the index.</summary>
        public void ClearCache()
        {
            lock (indexLock)
            {
                foreach (CoverRecord record in index.Values)
                {
                    DeleteFiles(record);
                }

                index.Clear();
                SaveIndex();
            }
        }

        public static bool IsDownloaded(string? path) =>
            path != null && path.StartsWith(CacheFolder, StringComparison.OrdinalIgnoreCase);

        // =====================================================================
        // Worker
        // =====================================================================

        private bool NeedsWork(Game game)
        {
            CoverRecord? record = GetRecord(game.Id);
            bool needsCover = !IsLocalFile(game.CoverImage);
            bool needsHero = WantsHero(game) && !IsLocalFile(game.HeroImage);

            if (!needsCover && !needsHero)
            {
                return false;
            }

            if (record == null)
            {
                return true;
            }

            if (record.NotFound)
            {
                // A launcher now offers art (e.g. Epic catalog) -> try that right away.
                return (IsUrl(game.CoverImage) && record.Candidates.Count == 0)
                    || DateTime.UtcNow - record.LastAttempt > RetryNotFoundAfter;
            }

            // Already searched (hero included, even if there was none): only a lost cover file needs work,
            // or background art skipped while the game wasn't installed.
            return (needsCover && !IsLocalFile(record.CoverPath)) || (needsHero && record.HeroSkipped);
        }

        /// <summary>Background art is big; Library games (owned, not installed) get it once they're installed.</summary>
        private bool WantsHero(Game game) => config.DownloadHeroArt && game.IsInstalled;

        private void Enqueue(Game game, bool force)
        {
            if (!inFlight.TryAdd(game.Id, 0))
            {
                return;
            }

            Interlocked.Increment(ref pendingCount);
            QueueChanged?.Invoke(this, Pending);
            queue.Writer.TryWrite((game, force));
        }

        private async Task WorkerLoop()
        {
            try
            {
                await foreach ((Game game, bool force) in queue.Reader.ReadAllAsync(shutdown.Token))
                {
                    try
                    {
                        await ProcessAsync(game, force, shutdown.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Cover lookup failed for {game.Title}: {ex.Message}");
                    }
                    finally
                    {
                        inFlight.TryRemove(game.Id, out _);
                        Interlocked.Decrement(ref pendingCount);
                        QueueChanged?.Invoke(this, Pending);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }

        private async Task ProcessAsync(Game game, bool force, CancellationToken token)
        {
            CoverRecord? existing = GetRecord(game.Id);
            CoverRecord record = existing != null && !force && existing.Candidates.Count > 0
                ? existing
                : await SearchAsync(game, token);

            bool needsCover = !IsLocalFile(game.CoverImage) && !IsLocalFile(record.CoverPath);
            if (record.Candidates.Count == 0)
            {
                record.NotFound = true;
                record.LastAttempt = DateTime.UtcNow;
                Store(game.Id, record);
                return;
            }

            if (needsCover)
            {
                for (int i = 0; i < record.Candidates.Count; i++)
                {
                    if (await ApplyCandidateAsync(game, record, i, token))
                    {
                        return;
                    }
                }

                record.NotFound = true;
                record.LastAttempt = DateTime.UtcNow;
                Store(game.Id, record);
                return;
            }

            // Cover is fine (e.g. Steam's own cache); just pick up hero art.
            if (WantsHero(game) && !IsLocalFile(game.HeroImage) && record.HeroPath == null)
            {
                CoverCandidate best = record.Candidates[Math.Max(0, record.CandidateIndex)];
                record.HeroPath = await DownloadFirstAsync(best.HeroUrls, KeyFor(game.Id) + "_hero", token);
                record.HeroSkipped = false;
                record.LastAttempt = DateTime.UtcNow;
                Store(game.Id, record);
                Raise(game, record);
            }
        }

        private async Task<CoverRecord> SearchAsync(Game game, CancellationToken token)
        {
            List<ArtMatch> matches = new();

            if (game.SteamAppId != null)
            {
                ArtMatch? own = await steam.ForAppAsync(game.SteamAppId, token);
                if (own != null)
                {
                    own.Score = 1;
                    own.Title = game.Title;
                    matches.Add(own);
                }
            }

            string term = TitleCleaner.SearchTerm(game.Title);
            if (matches.Count == 0 && term.Length >= 2)
            {
                if (gridDb.IsConfigured)
                {
                    matches.AddRange(await gridDb.SearchAsync(term, token));
                }

                string fullTitle = TitleCleaner.Clean(game.Title);
                List<ArtMatch> steamMatches = await steam.SearchAsync(term, token, fullTitle);
                if (steamMatches.Count == 0 && TitleCleaner.ShortSearchTerm(term) is string shortTerm)
                {
                    steamMatches = await steam.SearchAsync(shortTerm, token, fullTitle);
                    steamMatches.ForEach(match => match.Score -= 0.1); // a weaker kind of match
                }

                matches.AddRange(steamMatches);
            }

            CoverRecord record = new() { SearchedFor = term, LastAttempt = DateTime.UtcNow };

            // Art URLs the launcher (Epic catalog) or config.json already gave us come first.
            if (IsUrl(game.CoverImage))
            {
                record.Candidates.Add(new CoverCandidate
                {
                    Title = game.Title, Provider = game.SourceLabel, Score = 1,
                    Urls = new List<string> { game.CoverImage! },
                    HeroUrls = IsUrl(game.HeroImage) ? new List<string> { game.HeroImage! } : new List<string>()
                });
            }

            // Best title match first. On a tie, official Steam art before community art.
            foreach (ArtMatch match in matches
                         .OrderByDescending(m => Math.Round(m.Score, 2))
                         .ThenBy(m => m.Provider == "Steam" ? 0 : 1))
            {
                if (match.Provider == "SteamGridDB")
                {
                    // Every SteamGridDB url is a different design: one candidate each.
                    foreach (string url in match.CoverUrls)
                    {
                        record.Candidates.Add(new CoverCandidate
                        {
                            Title = match.Title, Provider = match.Provider, Score = match.Score,
                            Urls = new List<string> { url }, HeroUrls = match.HeroUrls
                        });
                    }
                }
                else
                {
                    // Steam: urls are the same art at 2x/1x on two mirrors. No portrait art (demos,
                    // old games) -> fall back to the landscape capsule; the tile letterboxes it.
                    List<string> urls = match.CoverUrls.Count > 0 ? match.CoverUrls : match.LandscapeUrls;
                    if (urls.Count > 0)
                    {
                        record.Candidates.Add(new CoverCandidate
                        {
                            Title = match.Title, Provider = match.Provider, Score = match.Score,
                            Urls = urls, HeroUrls = match.HeroUrls
                        });
                    }
                }
            }

            return record;
        }

        private async Task<bool> ApplyCandidateAsync(Game game, CoverRecord record, int candidateIndex, CancellationToken token)
        {
            CoverCandidate candidate = record.Candidates[candidateIndex];
            string key = KeyFor(game.Id);

            // New file name per candidate: WPF caches images by path, so overwriting wouldn't show.
            string? cover = await DownloadFirstAsync(candidate.Urls, $"{key}_{candidateIndex}", token);
            if (cover == null)
            {
                return false;
            }

            string? hero = record.HeroPath;
            bool sameGame = record.MatchedTitle == null || record.MatchedTitle == candidate.Title;
            if (WantsHero(game) && !IsLocalFile(game.HeroImage) && (hero == null || !sameGame || !File.Exists(hero)))
            {
                hero = await DownloadFirstAsync(candidate.HeroUrls, $"{key}_hero_{candidateIndex}", token) ?? (sameGame ? hero : null);
            }

            record.HeroSkipped = config.DownloadHeroArt && !game.IsInstalled && !IsLocalFile(game.HeroImage) && hero == null;

            string? oldCover = record.CoverPath;
            string? oldHero = record.HeroPath;

            record.CoverPath = cover;
            record.HeroPath = hero;
            record.MatchedTitle = candidate.Title;
            record.MatchScore = candidate.Score;
            record.CandidateIndex = candidateIndex;
            record.NotFound = false;
            record.LastAttempt = DateTime.UtcNow;
            Store(game.Id, record);

            TryDelete(oldCover, keep: cover);
            TryDelete(oldHero, keep: hero);

            Raise(game, record);
            return true;
        }

        private void Raise(Game game, CoverRecord record)
        {
            bool launcherArtWins = IsLocalFile(game.CoverImage) && !IsDownloaded(game.CoverImage) && !record.UserChosen;
            ArtUpdated?.Invoke(this, new ArtUpdate(
                game.Id,
                launcherArtWins ? null : record.CoverPath,
                record.HeroPath,
                OfficialTitleFor(game, record)));
        }

        private string? OfficialTitleFor(Game game, CoverRecord record)
        {
            bool confident = record.MatchScore >= 0.72;
            return game.TitleIsGuess && config.UseOfficialNames && confident && !string.IsNullOrWhiteSpace(record.MatchedTitle)
                ? record.MatchedTitle
                : null;
        }

        // =====================================================================
        // Download / cache
        // =====================================================================

        private async Task<string?> DownloadFirstAsync(IEnumerable<string> urls, string fileKey, CancellationToken token)
        {
            foreach (string url in urls)
            {
                try
                {
                    using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    string? type = response.Content.Headers.ContentType?.MediaType;
                    if (!response.IsSuccessStatusCode || type == null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
                    if (bytes.Length < 2048)
                    {
                        continue; // placeholder / broken image
                    }

                    string extension = type switch
                    {
                        "image/png" => ".png",
                        "image/webp" => ".webp",
                        "image/gif" => ".gif",
                        _ => ".jpg"
                    };

                    string path = Path.Combine(CacheFolder, fileKey + extension);
                    string temp = path + ".part";
                    await File.WriteAllBytesAsync(temp, bytes, token);
                    File.Move(temp, path, overwrite: true);
                    return path;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Download failed {url}: {ex.Message}");
                }
            }

            return null;
        }

        private static bool IsUrl(string? value) =>
            value != null && (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

        private static string KeyFor(string gameId)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(gameId));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        private static bool IsLocalFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string? path, string? keep)
        {
            if (path == null || path == keep || !IsDownloaded(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // in use by an open image; it'll be overwritten or cleaned up later
            }
        }

        private static void DeleteFiles(CoverRecord record)
        {
            TryDelete(record.CoverPath, null);
            TryDelete(record.HeroPath, null);
        }

        private CoverRecord? GetRecord(string gameId)
        {
            lock (indexLock)
            {
                return index.TryGetValue(gameId, out CoverRecord? record) ? record : null;
            }
        }

        private void Store(string gameId, CoverRecord record)
        {
            lock (indexLock)
            {
                index[gameId] = record;
                SaveIndex();
            }
        }

        private Dictionary<string, CoverRecord> LoadIndex()
        {
            try
            {
                if (File.Exists(IndexPath))
                {
                    return JsonSerializer.Deserialize<Dictionary<string, CoverRecord>>(File.ReadAllText(IndexPath))
                        ?? new Dictionary<string, CoverRecord>();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cover index unreadable, starting fresh: {ex.Message}");
            }

            return new Dictionary<string, CoverRecord>();
        }

        // Caller holds indexLock.
        private void SaveIndex()
        {
            try
            {
                string temp = IndexPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(index));
                File.Move(temp, IndexPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cover index save failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            shutdown.Cancel();
            queue.Writer.TryComplete();
            http.Dispose();
        }
    }
}
