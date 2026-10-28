using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CouchDeck.Audio;
using CouchDeck.Games;
using CouchDeck.WindowsControl;

namespace CouchDeck.UI
{
    /// <summary>Steam account setup (Customize > Steam) and the "new achievements" summary after playing.</summary>
    public partial class OverlayWindow
    {
        private const string SteamKeyPage = "https://steamcommunity.com/dev/apikey";
        private const int AchievementIconPixels = 64; // Steam's icons are 64 x 64

        private bool showingAchievements;

        // =====================================================================
        // Download progress (installs from Library, and updates)
        // =====================================================================

        /// <summary>Steam downloads in progress, by app id. Called every couple of seconds while one runs.</summary>
        public void SetDownloads(IReadOnlyDictionary<string, SteamDownload> current)
        {
            downloads = current;
            ApplyDownloads();

            // The line under the focused game's name shows the percentage too.
            if (IsGridPage(currentPage) && lastFocusedGameId != null && tileMap.TryGetValue(lastFocusedGameId, out GameTileViewModel? focused)
                && HeroTitle.Text == focused.Title)
            {
                UpdateHero(focused);
            }
        }

        private void ApplyDownloads()
        {
            foreach (GameTileViewModel vm in tileMap.Values)
            {
                vm.Download = DownloadOf(vm.Game);
            }
        }

        private SteamDownload? DownloadOf(Game game) =>
            game.Source == GameSource.Steam && game.SteamAppId is string appId && downloads.TryGetValue(appId, out SteamDownload? download)
                ? download
                : null;

        private bool IsDownloading(Game game) => DownloadOf(game) != null;

        // =====================================================================
        // New achievements (after a Steam game closes)
        // =====================================================================

        /// <summary>Shown right away when the overlay is up, otherwise the next time it opens.</summary>
        public void QueueAchievements(AchievementSummary summary)
        {
            pendingAchievements.Add(summary);
            if (IsVisible)
            {
                _ = ShowPendingAchievementsAsync();
            }
        }

        private async Task ShowPendingAchievementsAsync()
        {
            if (pendingAchievements.Count == 0 || showingAchievements)
            {
                return;
            }

            showingAchievements = true;
            try
            {
                List<AchievementSummary> batch = pendingAchievements.ToList();
                pendingAchievements.Clear();
                bool severalGames = batch.Count > 1;

                List<ModalItem> items = new();
                foreach (AchievementSummary summary in batch)
                {
                    string game = GameTitle(summary);
                    foreach (SteamAchievement achievement in summary.Earned)
                    {
                        string? detail = !severalGames ? achievement.Description
                            : string.IsNullOrEmpty(achievement.Description) ? game
                            : $"{game}   ·   {achievement.Description}";
                        ImageSource? icon = await ImageLoader.LoadAsync(achievement.IconPath, AchievementIconPixels);
                        items.Add(new ModalItem("", achievement.Name, () => CloseModal(), detail) { Image = icon });
                    }
                }

                if (!IsVisible)
                {
                    pendingAchievements.InsertRange(0, batch); // closed while the icons were loading
                    return;
                }

                AchievementSummary first = batch[0];
                string subtitle = severalGames
                    ? "In " + string.Join(", ", batch.Select(GameTitle).Distinct())
                    : first.Unlocked == first.Total
                        ? $"{GameTitle(first)}   ·   all {first.Total} unlocked"
                        : $"{GameTitle(first)}   ·   {first.Unlocked} of {first.Total} unlocked";

                Sound(Sfx.Notify);
                ShowModal("", items.Count == 1 ? "New achievement" : $"{items.Count} new achievements", subtitle, items);
            }
            finally
            {
                showingAchievements = false;
            }
        }

        /// <summary>The name the library uses (renames included), else Steam's.</summary>
        private string GameTitle(AchievementSummary summary) =>
            allGames.FirstOrDefault(g => g.Id == summary.GameId)?.Title
            ?? (string.IsNullOrEmpty(summary.GameTitle) ? "Steam game" : summary.GameTitle);

        // =====================================================================
        // Steam account (Customize > Steam)
        // =====================================================================

        /// <summary>Called by App when the key, the owned list or Steam's last answer changed.</summary>
        public void OnSteamStatusChanged()
        {
            RefreshSettings();
            if (tilesPage == OverlayPage.Library && tiles.Count == 0)
            {
                GamesStatus.Text = EmptyGridText();
            }
        }

        private void ShowSteamSetup()
        {
            bool hasKey = SteamWebApi.KeyFrom(Config) != null;
            List<ModalItem> items = new()
            {
                new("", "Get a free Steam Web API key", () =>
                {
                    CloseModal(restoreFocus: false);
                    SystemActions.OpenUrl(SteamKeyPage);
                    HideRequested?.Invoke(this, EventArgs.Empty); // leave the browser in front
                }, "Opens steamcommunity.com/dev/apikey. Any domain name works (e.g. localhost). Copy the key, come back"),
                new("", "Paste key from clipboard", PasteSteamKey, hasKey ? "Replaces the current key" : "The 32-character key you copied")
            };

            if (hasKey)
            {
                items.Add(new ModalItem("", "Reload owned games", () =>
                {
                    CloseModal();
                    host?.RefreshSteamLibrary();
                }, host?.SteamStatus));
                items.Add(new ModalItem("", "Disconnect", () =>
                    Confirm("", "Disconnect your Steam account?",
                        "Removes the key from config.json. The Library page empties; installed games stay.", "Disconnect", () =>
                        {
                            Config.SteamWebApiKey = null;
                            host?.SaveConfig();
                            SteamOwnedGames.Clear();
                            host?.RefreshSteamLibrary();
                            host?.Rescan();
                            ShowToast("", "Steam disconnected", null);
                        }), isDanger: true));
            }

            ShowModal("", "Steam account",
                "CouchDeck only reads from Steam: the games you own and the achievements you unlock. " +
                "The key stays on this PC, in config.json.", items);
        }

        private void PasteSteamKey()
        {
            string text = "";
            try
            {
                text = Clipboard.GetText().Trim();
            }
            catch (Exception)
            {
                // clipboard busy (another app has it open)
            }

            if (!SteamWebApi.LooksLikeKey(text))
            {
                ShowToast("", "No Steam key on the clipboard",
                    "Copy the 32-character key from steamcommunity.com/dev/apikey, then try again.", Sfx.Error);
                return;
            }

            Config.SteamWebApiKey = text;
            host?.SaveConfig();
            CloseModal();
            host?.RefreshSteamLibrary();
            ShowToast("", "Steam connected", "Loading the games you own...");
        }
    }
}
