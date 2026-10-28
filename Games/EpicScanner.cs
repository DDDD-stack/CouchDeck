using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CouchDeck.Games
{
    /// <summary>
    /// The Epic launcher writes one JSON ".item" manifest per installed game to
    ///   C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests
    /// Launch through the launcher's URI so it handles login/DRM:
    ///   com.epicgames.launcher://apps/&lt;namespace&gt;%3A&lt;catalogItemId&gt;%3A&lt;appName&gt;?action=launch&amp;silent=true
    ///
    /// Cover art: the launcher's catalog cache (Data\Catalog\catcache.bin, base64 JSON) lists official
    /// art per catalog item: DieselGameBoxTall (portrait) and DieselGameBox (wide). Those URLs are
    /// handed to CoverArtService, which downloads and caches them. Fortnite isn't on Steam, so
    /// this is the only key-free source for some Epic games.
    /// </summary>
    public sealed class EpicScanner : IGameScanner
    {
        public string Name => "Epic";

        public IEnumerable<Game> Scan()
        {
            List<Game> games = new();

            try
            {
                string manifests = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Epic\EpicGamesLauncher\Data\Manifests");

                if (!Directory.Exists(manifests))
                {
                    return games;
                }

                Dictionary<string, (string? tall, string? wide)> art = ReadCatalogArt();

                foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
                {
                    Game? game = ReadManifest(file, art);
                    if (game != null)
                    {
                        games.Add(game);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Epic scan failed: {ex}");
            }

            return games;
        }

        private static Game? ReadManifest(string file, Dictionary<string, (string? tall, string? wide)> art)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = document.RootElement;

                string? displayName = GetString(root, "DisplayName");
                string? appName = GetString(root, "AppName");
                string? catalogNamespace = GetString(root, "CatalogNamespace");
                string? catalogItemId = GetString(root, "CatalogItemId");
                string? installLocation = GetString(root, "InstallLocation");

                if (displayName == null || appName == null || catalogNamespace == null || catalogItemId == null)
                {
                    return null;
                }

                // Epic also installs things like the Unreal Engine; only keep entries tagged "games".
                if (root.TryGetProperty("AppCategories", out JsonElement categories)
                    && categories.ValueKind == JsonValueKind.Array
                    && !categories.EnumerateArray().Any(c => c.GetString() == "games"))
                {
                    return null;
                }

                string launchUri =
                    $"com.epicgames.launcher://apps/{catalogNamespace}%3A{catalogItemId}%3A{appName}?action=launch&silent=true";

                art.TryGetValue(catalogItemId, out (string? tall, string? wide) images);

                return new Game
                {
                    Id = $"epic:{appName}",
                    Title = displayName,
                    Source = GameSource.Epic,
                    LaunchTarget = launchUri,
                    InstallDir = installLocation,
                    CoverImage = images.tall, // URLs: CoverArtService downloads them once
                    HeroImage = images.wide
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Bad Epic manifest {file}: {ex.Message}");
                return null;
            }
        }

        /// <summary>catalog item id -> (portrait url, wide url). Empty when the cache is missing or unreadable.</summary>
        private static Dictionary<string, (string? tall, string? wide)> ReadCatalogArt()
        {
            Dictionary<string, (string?, string?)> result = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Epic\EpicGamesLauncher\Data\Catalog\catcache.bin");
                if (!File.Exists(path))
                {
                    return result;
                }

                byte[] json = Convert.FromBase64String(File.ReadAllText(path).Trim());
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    string? id = GetString(item, "id");
                    if (id == null || !item.TryGetProperty("keyImages", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    string? tall = null;
                    string? wide = null;
                    foreach (JsonElement image in images.EnumerateArray())
                    {
                        switch (GetString(image, "type"))
                        {
                            case "DieselGameBoxTall": tall ??= GetString(image, "url"); break;
                            case "DieselGameBox": wide ??= GetString(image, "url"); break;
                        }
                    }

                    if (tall != null || wide != null)
                    {
                        result[id] = (tall, wide);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Epic catalog cache unreadable: {ex.Message}");
            }

            return result;
        }

        private static string? GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }
}
