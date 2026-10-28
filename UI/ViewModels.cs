using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CouchDeck.Games;
using CouchDeck.WindowsControl;

namespace CouchDeck.UI
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One game tile. Cover art arrives asynchronously (decode, or download by CoverArtService).</summary>
    public sealed class GameTileViewModel : ObservableObject
    {
        private Game game;
        private ImageSource? cover;
        private ImageSource? icon;
        private bool isLandscape;
        private bool isFavorite;
        private int loadedWidth;
        private string? loadedPath;
        private SteamDownload? download;

        public GameTileViewModel(Game game, bool isFavorite)
        {
            this.game = game;
            this.isFavorite = isFavorite;
        }

        public Game Game
        {
            get => game;
            set
            {
                Game old = game;
                game = value;
                Raise();
                Raise(nameof(Title));
                Raise(nameof(SourceLabel));
                if (old.CoverImage != value.CoverImage)
                {
                    loadedPath = null;
                }
            }
        }

        public string Id => game.Id;
        public string Title => game.Title;
        public string SourceLabel => game.SourceLabel;

        public ImageSource? Cover { get => cover; private set { if (Set(ref cover, value)) Raise(nameof(HasCover)); } }
        public ImageSource? Icon { get => icon; private set => Set(ref icon, value); }
        public bool HasCover => cover != null;
        public bool IsLandscape { get => isLandscape; private set => Set(ref isLandscape, value); }
        public bool IsFavorite { get => isFavorite; set => Set(ref isFavorite, value); }

        /// <summary>Steam is downloading (installing or updating) this game. Null when it isn't.</summary>
        public SteamDownload? Download
        {
            get => download;
            set
            {
                if (Set(ref download, value))
                {
                    Raise(nameof(IsDownloading));
                    Raise(nameof(DownloadFraction));
                    Raise(nameof(DownloadPercent));
                    Raise(nameof(DownloadLabel));
                    Raise(nameof(DownloadGlyph));
                }
            }
        }

        public bool IsDownloading => download != null;
        public double DownloadFraction => download?.Fraction ?? 0;

        // Rounded down: 100% only once it's really done.
        public string DownloadPercent => download == null ? "" : $"{(int)(download.Fraction * 100)}%";

        public string DownloadLabel => download?.Phase switch
        {
            null => "",
            DownloadPhase.Paused => "Paused",
            DownloadPhase.Installing => "Installing",
            _ => game.IsInstalled ? "Updating" : "Downloading"
        };

        public string DownloadGlyph => download?.Phase == DownloadPhase.Paused ? "\uE769" : "\uE896";

        /// <summary>Loads (or reloads at a new size) the cover. No-op when nothing changed.</summary>
        public async Task LoadCoverAsync(int decodeWidth)
        {
            string? path = game.CoverImage;
            if (path == loadedPath && decodeWidth == loadedWidth && (cover != null || path == null))
            {
                return;
            }

            loadedPath = path;
            loadedWidth = decodeWidth;

            BitmapSource? image = await ImageLoader.LoadAsync(path, decodeWidth);
            if (path != game.CoverImage)
            {
                return; // a newer cover arrived while this one was decoding
            }

            if (image != null)
            {
                IsLandscape = image.PixelWidth > image.PixelHeight * 1.05;
                Cover = image;
            }
            else
            {
                Cover = null;
                if (Icon == null && game.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    Icon = ShellIcons.Get(game.LaunchTarget, 256);
                }
            }
        }
    }

    /// <summary>The "+" tile at the end of the library.</summary>
    public sealed class AddTileViewModel
    {
        public string Title => "Add a game";
        public string Subtitle => "Pick an .exe or a games folder";
    }

    public sealed class WindowRowViewModel
    {
        public WindowRowViewModel(OpenWindow window)
        {
            Window = window;
            string? path = ShellIcons.ProcessPath(window.ProcessId);
            Icon = ShellIcons.Get(path, 96);
            Initial = string.IsNullOrEmpty(window.ProcessName) ? "?" : window.ProcessName[..1].ToUpperInvariant();
        }

        public OpenWindow Window { get; }
        public string Title => Window.Title;
        public string ProcessName => Window.ProcessName;
        public ImageSource? Icon { get; }
        public string Initial { get; }
        public bool HasIcon => Icon != null;
    }

    public enum HintButton { A, B, X, Y, Bumpers, View, Menu, DPad }

    /// <summary>One button in the hint bar, drawn the way the controller in your hands labels it.</summary>
    public sealed class HintViewModel
    {
        public HintViewModel(HintButton button, string label, bool playStation = false)
        {
            Button = button;
            Label = label;
            PlayStation = playStation;
        }

        public HintButton Button { get; }
        public string Label { get; }

        /// <summary>✕ ○ □ △ instead of A B X Y (same positions).</summary>
        public bool PlayStation { get; }

        public string Glyph => (Button, PlayStation) switch
        {
            (HintButton.A, true) => "✕",
            (HintButton.B, true) => "○",
            (HintButton.X, true) => "□",
            (HintButton.Y, true) => "△",
            (HintButton.View, true) => "CREATE",
            (HintButton.Menu, true) => "OPTIONS",
            (HintButton.Bumpers, true) => "L1  R1",
            (HintButton.View, false) => "VIEW",
            (HintButton.Menu, false) => "MENU",
            (HintButton.Bumpers, false) => "LB  RB",
            (HintButton.DPad, _) => "◀ ▶",
            _ => Button.ToString()
        };

        public bool IsRound => Button is HintButton.A or HintButton.B or HintButton.X or HintButton.Y;

        public Brush Fill => (Button, PlayStation) switch
        {
            (HintButton.A, false) => Frozen("#2EA043"),
            (HintButton.B, false) => Frozen("#DA3633"),
            (HintButton.X, false) => Frozen("#1F6FEB"),
            (HintButton.Y, false) => Frozen("#D29922"),
            (HintButton.A, true) => Frozen("#4C7BD9"), // cross: blue
            (HintButton.B, true) => Frozen("#D9474F"), // circle: red
            (HintButton.X, true) => Frozen("#C25FA6"), // square: pink
            (HintButton.Y, true) => Frozen("#2F9E78"), // triangle: green
            _ => Frozen("#33FFFFFF")
        };

        private static readonly Dictionary<string, Brush> Brushes = new();

        private static Brush Frozen(string hex)
        {
            if (!Brushes.TryGetValue(hex, out Brush? brush))
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                Brushes[hex] = brush;
            }

            return brush;
        }
    }

    public sealed class FilterChipViewModel : ObservableObject
    {
        private bool isActive;

        public FilterChipViewModel(string key, string label, int count)
        {
            Key = key;
            Label = label;
            Count = count;
        }

        public string Key { get; }
        public string Label { get; }
        public int Count { get; }
        public bool IsActive { get => isActive; set => Set(ref isActive, value); }
    }

    /// <summary>One row in a modal (menu entry, confirm option, file browser entry).</summary>
    public sealed class ModalItem
    {
        public ModalItem(string glyph, string label, Action onSelect, string? detail = null, bool isDanger = false)
        {
            Glyph = glyph;
            Label = label;
            OnSelect = onSelect;
            Detail = detail;
            IsDanger = isDanger;
        }

        public string Glyph { get; }
        public string Label { get; }
        public string? Detail { get; }
        public bool IsDanger { get; }
        public bool HasDetail => !string.IsNullOrEmpty(Detail);
        public Action OnSelect { get; }

        /// <summary>Shown instead of the glyph (achievement icons).</summary>
        public ImageSource? Image { get; init; }
        public bool HasImage => Image != null;
    }

    /// <summary>Chooses the tile template: game or "+ Add".</summary>
    public sealed class TileTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? GameTemplate { get; set; }
        public DataTemplate? AddTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
            item is AddTileViewModel ? AddTemplate : GameTemplate;
    }
}
