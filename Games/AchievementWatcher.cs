using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CouchDeck.Games
{
    /// <summary>The achievements earned in one play session, as the Steam account reports them.</summary>
    public sealed record AchievementSummary(string GameId, string GameTitle, IReadOnlyList<SteamAchievement> Earned, int Unlocked, int Total);

    /// <summary>
    /// Finds out which Steam achievements were earned while a game ran, so CouchDeck can list them when you
    /// quit. Steam shows its own popup in-game; this is the summary afterwards. Nothing is counted here:
    /// unlocks are read from the Steam account (needs a Web API key, see <see cref="SteamWebApi"/>).
    ///
    /// Steam keeps the running game's app id in HKCU\Software\Valve\Steam\RunningAppID (0 = none).
    /// Polling it every few seconds costs nothing and catches games however they were started.
    /// Game starts: remember what's already unlocked. Game ends: ask again; the difference is new.
    /// </summary>
    public sealed class AchievementWatcher : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan SyncDelay = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);

        // Unlock times come from Steam's clock, session times from this PC's.
        private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(2);

        private readonly SteamWebApi api;
        private readonly Timer timer;
        private readonly CancellationTokenSource shutdown = new();
        private volatile Session? current;
        private int polling;

        private sealed class Session
        {
            public Session(string appId, string steamId)
            {
                AppId = appId;
                SteamId = steamId;
            }

            public string AppId { get; }
            public string SteamId { get; }
            public DateTime StartedUtc { get; } = DateTime.UtcNow;

            /// <summary>Api names unlocked when the game started. Null = unknown (offline), unlock times decide.</summary>
            public Task<HashSet<string>?> UnlockedBefore { get; set; } = Task.FromResult<HashSet<string>?>(null);
        }

        public AchievementWatcher(SteamWebApi api)
        {
            this.api = api;
            timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
        }

        /// <summary>Raised on a background thread after a game closed, only when something new was unlocked.</summary>
        public event EventHandler<AchievementSummary>? Earned;

        /// <summary>What Steam refused last time (private profile, bad key). Null when the last read worked.</summary>
        public string? LastError { get; private set; }

        public bool IsGameRunning => current != null;

        private void Poll()
        {
            if (Interlocked.Exchange(ref polling, 1) == 1)
            {
                return;
            }

            try
            {
                string? running = SteamScanner.RunningAppId();
                Session? session = current;
                if (running == session?.AppId)
                {
                    return;
                }

                if (session != null)
                {
                    current = null;
                    _ = FinishAsync(session);
                }

                if (running != null && api.Key != null && api.SteamId is string steamId)
                {
                    Session started = new(running, steamId);
                    started.UnlockedBefore = UnlockedNowAsync(started);
                    current = started;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Achievement watch failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref polling, 0);
            }
        }

        private async Task<HashSet<string>?> UnlockedNowAsync(Session session)
        {
            try
            {
                PlayerAchievements? now = await api.GetPlayerAchievementsAsync(session.SteamId, session.AppId, shutdown.Token);
                return now?.Achievements.Where(a => a.Achieved).Select(a => a.ApiName).ToHashSet() ?? new HashSet<string>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Couldn't read achievements at game start: {ex.Message}");
                return null;
            }
        }

        private async Task FinishAsync(Session session)
        {
            try
            {
                HashSet<string>? before = await session.UnlockedBefore;

                // Give Steam a moment to sync the game's stats after it closed.
                await Task.Delay(SyncDelay, shutdown.Token);
                PlayerAchievements? after = await ReadWithRetriesAsync(session);
                if (after == null)
                {
                    return; // the game has no achievements
                }

                DateTime since = session.StartedUtc - ClockSlack;
                List<SteamAchievement> earned = after.Achievements
                    .Where(a => a.Achieved && (before != null ? !before.Contains(a.ApiName) : a.UnlockTime >= since))
                    .OrderBy(a => a.UnlockTime)
                    .ToList();
                if (earned.Count == 0)
                {
                    return;
                }

                await DownloadIconsAsync(session.AppId, earned);
                Earned?.Invoke(this, new AchievementSummary($"steam:{session.AppId}", after.GameName, earned,
                    after.Achievements.Count(a => a.Achieved), after.Achievements.Count));
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (SteamApiException ex)
            {
                LastError = ex.Message;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Achievement summary failed: {ex.Message}");
            }
        }

        private async Task<PlayerAchievements?> ReadWithRetriesAsync(Session session)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    PlayerAchievements? result = await api.GetPlayerAchievementsAsync(session.SteamId, session.AppId, shutdown.Token);
                    LastError = null;
                    return result;
                }
                catch (SteamApiException ex) when (ex.Retryable && attempt < 4)
                {
                    await Task.Delay(RetryDelay, shutdown.Token);
                }
            }
        }

        /// <summary>Best effort: without icons the list shows a star instead.</summary>
        private async Task DownloadIconsAsync(string appId, List<SteamAchievement> earned)
        {
            try
            {
                Dictionary<string, string> icons = await api.GetAchievementIconsAsync(appId, shutdown.Token);
                foreach (SteamAchievement achievement in earned)
                {
                    if (icons.TryGetValue(achievement.ApiName, out string? url))
                    {
                        achievement.IconPath = await api.DownloadIconAsync(url, appId, achievement.ApiName, shutdown.Token);
                    }
                }
            }
            catch (SteamApiException ex)
            {
                Debug.WriteLine($"Achievement icons unavailable: {ex.Message}");
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            shutdown.Cancel();
        }
    }
}
