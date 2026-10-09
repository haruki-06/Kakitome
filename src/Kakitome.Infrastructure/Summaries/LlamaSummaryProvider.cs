using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Models;
using Kakitome.Application.Summaries;

namespace Kakitome.Infrastructure.Summaries;

/// <summary>
/// Local-LLM summaries with llama.cpp (LLamaSharp) and a 4-bit GGUF model the user installed (docs/04 "Summary",
/// docs/08 "Local LLM"). Output is grammar-constrained JSON (<see cref="LlmSummaryFormat"/>). Long transcripts are
/// summarized per chunk (map), then title/overview/key points are condensed from the chunk notes (reduce); decisions,
/// actions and questions keep their citations into the transcript. The GPU is used only on AC power and when it has
/// room for the whole model (<see cref="MinimumGpuMemoryBytes"/>); if it still fails there, the summary runs on the CPU.
/// </summary>
public sealed partial class LlamaSummaryProvider(
    IModelStore models,
    IAccelerationProbe acceleration,
    ISystemResourceProbe resources,
    ILogger<LlamaSummaryProvider> logger) : ISummaryProvider
{
    public const string ProviderId = "llama.cpp";

    /// <summary>Tokens of transcript per map pass (context 8192 leaves room for instructions and the answer).</summary>
    internal const int ChunkTokens = 4_500;

    /// <summary>Qwen3-4B Q4 fully offloaded: ~2.5 GB weights + ~1.2 GB KV cache (8k context) + buffers (ADR-035).</summary>
    public const long MinimumGpuMemoryBytes = 6L * 1024 * 1024 * 1024;

    private const int ContextTokens = 8_192;
    private const int AnswerTokens = 1_800;

    public string Id => ProviderId;

    /// <summary>Above the extractive provider (0) when a model is installed — set from the benchmark (ADR-027).</summary>
    public int Preference => 10;

    public JobResourceClass ResourceClass => JobResourceClass.Heavy;

    /// <summary>Model forced for benchmarking; null = first installed model in <see cref="ModelCatalog.SummaryModels"/> order.</summary>
    public string? ModelOverride { get; init; }

    /// <summary>Force CPU (benchmark comparisons); otherwise the GPU is used on AC power when capable.</summary>
    public bool? UseGpuOverride { get; init; }

    /// <summary>Receives each raw model answer (benchmark diagnostics).</summary>
    public Action<string>? RawOutputObserver { get; init; }

    public bool IsAvailable(string? language) => SelectModel() is not null;

    public async Task<SummaryDraft> SummarizeAsync(SummaryInput input, ResourceBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(budget);
        var model = SelectModel() ?? throw new InvalidOperationException("No local summary model is installed.");
        var useGpu = UseGpuOverride
            ?? (acceleration.HasCapableGpu && acceleration.GpuMemoryBytes >= MinimumGpuMemoryBytes && resources.Current.OnAcPower && !budget.PreferEfficiency);
        LlamaRuntime.ConfigureNative();
        if (useGpu && UseGpuOverride is null)
        {
            try
            {
                return await SummarizeOnAsync(model, input, budget, gpu: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Out of GPU memory, a driver problem…: the CPU is slower but always works.
                LogGpuFailed(ex, model.Id);
            }

            useGpu = false;
        }

        return await SummarizeOnAsync(model, input, budget, useGpu, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SummaryDraft> SummarizeOnAsync(
        ModelDescriptor model, SummaryInput input, ResourceBudget budget, bool gpu, CancellationToken cancellationToken)
    {
        var useGpu = gpu;

        var path = Path.Combine(models.GetDirectory(model.Id), model.Files[0].Name);
        var parameters = new ModelParams(path)
        {
            ContextSize = ContextTokens,
            GpuLayerCount = useGpu ? 99 : 0,
            Threads = Math.Max(1, budget.MaxThreads),
            BatchSize = 512,
        };

        var started = Stopwatch.GetTimestamp();
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
        var engine = $"llama.cpp (LLamaSharp 0.27.0, {(useGpu ? "Vulkan GPU" : "CPU")})";
        var system = LlmSummaryFormat.SystemPrompt(input.Language);

        var chunks = Chunk(input.Segments, s => weights.Tokenize(s.Text, false, false, Encoding.UTF8).Length + 12);
        var drafts = new List<SummaryDraft>();
        foreach (var chunk in chunks)
        {
            var json = await LlamaRuntime.InferAsync(weights, parameters, system, LlmSummaryFormat.UserPrompt(input, 1, chunk), LlmSummaryFormat.GrammarFor(chunk.Count), AnswerTokens, cancellationToken).ConfigureAwait(false);
            RawOutputObserver?.Invoke(json);
            drafts.Add(LlmSummaryFormat.Parse(json, chunk, engine, model.Id));
        }

        var result = LlmSummaryFormat.ApplyKind(drafts.Count == 1 ? drafts[0] : await ReduceAsync(weights, parameters, input, drafts, engine, model.Id, cancellationToken).ConfigureAwait(false));
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        LogSummarized(model.Id, input.Segments.Count, chunks.Count, seconds, useGpu);
        return result;
    }

    /// <summary>Splits segments into consecutive chunks of at most <see cref="ChunkTokens"/> estimated tokens.</summary>
    internal static List<IReadOnlyList<SummarySourceSegment>> Chunk(IReadOnlyList<SummarySourceSegment> segments, Func<SummarySourceSegment, int> tokens)
    {
        var chunks = new List<IReadOnlyList<SummarySourceSegment>>();
        var current = new List<SummarySourceSegment>();
        var size = 0;
        foreach (var segment in segments)
        {
            var cost = tokens(segment);
            if (current.Count > 0 && size + cost > ChunkTokens)
            {
                chunks.Add(current);
                current = [];
                size = 0;
            }

            current.Add(segment);
            size += cost;
        }

        if (current.Count > 0 || chunks.Count == 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    /// <summary>
    /// Reduce: the chunk overviews and key points become numbered notes; the model writes the final title, overview,
    /// key points and topics citing notes, which map back to the notes' transcript citations. Decisions, actions and
    /// questions are the union of the chunks (duplicates removed).
    /// </summary>
    private async Task<SummaryDraft> ReduceAsync(
        LLamaWeights weights, ModelParams parameters, SummaryInput input, List<SummaryDraft> drafts, string engine, string model, CancellationToken cancellationToken)
    {
        var notes = new List<SummarySourceSegment>();
        var noteSources = new List<DraftItem?>();
        foreach (var draft in drafts)
        {
            if (draft.Overview is { } overview)
            {
                notes.Add(new SummarySourceSegment($"n{notes.Count + 1}", (draft.KeyPoints.Count > 0 ? draft.KeyPoints[0].AtSeconds : null) ?? 0, 0, overview, null));
                noteSources.Add(null);
            }

            foreach (var point in draft.KeyPoints)
            {
                notes.Add(new SummarySourceSegment($"n{notes.Count + 1}", point.AtSeconds ?? 0, 0, point.Text, null));
                noteSources.Add(point);
            }
        }

        var json = await LlamaRuntime.InferAsync(weights, parameters, LlmSummaryFormat.SystemPrompt(input.Language), LlmSummaryFormat.UserPrompt(input, 1, notes), LlmSummaryFormat.ReduceGrammar, AnswerTokens, cancellationToken)
            .ConfigureAwait(false);
        var reduced = LlmSummaryFormat.Parse(json, notes, engine, model);

        DraftItem Remap(DraftItem item)
        {
            var sources = (item.SegmentIds ?? []).Select(id => int.Parse(id.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) - 1)
                .Select(i => noteSources[i]).OfType<DraftItem>().ToList();
            return new DraftItem(item.Text, sources.FirstOrDefault()?.AtSeconds, sources.SelectMany(s => s.SegmentIds ?? []).Distinct().ToList() is { Count: > 0 } ids ? ids : null);
        }

        static IReadOnlyList<T> Union<T>(IEnumerable<T> items)
            where T : DraftItem => [.. items.GroupBy(i => i.Text, StringComparer.Ordinal).Select(g => g.First()).OrderBy(i => i.AtSeconds ?? double.MaxValue)];

        return reduced with
        {
            KeyPoints = [.. reduced.KeyPoints.Select(Remap)],
            Decisions = Union(drafts.SelectMany(d => d.Decisions)),
            ActionItems = Union(drafts.SelectMany(d => d.ActionItems)),
            Questions = Union(drafts.SelectMany(d => d.Questions)),
            Topics = reduced.Topics.Count > 0 ? reduced.Topics : [.. drafts.SelectMany(d => d.Topics).Distinct(StringComparer.Ordinal).Take(5)],
        };
    }

    private ModelDescriptor? SelectModel() =>
        ModelCatalog.SummaryModels
            .Where(m => ModelOverride is null || m.Id == ModelOverride)
            .FirstOrDefault(m => models.GetState(m.Id) == ModelState.Installed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary with {Model} failed on the GPU; retrying on the CPU")]
    private partial void LogGpuFailed(Exception ex, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summarized {Segments} segments with {Model} in {Chunks} chunk(s), {Seconds:F1} s (GPU: {Gpu})")]
    private partial void LogSummarized(string model, int segments, int chunks, double seconds, bool gpu);
}
