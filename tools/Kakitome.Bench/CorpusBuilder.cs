using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Kakitome.Application.Recording;
using Kakitome.Storage.Audio;
using Windows.Media.SpeechSynthesis;

namespace Kakitome.Bench;

internal sealed record CorpusSpec(string Schema, int Version, string Description, List<NoiseSpec> Noise, List<CorpusItemSpec> Items);

internal sealed record NoiseSpec(string Suffix, double? SnrDb);

internal sealed record CorpusItemSpec(string Id, string Category, string Language, string Text);

/// <summary>One audio file of the generated corpus.</summary>
internal sealed record CorpusEntry(string Id, string Category, string Language, string Voice, string Noise, string File, string Text, double DurationSeconds);

internal sealed record CorpusManifest(int Version, string CreatedWith, List<CorpusEntry> Entries);

/// <summary>Synthesizes the benchmark corpus with installed Windows voices and mixes deterministic noise variants.</summary>
internal static class CorpusBuilder
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<CorpusManifest> BuildAsync(string specPath, string outDir)
    {
        var spec = JsonSerializer.Deserialize<CorpusSpec>(await File.ReadAllTextAsync(specPath), Json)
            ?? throw new InvalidDataException("Empty corpus spec.");
        Directory.CreateDirectory(outDir);

        var jaVoices = SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.DisplayName).ToList();
        var enVoices = SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.DisplayName).ToList();
        if (jaVoices.Count == 0)
        {
            throw new InvalidOperationException("No Japanese Windows voice is installed (Settings › Time & language › Speech).");
        }

        var entries = new List<CorpusEntry>();
        using var synth = new SpeechSynthesizer();
        for (var i = 0; i < spec.Items.Count; i++)
        {
            var item = spec.Items[i];
            var voices = item.Language.StartsWith("ja", StringComparison.Ordinal) ? jaVoices : enVoices;

            var cleanPath = Path.Combine(outDir, $"{item.Id}.tts.wav");
            string voiceName;
            if (voices.Count > 0)
            {
                var voice = voices[i % voices.Count];
                synth.Voice = voice;
                using var stream = await synth.SynthesizeTextToStreamAsync(item.Text);
                var bytes = new byte[stream.Size];
                await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
                await File.WriteAllBytesAsync(cleanPath, bytes);
                voiceName = voice.DisplayName.Replace("Microsoft ", string.Empty, StringComparison.Ordinal);
            }
            else if (SapiVoice(item.Language) is { } sapi)
            {
                // OneCore may expose no voice for a language that legacy SAPI has (e.g. "Zira Desktop").
                using var legacy = new System.Speech.Synthesis.SpeechSynthesizer();
                legacy.SelectVoice(sapi);
                legacy.SetOutputToWaveFile(cleanPath, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16_000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                legacy.Speak(item.Text);
                legacy.SetOutputToNull();
                voiceName = sapi.Replace("Microsoft ", string.Empty, StringComparison.Ordinal);
            }
            else
            {
                Console.WriteLine($"skip {item.Id}: no {item.Language} voice");
                continue;
            }

            float[] samples;
            AudioFormat format;
            using (var reader = new WavSampleReader(cleanPath))
            {
                format = reader.Format;
                samples = new float[reader.TotalFrames * format.Channels];
                var read = 0;
                int n;
                while ((n = reader.Read(samples.AsSpan(read))) > 0)
                {
                    read += n * format.Channels;
                }
            }

            File.Delete(cleanPath);
            foreach (var noise in spec.Noise)
            {
                var mixed = noise.SnrDb is { } snr ? AddNoise(samples, snr, seed: 1000 + i) : samples;
                var file = $"{item.Id}.{noise.Suffix}.wav";
                using (var writer = new WavFileWriter(Path.Combine(outDir, file), format))
                {
                    writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(mixed.AsSpan()));
                    writer.Complete();
                }

                entries.Add(new CorpusEntry(item.Id, item.Category, item.Language, voiceName, noise.Suffix, file, item.Text,
                    Math.Round(samples.Length / (double)format.Channels / format.SampleRate, 2)));
            }

            Console.WriteLine($"{item.Id,-14} {voiceName,-10} {entries[^1].DurationSeconds,6:F1}s");
        }

        var manifest = new CorpusManifest(spec.Version, "Windows OneCore TTS + Kakitome.Bench", entries);
        await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        return manifest;
    }

    private static string? SapiVoice(string language)
    {
        using var legacy = new System.Speech.Synthesis.SpeechSynthesizer();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language == "en" ? "en-US" : language);
        return legacy.GetInstalledVoices(culture).Select(v => v.VoiceInfo).FirstOrDefault()?.Name;
    }

    /// <summary>Pink-ish noise (one-pole filtered white noise) scaled to the requested SNR over the whole clip.</summary>
    internal static float[] AddNoise(float[] clean, double snrDb, int seed)
    {
        var rng = new Random(seed);
        var noise = new float[clean.Length];
        float state = 0;
        for (var i = 0; i < noise.Length; i++)
        {
            state = (0.97f * state) + (0.03f * (float)((rng.NextDouble() * 2) - 1));
            noise[i] = state;
        }

        static double Rms(float[] x) => Math.Sqrt(x.Select(v => (double)v * v).Average());
        var gain = Rms(clean) / (Rms(noise) * Math.Pow(10, snrDb / 20));
        var mixed = new float[clean.Length];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = Math.Clamp(clean[i] + (float)(noise[i] * gain), -1f, 1f);
        }

        return mixed;
    }
}
