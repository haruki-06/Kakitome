namespace Kakitome.Domain.Rendering;

/// <summary>One segment's text as shown in a reading block (possibly with a sentence end added).</summary>
public sealed record ReadingPiece<T>(T Segment, string Text);

/// <summary>A short paragraph for reading; <see cref="Speaker"/> is the speaker of its segments.</summary>
public sealed record ReadingBlock<T>(string? Speaker, IReadOnlyList<ReadingPiece<T>> Pieces);

/// <summary>
/// The in-app reader's layout of a transcript, segment by segment (so the segment being heard can still be highlighted
/// and clicked): no time stamps, a sentence end added where the speaker paused without one, and short paragraphs —
/// a new one at speaker changes and long pauses (<see cref="TranscriptRenderer.GroupParagraphs"/>), after about
/// <see cref="ReadableText.MaxSentences"/> sentences or a character budget, and before words that open a new topic.
/// Same reading rules as <c>transcript.txt</c> (<see cref="ReadableText"/>), applied at segment boundaries.
/// </summary>
public static class ReadingLayout
{
    /// <summary>A pause this long after a segment that ends without punctuation is read as the end of a sentence.</summary>
    public const double SentencePauseSeconds = 0.8;

    public static List<ReadingBlock<T>> Blocks<T>(
        IReadOnlyList<T> segments,
        Func<T, double> start,
        Func<T, double> end,
        Func<T, string?> speaker,
        Func<T, string> text,
        bool japanese)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(text);
        var limit = japanese ? ReadableText.MaxCharsJapanese : ReadableText.MaxCharsSpaced;
        var blocks = new List<ReadingBlock<T>>();
        foreach (var paragraph in TranscriptRenderer.GroupParagraphs(segments, start, end, speaker))
        {
            var pieces = new List<ReadingPiece<T>>();
            var sentences = 0;
            var length = 0;
            for (var i = 0; i < paragraph.Count; i++)
            {
                var segment = paragraph[i];
                var value = TranscriptRenderer.SingleLine(text(segment));
                if (value.Length == 0)
                {
                    continue;
                }

                // Break before a topic opener or once the paragraph is long enough — only after a sentence end.
                if (pieces.Count > 0 && EndsSentence(pieces[^1].Text)
                    && (sentences >= ReadableText.MaxSentences || length + value.Length > limit || ReadableText.OpensNewTopic(value, japanese)))
                {
                    blocks.Add(new ReadingBlock<T>(speaker(paragraph[0]), pieces));
                    pieces = [];
                    sentences = 0;
                    length = 0;
                }

                var last = i == paragraph.Count - 1;
                var pausedAfter = last || start(paragraph[i + 1]) - end(segment) >= SentencePauseSeconds;
                if (pausedAfter && !EndsWithPunctuation(value))
                {
                    value += japanese ? "。" : ".";
                }

                sentences += CountSentenceEnds(value);
                length += value.Length;
                pieces.Add(new ReadingPiece<T>(segment, value));
            }

            if (pieces.Count > 0)
            {
                blocks.Add(new ReadingBlock<T>(speaker(paragraph[0]), pieces));
            }
        }

        return blocks;
    }

    private static readonly char[] SentenceEnds = ['。', '．', '！', '？', '!', '?', '.', '…'];

    private static readonly char[] Closers = ['」', '』', '）', ')', '"', '\'', '”', '’'];

    private static bool EndsSentence(string value)
    {
        var trimmed = value.TrimEnd(Closers);
        return trimmed.Length > 0 && SentenceEnds.Contains(trimmed[^1]);
    }

    /// <summary>Any punctuation at the end (a comma means the sentence goes on, so no 「。」 is added after it).</summary>
    private static bool EndsWithPunctuation(string value)
    {
        var trimmed = value.TrimEnd(Closers);
        return trimmed.Length > 0 && (SentenceEnds.Contains(trimmed[^1]) || trimmed[^1] is '、' or '，' or ',' or ';' or ':' or '：');
    }

    private static int CountSentenceEnds(string value) => Math.Max(1, value.Count(c => c is '。' or '！' or '？' or '!' or '?'));
}
