using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CouchDeck.Input
{
    /// <summary>
    /// PlayStation controllers (DualShock 4, DualSense, DualSense Edge) over USB or Bluetooth. Windows doesn't offer
    /// them through XInput, so they're read directly as HID devices and every report is turned into the same XInput
    /// gamepad struct: the rest of CouchDeck can't tell the difference. Buttons map by position: Cross = A,
    /// Circle = B, Square = X, Triangle = Y, PS = Guide (Xbox button), Create/Share = View, Options = Menu.
    ///
    /// One background thread per controller blocks on ReadFile and keeps the latest state; ControllerService picks it
    /// up on every poll. Report layouts as in Linux's hid-playstation driver and SDL's PS4/PS5 drivers.
    /// </summary>
    internal sealed class PlayStationPads : IDisposable
    {
        public const int MaxControllers = 4;

        private const int SonyVendorId = 0x054C;
        private static readonly HashSet<int> DualShock4 = new() { 0x05C4, 0x09CC, 0x0BA0 }; // v1, v2, USB wireless adapter
        private static readonly HashSet<int> DualSense = new() { 0x0CE6, 0x0DF2 };          // DualSense, DualSense Edge

        // \\?\hid#vid_054c&pid_0ce6&mi_03#...                                  (USB)
        // \\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#... (Bluetooth)
        private const string BluetoothHid = "00001124-0000-1000-8000-00805f9b34fb";
        private static readonly Regex VendorProduct = new(@"vid[_&](?:0002)?([0-9a-f]{4})[_&]pid[_&]([0-9a-f]{4})", RegexOptions.IgnoreCase);

        private readonly Pad?[] slots = new Pad?[MaxControllers];
        private readonly HashSet<string> unavailable = new(StringComparer.OrdinalIgnoreCase); // logged once: another app holds it

        /// <summary>Picks up controllers that were plugged in or switched on, drops ones that went away. Poll thread only.</summary>
        public void Refresh()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] is Pad pad && !pad.IsAlive)
                {
                    pad.Dispose();
                    slots[i] = null;
                }
            }

            int free = Array.IndexOf(slots, null);
            if (free < 0)
            {
                return;
            }

            foreach (string path in HidDevicePaths())
            {
                if (!IsPlayStationController(path, out bool dualSense) || Array.Exists(slots, pad => pad?.Path == path))
                {
                    continue;
                }

                Pad? opened = Pad.TryOpen(path, dualSense, path.Contains(BluetoothHid, StringComparison.OrdinalIgnoreCase));
                if (opened == null)
                {
                    if (unavailable.Add(path))
                    {
                        Debug.WriteLine($"PlayStation controller found but can't be opened (another app has it exclusively?): {path}");
                    }

                    continue;
                }

                slots[free] = opened;
                free = Array.IndexOf(slots, null);
                if (free < 0)
                {
                    return;
                }
            }
        }

        /// <summary>The controller in this slot, as an XInput gamepad. False when there's none (or no report yet).</summary>
        public bool TryGetState(int slot, out XInputNative.XInputGamepad gamepad)
        {
            if (slots[slot] is Pad pad && pad.IsAlive && pad.TryGetState(out gamepad))
            {
                return true;
            }

            gamepad = default;
            return false;
        }

        internal static bool IsPlayStationController(string path, out bool dualSense)
        {
            dualSense = false;
            Match match = VendorProduct.Match(path);
            if (!match.Success || Convert.ToInt32(match.Groups[1].Value, 16) != SonyVendorId)
            {
                return false;
            }

            int product = Convert.ToInt32(match.Groups[2].Value, 16);
            dualSense = DualSense.Contains(product);
            return dualSense || DualShock4.Contains(product);
        }

        /// <summary>
        /// One input report -> an XInput-style gamepad. False for reports that aren't controller state.
        ///   Full reports (sticks, triggers, counter, buttons): DualSense over USB (0x01) and Bluetooth (0x31).
        ///   Simple reports (sticks, buttons, triggers): DualShock 4 (0x01 USB and Bluetooth, 0x11 Bluetooth), and a
        ///   DualSense over Bluetooth until some app (Steam, a game) switches it to full reports.
        /// </summary>
        internal static bool TryParse(byte[] report, int length, bool dualSense, bool bluetooth, out XInputNative.XInputGamepad gamepad)
        {
            gamepad = default;
            if (length < 1)
            {
                return false;
            }

            (int offset, bool full) = (dualSense, bluetooth, report[0]) switch
            {
                (true, false, 0x01) => (1, true),
                (true, true, 0x31) => (2, true),
                (true, true, 0x01) => (1, false),
                (false, _, 0x01) => (1, false),
                (false, true, 0x11) => (3, false),
                _ => (-1, false)
            };
            if (offset < 0 || length < offset + 10)
            {
                return false;
            }

            (byte face, byte shoulders, byte system, byte leftTrigger, byte rightTrigger) = full
                ? (report[offset + 7], report[offset + 8], report[offset + 9], report[offset + 4], report[offset + 5])
                : (report[offset + 4], report[offset + 5], report[offset + 6], report[offset + 7], report[offset + 8]);

            PadButton buttons = PadButton.None;

            int hat = face & 0x0F; // 0 = up, clockwise to 7 = up-left, 8 = released
            if (hat is 0 or 1 or 7) buttons |= PadButton.DPadUp;
            if (hat is 1 or 2 or 3) buttons |= PadButton.DPadRight;
            if (hat is 3 or 4 or 5) buttons |= PadButton.DPadDown;
            if (hat is 5 or 6 or 7) buttons |= PadButton.DPadLeft;

            if ((face & 0x10) != 0) buttons |= PadButton.X;              // Square
            if ((face & 0x20) != 0) buttons |= PadButton.A;              // Cross
            if ((face & 0x40) != 0) buttons |= PadButton.B;              // Circle
            if ((face & 0x80) != 0) buttons |= PadButton.Y;              // Triangle
            if ((shoulders & 0x01) != 0) buttons |= PadButton.LeftShoulder;  // L1
            if ((shoulders & 0x02) != 0) buttons |= PadButton.RightShoulder; // R1
            if ((shoulders & 0x10) != 0) buttons |= PadButton.Back;          // Create / Share
            if ((shoulders & 0x20) != 0) buttons |= PadButton.Start;         // Options
            if ((shoulders & 0x40) != 0) buttons |= PadButton.LeftThumb;     // L3
            if ((shoulders & 0x80) != 0) buttons |= PadButton.RightThumb;    // R3
            if ((system & 0x01) != 0) buttons |= PadButton.Guide;            // PS

            // Sticks are 0..255 with 128 in the middle and 0 = up; XInput is signed with up positive.
            static short Axis(int value) => (short)Math.Clamp((value - 128) * 258, short.MinValue, short.MaxValue);

            gamepad = new XInputNative.XInputGamepad
            {
                Buttons = (ushort)buttons,
                LeftTrigger = leftTrigger,
                RightTrigger = rightTrigger,
                ThumbLX = Axis(report[offset]),
                ThumbLY = Axis(255 - report[offset + 1]),
                ThumbRX = Axis(report[offset + 2]),
                ThumbRY = Axis(255 - report[offset + 3])
            };
            return true;
        }

        private static List<string> HidDevicePaths()
        {
            List<string> paths = new();
            Guid hid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (CM_Get_Device_Interface_List_Size(out int size, ref hid, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || size <= 1)
                {
                    return paths;
                }

                char[] buffer = new char[size];
                int result = CM_Get_Device_Interface_List(ref hid, null, buffer, size, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
                if (result == CR_BUFFER_SMALL)
                {
                    continue; // a device arrived in between
                }

                if (result == CR_SUCCESS)
                {
                    paths.AddRange(new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }

                break;
            }

            return paths;
        }

        public void Dispose()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i]?.Dispose();
                slots[i] = null;
            }
        }

        /// <summary>One open controller and the thread reading it.</summary>
        private sealed class Pad : IDisposable
        {
            private readonly SafeFileHandle handle;
            private readonly int reportLength;
            private readonly bool dualSense;
            private readonly bool bluetooth;
            private readonly object stateLock = new();
            private XInputNative.XInputGamepad state;
            private bool hasState;
            private volatile bool alive = true;

            private Pad(string path, SafeFileHandle handle, int reportLength, bool dualSense, bool bluetooth)
            {
                Path = path;
                this.handle = handle;
                this.reportLength = reportLength;
                this.dualSense = dualSense;
                this.bluetooth = bluetooth;

                new Thread(ReadLoop) { IsBackground = true, Name = "CouchDeck PlayStation controller" }.Start();
            }

            public string Path { get; }
            public bool IsAlive => alive;

            public static Pad? TryOpen(string path, bool dualSense, bool bluetooth)
            {
                // Shared, so Steam or a game can read it at the same time.
                SafeFileHandle handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                }

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                // The gamepad collection only (USB DualSense also exposes audio-related interfaces).
                if (!TryGetCaps(handle, out HidpCaps caps) || caps.UsagePage != 0x01 || caps.Usage != 0x05 || caps.InputReportByteLength == 0)
                {
                    handle.Dispose();
                    return null;
                }

                return new Pad(path, handle, caps.InputReportByteLength, dualSense, bluetooth);
            }

            public bool TryGetState(out XInputNative.XInputGamepad gamepad)
            {
                lock (stateLock)
                {
                    gamepad = state;
                    return hasState;
                }
            }

            private void ReadLoop()
            {
                byte[] report = new byte[reportLength];
                try
                {
                    while (alive)
                    {
                        if (!ReadFile(handle, report, report.Length, out int read, IntPtr.Zero))
                        {
                            break; // unplugged or switched off
                        }

                        if (TryParse(report, read, dualSense, bluetooth, out XInputNative.XInputGamepad parsed))
                        {
                            lock (stateLock)
                            {
                                state = parsed;
                                hasState = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlayStation controller read failed: {ex.Message}");
                }
                finally
                {
                    alive = false;
                }
            }

            public void Dispose()
            {
                alive = false;
                CancelIoEx(handle, IntPtr.Zero); // unblocks ReadFile
                handle.Dispose();
            }

            private static bool TryGetCaps(SafeFileHandle device, out HidpCaps caps)
            {
                caps = default;
                if (!HidD_GetPreparsedData(device, out IntPtr preparsed))
                {
                    return false;
                }

                try
                {
                    return HidP_GetCaps(preparsed, out caps) == HIDP_STATUS_SUCCESS;
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }
        }

        // ---------- Win32 ----------

        private const int CR_SUCCESS = 0;
        private const int CR_BUFFER_SMALL = 0x1A;
        private const int CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid interfaceClass, string? deviceId, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, char[] buffer, int length, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int toRead, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);
    }
}
