using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CouchDeck.WindowsControl
{
    /// <summary>All Win32 imports in one place so the rest of the code stays readable.</summary>
    internal static class NativeMethods
    {
        // ---------- Window enumeration / focus ----------

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>While we're in front: let another process (Steam's install dialog) take the foreground.</summary>
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        public const int ASFW_ANY = -1;

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int GWL_STYLE = -16;
        public const long WS_CAPTION = 0x00C00000;

        public const uint GW_OWNER = 4;
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_NOACTIVATE = 0x08000000;
        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const uint WM_CLOSE = 0x0010;
        public const int DWMWA_CLOAKED = 14; // hidden UWP / other-desktop windows report non-zero

        // ---------- Input injection ----------

        [StructLayout(LayoutKind.Sequential)]
        public struct InputRecord
        {
            public uint Type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int Dx;
            public int Dy;
            public int MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, InputRecord[] inputs, int size);

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        // Virtual keys we send
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_ESCAPE = 0x1B;
        public const ushort VK_TAB = 0x09;
        public const ushort VK_MENU = 0x12;     // Alt
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_BACK = 0x08;
        public const ushort VK_LEFT = 0x25;
        public const ushort VK_UP = 0x26;
        public const ushort VK_RIGHT = 0x27;
        public const ushort VK_DOWN = 0x28;
        public const ushort VK_F4 = 0x73;
        public const ushort VK_VOLUME_MUTE = 0xAD;
        public const ushort VK_VOLUME_DOWN = 0xAE;
        public const ushort VK_VOLUME_UP = 0xAF;
        public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

        // ---------- Power ----------

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();
    }
}
