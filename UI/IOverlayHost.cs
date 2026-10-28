using System;
using System.Globalization;
using System.Windows.Data;
using CouchDeck.Audio;
using CouchDeck.Config;
using CouchDeck.Covers;

namespace CouchDeck.UI
{
    /// <summary>What the overlay needs from the app for the library and Customize page.</summary>
    public interface IOverlayHost
    {
        AppConfig Config { get; }
        CoverArtService Covers { get; }
        SoundService Sounds { get; }

        /// <summary>Saves config.json soon (debounced: sliders change many times a second).</summary>
        void SaveConfig();

        void Rescan();

        /// <summary>Re-applies mouse speed, repeat rate, chord hold and start-with-Windows.</summary>
        void ApplyControllerSettings();

        /// <summary>Reloads the owned Steam games (Library page) from the Steam account.</summary>
        void RefreshSteamLibrary();

        /// <summary>"312 games owned", "Not connected", or what Steam refused last. Customize > Steam shows it.</summary>
        string SteamStatus { get; }
    }

    /// <summary>0..1 fraction * ConverterParameter -> width in pixels (slider fill bars).</summary>
    public sealed class FractionToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double fraction = value is double d ? d : 0;
            double full = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) ? p : 100;
            return Math.Max(0, Math.Min(1, fraction)) * full;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
