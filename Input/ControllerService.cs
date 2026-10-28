using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CouchDeck.Input
{
    /// <summary>
    /// Polls every connected controller on a background thread and turns raw state into events:
    ///   ButtonPressed / ButtonReleased  - edge detected, with auto-repeat for directions
    ///   StateUpdated                    - every poll, analog values (used by mouse mode)
    ///   ChordHeld                       - a registered button combination held for N ms
    ///
    /// Pads 0-3 are XInput (Xbox and compatible), pads 4-7 PlayStation controllers (see PlayStationPads).
    ///
    /// IMPORTANT: every event is raised on the polling thread. UI code must marshal to the
    /// WPF Dispatcher (App.xaml.cs does this).
    /// </summary>
    public sealed class ControllerService : IDisposable
    {
        private const int PollIntervalMs = 8;          // ~120 Hz, ~0.1% CPU
        private const int DisconnectedRecheckMs = 1000; // probing empty slots is slow; don't do it every tick
        private const int PlayStationScanMs = 2000;     // looking for newly connected PlayStation controllers
        private const int CopyWindowMs = 60;            // the same press from two pads this close together is one press
        private const float StickDeadZone = 0.24f;
        private const float StickAsDPadThreshold = 0.6f;
        private const float TriggerThreshold = 0.1f;

        private const int MaxPads = XInputNative.MaxControllers + PlayStationPads.MaxControllers;
        private const PadButton DirectionMask = PadButton.DPadUp | PadButton.DPadDown | PadButton.DPadLeft | PadButton.DPadRight;

        private readonly Thread pollThread;
        private volatile bool running;

        private readonly PlayStationPads playStation = new();
        private long nextPlayStationScan;

        private readonly PadButton[] previousButtons = new PadButton[MaxPads];
        private readonly bool[] connected = new bool[MaxPads];
        private readonly long[] nextProbeAt = new long[MaxPads];

        // A PlayStation pad that DS4Windows / Steam also present as an Xbox pad: index of that Xbox pad, else -1.
        private readonly int[] copyOf = new int[MaxPads];
        private readonly Dictionary<PadButton, (int pad, long at)> lastPress = new();
        private readonly Dictionary<(int pad, PadButton button), long> nextRepeatAt = new();
        private readonly Dictionary<(int pad, PadButton chord), long> chordHeldSince = new();
        private readonly HashSet<(int pad, PadButton chord)> chordFired = new();
        private readonly List<(PadButton chord, int holdMs)> registeredChords = new();
        private readonly object chordLock = new();

        private readonly Stopwatch clock = Stopwatch.StartNew();

        public event EventHandler<PadButtonEventArgs>? ButtonPressed;
        public event EventHandler<PadButtonEventArgs>? ButtonReleased;
        public event EventHandler<(int controllerIndex, PadState state)>? StateUpdated;
        public event EventHandler<(int controllerIndex, PadButton chord)>? ChordHeld;
        public event EventHandler<(int controllerIndex, bool connected)>? ConnectionChanged;

        /// <summary>When true, the left stick also produces DPad presses (for menu navigation).</summary>
        public bool LeftStickAsDPad { get; set; } = true;

        public int RepeatDelayMs { get; set; } = 400;
        public int RepeatIntervalMs { get; set; } = 110;

        public ControllerService()
        {
            Array.Fill(copyOf, -1);
            pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "CouchDeck controller poll",
                Priority = ThreadPriority.AboveNormal
            };
        }

        public void Start()
        {
            running = true;
            pollThread.Start();
        }

        /// <summary>Fire ChordHeld when all buttons in <paramref name="chord"/> are held for <paramref name="holdMs"/>.</summary>
        public void RegisterChord(PadButton chord, int holdMs)
        {
            lock (chordLock)
            {
                registeredChords.Add((chord, holdMs));
            }
        }

        /// <summary>Changes the hold time of an already registered chord (Customize page).</summary>
        public void SetChordHold(PadButton chord, int holdMs)
        {
            lock (chordLock)
            {
                int index = registeredChords.FindIndex(c => c.chord == chord);
                if (index >= 0)
                {
                    registeredChords[index] = (chord, holdMs);
                }
                else
                {
                    registeredChords.Add((chord, holdMs));
                }
            }
        }

        /// <summary>Short rumble, useful as feedback (e.g. mouse mode toggled). Xbox pads only.</summary>
        public void Rumble(int controllerIndex, int durationMs = 120, ushort strength = 30000)
        {
            if (controllerIndex >= XInputNative.MaxControllers)
            {
                return; // PlayStation rumble needs its own output reports (with a checksum over Bluetooth)
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                XInputNative.XInputVibration on = new() { LeftMotorSpeed = strength, RightMotorSpeed = strength };
                XInputNative.XInputVibration off = new();
                XInputNative.XInputSetState(controllerIndex, ref on);
                Thread.Sleep(durationMs);
                XInputNative.XInputSetState(controllerIndex, ref off);
            });
        }

        private void PollLoop()
        {
            while (running)
            {
                long now = clock.ElapsedMilliseconds;

                if (now >= nextPlayStationScan)
                {
                    playStation.Refresh();
                    nextPlayStationScan = now + PlayStationScanMs;
                }

                for (int pad = 0; pad < MaxPads; pad++)
                {
                    bool isConnected;
                    XInputNative.XInputGamepad gamepad;

                    if (pad < XInputNative.MaxControllers)
                    {
                        // Polling a disconnected slot costs ~1ms each; only re-probe once a second.
                        if (!connected[pad] && now < nextProbeAt[pad])
                        {
                            continue;
                        }

                        isConnected = XInputNative.XInputGetStateEx(pad, out XInputNative.XInputState raw) == XInputNative.ERROR_SUCCESS;
                        gamepad = raw.Gamepad;
                    }
                    else
                    {
                        isConnected = playStation.TryGetState(pad - XInputNative.MaxControllers, out gamepad);
                    }

                    if (isConnected != connected[pad])
                    {
                        connected[pad] = isConnected;
                        previousButtons[pad] = PadButton.None;
                        ForgetCopies(pad);
                        ConnectionChanged?.Invoke(this, (pad, isConnected));
                    }

                    if (!isConnected)
                    {
                        nextProbeAt[pad] = now + DisconnectedRecheckMs;
                        continue;
                    }

                    // Its Xbox copy (DS4Windows, Steam) already speaks for it: listening to both would double every press.
                    if (copyOf[pad] >= 0)
                    {
                        continue;
                    }

                    PadState state = Normalise(gamepad);
                    ProcessButtons(pad, state, now);
                    ProcessChords(pad, state.Buttons, now);
                    StateUpdated?.Invoke(this, (pad, state));
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        private PadState Normalise(XInputNative.XInputGamepad gamepad)
        {
            (float lx, float ly) = ApplyRadialDeadZone(gamepad.ThumbLX, gamepad.ThumbLY);
            (float rx, float ry) = ApplyRadialDeadZone(gamepad.ThumbRX, gamepad.ThumbRY);

            float lt = gamepad.LeftTrigger / 255f;
            float rt = gamepad.RightTrigger / 255f;

            PadButton buttons = (PadButton)gamepad.Buttons;

            if (LeftStickAsDPad)
            {
                if (ly > StickAsDPadThreshold) buttons |= PadButton.DPadUp;
                if (ly < -StickAsDPadThreshold) buttons |= PadButton.DPadDown;
                if (lx < -StickAsDPadThreshold) buttons |= PadButton.DPadLeft;
                if (lx > StickAsDPadThreshold) buttons |= PadButton.DPadRight;
            }

            return new PadState(buttons, lx, ly, rx, ry, lt < TriggerThreshold ? 0 : lt, rt < TriggerThreshold ? 0 : rt);
        }

        private static (float x, float y) ApplyRadialDeadZone(short rawX, short rawY)
        {
            float x = rawX / 32767f;
            float y = rawY / 32767f;
            float magnitude = MathF.Sqrt(x * x + y * y);

            if (magnitude < StickDeadZone)
            {
                return (0, 0);
            }

            // Rescale so movement starts from 0 just outside the dead zone (no jump).
            float scaled = Math.Min(1f, (magnitude - StickDeadZone) / (1f - StickDeadZone));
            return (x / magnitude * scaled, y / magnitude * scaled);
        }

        private void ProcessButtons(int pad, PadState state, long now)
        {
            PadButton current = state.Buttons;
            PadButton previous = previousButtons[pad];
            PadButton pressed = current & ~previous;
            PadButton released = previous & ~current;

            bool playStationLayout = IsPlayStation(pad);

            foreach (PadButton button in EachFlag(pressed))
            {
                if ((button & DirectionMask) != 0)
                {
                    nextRepeatAt[(pad, button)] = now + RepeatDelayMs;
                }

                if (!IsCopyOfAnotherPress(pad, button, now))
                {
                    ButtonPressed?.Invoke(this, new PadButtonEventArgs(pad, button, false, playStationLayout));
                }
            }

            foreach (PadButton button in EachFlag(released))
            {
                nextRepeatAt.Remove((pad, button));
                ButtonReleased?.Invoke(this, new PadButtonEventArgs(pad, button, false, playStationLayout));
            }

            // Auto-repeat for held directions, so holding down scrolls a long game list.
            foreach (PadButton button in EachFlag(current & previous & DirectionMask))
            {
                if (nextRepeatAt.TryGetValue((pad, button), out long due) && now >= due)
                {
                    nextRepeatAt[(pad, button)] = now + RepeatIntervalMs;
                    ButtonPressed?.Invoke(this, new PadButtonEventArgs(pad, button, true, playStationLayout));
                }
            }

            previousButtons[pad] = current;
        }

        private static bool IsPlayStationSlot(int pad) => pad >= XInputNative.MaxControllers;

        /// <summary>A PlayStation controller, read directly or through its Xbox copy: hints show ✕ ○ □ △.</summary>
        private bool IsPlayStation(int pad) => IsPlayStationSlot(pad) || Array.IndexOf(copyOf, pad) >= 0;

        /// <summary>
        /// DS4Windows, Steam and similar tools present a PlayStation controller as a virtual Xbox one, so the same press
        /// arrives twice: from the controller itself and from its copy. The second one is dropped, and once a
        /// PlayStation controller turns out to have a copy, only the copy is listened to (it has the tool's mapping).
        /// </summary>
        private bool IsCopyOfAnotherPress(int pad, PadButton button, long now)
        {
            if (lastPress.TryGetValue(button, out (int pad, long at) last) && last.pad != pad && now - last.at <= CopyWindowMs)
            {
                (int playStationPad, int xboxPad) = IsPlayStationSlot(pad) ? (pad, last.pad) : (last.pad, pad);
                if (IsPlayStationSlot(playStationPad) && !IsPlayStationSlot(xboxPad))
                {
                    copyOf[playStationPad] = xboxPad;
                }

                return true;
            }

            lastPress[button] = (pad, now);
            return false;
        }

        /// <summary>A pad went away: anything known about copies involving it is stale.</summary>
        private void ForgetCopies(int pad)
        {
            copyOf[pad] = -1;
            for (int other = 0; other < copyOf.Length; other++)
            {
                if (copyOf[other] == pad)
                {
                    copyOf[other] = -1;
                }
            }
        }

        private void ProcessChords(int pad, PadButton current, long now)
        {
            List<(PadButton chord, int holdMs)> chords;
            lock (chordLock)
            {
                chords = new List<(PadButton, int)>(registeredChords);
            }

            foreach ((PadButton chord, int holdMs) in chords)
            {
                (int, PadButton) key = (pad, chord);
                bool held = (current & chord) == chord;

                if (!held)
                {
                    chordHeldSince.Remove(key);
                    chordFired.Remove(key);
                    continue;
                }

                if (!chordHeldSince.TryGetValue(key, out long since))
                {
                    chordHeldSince[key] = now;
                    continue;
                }

                // Fire once per hold; the chord must be released before it can fire again.
                if (!chordFired.Contains(key) && now - since >= holdMs)
                {
                    chordFired.Add(key);
                    ChordHeld?.Invoke(this, (pad, chord));
                }
            }
        }

        private static IEnumerable<PadButton> EachFlag(PadButton flags)
        {
            ushort value = (ushort)flags;
            for (int bit = 0; bit < 16; bit++)
            {
                ushort mask = (ushort)(1 << bit);
                if ((value & mask) != 0)
                {
                    yield return (PadButton)mask;
                }
            }
        }

        public void Dispose()
        {
            running = false;
            if (pollThread.IsAlive)
            {
                pollThread.Join(200);
            }

            playStation.Dispose();
        }
    }
}
