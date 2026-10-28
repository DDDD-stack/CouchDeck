using System;
using System.Diagnostics;
using System.IO;
using static CouchDeck.WindowsControl.NativeMethods;

namespace CouchDeck.WindowsControl
{
    public enum DisplayMode
    {
        PcScreenOnly,   // laptop screen only
        Duplicate,      // same picture on both
        Extend,
        SecondScreenOnly // TV only - usually what you want on the couch
    }

    /// <summary>
    /// Windows-level actions exposed on the "System" page. Each method is one button.
    /// Destructive ones (shutdown, restart) must be confirmed by the UI before calling.
    /// </summary>
    public static class SystemActions
    {
        // ----- Audio (media keys: works with every output device, no COM needed) -----

        public static void VolumeUp() => InputSimulator.TapKey(VK_VOLUME_UP);
        public static void VolumeDown() => InputSimulator.TapKey(VK_VOLUME_DOWN);
        public static void ToggleMute() => InputSimulator.TapKey(VK_VOLUME_MUTE);
        public static void PlayPause() => InputSimulator.TapKey(VK_MEDIA_PLAY_PAUSE);

        // TODO: switch audio output (laptop speakers <-> TV over HDMI). Needs the undocumented
        // IPolicyConfig COM interface, or the AudioDeviceCmdlets / SoundVolumeView tool. Nice next step.

        // ----- Display -----

        /// <summary>Same as Win+P. DisplaySwitch.exe ships with Windows 10/11.</summary>
        public static void SetDisplayMode(DisplayMode mode)
        {
            string argument = mode switch
            {
                DisplayMode.PcScreenOnly => "/internal",
                DisplayMode.Duplicate => "/clone",
                DisplayMode.Extend => "/extend",
                DisplayMode.SecondScreenOnly => "/external",
                _ => "/extend"
            };

            StartHidden("DisplaySwitch.exe", argument);
        }

        // ----- Keyboard -----

        /// <summary>
        /// The touch keyboard (TabTip) works with mouse-mode clicks and looks better on a TV
        /// than osk.exe. Falls back to the classic on-screen keyboard.
        /// TODO: a built-in controller keyboard inside the overlay (like Steam's) would be
        /// far nicer for typing a search or a password; this is the quick version.
        /// </summary>
        public static void ShowOnScreenKeyboard()
        {
            string tabTip = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                @"microsoft shared\ink\TabTip.exe");

            if (File.Exists(tabTip))
            {
                StartShell(tabTip);
            }
            else
            {
                StartShell("osk.exe");
            }
        }

        // ----- Common keys, for apps that don't understand a controller -----

        public static void SendEnter() => InputSimulator.TapKey(VK_RETURN);
        public static void SendEscape() => InputSimulator.TapKey(VK_ESCAPE);
        public static void SendAltF4() => InputSimulator.KeyCombo(VK_MENU, VK_F4);
        public static void SendAltTab() => InputSimulator.KeyCombo(VK_MENU, VK_TAB);
        public static void OpenStartMenu() => InputSimulator.TapKey(VK_LWIN);

        // ----- Power -----

        public static void Sleep() => SetSuspendState(false, false, false);
        public static void Lock() => LockWorkStation();
        public static void Shutdown() => StartHidden("shutdown.exe", "/s /t 0");
        public static void Restart() => StartHidden("shutdown.exe", "/r /t 0");

        // ----- Launchers -----

        public static void OpenSteamBigPicture() => StartShell("steam://open/bigpicture");
        public static void OpenUrl(string url) => StartShell(url);

        private static void StartHidden(string fileName, string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to run {fileName} {arguments}: {ex.Message}");
            }
        }

        private static void StartShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open {target}: {ex.Message}");
            }
        }
    }
}
