using System;
using System.Collections.Generic;

namespace CouchDeck.Audio
{
    /// <summary>
    /// The built-in sound packs, synthesized at startup (no audio files shipped). Every sound is
    /// built from a few enveloped oscillators, filtered noise and a small stereo room, then
    /// normalised to a per-event level so packs are equally loud.
    /// </summary>
    public static class SoundSynth
    {
        public const int Rate = 44100;

        public static Dictionary<Sfx, float[]> Build(string pack) =>
            pack.Equals("Arcade", StringComparison.OrdinalIgnoreCase) ? Arcade() : Soft();

        /// <summary>
        /// Scales a sound so its loudest 50 ms has the event's target loudness (RMS), never peaking
        /// above 0.95. Matching loudness rather than peaks keeps bright square waves (Arcade) and
        /// AI-generated files as loud as the soft built-ins at the same volume.
        /// </summary>
        public static void Normalize(float[] interleaved, Sfx sfx)
        {
            int window = Rate * 2 / 20; // 50 ms, interleaved stereo
            double loudest = 0;
            for (int start = 0; start < Math.Max(1, interleaved.Length - window); start += window / 2)
            {
                double sum = 0;
                int end = Math.Min(interleaved.Length, start + window);
                for (int i = start; i < end; i++)
                {
                    sum += interleaved[i] * (double)interleaved[i];
                }

                loudest = Math.Max(loudest, Math.Sqrt(sum / Math.Max(1, end - start)));
            }

            float peak = 0.0001f;
            foreach (float sample in interleaved)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            double scale = Level(sfx) * 0.27 / Math.Max(loudest, 0.0001);
            scale = Math.Min(scale, 0.95 / peak);
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] = (float)(interleaved[i] * scale);
            }
        }

        /// <summary>Relative loudness per event, so a tick is quieter than a launch.</summary>
        public static float Level(Sfx sfx) => sfx switch
        {
            Sfx.Navigate => 0.30f,
            Sfx.Bump => 0.30f,
            Sfx.ChangeUp or Sfx.ChangeDown => 0.30f,
            Sfx.Page => 0.34f,
            Sfx.ModalOpen or Sfx.ModalClose => 0.38f,
            Sfx.Back => 0.40f,
            Sfx.Select => 0.45f,
            Sfx.Error => 0.38f,
            Sfx.Favorite or Sfx.Notify => 0.40f,
            Sfx.Close => 0.40f,
            Sfx.Open => 0.48f,
            Sfx.Launch => 0.55f,
            _ => 0.4f
        };

        // =====================================================================
        // Soft: warm sine tones, airy noise, a bit of room. Modern console feel.
        // =====================================================================

        private static Dictionary<Sfx, float[]> Soft()
        {
            Dictionary<Sfx, float[]> s = new();

            s[Sfx.Navigate] = Make(Sfx.Navigate, 0.08, b =>
            {
                b.Tone(0, 0.05, 1750, 1550, 1.0, Wave.Sine, attack: 0.001, decay: 0.018);
                b.Tone(0, 0.03, 3500, 3200, 0.22, Wave.Sine, attack: 0.001, decay: 0.008);
                b.Noise(0, 0.012, 0.12, 6000, 3000, attack: 0.0005, decay: 0.003);
                b.Room(0.10, 0.35);
            });

            s[Sfx.Bump] = Make(Sfx.Bump, 0.12, b =>
            {
                b.Tone(0, 0.08, 330, 250, 1.0, Wave.Warm, attack: 0.002, decay: 0.03);
                b.Room(0.08, 0.3);
            });

            s[Sfx.Select] = Make(Sfx.Select, 0.4, b =>
            {
                b.Tone(0, 0.09, 880, 880, 1.0, Wave.Warm, attack: 0.002, decay: 0.05);
                b.Tone(0.045, 0.22, 1320, 1320, 0.9, Wave.Warm, attack: 0.002, decay: 0.08);
                b.Tone(0.045, 0.12, 2640, 2640, 0.12, Wave.Sine, attack: 0.001, decay: 0.03);
                b.Room(0.22, 0.55);
            });

            s[Sfx.Back] = Make(Sfx.Back, 0.4, b =>
            {
                b.Tone(0, 0.09, 1175, 1175, 1.0, Wave.Warm, attack: 0.002, decay: 0.05);
                b.Tone(0.045, 0.2, 784, 784, 0.9, Wave.Warm, attack: 0.002, decay: 0.07);
                b.Room(0.2, 0.5);
            });

            s[Sfx.Page] = Make(Sfx.Page, 0.4, b =>
            {
                b.Noise(0, 0.2, 1.0, 600, 4200, attack: 0.05, decay: 0.07);
                b.Tone(0.02, 0.12, 1400, 1900, 0.22, Wave.Sine, attack: 0.01, decay: 0.05);
                b.Room(0.18, 0.5);
            });

            s[Sfx.ModalOpen] = Make(Sfx.ModalOpen, 0.3, b =>
            {
                b.Tone(0, 0.12, 520, 880, 1.0, Wave.Sine, attack: 0.002, decay: 0.05, glide: 0.06);
                b.Tone(0, 0.08, 1040, 1760, 0.18, Wave.Sine, attack: 0.002, decay: 0.03, glide: 0.06);
                b.Room(0.2, 0.45);
            });

            s[Sfx.ModalClose] = Make(Sfx.ModalClose, 0.3, b =>
            {
                b.Tone(0, 0.12, 880, 520, 1.0, Wave.Sine, attack: 0.002, decay: 0.05, glide: 0.06);
                b.Room(0.18, 0.45);
            });

            s[Sfx.ChangeUp] = Make(Sfx.ChangeUp, 0.1, b =>
            {
                b.Tone(0, 0.06, 1500, 1800, 1.0, Wave.Sine, attack: 0.001, decay: 0.022);
                b.Tone(0, 0.04, 3000, 3600, 0.15, Wave.Sine, attack: 0.001, decay: 0.01);
                b.Room(0.1, 0.3);
            });

            s[Sfx.ChangeDown] = Make(Sfx.ChangeDown, 0.1, b =>
            {
                b.Tone(0, 0.06, 1500, 1250, 1.0, Wave.Sine, attack: 0.001, decay: 0.022);
                b.Tone(0, 0.04, 3000, 2500, 0.15, Wave.Sine, attack: 0.001, decay: 0.01);
                b.Room(0.1, 0.3);
            });

            s[Sfx.Open] = Make(Sfx.Open, 1.3, b =>
            {
                double[] notes = { 523.25, 659.25, 783.99, 1046.5 };
                for (int i = 0; i < notes.Length; i++)
                {
                    b.Tone(i * 0.045, 0.75, notes[i], notes[i], 0.7, Wave.Warm, attack: 0.004, decay: 0.22, pan: -0.4 + i * 0.27);
                }

                b.Tone(0.135, 0.6, 2093, 2093, 0.12, Wave.Sine, attack: 0.01, decay: 0.2);
                b.Noise(0, 0.3, 0.14, 2000, 8000, attack: 0.08, decay: 0.1);
                b.Room(0.35, 0.7);
            });

            s[Sfx.Close] = Make(Sfx.Close, 0.8, b =>
            {
                double[] notes = { 1046.5, 783.99, 523.25 };
                for (int i = 0; i < notes.Length; i++)
                {
                    b.Tone(i * 0.05, 0.45, notes[i], notes[i], 0.7, Wave.Warm, attack: 0.003, decay: 0.12, pan: 0.3 - i * 0.3);
                }

                b.Room(0.3, 0.6);
            });

            s[Sfx.Launch] = Make(Sfx.Launch, 2.0, b =>
            {
                b.Noise(0, 0.45, 0.55, 300, 6000, attack: 0.32, decay: 0.12);
                b.Tone(0, 0.42, 220, 880, 0.28, Wave.Sine, attack: 0.25, decay: 0.1);
                double[] chord = { 523.25, 659.25, 783.99, 1046.5, 1318.5 };
                for (int i = 0; i < chord.Length; i++)
                {
                    b.Tone(0.38 + i * 0.012, 1.2, chord[i], chord[i], 0.5, Wave.Warm, attack: 0.003, decay: 0.38, pan: -0.5 + i * 0.25);
                }

                b.Room(0.4, 0.8);
            });

            s[Sfx.Favorite] = Make(Sfx.Favorite, 0.9, b =>
            {
                Bell(b, 0, 1568, 0.8, 0.09);
                Bell(b, 0.07, 2093, 0.8, 0.13);
                b.Room(0.3, 0.6);
            });

            s[Sfx.Error] = Make(Sfx.Error, 0.4, b =>
            {
                b.Tone(0, 0.1, 330, 300, 1.0, Wave.SoftSquare, attack: 0.002, decay: 0.06);
                b.Tone(0.11, 0.14, 262, 240, 1.0, Wave.SoftSquare, attack: 0.002, decay: 0.07);
                b.Room(0.12, 0.4);
            });

            s[Sfx.Notify] = Make(Sfx.Notify, 1.2, b =>
            {
                Bell(b, 0, 1318.5, 0.8, 0.25);
                Bell(b, 0.09, 1975.5, 0.6, 0.3);
                b.Room(0.35, 0.7);
            });

            return s;
        }

        // =====================================================================
        // Arcade: band-limited square waves, quick arpeggios, almost dry. 8-bit feel.
        // =====================================================================

        private static Dictionary<Sfx, float[]> Arcade()
        {
            Dictionary<Sfx, float[]> s = new();

            void Steps(SynthBuffer b, double step, double length, double decay, params double[] notes)
            {
                for (int i = 0; i < notes.Length; i++)
                {
                    b.Tone(i * step, length, notes[i], notes[i], 1.0, Wave.Square, attack: 0.001, decay: decay);
                }
            }

            s[Sfx.Navigate] = Make(Sfx.Navigate, 0.06, b => Steps(b, 0, 0.035, 0.02, 1046.5));
            s[Sfx.Bump] = Make(Sfx.Bump, 0.08, b => Steps(b, 0, 0.06, 0.03, 196));
            s[Sfx.Select] = Make(Sfx.Select, 0.2, b => { Steps(b, 0.05, 0.08, 0.05, 1046.5, 1568); b.Room(0.06, 0.3); });
            s[Sfx.Back] = Make(Sfx.Back, 0.2, b => { Steps(b, 0.05, 0.08, 0.05, 1568, 1046.5); b.Room(0.06, 0.3); });
            s[Sfx.Page] = Make(Sfx.Page, 0.12, b =>
            {
                b.Noise(0, 0.06, 1.0, 3000, 3000, attack: 0.001, decay: 0.03);
                b.Tone(0, 0.05, 784, 784, 0.6, Wave.Square, attack: 0.001, decay: 0.03);
            });
            s[Sfx.ModalOpen] = Make(Sfx.ModalOpen, 0.15, b => Steps(b, 0.04, 0.06, 0.04, 784, 1174.7));
            s[Sfx.ModalClose] = Make(Sfx.ModalClose, 0.15, b => Steps(b, 0.04, 0.06, 0.04, 1174.7, 784));
            s[Sfx.ChangeUp] = Make(Sfx.ChangeUp, 0.06, b => Steps(b, 0, 0.04, 0.02, 1318.5));
            s[Sfx.ChangeDown] = Make(Sfx.ChangeDown, 0.06, b => Steps(b, 0, 0.04, 0.02, 987.8));
            s[Sfx.Open] = Make(Sfx.Open, 0.5, b => { Steps(b, 0.06, 0.12, 0.08, 523.25, 659.25, 783.99, 1046.5); b.Room(0.08, 0.35); });
            s[Sfx.Close] = Make(Sfx.Close, 0.4, b => { Steps(b, 0.05, 0.1, 0.06, 1046.5, 783.99, 659.25, 523.25); b.Room(0.08, 0.35); });
            s[Sfx.Launch] = Make(Sfx.Launch, 0.8, b =>
            {
                b.Tone(0, 0.36, 400, 1600, 1.0, Wave.Square, attack: 0.002, decay: 0.5, vibratoHz: 18, vibratoDepth: 0.03);
                b.Tone(0.36, 0.3, 2093, 2093, 1.0, Wave.Square, attack: 0.001, decay: 0.12);
                b.Room(0.08, 0.35);
            });
            s[Sfx.Favorite] = Make(Sfx.Favorite, 0.45, b =>
            {
                b.Tone(0, 0.07, 987.8, 987.8, 1.0, Wave.Square, attack: 0.001, decay: 0.2);
                b.Tone(0.07, 0.35, 1318.5, 1318.5, 1.0, Wave.Square, attack: 0.001, decay: 0.15);
            });
            s[Sfx.Error] = Make(Sfx.Error, 0.25, b => Steps(b, 0.11, 0.1, 0.06, 196, 146.8));
            s[Sfx.Notify] = Make(Sfx.Notify, 0.3, b => { Steps(b, 0.07, 0.12, 0.07, 1568, 2093); b.Room(0.06, 0.3); });

            return s;
        }

        /// <summary>Struck bell: fundamental plus a fast-decaying inharmonic partial.</summary>
        private static void Bell(SynthBuffer b, double start, double freq, double gain, double decay)
        {
            b.Tone(start, decay * 5, freq, freq, gain, Wave.Sine, attack: 0.001, decay: decay);
            b.Tone(start, decay * 2, freq * 2.76, freq * 2.76, gain * 0.2, Wave.Sine, attack: 0.001, decay: decay * 0.35);
            b.Tone(start, decay * 1.5, freq * 5.4, freq * 5.4, gain * 0.06, Wave.Sine, attack: 0.001, decay: decay * 0.2);
        }

        private static float[] Make(Sfx sfx, double seconds, Action<SynthBuffer> build)
        {
            SynthBuffer buffer = new(seconds + 0.45); // room for the reverb tail
            build(buffer);
            float[] data = buffer.Finish();
            Normalize(data, sfx);
            return data;
        }
    }

    public enum Wave { Sine, Warm, SoftSquare, Square }

    /// <summary>A stereo float buffer with a few synthesis primitives.</summary>
    internal sealed class SynthBuffer
    {
        private const int Rate = SoundSynth.Rate;
        private readonly float[] left;
        private readonly float[] right;
        private readonly Random random = new(7); // fixed seed: same sound every run

        public SynthBuffer(double seconds)
        {
            int length = (int)(seconds * Rate);
            left = new float[length];
            right = new float[length];
        }

        public void Tone(double start, double duration, double f0, double f1, double gain, Wave wave,
            double attack = 0.003, double decay = 0.08, double glide = -1, double pan = 0,
            double vibratoHz = 0, double vibratoDepth = 0)
        {
            int first = (int)(start * Rate);
            int count = (int)(duration * Rate);
            double glideTime = glide < 0 ? duration : glide;
            (double gl, double gr) = Pan(pan);
            double phase = 0;

            for (int i = 0; i < count && first + i < left.Length; i++)
            {
                double t = (double)i / Rate;
                double progress = Math.Min(1, t / glideTime);
                double freq = f0 * Math.Pow(f1 / f0, progress);
                if (vibratoHz > 0)
                {
                    freq *= 1 + vibratoDepth * Math.Sin(2 * Math.PI * vibratoHz * t);
                }

                phase += 2 * Math.PI * freq / Rate;

                double env = t < attack ? t / attack : Math.Exp(-(t - attack) / decay);
                env *= Math.Min(1, (duration - t) / 0.004); // no click at the cut

                double sample = Oscillator(wave, phase, freq) * env * gain;
                left[first + i] += (float)(sample * gl);
                right[first + i] += (float)(sample * gr);
            }
        }

        public void Noise(double start, double duration, double gain, double cutoff0, double cutoff1,
            double attack = 0.01, double decay = 0.05, double pan = 0)
        {
            int first = (int)(start * Rate);
            int count = (int)(duration * Rate);
            (double gl, double gr) = Pan(pan);
            double low = 0;
            double rumble = 0;

            for (int i = 0; i < count && first + i < left.Length; i++)
            {
                double t = (double)i / Rate;
                double cutoff = cutoff0 * Math.Pow(cutoff1 / cutoff0, t / duration);
                double alpha = 1 - Math.Exp(-2 * Math.PI * cutoff / Rate);
                double white = random.NextDouble() * 2 - 1;
                low += alpha * (white - low);
                rumble += 0.03 * (low - rumble); // strip sub-200 Hz mud

                double env = t < attack ? t / attack : Math.Exp(-(t - attack) / decay);
                env *= Math.Min(1, (duration - t) / 0.004);

                double sample = (low - rumble) * env * gain;
                left[first + i] += (float)(sample * gl);
                right[first + i] += (float)(sample * gr);
            }
        }

        /// <summary>Small Schroeder room: 4 damped combs + 2 allpasses per channel, decorrelated for width.</summary>
        public void Room(double mix, double size)
        {
            float[] wetL = Reverb(left, new[] { 1116, 1188, 1277, 1356 }, size);
            float[] wetR = Reverb(right, new[] { 1139, 1211, 1300, 1379 }, size);
            for (int i = 0; i < left.Length; i++)
            {
                left[i] += (float)(wetL[i] * mix);
                right[i] += (float)(wetR[i] * mix);
            }
        }

        private static float[] Reverb(float[] input, int[] combDelays, double size)
        {
            float[] output = new float[input.Length];
            double feedback = 0.62 + 0.28 * Math.Clamp(size, 0, 1);
            const double damping = 0.35;

            foreach (int delay in combDelays)
            {
                float[] line = new float[delay];
                int position = 0;
                double store = 0;
                for (int i = 0; i < input.Length; i++)
                {
                    double delayed = line[position];
                    store = delayed * (1 - damping) + store * damping;
                    line[position] = (float)(input[i] + store * feedback);
                    position = (position + 1) % delay;
                    output[i] += (float)(delayed * 0.25);
                }
            }

            foreach (int delay in new[] { 556, 225 })
            {
                float[] line = new float[delay];
                int position = 0;
                for (int i = 0; i < output.Length; i++)
                {
                    double delayed = line[position];
                    double value = output[i];
                    line[position] = (float)(value + delayed * 0.5);
                    output[i] = (float)(delayed - value * 0.5);
                    position = (position + 1) % delay;
                }
            }

            return output;
        }

        /// <summary>Trim the silent tail and return interleaved stereo at peak 1 (SoundSynth.Normalize sets loudness).</summary>
        public float[] Finish()
        {
            float peak = 0.0001f;
            int last = 0;
            for (int i = 0; i < left.Length; i++)
            {
                float magnitude = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
                peak = Math.Max(peak, magnitude);
            }

            for (int i = left.Length - 1; i >= 0; i--)
            {
                if (Math.Max(Math.Abs(left[i]), Math.Abs(right[i])) / peak > 0.0005)
                {
                    last = i;
                    break;
                }
            }

            int length = last + 1;
            float scale = 1f / peak;
            float[] interleaved = new float[length * 2];
            int fade = Math.Min(length, Rate / 100); // 10 ms fade at the very end
            for (int i = 0; i < length; i++)
            {
                float tail = i >= length - fade ? (length - i) / (float)fade : 1f;
                interleaved[i * 2] = left[i] * scale * tail;
                interleaved[i * 2 + 1] = right[i] * scale * tail;
            }

            return interleaved;
        }

        private static double Oscillator(Wave wave, double phase, double freq)
        {
            switch (wave)
            {
                case Wave.Warm:
                    return Math.Sin(phase) + 0.25 * Math.Sin(2 * phase) + 0.08 * Math.Sin(3 * phase);
                case Wave.SoftSquare:
                    return Math.Sin(phase) + Math.Sin(3 * phase) / 3 * 0.6 + Math.Sin(5 * phase) / 5 * 0.3;
                case Wave.Square:
                    // Band-limited: odd harmonics up to ~12 kHz, so high notes don't alias into hiss.
                    double sum = 0;
                    for (int k = 1; k <= 15 && k * freq < 12000; k += 2)
                    {
                        sum += Math.Sin(k * phase) / k;
                    }

                    return sum * 0.9;
                default:
                    return Math.Sin(phase);
            }
        }

        private static (double left, double right) Pan(double pan)
        {
            double angle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
            return (Math.Cos(angle) * Math.Sqrt(2), Math.Sin(angle) * Math.Sqrt(2));
        }
    }
}
