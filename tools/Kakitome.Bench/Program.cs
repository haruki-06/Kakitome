using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Asr;
using Kakitome.Application.Models;
using Kakitome.Bench;
using Kakitome.Infrastructure.Asr;
using Kakitome.Storage;
using Kakitome.Storage.Models;

// Kakitome ASR benchmark harness (docs/08_BENCHMARKS.md).
//   corpus  [--spec benchmarks/corpus-v1.json] [--out benchmarks/corpus/v1]
//   install --model <id>                       (downloads + verifies a catalog model into the app's model folder)
//   run     --models <id,id> [--corpus dir] [--out docs/benchmarks/asr-v1] [--threads n] [--cpu-only]
//   report  [--out docs/benchmarks/asr-v1]
var command = args.FirstOrDefault() ?? "help";
var options = ParseOptions(args.Skip(1).ToArray());
var root = FindRepoRoot();
var corpusDir = Path.GetFullPath(options.GetValueOrDefault("corpus", Path.Combine(root, "benchmarks", "corpus", "v1")));
var outDir = Path.GetFullPath(options.GetValueOrDefault("out", Path.Combine(root, "docs", "benchmarks", "asr-v1")));
var threads = int.Parse(options.GetValueOrDefault("threads", Math.Clamp(Environment.ProcessorCount - 2, 1, 8).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
var cpuOnly = options.ContainsKey("cpu-only");
var store = new ModelStore(AppDataPaths.Default, new SharedHttpClientFactory(), NullLogger<ModelStore>.Instance);

switch (command)
{
    case "corpus":
        await CorpusBuilder.BuildAsync(options.GetValueOrDefault("spec", Path.Combine(root, "benchmarks", "corpus-v1.json")), corpusDir);
        return 0;

    case "install":
    {
        var id = options["model"];
        var last = -1;
        await store.InstallAsync(id, new Progress<double>(p =>
        {
            var pct = (int)(p * 100);
            if (pct != last)
            {
                last = pct;
                Console.Write($"\r{id}: {pct}%   ");
            }
        }));
        Console.WriteLine($"\n{id}: installed and verified in {store.GetDirectory(id)}");
        return 0;
    }

    case "run":
        // One process per model/accelerator so peak memory and native runtime state are isolated.
        foreach (var model in options["models"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var efficient in cpuOnly ? [true] : new[] { false, true })
            {
                if (efficient && !IsWhisper(model) && !cpuOnly)
                {
                    continue; // sherpa-onnx runs on CPU either way
                }

                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                foreach (var a in new[] { "run-one", "--model", model, "--corpus", corpusDir, "--out", outDir, "--threads", threads.ToString(CultureInfo.InvariantCulture) })
                {
                    psi.ArgumentList.Add(a);
                }

                if (efficient)
                {
                    psi.ArgumentList.Add("--cpu-only");
                }

                using var child = Process.Start(psi)!;
                await child.WaitForExitAsync();
                if (child.ExitCode != 0)
                {
                    Console.Error.WriteLine($"{model}: run failed ({child.ExitCode})");
                }
            }
        }

        await WriteReportAsync(outDir);
        return 0;

    case "run-one":
    {
        var model = options["model"];
        var descriptor = ModelCatalog.Find(model) ?? throw new ArgumentException($"Unknown model {model}");
        IFinalAsrProvider provider = descriptor.ProviderId == WhisperCppProvider.ProviderId ? new WhisperCppProvider(store) : new SherpaOnnxProvider(store);
        var result = await BenchmarkRunner.RunAsync(provider, model, corpusDir, threads, cpuOnly);
        Directory.CreateDirectory(Path.Combine(outDir, "raw"));
        var file = Path.Combine(outDir, "raw", $"{model}.{(cpuOnly ? "cpu" : "auto")}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, CorpusBuilder.Json));
        Console.WriteLine($"→ {file}");
        return 0;
    }

    case "debug-one":
    {
        // Prints each chunk's segments for one file (diagnostics).
        var model = options["model"];
        var descriptor = ModelCatalog.Find(model)!;
        IFinalAsrProvider provider = descriptor.ProviderId == WhisperCppProvider.ProviderId ? new WhisperCppProvider(store) : new SherpaOnnxProvider(store);
        await using var session = await provider.OpenAsync(new AsrSessionOptions(model, "ja", threads, cpuOnly));
        Console.WriteLine(session.EngineDescription);
        using var reader = new Kakitome.Storage.Audio.WavSampleReader(options["file"]);
        Console.WriteLine($"format {reader.Format} frames {reader.TotalFrames}");
        foreach (var chunk in SpeechChunker.Split(reader))
        {
            var r = await session.TranscribeAsync(chunk.Samples, null);
            Console.WriteLine($"chunk {chunk.Index} @{chunk.StartSeconds:F2}s len {chunk.DurationSeconds:F2}s");
            foreach (var s in r.Segments)
            {
                Console.WriteLine($"  [{s.StartSeconds:F2}-{s.EndSeconds:F2}] {s.Text}");
            }
        }

        return 0;
    }

    case "lecture":
    {
        // Transcribes a real recording with prompt/decoding variants (ADR-030). The audio and its text stay local.
        //   lecture --file <audio> --model <id> [--beam n] [--glossary file] [--no-context] [--seed text] [--minutes m] --out <file.txt>
        var model = options["model"];
        var descriptor = ModelCatalog.Find(model)!;
        IFinalAsrProvider provider = descriptor.ProviderId == WhisperCppProvider.ProviderId ? new WhisperCppProvider(store) : new SherpaOnnxProvider(store);
        var beam = int.Parse(options.GetValueOrDefault("beam", "0"), CultureInfo.InvariantCulture);
        var glossary = options.TryGetValue("glossary", out var gpath) ? Kakitome.Domain.Transcripts.Glossary.Parse(await File.ReadAllTextAsync(gpath)) : Kakitome.Domain.Transcripts.Glossary.Empty;
        var useContext = !options.ContainsKey("no-context");
        var seed = options.GetValueOrDefault("seed");
        var maxSeconds = double.Parse(options.GetValueOrDefault("minutes", "1000"), CultureInfo.InvariantCulture) * 60;
        await using var session = await provider.OpenAsync(new AsrSessionOptions(model, "ja", threads, cpuOnly, BeamSize: beam));
        var prompts = new AsrPromptBuilder(glossary, "ja");
        var output = new StringBuilder();
        var recent = new StringBuilder();
        var watch = Stopwatch.StartNew();
        double audio = 0;
        using var reader = new Kakitome.Infrastructure.Audio.MediaAudioSampleReaderFactory().Open(options["file"]);
        foreach (var chunk in SpeechChunker.Split(reader))
        {
            if (chunk.StartSeconds >= maxSeconds)
            {
                break;
            }

            var prompt = prompts.Next(useContext ? recent.ToString() : null);
            if (seed is not null && (prompt is null || recent.Length == 0))
            {
                prompt = seed + prompt;
            }

            var r = await session.TranscribeAsync(chunk.Samples, prompt);
            var text = string.Concat(r.Segments.Select(x => x.Text.Trim()));
            prompts.Observe(glossary.Apply(text));
            recent.Append(text);
            if (recent.Length > 400)
            {
                recent.Remove(0, recent.Length - 400);
            }

            audio = chunk.StartSeconds + chunk.DurationSeconds;
            output.AppendLine(CultureInfo.InvariantCulture, $"[{TimeSpan.FromSeconds(chunk.StartSeconds):hh\\:mm\\:ss}] {text}");
        }

        var elapsed = watch.Elapsed.TotalSeconds;
        var header = $"# {session.EngineDescription} beam={beam} glossary={glossary.Terms.Count} context={useContext} seed={seed} audio={audio:F0}s elapsed={elapsed:F1}s rtf={elapsed / Math.Max(1, audio):F3}";
        output.Insert(0, header + "\n");
        await File.WriteAllTextAsync(options["out"], output.ToString(), Encoding.UTF8);
        Console.WriteLine(header);
        return 0;
    }

    case "debug-sherpa":
    {
        // Raw sherpa-onnx tokens for a time range of a file (diagnostics).
        var dir = store.GetDirectory(ModelCatalog.ReazonSpeechK2V2Int8);
        var config = new SherpaOnnx.OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(dir, "encoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(dir, "decoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(dir, "joiner-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(dir, "tokens.txt");
        config.ModelConfig.NumThreads = threads;
        config.DecodingMethod = "greedy_search";
        using var recognizer = new SherpaOnnx.OfflineRecognizer(config);
        using var reader = new Kakitome.Storage.Audio.WavSampleReader(options["file"]);
        var all = new float[reader.TotalFrames];
        reader.Read(all);
        var from = (int)(double.Parse(options.GetValueOrDefault("from", "0"), CultureInfo.InvariantCulture) * 16000);
        var to = (int)(double.Parse(options.GetValueOrDefault("to", "999"), CultureInfo.InvariantCulture) * 16000);
        var slice = all[from..Math.Min(all.Length, to)];
        var report = new StringBuilder();
        foreach (var (us, ul) in SherpaOnnxProvider.Session.Utterances(slice))
        {
            using var u = recognizer.CreateStream();
            u.AcceptWaveform(16000, slice[us..(us + ul)]);
            recognizer.Decode(u);
            report.AppendLine(CultureInfo.InvariantCulture, $"utt {us / 16000.0:F2}-{(us + ul) / 16000.0:F2}: {u.Result.Text}");
        }

        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, slice);
        recognizer.Decode(stream);
        var r = stream.Result;
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "sherpa-debug.txt"),
            report + $"text: {r.Text}\n" + string.Join("\n", r.Tokens.Select((t, i) => $"{r.Timestamps[i]:F2} {t}")), Encoding.UTF8);
        return 0;
    }

    case "glossary-draft":
        return await GlossaryBenchmark.DraftAsync(store, options);

    case "glossary-score":
        return await GlossaryBenchmark.ScoreAsync(options);

    case "summary":
        await SummaryBenchmark.RunAsync(root, store, includeCpu: options.ContainsKey("cpu"));
        return 0;

    case "summary-file":
        await SummaryBenchmark.ProbeFileAsync(store, args[1]);
        return 0;

    case "report":
        await WriteReportAsync(outDir);
        return 0;

    case "hardware":
    {
        // What the app's platform probe sees (GPUs via Vulkan; the one local models use; memory).
        var probe = new GpuAccelerationProbe();
        foreach (var d in probe.Devices)
        {
            Console.WriteLine($"GPU {d.Index}: {d.Name} ({d.Type}, {d.MemoryBytes / (1024 * 1024)} MB)");
        }

        Console.WriteLine($"Used for local models: {probe.Gpu?.Name ?? "none (CPU)"}; GGML_VK_VISIBLE_DEVICES={Environment.GetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES") ?? "(unset)"}");
        Console.WriteLine($"Memory: {probe.TotalMemoryBytes / (1024 * 1024)} MB");
        return 0;
    }

    default:
        Console.WriteLine("usage: Kakitome.Bench corpus | install --model <id> | run --models <ids> | report | summary [--cpu] | hardware");
        return 1;
}

static bool IsWhisper(string model) => ModelCatalog.Find(model)?.ProviderId == WhisperCppProvider.ProviderId;

static async Task WriteReportAsync(string outDir)
{
    var rawDir = Path.Combine(outDir, "raw");
    if (!Directory.Exists(rawDir))
    {
        return;
    }

    var runs = new List<RunResult>();
    foreach (var file in Directory.GetFiles(rawDir, "*.json").Order(StringComparer.Ordinal))
    {
        runs.Add(JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(file), CorpusBuilder.Json)!);
    }

    var md = new StringBuilder();
    md.AppendLine("# ASR benchmark — corpus v1 (synthetic)").AppendLine();
    if (runs.Count > 0)
    {
        var hw = runs[0].Hardware;
        md.AppendLine(CultureInfo.InvariantCulture, $"- Hardware: {hw.Cpu}, {hw.LogicalCores} logical cores, {hw.RamGb} GB RAM; GPU: {string.Join(" / ", hw.Gpus)}");
        md.AppendLine(CultureInfo.InvariantCulture, $"- OS: {hw.Os}; power: {(hw.OnAcPower ? "AC" : "battery")}; run: {runs.Min(r => r.StartedAt):yyyy-MM-dd}");
        md.AppendLine("- Corpus: `benchmarks/corpus-v1.json`, Windows OneCore voices, noise variants clean / 15 dB / 5 dB SNR. Japanese scored by CER, English by WER (normalized; see `AsrMetrics`).");
        md.AppendLine("- RTF = processing time / audio time through the product path (reader → 16 kHz → VAD chunks → engine); lower is faster. Peak RAM is the benchmark process' peak working set.").AppendLine();
    }

    md.AppendLine("| Model | Accel. | ja CER clean | ja CER 15 dB | ja CER 5 dB | en WER clean | RTF | Peak RAM | Load |");
    md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var r in runs.OrderBy(r => r.JapaneseCer("clean")))
    {
        md.AppendLine(CultureInfo.InvariantCulture,
            $"| {r.ModelId} | {r.Accelerator} | {Pct(r.JapaneseCer("clean"))} | {Pct(r.JapaneseCer("noise15"))} | {Pct(r.JapaneseCer("noise5"))} | {Pct(r.EnglishWer("clean"))} | {r.RealTimeFactor:F3} | {r.PeakWorkingSetBytes / (1024.0 * 1024):F0} MB | {r.LoadSeconds:F1} s |");
    }

    md.AppendLine().AppendLine("## ja CER by category (clean)").AppendLine();
    var categories = runs.SelectMany(r => r.Categories.Where(c => c.Noise == "clean").Select(c => c.Category)).Distinct().Order().ToList();
    md.AppendLine("| Model | Accel. | " + string.Join(" | ", categories) + " |");
    md.AppendLine("|---|---|" + string.Concat(Enumerable.Repeat("---:|", categories.Count)));
    foreach (var r in runs)
    {
        md.Append(CultureInfo.InvariantCulture, $"| {r.ModelId} | {r.Accelerator} |");
        foreach (var c in categories)
        {
            var cat = r.Categories.FirstOrDefault(x => x.Category == c && x.Noise == "clean");
            md.Append(' ').Append(cat is null ? "–" : Pct(cat.ErrorRate)).Append(" |");
        }

        md.AppendLine();
    }

    md.AppendLine().AppendLine("Engines: " + string.Join("; ", runs.Select(r => $"`{r.Engine}`").Distinct()));
    await File.WriteAllTextAsync(Path.Combine(outDir, "README.md"), md.ToString());
    Console.WriteLine(md.ToString());

    static string Pct(double v) => double.IsNaN(v) ? "–" : v.ToString("P1", CultureInfo.InvariantCulture);
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? args[++i] : "true";
    }

    return result;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kakitome.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
