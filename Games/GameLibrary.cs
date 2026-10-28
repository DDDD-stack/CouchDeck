using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CouchDeck.Config;

namespace CouchDeck.Games
{
    /// <summary>Runs every scanner in parallel and merges the results into one sorted list.</summary>
    public sealed class GameLibrary
    {
        private readonly AppConfig config;
        private readonly List<IGameScanner> scanners;

        public GameLibrary(AppConfig config)
        {
            this.config = config;
            scanners = new List<IGameScanner>
            {
                new SteamScanner(),
                new EpicScanner(),
                new GogScanner(),
                new XboxScanner(),
                new SteamOwnedScanner(config),
                new CustomGamesScanner(config),
                new FolderGamesScanner(config)
                // new UbisoftScanner(), new EaScanner(), new BattleNetScanner() ...
            };
        }

        public async Task<List<Game>> ScanAsync()
        {
            Stopwatch timer = Stopwatch.StartNew();

            Task<IEnumerable<Game>>[] tasks = scanners
                .Select(scanner => Task.Run(() => SafeScan(scanner)))
                .ToArray();

            IEnumerable<Game>[] results = await Task.WhenAll(tasks);

            List<Game> found = results.SelectMany(games => games).ToList();

            // A folder-scanned game that a launcher already knows (same install folder) is a duplicate.
            HashSet<string> knownDirs = found
                .Where(game => game.Source != GameSource.Folder && !string.IsNullOrEmpty(game.InstallDir))
                .Select(game => NormalizeDir(game.InstallDir!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<Game> games = found
                .Where(game => game.Source != GameSource.Folder || !knownDirs.Contains(NormalizeDir(game.InstallDir ?? "")))
                .Where(game => !config.HiddenGameIds.Contains(game.Id))
                .GroupBy(game => game.Id)
                .Select(group => group.FirstOrDefault(game => game.IsInstalled) ?? group.First()) // installed beats "owned"
                .Select(ApplyOverrides)
                .Select(ApplyOwnLaunchHistory)
                .OrderByDescending(game => game.LastPlayed ?? DateTime.MinValue) // recently played first
                .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Debug.WriteLine($"Found {games.Count} games in {timer.ElapsedMilliseconds} ms");
            return games;
        }

        private static IEnumerable<Game> SafeScan(IGameScanner scanner)
        {
            try
            {
                return scanner.Scan().ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{scanner.Name} scanner crashed: {ex}");
                return Enumerable.Empty<Game>();
            }
        }

        private static string NormalizeDir(string dir)
        {
            try
            {
                return Path.GetFullPath(dir).TrimEnd('\\');
            }
            catch
            {
                return dir;
            }
        }

        /// <summary>Title / exe / cover the user changed from the overlay or in config.json.</summary>
        private Game ApplyOverrides(Game game)
        {
            if (!config.GameOverrides.TryGetValue(game.Id, out GameOverride? custom))
            {
                return game;
            }

            return game with
            {
                Title = string.IsNullOrWhiteSpace(custom.Title) ? game.Title : custom.Title,
                TitleIsGuess = string.IsNullOrWhiteSpace(custom.Title) && game.TitleIsGuess,
                LaunchTarget = string.IsNullOrWhiteSpace(custom.LaunchTarget) ? game.LaunchTarget : custom.LaunchTarget,
                Arguments = custom.Arguments ?? game.Arguments,
                CoverImage = !string.IsNullOrWhiteSpace(custom.CoverImage) && File.Exists(custom.CoverImage) ? custom.CoverImage : game.CoverImage
            };
        }

        /// <summary>Use whichever is newer: the store's last-played date or our own launch record.</summary>
        private Game ApplyOwnLaunchHistory(Game game)
        {
            if (!config.LastLaunched.TryGetValue(game.Id, out DateTime launched))
            {
                return game;
            }

            if (game.LastPlayed == null || launched > game.LastPlayed)
            {
                return game with { LastPlayed = launched };
            }

            return game;
        }
    }
}
