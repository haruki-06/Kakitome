using Kakitome.Application.Asr;

namespace Kakitome.Tests.Asr;

public sealed class AsrMetricsTests
{
    [Fact]
    public void Japanese_normalization_ignores_punctuation_width_and_spaces()
    {
        Assert.Equal(AsrMetrics.NormalizeForCer("ＧｉｔＨｕｂで、テスト！"), AsrMetrics.NormalizeForCer("github で テスト"));
        Assert.Equal(0, AsrMetrics.Cer("それでは始めます。", "それでは 始めます").Edits);
    }

    [Fact]
    public void Cer_counts_substitutions_insertions_and_deletions()
    {
        var r = AsrMetrics.Cer("今日は晴れです", "今日は雨です。");
        Assert.Equal(2, r.Edits); // 晴れ → 雨 (1 substitution + 1 deletion)
        Assert.Equal(7, r.ReferenceLength);
        Assert.Equal(2 / 7.0, r.Rate, 6);
    }

    [Fact]
    public void Wer_is_word_based_and_case_insensitive()
    {
        var r = AsrMetrics.Wer("Thank you all for joining today.", "thank you for joining today");
        Assert.Equal(1, r.Edits);
        Assert.Equal(6, r.ReferenceLength);
    }

    [Fact]
    public void Error_rates_aggregate_by_summing_edits_and_lengths()
    {
        var total = AsrMetrics.Cer("あいう", "あい") + AsrMetrics.Cer("かきくけこ", "かきくけこ");
        Assert.Equal(new ErrorRate(1, 8), total);
        Assert.Equal(0.125, total.Rate);
    }

    [Fact]
    public void Empty_reference_is_handled()
    {
        Assert.Equal(0, AsrMetrics.Cer(string.Empty, string.Empty).Rate);
        Assert.Equal(1, AsrMetrics.Cer(string.Empty, "x").Rate);
    }

    [Fact]
    public void Noise_mixing_hits_the_requested_snr_deterministically()
    {
        var clean = AsrPipelineTests.Tone(16_000, 300, 2, 0.3f);
        var a = Kakitome.Bench.CorpusBuilder.AddNoise(clean, 15, seed: 1);
        var b = Kakitome.Bench.CorpusBuilder.AddNoise(clean, 15, seed: 1);
        Assert.Equal(a, b);

        double signal = clean.Select(v => (double)v * v).Average();
        double noise = a.Zip(clean, (m, c) => (double)(m - c) * (m - c)).Average();
        Assert.InRange(10 * Math.Log10(signal / noise), 14.5, 15.5);
    }
}
