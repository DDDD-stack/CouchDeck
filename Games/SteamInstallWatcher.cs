using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace CouchDeck.Games
{
    public enum DownloadPhase
    {
        Downloading,
        Installing, // everything is downloaded, Steam is putting the files in place
        Paused
    }

    /// <summary>A Steam download (a new install or an update) in progress. Fraction is 0..1.</summary>
    public sealed record SteamDownload(string AppId, double Fraction, DownloadPhase Phase);

    /// <summary>
    /// Notices when a Steam game finishes installing or gets uninstalled, so its tile moves between the
    /// Library and Games pages without a manual rescan, and reports download progress for the tiles.
    /// Steam rewrites appmanifest_&lt;appid&gt;.acf all the time while downloading, so only a change of the
    /// "fully installed" flag counts as a move. Progress comes from the same file: BytesDownloaded / BytesToDownload,
    /// then BytesStaged / BytesToStage while Steam puts the files in place.
    /// </summary>
    public sealed class SteamInstallWatcher : IDisposable
    {
        private const int StateUpdateRunning = 256;
        private const int StateUpdatePaused = 512;

        private static readonly Regex ManifestName = new(@"^appmanifest_(\d+)\.acf", RegexOptions.IgnoreCase);
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

        private readonly List<FileSystemWatcher> watchers = new();
        private readonly ConcurrentDictionary<string, string> touched = new(StringComparer.OrdinalIgnoreCase); // manifest path -> app id
        private readonly Timer settleTimer;
        private readonly object installedLock = new();
        private HashSet<string> installed = new();

        // Manifests worth re-reading for progress: written to lately, or downloading. Manifest path -> app id.
        private readonly ConcurrentDictionary<string, string> active = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer progressTimer;
        private Dictionary<string, SteamDownload> downloads = new();
        private int pollingProgress;

        public SteamInstallWatcher()
        {
            settleTimer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);

            foreach (string steamApps in SteamScanner.SteamAppsFolders())
            {
                try
                {
                    // "appmanifest_*" rather than "*.acf": Steam may write a temp file and rename it.
                    FileSystemWatcher watcher = new(steamApps, "appmanifest_*")
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                    };
                    watcher.Created += (_, e) => Touch(e.FullPath);
                    watcher.Changed += (_, e) => Touch(e.FullPath);
                    watcher.Deleted += (_, e) => Touch(e.FullPath);
                    watcher.Renamed += (_, e) =>
                    {
                        Touch(e.FullPath);
                        Touch(e.OldFullPath);
                    };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);

                    // A download may already be running (CouchDeck started after it): the first poll finds it.
                    foreach (string manifestPath in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                    {
                        if (ManifestFor(manifestPath) is (string manifest, string appId))
                        {
                            active[manifest] = appId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Can't watch {steamApps}: {ex.Message}");
                }
            }

            progressTimer = new Timer(_ => PollProgress(), null, ProgressInterval, ProgressInterval);
        }

        /// <summary>Raised on a background thread: a Steam game became installed, or stopped being installed.</summary>
        public event EventHandler? InstallsChanged;

        /// <summary>Raised on a background thread when a download starts, moves on or ends. Keyed by app id.</summary>
        public event EventHandler<IReadOnlyDictionary<string, SteamDownload>>? DownloadsChanged;

        /// <summary>Call after every scan with the app ids of the installed Steam games.</summary>
        public void SetInstalled(IEnumerable<string> appIds)
        {
            lock (installedLock)
            {
                installed = new HashSet<string>(appIds);
            }
        }

        private static (string manifest, string appId)? ManifestFor(string path)
        {
            Match match = ManifestName.Match(Path.GetFileName(path));
            if (!match.Success || SteamScanner.ExcludedAppIds.Contains(match.Groups[1].Value))
            {
                return null;
            }

            string appId = match.Groups[1].Value;
            return (Path.Combine(Path.GetDirectoryName(path) ?? "", $"appmanifest_{appId}.acf"), appId);
        }

        private void Touch(string path)
        {
            if (ManifestFor(path) is not (string manifest, string appId))
            {
                return;
            }

            touched[manifest] = appId;
            active[manifest] = appId; // a write is the first sign of a download starting
            settleTimer.Change(Settle, Timeout.InfiniteTimeSpan); // wait until Steam is done writing
        }

        private void Check()
        {
            bool changed = false;

            foreach ((string manifest, string appId) in touched.ToArray())
            {
                touched.TryRemove(manifest, out _);
                bool? now = SteamScanner.IsFullyInstalled(manifest);
                if (now == null)
                {
                    Touch(manifest); // still being written: look again shortly
                }
                else
                {
                    changed |= Moved(appId, now.Value);
                }
            }

            if (changed)
            {
                InstallsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Compares with the last scan. A difference is remembered right away so it's reported once.</summary>
        private bool Moved(string appId, bool isInstalled)
        {
            lock (installedLock)
            {
                if (isInstalled == installed.Contains(appId))
                {
                    return false;
                }

                HashSet<string> updated = new(installed);
                if (isInstalled)
                {
                    updated.Add(appId);
                }
                else
                {
                    updated.Remove(appId);
                }

                installed = updated;
                return true;
            }
        }

        private void PollProgress()
        {
            if (active.IsEmpty || Interlocked.Exchange(ref pollingProgress, 1) == 1)
            {
                return;
            }

            try
            {
                Dictionary<string, SteamDownload> previous = downloads;
                Dictionary<string, SteamDownload> now = new();
                bool moved = false;

                foreach ((string manifest, string appId) in active.ToArray())
                {
                    if (!TryReadDownload(manifest, appId, out SteamDownload? download))
                    {
                        // Steam is writing it right now: keep the last value.
                        if (previous.TryGetValue(appId, out SteamDownload? last))
                        {
                            now[appId] = last;
                        }

                        continue;
                    }

                    if (download != null)
                    {
                        now[appId] = download;
                        continue;
                    }

                    // Finished (or cancelled). Move the tile now instead of waiting until Steam stops writing other manifests.
                    active.TryRemove(manifest, out _);
                    if (previous.ContainsKey(appId) && SteamScanner.IsFullyInstalled(manifest) is bool isInstalled)
                    {
                        moved |= Moved(appId, isInstalled);
                    }
                }

                downloads = now;
                bool changed = now.Count != previous.Count
                    || now.Any(pair => !previous.TryGetValue(pair.Key, out SteamDownload? old) || old != pair.Value);
                if (changed)
                {
                    DownloadsChanged?.Invoke(this, now);
                }

                if (moved)
                {
                    InstallsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam download progress failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref pollingProgress, 0);
            }
        }

        /// <summary>False when the file can't be read right now. Download is null when nothing is downloading.</summary>
        private static bool TryReadDownload(string manifest, string appId, out SteamDownload? download)
        {
            download = null;
            try
            {
                if (!File.Exists(manifest))
                {
                    return true;
                }

                Dictionary<string, object>? app = VdfParser.GetObject(VdfParser.Parse(File.ReadAllText(manifest)), "AppState");
                if (app == null || !int.TryParse(VdfParser.GetString(app, "StateFlags"), out int flags)
                    || (flags & (StateUpdateRunning | StateUpdatePaused)) == 0)
                {
                    return true;
                }

                long Bytes(string key) => long.TryParse(VdfParser.GetString(app, key), out long value) ? value : 0;
                long toDownload = Bytes("BytesToDownload");
                long downloaded = Bytes("BytesDownloaded");
                long toStage = Bytes("BytesToStage");
                long staged = Bytes("BytesStaged");

                bool paused = (flags & StateUpdatePaused) != 0;
                if (paused && downloaded == 0 && staged == 0)
                {
                    return true; // an update waiting in Steam's queue, nothing to show yet
                }

                (double fraction, DownloadPhase phase) = downloaded < toDownload || toStage == 0
                    ? (Fraction(downloaded, toDownload), DownloadPhase.Downloading)
                    : (Fraction(staged, toStage), DownloadPhase.Installing);
                download = new SteamDownload(appId, fraction, paused ? DownloadPhase.Paused : phase);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (Exception)
            {
                return true; // unreadable: treat as not downloading
            }
        }

        private static double Fraction(long done, long total) => total > 0 ? Math.Clamp(done / (double)total, 0, 1) : 0;

        public void Dispose()
        {
            foreach (FileSystemWatcher watcher in watchers)
            {
                watcher.Dispose();
            }

            settleTimer.Dispose();
            progressTimer.Dispose();
        }
    }
}
