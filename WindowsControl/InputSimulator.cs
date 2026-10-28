using System;
using System.Runtime.InteropServices;
using static CouchDeck.WindowsControl.NativeMethods;

namespace CouchDeck.WindowsControl
{
    /// <summary>Thin wrapper over SendInput for fake mouse and keyboard events.</summary>
    public static class InputSimulator
    {
        private static readonly int InputSize = Marshal.SizeOf<InputRecord>();

        public static void MoveMouseBy(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
            {
                return;
            }

            Send(MouseEvent(MOUSEEVENTF_MOVE, dx, dy, 0));
        }

        public static void LeftDown() => Send(MouseEvent(MOUSEEVENTF_LEFTDOWN, 0, 0, 0));
        public static void LeftUp() => Send(MouseEvent(MOUSEEVENTF_LEFTUP, 0, 0, 0));
        public static void RightDown() => Send(MouseEvent(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0));
        public static void RightUp() => Send(MouseEvent(MOUSEEVENTF_RIGHTUP, 0, 0, 0));

        /// <summary>Positive = scroll up. 120 = one notch.</summary>
        public static void ScrollVertical(int amount)
        {
            if (amount != 0)
            {
                Send(MouseEvent(MOUSEEVENTF_WHEEL, 0, 0, amount));
            }
        }

        /// <summary>Positive = scroll right.</summary>
        public static void ScrollHorizontal(int amount)
        {
            if (amount != 0)
            {
                Send(MouseEvent(MOUSEEVENTF_HWHEEL, 0, 0, amount));
            }
        }

        public static void TapKey(ushort virtualKey)
        {
            Send(KeyEvent(virtualKey, false), KeyEvent(virtualKey, true));
        }

        /// <summary>Press keys in order, release in reverse. E.g. KeyCombo(VK_MENU, VK_F4) = Alt+F4.</summary>
        public static void KeyCombo(params ushort[] keys)
        {
            InputRecord[] inputs = new InputRecord[keys.Length * 2];
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[i] = KeyEvent(keys[i], false);
                inputs[keys.Length * 2 - 1 - i] = KeyEvent(keys[i], true);
            }

            Send(inputs);
        }

        public static void KeyDown(ushort virtualKey) => Send(KeyEvent(virtualKey, false));
        public static void KeyUp(ushort virtualKey) => Send(KeyEvent(virtualKey, true));

        private static InputRecord MouseEvent(uint flags, int dx, int dy, int data)
        {
            return new InputRecord
            {
                Type = INPUT_MOUSE,
                U = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags } }
            };
        }

        private static InputRecord KeyEvent(ushort virtualKey, bool up)
        {
            uint flags = up ? KEYEVENTF_KEYUP : 0;
            if (IsExtended(virtualKey))
            {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }

            return new InputRecord
            {
                Type = INPUT_KEYBOARD,
                U = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } }
            };
        }

        private static bool IsExtended(ushort vk) =>
            vk is VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN or VK_LWIN
                or VK_VOLUME_MUTE or VK_VOLUME_DOWN or VK_VOLUME_UP or VK_MEDIA_PLAY_PAUSE;

        private static void Send(params InputRecord[] inputs)
        {
            SendInput((uint)inputs.Length, inputs, InputSize);
        }
    }
}
