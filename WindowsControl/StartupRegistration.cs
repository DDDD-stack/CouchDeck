using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace CouchDeck.WindowsControl
{
    /// <summary>
    /// CouchDeck must already be running in the background for the Xbox button to work,
    /// so by default it registers itself to start with Windows (per-user, no admin needed).
    /// </summary>
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CouchDeck";

        public static void Apply(bool enabled)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key == null)
                {
                    return;
                }

                if (enabled)
                {
                    string? exePath = Environment.ProcessPath;
                    if (exePath != null)
                    {
                        key.SetValue(ValueName, $"\"{exePath}\" --background");
                    }
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not update startup registration: {ex.Message}");
            }
        }
    }
}
