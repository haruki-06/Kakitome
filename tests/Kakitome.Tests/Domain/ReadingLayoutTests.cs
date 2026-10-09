using Kakitome.Domain.Rendering;

namespace Kakitome.Tests.Domain;

public sealed class ReadingLayoutTests
{
    private sealed record Seg(double Start, double End, string Text, string? Speaker = null);

    private static List<ReadingBlock<Seg>> Layout(bool japanese, params Seg[] segments) =>
        ReadingLayout.Blocks(segments, s => s.Start, s => s.End, s => s.Speaker, s => s.Text, japanese);

    private static string Read(IEnumerable<ReadingBlock<Seg>> blocks) =>
        string.Join("|", blocks.Select(b => string.Concat(b.Pieces.Select(p => p.Text))));

    [Fact]
    public void A_sentence_end_is_added_where_the_speaker_paused_but_not_after_a_comma_or_mid_sentence()
    {
        var blocks = Layout(true,
            new Seg(0, 2, "本日の議題は二つあります"),       // pause after → 。
            new Seg(3, 5, "まず予算について"),               // no pause → runs on
            new Seg(5.2, 7, "説明します、"),                 // comma, pause → stays
            new Seg(8, 9, "よろしいでしょうか？"));           // already punctuated

        Assert.Equal("本日の議題は二つあります。まず予算について説明します、よろしいでしょうか？", Read(blocks));
    }

    [Fact]
    public void Paragraphs_break_after_a_few_sentences_and_before_a_new_topic()
    {
        var blocks = Layout(true,
            new Seg(0, 1, "一つ目です。"), new Seg(1, 2, "二つ目です。"), new Seg(2, 3, "三つ目です。"),
            new Seg(3, 4, "四つ目です。"), new Seg(4, 5, "次に、別の話です。"));

        Assert.Equal("一つ目です。二つ目です。三つ目です。|四つ目です。|次に、別の話です。", Read(blocks));
    }

    [Fact]
    public void Speaker_changes_and_long_pauses_start_a_new_paragraph_and_every_segment_keeps_its_piece()
    {
        var segments = new[]
        {
            new Seg(0, 2, "Hello there", "A"), new Seg(2.1, 3, "how are you?", "A"),
            new Seg(3.5, 5, "Fine, thanks.", "B"), new Seg(20, 22, "Shall we start", "B"),
        };
        var blocks = Layout(false, segments);

        Assert.Equal(["A", "B", "B"], blocks.Select(b => b.Speaker));
        Assert.Equal("Shall we start.", blocks[2].Pieces[0].Text);
        Assert.Equal(segments, blocks.SelectMany(b => b.Pieces).Select(p => p.Segment));
    }

    [Fact]
    public void Playback_time_shows_position_and_length()
    {
        Assert.Equal("00:00 / 32:12", TimeFormat.Playback(TimeSpan.Zero, TimeSpan.FromSeconds(1932)));
        Assert.Equal("08:36 / 32:12", TimeFormat.Playback(TimeSpan.FromSeconds(516.4), TimeSpan.FromSeconds(1932)));
        Assert.Equal("0:08:36 / 1:27:05", TimeFormat.Playback(TimeSpan.FromSeconds(516), TimeSpan.FromSeconds(5225)));
    }
}
