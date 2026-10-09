using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Backup;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Maintenance;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Maintenance;

public sealed class BackupTests
{
    [Fact]
    public async Task Backup_contains_the_library_and_restores_it_after_loss()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Title = "講義 第1回", Project = "大学", RecordedAt = Samples.Jst });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id), cancellationToken: TestContext.Current.CancellationToken);
        var audio = Path.Combine(await f.Library.GetRecordingPathAsync(a.Id, TestContext.Current.CancellationToken), "audio.m4a");
        await File.WriteAllBytesAsync(audio, Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray(), TestContext.Current.CancellationToken);
        var before = f.SnapshotLibrary();

        var result = await Backups(f).CreateAsync(Locations(f).BackupsDirectory, "manual", "1.0.0", null, TestContext.Current.CancellationToken);

        Assert.EndsWith(BackupService.Extension, result.FilePath, StringComparison.Ordinal);
        Assert.Equal(1, result.Recordings);
        Assert.Empty(Directory.EnumerateFiles(Locations(f).BackupsDirectory, "*.partial"));
        var manifest = await BackupService.VerifyAsync(result.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(before.Count, manifest.Files.Count);

        // Lose the Library, then restore.
        Directory.Delete(f.Library.LibraryRoot, recursive: true);
        Directory.CreateDirectory(f.Library.LibraryRoot);
        await f.Library.SynchronizeAsync(TestContext.Current.CancellationToken);

        var restore = await Backups(f).RestoreAsync(result.FilePath, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, restore.Restored);
        Assert.Equal(before, f.SnapshotLibrary());
        Assert.Equal("講義 第1回", (await f.Library.FindAsync(a.Id, TestContext.Current.CancellationToken))!.Title);
    }

    [Fact]
    public async Task Restore_never_overwrites_existing_recordings()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Title = "original", RecordedAt = Samples.Jst });
        var backup = await Backups(f).CreateAsync(Locations(f).BackupsDirectory, "manual", null, null, TestContext.Current.CancellationToken);
        await f.Library.UpdateMetadataAsync(a.Id, m => m.Title = "edited after backup", TestContext.Current.CancellationToken);
        var before = f.SnapshotLibrary();

        var restore = await Backups(f).RestoreAsync(backup.FilePath, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, restore.Restored);
        Assert.Equal(1, restore.SkippedExisting);
        Assert.Equal(before, f.SnapshotLibrary());
        Assert.Equal("edited after backup", (await f.Library.FindAsync(a.Id, TestContext.Current.CancellationToken))!.Title);
    }

    [Fact]
    public async Task A_taken_folder_name_is_restored_beside_it()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Title = "from backup", RecordedAt = Samples.Jst });
        var backup = await Backups(f).CreateAsync(Locations(f).BackupsDirectory, "manual", null, null, TestContext.Current.CancellationToken);
        var folder = await f.Library.GetRecordingPathAsync(a.Id, TestContext.Current.CancellationToken);

        // Another (unrelated) recording now occupies the same folder name.
        await f.RestartAsync(() =>
        {
            Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "someone else's file");
        });
        await f.Database.InitializeAsync(TestContext.Current.CancellationToken);
        await f.Library.SynchronizeAsync(TestContext.Current.CancellationToken);

        var restore = await Backups(f).RestoreAsync(backup.FilePath, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, restore.Restored);
        Assert.Single(restore.RestoredAsCopies);
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(folder, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(folder + " (restored)", "metadata.json")));
    }

    [Fact]
    public async Task Damaged_or_crafted_backups_are_rejected_before_anything_is_written()
    {
        await using var f = await LibraryFixture.CreateAsync();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        var backup = await Backups(f).CreateAsync(Locations(f).BackupsDirectory, "manual", null, null, TestContext.Current.CancellationToken);

        // Tamper with one file's content.
        var damaged = Path.Combine(f.Root, "damaged" + BackupService.Extension);
        File.Copy(backup.FilePath, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.EndsWith("metadata.json", StringComparison.Ordinal));
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("{ \"tampered\": true }");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.VerifyAsync(damaged, TestContext.Current.CancellationToken));

        // A manifest pointing outside the Library.
        var crafted = Path.Combine(f.Root, "crafted" + BackupService.Extension);
        using (var zip = ZipFile.Open(crafted, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("""{ "format": "kakitome-backup", "version": 1, "createdAt": "2026-10-01T00:00:00+09:00", "files": [ { "path": "../evil.txt", "size": 1, "sha256": "00" } ] }""");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Backups(f).RestoreAsync(crafted, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(f.Root, "evil.txt")));

        // A backup from a newer Kakitome.
        var newer = Path.Combine(f.Root, "newer" + BackupService.Extension);
        using (var zip = ZipFile.Open(newer, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("""{ "format": "kakitome-backup", "version": 99, "createdAt": "2026-10-01T00:00:00+09:00" }""");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.VerifyAsync(newer, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("/abs")]
    [InlineData("C:/x")]
    [InlineData("a//b")]
    [InlineData("")]
    public void Unsafe_backup_paths_are_rejected(string path) =>
        Assert.Throws<InvalidDataException>(() => BackupService.SafeRelative(path));

    [Fact]
    public async Task Automatic_backups_are_opt_in_due_based_and_rotated()
    {
        await using var f = await LibraryFixture.CreateAsync();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        var policy = f.Services.GetRequiredService<AutomaticBackupPolicy>();

        Assert.False(await policy.EnqueueIfDueAsync(null, TestContext.Current.CancellationToken)); // off by default

        await f.Settings.UpdateAsync(s => s.Backup.Automatic = true, TestContext.Current.CancellationToken);
        Assert.True(await policy.EnqueueIfDueAsync(null, TestContext.Current.CancellationToken));
        Assert.False(await policy.EnqueueIfDueAsync(null, TestContext.Current.CancellationToken)); // already queued
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);
        var job = Assert.Single(await f.Jobs.ListAsync(cancellationToken: TestContext.Current.CancellationToken), j => j.Kind == BackupJobHandler.JobKind);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Single(Directory.EnumerateFiles(Locations(f).BackupsDirectory, "*_auto" + BackupService.Extension));

        // Rotation keeps the newest automatic ones and never touches manual backups.
        var dir = Locations(f).BackupsDirectory;
        for (var day = 1; day <= 5; day++)
        {
            File.WriteAllText(Path.Combine(dir, $"Kakitome_2026-01-0{day}_00-00-00_auto{BackupService.Extension}"), "x");
        }

        File.WriteAllText(Path.Combine(dir, $"Kakitome_2026-01-01_00-00-00{BackupService.Extension}"), "manual");
        Backups(f).RotateAutomatic(BackupJobHandler.AutomaticToKeep);
        Assert.Equal(BackupJobHandler.AutomaticToKeep, Directory.EnumerateFiles(dir, "*_auto" + BackupService.Extension).Count());
        Assert.True(File.Exists(Path.Combine(dir, $"Kakitome_2026-01-01_00-00-00{BackupService.Extension}")));
    }

    private static BackupService Backups(LibraryFixture f) => f.Services.GetRequiredService<BackupService>();

    private static IAppLocations Locations(LibraryFixture f) => f.Services.GetRequiredService<IAppLocations>();
}
