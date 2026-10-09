using System.Globalization;
using Kakitome.Application.Diagnostics;
using Kakitome.Infrastructure.Asr;
using Microsoft.Win32;

namespace Kakitome.Infrastructure.Diagnostics;

/// <summary>CPU name, Windows build and the GPUs local models can see, for the diagnostics report.</summary>
public sealed class HardwareDiagnostics : IDiagnosticsSource
{
    public string Name => "Hardware";

    public IEnumerable<string> Describe()
    {
        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            yield return $"CPU: {(cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown"}";
        }

        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"Windows: {nt?.GetValue("ProductName")} {nt?.GetValue("DisplayVersion")} (build {nt?.GetValue("CurrentBuild")}.{nt?.GetValue("UBR")})");
        }

        var probe = new GpuAccelerationProbe();
        if (probe.Devices.Count == 0)
        {
            yield return "GPU (Vulkan): none found";
        }

        foreach (var d in probe.Devices)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"GPU (Vulkan) {d.Index}: {d.Name}, {d.Type}, {d.MemoryBytes / (1024 * 1024)} MB{(probe.Gpu == d ? " — used for local models" : string.Empty)}");
        }
    }
}
