using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Asr;
using Kakitome.Application.Live;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Infrastructure.Asr;
using Kakitome.Storage;
using Kakitome.Storage.Audio;
using Kakitome.Storage.Models;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Asr;

/// <summary>
/// Live transcript with real local models on the synthetic corpus: the preview must be readable (bounded CER) and
/// decode faster than real time on this machine. Skipped when a model or the corpus is missing.
/// </summary>
[Trait("Category", "Model")]
public sealed class RealLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ModelCatalog.ReazonSpeechK2V2Int8, "lecture-01", 0.25)]
    [InlineData(ModelCatalog.WhisperSmallQ5, "lecture-01", 0.30)]
    [InlineData(ModelCatalog.WhisperSmallQ5, "english-01", 0.30)]
    public async Task Live_preview_is_readable_and_faster_than_real_time(string modelId, string corpusId, double maxCer)
    {
        var store = new ModelStore(AppDataPaths.Default, new SharedHttpClientFactory(), NullLogger<ModelStore>.Instance);
        Assert.SkipUnless(store.GetState(modelId) == ModelState.Installed, $"{modelId} is not installed on this machine.");
        var corpus = Path.Combine(RepositoryPaths.Root, "benchmarks", "corpus", "v1");
        Assert.SkipUnless(File.Exists(Path.Combine(corpus, "manifest.json")), "Benchmark corpus not generated (Kakitome.Bench corpus).");
        var entry = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "manifest.json"))).RootElement.GetProperty("entries")
            .EnumerateArray().First(e => e.GetProperty("id").GetString() == corpusId && e.GetProperty("noise").GetString() == "clean");
        var reference = entry.GetProperty("text").GetString()!;
        var language = entry.GetProperty("language").GetString();

        IFinalAsrProvider provider = ModelCatalog.Find(modelId)!.ProviderId == SherpaOnnxProvider.ProviderId
            ? new SherpaOnnxProvider(store)
            : new WhisperCppProvider(store);
        await using var session = await provider.OpenAsync(new AsrSessionOptions(modelId, language, Threads: 2, PreferEfficiency: true, ShortUtterances: true));
        using var decoder = new TimedDecoder(new AsrSessionLiveDecoder(session));

        using var reader = new WavSampleReaderFactory().Open(Path.Combine(corpus, entry.GetProperty("file").GetString()!));
        var format = new AudioFormat(reader.Format.SampleRate, reader.Format.Channels);
        var lines = new ConcurrentDictionary<long, LiveSegment>();
        long id = 0;
        var clock = Stopwatch.StartNew();
        await using (var live = new LiveTranscriber(format, CaptureSourceKind.Microphone, decoder, () => Interlocked.Increment(ref id), maxBacklogSeconds: 3600))
        {
            live.SegmentChanged += (_, s) => lines[s.Id] = s;
            var buffer = new float[(format.SampleRate / 100) * format.Channels];
            int frames;
            while ((frames = reader.Read(buffer)) > 0)
            {
                live.OnSamples(MemoryMarshal.AsBytes(buffer.AsSpan(0, frames * format.Channels)));
            }

            await live.CompleteAsync();
        }

        var audioSeconds = reader.TotalFrames / (double)format.SampleRate;
        var text = string.Join(language == "en" ? " " : string.Empty, lines.Values.Where(l => l.IsFinal).OrderBy(l => l.StartSeconds).Select(l => l.Text));
        var cer = AsrMetrics.Cer(reference, text);
        var rtf = decoder.Busy.TotalSeconds / audioSeconds;
        output.WriteLine($"{modelId} {corpusId}: CER {cer.Rate:P1}, decode RTF {rtf:F2}, {decoder.Calls} decodes, wall {clock.Elapsed.TotalSeconds:F1}s for {audioSeconds:F0}s audio");
        output.WriteLine(text);

        Assert.True(cer.Rate <= maxCer, $"CER {cer.Rate:P1} > {maxCer:P0}: {text}");
        Assert.True(rtf < 1.0, $"Live decoding is slower than real time (RTF {rtf:F2}).");
    }

    private sealed class TimedDecoder(ILiveDecoder inner) : ILiveDecoder, IDisposable
    {
        private long _ticks;
        private int _calls;

        public TimeSpan Busy => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public int Calls => _calls;

        public async Task<string> DecodeAsync(float[] samples16k, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await inner.DecodeAsync(samples16k, cancellationToken);
            }
            finally
            {
                Interlocked.Add(ref _ticks, Stopwatch.GetElapsedTime(started).Ticks);
                Interlocked.Increment(ref _calls);
            }
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
