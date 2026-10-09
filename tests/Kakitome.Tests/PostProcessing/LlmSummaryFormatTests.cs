using System.Text.Json;
using Kakitome.Application.Summaries;
using Kakitome.Infrastructure.Summaries;

namespace Kakitome.Tests.PostProcessing;

public sealed class LlmSummaryFormatTests
{
    private static readonly SummarySourceSegment[] Segments =
    [
        new("seg-a", 0, 5, "では始めます。", "田中"),
        new("seg-b", 6, 11, "リリースは十一月五日で確定とします。", "田中"),
        new("seg-c", 12, 17, "佐藤さんは金曜日までにテスト環境を整えてください。", "田中"),
    ];

    [Fact]
    public void Prompt_numbers_lines_with_time_and_speaker()
    {
        var prompt = LlmSummaryFormat.UserPrompt(new SummaryInput("定例", "ja", null, Segments));

        Assert.Contains("タイトル: 定例", prompt, StringComparison.Ordinal);
        Assert.Contains("[2] (00:00:06) 田中: リリースは十一月五日で確定とします。", prompt, StringComparison.Ordinal);
        Assert.Contains("Japanese", LlmSummaryFormat.SystemPrompt("ja"), StringComparison.Ordinal);
        Assert.Contains("English", LlmSummaryFormat.SystemPrompt("en"), StringComparison.Ordinal);
    }

    [Fact]
    public void Output_maps_line_citations_to_segments_and_drops_invalid_ones()
    {
        const string json = """
            {"title":"定例","overview":"リリース日を決めた。","keyPoints":[{"text":"リリース日","lines":[2]}],
             "decisions":[{"text":"リリースは11月5日","lines":[2, 99]}],
             "actionItems":[{"text":"テスト環境を整える","owner":"佐藤","due":"金曜日","lines":[3]},{"text":"","owner":null,"due":null,"lines":[]}],
             "questions":[],"topics":["リリース","リリース","テスト"]}
            """;

        var draft = LlmSummaryFormat.Parse(json, Segments, "llama.cpp", "m");

        var decision = Assert.Single(draft.Decisions);
        Assert.Equal(["seg-b"], decision.SegmentIds);
        Assert.Equal(6, decision.AtSeconds);
        var action = Assert.Single(draft.ActionItems); // the empty one is skipped
        Assert.Equal(("佐藤", "金曜日", 12.0), (action.Owner, action.Due, action.AtSeconds!.Value));
        Assert.Equal(["リリース", "テスト"], draft.Topics);
        Assert.Equal("llama.cpp", draft.Engine);
    }

    [Fact]
    public void Items_without_valid_citations_keep_their_text_but_no_time()
    {
        var draft = LlmSummaryFormat.Parse("""{"title":"t","overview":"o","keyPoints":[{"text":"点","lines":[0, 7]}],"decisions":[],"actionItems":[],"questions":[],"topics":[]}""", Segments, "e", "m");

        var point = Assert.Single(draft.KeyPoints);
        Assert.Null(point.AtSeconds);
        Assert.Null(point.SegmentIds);
    }

    [Fact]
    public void Malformed_output_is_reported() =>
        Assert.ThrowsAny<JsonException>(() => LlmSummaryFormat.Parse("{ not json", Segments, "e", "m"));

    [Fact]
    public void Long_transcripts_are_chunked_without_splitting_or_dropping_lines()
    {
        var segments = Enumerable.Range(1, 100).Select(i => new SummarySourceSegment($"s{i}", i, i + 1, new string('あ', 10), null)).ToList();

        var chunks = LlamaSummaryProvider.Chunk(segments, _ => 100);

        Assert.Equal(3, chunks.Count); // 45 + 45 + 10 lines at 100 tokens each
        Assert.Equal(segments.Select(s => s.Id), chunks.SelectMany(c => c).Select(s => s.Id));
        Assert.All(chunks, c => Assert.True(c.Count * 100 <= LlamaSummaryProvider.ChunkTokens));
    }

    [Fact]
    public void Grammar_covers_every_field_the_parser_reads()
    {
        foreach (var field in new[] { "kind", "title", "overview", "keyPoints", "decisions", "actionItems", "questions", "topics", "owner", "due", "lines", "text" })
        {
            Assert.Contains($"\\\"{field}\\\"", LlmSummaryFormat.Grammar, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_meetings_keep_decisions_and_only_meetings_and_lectures_keep_action_items_and_questions()
    {
        var item = new DraftItem("x", 1, null);
        var draft = new SummaryDraft
        {
            Engine = "e",
            Decisions = [item],
            ActionItems = [new DraftAction("ate one more", 1, null, "audio-50", null)],
            Questions = [item],
        };

        var chat = LlmSummaryFormat.ApplyKind(draft with { Kind = "conversation" });
        Assert.Empty(chat.Decisions);
        Assert.Empty(chat.ActionItems);
        Assert.Empty(chat.Questions);

        var lecture = LlmSummaryFormat.ApplyKind(draft with { Kind = "lecture" });
        Assert.Empty(lecture.Decisions);
        Assert.Single(lecture.ActionItems);
        Assert.Single(lecture.Questions);

        var meeting = LlmSummaryFormat.ApplyKind(draft with { Kind = "meeting" });
        Assert.Single(meeting.Decisions);
        Assert.Single(meeting.ActionItems);
        Assert.Equal("conversation", LlmSummaryFormat.Parse("""{"kind":"conversation","title":"t","overview":"o","keyPoints":[],"decisions":[],"actionItems":[],"questions":[],"topics":[]}""", [], "e", "m").Kind);
    }

    [Fact]
    public void The_final_pass_requires_at_least_three_key_points()
    {
        Assert.Contains("kitems  ::= \"[\" ws item ( \",\" ws item ){2,7}", LlmSummaryFormat.ReduceGrammar, StringComparison.Ordinal);
        Assert.Contains("keyPoints\\\":\" ws kitems", LlmSummaryFormat.ReduceGrammar, StringComparison.Ordinal);
        Assert.Contains("keyPoints\\\":\" ws items", LlmSummaryFormat.Grammar, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("誰も指定なし")]
    [InlineData("指定なし")]
    [InlineData("未定")]
    public void Placeholder_owners_are_treated_as_missing(string owner) =>
        Assert.True(Kakitome.Domain.Rendering.ReadableText.IsMissing(owner));
}
