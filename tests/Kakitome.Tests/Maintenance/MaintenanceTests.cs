using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Library;
using Kakitome.Application.Maintenance;
using Kakitome.Application.Recording;
using Kakitome.Storage;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Maintenance;

public sealed class MaintenanceTests
{
    [Fact]
    public async Task Storage_is_measured_by_category()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var locations = f.Services.GetRequiredService<IAppLocations>();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        Write(Path.Combine(locations.ModelsDirectory, "m", "model.bin"), 3000);
        Write(Path.Combine(locations.CacheDirectory, "import", "x.tmp"), 2000);
        Write(Path.Combine(locations.BackupsDirectory, "a.kakitome-backup"), 1000);

        var sizes = await Maintenance(f).MeasureAsync(TestContext.Current.CancellationToken);

        Assert.True(sizes.Library > 0);
        Assert.Equal(3000, sizes.Models);
        Assert.Equal(2000, sizes.Cache);
        Assert.Equal(1000, sizes.Backups);
        Assert.True(sizes.AppData > 0); // database; models and cache are not double-counted
        Assert.True(sizes.AppData < new DirectoryInfo(locations.AppDataRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(i => i.Length));
    }

    [Fact]
    public async Task Clear_cache_removes_only_cache_files()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var locations = f.Services.GetRequiredService<IAppLocations>();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        var library = f.SnapshotLibrary();
        Write(Path.Combine(locations.CacheDirectory, "import", "url-1", "media.m4a"), 5000);
        Write(Path.Combine(locations.ModelsDirectory, "m", "model.bin"), 10);

        var freed = await Maintenance(f).ClearCacheAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5000, freed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(locations.CacheDirectory));
        Assert.True(File.Exists(Path.Combine(locations.ModelsDirectory, "m", "model.bin")));
        Assert.Equal(library, f.SnapshotLibrary());
    }

    [Fact]
    public async Task Reset_config_restores_defaults_without_touching_the_library()
    {
        await using var f = await LibraryFixture.CreateAsync();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        var library = f.SnapshotLibrary();
        await f.Settings.UpdateAsync(s => s.General.Theme = "dark", TestContext.Current.CancellationToken);

        await Maintenance(f).ResetConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal("system", f.Settings.Current.General.Theme);
        Assert.Equal(library, f.SnapshotLibrary());
    }

    [Fact]
    public async Task Factory_reset_removes_app_state_models_and_cache_but_keeps_the_library()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Title = "残す録音", RecordedAt = Samples.Jst });
        await f.Settings.UpdateAsync(s => s.General.Theme = "dark", TestContext.Current.CancellationToken);
        Write(Path.Combine(f.AppData.ModelsDirectory, "m", "model.bin"), 10);
        Write(Path.Combine(f.AppData.CacheDirectory, "x.tmp"), 10);
        Write(Path.Combine(f.AppData.LogsDirectory, "app.log"), 10);
        var library = f.SnapshotLibrary();

        Assert.Null(FactoryReset.RunIfRequested(f.AppData)); // nothing requested yet
        await Maintenance(f).RequestFactoryResetAsync(TestContext.Current.CancellationToken);

        // As at the next start: the runner executes before anything opens the database.
        IReadOnlyList<string>? problems = null;
        IReadOnlyList<string>? second = [];
        await f.RestartAsync(() =>
        {
            problems = FactoryReset.RunIfRequested(f.AppData);
            Assert.False(Directory.Exists(f.AppData.DataDirectory));
            Assert.False(Directory.Exists(f.AppData.ModelsDirectory));
            Assert.False(Directory.Exists(f.AppData.CacheDirectory));
            Assert.False(Directory.Exists(f.AppData.LogsDirectory));
            Assert.False(File.Exists(f.AppData.SettingsFile));
            second = FactoryReset.RunIfRequested(f.AppData);
        });

        Assert.NotNull(problems);
        Assert.Empty(problems);
        Assert.Null(second); // the request is consumed
        Assert.Equal(library, f.SnapshotLibrary());
        Assert.Equal("system", f.Settings.Current.General.Theme);

        // The next start rebuilds the index from the Library.
        await f.Database.InitializeAsync(TestContext.Current.CancellationToken);
        await f.Library.SynchronizeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("残す録音", (await f.Library.FindAsync(entry.Id, TestContext.Current.CancellationToken))!.Title);
    }

    [Fact]
    public async Task Uninstall_keeps_everything_unless_chosen()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var locations = f.Services.GetRequiredService<IAppLocations>();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        Write(Path.Combine(locations.ModelsDirectory, "m", "model.bin"), 10);
        Write(Path.Combine(locations.BackupsDirectory, "a.kakitome-backup"), 10);

        var kept = await Maintenance(f).PrepareUninstallAsync(new UninstallChoices(false, false, false), TestContext.Current.CancellationToken);
        Assert.False(kept.LibraryRemoved || kept.ModelsRemoved || kept.BackupsRemoved);
        Assert.Empty(f.RecycleBin.Recycled);
        Assert.True(Directory.Exists(f.LibraryRoot));
        Assert.True(Directory.Exists(locations.ModelsDirectory));

        var removed = await Maintenance(f).PrepareUninstallAsync(new UninstallChoices(true, true, true), TestContext.Current.CancellationToken);
        Assert.True(removed.LibraryRemoved && removed.ModelsRemoved && removed.BackupsRemoved);
        Assert.Contains(f.Library.LibraryRoot + ".recycled", f.RecycleBin.Recycled); // recoverable, never a hard delete
        Assert.Contains(locations.BackupsDirectory + ".recycled", f.RecycleBin.Recycled);
        Assert.False(Directory.Exists(locations.ModelsDirectory));
    }

    [Fact]
    public async Task Packaged_uninstall_never_deletes_models_it_was_not_asked_to_delete()
    {
        await using var f = await LibraryFixture.CreateAsync(configure: s =>
            s.AddSingleton<IAppLocations>(sp => new PackagedLocations(sp.GetRequiredService<Kakitome.Storage.AppDataPaths>())));
        var locations = f.Services.GetRequiredService<IAppLocations>();
        Write(Path.Combine(locations.ModelsDirectory, "m", "model.bin"), 10);

        var result = await Maintenance(f).PrepareUninstallAsync(new UninstallChoices(false, false, false), TestContext.Current.CancellationToken);

        Assert.False(result.ModelsRemoved);
        Assert.True(File.Exists(Path.Combine(locations.ModelsDirectory, "m", "model.bin")));
    }

    private sealed class PackagedLocations(Kakitome.Storage.AppDataPaths paths) : IAppLocations
    {
        public string AppDataRoot => paths.Root;

        public string CacheDirectory => paths.CacheDirectory;

        public string ModelsDirectory => paths.ModelsDirectory;

        public string BackupsDirectory => Path.Combine(paths.Root, "..", "Backups");

        public bool IsPackaged => true;
    }

    [Fact]
    public async Task Uninstall_reports_what_could_not_be_recycled()
    {
        await using var f = await LibraryFixture.CreateAsync();
        await f.Library.CreateRecordingAsync(new NewRecording { RecordedAt = Samples.Jst });
        f.RecycleBin.Works = false;

        var result = await Maintenance(f).PrepareUninstallAsync(new UninstallChoices(true, false, false), TestContext.Current.CancellationToken);

        Assert.False(result.LibraryRemoved);
        Assert.Contains(f.Library.LibraryRoot, result.Problems);
        Assert.True(Directory.Exists(f.LibraryRoot));
    }

    [Fact]
    public async Task Destructive_maintenance_is_refused_while_recording()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Maintenance(f).RequestFactoryResetAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Maintenance(f).PrepareUninstallAsync(new UninstallChoices(true, true, true), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(f.AppData.Root, MaintenanceService.FactoryResetMarker)));
        await session.StopAsync();
    }

    private static MaintenanceService Maintenance(LibraryFixture f) => f.Services.GetRequiredService<MaintenanceService>();

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
