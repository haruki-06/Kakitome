namespace Kakitome.Application.Decision;

/// <summary>
/// Local, free decision capability (docs/04, ADR-010). It classifies, scores and routes within bounded schemas only:
/// it returns candidates and labels, never performs edits, deletions or any other action itself. The rule-based
/// engine is the default; a free local decision model may replace it if it wins the benchmark.
/// </summary>
public interface IDecisionEngine
{
    string Id { get; }

    /// <summary>Spans in one segment that look like fillers, false starts or repetitions.</summary>
    IReadOnlyList<EditCandidate> FindEditCandidates(string text, string? language);

    /// <summary>Flags for a segment that may be mis-recognized (hallucination patterns, low confidence).</summary>
    IReadOnlyList<string> AssessSegment(string text, double? confidence, double durationSeconds, string? language);

    /// <summary>Best-guess recording type, used to pick a summary profile.</summary>
    RecordingTypeDecision ClassifyRecording(IReadOnlyList<string> segmentTexts, int speakerCount, double durationSeconds, string? language);
}

public enum EditKind
{
    /// <summary>Hesitation words (えー, あのー, um).</summary>
    Filler,

    /// <summary>Immediately repeated word or phrase ("それで、それで").</summary>
    Repetition,

    /// <summary>Whitespace/punctuation normalization.</summary>
    Formatting,
}

public enum EditRisk
{
    /// <summary>Meaning-preserving; applied automatically to the Clean transcript.</summary>
    Low,

    /// <summary>Could change meaning; offered to the user as a suggestion only.</summary>
    High,
}

/// <param name="Start">UTF-16 start index in the segment text.</param>
/// <param name="Length">UTF-16 length of the span to replace.</param>
/// <param name="Replacement">Text that replaces the span (often empty).</param>
public sealed record EditCandidate(EditKind Kind, EditRisk Risk, int Start, int Length, string Replacement, string Reason);

public enum RecordingType
{
    Unknown,
    Lecture,
    Meeting,
    Interview,
    VoiceNote,
}

public sealed record RecordingTypeDecision(RecordingType Type, double Confidence, string Reason);
