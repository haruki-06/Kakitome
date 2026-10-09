using System.IO.Compression;
using Kakitome.Application.Diagnostics;
using Kakitome.Storage.Logging;
using Kakitome.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kakitome.Tests.Diagnostics;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Personal_folders_and_the_user_name_are_hidden()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var text = LogRedactor.Redact($"open {Path.Combine(profile, "x.txt")} and {Path.Combine(documents, "Kakitome", "Library")} on {Environment.MachineName}");

        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(documents, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
        Assert.Contains("%DOCUMENTS%", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
#pragma warning disable CA1848, CA1873 // plain logging calls are clearer in a test of the logger itself
    public async Task Log_files_get_Kakitome_messages_and_only_warnings_from_libraries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "KakitomeTests", "logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new FileLoggerProvider(dir, "started"))
            {
                provider.CreateLogger("Kakitome.Application.Jobs.JobScheduler").LogInformation("Job {Id} done", 7);
                provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("SELECT secret");
                provider.CreateLogger("Microsoft.EntityFrameworkCore").LogWarning("slow");
                provider.CreateLogger("Kakitome.Storage.X").LogError(new IOException("disk"), "Writing {File} failed",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "a.wav"));
            }

            var text = await File.ReadAllTextAsync(Directory.GetFiles(dir, FileLoggerProvider.FilePrefix + "*.log").Single(), TestContext.Current.CancellationToken);
            Assert.Contains("[INF] Kakitome: started", text, StringComparison.Ordinal);
            Assert.Contains("[INF] Application.Jobs.JobScheduler: Job 7 done", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT secret", text, StringComparison.Ordinal);
            Assert.Contains("[WRN] Microsoft.EntityFrameworkCore: slow", text, StringComparison.Ordinal);
            Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
            Assert.Contains("System.IO.IOException: disk", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

#pragma warning restore CA1848, CA1873

    [Fact]
    public async Task The_diagnostics_zip_has_logs_system_and_jobs_but_no_recording_content()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new Kakitome.Application.Library.NewRecording { Project = "P", Title = "極秘の打ち合わせ" });
        var transcript = Samples.Transcript(entry.Id);
        await f.Library.SaveTranscriptAsync(transcript);
        await f.Settings.UpdateAsync(s => s.General.Theme = "dark"); // writes settings.json
        var diagnostics = f.Services.GetRequiredService<DiagnosticsService>();
        Directory.CreateDirectory(diagnostics.LogsDirectory);
        await File.WriteAllTextAsync(Path.Combine(diagnostics.LogsDirectory, "kakitome-20261009.log"), "a line", TestContext.Current.CancellationToken);
        var zipPath = Path.Combine(f.Root, DiagnosticsService.SuggestedFileName(DateTimeOffset.Now));

        await diagnostics.ExportAsync(zipPath, TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(["README.txt", "jobs.txt", "logs/kakitome-20261009.log", "settings.json", "system.txt"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        var all = string.Concat(zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.Contains("Kakitome:", all, StringComparison.Ordinal);
        Assert.DoesNotContain("極秘の打ち合わせ", all, StringComparison.Ordinal);
        Assert.DoesNotContain(transcript.Segments[0].Text, all, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void The_real_hardware_and_audio_sources_describe_this_PC()
    {
        var hardware = new Kakitome.Infrastructure.Diagnostics.HardwareDiagnostics().Describe().ToList();
        Assert.StartsWith("CPU: ", hardware[0], StringComparison.Ordinal);
        Assert.Contains(hardware, l => l.StartsWith("Windows: ", StringComparison.Ordinal));

        using var catalog = new Kakitome.Infrastructure.Audio.WasapiDeviceCatalog();
        var audio = new Kakitome.Infrastructure.Diagnostics.AudioDiagnostics(catalog).Describe().ToList();
        Assert.Contains(audio, l => l.StartsWith("Per-app capture supported: ", StringComparison.Ordinal));
    }
}
