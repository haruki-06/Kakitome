using Kakitome.Infrastructure.Asr;

namespace Kakitome.Tests.Asr;

public sealed class UtteranceSplitterTests
{
    private const int Rate = 16_000;

    [Fact]
    public void Long_pauses_always_split_and_soft_onsets_are_kept()
    {
        // 2 s loud speech, 1 s silence, 0.4 s soft onset (-30 dB) then 2 s loud speech.
        var audio = Concat(Tone(2, 0.3f), Silence(1), Tone(0.4, 0.01f), Tone(2, 0.3f));

        var utterances = SherpaOnnxProvider.Session.Utterances(audio);

        Assert.Equal(2, utterances.Count);
        var second = utterances[1];
        Assert.True(second.Start / (double)Rate <= 3.0, "the soft onset at 3.0 s must be inside the second utterance");
        Assert.True((second.Start + second.Length) / (double)Rate >= 5.3);
    }

    [Fact]
    public void Continuous_speech_is_cut_into_utterances_of_at_most_about_six_seconds_at_pauses()
    {
        // 20 s of speech with a 0.2 s pause every 2.5 s.
        var parts = new List<float[]>();
        for (var i = 0; i < 8; i++)
        {
            parts.Add(Tone(2.3, 0.3f));
            parts.Add(Silence(0.2));
        }

        var audio = Concat([.. parts]);
        var utterances = SherpaOnnxProvider.Session.Utterances(audio);

        Assert.All(utterances, u => Assert.True(u.Length / (double)Rate <= 6.5, $"{u.Length / (double)Rate:F2}s"));
        Assert.True(utterances.Count >= 4);
        // Together they cover all speech.
        Assert.True(utterances[0].Start == 0);
        Assert.True(utterances[^1].Start + utterances[^1].Length >= audio.Length - (Rate / 4));
    }

    [Fact]
    public void Silence_is_padded_around_each_utterance()
    {
        var padded = SherpaOnnxProvider.Session.WithSilence(Tone(1, 0.3f));
        Assert.Equal(Rate + (2 * (int)(0.3 * Rate)), padded.Length);
        Assert.Equal(0, padded[0]);
        Assert.Equal(0, padded[^1]);
    }

    private static float[] Tone(double seconds, float amplitude) =>
        AsrPipelineTests.Tone(Rate, 220, seconds, amplitude);

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();
}
