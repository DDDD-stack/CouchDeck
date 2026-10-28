using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using static CouchDeck.WindowsControl.NativeMethods;

namespace CouchDeck.WindowsControl
{
    public sealed record OpenWindow(IntPtr Handle, string Title, string ProcessName, int ProcessId);

    /// <summary>
    /// Everything to do with other apps' windows: listing them (the "Alt+Tab" page),
    /// switching to them, closing them, and stealing focus back for the overlay.
    /// </summary>
    public static class WindowManager
    {
        // Processes that own visible top-level windows but are never interesting to switch to.
        private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "TextInputHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
            "ApplicationFrameHost", "SystemSettings", "LockApp", "CouchDeck"
        };

        /// <summary>Roughly the same list Alt+Tab shows.</summary>
        public static List<OpenWindow> GetOpenWindows()
        {
            List<OpenWindow> result = new();

            EnumWindows((hWnd, _) =>
            {
                if (!IsAltTabWindow(hWnd))
                {
                    return true;
                }

                string title = GetTitle(hWnd);
                GetWindowThreadProcessId(hWnd, out uint pid);
                string processName = TryGetProcessName((int)pid);

                if (title.Length > 0 && !IgnoredProcesses.Contains(processName))
                {
                    result.Add(new OpenWindow(hWnd, title, processName, (int)pid));
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        /// <summary>
        /// Windows refuses SetForegroundWindow from a background process unless that process
        /// received the last input event. Controller input never counts as "input" here, so we
        /// temporarily attach our thread to the current foreground thread's input queue.
        /// </summary>
        public static void ForceForeground(IntPtr hWnd)
        {
            if (!IsWindow(hWnd))
            {
                return;
            }

            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }

            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint ourThread = GetCurrentThreadId();
            bool attached = false;

            if (foregroundThread != ourThread && foregroundThread != 0)
            {
                attached = AttachThreadInput(ourThread, foregroundThread, true);
            }

            try
            {
                BringWindowToTop(hWnd);
                ShowWindow(hWnd, SW_SHOW);

                if (!SetForegroundWindow(hWnd))
                {
                    // Fallback trick: a synthetic Alt tap makes us the "last input" process.
                    InputSimulator.TapKey(VK_MENU);
                    SetForegroundWindow(hWnd);
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(ourThread, foregroundThread, false);
                }
            }
        }

        /// <summary>Polite close: same as clicking X. Games usually show a "quit?" prompt or just exit.</summary>
        public static void RequestClose(IntPtr hWnd)
        {
            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Hard kill for frozen games. Only call after the user confirms.</summary>
        public static bool KillProcess(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Kill failed for {processId}: {ex.Message}");
                return false;
            }
        }

        // The desktop and taskbar cover the screen too, but nobody is playing them.
        private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd" };

        /// <summary>
        /// True when the window in front belongs to another app and covers its whole monitor: a game
        /// (exclusive or borderless full screen), a video player, Big Picture. CouchDeck stays out of its way.
        /// </summary>
        public static bool IsFullScreenAppInFront()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
            {
                return false;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == Environment.ProcessId)
            {
                return false;
            }

            StringBuilder className = new(64);
            GetClassName(hWnd, className, className.Capacity);
            if (ShellClasses.Contains(className.ToString()))
            {
                return false;
            }

            // A maximized window with a title bar can cover the screen too (auto-hiding taskbar). Games have none.
            if ((GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION)
            {
                return false;
            }

            MonitorInfo monitor = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetWindowRect(hWnd, out Rect window) || !GetMonitorInfo(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref monitor))
            {
                return false;
            }

            return window.Left <= monitor.Monitor.Left && window.Top <= monitor.Monitor.Top
                && window.Right >= monitor.Monitor.Right && window.Bottom >= monitor.Monitor.Bottom;
        }

        public static OpenWindow? Describe(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            {
                return null;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            return new OpenWindow(hWnd, GetTitle(hWnd), TryGetProcessName((int)pid), (int)pid);
        }

        private static bool IsAltTabWindow(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd))
            {
                return false;
            }

            // Owned windows (dialogs, popups) are represented by their owner.
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
            {
                return false;
            }

            long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0)
            {
                return false;
            }

            // Suspended UWP apps keep an invisible "cloaked" window around.
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }

            return true;
        }

        private static string GetTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        private static string TryGetProcessName(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
