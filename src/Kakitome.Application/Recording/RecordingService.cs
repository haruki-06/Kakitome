using Microsoft.Extensions.Logging;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;

namespace Kakitome.Application.Recording;

/// <summary>
/// Entry point for recording from the main window, tray and global hotkey. At most one recording runs at a time.
/// </summary>
public sealed partial class RecordingService : IDisposable
{
    private readonly LibraryService _library;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly IAudioFileWriterFactory _writerFactory;
    private readonly IKeepAwake _keepAwake;
    private readonly IPowerEvents _power;
    private readonly IDiskSpaceProbe _disk;
    private readonly IRecycleBin _recycleBin;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RecordingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RecordingSession? _current;

    public RecordingService(
        LibraryService library,
        IAudioCaptureFactory captureFactory,
        IAudioFileWriterFactory writerFactory,
        IKeepAwake keepAwake,
        IPowerEvents power,
        IDiskSpaceProbe disk,
        IRecycleBin recycleBin,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _library = library;
        _captureFactory = captureFactory;
        _writerFactory = writerFactory;
        _keepAwake = keepAwake;
        _power = power;
        _disk = disk;
        _recycleBin = recycleBin;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RecordingService>();
        _power.Suspending += OnSuspending;
        _power.Resumed += OnResumed;
    }

    /// <summary>The active recording, or null.</summary>
    public RecordingSession? Current => Volatile.Read(ref _current);

    /// <summary>Raised whenever the active recording changes state (from arbitrary threads).</summary>
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a recording session was created, just before capture starts (live consumers attach here).</summary>
    public event EventHandler<RecordingSession>? SessionStarted;

    /// <summary>Raised once when a recording was stopped and saved (not when discarded).</summary>
    public event EventHandler<Kakitome.Domain.Recordings.RecordingId>? RecordingCompleted;

    public async Task<RecordingSession> StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sources = new List<(CaptureSourceRequest Request, IAudioCaptureSource Source)>();
        try
        {
            if (Current is not null)
            {
                throw new RecordingStartException(RecordingStartFailure.AlreadyRecording, "A recording is already in progress.");
            }

            var requests = BuildRequests(options);
            if (requests.Count == 0)
            {
                throw new RecordingStartException(RecordingStartFailure.NoSources, "Select at least one audio source.");
            }

            EnsureDiskSpace();

            // Open every device first so a missing device fails before anything is written.
            foreach (var request in requests)
            {
                var format = request.Kind == CaptureSourceKind.Microphone ? AudioFormat.Microphone : AudioFormat.Loopback;
                try
                {
                    sources.Add((request, await _captureFactory.CreateAsync(request, format, cancellationToken).ConfigureAwait(false)));
                }
                catch (AudioDeviceUnavailableException ex)
                {
                    throw new RecordingStartException(RecordingStartFailure.DeviceUnavailable, ex.Message, ex);
                }
            }

            var entry = await _library.CreateRecordingAsync(
                new NewRecording
                {
                    Project = options.Project,
                    Title = options.Title,
                    Tags = options.Tags,
                    SourceType = RecordingSourceType.Recording,
                    RecordedAt = _time.GetLocalNow(),
                    Language = options.Language,
                    ProcessingProfile = options.ProcessingProfile,
                },
                cancellationToken).ConfigureAwait(false);
            var folderPath = _library.GetAbsolutePath(entry);

            var streams = new List<StreamRecorder>();
            RecordingSession? session = null;
            try
            {
                for (var i = 0; i < sources.Count; i++)
                {
                    var (request, source) = sources[i];
                    var fileName = LibraryLayout.AudioFileName(_writerFactory.Extension, i == 0 ? null : StreamSuffix(request.Kind));
                    var writer = _writerFactory.Create(Path.Combine(folderPath, fileName), source.Format);
                    streams.Add(new StreamRecorder(
                        request,
                        source,
                        writer,
                        _captureFactory,
                        _time,
                        (stream, kind, detail) => session?.OnStreamEvent(stream, kind, detail),
                        _loggerFactory.CreateLogger<StreamRecorder>()));
                }

                await _library.UpdateMetadataAsync(entry.Id, metadata =>
                {
                    metadata.RetainedAudioFormat = _writerFactory.Extension;
                    metadata.Audio = streams.Select(s => new AudioStreamInfo
                    {
                        FileName = s.FileName,
                        Role = RoleOf(s.Request.Kind),
                        Format = _writerFactory.FormatLabel,
                        SampleRate = s.Format.SampleRate,
                        Channels = s.Format.Channels,
                        Device = s.DisplayName,
                    }).ToList();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await AbandonStartAsync(entry.Id, streams).ConfigureAwait(false);
                throw;
            }

            var keepAwake = options.PreventSleep ? _keepAwake.Acquire("Recording audio") : null;
            session = new RecordingSession(entry.Id, folderPath, streams, _library, _disk, _recycleBin, keepAwake, _time,
                _loggerFactory.CreateLogger<RecordingSession>());
            session.Changed += OnSessionChanged;
            session.EndedUnexpectedly += OnSessionChanged;
            sources.Clear(); // ownership moved to the streams
            Volatile.Write(ref _current, session);
            try
            {
                SessionStarted?.Invoke(this, session);
            }
#pragma warning disable CA1031 // Optional live consumers must never prevent a recording from starting.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLiveConsumerFailed(ex);
            }

            session.Start();
            var sourceSummary = string.Join(", ", streams.Select(s => $"{s.Request.Kind}:{s.DisplayName}"));
            LogStarted(entry.Id, sourceSummary);
            RaiseChanged(session);
            return session;
        }
        finally
        {
            foreach (var (_, source) in sources)
            {
                source.Dispose();
            }

            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while no recording can be starting, so startup crash recovery never mistakes
    /// a recording that is just being created for an abandoned one.
    /// </summary>
    internal async Task<T> ExcludingStartsAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task PauseAsync() => Current?.PauseAsync() ?? Task.CompletedTask;

    public Task ResumeAsync() => Current?.ResumeAsync() ?? Task.CompletedTask;

    public Task StopAsync() => Current?.StopAsync() ?? Task.CompletedTask;

    public Task CancelAsync() => Current?.CancelAsync() ?? Task.CompletedTask;

    /// <summary>Global hotkey / tray behavior: start with the given options when idle, stop when recording.</summary>
    public async Task ToggleAsync(Func<RecordingOptions> defaults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (Current is { } session)
        {
            await session.StopAsync().ConfigureAwait(false);
        }
        else
        {
            await StartAsync(defaults(), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _power.Suspending -= OnSuspending;
        _power.Resumed -= OnResumed;
        _gate.Dispose();
    }

    /// <summary>
    /// Undoes a start that failed before any audio was captured: closes the empty audio files Kakitome just created,
    /// deletes them only if they hold no audio, and removes the folder only if nothing else is in it.
    /// </summary>
    private async Task AbandonStartAsync(Kakitome.Domain.Recordings.RecordingId id, List<StreamRecorder> streams)
    {
        foreach (var stream in streams)
        {
            var path = stream.Writer.Path;
            var empty = stream.Writer.FramesWritten == 0;
            stream.Writer.Dispose();
            if (empty && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        try
        {
            await _library.DiscardUnusedRecordingAsync(id).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort cleanup; the original start failure is what the caller must see.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAbandonFailed(ex, id);
        }
    }

    private void EnsureDiskSpace()
    {
        var available = _disk.GetAvailableBytes(_library.LibraryRoot);
        if (available < RecordingLimits.MinFreeBytesToStart)
        {
            throw new RecordingStartException(
                RecordingStartFailure.InsufficientDiskSpace,
                $"Not enough free disk space to record safely ({available / (1024 * 1024)} MB free).");
        }
    }

    private static List<CaptureSourceRequest> BuildRequests(RecordingOptions options)
    {
        var requests = new List<CaptureSourceRequest>();
        if (options.IncludeMicrophone)
        {
            requests.Add(new CaptureSourceRequest(CaptureSourceKind.Microphone, options.MicrophoneId));
        }

        if (options.ApplicationProcessId is { } pid)
        {
            requests.Add(new CaptureSourceRequest(CaptureSourceKind.Application, ProcessId: pid, ProcessName: options.ApplicationName));
        }
        else if (options.IncludeSystemAudio)
        {
            requests.Add(new CaptureSourceRequest(CaptureSourceKind.SystemAudio, options.SystemAudioDeviceId));
        }

        return requests;
    }

    private static string StreamSuffix(CaptureSourceKind kind) => kind switch
    {
        CaptureSourceKind.Microphone => "mic",
        CaptureSourceKind.SystemAudio => "system",
        _ => "app",
    };

    private static AudioStreamRole RoleOf(CaptureSourceKind kind) => kind switch
    {
        CaptureSourceKind.Microphone => AudioStreamRole.Microphone,
        CaptureSourceKind.SystemAudio => AudioStreamRole.SystemAudio,
        _ => AudioStreamRole.Application,
    };

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (sender is not RecordingSession session)
        {
            return;
        }

        if (session.State is RecordingState.Stopped or RecordingState.Cancelled
            && Interlocked.CompareExchange(ref _current, null, session) == session
            && session.State == RecordingState.Stopped)
        {
            RecordingCompleted?.Invoke(this, session.Id);
        }

        RaiseChanged(session);
    }

    private void RaiseChanged(RecordingSession session)
    {
        var status = Current is null ? null : session.GetStatus();
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(status));
    }

    private void OnSuspending(object? sender, EventArgs e) => _ = Current?.SuspendAsync();

    private void OnResumed(object? sender, EventArgs e) => _ = Current?.WakeAsync();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clean up the unused recording folder for {Id}")]
    private partial void LogAbandonFailed(Exception ex, Kakitome.Domain.Recordings.RecordingId id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Id} started: {Sources}")]
    private partial void LogStarted(Kakitome.Domain.Recordings.RecordingId id, string sources);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live consumer failed to attach to the recording")]
    private partial void LogLiveConsumerFailed(Exception ex);
}
