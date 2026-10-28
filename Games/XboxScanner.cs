using System.Collections.Generic;

namespace CouchDeck.Games
{
    /// <summary>
    /// STUB - Xbox app / Game Pass / Microsoft Store games.
    ///
    /// These are packaged (MSIX) apps, so there's no plain .exe to launch. The clean approach:
    ///   1. Change the TargetFramework to net8.0-windows10.0.19041.0 to get WinRT APIs.
    ///   2. Use Windows.Management.Deployment.PackageManager.FindPackagesForUser("") and keep
    ///      packages whose install folder contains MicrosoftGame.config (that file marks a game).
    ///   3. Title + logo come from package.DisplayName / package.Logo.
    ///   4. Launch with "shell:AppsFolder\&lt;PackageFamilyName&gt;!&lt;AppId&gt;" (AppId is in AppxManifest.xml),
    ///      or await package.GetAppListEntries()[0].LaunchAsync().
    ///
    /// Quick alternative without WinRT: games installed by the Xbox app live in X:\XboxGames\&lt;name&gt;\Content.
    /// Until this is done, add Game Pass games by hand in config.json (CustomGames) using the
    /// shell:AppsFolder\... target, which you can find by creating a desktop shortcut to the game.
    /// </summary>
    public sealed class XboxScanner : IGameScanner
    {
        public string Name => "Xbox";

        public IEnumerable<Game> Scan()
        {
            return new List<Game>();
        }
    }
}
