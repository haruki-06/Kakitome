using Kakitome.Application.Library;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Library;

/// <summary>SQLite is derived state: losing or corrupting it must never lose the Library (docs/09).</summary>
public sealed class IndexRecoveryTests
{
    [Fact]
    public async Task Index_is_rebuilt_from_Library_files_alone()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "講義", Title = "A", Tags = ["x"] });
        var b = await f.Library.CreateRecordingAsync(new NewRecording { Project = "会議", Title = "B" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id));
        await f.Library.SaveSummaryAsync(Samples.Summary(a.Id));
        var expected = (await f.Library.ListRecordingsAsync(TestContext.Current.CancellationToken)).OrderBy(e => e.Title).ToList();

        // Delete the database entirely.
        await f.RestartAsync();
        f.ClearOwnPool();
        File.Delete(f.AppData.DatabaseFile);
        await f.RestartAsync();
        var init = await f.Database.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(init.NeedsIndexRebuild);
        Assert.Empty(await f.Library.ListRecordingsAsync(TestContext.Current.CancellationToken));

        var report = await f.Library.RebuildIndexAsync();

        Assert.Equal(2, report.Total);
        var rebuilt = (await f.Library.ListRecordingsAsync(TestContext.Current.CancellationToken)).OrderBy(e => e.Title).ToList();
        Assert.Equal(expected.Select(e => (e.Id, e.Folder, e.Title, e.HasTranscript, e.HasSummary)),
            rebuilt.Select(e => (e.Id, e.Folder, e.Title, e.HasTranscript, e.HasSummary)));
        Assert.Equal(["x"], rebuilt.Single(e => e.Id == a.Id).Tags);
        Assert.Equal(b.Id, rebuilt.Single(e => e.Title == "B").Id);
    }

    [Fact]
    public async Task Corrupt_database_is_quarantined_and_Library_survives()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", Title = "survivor" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id));
        var libraryBefore = f.SnapshotLibrary();

        await f.RestartAsync();
        f.ClearOwnPool();
        foreach (var side in new[] { "-wal", "-shm" })
        {
            File.Delete(f.AppData.DatabaseFile + side);
        }

        await File.WriteAllBytesAsync(f.AppData.DatabaseFile, Enumerable.Repeat((byte)0xAB, 8192).ToArray(), TestContext.Current.CancellationToken);
        await f.RestartAsync();

        var init = await f.Database.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(init.NeedsIndexRebuild);
        Assert.NotNull(init.QuarantinedPath);
        Assert.True(File.Exists(init.QuarantinedPath), "corrupt database is kept for diagnosis, not deleted");
        await f.Library.RebuildIndexAsync();
        Assert.Equal("survivor", (await f.Library.FindAsync(a.Id, TestContext.Current.CancellationToken))!.Title);
        Assert.Equal(libraryBefore, f.SnapshotLibrary());
    }

    [Fact]
    public async Task Healthy_database_is_reused_across_restarts()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });

        await f.RestartAsync();
        var init = await f.Database.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(init.NeedsIndexRebuild);
        Assert.Null(init.QuarantinedPath);
        Assert.NotNull(await f.Library.FindAsync(a.Id, TestContext.Current.CancellationToken));
    }
}
