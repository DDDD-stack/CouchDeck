using System;
using System.Runtime.InteropServices;

namespace CouchDeck.Input
{
    /// <summary>
    /// Raw XInput P/Invoke. Works with Xbox One / Series controllers over USB, the
    /// Xbox Wireless Adapter, and Bluetooth (on Windows 10/11).
    ///
    /// The Xbox (Guide) button is NOT reported by the public XInputGetState.
    /// The undocumented export at ordinal 100 ("XInputGetStateEx") reports it as bit 0x0400.
    /// It has been stable in xinput1_4.dll since Windows 8, and Steam, DS4Windows etc. rely on it.
    /// </summary>
    internal static class XInputNative
    {
        public const int ERROR_SUCCESS = 0;
        public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        public const int MaxControllers = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputVibration
        {
            public ushort LeftMotorSpeed;
            public ushort RightMotorSpeed;
        }

        // Public API (no Guide button)
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        public static extern int XInputGetState(int userIndex, out XInputState state);

        // Undocumented ordinal 100: same struct, but includes the Guide button (0x0400)
        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        public static extern int XInputGetStateEx(int userIndex, out XInputState state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        public static extern int XInputSetState(int userIndex, ref XInputVibration vibration);
    }
}
