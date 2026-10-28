using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CouchDeck.UI
{
    public enum BrowseMode
    {
        GameExe,      // pick an .exe / shortcut to add as a game
        GamesFolder,  // pick a folder whose sub-folders are games
        Image         // pick a cover or background image
    }

    public enum BrowserEntryKind { Up, UseFolder, Drive, Folder, File }

    public sealed record BrowserEntry(string Glyph, string Label, string? Detail, BrowserEntryKind Kind, string Path);

    /// <summary>
    /// A file picker you can drive with a controller: drives -> folders -> files, A to open, B to go up.
    /// The Windows file dialog needs a mouse; this doesn't.
    /// </summary>
    public sealed class FileBrowser
    {
        private const int MaxEntries = 500;

        private static readonly string[] ExeExtensions = { ".exe", ".lnk", ".url", ".bat", ".cmd" };
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        public FileBrowser(BrowseMode mode, string? startPath = null)
        {
            Mode = mode;
            CurrentPath = startPath != null && Directory.Exists(startPath) ? startPath : null;
        }

        public BrowseMode Mode { get; }

        /// <summary>Null = the top level (quick access + drives).</summary>
        public string? CurrentPath { get; private set; }

        public string Title => Mode switch
        {
            BrowseMode.GameExe => "Add a game",
            BrowseMode.GamesFolder => "Add a games folder",
            _ => "Choose an image"
        };

        public string Hint => Mode switch
        {
            BrowseMode.GameExe => "Open folders with A and pick the game's .exe. Cover art is found automatically.",
            BrowseMode.GamesFolder => "Open the folder that holds your games, then choose \"Use this folder\". Every sub-folder becomes a game.",
            _ => "Pick a .jpg, .png or .webp image."
        };

        public void Open(string path) => CurrentPath = path;

        /// <summary>Goes one level up. Returns false when already at the top.</summary>
        public bool GoUp()
        {
            if (CurrentPath == null)
            {
                return false;
            }

            DirectoryInfo? parent = Directory.GetParent(CurrentPath.TrimEnd('\\'));
            CurrentPath = parent?.FullName;
            return true;
        }

        public List<BrowserEntry> Entries()
        {
            List<BrowserEntry> entries = new();

            if (CurrentPath == null)
            {
                foreach ((string label, string path) in QuickAccess())
                {
                    entries.Add(new BrowserEntry("", label, path, BrowserEntryKind.Folder, path));
                }

                foreach (DriveInfo drive in SafeDrives())
                {
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                    string detail = $"{FormatSize(drive.AvailableFreeSpace)} free of {FormatSize(drive.TotalSize)}";
                    entries.Add(new BrowserEntry("", label, detail, BrowserEntryKind.Drive, drive.RootDirectory.FullName));
                }

                return entries;
            }

            entries.Add(new BrowserEntry("", "Up one level", Directory.GetParent(CurrentPath.TrimEnd('\\'))?.FullName ?? "Drives", BrowserEntryKind.Up, ""));

            if (Mode == BrowseMode.GamesFolder)
            {
                entries.Add(new BrowserEntry("", "Use this folder", CurrentPath, BrowserEntryKind.UseFolder, CurrentPath));
            }

            try
            {
                DirectoryInfo dir = new(CurrentPath);

                foreach (DirectoryInfo sub in dir.EnumerateDirectories()
                             .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                             .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                             .Take(MaxEntries))
                {
                    entries.Add(new BrowserEntry("", sub.Name, null, BrowserEntryKind.Folder, sub.FullName));
                }

                if (Mode != BrowseMode.GamesFolder)
                {
                    string[] extensions = Mode == BrowseMode.Image ? ImageExtensions : ExeExtensions;
                    foreach (FileInfo file in dir.EnumerateFiles()
                                 .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                                 .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                                 .Take(MaxEntries))
                    {
                        string glyph = Mode == BrowseMode.Image ? "" : "";
                        entries.Add(new BrowserEntry(glyph, file.Name, FormatSize(file.Length), BrowserEntryKind.File, file.FullName));
                    }
                }
            }
            catch (Exception ex)
            {
                entries.Add(new BrowserEntry("", "Can't open this folder", ex.Message, BrowserEntryKind.Up, ""));
            }

            return entries;
        }

        private IEnumerable<(string label, string path)> QuickAccess()
        {
            List<(string, string)> places = new();

            void Add(string label, string? path)
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    places.Add((label, path));
                }
            }

            foreach (DriveInfo drive in SafeDrives())
            {
                Add($"Games ({drive.Name.TrimEnd('\\')})", Path.Combine(drive.RootDirectory.FullName, "Games"));
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add("Downloads", Path.Combine(profile, "Downloads"));
            Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            if (Mode == BrowseMode.Image)
            {
                Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            }

            Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            return places;
        }

        private static IEnumerable<DriveInfo> SafeDrives()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                bool ready;
                try
                {
                    ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network;
                }
                catch
                {
                    ready = false;
                }

                if (ready)
                {
                    yield return drive;
                }
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}
