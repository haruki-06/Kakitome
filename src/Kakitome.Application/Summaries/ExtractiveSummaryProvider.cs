using System.Text.RegularExpressions;
using Kakitome.Application.Jobs;

namespace Kakitome.Application.Summaries;

/// <summary>
/// Offline, deterministic summary built only from sentences that were actually said: key sentences by term salience,
/// decisions / action items / questions by Japanese and English cue patterns, topics by frequent terms. Never invents
/// content, so it is the safe fallback when no local LLM is installed (and the low-power choice on battery).
/// </summary>
public sealed partial class ExtractiveSummaryProvider : ISummaryProvider
{
    public const string ProviderId = "extractive-v1";

    private static readonly HashSet<string> StopTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "これ", "それ", "あれ", "ここ", "そこ", "今日", "今回", "前回", "次回", "皆さん", "本日", "場合", "部分", "ところ", "ため", "よう",
        "こと", "もの", "とき", "感じ", "the", "and", "that", "this", "with", "have", "will", "from", "they", "there", "about",
    };

    public string Id => ProviderId;

    public int Preference => 0;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public bool IsAvailable(string? language) => true;

    public Task<SummaryDraft> SummarizeAsync(SummaryInput input, ResourceBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sentences = SplitSentences(input.Segments);
        if (sentences.Count == 0)
        {
            return Task.FromResult(new SummaryDraft { Title = input.Title, Engine = "Kakitome extractive summary v1 (local)" });
        }

        var termFrequency = sentences.SelectMany(s => Terms(s.Text)).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var decisions = sentences.Where(s => DecisionCue().IsMatch(s.Text)).ToList();
        var actions = sentences.Where(s => ActionCue().IsMatch(s.Text) && !decisions.Contains(s)).ToList();
        var questions = sentences.Where(s => QuestionCue().IsMatch(s.Text)).ToList();

        var duration = input.Segments.Count == 0 ? 0 : input.Segments[^1].EndSeconds;
        var keyCount = Math.Clamp((int)(duration / 120) + 3, 3, 8);
        var keyPoints = sentences
            .Where(s => s.Text.Length is >= 12 and <= 160 && !questions.Contains(s))
            .Select((s, i) => (Sentence: s, Score: Score(s, termFrequency, i, sentences.Count)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Sentence)
            .Aggregate(new List<Sentence>(), (chosen, s) =>
            {
                if (chosen.Count < keyCount && !chosen.Any(c => Similar(c.Text, s.Text)))
                {
                    chosen.Add(s);
                }

                return chosen;
            })
            .OrderBy(s => s.Start)
            .ToList();

        var topics = termFrequency
            .Where(kv => kv.Value >= 2 && !StopTerms.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value * Math.Min(kv.Key.Length, 6))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(kv => kv.Key)
            .ToList();

        var overview = string.Concat(keyPoints.Take(2).Select(s => s.Text));
        return Task.FromResult(new SummaryDraft
        {
            Title = input.Title,
            Overview = overview.Length == 0 ? null : overview,
            KeyPoints = keyPoints.Select(Item).ToList(),
            Decisions = decisions.Take(10).Select(Item).ToList(),
            ActionItems = actions.Take(10).Select(a => new DraftAction(a.Text, a.Start, [a.SegmentId], Owner(a.Text), Due(a.Text))).ToList(),
            Questions = questions.Take(10).Select(Item).ToList(),
            Topics = topics,
            Engine = "Kakitome extractive summary v1 (local)",
        });
    }

    private static DraftItem Item(Sentence s) => new(s.Text, s.Start, [s.SegmentId]);

    private static double Score(Sentence s, Dictionary<string, int> tf, int index, int count)
    {
        var terms = Terms(s.Text).ToList();
        if (terms.Count == 0)
        {
            return 0;
        }

        var salience = terms.Where(t => !StopTerms.Contains(t)).Sum(t => Math.Log(1 + tf[t])) / Math.Sqrt(terms.Count);
        var position = index < count * 0.1 ? 1.2 : 1.0; // openings often state the subject
        var cue = SummaryCue().IsMatch(s.Text) ? 1.3 : 1.0;
        return salience * position * cue;
    }

    private static bool Similar(string a, string b)
    {
        var ta = Terms(a).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tb = Terms(b).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return false;
        }

        return (double)ta.Intersect(tb).Count() / Math.Min(ta.Count, tb.Count) > 0.7;
    }

    /// <summary>Content terms: runs of kanji/katakana (2–10 chars) and Latin words of 4+ letters.</summary>
    private static IEnumerable<string> Terms(string text) => TermPattern().Matches(text).Select(m => m.Value);

    private static string? Owner(string text)
    {
        var m = OwnerPattern().Match(text);
        return m.Success ? m.Groups["o"].Value : null;
    }

    private static string? Due(string text)
    {
        var m = DuePattern().Match(text);
        return m.Success ? m.Value : null;
    }

    private static List<Sentence> SplitSentences(IReadOnlyList<SummarySourceSegment> segments)
    {
        var result = new List<Sentence>();
        foreach (var segment in segments)
        {
            foreach (Match m in SentencePattern().Matches(segment.Text))
            {
                var text = m.Value.Trim();
                if (text.Length >= 4)
                {
                    result.Add(new Sentence(text, segment.StartSeconds, segment.Id));
                }
            }
        }

        return result;
    }

    private sealed record Sentence(string Text, double Start, string SegmentId);

    [GeneratedRegex(@"[^。！？!?\.]+[。！？!?\.]?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}ー]{2,10}|[A-Za-z][A-Za-z'\-]{3,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TermPattern();

    [GeneratedRegex(@"決定し|決まり|決めました|ことにします|ことにしました|方針で進め|合意|承認|we decided|we agreed|decision", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DecisionCue();

    [GeneratedRegex(@"してください|までに|担当|対応します|やっておいて|宿題|解いておいて|action item|to-?do|please|will .{1,40} by", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ActionCue();

    [GeneratedRegex(@"(?:ですか|ますか|でしょうか|のか|か)[？?。]?$|[？?]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex QuestionCue();

    [GeneratedRegex(@"大事なのは|重要|ポイント|結論|まとめ|つまり|要するに|in summary|the key|important", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SummaryCue();

    [GeneratedRegex(@"担当は(?<o>[^、。\s]{1,10}?)(?:さん|様|氏|くん|君)?(?:で|に|が|。|、)|(?<o>[^、。\s]{1,10}?)(?:さん|様)が(?:担当|対応)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"(?:来週|今週|再来週)の?[月火水木金土日]曜日?|来週中|今週中|今月中|来月|明日|明後日|次回(?:まで|の会議まで)?|\d{1,2}月\d{1,2}日|by (?:monday|tuesday|wednesday|thursday|friday|next week|tomorrow)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DuePattern();
}
