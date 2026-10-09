using Kakitome.Application.Jobs;

namespace Kakitome.Tests.Jobs;

public sealed class ResourcePolicyTests
{
    private static readonly ResourceSnapshot Ac = ResourceSnapshot.Unconstrained with { BatteryPercent = 80 };
    private static readonly ResourceSnapshot Battery = Ac with { OnAcPower = false };

    [Theory]
    [InlineData(ProcessingMode.Auto, JobWaitReason.None)] // reduced budget, see Budget_…
    [InlineData(ProcessingMode.BatterySaver, JobWaitReason.OnBattery)]
    [InlineData(ProcessingMode.AlwaysProcess, JobWaitReason.None)]
    public void Heavy_work_on_battery_depends_on_mode(ProcessingMode mode, JobWaitReason expected) =>
        Assert.Equal(expected, ResourcePolicy.Evaluate(JobResourceClass.Heavy, Battery, mode));

    [Fact]
    public void Light_work_runs_on_battery_and_while_recording()
    {
        Assert.Equal(JobWaitReason.None, ResourcePolicy.Evaluate(JobResourceClass.Light, Battery, ProcessingMode.BatterySaver));
        Assert.Equal(JobWaitReason.None, ResourcePolicy.Evaluate(JobResourceClass.Light, Ac with { RecordingActive = true }, ProcessingMode.Auto));
    }

    [Fact]
    public void Auto_keeps_working_on_battery_until_it_runs_low()
    {
        Assert.Equal(JobWaitReason.None, ResourcePolicy.Evaluate(JobResourceClass.Heavy, Battery with { BatteryPercent = 43 }, ProcessingMode.Auto));
        Assert.Equal(JobWaitReason.LowBattery,
            ResourcePolicy.Evaluate(JobResourceClass.Heavy, Battery with { BatteryPercent = ResourcePolicy.AutoBatteryMinPercent - 1 }, ProcessingMode.Auto));
        Assert.Equal(JobWaitReason.EnergySaver, ResourcePolicy.Evaluate(JobResourceClass.Heavy, Battery with { EnergySaverOn = true }, ProcessingMode.Auto));
        Assert.True(ResourcePolicy.Budget(Battery, ProcessingMode.Auto, 12).PreferEfficiency);
    }

    [Fact]
    public void Critical_battery_stops_heavy_work_even_in_always_process()
    {
        var critical = Battery with { BatteryPercent = ResourcePolicy.CriticalBatteryPercent };
        Assert.Equal(JobWaitReason.LowBattery, ResourcePolicy.Evaluate(JobResourceClass.Heavy, critical, ProcessingMode.AlwaysProcess));
    }

    [Fact]
    public void Recording_reliability_outranks_processing()
    {
        Assert.Equal(JobWaitReason.RecordingInProgress,
            ResourcePolicy.Evaluate(JobResourceClass.Heavy, Ac with { RecordingActive = true }, ProcessingMode.AlwaysProcess));
    }

    [Theory]
    [InlineData(ProcessingMode.Auto, JobWaitReason.EnergySaver)]
    [InlineData(ProcessingMode.BatterySaver, JobWaitReason.EnergySaver)]
    [InlineData(ProcessingMode.AlwaysProcess, JobWaitReason.None)]
    public void Energy_saver_defers_heavy_work(ProcessingMode mode, JobWaitReason expected) =>
        Assert.Equal(expected, ResourcePolicy.Evaluate(JobResourceClass.Heavy, Ac with { EnergySaverOn = true }, mode));

    [Fact]
    public void Battery_saver_mode_waits_for_a_charged_battery_on_AC()
    {
        Assert.Equal(JobWaitReason.LowBattery,
            ResourcePolicy.Evaluate(JobResourceClass.Heavy, Ac with { BatteryPercent = 30 }, ProcessingMode.BatterySaver));
        Assert.Equal(JobWaitReason.None,
            ResourcePolicy.Evaluate(JobResourceClass.Heavy, Ac with { BatteryPercent = 30 }, ProcessingMode.Auto));
    }

    [Fact]
    public void Pressure_signals_defer_heavy_starts_but_do_not_preempt()
    {
        var busy = Ac with { CpuLoadOthers = 0.95 };
        var lowMemory = Ac with { AvailableMemoryBytes = 512L * 1024 * 1024 };

        Assert.Equal(JobWaitReason.SystemBusy, ResourcePolicy.Evaluate(JobResourceClass.Heavy, busy, ProcessingMode.Auto));
        Assert.Equal(JobWaitReason.LowMemory, ResourcePolicy.Evaluate(JobResourceClass.Heavy, lowMemory, ProcessingMode.Auto));
        Assert.False(ResourcePolicy.Preempts(JobWaitReason.SystemBusy));
        Assert.False(ResourcePolicy.Preempts(JobWaitReason.LowMemory));
        Assert.True(ResourcePolicy.Preempts(JobWaitReason.OnBattery));
        Assert.True(ResourcePolicy.Preempts(JobWaitReason.RecordingInProgress));
    }

    [Fact]
    public void Low_disk_pauses_all_jobs()
    {
        var full = Ac with { LibraryFreeBytes = ResourcePolicy.MinFreeDiskBytes - 1 };
        Assert.Equal(JobWaitReason.LowDiskSpace, ResourcePolicy.Evaluate(JobResourceClass.Light, full, ProcessingMode.AlwaysProcess));
    }

    [Fact]
    public void Budget_leaves_headroom_and_prefers_efficiency_on_battery()
    {
        var ac = ResourcePolicy.Budget(Ac, ProcessingMode.Auto, 16);
        var battery = ResourcePolicy.Budget(Battery, ProcessingMode.AlwaysProcess, 16);
        var small = ResourcePolicy.Budget(Ac, ProcessingMode.Auto, 2);

        Assert.Equal(8, ac.MaxThreads);
        Assert.False(ac.PreferEfficiency);
        Assert.Equal(4, battery.MaxThreads);
        Assert.True(battery.PreferEfficiency);
        Assert.Equal(1, small.MaxThreads);
    }
}
