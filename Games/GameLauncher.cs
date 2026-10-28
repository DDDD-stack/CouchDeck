using System;
using System.Diagnostics;
using System.IO;
using CouchDeck.Config;

namespace CouchDeck.Games
{
    public sealed class GameLauncher
    {
        private readonly AppConfig config;

        public GameLauncher(AppConfig config)
        {
            this.config = config;
        }

        /// <summary>
        /// Starts the game. For store games the target is a URI, so the process we start is the
        /// launcher, not the game. That's why "close game" works on the foreground window instead
        /// of on a process we started.
        /// </summary>
        public bool Launch(Game game)
        {
            try
            {
                ProcessStartInfo startInfo = new(game.LaunchTarget)
                {
                    UseShellExecute = true, // required for URIs and shell:AppsFolder targets
                    Arguments = game.Arguments ?? string.Empty
                };

                // Many older games break if the working directory isn't their own folder.
                if (game.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.WorkingDirectory = Path.GetDirectoryName(game.LaunchTarget) ?? string.Empty;
                }

                Process.Start(startInfo);

                config.LastLaunched[game.Id] = DateTime.Now;
                ConfigStore.Save(config);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Launch failed for {game.Title}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Hands an install / uninstall URI (steam://install/...) to its store, which shows its own window.</summary>
        public static bool OpenStoreAction(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Store action failed for {target}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Best-effort: finds a running process whose exe lives inside the game's install folder.
        /// Useful for showing "Running" on a tile or a "Close" button next to it.
        /// Reading MainModule can throw for elevated or protected (anti-cheat) processes; those are skipped.
        /// </summary>
        public static Process? FindRunningProcess(Game game)
        {
            if (string.IsNullOrEmpty(game.InstallDir))
            {
                return null;
            }

            string installDir = Path.GetFullPath(game.InstallDir).TrimEnd('\\') + "\\";

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string? exePath = process.MainModule?.FileName;
                    if (exePath != null && exePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return process;
                    }
                }
                catch
                {
                    // access denied / 32-bit vs 64-bit / exited - ignore
                }

                process.Dispose();
            }

            return null;
        }
    }
}
