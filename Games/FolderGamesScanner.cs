using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CouchDeck.Config;

namespace CouchDeck.Games
{
    /// <summary>
    /// Games that no launcher knows about: portable installs, DRM-free downloads, repacks.
    /// For every folder in config.GameFolders:
    ///   - folder has a game .exe directly inside  -> the folder itself is one game
    ///   - otherwise                               -> every sub-folder is one game
    /// The title comes from the folder name (cleaned up by TitleCleaner) and the main .exe is
    /// picked by <see cref="ExeFinder"/>. Cover art is fetched afterwards by CoverArtService.
    /// </summary>
    public sealed class FolderGamesScanner : IGameScanner
    {
        private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "$recycle.bin", "system volume information", "_commonredist", "commonredist", "redist", "redistributables",
            "directx", "vcredist", "dotnet", "support", "__installer", "installers", "_installer", "prerequisites",
            "steamapps", "steamlibrary", "epic games", "gog galaxy", "xboxgames", "windowsapps", "program files",
            "program files (x86)", "windows", "drivers", "temp", "tmp", "downloads", "torrents", "_redist", "msi"
        };

        private readonly AppConfig config;

        public FolderGamesScanner(AppConfig config)
        {
            this.config = config;
        }

        public string Name => "Folders";

        public IEnumerable<Game> Scan()
        {
            List<Game> games = new();

            foreach (string root in config.GameFolders.ToList())
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    // A single game's folder added directly?
                    if (ExeFinder.FindMainExe(root, Path.GetFileName(root.TrimEnd('\\')), maxDepth: 0) is string directExe)
                    {
                        games.Add(MakeGame(root, directExe));
                        continue;
                    }

                    foreach (string folder in Directory.EnumerateDirectories(root))
                    {
                        string name = Path.GetFileName(folder);
                        if (IgnoredFolders.Contains(name) || name.StartsWith('.') || IsHidden(folder))
                        {
                            continue;
                        }

                        if (ExeFinder.FindMainExe(folder, name) is string exe)
                        {
                            games.Add(MakeGame(folder, exe));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Folder scan failed for {root}: {ex.Message}");
                }
            }

            return games;
        }

        private static Game MakeGame(string folder, string exe)
        {
            string folderName = Path.GetFileName(folder.TrimEnd('\\'));
            return new Game
            {
                Id = IdFor(folder),
                Title = TitleCleaner.Clean(folderName),
                Source = GameSource.Folder,
                LaunchTarget = exe,
                InstallDir = folder,
                TitleIsGuess = true
            };
        }

        public static string IdFor(string folder) => "folder:" + Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant();

        private static bool IsHidden(string folder)
        {
            try
            {
                return (File.GetAttributes(folder) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Picks the .exe that most likely starts the game. Scores every .exe up to a few levels deep:
    /// shallow beats deep (Unreal games have a small launcher stub at the top that sets things up
    /// right), a name that looks like the folder wins, installers/redistributables/crash
    /// reporters/anti-cheat helpers are skipped.
    /// </summary>
    public static class ExeFinder
    {
        private static readonly string[] SkipExePrefixes =
        {
            "unins", "setup", "install", "vc_redist", "vcredist", "dxsetup", "dxwebsetup", "dotnet", "ndp", "oalinst",
            "physx", "crashreport", "crashhandler", "unitycrashhandler", "crashpad", "bugreport", "bugsplat",
            "errorreport", "easyanticheat", "eac_", "beservice", "battleye", "ue4prereq", "ueprereq", "uninstall",
            "quicksfv", "7z", "unrar", "winrar", "python", "cefprocess", "cefsharp", "qtwebengine", "notification_helper",
            "steamerrorreporter", "steamservice", "gameoverlayui", "dxdiag", "zfgamebrowser", "dowser",
            "redist", "touchup", "cleanup", "vc_", "directx", "xnafx", "openal", "activation", "createdump",
            "wow_helper", "nwjc"
        };

        private static readonly HashSet<string> SkipExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "helper", "service", "elevate", "subprocess", "payload", "register", "crashsender", "reporter"
        };

        private static readonly string[] SkipFolderNames =
        {
            "_commonredist", "commonredist", "redist", "redistributables", "directx", "dotnet", "vcredist",
            "__installer", "installer", "installers", "support", "prerequisites", "easyanticheat", "battleye",
            "thirdparty", "extras", "docs", "manual", "soundtrack", "ost", "__macosx", "mono", "jre", "java",
            "python", "crashpad", "crashreporter", "tools", "sdk", "editor", "_redist", "artbook", "bonus"
        };

        public static string? FindMainExe(string folder, string folderName, int maxDepth = 4)
        {
            string wanted = TitleCleaner.Normalize(TitleCleaner.Clean(folderName));

            string? best = null;
            double bestScore = double.MinValue;
            int examined = 0;

            Queue<(string dir, int depth)> queue = new();
            queue.Enqueue((folder, 0));

            while (queue.Count > 0 && examined < 3000)
            {
                (string dir, int depth) = queue.Dequeue();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.exe").ToList();
                }
                catch
                {
                    continue; // access denied etc.
                }

                foreach (string file in files)
                {
                    examined++;
                    double? score = Score(file, depth, wanted);
                    if (score is double s && s > bestScore)
                    {
                        bestScore = s;
                        best = file;
                    }
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string subName = Path.GetFileName(sub).ToLowerInvariant();
                        if (!SkipFolderNames.Contains(subName) && !subName.StartsWith('$') && !subName.StartsWith('.'))
                        {
                            queue.Enqueue((sub, depth + 1));
                        }
                    }
                }
                catch
                {
                    // ignore unreadable folders
                }
            }

            return best;
        }

        private static double? Score(string file, int depth, string wantedNormalized)
        {
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (SkipExeNames.Contains(name) || SkipExePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return null;
            }

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch
            {
                return null;
            }

            if (size < 40 * 1024)
            {
                return null; // tiny stubs / tools
            }

            double score = -15 * depth;
            score += 6 * Math.Log10(size / (1024.0 * 1024.0) + 1);

            string normalizedName = TitleCleaner.Normalize(name.Replace("-win64-shipping", "").Replace("-shipping", ""));
            if (wantedNormalized.Length > 0 && normalizedName.Length > 0)
            {
                if (normalizedName == wantedNormalized)
                {
                    score += 35;
                }
                else if (wantedNormalized.Contains(normalizedName) || normalizedName.Contains(wantedNormalized))
                {
                    score += 22;
                }
                else
                {
                    score += 20 * TitleCleaner.Similarity(normalizedName, wantedNormalized);
                }
            }

            if (name.Contains("launcher") || name.Contains("config") || name.Contains("settings") || name.Contains("editor"))
            {
                score -= 12;
            }

            if (name.Contains("server") || name.Contains("benchmark") || name.Contains("test") || name.Contains("debug"))
            {
                score -= 18;
            }

            if (name.EndsWith("-win64-shipping") || name.EndsWith("_x64") || name.EndsWith("64"))
            {
                score += 3;
            }

            return score;
        }
    }
}
