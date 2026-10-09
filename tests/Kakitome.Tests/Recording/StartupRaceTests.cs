using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Storage.Audio;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Recording;

/// <summary>Regressions found by the end-to-end UI test.</summary>
public sealed class StartupRaceTests
{
    [Fact]
    public async Task Recording_can_start_before_background_database_initialization_ran()
    {
        // At app launch the user can press Start before LibraryBootstrapper has migrated the database.
        await using var f = await LibraryFixture.CreateAsync(initializeDatabase: false);

        var session = await f.Recording.StartAsync(new RecordingOptions());
        await session.StopAsync();

        Assert.NotNull(await f.Library.FindAsync(session.Id));
    }

    [Fact]
    public async Task A_start_that_fails_after_creating_the_folder_leaves_nothing_behind()
    {
        await using var f = await LibraryFixture.CreateAsync(configure: s =>
            s.AddSingleton<IAudioFileWriterFactory>(new FailingOnSecondStreamWriterFactory()));

        await Assert.ThrowsAsync<IOException>(() =>
            f.Recording.StartAsync(new RecordingOptions { IncludeSystemAudio = true }));

        Assert.Empty(f.Store.EnumerateRecordingFolders());
        Assert.Empty(await f.Library.ListRecordingsAsync());
        Assert.Null(f.Recording.Current);
        Assert.All(f.Capture.Created, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task Startup_recovery_never_touches_a_recording_that_is_running()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        f.Capture.Latest(CaptureSourceKind.Microphone).PushSeconds(1);

        Assert.Equal(0, await f.RecordingRecovery.RecoverAsync());

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(Kakitome.Domain.Library.CaptureStatus.InProgress, metadata.Capture!.Status);
        await session.StopAsync();
    }

    [Fact]
    public async Task Rebuild_during_use_never_hides_existing_recordings()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });

        var rebuild = f.Library.RebuildIndexAsync();
        for (var i = 0; i < 50 && !rebuild.IsCompleted; i++)
        {
            Assert.NotNull(await f.Library.FindAsync(entry.Id));
        }

        await rebuild;
        Assert.NotNull(await f.Library.FindAsync(entry.Id));
    }

    [Fact]
    public async Task Discarding_an_unused_recording_refuses_folders_with_content()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        var folder = await f.Library.GetRecordingPathAsync(entry.Id);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "user file");

        Assert.False(await f.Library.DiscardUnusedRecordingAsync(entry.Id));
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        Assert.NotNull(await f.Library.FindAsync(entry.Id));
    }

    private sealed class FailingOnSecondStreamWriterFactory : IAudioFileWriterFactory
    {
        private readonly WavFileWriterFactory _inner = new();
        private int _created;

        public string Extension => _inner.Extension;

        public string FormatLabel => _inner.FormatLabel;

        public IAudioFileWriter Create(string path, AudioFormat format) =>
            ++_created == 2 ? throw new IOException("disk error") : _inner.Create(path, format);
    }
}
