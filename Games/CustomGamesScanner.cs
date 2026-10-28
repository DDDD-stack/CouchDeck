using System;
using System.Collections.Generic;
using System.IO;
using CouchDeck.Config;

namespace CouchDeck.Games
{
    /// <summary>Games added by hand in config.json -> CustomGames.</summary>
    public sealed class CustomGamesScanner : IGameScanner
    {
        private readonly AppConfig config;

        public CustomGamesScanner(AppConfig config)
        {
            this.config = config;
        }

        public string Name => "Custom";

        public IEnumerable<Game> Scan()
        {
            List<Game> games = new();

            foreach (CustomGameEntry entry in config.CustomGames)
            {
                if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                bool isExe = entry.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (isExe && !File.Exists(entry.Path))
                {
                    continue; // uninstalled; keep the entry in config but don't show a dead tile
                }

                games.Add(new Game
                {
                    Id = $"custom:{entry.Title.ToLowerInvariant()}",
                    Title = entry.Title,
                    Source = GameSource.Custom,
                    LaunchTarget = entry.Path,
                    Arguments = entry.Arguments,
                    InstallDir = isExe ? Path.GetDirectoryName(entry.Path) : null,
                    CoverImage = entry.CoverImage,
                    TitleIsGuess = entry.AutoTitle
                });
            }

            return games;
        }
    }
}
