using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Maintenance;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Application.Updates;

namespace Kakitome.Application.Diagnostics;

/// <summary>Platform details for the diagnostics report (CPU and GPU names, OS build…), provided by the infrastructure.</summary>
public interface IDiagnosticsSource
{
    /// <summary>Section title in <c>system.txt</c>.</summary>
    string Name { get; }

    IEnumerable<string> Describe();
}

/// <summary>
/// One zip to attach to an issue or send back after a field test (ADR-041): the log files, the PC (OS, CPU, memory,
/// GPU, power, free space, audio devices), app version, settings, installed models and the recent processing history.
/// Never included: audio, transcripts, summaries, recording titles. Personal folders and the user name are replaced
/// (<see cref="LogRedactor"/>); file and project names inside the Library can still appear in log lines.
/// </summary>
public sealed class DiagnosticsService(
    IAppLocations locations,
    LibraryService library,
    JobScheduler scheduler,
    IModelStore models,
    IAccelerationProbe acceleration,
    ISystemResourceProbe resources,
    IDiskSpaceProbe disk,
    UpdateChecker updates,
    IEnumerable<IDiagnosticsSource> sources,
    TimeProvider time)
{
    public const int JobHistory = 300;

    public string LogsDirectory => Path.Combine(locations.AppDataRoot, "Logs");

    public static string SuggestedFileName(DateTimeOffset at) =>
        $"Kakitome-diagnostics-{at.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip";

    public async Task ExportAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var temp = zipPath + ".partial";
        File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Add(zip, "README.txt", Readme());
            Add(zip, "system.txt", LogRedactor.Redact(SystemReport()));
            Add(zip, "jobs.txt", LogRedactor.Redact(await JobsReportAsync(cancellationToken).ConfigureAwait(false)));
            var settingsFile = Path.Combine(locations.AppDataRoot, "settings.json");
            if (File.Exists(settingsFile))
            {
                Add(zip, "settings.json", LogRedactor.Redact(await ReadSharedAsync(settingsFile, cancellationToken).ConfigureAwait(false)));
            }

            if (Directory.Exists(LogsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(LogsDirectory, "*.log").Order())
                {
                    // Already redacted when written; redacted again for crash.log and files from older versions.
                    Add(zip, "logs/" + Path.GetFileName(file), LogRedactor.Redact(await ReadSharedAsync(file, cancellationToken).ConfigureAwait(false)));
                }
            }
        }

        File.Move(temp, zipPath, overwrite: true);
    }

    private static string Readme() => """
        Kakitome diagnostics

        system.txt     PC, app version, power, free space, audio devices, installed models
        jobs.txt       recent processing steps (kind, state, timing, engine, error)
        settings.json  Kakitome settings
        logs/          Kakitome log files (last 14 days) and crash.log

        Not included: audio, transcripts, summaries and recording titles. Personal folders and the Windows user and
        computer names are replaced by %USERPROFILE%, %DOCUMENTS%, %USERNAME%, %COMPUTERNAME%. Log lines can still
        contain file and project names of the Library — check the files before sharing them.
        """;

    private string SystemReport()
    {
        var b = new StringBuilder();
        void Line(string text) => b.AppendLine(text);
        Line($"Created: {time.GetLocalNow():yyyy-MM-dd HH:mm:ss zzz}");
        Line($"Kakitome: {updates.CurrentVersion}");
        Line($"Latest release known: {updates.Available?.Version ?? "(none newer / not checked)"}");
        Line($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), process {RuntimeInformation.ProcessArchitecture}");
        Line($".NET: {RuntimeInformation.FrameworkDescription}");
        Line($"UI culture: {CultureInfo.CurrentUICulture.Name}, culture {CultureInfo.CurrentCulture.Name}");
        Line($"Processors: {Environment.ProcessorCount}");
        Line($"Memory: {Gb(acceleration.TotalMemoryBytes)} total");
        Line($"GPU for local models: {(acceleration.HasCapableGpu ? Gb(acceleration.GpuMemoryBytes) : "none (CPU)")}");
        foreach (var source in sources)
        {
            b.AppendLine();
            Line("[" + source.Name + "]");
            foreach (var line in SafeList(source.Describe))
            {
                Line(line);
            }
        }

        b.AppendLine().AppendLine("[Power and resources]");
        var now = resources.Current;
        Line($"On AC: {now.OnAcPower}, battery: {now.BatteryPercent?.ToString(CultureInfo.InvariantCulture) ?? "-"} %, Energy Saver: {now.EnergySaverOn}");
        Line($"CPU load by other apps: {now.CpuLoadOthers:P0}, available memory: {Gb(now.AvailableMemoryBytes)}");
        Line($"Free space where the Library is: {Gb(Safe(() => disk.GetAvailableBytes(library.LibraryRoot)))}");
        Line($"Library: {library.LibraryRoot}");

        b.AppendLine().AppendLine("[Models]");
        foreach (var m in ModelCatalog.Everything)
        {
            Line($"{m.Id}: {models.GetState(m.Id)}");
        }

        return b.ToString();
    }

    private async Task<string> JobsReportAsync(CancellationToken cancellationToken)
    {
        var jobs = (await scheduler.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .OrderByDescending(j => j.CreatedAt).Take(JobHistory).ToList();
        var b = new StringBuilder();
        b.AppendLine("created | kind | state | attempts | recording | started | finished | wait | engine | error");
        foreach (var j in jobs)
        {
            b.AppendLine(string.Join(" | ",
                j.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                j.Kind,
                j.State,
                j.Attempts,
                j.RecordingId?.ToString() ?? "-",
                j.StartedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-",
                j.FinishedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-",
                j.WaitReason,
                j.Engine ?? "-",
                (j.LastError ?? "-").ReplaceLineEndings(" ")));
        }

        return b.ToString();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>Reads a file another writer (the logger, the settings store) may have open.</summary>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Gb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB");

    /// <summary>A probe that fails must not stop the report.</summary>
    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Diagnostics collect what they can.
        catch
#pragma warning restore CA1031
        {
            return default;
        }
    }

    private static List<T> SafeList<T>(Func<IEnumerable<T>> read) => Safe<List<T>>(() => [.. read()]) ?? [];
}
