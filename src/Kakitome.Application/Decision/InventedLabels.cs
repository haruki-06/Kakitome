using System.Text.RegularExpressions;

namespace Kakitome.Application.Decision;

/// <summary>
/// Whisper sometimes writes video or chat audio like a script, with 「名前:」 in front of lines — names it guessed or
/// words it heard once (「黒玉:」 300 times in a 57-minute video, 「松田:」 in a variety show). Nobody speaks a colon,
/// so in a Japanese transcript a short word followed by 「:」/「：」 at the start of a line or sentence is not speech.
/// A label is removed when it appears at least <see cref="MinOccurrences"/> times in the transcript (ADR-043); single
/// ones are left alone. Cleanup keeps the original in rawText.
/// </summary>
public static partial class InventedLabels
{
    public const int MinOccurrences = 3;

    /// <summary>Labels that occur often enough across the transcript to be removed (Japanese only).</summary>
    public static IReadOnlySet<string> Find(IEnumerable<string> segmentTexts, string? language)
    {
        ArgumentNullException.ThrowIfNull(segmentTexts);
        if (language is not null && !language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>();
        }

        return segmentTexts
            .SelectMany(t => Label().Matches(t).Select(m => m.Groups["l"].Value))
            .GroupBy(l => l, StringComparer.Ordinal)
            .Where(g => g.Count() >= MinOccurrences)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Low-risk edits removing the given labels (with the colon and a following space) wherever they appear.</summary>
    public static IEnumerable<EditCandidate> Candidates(string text, IReadOnlySet<string> labels)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(labels);
        foreach (var label in labels)
        {
            // Once a word is known to be an invented label, every "word:" is one — Whisper also glues it to the
            // previous sentence (「278円黒玉:いい時代…」).
            foreach (Match m in Regex.Matches(text, Regex.Escape(label) + @"[:：](?!//)[ \u3000]?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                yield return new EditCandidate(EditKind.Formatting, EditRisk.Low, m.Index, m.Length, string.Empty, $"invented label \"{label}:\"");
            }
        }
    }

    // At the start or after a space / sentence end, 1–10 letters (kana, kanji, Latin…) then 「:」 or 「：」 — not a time
    // (12:30) or a URL (https://).
    [GeneratedRegex(@"(?<=^|[\s。、．，！？!?」』）)\]])(?<l>[\p{L}\p{Mn}ー・々]{1,10})[:：](?!//)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Label();
}
