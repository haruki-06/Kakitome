using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Kakitome.Application.Asr;
using Kakitome.Storage.Audio;

namespace Kakitome.Bench;

internal sealed record ItemResult(string Id, string Category, string Language, string Noise, double AudioSeconds, double ProcessingSeconds,
    int Edits, int ReferenceLength, string Reference, string Hypothesis);

internal sealed record CategoryResult(string Category, string Noise, double ErrorRate, int Edits, int ReferenceLength);

internal sealed record RunResult(
    string ModelId,
    string Engine,
    string Accelerator,
    int Threads,
    HardwareInfo Hardware,
    DateTimeOffset StartedAt,
    double LoadSeconds,
    double TotalAudioSeconds,
    double TotalProcessingSeconds,
    double RealTimeFactor,
    long PeakWorkingSetBytes,
    List<CategoryResult> Categories,
    List<ItemResult> Items)
{
    /// <summary>Japanese CER over clean + noisy items (headline metric).</summary>
    public double JapaneseCer(string? noise = null) => Rate(Items.Where(i => i.Language == "ja" && (noise is null || i.Noise == noise)));

    public double EnglishWer(string? noise = null) => Rate(Items.Where(i => i.Language == "en" && (noise is null || i.Noise == noise)));

    private static double Rate(IEnumerable<ItemResult> items)
    {
        var list = items.ToList();
        var reference = list.Sum(i => i.ReferenceLength);
        return reference == 0 ? double.NaN : (double)list.Sum(i => i.Edits) / reference;
    }
}

internal sealed record HardwareInfo(string Cpu, int LogicalCores, double RamGb, List<string> Gpus, string Os, bool OnAcPower);

/// <summary>Runs one model over the corpus in this process (the CLI runs each model in its own process).</summary>
internal static class BenchmarkRunner
{
    public static async Task<RunResult> RunAsync(IFinalAsrProvider provider, string modelId, string corpusDir, int threads, bool preferEfficiency)
    {
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(await File.ReadAllTextAsync(Path.Combine(corpusDir, "manifest.json")), CorpusBuilder.Json)
            ?? throw new InvalidDataException("Empty manifest.");
        var started = DateTimeOffset.Now;
        var hardware = Hardware(); // fail fast, before spending minutes on inference

        var load = Stopwatch.StartNew();
        var session = await provider.OpenAsync(new AsrSessionOptions(modelId, null, threads, preferEfficiency));
        load.Stop();

        var items = new List<ItemResult>();
        await using (session)
        {
            // Warm-up (first inference allocates buffers / compiles shaders) is excluded from timing.
            var first = manifest.Entries[0];
            await TranscribeFileAsync(session, Path.Combine(corpusDir, first.File), first.Language);

            foreach (var entry in manifest.Entries)
            {
                var watch = Stopwatch.StartNew();
                var hypothesis = await TranscribeFileAsync(session, Path.Combine(corpusDir, entry.File), entry.Language);
                watch.Stop();
                var rate = entry.Language == "ja" ? AsrMetrics.Cer(entry.Text, hypothesis) : AsrMetrics.Wer(entry.Text, hypothesis);
                items.Add(new ItemResult(entry.Id, entry.Category, entry.Language, entry.Noise, entry.DurationSeconds, watch.Elapsed.TotalSeconds,
                    rate.Edits, rate.ReferenceLength, entry.Text, hypothesis));
                Console.WriteLine($"{modelId,-28} {entry.Id,-14} {entry.Noise,-8} {rate.Rate,6:P1}  {watch.Elapsed.TotalSeconds / entry.DurationSeconds,5:F2}x");
            }

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var categories = items
                .GroupBy(i => (i.Category, i.Noise))
                .Select(g => new CategoryResult(g.Key.Category, g.Key.Noise,
                    g.Sum(i => i.ReferenceLength) == 0 ? 0 : (double)g.Sum(i => i.Edits) / g.Sum(i => i.ReferenceLength),
                    g.Sum(i => i.Edits), g.Sum(i => i.ReferenceLength)))
                .OrderBy(c => c.Category).ThenBy(c => c.Noise)
                .ToList();
            var audio = items.Sum(i => i.AudioSeconds);
            var processing = items.Sum(i => i.ProcessingSeconds);
            return new RunResult(modelId, session.EngineDescription, preferEfficiency ? "cpu" : "auto", threads, hardware, started,
                load.Elapsed.TotalSeconds, audio, processing, processing / audio, process.PeakWorkingSet64, categories, items);
        }
    }

    /// <summary>The product path: WAV reader → 16 kHz resampler → VAD chunker → provider.</summary>
    private static async Task<string> TranscribeFileAsync(IAsrSession session, string path, string language)
    {
        _ = language; // language is fixed per session in the product; kept for future per-item hints
        var text = new StringBuilder();
        using var reader = new WavSampleReader(path);
        string? prompt = null;
        foreach (var chunk in SpeechChunker.Split(reader))
        {
            var result = await session.TranscribeAsync(chunk.Samples, prompt);
            foreach (var segment in result.Segments)
            {
                text.Append(segment.Text);
                text.Append(language == "en" ? " " : string.Empty);
            }

            prompt = text.Length > 200 ? text.ToString(text.Length - 200, 200) : text.ToString();
        }

        return text.ToString().Trim();
    }

    public static HardwareInfo Hardware()
    {
        using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpu = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        var gpus = new List<string>();
        using (var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
        {
            foreach (var name in classKey?.GetSubKeyNames() ?? [])
            {
                // Some subkeys (e.g. "Properties") are access-protected for standard users; skip them.
                try
                {
                    using var adapter = classKey!.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is string desc && !gpus.Contains(desc))
                    {
                        gpus.Add(desc);
                    }
                }
                catch (System.Security.SecurityException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        var power = new Kakitome.Infrastructure.Power.WindowsSystemResourceProbe();
        var onAc = power.Current.OnAcPower;
        power.Dispose();
        return new HardwareInfo(cpu, Environment.ProcessorCount, Math.Round(ram, 1), gpus, Environment.OSVersion.VersionString, onAc);
    }
}
