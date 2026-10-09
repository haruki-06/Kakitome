using System.Text;
using Kakitome.Domain.Library;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Domain.Rendering;

/// <summary>
/// Renders <c>transcript.md</c>: readable paragraphs, speaker labels when known, and plain <c>[HH:MM:SS]</c>
/// timestamps. <c>transcript.txt</c> is the timestamp-free reading version (<see cref="ReadableText"/>).
/// </summary>
public static class TranscriptRenderer
{
    /// <summary>A paragraph is closed when it would exceed this span.</summary>
    public const double MaxParagraphSeconds = 60;

    /// <summary>A silence longer than this starts a new paragraph.</summary>
    public const double ParagraphGapSeconds = 4;

    public static string ToMarkdown(RecordingMetadata metadata, TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(transcript);

        var labels = ArtifactLabels.For(transcript.Language ?? metadata.Language);
        var sb = new StringBuilder();
        sb.Append("# ").Append(SingleLine(metadata.Title)).Append('\n').Append('\n');
        AppendHeaderFacts(sb, metadata, transcript, labels, markdown: true);

        foreach (var p in BuildParagraphs(transcript))
        {
            sb.Append(TimeFormat.Stamp(p.Start)).Append(' ');
            if (p.Speaker is not null)
            {
                sb.Append("**").Append(SpeakerLabel(transcript, p.Speaker, labels)).Append(":** ");
            }

            sb.Append(p.Text).Append('\n').Append('\n');
        }

        return TrimTrailingBlankLines(sb);
    }

    /// <summary>Reading text without timestamps (<see cref="ReadableText"/>).</summary>
    public static string ToPlainText(RecordingMetadata metadata, TranscriptDocument transcript) => ReadableText.Transcript(metadata, transcript);

    internal static IReadOnlyList<Paragraph> BuildParagraphs(TranscriptDocument transcript)
    {
        var separator = TimeFormat.UsesSpaces(transcript.Language) ? " " : string.Empty;
        var segments = transcript.Segments
            .Select(s => (Segment: s, Text: SingleLine(s.Text)))
            .Where(s => s.Text.Length > 0);
        return GroupParagraphs(segments, s => s.Segment.StartSeconds, s => s.Segment.EndSeconds, s => s.Segment.Speaker)
            .Select(group => new Paragraph(group[0].Segment.StartSeconds, group[0].Segment.Speaker,
                new StringBuilder(string.Join(separator, group.Select(s => s.Text)))))
            .ToList();
    }

    /// <summary>
    /// Groups consecutive segments into reading paragraphs: a new paragraph starts when the speaker changes, after a pause
    /// longer than <see cref="ParagraphGapSeconds"/>, or when the paragraph would exceed <see cref="MaxParagraphSeconds"/>.
    /// Shared by the Markdown/text renderers and the in-app reader so they break paragraphs identically.
    /// </summary>
    public static List<List<T>> GroupParagraphs<T>(
        IEnumerable<T> segments, Func<T, double> start, Func<T, double> end, Func<T, string?> speaker)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(speaker);
        var result = new List<List<T>>();
        List<T>? current = null;
        double currentStart = 0;
        var previousEnd = double.NegativeInfinity;
        foreach (var segment in segments.OrderBy(start))
        {
            var startsNew = current is null
                || !string.Equals(speaker(current[0]), speaker(segment), StringComparison.Ordinal)
                || start(segment) - previousEnd > ParagraphGapSeconds
                || end(segment) - currentStart > MaxParagraphSeconds;
            if (startsNew)
            {
                current = [];
                currentStart = start(segment);
                result.Add(current);
            }

            current!.Add(segment);
            previousEnd = end(segment);
        }

        return result;
    }

    private static void AppendHeaderFacts(
        StringBuilder sb, RecordingMetadata metadata, TranscriptDocument transcript, ArtifactLabels labels, bool markdown)
    {
        var facts = new List<string> { $"{labels.Project}: {SingleLine(metadata.Project)}" };
        facts.Add($"{labels.Recorded}: {TimeFormat.DateTime(metadata.RecordedAt ?? metadata.CreatedAt)}");
        if (metadata.DurationSeconds is { } d)
        {
            facts.Add($"{labels.Duration}: {TimeFormat.Clock(d)}");
        }

        if ((transcript.Language ?? metadata.Language) is { Length: > 0 } lang)
        {
            facts.Add($"{labels.Language}: {lang}");
        }

        foreach (var fact in facts)
        {
            sb.Append(markdown ? "- " : string.Empty).Append(fact).Append('\n');
        }

        sb.Append('\n');
    }

    private static string SpeakerLabel(TranscriptDocument transcript, string speakerId, ArtifactLabels labels)
    {
        var known = transcript.Speakers.FirstOrDefault(s => s.Id == speakerId)?.Label;
        if (!string.IsNullOrWhiteSpace(known))
        {
            return SingleLine(known);
        }

        var index = transcript.Speakers.FindIndex(s => s.Id == speakerId);
        return index >= 0 ? $"{labels.Speaker} {index + 1}" : SingleLine(speakerId);
    }

    internal static string SingleLine(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal static string TrimTrailingBlankLines(StringBuilder sb)
    {
        var text = sb.ToString().TrimEnd('\n');
        return text + "\n";
    }

    internal sealed class Paragraph(double start, string? speaker, StringBuilder builder)
    {
        public double Start { get; } = start;

        public string? Speaker { get; } = speaker;

        public StringBuilder Builder { get; } = builder;

        public string Text => Builder.ToString();
    }
}
