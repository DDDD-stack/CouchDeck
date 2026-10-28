using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CouchDeck.Config;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CouchDeck.Audio
{
    /// <summary>Every UI moment that can make a sound.</summary>
    public enum Sfx
    {
        Navigate, Bump, Select, Back, Page, ModalOpen, ModalClose, ChangeUp, ChangeDown,
        Open, Close, Launch, Favorite, Error, Notify
    }

    /// <summary>
    /// UI sound effects. Sounds are pre-rendered into memory and mixed (so a tick doesn't cut off a
    /// chime) into one low-latency output. Opening the device takes ~200 ms, so it never happens on
    /// the UI thread: a few seconds after the overlay hides the stream is paused (a running stream
    /// would keep Windows from sleeping; resuming a paused one is instant), and after 10 idle
    /// minutes the device is released.
    ///
    /// Packs:
    ///   "Soft", "Arcade"           synthesized at startup (SoundSynth), no files
    ///   any folder in %AppData%\CouchDeck\sounds\&lt;Pack&gt;\   your own .wav/.mp3 files named after
    ///                              the events (navigate.wav, select.mp3, ...); missing ones fall back to Soft
    ///   "ElevenLabs"               generated from text prompts with an ElevenLabs API key (Customize page)
    /// </summary>
    public sealed class SoundService : IDisposable
    {
        public static readonly string[] BuiltInPacks = { "Soft", "Arcade" };

        private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SoundSynth.Rate, 2);

        private readonly object gate = new();
        private readonly MixingSampleProvider mixer = new(MixFormat) { ReadFully = true };
        private readonly Timer idleTimer;
        private readonly Timer releaseTimer;
        private Dictionary<Sfx, float[]> sounds = new();
        private Dictionary<Sfx, float[]>? softFallback;
        private WaveOutEvent? output;
        private volatile bool outputReady;
        private SoundSettings settings = new();
        private string? loadedPack;
        private long lastNavigateTick;

        public SoundService()
        {
            idleTimer = new Timer(_ => PauseOutput(), null, Timeout.Infinite, Timeout.Infinite);
            releaseTimer = new Timer(_ => CloseOutput(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public static string SoundsFolder { get; } = Path.Combine(ConfigStore.FolderPath, "sounds");

        /// <summary>Built-in packs plus every sub-folder of the sounds folder that has audio files.</summary>
        public static IReadOnlyList<string> AvailablePacks()
        {
            List<string> packs = BuiltInPacks.ToList();
            try
            {
                if (Directory.Exists(SoundsFolder))
                {
                    packs.AddRange(Directory.EnumerateDirectories(SoundsFolder)
                        .Where(dir => Directory.EnumerateFiles(dir).Any(IsAudioFile))
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrEmpty(name) && !name.StartsWith('.')
                            && !BuiltInPacks.Contains(name, StringComparer.OrdinalIgnoreCase))
                        .Cast<string>());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Sound packs unreadable: {ex.Message}");
            }

            return packs;
        }

        public static string FileName(Sfx sfx) => sfx switch
        {
            Sfx.ModalOpen => "modal-open",
            Sfx.ModalClose => "modal-close",
            Sfx.ChangeUp => "change-up",
            Sfx.ChangeDown => "change-down",
            _ => sfx.ToString().ToLowerInvariant()
        };

        /// <summary>Apply settings; (re)loads the pack in the background when it changed.</summary>
        public void Apply(SoundSettings newSettings)
        {
            settings = newSettings;
            if (!string.Equals(loadedPack, settings.Pack, StringComparison.OrdinalIgnoreCase))
            {
                string pack = settings.Pack;
                loadedPack = pack;
                _ = Task.Run(() =>
                {
                    Dictionary<Sfx, float[]> loaded = LoadPack(pack);
                    if (loadedPack == pack)
                    {
                        sounds = loaded;
                    }
                });
            }
        }

        /// <summary>Forces a reload of the current pack (e.g. after generating new files).</summary>
        public void Reload()
        {
            loadedPack = null;
            Apply(settings);
        }

        public void Play(Sfx sfx)
        {
            if (!settings.Enabled || settings.Volume <= 0)
            {
                return;
            }

            if (sfx is Sfx.Navigate or Sfx.Bump or Sfx.ChangeUp or Sfx.ChangeDown)
            {
                if (!settings.NavigationSounds)
                {
                    return;
                }

                // Holding a direction repeats every ~100 ms; stacking ticks closer than that sounds like a buzz.
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastNavigateTick) < 45)
                {
                    return;
                }

                Interlocked.Exchange(ref lastNavigateTick, now);
            }

            if (!sounds.TryGetValue(sfx, out float[]? data))
            {
                return;
            }

            float volume = (float)Math.Pow(Math.Clamp(settings.Volume, 0, 100) / 100.0, 1.6); // perceptual curve
            BufferProvider voice = new(data, volume);

            // The mixer is thread-safe; the UI thread never waits for the audio device.
            if (outputReady)
            {
                mixer.AddMixerInput(voice);
                return;
            }

            Task.Run(() =>
            {
                lock (gate)
                {
                    if (EnsureOutput())
                    {
                        mixer.AddMixerInput(voice);
                    }
                }
            });
        }

        /// <summary>Overlay shown: open the device (in the background) so the first sound has no start-up delay.</summary>
        public void Wake()
        {
            idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            releaseTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (settings.Enabled && !outputReady)
            {
                Task.Run(() =>
                {
                    lock (gate)
                    {
                        EnsureOutput();
                    }
                });
            }
        }

        /// <summary>Overlay hidden: let the close sound finish, then release the device.</summary>
        public void Idle()
        {
            idleTimer.Change(6000, Timeout.Infinite);
            releaseTimer.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        }

        // =====================================================================
        // ElevenLabs pack generation
        // =====================================================================

        /// <summary>Text prompts for an AI-generated pack: (event, prompt, seconds).</summary>
        public static readonly IReadOnlyList<(Sfx sfx, string prompt, double seconds)> ElevenLabsPrompts = new List<(Sfx, string, double)>
        {
            (Sfx.Navigate, "Very short soft UI tick for moving between items in a modern game console menu, clean digital click, subtle, dry", 0.5),
            (Sfx.Bump, "Very short soft muted thud, menu boundary bump on a game console, dry", 0.5),
            (Sfx.Select, "Short bright confirm sound for selecting an item in a modern game console menu, two rising soft synth notes", 0.6),
            (Sfx.Back, "Short soft back/cancel sound in a modern game console menu, two descending gentle synth notes", 0.6),
            (Sfx.Page, "Quick airy whoosh swipe for switching tabs in a game console dashboard", 0.6),
            (Sfx.ModalOpen, "Soft pop-up sound of a menu panel opening, gentle rising bubble, modern UI", 0.5),
            (Sfx.ModalClose, "Soft sound of a menu panel closing, gentle falling bubble, modern UI", 0.5),
            (Sfx.ChangeUp, "Tiny high soft click for increasing a value in a settings menu, dry", 0.5),
            (Sfx.ChangeDown, "Tiny lower soft click for decreasing a value in a settings menu, dry", 0.5),
            (Sfx.Open, "Elegant game console dashboard opening sound, shimmering rising synth arpeggio with a soft airy whoosh, premium and modern", 1.2),
            (Sfx.Close, "Elegant game console dashboard closing sound, soft descending synth notes", 0.8),
            (Sfx.Launch, "Exciting game launch sound for a console dashboard, swelling whoosh into a bright confident synth chord", 2.0),
            (Sfx.Favorite, "Magical sparkle chime for adding a game to favorites, two bright twinkling bell notes", 0.8),
            (Sfx.Error, "Soft low error buzz for an invalid action in a console menu, two short muted tones", 0.6),
            (Sfx.Notify, "Pleasant notification chime for a game console pop-up, soft bell with gentle reverb", 1.0)
        };

        /// <summary>
        /// Generates the "ElevenLabs" pack with the ElevenLabs sound-generation API (one request per
        /// event) into sounds\ElevenLabs. Reports progress as (done, total).
        /// </summary>
        public static async Task<string?> GenerateElevenLabsPackAsync(string apiKey, IProgress<(int done, int total)>? progress, CancellationToken token)
        {
            // Built in a temporary folder and swapped in only when every sound succeeded.
            string folder = Path.Combine(SoundsFolder, "ElevenLabs");
            string staging = Path.Combine(SoundsFolder, ".ElevenLabs-new");
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);

            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

            try
            {
                string? error = await DownloadAllAsync(http, staging, progress, token);
                if (error != null)
                {
                    return error;
                }

                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                Directory.Move(staging, folder);
                return null; // success
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        private static async Task<string?> DownloadAllAsync(HttpClient http, string folder, IProgress<(int done, int total)>? progress, CancellationToken token)
        {
            int done = 0;
            foreach ((Sfx sfx, string prompt, double seconds) in ElevenLabsPrompts)
            {
                string body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["text"] = prompt,
                    ["model_id"] = "eleven_text_to_sound_v2",
                    ["duration_seconds"] = seconds,
                    ["prompt_influence"] = 0.7
                });

                using HttpResponseMessage response = await http.PostAsync(
                    "https://api.elevenlabs.io/v1/sound-generation?output_format=mp3_44100_128",
                    new StringContent(body, Encoding.UTF8, "application/json"), token);

                if (!response.IsSuccessStatusCode)
                {
                    string detail = await response.Content.ReadAsStringAsync(token);
                    return $"ElevenLabs returned {(int)response.StatusCode} for \"{FileName(sfx)}\": {Truncate(detail, 160)}";
                }

                byte[] audio = await response.Content.ReadAsByteArrayAsync(token);
                await File.WriteAllBytesAsync(Path.Combine(folder, FileName(sfx) + ".mp3"), audio, token);
                progress?.Report((++done, ElevenLabsPrompts.Count));
            }

            return null; // success
        }

        private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";

        // =====================================================================
        // Loading
        // =====================================================================

        private Dictionary<Sfx, float[]> LoadPack(string pack)
        {
            if (BuiltInPacks.Contains(pack, StringComparer.OrdinalIgnoreCase))
            {
                return SoundSynth.Build(pack);
            }

            softFallback ??= SoundSynth.Build("Soft");
            Dictionary<Sfx, float[]> result = new(softFallback);
            string folder = Path.Combine(SoundsFolder, pack);

            foreach (Sfx sfx in Enum.GetValues<Sfx>())
            {
                string? file = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, FileName(sfx) + ".*").FirstOrDefault(IsAudioFile)
                    : null;
                if (file != null && LoadFile(file, sfx) is float[] data)
                {
                    result[sfx] = data;
                }
            }

            return result;
        }

        /// <summary>Any .wav/.mp3 -> 44.1 kHz stereo float, leading silence trimmed, levelled like the built-ins.</summary>
        private static float[]? LoadFile(string path, Sfx sfx)
        {
            try
            {
                using AudioFileReader reader = new(path);
                ISampleProvider provider = reader;
                if (provider.WaveFormat.Channels == 1)
                {
                    provider = new MonoToStereoSampleProvider(provider);
                }
                else if (provider.WaveFormat.Channels > 2)
                {
                    return null;
                }

                if (provider.WaveFormat.SampleRate != SoundSynth.Rate)
                {
                    provider = new WdlResamplingSampleProvider(provider, SoundSynth.Rate);
                }

                int maxSamples = SoundSynth.Rate * 2 * (sfx == Sfx.Launch ? 4 : 2);
                List<float> samples = new();
                float[] buffer = new float[SoundSynth.Rate];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0 && samples.Count < maxSamples)
                {
                    samples.AddRange(buffer.Take(read));
                }

                // Generated audio often starts with silence; UI sounds must start on the press.
                int start = samples.FindIndex(s => Math.Abs(s) > 0.01f);
                if (start < 0)
                {
                    return null;
                }

                start = Math.Max(0, start - start % 2 - 2 * SoundSynth.Rate / 500); // keep 2 ms before the onset
                float[] data = samples.Skip(start).Take(maxSamples).ToArray();

                SoundSynth.Normalize(data, sfx);
                int fade = Math.Min(data.Length / 2, SoundSynth.Rate / 20) * 2; // 50 ms fade-out
                for (int i = data.Length - fade; i < data.Length; i++)
                {
                    data[i] *= (data.Length - i) / (float)fade;
                }

                return data;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Sound file unreadable {path}: {ex.Message}");
                return null;
            }
        }

        private static bool IsAudioFile(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".wav" or ".mp3";
        }

        // =====================================================================
        // Output device
        // =====================================================================

        // Caller holds gate.
        private bool EnsureOutput()
        {
            if (output != null)
            {
                if (output.PlaybackState != PlaybackState.Playing)
                {
                    output.Play(); // resume after an idle pause: instant
                }

                outputReady = true;
                return true;
            }

            try
            {
                output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
                output.Init(mixer);
                output.Play();
                outputReady = true;
                return true;
            }
            catch (Exception ex)
            {
                // No audio device (e.g. TV unplugged): stay silent, try again on the next sound.
                Debug.WriteLine($"Audio output unavailable: {ex.Message}");
                output?.Dispose();
                output = null;
                return false;
            }
        }

        private void PauseOutput()
        {
            lock (gate)
            {
                if (output?.PlaybackState == PlaybackState.Playing)
                {
                    outputReady = false;
                    output.Pause();
                }
            }
        }

        private void CloseOutput()
        {
            lock (gate)
            {
                if (output == null)
                {
                    return;
                }

                outputReady = false;
                try
                {
                    output.Stop();
                    output.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Audio close failed: {ex.Message}");
                }

                output = null;
                mixer.RemoveAllMixerInputs();
            }
        }

        public void Dispose()
        {
            idleTimer.Dispose();
            releaseTimer.Dispose();
            CloseOutput();
        }

        /// <summary>Plays a pre-rendered buffer once, at a volume.</summary>
        private sealed class BufferProvider : ISampleProvider
        {
            private readonly float[] data;
            private readonly float volume;
            private int position;

            public BufferProvider(float[] data, float volume)
            {
                this.data = data;
                this.volume = volume;
            }

            public WaveFormat WaveFormat => MixFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int available = Math.Min(count, data.Length - position);
                for (int i = 0; i < available; i++)
                {
                    buffer[offset + i] = data[position + i] * volume;
                }

                position += available;
                return available;
            }
        }
    }
}
