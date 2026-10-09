namespace Kakitome.Application.Recording;

/// <summary>Canonical capture format: 32-bit float PCM, interleaved (docs/03 "lossless floating-point PCM").</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public const int BytesPerSample = sizeof(float);

    /// <summary>Microphone streams: 48 kHz mono (speech; the OS converts from the device's native format).</summary>
    public static AudioFormat Microphone { get; } = new(48_000, 1);

    /// <summary>System/application audio: 48 kHz stereo.</summary>
    public static AudioFormat Loopback { get; } = new(48_000, 2);

    public int BytesPerFrame => Channels * BytesPerSample;

    public int BytesPerSecond => SampleRate * BytesPerFrame;
}

public enum CaptureSourceKind
{
    Microphone,
    SystemAudio,
    Application,
}

/// <summary>An audio endpoint the user can choose.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <param name="IsPlaying">The application is producing sound right now.</param>
public sealed record AudioApplicationInfo(int ProcessId, string Name, bool IsPlaying);

/// <summary>What to capture for one stream.</summary>
/// <param name="DeviceId">Endpoint id; null means "the current default device".</param>
/// <param name="ProcessId">Target process for <see cref="CaptureSourceKind.Application"/> capture.</param>
public sealed record CaptureSourceRequest(CaptureSourceKind Kind, string? DeviceId = null, int? ProcessId = null, string? ProcessName = null);

/// <summary>Enumerates capture (microphone) and render (for system-audio loopback) endpoints.</summary>
public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> GetMicrophones();

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>True when per-application loopback capture is available on this OS.</summary>
    bool IsApplicationCaptureSupported { get; }

    /// <summary>Applications that currently have an audio session (candidates for per-application capture).</summary>
    IReadOnlyList<AudioApplicationInfo> GetAudioApplications() => [];

    /// <summary>Raised (on an arbitrary thread) when devices are added, removed or the default changes.</summary>
    event EventHandler? DevicesChanged;
}

/// <summary>
/// Receives captured audio. Called on the real-time capture thread: implementations must only copy data and
/// return; no I/O, locks held for long, or allocations in steady state.
/// </summary>
public interface IAudioSampleSink
{
    /// <param name="interleavedFloat32">Samples in the source's canonical <see cref="AudioFormat"/>.</param>
    void OnSamples(ReadOnlySpan<byte> interleavedFloat32);
}

/// <summary>A running capture of one endpoint/process, already converted to its canonical format.</summary>
public interface IAudioCaptureSource : IDisposable
{
    CaptureSourceKind Kind { get; }

    /// <summary>User-visible name of the device or application being captured.</summary>
    string DisplayName { get; }

    /// <summary>Endpoint id actually in use (null for application capture).</summary>
    string? DeviceId { get; }

    AudioFormat Format { get; }

    void StartCapture(IAudioSampleSink sink);

    void StopCapture();

    /// <summary>Raised once when capture stops unexpectedly (device removed/invalidated, driver error).</summary>
    event EventHandler<CaptureFaultedEventArgs>? Faulted;
}

public sealed class CaptureFaultedEventArgs(Exception? error, bool deviceLost) : EventArgs
{
    public Exception? Error { get; } = error;

    /// <summary>True when the endpoint disappeared (unplugged, disabled); reconnecting may succeed later.</summary>
    public bool DeviceLost { get; } = deviceLost;
}

public interface IAudioCaptureFactory
{
    /// <summary>Opens a capture for the request. Throws <see cref="AudioDeviceUnavailableException"/> when not available.</summary>
    Task<IAudioCaptureSource> CreateAsync(CaptureSourceRequest request, AudioFormat format, CancellationToken cancellationToken = default);
}

public sealed class AudioDeviceUnavailableException : Exception
{
    public AudioDeviceUnavailableException()
    {
    }

    public AudioDeviceUnavailableException(string message)
        : base(message)
    {
    }

    public AudioDeviceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
