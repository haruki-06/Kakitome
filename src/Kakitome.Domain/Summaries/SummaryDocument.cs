using System.Text.Json;
using System.Text.Json.Serialization;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Domain.Summaries;

/// <summary>Contents of <c>summary.json</c>: structured local summary rendered to Markdown/TXT.</summary>
public sealed class SummaryDocument
{
    public const string SchemaName = "kakitome.summary";
    public const int CurrentSchemaVersion = 1;

    public string Schema { get; set; } = SchemaName;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required RecordingId RecordingId { get; set; }

    public string? Language { get; set; }

    public string? Title { get; set; }

    public string? Overview { get; set; }

    public List<SummaryItem> KeyPoints { get; set; } = [];

    public List<SummaryItem> Decisions { get; set; } = [];

    public List<ActionItem> ActionItems { get; set; } = [];

    public List<SummaryItem> Questions { get; set; } = [];

    public List<string> Topics { get; set; } = [];

    public required SummaryGeneration Generation { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class SummaryItem
{
    public required string Text { get; set; }

    /// <summary>Optional position in the recording that supports this item.</summary>
    public double? AtSeconds { get; set; }

    /// <summary>Optional transcript segment ids that support this item.</summary>
    public List<string>? SegmentIds { get; set; }
}

public sealed class ActionItem : SummaryItem
{
    public string? Owner { get; set; }

    /// <summary>Due date/time as spoken or inferred (free text; not parsed).</summary>
    public string? Due { get; set; }
}

public sealed class SummaryGeneration
{
    public required EngineInfo Engine { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Transcript revision the summary was generated from (detects stale summaries).</summary>
    public int? TranscriptRevision { get; set; }

    public string? Profile { get; set; }
}
