using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kakitome.Domain.Library;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Domain.Rendering;

/// <summary>
/// <c>transcript.txt</c> / <c>summary.txt</c>: text for reading, not for navigating — no timestamps, short paragraphs
/// broken at pauses, speaker changes and every few sentences, the speaker named only when it changes. The JSON stays
/// the structured source and the Markdown keeps the timestamps.
/// </summary>
public static partial class ReadableText
{
    /// <summary>A paragraph holds at most this many sentences …</summary>
    internal const int MaxSentences = 3;

    /// <summary>… or about this many characters (Japanese has no spaces, so fewer characters read as much).</summary>
    internal const int MaxCharsJapanese = 120;

    internal const int MaxCharsSpaced = 320;

    public static string Transcript(RecordingMetadata metadata, TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(transcript);
        var language = transcript.Language ?? metadata.Language;
        var japanese = !TimeFormat.UsesSpaces(language);
        var labels = ArtifactLabels.For(language);

        var sb = new StringBuilder();
        sb.Append(TranscriptRenderer.SingleLine(metadata.Title)).Append('\n');
        sb.Append(MetaLine(metadata, japanese, includeDuration: true)).Append("\n\n");

        var blocks = ReadingBlocks(TranscriptRenderer.BuildParagraphs(transcript), japanese);
        var named = blocks.Select(b => b.Speaker).Where(s => s is not null).Distinct(StringComparer.Ordinal).Count() > 1;
        string? lastSpeaker = null;
        foreach (var (speaker, text) in blocks)
        {
            if (named && speaker is not null && !string.Equals(speaker, lastSpeaker, StringComparison.Ordinal))
            {
                sb.Append(SpeakerName(transcript, speaker, labels)).Append(japanese ? "：" : ": ");
            }

            sb.Append(text).Append("\n\n");
            lastSpeaker = speaker ?? lastSpeaker;
        }

        return TranscriptRenderer.TrimTrailingBlankLines(sb);
    }

    /// <summary>
    /// Reading blocks of every paragraph. A short block that ends a paragraph before a pause ("最後に、来月のリリースに
    /// ついてです。") is a lead-in to what the same speaker says next, so it is joined to that instead of standing alone.
    /// </summary>
    internal static List<(string? Speaker, string Text)> ReadingBlocks(IEnumerable<TranscriptRenderer.Paragraph> paragraphs, bool japanese)
    {
        var limit = japanese ? 40 : 100;
        var separator = japanese ? string.Empty : " ";
        var result = new List<(string? Speaker, string Text)>();
        string? carry = null;
        string? carrySpeaker = null;
        foreach (var paragraph in paragraphs)
        {
            var text = paragraph.Text;
            if (carry is not null)
            {
                if (string.Equals(carrySpeaker, paragraph.Speaker, StringComparison.Ordinal))
                {
                    text = carry + separator + text;
                }
                else
                {
                    result.Add((carrySpeaker, carry));
                }

                carry = null;
            }

            var blocks = Blocks(text, japanese);
            if (blocks.Count == 0)
            {
                continue;
            }

            var last = blocks[^1];
            var keep = last.Length <= limit ? blocks.Count - 1 : blocks.Count;
            result.AddRange(blocks.Take(keep).Select(b => (paragraph.Speaker, b)));
            if (keep < blocks.Count)
            {
                carry = last;
                carrySpeaker = paragraph.Speaker;
            }
        }

        if (carry is not null)
        {
            result.Add((carrySpeaker, carry));
        }

        return result;
    }

    public static string Summary(RecordingMetadata metadata, SummaryDocument summary)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(summary);
        var language = summary.Language ?? metadata.Language;
        var japanese = !TimeFormat.UsesSpaces(language);
        var labels = ArtifactLabels.For(language);

        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(summary.Title) ? metadata.Title : summary.Title;
        sb.Append(TranscriptRenderer.SingleLine(title)).Append('\n');
        sb.Append(MetaLine(metadata, japanese, includeDuration: false)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(summary.Overview))
        {
            Heading(sb, labels, labels.Overview);
            foreach (var block in Blocks(TranscriptRenderer.SingleLine(summary.Overview), japanese))
            {
                sb.Append(block).Append('\n');
            }

            sb.Append('\n');
        }

        Section(sb, labels, labels.KeyPoints, summary.KeyPoints.Select(i => Clean(i.Text)));
        Section(sb, labels, labels.Decisions, summary.Decisions.Select(i => Clean(i.Text)));
        Section(sb, labels, labels.ActionItems, summary.ActionItems.Select(a => Clean(a.Text) + ActionDetails(a, labels, japanese)));
        Section(sb, labels, labels.Questions, summary.Questions.Select(i => Clean(i.Text)));
        if (summary.Topics.Count > 0)
        {
            Heading(sb, labels, labels.Topics);
            sb.Append(string.Join(labels.ListSeparator, summary.Topics.Select(TranscriptRenderer.SingleLine))).Append("\n\n");
        }

        return TranscriptRenderer.TrimTrailingBlankLines(sb);
    }

    /// <summary>Owner/due values a model may write instead of leaving them empty.</summary>
    public static bool IsMissing(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().ToLowerInvariant() is "null" or "none" or "n/a" or "na" or "-" or "—" or "なし" or "不明" or "該当なし"
            or "未定" or "未指定" or "特になし" or "not specified" or "unspecified" or "unknown"
        || value.Contains("指定なし", StringComparison.Ordinal) || value.Contains("指定されていない", StringComparison.Ordinal);

    /// <summary>Splits text into readable blocks of up to <see cref="MaxSentences"/> sentences / a character budget.</summary>
    internal static List<string> Blocks(string text, bool japanese)
    {
        var limit = japanese ? MaxCharsJapanese : MaxCharsSpaced;
        var sentences = (japanese ? JapaneseSentenceEnd() : SpacedSentenceEnd()).Split(text.Trim())
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .SelectMany(s => SplitLong(s, limit, japanese))
            .ToList();
        var separator = japanese ? string.Empty : " ";
        var blocks = new List<string>();
        var current = new StringBuilder();
        var count = 0;
        foreach (var sentence in sentences)
        {
            if (count > 0 && (count >= MaxSentences || current.Length + sentence.Length > limit || OpensNewTopic(sentence, japanese)))
            {
                blocks.Add(current.ToString());
                current.Clear();
                count = 0;
            }

            if (count > 0)
            {
                current.Append(separator);
            }

            current.Append(sentence);
            count++;
        }

        if (current.Length > 0)
        {
            blocks.Add(current.ToString());
        }

        return blocks;
    }

    /// <summary>
    /// Speech recognizers often leave long stretches without sentence-ending punctuation (Whisper on lectures). Such a
    /// run is cut near the length limit at the pauses the recognizer marked with spaces, else at 「、」/commas, so it
    /// still reads as paragraphs.
    /// </summary>
    internal static IEnumerable<string> SplitLong(string sentence, int limit, bool japanese)
    {
        var rest = sentence;
        while (rest.Length > limit)
        {
            var window = rest[..limit];
            var cut = window.LastIndexOf(' ');
            if (cut < limit / 3)
            {
                cut = Math.Max(window.LastIndexOf('、'), window.LastIndexOf(','));
                cut = cut >= limit / 3 ? cut + 1 : -1; // keep the comma with the first part
            }

            if (cut < limit / 3)
            {
                // No pause marker: look a little further for one rather than cutting a word.
                var ahead = rest.IndexOfAny(japanese ? [' ', '、'] : [' ', ','], limit);
                cut = ahead > 0 && ahead < limit * 2 ? ahead + (rest[ahead] == ' ' ? 0 : 1) : limit;
            }

            yield return rest[..cut].Trim();
            rest = rest[cut..].Trim();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    /// <summary>Words that usually start a new topic in speech ("次に、", "Finally, …") begin a new paragraph.</summary>
    private static readonly string[] JapaneseOpeners =
        ["では", "それでは", "まず", "次に", "続いて", "続きまして", "最後に", "結論として", "まとめると", "以上で", "一方", "ところで", "さて"];

    private static readonly string[] SpacedOpeners =
        ["Next,", "Finally,", "First,", "Firstly,", "Secondly,", "Now,", "So,", "In conclusion", "To sum up", "Moving on", "Meanwhile,", "By the way"];

    internal static bool OpensNewTopic(string sentence, bool japanese) =>
        japanese
            ? JapaneseOpeners.Any(o => sentence.StartsWith(o, StringComparison.Ordinal) && sentence.Length > o.Length && (sentence[o.Length] is '、' or '，' || o.Length >= 3))
            : SpacedOpeners.Any(o => sentence.StartsWith(o, StringComparison.OrdinalIgnoreCase));

    private static string MetaLine(RecordingMetadata metadata, bool japanese, bool includeDuration)
    {
        var when = (metadata.RecordedAt ?? metadata.CreatedAt).ToString(
            japanese ? "yyyy年M月d日 H:mm" : "MMMM d, yyyy H:mm", japanese ? CultureInfo.GetCultureInfo("ja-JP") : CultureInfo.GetCultureInfo("en-US"));
        var parts = new List<string> { when };
        if (includeDuration && metadata.DurationSeconds is { } seconds && seconds > 0)
        {
            parts.Add(Duration(seconds, japanese));
        }

        parts.Add(TranscriptRenderer.SingleLine(metadata.Project));
        return string.Join(japanese ? " ・ " : " · ", parts);
    }

    internal static string Duration(double seconds, bool japanese)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        if (japanese)
        {
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}時間{span.Minutes}分"
                : span.TotalMinutes >= 1 ? $"{span.Minutes}分{span.Seconds}秒"
                : $"{span.Seconds}秒";
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : span.TotalMinutes >= 1 ? $"{span.Minutes} min {span.Seconds} s"
            : $"{span.Seconds} s";
    }

    private static void Heading(StringBuilder sb, ArtifactLabels labels, string heading) =>
        sb.Append(labels.PlainHeadingOpen).Append(heading).Append(labels.PlainHeadingClose).Append('\n');

    private static void Section(StringBuilder sb, ArtifactLabels labels, string heading, IEnumerable<string> lines)
    {
        var list = lines.Where(l => l.Length > 0).ToList();
        if (list.Count == 0)
        {
            return;
        }

        Heading(sb, labels, heading);
        foreach (var line in list)
        {
            sb.Append(labels.PlainBullet).Append(line).Append('\n');
        }

        sb.Append('\n');
    }

    private static string Clean(string text) => TranscriptRenderer.SingleLine(text);

    private static string ActionDetails(ActionItem item, ArtifactLabels labels, bool japanese)
    {
        var parts = new List<string>();
        if (!IsMissing(item.Owner))
        {
            parts.Add(japanese ? $"{labels.Owner}：{Clean(item.Owner!)}" : $"{labels.Owner.ToLowerInvariant()}: {Clean(item.Owner!)}");
        }

        if (!IsMissing(item.Due))
        {
            parts.Add(japanese ? $"{labels.Due}：{Clean(item.Due!)}" : $"{labels.Due.ToLowerInvariant()}: {Clean(item.Due!)}");
        }

        return parts.Count == 0 ? string.Empty : japanese ? $"（{string.Join("／", parts)}）" : $" ({string.Join(", ", parts)})";
    }

    private static string SpeakerName(TranscriptDocument transcript, string speakerId, ArtifactLabels labels)
    {
        var known = transcript.Speakers.FirstOrDefault(s => s.Id == speakerId)?.Label;
        if (!string.IsNullOrWhiteSpace(known))
        {
            return TranscriptRenderer.SingleLine(known);
        }

        var index = transcript.Speakers.FindIndex(s => s.Id == speakerId);
        return index >= 0 ? $"{labels.Speaker}{(labels == ArtifactLabels.Japanese ? string.Empty : " ")}{index + 1}" : TranscriptRenderer.SingleLine(speakerId);
    }

    [GeneratedRegex("(?<=[。！？!?])")]
    private static partial Regex JapaneseSentenceEnd();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""'(])")]
    private static partial Regex SpacedSentenceEnd();
}
