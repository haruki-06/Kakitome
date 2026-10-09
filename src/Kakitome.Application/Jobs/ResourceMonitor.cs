using Kakitome.Application.Library;
using Kakitome.Application.Recording;

namespace Kakitome.Application.Jobs;

/// <summary>OS-level signals (power, Energy Saver, CPU, memory). Implemented per platform.</summary>
public interface ISystemResourceProbe
{
    SystemResources Current { get; }

    /// <summary>Raised on AC/battery, Energy Saver or battery-level transitions.</summary>
    event EventHandler? Changed;
}

/// <param name="CpuLoadOthers">0..1 system CPU load excluding this process, smoothed.</param>
public sealed record SystemResources(bool OnAcPower, int? BatteryPercent, bool EnergySaverOn, double CpuLoadOthers, long AvailableMemoryBytes);

/// <summary>Combines OS signals with app state (free space where the Library lives, active recording).</summary>
public sealed class ResourceMonitor : IResourceMonitor
{
    private readonly ISystemResourceProbe _system;
    private readonly IDiskSpaceProbe _disk;
    private readonly LibraryService _library;
    private readonly RecordingService _recording;

    public ResourceMonitor(ISystemResourceProbe system, IDiskSpaceProbe disk, LibraryService library, RecordingService recording)
    {
        _system = system;
        _disk = disk;
        _library = library;
        _recording = recording;
        _system.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        // Starting or stopping a recording immediately changes what may run.
        _recording.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public ResourceSnapshot Current
    {
        get
        {
            var s = _system.Current;
            long free;
            try
            {
                free = _disk.GetAvailableBytes(_library.LibraryRoot);
            }
            catch (IOException)
            {
                free = long.MaxValue;
            }

            return new ResourceSnapshot(s.OnAcPower, s.BatteryPercent, s.EnergySaverOn, s.CpuLoadOthers, s.AvailableMemoryBytes, free,
                _recording.Current is not null);
        }
    }
}
