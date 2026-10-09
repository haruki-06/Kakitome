using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Jobs;

/// <summary>Durable job lifecycle (docs/06): Pending → Running → Paused / Succeeded / Failed / Cancelled.</summary>
public enum JobState
{
    Pending = 0,
    Running = 1,

    /// <summary>Paused by the user; the scheduler will not start it until resumed.</summary>
    Paused = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>How much a job costs; the resource policy decides per class whether it may run now.</summary>
public enum JobResourceClass
{
    /// <summary>Small I/O or bookkeeping (indexing, metadata). Runs on battery.</summary>
    Light = 0,

    /// <summary>Sustained CPU/GPU/NPU work (ASR, summarization, transcoding). Deferred on battery by default.</summary>
    Heavy = 1,
}

/// <summary>Why a pending job is not running right now (shown in the Processing Queue).</summary>
public enum JobWaitReason
{
    None = 0,
    Dependency = 1,
    RetryBackoff = 2,
    OnBattery = 3,
    EnergySaver = 4,
    LowBattery = 5,
    SystemBusy = 6,
    LowMemory = 7,
    LowDiskSpace = 8,
    RecordingInProgress = 9,
    ConcurrencyLimit = 10,
}

/// <summary>Persistent job row.</summary>
public sealed record JobRecord
{
    public required Guid Id { get; init; }

    /// <summary>Handler key, e.g. <c>audio.analyze</c>, <c>asr</c>, <c>summary</c>.</summary>
    public required string Kind { get; init; }

    public RecordingId? RecordingId { get; init; }

    public JobState State { get; init; } = JobState.Pending;

    public JobResourceClass ResourceClass { get; init; }

    /// <summary>Higher runs first.</summary>
    public int Priority { get; init; }

    /// <summary>Job that must succeed before this one can run.</summary>
    public Guid? DependsOn { get; init; }

    /// <summary>Groups the stages created for one recording/import (a pipeline run).</summary>
    public Guid? PipelineId { get; init; }

    /// <summary>Handler-specific JSON input.</summary>
    public string? Payload { get; init; }

    /// <summary>Handler-specific JSON resume point, persisted as work progresses.</summary>
    public string? Checkpoint { get; init; }

    public int Attempts { get; init; }

    public int MaxAttempts { get; init; } = 3;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>0..1 when known.</summary>
    public double? Progress { get; init; }

    public string? ProgressText { get; init; }

    public JobWaitReason WaitReason { get; init; }

    /// <summary>User-readable failure summary (no stack traces, no user content).</summary>
    public string? LastError { get; init; }

    /// <summary>Engine/model identity that ran the job (e.g. <c>whisper.cpp / large-v3-turbo</c>) and where.</summary>
    public string? Engine { get; init; }

    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
}

/// <summary>Input for enqueueing a job.</summary>
public sealed record JobRequest(string Kind)
{
    public RecordingId? RecordingId { get; init; }

    public int Priority { get; init; }

    public Guid? DependsOn { get; init; }

    public Guid? PipelineId { get; init; }

    public string? Payload { get; init; }

    public int MaxAttempts { get; init; } = 3;
}

public sealed class JobChangedEventArgs(JobRecord job) : EventArgs
{
    public JobRecord Job { get; } = job;
}
