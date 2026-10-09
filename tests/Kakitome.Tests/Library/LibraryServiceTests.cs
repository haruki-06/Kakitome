using System.Text;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Serialization;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Library;

public sealed class LibraryServiceTests
{
    [Fact]
    public async Task Create_recording_writes_metadata_in_canonical_folder()
    {
        await using var f = await LibraryFixture.CreateAsync();

        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "大学講義", Title = "第1回", Tags = ["講義", " 講義 ", ""] });

        Assert.Equal("Projects/大学講義/大学講義_2026-09-30_10-15-30", entry.Folder);
        var metadataPath = f.PathOf(entry.Folder, LibraryLayout.MetadataFile);
        Assert.True(File.Exists(metadataPath));

        var metadata = LibraryJson.DeserializeMetadata(await File.ReadAllBytesAsync(metadataPath, TestContext.Current.CancellationToken));
        Assert.Equal(entry.Id, metadata.Id);
        Assert.Equal("第1回", metadata.Title);
        Assert.Equal(["講義"], metadata.Tags);
        Assert.Equal(CaptureStatus.InProgress, metadata.Capture!.Status);
        Assert.Equal(TimeSpan.FromHours(9), metadata.CreatedAt.Offset);
    }

    [Fact]
    public async Task Recordings_in_the_same_second_get_unique_folders()
    {
        await using var f = await LibraryFixture.CreateAsync();

        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "Meeting" });
        var b = await f.Library.CreateRecordingAsync(new NewRecording { Project = "Meeting" });

        Assert.EndsWith("Meeting_2026-09-30_10-15-30", a.Folder, StringComparison.Ordinal);
        Assert.EndsWith("Meeting_2026-09-30_10-15-30_2", b.Folder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_project_and_title_are_filled_in()
    {
        await using var f = await LibraryFixture.CreateAsync();

        var entry = await f.Library.CreateRecordingAsync(new NewRecording());

        Assert.Equal("Inbox", entry.Project);
        Assert.Equal("Inbox 2026-09-30 10:15", entry.Title);
    }

    [Fact]
    public async Task Saving_transcript_writes_json_markdown_and_reading_text_by_default()
    {
        await using var defaults = await LibraryFixture.CreateAsync();
        var e1 = await defaults.Library.CreateRecordingAsync(new NewRecording { Project = "P", Language = "ja" });
        var r1 = await defaults.Library.SaveTranscriptAsync(Samples.Transcript(e1.Id));

        Assert.True(r1.Saved);
        var files1 = defaults.Store.ListFiles(e1.Folder);
        Assert.Contains(LibraryLayout.TranscriptJsonFile, files1);
        Assert.Contains(LibraryLayout.TranscriptMarkdownFile, files1);
        Assert.Contains(LibraryLayout.TranscriptTextFile, files1);

        await using var noText = await LibraryFixture.CreateAsync(new LibraryOutputOptions { WriteText = false });
        var e0 = await noText.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await noText.Library.SaveTranscriptAsync(Samples.Transcript(e0.Id));
        Assert.DoesNotContain(LibraryLayout.TranscriptTextFile, noText.Store.ListFiles(e0.Folder));

        await using var withText = await LibraryFixture.CreateAsync(new LibraryOutputOptions { WriteMarkdown = false, WriteText = true });
        var e2 = await withText.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await withText.Library.SaveTranscriptAsync(Samples.Transcript(e2.Id));
        var files2 = withText.Store.ListFiles(e2.Folder);
        Assert.Contains(LibraryLayout.TranscriptJsonFile, files2);
        Assert.Contains(LibraryLayout.TranscriptTextFile, files2);
        Assert.DoesNotContain(LibraryLayout.TranscriptMarkdownFile, files2);

        var entry = await withText.Library.FindAsync(e2.Id, TestContext.Current.CancellationToken);
        Assert.True(entry!.HasTranscript);
        Assert.False(entry.HasSummary);
    }

    [Fact]
    public async Task No_temporary_files_are_left_behind()
    {
        await using var f = await LibraryFixture.CreateAsync(new LibraryOutputOptions { WriteText = true });
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));
        await f.Library.SaveSummaryAsync(Samples.Summary(e.Id));

        var leftovers = Directory.EnumerateFiles(f.LibraryRoot, LibraryLayout.TempFilePrefix + "*", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Summary_save_never_touches_the_transcript()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));
        var before = f.SnapshotLibrary().Where(kv => kv.Key.Contains("transcript", StringComparison.Ordinal)).ToList();

        // Summary blocked by an external edit of summary.md: nothing written, transcript intact.
        await File.WriteAllTextAsync(f.PathOf(e.Folder, LibraryLayout.SummaryMarkdownFile), "my notes", TestContext.Current.CancellationToken);
        var blocked = await f.Library.SaveSummaryAsync(Samples.Summary(e.Id));
        Assert.False(blocked.Saved);
        Assert.False(File.Exists(f.PathOf(e.Folder, LibraryLayout.SummaryJsonFile)));

        var after = f.SnapshotLibrary().Where(kv => kv.Key.Contains("transcript", StringComparison.Ordinal)).ToList();
        Assert.Equal(before, after);
        Assert.NotNull(await f.Library.LoadTranscriptAsync(e.Id));
    }

    [Fact]
    public async Task Externally_edited_markdown_is_never_silently_overwritten()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));

        var mdPath = f.PathOf(e.Folder, LibraryLayout.TranscriptMarkdownFile);
        await File.AppendAllTextAsync(mdPath, "\nユーザーの追記\n", TestContext.Current.CancellationToken);
        var edited = await File.ReadAllTextAsync(mdPath, TestContext.Current.CancellationToken);

        var report = await f.Library.SynchronizeAsync();
        Assert.Contains(report.Issues, i => i.Kind == LibraryIssueKind.ExternalChange && i.FileName == LibraryLayout.TranscriptMarkdownFile);
        Assert.True((await f.Library.FindAsync(e.Id, TestContext.Current.CancellationToken))!.HasExternalChanges);

        var transcript = Samples.Transcript(e.Id);
        transcript.Revision = 2;
        transcript.Segments[0].Text = "改訂版";
        var result = await f.Library.SaveTranscriptAsync(transcript);

        Assert.False(result.Saved);
        Assert.Equal([LibraryLayout.TranscriptMarkdownFile], result.ConflictingFiles);
        Assert.Equal(edited, await File.ReadAllTextAsync(mdPath, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await f.Library.LoadTranscriptAsync(e.Id))!.Revision);
    }

    [Fact]
    public async Task Overwrite_after_user_confirmation_preserves_the_edited_file()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));
        var mdPath = f.PathOf(e.Folder, LibraryLayout.TranscriptMarkdownFile);
        await File.WriteAllTextAsync(mdPath, "hand edited", TestContext.Current.CancellationToken);

        var transcript = Samples.Transcript(e.Id);
        transcript.Segments[0].Text = "改訂版";
        var result = await f.Library.SaveTranscriptAsync(transcript, ConflictPolicy.PreserveAndOverwrite);

        Assert.True(result.Saved);
        var preserved = Assert.Single(result.PreservedFiles);
        Assert.Equal("transcript.conflict-20260930-101530.md", preserved);
        Assert.Equal("hand edited", await File.ReadAllTextAsync(f.PathOf(e.Folder, preserved), TestContext.Current.CancellationToken));
        Assert.Contains("改訂版", await File.ReadAllTextAsync(mdPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepting_external_changes_clears_the_conflict()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));
        await File.WriteAllTextAsync(f.PathOf(e.Folder, LibraryLayout.TranscriptMarkdownFile), "mine", TestContext.Current.CancellationToken);
        await f.Library.SynchronizeAsync();

        await f.Library.AcceptExternalChangesAsync(e.Id);

        Assert.False((await f.Library.FindAsync(e.Id, TestContext.Current.CancellationToken))!.HasExternalChanges);
        Assert.True((await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id))).Saved);
    }

    [Fact]
    public async Task Rewriting_identical_content_is_not_a_conflict()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        var transcript = Samples.Transcript(e.Id);
        await f.Library.SaveTranscriptAsync(transcript);

        // Index lost (e.g. database reset) but files unchanged: re-saving the same content is fine.
        await f.Index.SetArtifactStampAsync(e.Id, LibraryLayout.TranscriptMarkdownFile, null, TestContext.Current.CancellationToken);

        Assert.True((await f.Library.SaveTranscriptAsync(transcript)).Saved);
    }

    [Fact]
    public async Task Metadata_updates_merge_external_edits_and_rerender_views()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", Title = "旧タイトル" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));

        // The user adds a tag in VS Code.
        var metadataPath = f.PathOf(e.Folder, LibraryLayout.MetadataFile);
        var external = LibraryJson.DeserializeMetadata(await File.ReadAllBytesAsync(metadataPath, TestContext.Current.CancellationToken));
        external.Tags.Add("外部で追加");
        await File.WriteAllBytesAsync(metadataPath, LibraryJson.Serialize(external), TestContext.Current.CancellationToken);

        var updated = await f.Library.UpdateMetadataAsync(e.Id, m => m.Title = "新タイトル");

        Assert.Equal("新タイトル", updated.Title);
        Assert.Contains("外部で追加", updated.Tags);
        var md = await File.ReadAllTextAsync(f.PathOf(e.Folder, LibraryLayout.TranscriptMarkdownFile), TestContext.Current.CancellationToken);
        Assert.StartsWith("# 新タイトル", md, StringComparison.Ordinal);
        Assert.Equal("新タイトル", (await f.Library.FindAsync(e.Id, TestContext.Current.CancellationToken))!.Title);
    }

    [Fact]
    public async Task Changing_the_recording_id_is_rejected()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Library.UpdateMetadataAsync(e.Id, m => m.Id = Kakitome.Domain.Recordings.RecordingId.New()));
    }

    [Fact]
    public async Task Synchronize_picks_up_external_json_edits_moves_and_deletions()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var moved = await f.Library.CreateRecordingAsync(new NewRecording { Project = "A", Title = "moved" });
        var deleted = await f.Library.CreateRecordingAsync(new NewRecording { Project = "A", Title = "deleted" });
        var retitled = await f.Library.CreateRecordingAsync(new NewRecording { Project = "A", Title = "before" });

        // User moves a folder to another project in Explorer.
        Directory.CreateDirectory(f.PathOf("Projects/B"));
        Directory.Move(f.PathOf(moved.Folder), f.PathOf("Projects/B/renamed-by-user"));
        // User deletes a recording folder in Explorer.
        Directory.Delete(f.PathOf(deleted.Folder), recursive: true);
        // User edits metadata.json by hand.
        var path = f.PathOf(retitled.Folder, LibraryLayout.MetadataFile);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, text.Replace("\"before\"", "\"after\"", StringComparison.Ordinal), TestContext.Current.CancellationToken);

        var report = await f.Library.SynchronizeAsync();

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Removed);
        Assert.Equal("Projects/B/renamed-by-user", (await f.Library.FindAsync(moved.Id, TestContext.Current.CancellationToken))!.Folder);
        Assert.Null(await f.Library.FindAsync(deleted.Id, TestContext.Current.CancellationToken));
        Assert.Equal("after", (await f.Library.FindAsync(retitled.Id, TestContext.Current.CancellationToken))!.Title);
    }

    [Fact]
    public async Task Problem_folders_are_reported_and_left_untouched()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var good = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", Title = "good" });

        // Audio without metadata (e.g. copied in by hand).
        Directory.CreateDirectory(f.PathOf("Projects/P/orphan"));
        await File.WriteAllBytesAsync(f.PathOf("Projects/P/orphan", "audio.wav"), [1, 2, 3], TestContext.Current.CancellationToken);
        // Broken metadata.
        Directory.CreateDirectory(f.PathOf("Projects/P/broken"));
        await File.WriteAllTextAsync(f.PathOf("Projects/P/broken", LibraryLayout.MetadataFile), "{ oops", TestContext.Current.CancellationToken);
        // A duplicated folder (same id).
        var copy = f.PathOf("Projects/P/zz-copy");
        Directory.CreateDirectory(copy);
        File.Copy(f.PathOf(good.Folder, LibraryLayout.MetadataFile), Path.Combine(copy, LibraryLayout.MetadataFile));

        var before = f.SnapshotLibrary();
        var report = await f.Library.SynchronizeAsync();

        Assert.Equal(before, f.SnapshotLibrary());
        Assert.Equal(1, report.Total);
        Assert.Contains(report.Issues, i => i.Kind == LibraryIssueKind.MissingMetadata && i.Folder == "Projects/P/orphan");
        Assert.Contains(report.Issues, i => i.Kind == LibraryIssueKind.UnreadableMetadata && i.Folder == "Projects/P/broken");
        Assert.Contains(report.Issues, i => i.Kind == LibraryIssueKind.DuplicateId && i.Folder == "Projects/P/zz-copy");
        Assert.Equal(3, (await f.Library.ListIssuesAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Json_artifacts_are_utf8_without_bom()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var e = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(e.Id));

        foreach (var file in f.Store.ListFiles(e.Folder))
        {
            var bytes = await File.ReadAllBytesAsync(f.PathOf(e.Folder, file), TestContext.Current.CancellationToken);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), file);
        }
    }
}
