using System.Text.Json;
using System.Text.Json.Serialization;
using Kakitome.Domain.Recordings;

namespace Kakitome.Domain.Transcripts;

/// <summary>
/// Contents of <c>transcript.json</c>. Holds the current transcript plus enough lineage to tell Raw,
/// Clean and user-edited states apart: every segment keeps its original ASR text in
/// <see cref="TranscriptSegment.RawText"/> whenever the current text differs from it.
/// </summary>
public sealed class TranscriptDocument
{
    public const string SchemaName = "kakitome.transcript";
    public const int CurrentSchemaVersion = 1;

    public string Schema { get; set; } = SchemaName;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required RecordingId RecordingId { get; set; }

    /// <summary>BCP-47 language (e.g. <c>ja</c>); mixed-language content uses the dominant language.</summary>
    public string? Language { get; set; }

    /// <summary>Monotonic revision; bumped on every rewrite (cleanup, user edit, re-transcription).</summary>
    public int Revision { get; set; } = 1;

    /// <summary>What the current <see cref="TranscriptSegment.Text"/> values represent.</summary>
    public TranscriptKind Kind { get; set; } = TranscriptKind.Raw;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The ASR engine that produced the raw text.</summary>
    public EngineInfo? Engine { get; set; }

    /// <summary>History of transformations, oldest first.</summary>
    public List<TranscriptLineageEntry> Lineage { get; set; } = [];

    public List<SpeakerInfo> Speakers { get; set; } = [];

    public List<TranscriptSegment> Segments { get; set; } = [];

    public TranscriptQuality? Quality { get; set; }

    /// <summary>Higher-risk edits proposed by cleanup; applied only when the user accepts them.</summary>
    public List<EditSuggestion>? Suggestions { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class EditSuggestion
{
    public required string SegmentId { get; set; }

    /// <summary><c>filler</c>, <c>repetition</c>, …</summary>
    public required string Kind { get; set; }

    /// <summary>The exact text the suggestion would replace (used to re-locate it safely).</summary>
    public required string Original { get; set; }

    public required string Replacement { get; set; }

    public string? Reason { get; set; }
}

public enum TranscriptKind
{
    [JsonStringEnumMemberName("raw")]
    Raw,

    [JsonStringEnumMemberName("clean")]
    Clean,

    [JsonStringEnumMemberName("edited")]
    Edited,
}

public sealed class EngineInfo
{
    public required string Provider { get; set; }

    public string? Model { get; set; }

    public string? Version { get; set; }

    /// <summary><c>local</c> or <c>windows</c>.</summary>
    public string? Locality { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class TranscriptLineageEntry
{
    public required TranscriptKind Kind { get; set; }

    public required int Revision { get; set; }

    public required DateTimeOffset At { get; set; }

    /// <summary>What produced this revision, e.g. <c>asr</c>, <c>cleanup:rules</c>, <c>user</c>.</summary>
    public required string Source { get; set; }

    public EngineInfo? Engine { get; set; }

    public string? Note { get; set; }
}

public sealed class SpeakerInfo
{
    public required string Id { get; set; }

    public string? Label { get; set; }
}

public sealed class TranscriptSegment
{
    /// <summary>Stable id within the transcript (used by summary references and UI navigation).</summary>
    public required string Id { get; set; }

    public required double StartSeconds { get; set; }

    public required double EndSeconds { get; set; }

    public required string Text { get; set; }

    /// <summary>Original ASR text when <see cref="Text"/> was changed by cleanup or the user; otherwise null.</summary>
    public string? RawText { get; set; }

    /// <summary>True when the user edited this segment (cleanup must not overwrite it).</summary>
    public bool Edited { get; set; }

    public string? Speaker { get; set; }

    public double? Confidence { get; set; }

    public List<TranscriptWord>? Words { get; set; }

    /// <summary>Quality/decision flags, e.g. <c>lowConfidence</c>, <c>suspicious</c>.</summary>
    public List<string>? Flags { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class TranscriptWord
{
    public required string Text { get; set; }

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public double? Confidence { get; set; }
}

public sealed class TranscriptQuality
{
    public double? MeanConfidence { get; set; }

    public double? SpeechRatio { get; set; }

    public List<string>? Warnings { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
