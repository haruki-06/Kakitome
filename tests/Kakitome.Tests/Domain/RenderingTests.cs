using Kakitome.Domain.Recordings;
using Kakitome.Domain.Rendering;
using Kakitome.Domain.Transcripts;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Domain;

public sealed class RenderingTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(5.9, "00:00:05")]
    [InlineData(3723, "01:02:03")]
    [InlineData(90061, "25:01:01")]
    [InlineData(-3, "00:00:00")]
    public void Clock_format(double seconds, string expected) => Assert.Equal(expected, TimeFormat.Clock(seconds));

    [Fact]
    public void Transcript_markdown_is_readable_paragraphs_with_speakers_and_timestamps()
    {
        var metadata = Samples.Metadata();
        var md = TranscriptRenderer.ToMarkdown(metadata, Samples.Transcript(metadata.Id));

        const string expected = """
            # 機械学習 第3回

            - プロジェクト: 大学講義
            - 録音日時: 2026-09-30 10:15
            - 長さ: 01:02:03
            - 言語: ja

            [00:00:05] **話者 1:** それでは始めます。今日は勾配降下法です。

            [00:00:20] **田中:** 質問があります。

            [01:01:40] **話者 1:** 以上です。

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), md);
    }

    [Fact]
    public void English_segments_are_joined_with_spaces_and_labels_are_English()
    {
        var metadata = Samples.Metadata();
        metadata.Language = "en";
        var transcript = new TranscriptDocument
        {
            RecordingId = metadata.Id,
            Language = "en",
            Segments =
            [
                new TranscriptSegment { Id = "a", StartSeconds = 0, EndSeconds = 2, Text = "Hello there." },
                new TranscriptSegment { Id = "b", StartSeconds = 2, EndSeconds = 4, Text = "Let's start." },
            ],
        };

        var txt = TranscriptRenderer.ToPlainText(metadata, transcript);

        Assert.Contains("September 30, 2026 10:15 · 1 h 2 min · 大学講義", txt, StringComparison.Ordinal);
        Assert.Contains("Hello there. Let's start.", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("[00:", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("**", txt, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_monologue_is_split_into_paragraphs()
    {
        var metadata = Samples.Metadata();
        var segments = Enumerable.Range(0, 30)
            .Select(i => new TranscriptSegment { Id = $"s{i}", StartSeconds = i * 5, EndSeconds = i * 5 + 5, Text = $"文{i}。" })
            .ToList();
        var transcript = new TranscriptDocument { RecordingId = metadata.Id, Language = "ja", Segments = segments };

        var paragraphs = TranscriptRenderer.BuildParagraphs(transcript);

        Assert.True(paragraphs.Count >= 3);
        Assert.All(paragraphs, p => Assert.DoesNotContain("\n", p.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Newlines_inside_segment_text_cannot_break_the_layout()
    {
        var metadata = Samples.Metadata();
        var transcript = new TranscriptDocument
        {
            RecordingId = metadata.Id,
            Language = "ja",
            Segments = [new TranscriptSegment { Id = "a", StartSeconds = 0, EndSeconds = 1, Text = "一行目\r\n# 見出しではない" }],
        };

        var md = TranscriptRenderer.ToMarkdown(metadata, transcript);

        Assert.Contains("[00:00:00] 一行目 # 見出しではない", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_markdown_renders_structured_sections_and_omits_empty_ones()
    {
        var metadata = Samples.Metadata();
        var md = SummaryRenderer.ToMarkdown(metadata, Samples.Summary(metadata.Id));

        Assert.StartsWith("# 勾配降下法の基礎\n", md, StringComparison.Ordinal);
        Assert.Contains("## 概要\n\n勾配降下法の考え方", md, StringComparison.Ordinal);
        Assert.Contains("- 学習率が大きすぎると発散する [00:02:05]", md, StringComparison.Ordinal);
        Assert.Contains("- [ ] 演習問題 3 を解く (担当: 受講者、期限: 来週月曜)", md, StringComparison.Ordinal);
        Assert.Contains("## トピック\n\n最適化、学習率", md, StringComparison.Ordinal);
        Assert.DoesNotContain("質問・未解決事項", md, StringComparison.Ordinal);
        Assert.Contains("Kakitome がローカルで生成 (test-llm / small, 2026-09-30 12:15)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_plain_text_uses_language_specific_headings()
    {
        var metadata = Samples.Metadata();
        var summary = Samples.Summary(metadata.Id);
        var ja = SummaryRenderer.ToPlainText(metadata, summary);
        summary.Language = "en";
        var en = SummaryRenderer.ToPlainText(metadata, summary);

        Assert.Contains("【要点】\n・学習率", ja, StringComparison.Ordinal);
        Assert.Contains("[Key points]\n- 学習率", en, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_title_falls_back_to_recording_title()
    {
        var metadata = Samples.Metadata();
        var summary = Samples.Summary(RecordingId.New());
        summary.Title = null;

        Assert.StartsWith("# 機械学習 第3回", SummaryRenderer.ToMarkdown(metadata, summary), StringComparison.Ordinal);
    }
}
