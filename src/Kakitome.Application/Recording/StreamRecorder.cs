using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Kakitome.Domain.Library;

namespace Kakitome.Application.Recording;

public enum StreamState
{
    Active,
    Reconnecting,
    Stopped,
    Failed,
}

/// <summary>
/// Records one capture source to one file. The capture thread only copies samples into a queue; a single
/// writer task does all file I/O. Gaps (device loss, loopback silence) are filled with silence against the
/// session clock so independent streams stay time-aligned.
/// </summary>
#pragma warning disable CA1001 // _stopping is disposed at the end of StopAsync, the recorder's terminal operation.
internal sealed partial class StreamRecorder : IAudioSampleSink
#pragma warning restore CA1001
{
    /// <summary>A source that has delivered nothing for this long is considered idle and gets padded.</summary>
    internal static readonly TimeSpan IdleThreshold = TimeSpan.FromMilliseconds(400);

    /// <summary>After this long without the requested device, capture switches to the default device.</summary>
    internal static readonly TimeSpan FallbackToDefaultAfter = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(1);

    /// <summary>Padding stops this far short of the clock so late-arriving buffered audio is not doubled.</summary>
    private static readonly TimeSpan PaddingMargin = TimeSpan.FromMilliseconds(50);

    private readonly Channel<WriterCommand> _queue = Channel.CreateUnbounded<WriterCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly IAudioCaptureFactory _factory;
    private readonly TimeProvider _time;
    private readonly Action<StreamRecorder, CaptureEventKind, string?> _onEvent;
    private readonly ILogger _logger;
    private readonly Lock _sourceLock = new();
    private readonly CancellationTokenSource _stopping = new();

    private IAudioCaptureSource? _source;
    private Task? _writerLoop;
    private Task? _reconnectLoop;
    private volatile bool _paused;
    private volatile StreamState _state = StreamState.Active;
    private long _framesEnqueued;
    private long _lastDataTimestamp;
    private int _peakBits;
    private IAudioSampleSink? _tap;

    public StreamRecorder(
        CaptureSourceRequest request,
        IAudioCaptureSource source,
        IAudioFileWriter writer,
        IAudioCaptureFactory factory,
        TimeProvider time,
        Action<StreamRecorder, CaptureEventKind, string?> onEvent,
        ILogger logger)
    {
        Request = request;
        _source = source;
        Writer = writer;
        Format = source.Format;
        DisplayName = source.DisplayName;
        _factory = factory;
        _time = time;
        _onEvent = onEvent;
        _logger = logger;
    }

    public CaptureSourceRequest Request { get; }

    public IAudioFileWriter Writer { get; }

    public AudioFormat Format { get; }

    public string FileName => System.IO.Path.GetFileName(Writer.Path);

    /// <summary>Name of the device/application currently captured (changes after a device switch).</summary>
    public string DisplayName { get; private set; }

    public StreamState State => _state;

    /// <summary>First write/disk error; the session stops the recording when this is set.</summary>
    public Exception? WriteError { get; private set; }

    public long FramesEnqueued => Interlocked.Read(ref _framesEnqueued);

    public double DurationSeconds => (double)FramesEnqueued / Format.SampleRate;

    public void Start()
    {
        _lastDataTimestamp = _time.GetTimestamp();
        _writerLoop = Task.Run(WriterLoopAsync);
        StartSource(_source!);
    }

    public void SetPaused(bool paused) => _paused = paused;

    /// <summary>A live consumer (e.g. live transcript) receiving a copy of written audio; null detaches it.</summary>
    public void SetTap(IAudioSampleSink? tap) => Volatile.Write(ref _tap, tap);

    /// <summary>Peak absolute sample value since the last call (0..1), for level meters.</summary>
    public float ReadAndResetPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    /// <summary>Capture-thread callback: copy and enqueue only.</summary>
    public void OnSamples(ReadOnlySpan<byte> interleavedFloat32)
    {
        if (_paused || _stopping.IsCancellationRequested || interleavedFloat32.Length < Format.BytesPerFrame)
        {
            return;
        }

        var length = interleavedFloat32.Length - (interleavedFloat32.Length % Format.BytesPerFrame);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        interleavedFloat32[..length].CopyTo(buffer);
        if (!_queue.Writer.TryWrite(WriterCommand.Data(buffer, length)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return;
        }

        Interlocked.Add(ref _framesEnqueued, length / Format.BytesPerFrame);
        Volatile.Write(ref _lastDataTimestamp, _time.GetTimestamp());
        UpdatePeak(MemoryMarshal.Cast<byte, float>(interleavedFloat32[..length]));

        // The recording is already queued; a live consumer only copies (and drops when it cannot keep up).
        Volatile.Read(ref _tap)?.OnSamples(interleavedFloat32[..length]);
    }

    /// <summary>
    /// Periodic call from the session with the number of frames the session clock says should exist.
    /// Pads with silence only while the source is idle, so continuous capture is never altered.
    /// </summary>
    public void Tick(long expectedFrames)
    {
        if (_paused || _stopping.IsCancellationRequested)
        {
            return;
        }

        var idleFor = _time.GetElapsedTime(Volatile.Read(ref _lastDataTimestamp));
        if (_state == StreamState.Active && idleFor < IdleThreshold)
        {
            return;
        }

        var margin = (long)(PaddingMargin.TotalSeconds * Format.SampleRate);
        var deficit = expectedFrames - margin - FramesEnqueued;
        if (deficit > 0 && _queue.Writer.TryWrite(WriterCommand.Silence(deficit)))
        {
            Interlocked.Add(ref _framesEnqueued, deficit);
        }
    }

    public void RequestCheckpoint() => _queue.Writer.TryWrite(WriterCommand.Checkpoint);

    /// <summary>Stops capture, drains the queue and finalizes the file.</summary>
    public async Task StopAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        lock (_sourceLock)
        {
            StopSource(_source);
            _source = null;
        }

        if (_reconnectLoop is not null)
        {
            await _reconnectLoop.ConfigureAwait(false);
        }

        _queue.Writer.TryComplete();
        if (_writerLoop is not null)
        {
            await _writerLoop.ConfigureAwait(false);
        }

        try
        {
            Writer.Complete();
        }
        catch (IOException ex)
        {
            WriteError ??= ex;
            LogWriteFailed(ex, FileName);
        }
        finally
        {
            Writer.Dispose();
        }

        _state = WriteError is null ? StreamState.Stopped : StreamState.Failed;
        _stopping.Dispose();
    }

    private void StartSource(IAudioCaptureSource source)
    {
        source.Faulted += OnSourceFaulted;
        source.StartCapture(this);
    }

    private static void StopSource(IAudioCaptureSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.StopCapture();
        }
        finally
        {
            source.Dispose();
        }
    }

    private void OnSourceFaulted(object? sender, CaptureFaultedEventArgs e)
    {
        lock (_sourceLock)
        {
            if (_stopping.IsCancellationRequested || !ReferenceEquals(sender, _source))
            {
                return;
            }

            var lost = _source!;
            lost.Faulted -= OnSourceFaulted;
            _state = StreamState.Reconnecting;
            _source = null;

            // Never block or dispose on the capture thread that raised the fault.
            _reconnectLoop = Task.Run(async () =>
            {
                StopSource(lost);
                await ReconnectAsync().ConfigureAwait(false);
            });
        }

        LogSourceFaulted(e.Error, DisplayName, e.DeviceLost);
        _onEvent(this, CaptureEventKind.DeviceLost, e.Error?.Message);
    }

    private async Task ReconnectAsync()
    {
        var since = _time.GetTimestamp();
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconnectInterval, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var useDefault = Request.DeviceId is not null
                && Request.Kind != CaptureSourceKind.Application
                && _time.GetElapsedTime(since) >= FallbackToDefaultAfter;
            var request = useDefault ? Request with { DeviceId = null } : Request;

            IAudioCaptureSource candidate;
            try
            {
                candidate = await _factory.CreateAsync(request, Format, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Keep retrying; any failure means "not back yet".
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReconnectAttemptFailed(ex, DisplayName);
                continue;
            }

            lock (_sourceLock)
            {
                if (token.IsCancellationRequested)
                {
                    candidate.Dispose();
                    return;
                }

                _source = candidate;
                DisplayName = candidate.DisplayName;
                _state = StreamState.Active;
                StartSource(candidate);
            }

            var switched = Request.DeviceId is not null && !string.Equals(candidate.DeviceId, Request.DeviceId, StringComparison.Ordinal);
            _onEvent(this, switched ? CaptureEventKind.DeviceSwitched : CaptureEventKind.DeviceRestored, candidate.DisplayName);
            return;
        }
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var command in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (WriteError is null)
                {
                    switch (command.Kind)
                    {
                        case WriterCommandKind.Data:
                            Writer.Write(command.Buffer.AsSpan(0, command.Length));
                            break;
                        case WriterCommandKind.Silence:
                            Writer.WriteSilence(command.Frames);
                            break;
                        case WriterCommandKind.Checkpoint:
                            Writer.Checkpoint();
                            break;
                    }
                }
            }
            catch (IOException ex)
            {
                WriteError = ex;
                LogWriteFailed(ex, FileName);
            }
            finally
            {
                if (command.Buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(command.Buffer);
                }
            }
        }
    }

    private void UpdatePeak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = MathF.Abs(s);
            if (a > peak)
            {
                peak = a;
            }
        }

        var bits = BitConverter.SingleToInt32Bits(Math.Min(peak, 1f));
        int current;
        do
        {
            current = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(current) >= peak)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakBits, bits, current) != current);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture of {Source} stopped unexpectedly (device lost: {DeviceLost})")]
    private partial void LogSourceFaulted(Exception? ex, string source, bool deviceLost);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reconnect attempt for {Source} failed")]
    private partial void LogReconnectAttemptFailed(Exception ex, string source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing {File} failed")]
    private partial void LogWriteFailed(Exception ex, string file);

    private enum WriterCommandKind
    {
        Data,
        Silence,
        Checkpoint,
    }

    private readonly record struct WriterCommand(WriterCommandKind Kind, byte[]? Buffer, int Length, long Frames)
    {
        public static WriterCommand Checkpoint { get; } = new(WriterCommandKind.Checkpoint, null, 0, 0);

        public static WriterCommand Data(byte[] buffer, int length) => new(WriterCommandKind.Data, buffer, length, 0);

        public static WriterCommand Silence(long frames) => new(WriterCommandKind.Silence, null, 0, frames);
    }
}
