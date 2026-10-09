using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Library;
using Kakitome.Storage;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Library;

public sealed class LibraryBootstrapperTests
{
    [Fact]
    public async Task Startup_indexes_an_existing_Library_into_a_fresh_database()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", Title = "from a previous install" });

        // Fresh database (e.g. after reinstall) over an existing Library.
        await f.RestartAsync();
        foreach (var file in new[] { "", "-wal", "-shm" })
        {
            File.Delete(f.AppData.DatabaseFile + file);
        }

        await f.RestartAsync();

        using var bootstrapper = new LibraryBootstrapper(f.Database, f.Library, f.RecordingRecovery, f.Pipeline, f.Jobs, f.Search, f.SearchIndex,
            f.Services.GetRequiredService<Kakitome.Application.Backup.AutomaticBackupPolicy>(), f.Time, NullLogger<LibraryBootstrapper>.Instance);
        await bootstrapper.StartAsync(TestContext.Current.CancellationToken);
        var report = await bootstrapper.Ready.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await bootstrapper.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal("from a previous install", (await f.Library.FindAsync(a.Id, TestContext.Current.CancellationToken))!.Title);
    }
}
