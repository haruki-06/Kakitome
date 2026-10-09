using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Recording;

/// <summary>What to record. Microphone is captured by default; system audio is opt-in (docs/03).</summary>
public sealed record RecordingOptions
{
    public string? Project { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public bool IncludeMicrophone { get; init; } = true;

    /// <summary>Null = default microphone.</summary>
    public string? MicrophoneId { get; init; }

    public bool IncludeSystemAudio { get; init; }

    /// <summary>Null = default output device.</summary>
    public string? SystemAudioDeviceId { get; init; }

    /// <summary>Advanced: capture only this application's audio (instead of all system audio).</summary>
    public int? ApplicationProcessId { get; init; }

    public string? ApplicationName { get; init; }

    public string? Language { get; init; }

    public string? ProcessingProfile { get; init; }

    /// <summary>Keep the PC from idle-sleeping while recording (lid close is still governed by Windows).</summary>
    public bool PreventSleep { get; init; } = true;
}

public enum RecordingState
{
    Recording,
    Paused,

    /// <summary>Paused automatically because the system is going to sleep; resumes on wake.</summary>
    Suspended,
    Stopping,
    Stopped,
    Cancelled,
}

public sealed record StreamStatus(string FileName, CaptureSourceKind Kind, string DisplayName, StreamState State, float Peak);

public sealed record RecordingStatus(
    RecordingId RecordingId,
    RecordingState State,
    TimeSpan Elapsed,
    IReadOnlyList<StreamStatus> Streams,
    string? StopReason);

public sealed class RecordingStateChangedEventArgs(RecordingStatus? status) : EventArgs
{
    /// <summary>Null when no recording is active any more.</summary>
    public RecordingStatus? Status { get; } = status;
}

/// <summary>Why a recording cannot start.</summary>
public enum RecordingStartFailure
{
    AlreadyRecording,
    NoSources,
    InsufficientDiskSpace,
    DeviceUnavailable,
}

public sealed class RecordingStartException(RecordingStartFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public RecordingStartFailure Failure { get; } = failure;
}

public static class RecordingLimits
{
    /// <summary>Refuse to start below this much free space (~20 min of mono float audio).</summary>
    public const long MinFreeBytesToStart = 256L * 1024 * 1024;

    /// <summary>Stop and keep what was recorded when free space drops below this.</summary>
    public const long MinFreeBytesWhileRecording = 64L * 1024 * 1024;

    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Audio files are flushed and their headers updated this often.</summary>
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(1);

    /// <summary>metadata.json is refreshed (duration, events) this often, for crash recovery.</summary>
    public static readonly TimeSpan MetadataInterval = TimeSpan.FromSeconds(10);
}
