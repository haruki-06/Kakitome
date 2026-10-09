using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Kakitome.Application.Diagnostics;

namespace Kakitome.Infrastructure.Diagnostics;

/// <summary>
/// Kakitome's crashes and hangs as Windows recorded them (Application log: Application Error 1000, Application Hang
/// 1002, .NET Runtime 1026, Windows Error Reporting 1001 for APPCRASH/AppHang). A crash inside native code (audio driver, model runtime, XAML) ends the process before
/// Kakitome can log anything; these entries name the faulting module. Field test, 2026-10-09: the app ended right
/// after a URL import with nothing in its own log.
/// </summary>
public sealed class WindowsErrorReports : IDiagnosticsSource
{
    public const int Days = 14;
    public const int MaxEntries = 15;

    public string Name => "Windows error reports (Kakitome, last 14 days)";

    public IEnumerable<string> Describe()
    {
        var since = DateTime.UtcNow.AddDays(-Days).ToString("o", CultureInfo.InvariantCulture);
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002) "
            + "or (Provider[@Name='.NET Runtime'] and EventID=1026) "
            + $"or (Provider[@Name='Windows Error Reporting'] and EventID=1001)][TimeCreated[@SystemTime>='{since}']]]")
        {
            ReverseDirection = true,
        };

        var lines = new List<string>();
        var found = 0;
        using (var reader = new EventLogReader(query))
        {
            for (var record = reader.ReadEvent(); record is not null && found < MaxEntries; record = reader.ReadEvent())
            {
                using (record)
                {
                    var message = SafeMessage(record);
                    if (!message.Contains("Kakitome.exe", StringComparison.OrdinalIgnoreCase)
                        || (record.ProviderName == "Windows Error Reporting"
                            && !message.Contains("APPCRASH", StringComparison.OrdinalIgnoreCase)
                            && !message.Contains("AppHang", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    found++;
                    lines.Add($"--- {record.TimeCreated:yyyy-MM-dd HH:mm:ss} {record.ProviderName} {record.Id}");
                    lines.AddRange(message.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0).Take(30).Select(l => "  " + l.TrimEnd()));
                }
            }
        }

        if (found == 0)
        {
            lines.Add("none");
        }

        return lines;
    }

    private static string SafeMessage(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? string.Join(" | ", record.Properties.Select(p => p.Value));
        }
        catch (EventLogException)
        {
            return string.Join(" | ", record.Properties.Select(p => p.Value));
        }
    }
}
