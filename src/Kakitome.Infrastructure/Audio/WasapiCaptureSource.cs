using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Kakitome.Application.Recording;

namespace Kakitome.Infrastructure.Audio;

/// <summary>
/// Event-driven shared-mode WASAPI capture on a dedicated MMCSS ("Audio") thread. Windows converts the device's
/// native format to the requested canonical float format (AUTOCONVERTPCM + SRC_DEFAULT_QUALITY), so the same code
/// serves microphones, system loopback and per-process loopback.
/// </summary>
internal sealed partial class WasapiCaptureSource : IAudioCaptureSource
{
    private const long BufferDuration100Ns = 1_000_000; // 100 ms
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly AudioClient _client;
    private readonly MMDevice? _device;
    private readonly AutoResetEvent _bufferReady = new(false);
    private byte[] _silence = new byte[48_000 * 2 * sizeof(float) / 10];
    private Thread? _thread;
    private IAudioSampleSink? _sink;
    private volatile bool _stopRequested;
    private int _faultRaised;
    private bool _disposed;

    private WasapiCaptureSource(CaptureSourceKind kind, AudioClient client, MMDevice? device, string displayName, AudioFormat format)
    {
        Kind = kind;
        _client = client;
        _device = device;
        DisplayName = displayName;
        DeviceId = device?.ID;
        Format = format;
    }

    public CaptureSourceKind Kind { get; }

    public string DisplayName { get; }

    public string? DeviceId { get; }

    public AudioFormat Format { get; }

    public event EventHandler<CaptureFaultedEventArgs>? Faulted;

    /// <summary>Opens and initializes the endpoint. Must be called on an MTA thread.</summary>
    internal static async Task<WasapiCaptureSource> OpenAsync(CaptureSourceRequest request, AudioFormat format)
    {
        MMDevice? device = null;
        AudioClient? client = null;
        try
        {
            string name;
            var loopback = request.Kind != CaptureSourceKind.Microphone;
            if (request.Kind == CaptureSourceKind.Application)
            {
                var pid = request.ProcessId ?? throw new AudioDeviceUnavailableException("No application was selected.");
                client = await AudioClient.ActivateProcessLoopbackAsync((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree).ConfigureAwait(false);
                name = request.ProcessName ?? $"PID {pid}";
            }
            else
            {
                device = ResolveDevice(request);
                client = device.CreateAudioClient();
                name = device.FriendlyName;
            }

            var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            if (loopback)
            {
                flags |= AudioClientStreamFlags.Loopback;
            }

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration100Ns, 0, waveFormat, Guid.Empty);
            return new WasapiCaptureSource(request.Kind, client, device, name, format);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            client?.Dispose();
            device?.Dispose();
            throw new AudioDeviceUnavailableException(Describe(request, ex), ex);
        }
    }

    public void StartCapture(IAudioSampleSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            throw new InvalidOperationException("Capture already started.");
        }

        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _client.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = $"Kakitome capture ({Kind})",
        };
        _thread.Start();
    }

    public void StopCapture()
    {
        _stopRequested = true;
        _bufferReady.Set();
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopCapture();
        _disposed = true;
        _client.Dispose();
        _device?.Dispose();
        _bufferReady.Dispose();
    }

    private void CaptureLoop()
    {
        var mmcss = NativeMethods.EnterAudioThread();
        try
        {
            _client.Start();
            var capture = _client.AudioCaptureClient;
            while (!_stopRequested)
            {
                // Also poll on timeout: an idle loopback stream signals nothing, and polling surfaces device loss.
                _bufferReady.WaitOne(PollInterval);
                if (_stopRequested)
                {
                    break;
                }

                Drain(capture);
            }
        }
        catch (COMException ex)
        {
            RaiseFaulted(ex, deviceLost: true);
        }
        catch (InvalidOperationException ex)
        {
            RaiseFaulted(ex, deviceLost: false);
        }
        finally
        {
            try
            {
                _client.Stop();
            }
            catch (COMException)
            {
                // The device is already gone.
            }

            NativeMethods.LeaveAudioThread(mmcss);
        }
    }

    private unsafe void Drain(AudioCaptureClient capture)
    {
        var bytesPerFrame = Format.BytesPerFrame;
        int packet;
        while ((packet = capture.GetNextPacketSize()) > 0)
        {
            var data = capture.GetBuffer(out var frames, out var flags);
            var bytes = frames * bytesPerFrame;
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                if (_silence.Length < bytes)
                {
                    _silence = new byte[bytes];
                }

                _sink!.OnSamples(_silence.AsSpan(0, bytes));
            }
            else
            {
                _sink!.OnSamples(new ReadOnlySpan<byte>((void*)data, bytes));
            }

            capture.ReleaseBuffer(frames);
        }
    }

    private void RaiseFaulted(Exception ex, bool deviceLost)
    {
        if (!_stopRequested && Interlocked.Exchange(ref _faultRaised, 1) == 0)
        {
            Faulted?.Invoke(this, new CaptureFaultedEventArgs(ex, deviceLost));
        }
    }

    private static MMDevice ResolveDevice(CaptureSourceRequest request)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = request.Kind == CaptureSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
        MMDevice device;
        if (request.DeviceId is { } id)
        {
            device = enumerator.GetDevice(id);
        }
        else if (!enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out device))
        {
            throw new AudioDeviceUnavailableException(flow == DataFlow.Capture ? "No microphone is available." : "No audio output device is available.");
        }

        if (device.State != DeviceState.Active)
        {
            var name = device.FriendlyName;
            device.Dispose();
            throw new AudioDeviceUnavailableException($"'{name}' is not connected.");
        }

        return device;
    }

    private static string Describe(CaptureSourceRequest request, Exception ex) => request.Kind switch
    {
        CaptureSourceKind.Microphone => $"The microphone could not be opened: {ex.Message}",
        CaptureSourceKind.SystemAudio => $"System audio could not be captured: {ex.Message}",
        _ => $"The application's audio could not be captured: {ex.Message}",
    };

    private static partial class NativeMethods
    {
        public static IntPtr EnterAudioThread()
        {
            var taskIndex = 0;
            return AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
        }

        public static void LeaveAudioThread(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(handle);
            }
        }

        [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

        [LibraryImport("avrt.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }
}

/// <summary>Opens WASAPI captures off the UI thread (Core Audio objects are created in the MTA).</summary>
public sealed class WasapiCaptureFactory : IAudioCaptureFactory
{
    public Task<IAudioCaptureSource> CreateAsync(CaptureSourceRequest request, AudioFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run<IAudioCaptureSource>(async () => await WasapiCaptureSource.OpenAsync(request, format).ConfigureAwait(false), cancellationToken);
    }
}
