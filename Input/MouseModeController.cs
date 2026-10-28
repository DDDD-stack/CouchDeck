using System;
using CouchDeck.WindowsControl;
using static CouchDeck.WindowsControl.NativeMethods;

namespace CouchDeck.Input
{
    /// <summary>
    /// "Controller as mouse" for anything that isn't controller friendly (browsers, launchers,
    /// login screens, Windows dialogs).
    ///
    ///   Left stick   move cursor (curved: small tilt = precise, full tilt = fast)
    ///   RT (held)    precision mode, cursor 3x slower
    ///   Right stick  scroll (vertical + horizontal)
    ///   A            left click (hold to drag)
    ///   B            right click
    ///   X            on-screen keyboard
    ///   Y            Enter
    ///   LB           Escape
    ///   RB           Backspace
    ///   D-pad        arrow keys
    ///
    /// Runs directly on the controller polling thread (SendInput is thread safe), so it keeps
    /// working while the overlay is hidden.
    /// </summary>
    public sealed class MouseModeController
    {
        private readonly ControllerService controller;

        private float pendingX;
        private float pendingY;
        private float pendingScrollX;
        private float pendingScrollY;
        private long lastTick = Environment.TickCount64;

        public MouseModeController(ControllerService controller)
        {
            this.controller = controller;
            controller.StateUpdated += OnStateUpdated;
            controller.ButtonPressed += OnButtonPressed;
            controller.ButtonReleased += OnButtonReleased;
        }

        public bool IsEnabled { get; private set; }

        /// <summary>Set by the app while the overlay is on screen, so buttons don't do double duty.</summary>
        public bool IsSuspended { get; set; }

        /// <summary>Pixels per second at full stick tilt. Exposed in settings.</summary>
        public float MaxSpeed { get; set; } = 1800f;

        public float ScrollSpeed { get; set; } = 900f;

        public event EventHandler<bool>? EnabledChanged;

        private bool Active => IsEnabled && !IsSuspended;

        public void Toggle(int controllerIndex)
        {
            SetEnabled(!IsEnabled);

            // Two short buzzes = on, one = off. You can't see a mode indicator from the couch.
            controller.Rumble(controllerIndex, IsEnabled ? 180 : 80);
        }

        public void SetEnabled(bool enabled)
        {
            if (IsEnabled == enabled)
            {
                return;
            }

            IsEnabled = enabled;

            // Never leave a button stuck down when switching off mid-drag.
            if (!enabled)
            {
                InputSimulator.LeftUp();
                InputSimulator.RightUp();
            }

            EnabledChanged?.Invoke(this, enabled);
        }

        private void OnStateUpdated(object? sender, (int controllerIndex, PadState state) e)
        {
            long now = Environment.TickCount64;
            float seconds = Math.Clamp((now - lastTick) / 1000f, 0f, 0.05f);
            lastTick = now;

            if (!Active)
            {
                return;
            }

            PadState state = e.state;

            float speed = MaxSpeed * (state.RightTrigger > 0.5f ? 0.33f : 1f);
            pendingX += Curve(state.LeftX) * speed * seconds;
            pendingY += -Curve(state.LeftY) * speed * seconds; // stick up = positive Y, screen up = negative Y

            int moveX = (int)pendingX;
            int moveY = (int)pendingY;
            pendingX -= moveX;
            pendingY -= moveY;
            InputSimulator.MoveMouseBy(moveX, moveY);

            pendingScrollY += Curve(state.RightY) * ScrollSpeed * seconds;
            pendingScrollX += Curve(state.RightX) * ScrollSpeed * seconds;

            int scrollY = (int)pendingScrollY;
            int scrollX = (int)pendingScrollX;
            pendingScrollY -= scrollY;
            pendingScrollX -= scrollX;
            InputSimulator.ScrollVertical(scrollY);
            InputSimulator.ScrollHorizontal(scrollX);
        }

        private void OnButtonPressed(object? sender, PadButtonEventArgs e)
        {
            if (!Active)
            {
                return;
            }

            switch (e.Button)
            {
                case PadButton.A when !e.IsRepeat:
                    InputSimulator.LeftDown();
                    break;
                case PadButton.B when !e.IsRepeat:
                    InputSimulator.RightDown();
                    break;
                case PadButton.X:
                    SystemActions.ShowOnScreenKeyboard();
                    break;
                case PadButton.Y:
                    InputSimulator.TapKey(VK_RETURN);
                    break;
                case PadButton.LeftShoulder:
                    InputSimulator.TapKey(VK_ESCAPE);
                    break;
                case PadButton.RightShoulder:
                    InputSimulator.TapKey(VK_BACK);
                    break;
                case PadButton.DPadUp:
                    InputSimulator.TapKey(VK_UP);
                    break;
                case PadButton.DPadDown:
                    InputSimulator.TapKey(VK_DOWN);
                    break;
                case PadButton.DPadLeft:
                    InputSimulator.TapKey(VK_LEFT);
                    break;
                case PadButton.DPadRight:
                    InputSimulator.TapKey(VK_RIGHT);
                    break;
            }
        }

        private void OnButtonReleased(object? sender, PadButtonEventArgs e)
        {
            // Release even if we got suspended in between, otherwise a drag could get stuck.
            if (!IsEnabled)
            {
                return;
            }

            if (e.Button == PadButton.A)
            {
                InputSimulator.LeftUp();
            }
            else if (e.Button == PadButton.B)
            {
                InputSimulator.RightUp();
            }
        }

        /// <summary>Squared response curve keeping the sign: fine control near center, speed at the edge.</summary>
        private static float Curve(float value) => value * MathF.Abs(value);
    }
}
