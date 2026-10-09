using Kakitome.Infrastructure.Power;

namespace Kakitome.Tests.Jobs;

[Trait("Category", "Hardware")]
public sealed class SystemResourceProbeTests
{
    [Fact]
    public async Task Windows_probe_reports_plausible_values()
    {
        using var probe = new WindowsSystemResourceProbe();
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken); // one CPU sample interval

        var r = probe.Current;
        Assert.InRange(r.CpuLoadOthers, 0, 1);
        Assert.True(r.AvailableMemoryBytes > 64L * 1024 * 1024);
        Assert.True(r.BatteryPercent is null or >= 0 and <= 100);
        // A machine without a battery is always on AC.
        Assert.True(r.BatteryPercent is not null || r.OnAcPower);
    }
}
