using System.Collections.Generic;

namespace CouchDeck.Games
{
    /// <summary>
    /// One implementation per store. To add a store (Ubisoft Connect, EA app, Battle.net...)
    /// write a class implementing this and add it to the list in GameLibrary.
    ///
    /// Rules for implementations:
    ///   - Never throw. A broken store must not hide the other stores' games. Return what you found.
    ///   - Read local files / registry only. No network calls during a scan.
    ///     (Owned-but-not-installed games come from a cache another class fills; see SteamOwnedScanner.)
    ///
    /// Owned games that aren't installed: return them with IsInstalled = false and an InstallTarget,
    /// and they show on the Library page. Installed games can set UninstallTarget.
    /// </summary>
    public interface IGameScanner
    {
        string Name { get; }

        IEnumerable<Game> Scan();
    }
}
