using System;

namespace CouchDeck.Games
{
    public enum GameSource
    {
        Steam,
        Epic,
        Gog,
        Xbox,
        Custom,
        Folder
    }

    /// <summary>
    /// One tile on the Games page. Immutable; scanners create these, GameLibrary merges them.
    /// </summary>
    public sealed record Game
    {
        /// <summary>Stable across scans, e.g. "steam:1245620". Used for hiding and last-played.</summary>
        public required string Id { get; init; }

        public required string Title { get; init; }
        public required GameSource Source { get; init; }

        /// <summary>A URI (steam://..., com.epicgames.launcher://...) or a full path to an .exe.</summary>
        public required string LaunchTarget { get; init; }

        public string? Arguments { get; init; }

        /// <summary>Folder the game is installed in. Used to find its running process.</summary>
        public string? InstallDir { get; init; }

        /// <summary>Local file path or http(s) URL for the portrait cover. Null = fetched by CoverArtService.</summary>
        public string? CoverImage { get; init; }

        /// <summary>Wide background art shown behind the focused game. Optional.</summary>
        public string? HeroImage { get; init; }

        /// <summary>Known Steam app id, lets cover lookup skip the name search.</summary>
        public string? SteamAppId { get; init; }

        /// <summary>True when the title was guessed from a folder or file name (not from a launcher).</summary>
        public bool TitleIsGuess { get; init; }

        public DateTime? LastPlayed { get; init; }

        /// <summary>False for games you own but haven't installed. They live on the Library page, not on Games.</summary>
        public bool IsInstalled { get; init; } = true;

        /// <summary>
        /// Asks the store to install the game (steam://install/&lt;appid&gt;). The store shows its own window
        /// (drive, size, confirm). Null = can't be installed from CouchDeck.
        /// </summary>
        public string? InstallTarget { get; init; }

        /// <summary>Asks the store to uninstall it (steam://uninstall/&lt;appid&gt;). The store asks to confirm.</summary>
        public string? UninstallTarget { get; init; }

        public string SourceLabel => Source switch
        {
            GameSource.Steam => "Steam",
            GameSource.Epic => "Epic",
            GameSource.Gog => "GOG",
            GameSource.Xbox => "Xbox",
            GameSource.Folder => "Local",
            _ => "Custom"
        };
    }
}
