namespace Kakitome.Application.Jobs;

/// <summary>User-selectable background processing mode (docs/06 "Battery / thermal behavior").</summary>
public enum ProcessingMode
{
    /// <summary>Normal on AC; heavy work deferred on battery or in Energy Saver.</summary>
    Auto = 0,

    /// <summary>Heavy work also on battery; still stops at critically low battery.</summary>
    AlwaysProcess = 1,

    /// <summary>Aggressive deferral: heavy work only on AC, outside Energy Saver, with a well-charged battery.</summary>
    BatterySaver = 2,
}

/// <summary>Point-in-time system signals used for scheduling.</summary>
/// <param name="CpuLoadOthers">System CPU load (0..1) excluding Kakitome's own work, smoothed.</param>
public sealed record ResourceSnapshot(
    bool OnAcPower,
    int? BatteryPercent,
    bool EnergySaverOn,
    double CpuLoadOthers,
    long AvailableMemoryBytes,
    long LibraryFreeBytes,
    bool RecordingActive)
{
    /// <summary>A desktop on AC with plenty of everything (tests, unknown platforms).</summary>
    public static ResourceSnapshot Unconstrained { get; } =
        new(true, null, false, 0, 16L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, false);
}

/// <summary>Supplies <see cref="ResourceSnapshot"/>s; raises <see cref="Changed"/> on power/battery transitions.</summary>
public interface IResourceMonitor
{
    ResourceSnapshot Current { get; }

    event EventHandler? Changed;
}

/// <summary>
/// Pure decision rules: may a job of a given class run now, and with what budget. Laptop-first: recording
/// reliability and foreground responsiveness outrank background throughput.
/// </summary>
public static class ResourcePolicy
{
    public const long MinFreeDiskBytes = 1L * 1024 * 1024 * 1024;
    public const long MinAvailableMemoryForHeavy = 1536L * 1024 * 1024;
    public const double BusyCpuThreshold = 0.85;
    public const int CriticalBatteryPercent = 15;
    public const int BatterySaverMinPercent = 50;

    public static JobWaitReason Evaluate(JobResourceClass resourceClass, ResourceSnapshot s, ProcessingMode mode)
    {
        ArgumentNullException.ThrowIfNull(s);

        // Jobs write outputs; pause them before the disk fills (docs/06 low-disk order).
        if (s.LibraryFreeBytes < MinFreeDiskBytes)
        {
            return JobWaitReason.LowDiskSpace;
        }

        if (resourceClass == JobResourceClass.Light)
        {
            return JobWaitReason.None;
        }

        if (s.RecordingActive)
        {
            return JobWaitReason.RecordingInProgress;
        }

        var onBattery = !s.OnAcPower;
        if (onBattery && s.BatteryPercent is { } critical && critical <= CriticalBatteryPercent)
        {
            return JobWaitReason.LowBattery;
        }

        switch (mode)
        {
            case ProcessingMode.Auto:
                if (onBattery)
                {
                    return JobWaitReason.OnBattery;
                }

                if (s.EnergySaverOn)
                {
                    return JobWaitReason.EnergySaver;
                }

                break;
            case ProcessingMode.BatterySaver:
                if (onBattery)
                {
                    return JobWaitReason.OnBattery;
                }

                if (s.EnergySaverOn)
                {
                    return JobWaitReason.EnergySaver;
                }

                if (s.BatteryPercent is { } percent && percent < BatterySaverMinPercent)
                {
                    return JobWaitReason.LowBattery;
                }

                break;
        }

        if (s.AvailableMemoryBytes < MinAvailableMemoryForHeavy)
        {
            return JobWaitReason.LowMemory;
        }

        if (s.CpuLoadOthers > BusyCpuThreshold)
        {
            return JobWaitReason.SystemBusy;
        }

        return JobWaitReason.None;
    }

    /// <summary>
    /// Reasons that stop an already running job (not just new starts). Momentary CPU/RAM spikes only block new
    /// starts, so a job is not thrashed by a brief burst of foreground activity.
    /// </summary>
    public static bool Preempts(JobWaitReason reason) => reason is JobWaitReason.LowDiskSpace
        or JobWaitReason.RecordingInProgress or JobWaitReason.LowBattery or JobWaitReason.OnBattery or JobWaitReason.EnergySaver;

    public static ResourceBudget Budget(ResourceSnapshot s, ProcessingMode mode, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(s);
        var efficient = !s.OnAcPower || s.EnergySaverOn || mode == ProcessingMode.BatterySaver;

        // Always leave headroom for the foreground app and the audio thread.
        var threads = efficient ? processorCount / 4 : processorCount - 2;
        return new ResourceBudget(Math.Clamp(threads, 1, 8), efficient);
    }
}
