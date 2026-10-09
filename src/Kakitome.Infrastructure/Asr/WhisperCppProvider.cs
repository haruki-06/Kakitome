using Kakitome.Application.Asr;
using Kakitome.Application.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Kakitome.Infrastructure.Asr;

/// <summary>
/// whisper.cpp through Whisper.net. Native runtime order: Vulkan → CPU (AVX) → CPU (no AVX). The GPU is used only
/// when the platform probe found a capable (discrete) GPU — integrated graphics stay on the CPU (ADR-035) — and not
/// when the scheduler asks for efficiency (on battery / Energy Saver).
/// </summary>
public sealed class WhisperCppProvider : IFinalAsrProvider
{
    public const string ProviderId = "whisper.cpp";

    /// <summary>Segments whisper itself flags as probably not speech are dropped (classic silence hallucinations).</summary>
    private const float NoSpeechThreshold = 0.6f;

    private static readonly Lock RuntimeLock = new();
    private static bool _runtimeConfigured;

    private readonly IModelStore _models;
    private readonly IAccelerationProbe? _acceleration;

    /// <param name="acceleration">The platform probe; null (benchmarks, tests) lets whisper.cpp use any Vulkan GPU.</param>
    public WhisperCppProvider(IModelStore models, IAccelerationProbe? acceleration = null)
    {
        _models = models;
        _acceleration = acceleration;
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
            throw new ArgumentException($"'{options.ModelId}' is not a whisper.cpp model.", nameof(options));
        }

        if (_models.GetState(model.Id) != ModelState.Installed)
        {
            throw new AsrModelMissingException($"{model.DisplayName} is not installed.");
        }

        ConfigureRuntime();
        var path = Path.Combine(_models.GetDirectory(model.Id), model.Files[0].Name);
        var factory = await Task.Run(
            () => WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = UsesGpu(options) }),
            cancellationToken).ConfigureAwait(false);
        return new Session(factory, model, options, UsesGpu(options));
    }

    private bool UsesGpu(AsrSessionOptions options) => !options.PreferEfficiency && (_acceleration?.HasCapableGpu ?? true);

    private static void ConfigureRuntime()
    {
        lock (RuntimeLock)
        {
            if (_runtimeConfigured)
            {
                return;
            }

            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            _runtimeConfigured = true;
        }
    }

    private sealed class Session(WhisperFactory factory, ModelDescriptor model, AsrSessionOptions options, bool useGpu) : IAsrSession
    {
        public string EngineDescription =>
            $"whisper.cpp (Whisper.net 1.9.1, {RuntimeOptions.LoadedLibrary?.ToString() ?? "native"}{(useGpu ? string.Empty : ", CPU")}{(options.BeamSize > 1 && !options.ShortUtterances ? $", beam {options.BeamSize}" : string.Empty)}) / {model.Id}";

        public async Task<AsrChunkResult> TranscribeAsync(ReadOnlyMemory<float> samples, string? prompt, CancellationToken cancellationToken = default)
        {
            var builder = factory.CreateBuilder()
                .WithThreads(Math.Max(1, options.Threads))
                .WithProbabilities()
                .WithNoSpeechThreshold(NoSpeechThreshold);
            builder = options.Language is { Length: > 0 } lang ? builder.WithLanguage(Normalize(lang)) : builder.WithLanguageDetection();
            if (options.BeamSize > 1 && !options.ShortUtterances)
            {
                builder = builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(options.BeamSize));
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                builder = builder.WithPrompt(prompt);
            }

            if (options.ShortUtterances)
            {
                // The encoder normally always processes a 30 s window (1500 frames); size it to the utterance instead.
                var frames = (int)Math.Ceiling(samples.Length / (double)Kakitome.Application.Asr.SpeechChunker.SampleRate * 50) + 64;
                builder = builder.WithAudioContextSize(Math.Min(1500, frames)).WithSingleSegment().WithNoContext();
            }

            var segments = new List<AsrSegment>();
            string? language = null;
            var processor = builder.Build();
            await using (processor.ConfigureAwait(false))
            {
                await foreach (var s in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    language ??= s.Language;
                    if (s.NoSpeechProbability > NoSpeechThreshold && s.Probability < 0.5f)
                    {
                        continue;
                    }

                    segments.Add(new AsrSegment(s.Start.TotalSeconds, s.End.TotalSeconds, s.Text, s.Probability));
                }
            }

            return new AsrChunkResult(segments, language);
        }

        public ValueTask DisposeAsync()
        {
            factory.Dispose();
            return ValueTask.CompletedTask;
        }

        private static string Normalize(string language) => language.Split('-', 2)[0].ToLowerInvariant();
    }
}
