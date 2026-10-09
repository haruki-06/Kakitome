namespace Kakitome.Application.Asr;

/// <summary>
/// A local final-ASR engine (docs/04). Replaceable capability: the product depends on this interface only; concrete
/// engines and models are chosen by benchmark (ADR-022).
/// </summary>
public interface IFinalAsrProvider
{
    /// <summary>Stable id, e.g. <c>whisper.cpp</c>, <c>sherpa-onnx</c>.</summary>
    string Id { get; }

    /// <summary>Models this provider can run.</summary>
    IReadOnlyList<AsrModelInfo> Models { get; }

    /// <summary>Loads a model and returns a session for transcribing chunks. The caller disposes the session.</summary>
    Task<IAsrSession> OpenAsync(AsrSessionOptions options, CancellationToken cancellationToken = default);
}

/// <summary>A loaded model. Not thread-safe: one chunk at a time.</summary>
public interface IAsrSession : IAsyncDisposable
{
    /// <summary>Engine/model identity for lineage, e.g. <c>whisper.cpp 1.9.1 / large-v3-turbo-q5_0 (Vulkan)</c>.</summary>
    string EngineDescription { get; }

    /// <summary>
    /// Transcribes 16 kHz mono float samples. Segment times are relative to the chunk start.
    /// <paramref name="prompt"/> carries the previous chunk's text for continuity where the engine supports it.
    /// </summary>
    Task<AsrChunkResult> TranscribeAsync(ReadOnlyMemory<float> samples, string? prompt, CancellationToken cancellationToken = default);
}

public sealed record AsrModelInfo(
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> Languages,
    long ApproximateRamBytes,
    string License);

/// <param name="Language">BCP-47 language hint (<c>ja</c>, <c>en</c>) or null for auto-detection.</param>
/// <param name="ShortUtterances">Live preview: inputs are single short utterances, so engines may trade a little
/// accuracy for latency (e.g. Whisper's encoder window sized to the input).</param>
/// <param name="BeamSize">Beam search width for engines that support it; 0 = the engine's default (greedy for Whisper).</param>
public sealed record AsrSessionOptions(string ModelId, string? Language, int Threads, bool PreferEfficiency, bool ShortUtterances = false, int BeamSize = 0);

public sealed record AsrChunkResult(IReadOnlyList<AsrSegment> Segments, string? DetectedLanguage);

public sealed record AsrSegment(double StartSeconds, double EndSeconds, string Text, double? Confidence)
{
    public IReadOnlyList<AsrWord>? Words { get; init; }
}

public sealed record AsrWord(string Text, double StartSeconds, double EndSeconds, double? Confidence);

/// <summary>Resolves which provider/model the <c>asr</c> job uses on this machine.</summary>
public interface IAsrEngineSelector
{
    /// <summary>The provider and model to use for <paramref name="language"/>, or null when none is installed.</summary>
    Task<AsrEngineChoice?> SelectAsync(string? language, CancellationToken cancellationToken = default);
}

public sealed record AsrEngineChoice(IFinalAsrProvider Provider, string ModelId);

/// <summary>No usable speech recognition model is installed; the user installs one in Settings › Models.</summary>
public sealed class AsrModelMissingException : Exception
{
    public AsrModelMissingException()
        : base("No speech recognition model is installed.")
    {
    }

    public AsrModelMissingException(string message)
        : base(message)
    {
    }

    public AsrModelMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
