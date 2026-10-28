using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CouchDeck.UI;

namespace CouchDeck.Games
{
    /// <summary>
    /// Notices when a game started from CouchDeck closes, so the overlay can come back like a console's home screen.
    ///   Steam games: HKCU\Software\Valve\Steam\RunningAppID, which Steam keeps set while the game runs.
    ///   Other games: any process running from the game's folder (or its .exe, when there's no folder).
    /// The game counts as closed only after it's been gone for a few seconds, which covers launchers that
    /// hand over to the real game and games that restart themselves.
    /// </summary>
    public sealed class GameExitWatcher : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10); // Steam may update the game first
        private const int ClosedAfterMisses = 2;

        private readonly Timer timer;
        private volatile Session? current;
        private int polling;

        private sealed class Session
        {
            public Session(Game game, string? folder, string? exe)
            {
                Game = game;
                Folder = folder;
                Exe = exe;
            }

            public Game Game { get; }
            public string? Folder { get; } // with a trailing backslash
            public string? Exe { get; }
            public bool IsSteam => Folder == null && Exe == null;
            public DateTime StartedUtc { get; } = DateTime.UtcNow;
            public bool SeenRunning { get; set; }
            public int Misses { get; set; }

            /// <summary>The game's process found last time: checking it is cheap, listing every process isn't.</summary>
            public Process? Process { get; set; }
        }

        public GameExitWatcher()
        {
            timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
        }

        /// <summary>Raised on a background thread once the game being watched has closed.</summary>
        public event EventHandler<Game>? GameClosed;

        /// <summary>Starts watching a game that was just launched (replaces the previous one). Games with nothing to recognise them by aren't watched.</summary>
        public void Watch(Game game)
        {
            string? folder = null;
            string? exe = null;

            if (game.Source != GameSource.Steam || game.SteamAppId == null)
            {
                try
                {
                    if (!string.IsNullOrEmpty(game.InstallDir) && Directory.Exists(game.InstallDir))
                    {
                        string full = Path.GetFullPath(game.InstallDir).TrimEnd('\\') + "\\";
                        folder = full.Length > 3 ? full : null; // not a whole drive
                    }

                    if (folder == null && game.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exe = Path.GetFullPath(game.LaunchTarget);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Can't watch {game.Title}: {ex.Message}");
                }

                if (folder == null && exe == null)
                {
                    Stop();
                    return;
                }
            }

            Session? previous = Interlocked.Exchange(ref current, new Session(game, folder, exe));
            previous?.Process?.Dispose();
        }

        public void Stop() => Interlocked.Exchange(ref current, null)?.Process?.Dispose();

        private void Poll()
        {
            Session? session = current;
            if (session == null || Interlocked.Exchange(ref polling, 1) == 1)
            {
                return;
            }

            try
            {
                if (IsRunning(session))
                {
                    session.SeenRunning = true;
                    session.Misses = 0;
                }
                else if (!session.SeenRunning)
                {
                    if (DateTime.UtcNow - session.StartedUtc > StartTimeout)
                    {
                        Finish(session, closed: false); // never started (cancelled, or failed to launch)
                    }
                }
                else if (++session.Misses >= ClosedAfterMisses)
                {
                    Finish(session, closed: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Game exit watch failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref polling, 0);
            }
        }

        private void Finish(Session session, bool closed)
        {
            if (Interlocked.CompareExchange(ref current, null, session) != session)
            {
                return; // another game was launched meanwhile
            }

            session.Process?.Dispose();
            if (closed)
            {
                GameClosed?.Invoke(this, session.Game);
            }
        }

        private static bool IsRunning(Session session)
        {
            if (session.IsSteam)
            {
                return SteamScanner.RunningAppId() == session.Game.SteamAppId;
            }

            if (session.Process is Process known && IsAlive(known))
            {
                return true;
            }

            session.Process?.Dispose();
            session.Process = FindProcess(session);
            return session.Process != null;
        }

        private static bool IsAlive(Process process)
        {
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false; // can't open it: list the processes again instead
            }
        }

        private static Process? FindProcess(Session session)
        {
            Process? found = null;
            foreach (Process process in Process.GetProcesses())
            {
                if (found == null && Matches(session, ShellIcons.ProcessPath(process.Id)))
                {
                    found = process;
                    continue;
                }

                process.Dispose();
            }

            return found;
        }

        private static bool Matches(Session session, string? path) =>
            path != null && (session.Exe != null
                ? string.Equals(path, session.Exe, StringComparison.OrdinalIgnoreCase)
                : path.StartsWith(session.Folder!, StringComparison.OrdinalIgnoreCase));

        public void Dispose()
        {
            timer.Dispose();
            Stop();
        }
    }
}
