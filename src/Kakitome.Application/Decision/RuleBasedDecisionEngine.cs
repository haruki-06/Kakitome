using System.Text.RegularExpressions;

namespace Kakitome.Application.Decision;

/// <summary>
/// Deterministic rules for Japanese (primary) and English. Conservative by design: only standalone hesitation words
/// delimited by punctuation, spaces or segment edges are low-risk; anything ambiguous ("あの" before a noun, "その",
/// "まあ" mid-sentence) is a high-risk suggestion or left alone.
/// </summary>
public sealed partial class RuleBasedDecisionEngine : IDecisionEngine
{
    public const string EngineId = "rules-v1";

    public string Id => EngineId;

    public IReadOnlyList<EditCandidate> FindEditCandidates(string text, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        var candidates = new List<EditCandidate>();
        var ja = language is null || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

        if (ja)
        {
            foreach (Match m in JapaneseLowRiskFiller().Matches(text))
            {
                candidates.Add(new EditCandidate(EditKind.Filler, EditRisk.Low, m.Index, m.Length, string.Empty, $"filler \"{m.Groups["f"].Value}\""));
            }

            foreach (Match m in JapaneseAmbiguousFiller().Matches(text))
            {
                if (!Overlaps(candidates, m.Index, m.Length))
                {
                    candidates.Add(new EditCandidate(EditKind.Filler, EditRisk.High, m.Index, m.Length, string.Empty, $"possible filler \"{m.Groups["f"].Value}\""));
                }
            }

            foreach (Match m in JapaneseRepetition().Matches(text))
            {
                // "それで、それで" → keep one; the first occurrence plus its delimiter is removed.
                var first = m.Groups["w"];
                var removeLength = m.Groups["d"].Index + m.Groups["d"].Length - m.Index;
                if (!Overlaps(candidates, m.Index, removeLength))
                {
                    candidates.Add(new EditCandidate(EditKind.Repetition, EditRisk.Low, m.Index, removeLength, string.Empty, $"repeated \"{first.Value}\""));
                }
            }

            foreach (Match m in JapaneseStutterLoop().Matches(text))
            {
                // "誰が入って誰が入って誰が入って" (a decoder loop or a stammer) → keep one occurrence.
                var unit = m.Groups["p"].Value;
                if (!Overlaps(candidates, m.Index, m.Length))
                {
                    candidates.Add(new EditCandidate(EditKind.Repetition, EditRisk.Low, m.Index + unit.Length, m.Length - unit.Length, string.Empty, $"repeated \"{unit}\""));
                }
            }
        }

        foreach (Match m in EnglishFiller().Matches(text))
        {
            if (!Overlaps(candidates, m.Index, m.Length))
            {
                candidates.Add(new EditCandidate(EditKind.Filler, EditRisk.Low, m.Index, m.Length, string.Empty, $"filler \"{m.Groups["f"].Value.Trim()}\""));
            }
        }

        foreach (Match m in EnglishRepetition().Matches(text))
        {
            if (!Overlaps(candidates, m.Index, m.Length))
            {
                candidates.Add(new EditCandidate(EditKind.Repetition, EditRisk.Low, m.Index, m.Groups["w"].Length + 1, string.Empty, $"repeated \"{m.Groups["w"].Value}\""));
            }
        }

        foreach (Match m in MultipleSpaces().Matches(text))
        {
            if (!Overlaps(candidates, m.Index, m.Length))
            {
                candidates.Add(new EditCandidate(EditKind.Formatting, EditRisk.Low, m.Index, m.Length, " ", "extra spaces"));
            }
        }

        return candidates.OrderBy(c => c.Start).ToList();
    }

    public IReadOnlyList<string> AssessSegment(string text, double? confidence, double durationSeconds, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        var flags = new List<string>();
        if (confidence is < 0.5)
        {
            flags.Add("lowConfidence");
        }

        // Whisper-style hallucinations on silence/music.
        if (HallucinationPhrase().IsMatch(text))
        {
            flags.Add("possibleHallucination");
        }

        // The same short phrase looping many times.
        if (LoopingPhrase().IsMatch(text))
        {
            flags.Add("repetitionLoop");
        }

        // Far more characters than can be spoken in the time (≈ 12 chars/s Japanese, 25 chars/s English).
        var maxRate = language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? 25 : 12;
        if (durationSeconds > 0.5 && text.Length / durationSeconds > maxRate * 1.8)
        {
            flags.Add("implausibleRate");
        }

        return flags;
    }

    public RecordingTypeDecision ClassifyRecording(IReadOnlyList<string> segmentTexts, int speakerCount, double durationSeconds, string? language)
    {
        ArgumentNullException.ThrowIfNull(segmentTexts);
        var all = string.Concat(segmentTexts);
        var lecture = Count(all, "講義", "授業", "説明します", "見ていきます", "今日は", "演習", "lecture", "chapter");
        var meeting = Count(all, "議題", "会議", "進捗", "決定", "担当", "次回", "報告", "agenda", "action item");
        var interview = Count(all, "教えていただけますか", "きっかけ", "インタビュー", "伺", "interview");

        if (durationSeconds < 180 && speakerCount <= 1 && lecture + meeting + interview < 2)
        {
            return new RecordingTypeDecision(RecordingType.VoiceNote, 0.6, "short single-speaker recording");
        }

        var best = new[] { (RecordingType.Lecture, lecture), (RecordingType.Meeting, meeting), (RecordingType.Interview, interview) }
            .OrderByDescending(x => x.Item2).First();
        if (best.Item2 == 0)
        {
            return new RecordingTypeDecision(RecordingType.Unknown, 0.3, "no type cues");
        }

        var total = lecture + meeting + interview;
        return new RecordingTypeDecision(best.Item1, Math.Round((double)best.Item2 / total, 2), $"{best.Item2} of {total} type cues");
    }

    private static int Count(string text, params string[] cues) =>
        cues.Sum(c => Regex.Count(text, Regex.Escape(c), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    private static bool Overlaps(List<EditCandidate> list, int start, int length) =>
        list.Any(c => start < c.Start + c.Length && c.Start < start + length);

    // Standalone hesitations: at a boundary (start, punctuation, space) and followed by a boundary.
    [GeneratedRegex(@"(?<=^|[、。,.\s！？!?])(?<f>え[ーっ]+と?|えっと|えと|あの[ーぉ]+|うーん|んー+|ええと|あー+|まー+)(?:[、,]\s*|\s+|(?=[。.!?！？]|$))", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JapaneseLowRiskFiller();

    // Words that are fillers only sometimes ("あの" can be a demonstrative, "まあ" can carry meaning).
    [GeneratedRegex(@"(?<=^|[、。,.\s])(?<f>あの|その|まあ|なんか|なんというか)[、,]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JapaneseAmbiguousFiller();

    // A word of 2–10 chars immediately repeated after a comma/space: "それで、それで".
    [GeneratedRegex(@"(?<w>[\p{L}\p{N}ー]{2,10})(?<d>[、,]\s*|\s+)\k<w>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JapaneseRepetition();

    // A phrase of 4–15 characters said three or more times back to back, without a pause mark.
    [GeneratedRegex(@"(?<p>[\p{L}\p{N}ー]{4,15}?)\k<p>{2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JapaneseStutterLoop();

    [GeneratedRegex(@"(?<=^|[\s,.;])(?<f>(?:um+|uh+|erm|hmm+)[,]?\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EnglishFiller();

    [GeneratedRegex(@"\b(?<w>[A-Za-z']+)\s+\k<w>\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EnglishRepetition();

    [GeneratedRegex(@" {2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"ご視聴ありがとうございました|チャンネル登録|字幕は|Thanks for watching|Subtitles by", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HallucinationPhrase();

    [GeneratedRegex(@"(.{2,12}?)\1{4,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LoopingPhrase();
}
