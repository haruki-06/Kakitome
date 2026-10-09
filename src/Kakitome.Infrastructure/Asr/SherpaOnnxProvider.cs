using System.Text;
using SherpaOnnx;
using Kakitome.Application.Asr;
using Kakitome.Application.Models;

namespace Kakitome.Infrastructure.Asr;

/// <summary>
/// sherpa-onnx offline transducer (e.g. ReazonSpeech k2 v2 for Japanese) on ONNX Runtime CPU. Token timestamps are
/// grouped into readable segments at pauses or sentence ends.
/// </summary>
public sealed class SherpaOnnxProvider : IFinalAsrProvider
{
    public const string ProviderId = "sherpa-onnx";

    /// <summary>A pause longer than this between tokens starts a new segment.</summary>
    private const float SegmentGapSeconds = 0.6f;

    private const int MaxSegmentChars = 80;

    private readonly IModelStore _models;

    public SherpaOnnxProvider(IModelStore models)
    {
        _models = models;
        Models = ModelCatalog.All
            .Where(m => m.ProviderId == ProviderId)
            .Select(m => new AsrModelInfo(m.Id, m.DisplayName, m.Languages, m.ApproximateRamBytes, m.License))
            .ToList();
    }

    public string Id => ProviderId;

    public IReadOnlyList<AsrModelInfo> Models { get; }

    public async Task<IAsrSession> OpenAsync(AsrSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var model = ModelCatalog.Find(options.ModelId);
        if (model is null || model.ProviderId != ProviderId)
        {
            throw new ArgumentException($"'{options.ModelId}' is not a sherpa-onnx model.", nameof(options));
        }

        if (_models.GetState(model.Id) != ModelState.Installed)
        {
            throw new AsrModelMissingException($"{model.DisplayName} is not installed.");
        }

        var dir = _models.GetDirectory(model.Id);
        string File(string prefix) => Path.Combine(dir, model.Files.First(f => f.Name.StartsWith(prefix, StringComparison.Ordinal)).Name);

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SpeechChunker.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = File("encoder");
        config.ModelConfig.Transducer.Decoder = File("decoder");
        config.ModelConfig.Transducer.Joiner = File("joiner");
        config.ModelConfig.Tokens = File("tokens");
        config.ModelConfig.NumThreads = Math.Max(1, options.Threads);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;

        SherpaNative.EnsureLoaded();
        var recognizer = await Task.Run(() => new OfflineRecognizer(config), cancellationToken).ConfigureAwait(false);
        return new Session(recognizer, model);
    }

    internal sealed class Session(OfflineRecognizer recognizer, ModelDescriptor model) : IAsrSession
    {
        public string EngineDescription => $"sherpa-onnx 1.13.8 (ONNX Runtime, CPU) / {model.Id}";

        public Task<AsrChunkResult> TranscribeAsync(ReadOnlyMemory<float> samples, string? prompt, CancellationToken cancellationToken = default) =>
            Task.Run(
                () =>
                {
                    // ReazonSpeech is trained on short single utterances: longer input loses earlier speech, and input
                    // that starts mid-word is often mis-decoded. Decode ~2–6 s pause-delimited utterances separately,
                    // each with real context and digital silence around it (measured in the benchmark, ADR-022).
                    var segments = new List<AsrSegment>();
                    foreach (var (start, length) in Utterances(samples.Span))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var stream = recognizer.CreateStream();
                        stream.AcceptWaveform(SpeechChunker.SampleRate, WithSilence(samples.Slice(start, length).Span));
                        recognizer.Decode(stream);
                        var offset = (start / (double)SpeechChunker.SampleRate) - SilenceSeconds;
                        segments.AddRange(Segment(stream.Result, (length / (double)SpeechChunker.SampleRate) + SilenceSeconds)
                            .Select(s => s with
                            {
                                StartSeconds = Math.Max(0, s.StartSeconds + offset),
                                EndSeconds = Math.Max(0, s.EndSeconds + offset),
                            }));
                    }

                    var language = model.Languages.FirstOrDefault(l => l != "*");
                    return new AsrChunkResult(segments, language);
                },
                cancellationToken);

        private const double SilenceSeconds = 0.3;

        internal static float[] WithSilence(ReadOnlySpan<float> audio)
        {
            var silence = (int)(SilenceSeconds * SpeechChunker.SampleRate);
            var padded = new float[audio.Length + (2 * silence)];
            audio.CopyTo(padded.AsSpan(silence));
            return padded;
        }

        /// <summary>Utterances of ~2–6 s cut at the longest pause (always at pauses ≥ 0.6 s), with 200 ms context.</summary>
        internal static List<(int Start, int Length)> Utterances(ReadOnlySpan<float> samples)
        {
            const int frame = SpeechChunker.SampleRate / 50; // 20 ms
            const int minFrames = 2 * 50;                    // utterances of at least ~2 s when possible
            const int maxFrames = 6 * 50;                    // and at most ~6 s (longer input loses earlier speech)
            const int hardPauseFrames = 30;                  // a pause ≥ 0.6 s always ends an utterance
            const int pad = SpeechChunker.SampleRate / 5; // 200 ms of real context around each utterance
            var frames = samples.Length / frame;
            if (frames == 0)
            {
                return samples.Length == 0 ? [] : [(0, samples.Length)];
            }

            var energy = new double[frames];
            for (var f = 0; f < frames; f++)
            {
                double sum = 0;
                foreach (var s in samples.Slice(f * frame, frame))
                {
                    sum += s * s;
                }

                energy[f] = sum / frame;
            }

            // Silence threshold relative to the loud frames (robust to overall level).
            var sorted = energy.Order().ToArray();
            var loud = sorted[(int)(sorted.Length * 0.9)];
            var threshold = Math.Max(loud * 0.0005, 1e-7); // ≈ -33 dB below loud speech: keeps soft onsets

            var speech = energy.Select(e => e > threshold).ToArray();
            var result = new List<(int, int)>();
            var f0 = 0;
            while (f0 < frames)
            {
                // Skip leading silence.
                while (f0 < frames && !speech[f0])
                {
                    f0++;
                }

                if (f0 >= frames)
                {
                    break;
                }

                // Walk forward: stop at a long pause, otherwise cut at the longest pause inside [min, max].
                var cut = -1;
                var bestPause = 0;
                var bestPauseStart = -1;
                var quiet = 0;
                for (var f = f0; f < frames && f - f0 < maxFrames; f++)
                {
                    if (speech[f])
                    {
                        if (quiet > 0 && f - quiet - f0 >= minFrames && quiet > bestPause)
                        {
                            bestPause = quiet;
                            bestPauseStart = f - quiet;
                        }

                        quiet = 0;
                    }
                    else if (++quiet >= hardPauseFrames)
                    {
                        cut = f - quiet + 1;
                        break;
                    }
                }

                if (cut < 0)
                {
                    var reachedEnd = f0 + maxFrames >= frames;
                    cut = reachedEnd ? frames : bestPauseStart > 0 ? bestPauseStart : f0 + maxFrames;
                }

                var start = Math.Max(0, (f0 * frame) - pad);
                var end = Math.Min(samples.Length, (cut * frame) + pad);
                if (cut >= frames)
                {
                    end = samples.Length;
                }

                if (end > start)
                {
                    result.Add((start, end - start));
                }

                f0 = Math.Max(cut, f0 + 1);
            }

            return result;
        }

        public ValueTask DisposeAsync()
        {
            recognizer.Dispose();
            return ValueTask.CompletedTask;
        }

        private static List<AsrSegment> Segment(OfflineRecognizerResult result, double chunkSeconds)
        {
            var tokens = result.Tokens ?? [];
            var times = result.Timestamps ?? [];
            if (tokens.Length == 0 || times.Length != tokens.Length)
            {
                return string.IsNullOrWhiteSpace(result.Text) ? [] : [new AsrSegment(0, chunkSeconds, result.Text.Trim(), null)];
            }

            var segments = new List<AsrSegment>();
            var text = new StringBuilder();
            var start = times[0];
            var last = times[0];
            for (var i = 0; i < tokens.Length; i++)
            {
                var gap = times[i] - last;
                if (text.Length > 0 && (gap > SegmentGapSeconds || text.Length >= MaxSegmentChars))
                {
                    segments.Add(new AsrSegment(start, last + 0.1, text.ToString(), null));
                    text.Clear();
                    start = times[i];
                }

                text.Append(tokens[i]);
                last = times[i];
                if (tokens[i] is "。" or "？" or "！" or "?" or "!")
                {
                    segments.Add(new AsrSegment(start, last + 0.1, text.ToString(), null));
                    text.Clear();
                    start = i + 1 < times.Length ? times[i + 1] : last;
                }
            }

            if (text.Length > 0)
            {
                segments.Add(new AsrSegment(start, Math.Min(chunkSeconds, last + 0.3), text.ToString(), null));
            }

            return segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        }
    }
}
