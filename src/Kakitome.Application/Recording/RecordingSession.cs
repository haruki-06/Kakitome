using Microsoft.Extensions.Logging;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Recording;

/// <summary>
/// One recording in progress: a set of independent streams sharing a session clock that runs only while
/// recording (not while paused or asleep). Created and owned by <see cref="RecordingService"/>.
/// </summary>
#pragma warning disable CA1001 // _loopCts is disposed in Finish, the session's terminal operation.
public sealed partial class RecordingSession
#pragma warning restore CA1001
{
    private readonly List<StreamRecorder> _streams;
    private readonly LibraryService _library;
    private readonly IDiskSpaceProbe _disk;
    private readonly IRecycleBin _recycleBin;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IDisposable? _keepAwake;
    private readonly Lock _lock = new();
    private readonly List<CaptureEvent> _events = [];
    private readonly CancellationTokenSource _loopCts = new();

    private RecordingState _state = RecordingState.Recording;
    private long _activeTicks;
    private long? _runningSinceTimestamp;
    private Task? _loop;
    private string? _stopReason;
    private TaskCompletionSource? _finished;
    private string? _targetProject;
    private string _folderPath;

    internal RecordingSession(
        RecordingId id,
        string folderPath,
        List<StreamRecorder> streams,
        LibraryService library,
        IDiskSpaceProbe disk,
        IRecycleBin recycleBin,
        IDisposable? keepAwake,
        TimeProvider time,
        ILogger logger)
    {
        Id = id;
        _folderPath = folderPath;
        _streams = streams;
        _library = library;
        _disk = disk;
        _recycleBin = recycleBin;
        _keepAwake = keepAwake;
        _time = time;
        _logger = logger;
        StartedAt = time.GetLocalNow();
    }

    public RecordingId Id { get; }

    /// <summary>Absolute path of the recording folder (changes once if the recording moves to another project on stop).</summary>
    public string FolderPath => Volatile.Read(ref _folderPath);

    /// <summary>
    /// The project the recording should end up in when it is stopped, or null to keep the one it started in. The
    /// folder cannot move while its audio files are open, so the move happens on <see cref="StopAsync"/>.
    /// </summary>
    public string? TargetProject
    {
        get
        {
            lock (_lock)
            {
                return _targetProject;
            }
        }

        set
        {
            lock (_lock)
            {
                _targetProject = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
    }

    public DateTimeOffset StartedAt { get; }

    public RecordingState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Recorded time (excludes pauses and sleep).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromTicks(ActiveTicksLocked());
            }
        }
    }

    /// <summary>Raised on state changes (from arbitrary threads).</summary>
    public event EventHandler? Changed;

    /// <summary>The captured streams (index, kind, format) for live consumers.</summary>
    public IReadOnlyList<LiveStreamInfo> LiveStreams => _streams.Select((s, i) => new LiveStreamInfo(i, s.Request.Kind, s.Format)).ToList();

    /// <summary>
    /// Attaches a live consumer to a stream (null detaches). It is called on the capture thread after the audio has
    /// been queued for writing, so it can never delay or alter the recording; it must only copy.
    /// </summary>
    public void SetTap(int stream, IAudioSampleSink? tap) => _streams[stream].SetTap(tap);

    /// <summary>Raised once when the session ends by itself (disk full, write error).</summary>
    public event EventHandler? EndedUnexpectedly;

    public RecordingStatus GetStatus()
    {
        lock (_lock)
        {
            return new RecordingStatus(
                Id,
                _state,
                TimeSpan.FromTicks(ActiveTicksLocked()),
                _streams.Select(s => new StreamStatus(s.FileName, s.Request.Kind, s.DisplayName, s.State, s.ReadAndResetPeak())).ToList(),
                _stopReason);
        }
    }

    internal void Start()
    {
        lock (_lock)
        {
            _runningSinceTimestamp = _time.GetTimestamp();
        }

        foreach (var stream in _streams)
        {
            stream.Start();
        }

        _loop = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public Task PauseAsync() => PauseCoreAsync(RecordingState.Paused, CaptureEventKind.Paused);

    public Task ResumeAsync() => ResumeCoreAsync(fromState: RecordingState.Paused, CaptureEventKind.Resumed);

    internal Task SuspendAsync() => PauseCoreAsync(RecordingState.Suspended, CaptureEventKind.SystemSleep);

    internal Task WakeAsync() => ResumeCoreAsync(fromState: RecordingState.Suspended, CaptureEventKind.SystemWake);

    /// <summary>Stops and keeps the recording.</summary>
    public async Task StopAsync(string? reason = null)
    {
        if (!await EndStreamsAsync(reason).ConfigureAwait(false))
        {
            return;
        }

        var failed = _streams.Where(s => s.WriteError is not null).ToList();
        await _library.UpdateMetadataAsync(Id, metadata =>
        {
            ApplyProgress(metadata);
            metadata.Capture!.Status = CaptureStatus.Completed;
            metadata.Capture.EndedAt = _time.GetLocalNow();
            metadata.Capture.Note = reason ?? (failed.Count > 0 ? $"Write error: {failed[0].WriteError!.Message}" : null);
        }).ConfigureAwait(false);

        await MoveToTargetProjectAsync().ConfigureAwait(false);
        Finish(RecordingState.Stopped);
    }

    /// <summary>Applies a project change made while recording. On failure the recording stays where it is.</summary>
    private async Task MoveToTargetProjectAsync()
    {
        if (TargetProject is not { } target)
        {
            return;
        }

        try
        {
            if (await _library.MoveToProjectAsync(Id, target).ConfigureAwait(false))
            {
                Volatile.Write(ref _folderPath, await _library.GetRecordingPathAsync(Id).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMoveFailed(ex, Id, target);
        }
    }

    /// <summary>
    /// Discards the recording the user chose to cancel: the folder goes to the Recycle Bin (recoverable).
    /// If that is not possible, the audio is kept and marked cancelled instead of being deleted.
    /// </summary>
    public async Task CancelAsync()
    {
        if (!await EndStreamsAsync("Cancelled by user").ConfigureAwait(false))
        {
            return;
        }

        if (_recycleBin.TryMoveToRecycleBin(FolderPath))
        {
            await _library.ForgetAsync(Id).ConfigureAwait(false);
            LogCancelled(Id);
        }
        else
        {
            await _library.UpdateMetadataAsync(Id, metadata =>
            {
                ApplyProgress(metadata);
                metadata.Capture!.Status = CaptureStatus.Cancelled;
                metadata.Capture.EndedAt = _time.GetLocalNow();
                metadata.Capture.Note = "Cancelled by user; kept because it could not be moved to the Recycle Bin.";
            }).ConfigureAwait(false);
            LogCancelKept(Id);
        }

        Finish(RecordingState.Cancelled);
    }

    /// <summary>Completes when the session has fully stopped or been cancelled.</summary>
    public Task Completion
    {
        get
        {
            lock (_lock)
            {
                _finished ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_state is RecordingState.Stopped or RecordingState.Cancelled)
                {
                    _finished.TrySetResult();
                }

                return _finished.Task;
            }
        }
    }

    private async Task PauseCoreAsync(RecordingState target, CaptureEventKind kind)
    {
        lock (_lock)
        {
            if (_state != RecordingState.Recording)
            {
                return;
            }

            _activeTicks = ActiveTicksLocked();
            _runningSinceTimestamp = null;
            _state = target;
            AddEventLocked(kind, null, null);
        }

        foreach (var stream in _streams)
        {
            stream.SetPaused(true);
            stream.RequestCheckpoint();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await PersistProgressAsync().ConfigureAwait(false);
    }

    private async Task ResumeCoreAsync(RecordingState fromState, CaptureEventKind kind)
    {
        lock (_lock)
        {
            if (_state != fromState)
            {
                return;
            }

            _runningSinceTimestamp = _time.GetTimestamp();
            _state = RecordingState.Recording;
            AddEventLocked(kind, null, null);
        }

        foreach (var stream in _streams)
        {
            stream.SetPaused(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal void OnStreamEvent(StreamRecorder stream, CaptureEventKind kind, string? detail)
    {
        lock (_lock)
        {
            AddEventLocked(kind, stream.FileName, detail);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> EndStreamsAsync(string? reason)
    {
        lock (_lock)
        {
            if (_state is RecordingState.Stopping or RecordingState.Stopped or RecordingState.Cancelled)
            {
                return false;
            }

            _activeTicks = ActiveTicksLocked();
            _runningSinceTimestamp = null;
            _state = RecordingState.Stopping;
            _stopReason = reason;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await _loopCts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        // Final alignment: every stream is padded up to the session length before closing.
        var expected = Elapsed;
        foreach (var stream in _streams)
        {
            stream.SetPaused(false);
            stream.Tick(ExpectedFrames(stream, expected) + MarginFrames(stream));
            stream.SetPaused(true);
        }

        await Task.WhenAll(_streams.Select(s => s.StopAsync())).ConfigureAwait(false);
        _keepAwake?.Dispose();
        return true;
    }

    private void Finish(RecordingState final)
    {
        lock (_lock)
        {
            _state = final;
            _finished?.TrySetResult();
        }

        _loopCts.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(RecordingLimits.TickInterval, _time);
        var lastCheckpoint = _time.GetTimestamp();
        var lastMetadata = _time.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var elapsed = Elapsed;
                foreach (var stream in _streams)
                {
                    stream.Tick(ExpectedFrames(stream, elapsed));
                }

                if (_time.GetElapsedTime(lastCheckpoint) >= RecordingLimits.CheckpointInterval)
                {
                    lastCheckpoint = _time.GetTimestamp();
                    foreach (var stream in _streams)
                    {
                        stream.RequestCheckpoint();
                    }
                }

                if (_streams.Any(s => s.WriteError is not null))
                {
                    _ = StopUnexpectedlyAsync("Audio could not be written to disk.");
                    return;
                }

                if (_time.GetElapsedTime(lastMetadata) >= RecordingLimits.MetadataInterval)
                {
                    lastMetadata = _time.GetTimestamp();
                    if (IsDiskAlmostFull())
                    {
                        lock (_lock)
                        {
                            AddEventLocked(CaptureEventKind.LowDiskSpace, null, null);
                        }

                        _ = StopUnexpectedlyAsync("Stopped because the disk is almost full; the audio recorded so far is kept.");
                        return;
                    }

                    await PersistProgressAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task StopUnexpectedlyAsync(string reason)
    {
        LogStoppedUnexpectedly(Id, reason);
        await StopAsync(reason).ConfigureAwait(false);
        EndedUnexpectedly?.Invoke(this, EventArgs.Empty);
    }

    private bool IsDiskAlmostFull()
    {
        try
        {
            return _disk.GetAvailableBytes(FolderPath) < RecordingLimits.MinFreeBytesWhileRecording;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task PersistProgressAsync()
    {
        try
        {
            await _library.UpdateMetadataAsync(Id, ApplyProgress).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Progress persistence is best-effort; audio keeps recording.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPersistFailed(ex, Id);
        }
    }

    private void ApplyProgress(RecordingMetadata metadata)
    {
        List<CaptureEvent> events;
        lock (_lock)
        {
            events = [.. _events];
        }

        metadata.DurationSeconds = Elapsed.TotalSeconds;
        metadata.Capture ??= new CaptureInfo { Status = CaptureStatus.InProgress, StartedAt = StartedAt };
        metadata.Capture.Events = events;
        foreach (var stream in _streams)
        {
            var info = metadata.Audio.FirstOrDefault(a => a.FileName == stream.FileName);
            if (info is not null)
            {
                info.DurationSeconds = stream.DurationSeconds;
                info.Device = stream.DisplayName;
            }
        }
    }

    private void AddEventLocked(CaptureEventKind kind, string? stream, string? detail) =>
        _events.Add(new CaptureEvent
        {
            At = _time.GetLocalNow(),
            OffsetSeconds = TimeSpan.FromTicks(ActiveTicksLocked()).TotalSeconds,
            Kind = kind,
            Stream = stream,
            Detail = detail,
        });

    private long ActiveTicksLocked() =>
        _activeTicks + (_runningSinceTimestamp is { } since ? _time.GetElapsedTime(since).Ticks : 0);

    private static long ExpectedFrames(StreamRecorder stream, TimeSpan elapsed) =>
        (long)(elapsed.TotalSeconds * stream.Format.SampleRate);

    private static long MarginFrames(StreamRecorder stream) => (long)(0.05 * stream.Format.SampleRate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Id} cancelled and moved to the Recycle Bin")]
    private partial void LogCancelled(RecordingId id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording {Id} cancelled but kept (Recycle Bin unavailable)")]
    private partial void LogCancelKept(RecordingId id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording {Id} stopped: {Reason}")]
    private partial void LogStoppedUnexpectedly(RecordingId id, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording {Id} could not be moved to project {Project}; it stays in its original project")]
    private partial void LogMoveFailed(Exception ex, RecordingId id, string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update metadata for recording {Id}")]
    private partial void LogPersistFailed(Exception ex, RecordingId id);
}

public sealed record LiveStreamInfo(int Index, CaptureSourceKind Kind, AudioFormat Format);
