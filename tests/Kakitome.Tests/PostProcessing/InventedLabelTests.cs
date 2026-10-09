using Kakitome.Application.Decision;

namespace Kakitome.Tests.PostProcessing;

public sealed class InventedLabelTests
{
    [Fact]
    public void Labels_that_keep_coming_back_are_removed_and_single_ones_or_times_and_urls_are_kept()
    {
        string[] texts =
        [
            "絶対高いから! 黒玉:思ったより多い! 黒玉:戻そう",
            "黒玉:278円黒玉:いい時代になったもんだ",
            "注意:ここは一回だけ。12:30に集合。https://example.com を見て",
        ];

        var labels = InventedLabels.Find(texts, "ja");
        Assert.Equal(["黒玉"], labels);

        Assert.Equal("絶対高いから! 思ったより多い! 戻そう", Clean(texts[0], labels));
        Assert.Equal("278円いい時代になったもんだ", Clean(texts[1], labels));
        Assert.Equal(texts[2], Clean(texts[2], labels));
        Assert.Empty(InventedLabels.Find(["Q: a. Q: b. Q: c."], "en")); // English transcripts are left alone
    }

    [Fact]
    public void Overlapping_edits_never_corrupt_the_text()
    {
        EditCandidate[] edits =
        [
            new(EditKind.Formatting, EditRisk.Low, 0, 3, string.Empty, "label"),
            new(EditKind.Filler, EditRisk.Low, 2, 3, string.Empty, "overlapping"),
        ];

        Assert.Equal("えーと本題", CleanupJobHandler.Apply("黒玉:えーと本題", edits));
    }

    private static string Clean(string text, IReadOnlySet<string> labels) =>
        CleanupJobHandler.Apply(text, InventedLabels.Candidates(text, labels));
}
