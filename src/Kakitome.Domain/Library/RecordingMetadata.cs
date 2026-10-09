using System.Text.Json;
using System.Text.Json.Serialization;
using Kakitome.Domain.Recordings;

namespace Kakitome.Domain.Library;

/// <summary>
/// Contents of <c>metadata.json</c>: everything needed to understand and re-index a recording without
/// Kakitome's database. Never contains secrets.
/// </summary>
public sealed class RecordingMetadata
{
    public const string SchemaName = "kakitome.metadata";
    public const int CurrentSchemaVersion = 1;

    public string Schema { get; set; } = SchemaName;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required RecordingId Id { get; set; }

    public required string Title { get; set; }

    /// <summary>Display name of the owning project (the folder name is its sanitized form).</summary>
    public required string Project { get; set; }

    public List<string> Tags { get; set; } = [];

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When capture started (recordings) or the media was recorded, if known (imports).</summary>
    public DateTimeOffset? RecordedAt { get; set; }

    public double? DurationSeconds { get; set; }

    public required RecordingSourceType SourceType { get; set; }

    public CaptureInfo? Capture { get; set; }

    public ImportInfo? Import { get; set; }

    /// <summary>Audio streams kept for this recording. Each source is an independent file.</summary>
    public List<AudioStreamInfo> Audio { get; set; } = [];

    /// <summary>Retention format for the audio (<c>wav</c>, <c>flac</c>, <c>mp3</c>, <c>m4a</c>, …).</summary>
    public string? RetainedAudioFormat { get; set; }

    /// <summary>Set only when the user's retention setting intentionally removed the audio.</summary>
    public AudioRemovalInfo? AudioRemoval { get; set; }

    /// <summary>BCP-47 language of the spoken content (e.g. <c>ja</c>, <c>en</c>), when known.</summary>
    public string? Language { get; set; }

    public string? ProcessingProfile { get; set; }

    /// <summary>Provenance of each processing stage (engine/model identity and versions).</summary>
    public List<ProcessingStepInfo> Processing { get; set; } = [];

    /// <summary>Unknown properties written by newer versions or by the user are preserved on rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public enum RecordingSourceType
{
    [JsonStringEnumMemberName("recording")]
    Recording,

    [JsonStringEnumMemberName("fileImport")]
    FileImport,

    [JsonStringEnumMemberName("urlImport")]
    UrlImport,
}

public sealed class CaptureInfo
{
    public required CaptureStatus Status { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Human-readable note, e.g. why a capture was interrupted.</summary>
    public string? Note { get; set; }

    /// <summary>Timeline of pauses, device changes, sleep and recovery during capture.</summary>
    public List<CaptureEvent> Events { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public enum CaptureStatus
{
    /// <summary>Capture is in progress (or the app stopped before finalizing it).</summary>
    [JsonStringEnumMemberName("inProgress")]
    InProgress,

    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Capture ended unexpectedly (device loss, crash, power); audio up to that point is kept.</summary>
    [JsonStringEnumMemberName("interrupted")]
    Interrupted,

    /// <summary>The user cancelled, but the audio could not be moved to the Recycle Bin, so it was kept.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

public sealed class CaptureEvent
{
    public required DateTimeOffset At { get; set; }

    /// <summary>Position on the recording timeline (seconds of recorded audio) when the event happened.</summary>
    public double OffsetSeconds { get; set; }

    public required CaptureEventKind Kind { get; set; }

    /// <summary>Affected stream file, when the event concerns a single stream.</summary>
    public string? Stream { get; set; }

    public string? Detail { get; set; }
}

public enum CaptureEventKind
{
    [JsonStringEnumMemberName("paused")]
    Paused,

    [JsonStringEnumMemberName("resumed")]
    Resumed,

    [JsonStringEnumMemberName("deviceLost")]
    DeviceLost,

    [JsonStringEnumMemberName("deviceRestored")]
    DeviceRestored,

    /// <summary>The requested device did not come back; capture continued on another device.</summary>
    [JsonStringEnumMemberName("deviceSwitched")]
    DeviceSwitched,

    [JsonStringEnumMemberName("systemSleep")]
    SystemSleep,

    [JsonStringEnumMemberName("systemWake")]
    SystemWake,

    [JsonStringEnumMemberName("lowDiskSpace")]
    LowDiskSpace,

    /// <summary>The app found this capture unfinished at startup and repaired its audio files.</summary>
    [JsonStringEnumMemberName("recovered")]
    Recovered,
}

public sealed class ImportInfo
{
    public string? OriginalFileName { get; set; }

    public string? SourceUrl { get; set; }

    public DateTimeOffset? ImportedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class AudioStreamInfo
{
    /// <summary>File name relative to the recording folder (never a path).</summary>
    public required string FileName { get; set; }

    public required AudioStreamRole Role { get; set; }

    /// <summary>Container/codec, e.g. <c>wav/pcm_f32le</c>, <c>flac</c>, <c>m4a/aac</c>.</summary>
    public string? Format { get; set; }

    public int? SampleRate { get; set; }

    public int? Channels { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>Friendly device name for captured streams (not a secret, helps the user identify sources).</summary>
    public string? Device { get; set; }

    /// <summary>Level/speech analysis computed after capture (absent until analyzed).</summary>
    public AudioAnalysis? Analysis { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class AudioAnalysis
{
    public double PeakDbfs { get; set; }

    public double RmsDbfs { get; set; }

    /// <summary>Estimated background level (10th percentile of 30 ms windows).</summary>
    public double NoiseFloorDbfs { get; set; }

    /// <summary>Fraction of 30 ms windows clearly above the noise floor (0..1).</summary>
    public double SpeechRatio { get; set; }

    /// <summary>True when the stream is effectively silent (muted/disconnected microphone).</summary>
    public bool Silent { get; set; }
}

public enum AudioStreamRole
{
    [JsonStringEnumMemberName("microphone")]
    Microphone,

    [JsonStringEnumMemberName("systemAudio")]
    SystemAudio,

    [JsonStringEnumMemberName("application")]
    Application,

    [JsonStringEnumMemberName("imported")]
    Imported,
}

public sealed class AudioRemovalInfo
{
    public required DateTimeOffset RemovedAt { get; set; }

    /// <summary>The retention setting that requested removal (e.g. <c>deleteAfterProcessing</c>).</summary>
    public required string Setting { get; set; }

    public string? Reason { get; set; }

    public List<string> RemovedFiles { get; set; } = [];
}

public sealed class ProcessingStepInfo
{
    /// <summary>Stage id: <c>normalize</c>, <c>asr</c>, <c>cleanup</c>, <c>diarization</c>, <c>summary</c>, <c>index</c>.</summary>
    public required string Stage { get; set; }

    public required ProcessingStepStatus Status { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public string? ModelVersion { get; set; }

    /// <summary><c>local</c> or <c>windows</c> (OS-provided on-device AI).</summary>
    public string? Locality { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public enum ProcessingStepStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("skipped")]
    Skipped,
}
