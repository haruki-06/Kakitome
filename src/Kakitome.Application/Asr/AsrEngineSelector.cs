using Kakitome.Application.Models;
using Kakitome.Application.Settings;

namespace Kakitome.Application.Asr;

/// <summary>
/// Picks the provider/model for a transcription: the user's explicit choice if installed, otherwise the first
/// installed model in the benchmark-derived preference order for the language (ADR-022).
/// </summary>
public sealed class AsrEngineSelector(
    IEnumerable<IFinalAsrProvider> providers,
    IModelStore models,
    ISettingsStore settings,
    IAccelerationProbe acceleration) : IAsrEngineSelector
{
    private readonly Dictionary<string, IFinalAsrProvider> _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public Task<AsrEngineChoice?> SelectAsync(string? language, CancellationToken cancellationToken = default)
    {
        var preferred = settings.Current.Processing.AsrModelId;
        var defaults = AsrDefaults.PreferenceFor(language, acceleration.HasCapableGpu);
        var candidates = preferred is null ? defaults : [preferred, .. defaults];
        foreach (var modelId in candidates)
        {
            var descriptor = ModelCatalog.Find(modelId);
            if (descriptor is null
                || !_providers.TryGetValue(descriptor.ProviderId, out var provider)
                || !SupportsLanguage(descriptor, language)
                || models.GetState(modelId) != ModelState.Installed)
            {
                continue;
            }

            return Task.FromResult<AsrEngineChoice?>(new AsrEngineChoice(provider, modelId));
        }

        return Task.FromResult<AsrEngineChoice?>(null);
    }

    private static bool SupportsLanguage(ModelDescriptor model, string? language) =>
        model.Languages.Contains("*") || (language is not null && model.Languages.Any(l => language.StartsWith(l, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Hardware facts used to choose and recommend models (platform probe).</summary>
public interface IAccelerationProbe
{
    /// <summary>Whether a GPU that speeds up large models is present (a discrete GPU with enough memory).</summary>
    bool HasCapableGpu { get; }

    /// <summary>Dedicated memory of that GPU in bytes (0 without one).</summary>
    long GpuMemoryBytes { get; }

    /// <summary>Installed physical memory in bytes.</summary>
    long TotalMemoryBytes { get; }
}

/// <summary>
/// Hardware-aware model preference from the benchmark (docs/benchmarks/asr-v1, ADR-022):
/// with a capable GPU, large-v3-turbo is the most accurate in every category at RTF ≈ 0.04;
/// CPU-only, Whisper small keeps noise robustness at a CPU-feasible cost (large-v3-turbo on CPU is slower than
/// real time). ReazonSpeech (Japanese only) is the fastest CPU option and the fallback when it is the only
/// Japanese model installed; it degrades sharply with noise, so it is not the automatic first choice.
/// </summary>
public static class AsrDefaults
{
    public static IReadOnlyList<string> PreferenceFor(string? language, bool hasCapableGpu)
    {
        var japanese = language is not null && language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> whisper = hasCapableGpu
            ? [ModelCatalog.WhisperLargeV3TurboQ5, ModelCatalog.WhisperSmallQ5]
            : [ModelCatalog.WhisperSmallQ5, ModelCatalog.WhisperLargeV3TurboQ5];
        return japanese ? [.. whisper, ModelCatalog.ReazonSpeechK2V2Int8] : whisper;
    }

    /// <summary>The model to recommend installing first (Model Manager / first run).</summary>
    public static string RecommendedModel(bool hasCapableGpu) =>
        hasCapableGpu ? ModelCatalog.WhisperLargeV3TurboQ5 : ModelCatalog.WhisperSmallQ5;
}

/// <summary>
/// Summary model recommendation (docs/benchmarks/summary-v1, ADR-027): Qwen3-4B has the best recall on CPU and GPU. It
/// needs about 4 GB while running, so it is recommended only on PCs with a 16 GB class of memory (12 GB or more is
/// reported); with less, the extractive summary is the better choice.
/// </summary>
public static class SummaryDefaults
{
    public const long MinimumMemoryBytes = 12L * 1024 * 1024 * 1024;

    public static string? RecommendedModel(long totalMemoryBytes) =>
        totalMemoryBytes >= MinimumMemoryBytes ? ModelCatalog.Qwen3Instruct4B : null;
}
