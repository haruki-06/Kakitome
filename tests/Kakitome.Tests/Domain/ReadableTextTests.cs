using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Rendering;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Domain;

/// <summary>transcript.txt / summary.txt are for reading: no timestamps, short paragraphs, natural labels.</summary>
public sealed class ReadableTextTests
{
    [Fact]
    public void Transcript_reads_as_paragraphs_without_timestamps()
    {
        var metadata = Samples.Metadata();
        var transcript = new TranscriptDocument
        {
            RecordingId = metadata.Id,
            Language = "ja",
            Speakers = [new SpeakerInfo { Id = "S1", Label = "田中" }, new SpeakerInfo { Id = "S2", Label = "佐藤" }],
            Segments =
            [
                new TranscriptSegment { Id = "a", StartSeconds = 0, EndSeconds = 4, Speaker = "S1", Text = "では始めます。今日は三つ話します。" },
                new TranscriptSegment { Id = "b", StartSeconds = 4, EndSeconds = 9, Speaker = "S1", Text = "一つ目はリリースです。二つ目はテストです。三つ目は予算です。" },
                new TranscriptSegment { Id = "c", StartSeconds = 10, EndSeconds = 13, Speaker = "S2", Text = "リリースは十一月五日で問題ありません。" },
                new TranscriptSegment { Id = "d", StartSeconds = 14, EndSeconds = 16, Speaker = "S1", Text = "では確定とします。" },
            ],
        };

        var text = ReadableText.Transcript(metadata, transcript);
        var paragraphs = text.TrimEnd('\n').Split("\n\n");

        Assert.Equal("機械学習 第3回", paragraphs[0].Split('\n')[0]);
        Assert.Equal("2026年9月30日 10:15 ・ 1時間2分 ・ 大学講義", paragraphs[0].Split('\n')[1]);
        Assert.DoesNotContain("[00:", text, StringComparison.Ordinal);
        Assert.Equal("田中：では始めます。今日は三つ話します。一つ目はリリースです。", paragraphs[1]); // at most 3 sentences
        Assert.Equal("二つ目はテストです。三つ目は予算です。", paragraphs[2]);                    // same speaker: no label again
        Assert.Equal("佐藤：リリースは十一月五日で問題ありません。", paragraphs[3]);
        Assert.Equal("田中：では確定とします。", paragraphs[4]);
    }

    [Fact]
    public void A_single_speaker_is_never_labelled()
    {
        var metadata = Samples.Metadata();
        var transcript = new TranscriptDocument
        {
            RecordingId = metadata.Id,
            Language = "ja",
            Speakers = [new SpeakerInfo { Id = "S1" }],
            Segments = [new TranscriptSegment { Id = "a", StartSeconds = 0, EndSeconds = 4, Speaker = "S1", Text = "講義を始めます。" }],
        };

        Assert.EndsWith("\n\n講義を始めます。\n", ReadableText.Transcript(metadata, transcript), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("報告します。次に、品質の話です。", true, 2)]
    [InlineData("報告します。最後に、来月の話です。", true, 2)]
    [InlineData("報告します。まずまずの結果でした。", true, 1)] // "まず" inside a word is not an opener
    [InlineData("That was the demo. Next, the budget.", false, 2)]
    public void Topic_openers_start_a_new_paragraph(string text, bool japanese, int expectedBlocks) =>
        Assert.Equal(expectedBlocks, ReadableText.Blocks(text, japanese).Count);

    [Fact]
    public void Unpunctuated_speech_is_cut_at_the_recognizers_pauses()
    {
        // Whisper on long lectures: hardly any 「。」, pauses marked by spaces.
        var phrase = "それに関する裁判がですね いよいよ最高裁判所の方まで上がってきました ";
        var text = string.Concat(Enumerable.Repeat(phrase, 12)).Trim();

        var blocks = ReadableText.Blocks(text, japanese: true);

        Assert.True(blocks.Count >= 4); // ~440 characters in blocks of at most 120
        Assert.All(blocks, b => Assert.True(b.Length <= ReadableText.MaxCharsJapanese));
        Assert.All(blocks, b => Assert.False(b.StartsWith(' ') || b.EndsWith(' ')));
        Assert.Equal(text.Replace(" ", string.Empty, StringComparison.Ordinal), string.Concat(blocks).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_without_any_pause_marker_is_still_bounded()
    {
        var blocks = ReadableText.Blocks(new string('あ', 500), japanese: true);

        Assert.All(blocks, b => Assert.True(b.Length <= ReadableText.MaxCharsJapanese));
        Assert.Equal(500, blocks.Sum(b => b.Length));
    }

    [Fact]
    public void Long_speech_is_broken_into_readable_blocks()
    {
        var blocks = ReadableText.Blocks(string.Concat(Enumerable.Repeat("短い文です。", 40)), japanese: true);

        Assert.All(blocks, b => Assert.True(b.Length <= ReadableText.MaxCharsJapanese));
        Assert.Equal(40 * 6, blocks.Sum(b => b.Length));
    }

    [Fact]
    public void Summary_reads_without_timestamps_and_hides_missing_owner_or_due()
    {
        var metadata = Samples.Metadata();
        var summary = new SummaryDocument
        {
            RecordingId = metadata.Id,
            Language = "ja",
            Title = "週次定例",
            Overview = "リリース日を決めた。テスト環境は金曜日までに整える。",
            KeyPoints = [new SummaryItem { Text = "リリースは十一月五日", AtSeconds = 12 }],
            Decisions = [new SummaryItem { Text = "リリース日を確定", AtSeconds = 12 }],
            ActionItems =
            [
                new ActionItem { Text = "テスト環境を整える", Owner = "佐藤", Due = "金曜日", AtSeconds = 20 },
                new ActionItem { Text = "予算を見直す", Owner = "null", Due = "なし" },
            ],
            Topics = ["リリース", "テスト"],
            Generation = new SummaryGeneration { Engine = new EngineInfo { Provider = "test" }, CreatedAt = Samples.Jst },
        };

        var text = ReadableText.Summary(metadata, summary);

        Assert.DoesNotContain("[00:", text, StringComparison.Ordinal);
        Assert.Contains("【概要】\nリリース日を決めた。テスト環境は金曜日までに整える。\n", text, StringComparison.Ordinal);
        Assert.Contains("・テスト環境を整える（担当：佐藤／期限：金曜日）\n", text, StringComparison.Ordinal);
        Assert.Contains("・予算を見直す\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
        Assert.Contains("【トピック】\nリリース、テスト", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_refresh_writes_reading_text_for_older_recordings_but_not_over_user_edits()
    {
        await using var f = await LibraryFixture.CreateAsync(new LibraryOutputOptions { WriteText = false });
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", Language = "ja" });
        var b = await f.Library.CreateRecordingAsync(new NewRecording { Project = "Q", Language = "ja" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id));
        await f.Library.SaveTranscriptAsync(Samples.Transcript(b.Id));
        var folderB = await f.Library.GetRecordingPathAsync(b.Id);
        await File.WriteAllTextAsync(Path.Combine(folderB, LibraryLayout.TranscriptTextFile), "my own notes");

        // The same Library opened by a build that writes reading text (as after an update).
        await f.RestartAsync();
        var refreshed = new LibraryService(
            f.Store, f.Index, Microsoft.Extensions.Options.Options.Create(new LibraryOutputOptions { WriteText = true }),
            f.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        var written = await refreshed.RefreshReadableTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, written); // a gets transcript.txt; b keeps the user's own file
        var folderA = await refreshed.GetRecordingPathAsync(a.Id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("[00:", await File.ReadAllTextAsync(Path.Combine(folderA, LibraryLayout.TranscriptTextFile), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("my own notes", await File.ReadAllTextAsync(Path.Combine(folderB, LibraryLayout.TranscriptTextFile), TestContext.Current.CancellationToken));
        Assert.Equal(0, await refreshed.RefreshReadableTextAsync(TestContext.Current.CancellationToken)); // idempotent
    }

    [Fact]
    public async Task Text_in_the_earlier_timestamped_format_is_converted()
    {
        await using var f = await LibraryFixture.CreateAsync(new LibraryOutputOptions { WriteText = false });
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", Language = "ja" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id));
        var folder = await f.Library.GetRecordingPathAsync(a.Id);
        await File.WriteAllTextAsync(Path.Combine(folder, LibraryLayout.TranscriptTextFile), "タイトル\n\n[00:00:05] それでは始めます。\n");

        await f.RestartAsync();
        var refreshed = new LibraryService(
            f.Store, f.Index, Microsoft.Extensions.Options.Options.Create(new LibraryOutputOptions { WriteText = true }),
            f.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);
        await refreshed.RebuildIndexAsync(TestContext.Current.CancellationToken); // stamps the existing files as baseline

        Assert.Equal(1, await refreshed.RefreshReadableTextAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("[00:", await File.ReadAllTextAsync(Path.Combine(folder, LibraryLayout.TranscriptTextFile), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
