using Kakitome.Domain.Library;
using Kakitome.Storage.Library;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Library;

public sealed class FileSystemLibraryStoreTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("Projects/../../outside")]
    [InlineData("C:/Windows")]
    [InlineData("\\\\server\\share")]
    public async Task Folder_paths_cannot_escape_the_Library_root(string folder)
    {
        await using var f = await LibraryFixture.CreateAsync();

        await Assert.ThrowsAsync<UnsafeLibraryPathException>(() =>
            f.Store.WriteFileAsync(folder, "x.txt", new byte[] { 1 }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../metadata.json")]
    [InlineData("..\\metadata.json")]
    [InlineData("sub/file.txt")]
    [InlineData("..")]
    [InlineData("file.txt:stream")]
    public async Task File_names_must_be_plain_names(string fileName)
    {
        await using var f = await LibraryFixture.CreateAsync();
        var folder = f.Store.CreateRecordingFolder("P", Samples.Jst);

        await Assert.ThrowsAsync<UnsafeLibraryPathException>(() =>
            f.Store.WriteFileAsync(folder, fileName, new byte[] { 1 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_replaces_content_atomically_and_reads_back()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var folder = f.Store.CreateRecordingFolder("P", Samples.Jst);

        await f.Store.WriteFileAsync(folder, "a.txt", "one"u8.ToArray(), TestContext.Current.CancellationToken);
        await f.Store.WriteFileAsync(folder, "a.txt", "two"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("two"u8.ToArray(), await f.Store.ReadFileAsync(folder, "a.txt", TestContext.Current.CancellationToken));
        Assert.Null(await f.Store.ReadFileAsync(folder, "missing.txt", TestContext.Current.CancellationToken));
        Assert.Equal(["a.txt"], f.Store.ListFiles(folder));
    }

    [Fact]
    public async Task Write_waits_out_a_briefly_locked_target()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var folder = f.Store.CreateRecordingFolder("P", Samples.Jst);
        await f.Store.WriteFileAsync(folder, "a.txt", "old"u8.ToArray(), TestContext.Current.CancellationToken);

        // Simulates an antivirus scanner / editor holding the file without sharing.
        var holder = new FileStream(f.PathOf(folder, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(700);
            await holder.DisposeAsync();
        });

        await f.Store.WriteFileAsync(folder, "a.txt", "new"u8.ToArray(), TestContext.Current.CancellationToken);
        await release;

        Assert.Equal("new"u8.ToArray(), await f.Store.ReadFileAsync(folder, "a.txt", TestContext.Current.CancellationToken));
        Assert.Equal(["a.txt"], f.Store.ListFiles(folder));
    }

    [Fact]
    public async Task Cleanup_removes_only_stale_temporary_files()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var folder = f.Store.CreateRecordingFolder("P", Samples.Jst);
        var stale = f.PathOf(folder, LibraryLayout.TempFilePrefix + "dead-transcript.md");
        var fresh = f.PathOf(folder, LibraryLayout.TempFilePrefix + "live-summary.md");
        var canonical = f.PathOf(folder, "audio.wav");
        await File.WriteAllTextAsync(stale, "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fresh, "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(canonical, "x", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(canonical, DateTime.UtcNow.AddDays(-2));

        var deleted = f.Store.CleanupTemporaryFiles(TimeSpan.FromHours(1));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(canonical));
    }

    [Fact]
    public async Task Hidden_and_dot_folders_are_not_recordings()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var folder = f.Store.CreateRecordingFolder("P", Samples.Jst);
        Directory.CreateDirectory(f.PathOf("Projects/P/.git"));
        Directory.CreateDirectory(f.PathOf("Projects/.trash/x"));

        Assert.Equal([folder], f.Store.EnumerateRecordingFolders());
    }

    [Fact]
    public async Task Large_media_is_fingerprinted_without_hashing()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var folder = f.Store.CreateRecordingFolder("P", Samples.Jst);
        await using (var fs = File.Create(f.PathOf(folder, "audio.wav")))
        {
            fs.SetLength(FileSystemLibraryStore.MaxHashedFileSize + 1);
        }

        var stamp = await f.Store.GetStampAsync(folder, "audio.wav", includeHash: true, TestContext.Current.CancellationToken);

        Assert.NotNull(stamp);
        Assert.Null(stamp.Sha256);
        Assert.Equal(FileSystemLibraryStore.MaxHashedFileSize + 1, stamp.Length);
    }
}
