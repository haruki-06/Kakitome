using System.Diagnostics;
using System.Runtime.InteropServices;
using Kakitome.Application.Jobs;

namespace Kakitome.Infrastructure.Power;

/// <summary>
/// Samples power status (GetSystemPowerStatus: AC line, battery %, Energy Saver), memory (GlobalMemoryStatusEx)
/// and CPU load excluding Kakitome itself (GetSystemTimes minus this process' CPU time) every few seconds.
/// Windows exposes no unprivileged thermal sensor API; Energy Saver (which Windows engages under battery/thermal
/// pressure) and sustained CPU load act as the thermal proxy (ADR-021).
/// </summary>
public sealed partial class WindowsSystemResourceProbe : ISystemResourceProbe, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

    /// <summary>Exponential smoothing factor for CPU load (≈ 15 s time constant at 3 s samples).</summary>
    private const double Smoothing = 0.2;

    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private readonly Process _self = Process.GetCurrentProcess();
    private SystemResources _current;
    private ulong _lastIdle;
    private ulong _lastTotal;
    private TimeSpan _lastSelfCpu;
    private double _cpuOthers;

    public WindowsSystemResourceProbe()
    {
        SampleCpu(prime: true);
        _current = Sample();
        _timer = new Timer(_ => Tick(), null, SampleInterval, SampleInterval);
    }

    public SystemResources Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        _timer.Dispose();
        _self.Dispose();
    }

    private void Tick()
    {
        SystemResources previous;
        SystemResources next;
        lock (_lock)
        {
            previous = _current;
            SampleCpu(prime: false);
            next = Sample();
            _current = next;
        }

        if (previous.OnAcPower != next.OnAcPower
            || previous.EnergySaverOn != next.EnergySaverOn
            || (previous.BatteryPercent ?? 100) / 5 != (next.BatteryPercent ?? 100) / 5
            || (previous.CpuLoadOthers > ResourcePolicy.BusyCpuThreshold) != (next.CpuLoadOthers > ResourcePolicy.BusyCpuThreshold))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private SystemResources Sample()
    {
        var onAc = true;
        int? battery = null;
        var energySaver = false;
        if (GetSystemPowerStatus(out var power))
        {
            // ACLineStatus: 0 offline, 1 online, 255 unknown. BatteryFlag 128 = no system battery.
            onAc = power.ACLineStatus != 0;
            if (power.BatteryFlag != 128 && power.BatteryLifePercent <= 100)
            {
                battery = power.BatteryLifePercent;
            }

            energySaver = power.SystemStatusFlag == 1;
        }

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var available = GlobalMemoryStatusEx(ref memory) ? (long)memory.AvailPhys : long.MaxValue;
        return new SystemResources(onAc, battery, energySaver, _cpuOthers, available);
    }

    private void SampleCpu(bool prime)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return;
        }

        _self.Refresh();
        var selfCpu = _self.TotalProcessorTime;
        var idleTicks = idle.Value;
        var totalTicks = kernel.Value + user.Value; // kernel time includes idle time
        if (!prime && totalTicks > _lastTotal)
        {
            var total = (double)(totalTicks - _lastTotal);
            var busy = total - (idleTicks - _lastIdle);
            var self = (selfCpu - _lastSelfCpu).Ticks; // 100 ns, summed over all cores like GetSystemTimes
            var others = Math.Clamp((busy - self) / total, 0, 1);
            _cpuOthers = (_cpuOthers * (1 - Smoothing)) + (others * Smoothing);
        }

        _lastIdle = idleTicks;
        _lastTotal = totalTicks;
        _lastSelfCpu = selfCpu;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}
