using System.Runtime.InteropServices;
using Kakitome.Application.Recording;

namespace Kakitome.Tests.TestSupport;

/// <summary>Scriptable capture device: tests push samples and trigger faults.</summary>
public sealed class FakeCaptureSource(CaptureSourceKind kind, string name, string? deviceId, AudioFormat format) : IAudioCaptureSource
{
    private IAudioSampleSink? _sink;

    public CaptureSourceKind Kind { get; } = kind;

    public string DisplayName { get; } = name;

    public string? DeviceId { get; } = deviceId;

    public AudioFormat Format { get; } = format;

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler<CaptureFaultedEventArgs>? Faulted;

    public void StartCapture(IAudioSampleSink sink)
    {
        _sink = sink;
        Started = true;
    }

    public void StopCapture() => Started = false;

    public void Dispose()
    {
        Started = false;
        Disposed = true;
    }

    /// <summary>Delivers <paramref name="frames"/> frames of a constant value, as the capture thread would.</summary>
    public void Push(int frames, float value = 0.25f)
    {
        if (!Started)
        {
            return;
        }

        var samples = new float[frames * Format.Channels];
        Array.Fill(samples, value);
        _sink!.OnSamples(MemoryMarshal.AsBytes(samples.AsSpan()));
    }

    public void PushSeconds(double seconds, float value = 0.25f) => Push((int)(seconds * Format.SampleRate), value);

    public void Fail() => Faulted?.Invoke(this, new CaptureFaultedEventArgs(new IOException("device removed"), deviceLost: true));
}

public sealed class FakeCaptureFactory : IAudioCaptureFactory
{
    private readonly Lock _lock = new();

    /// <summary>Device ids that currently "exist" (null key = a default device exists).</summary>
    public HashSet<string?> Available { get; } = [null, "mic-usb", "speakers"];

    public List<FakeCaptureSource> Created { get; } = [];

    public FakeCaptureSource Latest(CaptureSourceKind kind)
    {
        lock (_lock)
        {
            return Created.Last(c => c.Kind == kind);
        }
    }

    public Task<IAudioCaptureSource> CreateAsync(CaptureSourceRequest request, AudioFormat format, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!Available.Contains(request.DeviceId))
            {
                throw new AudioDeviceUnavailableException($"{request.DeviceId ?? "default"} is not connected");
            }

            var id = request.DeviceId ?? (request.Kind == CaptureSourceKind.Microphone ? "mic-default" : "speakers-default");
            var name = request.Kind switch
            {
                CaptureSourceKind.Application => request.ProcessName ?? "app",
                _ => request.DeviceId is null ? $"Default {request.Kind}" : $"Device {request.DeviceId}",
            };
            var source = new FakeCaptureSource(request.Kind, name, request.Kind == CaptureSourceKind.Application ? null : id, format);
            Created.Add(source);
            return Task.FromResult<IAudioCaptureSource>(source);
        }
    }
}

public sealed class FakeKeepAwake : IKeepAwake
{
    public int Held { get; private set; }

    public IDisposable Acquire(string reason)
    {
        Held++;
        return new Release(this);
    }

    private sealed class Release(FakeKeepAwake owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                owner.Held--;
                _done = true;
            }
        }
    }
}

public sealed class FakePowerEvents : IPowerEvents
{
    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Suspend() => Suspending?.Invoke(this, EventArgs.Empty);

    public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);
}

public sealed class FakeDiskSpace : IDiskSpaceProbe
{
    public long AvailableBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    public long GetAvailableBytes(string path) => AvailableBytes;
}

public sealed class FakeRecycleBin : IRecycleBin
{
    public bool Works { get; set; } = true;

    public List<string> Recycled { get; } = [];

    public bool TryMoveToRecycleBin(string folderPath)
    {
        if (!Works)
        {
            return false;
        }

        // Simulate the Recycle Bin by moving the folder aside (tests can still inspect it).
        var target = folderPath + ".recycled";
        Directory.Move(folderPath, target);
        Recycled.Add(target);
        return true;
    }
}

public sealed class FakeSystemResources : Kakitome.Application.Jobs.ISystemResourceProbe
{
    public Kakitome.Application.Jobs.SystemResources Current { get; private set; } =
        new(OnAcPower: true, BatteryPercent: 80, EnergySaverOn: false, CpuLoadOthers: 0.1, AvailableMemoryBytes: 8L * 1024 * 1024 * 1024);

    public event EventHandler? Changed;

    public void Set(Kakitome.Application.Jobs.SystemResources value)
    {
        Current = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Func<Kakitome.Application.Jobs.SystemResources, Kakitome.Application.Jobs.SystemResources> change) => Set(change(Current));
}

public sealed class NoGpu : Kakitome.Application.Asr.IAccelerationProbe
{
    public bool HasCapableGpu => false;

    public long GpuMemoryBytes => 0;

    /// <summary>8 GB: below the summary model recommendation.</summary>
    public long TotalMemoryBytes { get; set; } = 8L * 1024 * 1024 * 1024;
}

public sealed class FakeNetworkCost : Kakitome.Application.Models.INetworkCostProbe
{
    public bool IsMetered { get; set; }
}

/// <summary>Returns the turns a test sets; unavailable (the step is skipped) until a test enables it.</summary>
