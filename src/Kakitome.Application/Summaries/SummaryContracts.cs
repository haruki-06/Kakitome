using Kakitome.Application.Jobs;

namespace Kakitome.Application.Summaries;

/// <summary>
/// Local summary capability (docs/04 "Summary": local-only, structured output). Providers are chosen by benchmark; the
/// extractive rule-based provider always works offline without any model download.
/// </summary>
public interface ISummaryProvider
{
    string Id { get; }

    /// <summary>Higher wins when several providers are available (benchmark-derived).</summary>
    int Preference { get; }

    /// <summary>True when the provider can run now (model installed, language supported).</summary>
    bool IsAvailable(string? language);

    /// <summary>Heavy for LLMs, Light for rule-based extraction.</summary>
    JobResourceClass ResourceClass { get; }

    Task<SummaryDraft> SummarizeAsync(SummaryInput input, ResourceBudget budget, CancellationToken cancellationToken = default);
}

public sealed record SummaryInput(string Title, string? Language, string? Profile, IReadOnlyList<SummarySourceSegment> Segments);

public sealed record SummarySourceSegment(string Id, double StartSeconds, double EndSeconds, string Text, string? Speaker);

/// <summary>Provider output before it becomes <c>summary.json</c>.</summary>
public sealed record SummaryDraft
{
    /// <summary>What the recording is (LLM: meeting, lecture, conversation, other); null when not classified.</summary>
    public string? Kind { get; init; }

    public string? Title { get; init; }

    public string? Overview { get; init; }

    public IReadOnlyList<DraftItem> KeyPoints { get; init; } = [];

    public IReadOnlyList<DraftItem> Decisions { get; init; } = [];

    public IReadOnlyList<DraftAction> ActionItems { get; init; } = [];

    public IReadOnlyList<DraftItem> Questions { get; init; } = [];

    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>Engine/model identity for lineage.</summary>
    public required string Engine { get; init; }

    public string? Model { get; init; }
}

public record DraftItem(string Text, double? AtSeconds, IReadOnlyList<string>? SegmentIds);

public sealed record DraftAction(string Text, double? AtSeconds, IReadOnlyList<string>? SegmentIds, string? Owner, string? Due)
    : DraftItem(Text, AtSeconds, SegmentIds);
