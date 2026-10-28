using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CouchDeck.Games
{
    /// <summary>
    /// GOG installers (with or without Galaxy) register every game under
    ///   HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;gameId&gt;
    /// with values gameName, exe, path, workingDir, launchParam.
    /// GOG games are DRM-free, so we start the exe directly.
    /// </summary>
    public sealed class GogScanner : IGameScanner
    {
        public string Name => "GOG";

        public IEnumerable<Game> Scan()
        {
            List<Game> games = new();

            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
                if (root == null)
                {
                    return games;
                }

                foreach (string gameId in root.GetSubKeyNames())
                {
                    using RegistryKey? key = root.OpenSubKey(gameId);
                    if (key == null)
                    {
                        continue;
                    }

                    string? name = key.GetValue("gameName") as string;
                    string? exe = key.GetValue("exe") as string;
                    string? path = key.GetValue("path") as string;
                    string? launchParam = key.GetValue("launchParam") as string;

                    if (name == null || exe == null || !File.Exists(exe))
                    {
                        continue;
                    }

                    games.Add(new Game
                    {
                        Id = $"gog:{gameId}",
                        Title = name,
                        Source = GameSource.Gog,
                        LaunchTarget = exe,
                        Arguments = string.IsNullOrWhiteSpace(launchParam) ? null : launchParam,
                        InstallDir = path
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GOG scan failed: {ex}");
            }

            return games;
        }
    }
}
