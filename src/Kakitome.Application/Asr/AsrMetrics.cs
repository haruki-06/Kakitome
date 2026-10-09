using System.Globalization;
using System.Text;

namespace Kakitome.Application.Asr;

/// <summary>
/// Error-rate metrics for the ASR benchmark (docs/08). Japanese is scored by character error rate (CER) after
/// normalization; space-delimited languages by word error rate (WER).
/// </summary>
public static class AsrMetrics
{
    /// <summary>
    /// NFKC (full/half width unification), lower case, and removal of whitespace, punctuation and symbols. Kana/kanji
    /// spelling variants and numerals ("三" vs "3") still count as errors; they are reported per category instead.
    /// </summary>
    public static string NormalizeForCer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || IsPunctuationOrSymbol(category))
            {
                continue;
            }

            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> WordsForWer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            normalized.Append(IsPunctuationOrSymbol(Rune.GetUnicodeCategory(rune)) && rune.Value != '\'' ? " " : rune.ToString());
        }

        return normalized.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Character error rate (edits / reference length) on normalized text.</summary>
    public static ErrorRate Cer(string reference, string hypothesis)
    {
        var r = TextElements(NormalizeForCer(reference));
        var h = TextElements(NormalizeForCer(hypothesis));
        return new ErrorRate(EditDistance(r, h), r.Count);
    }

    public static ErrorRate Wer(string reference, string hypothesis) =>
        new(EditDistance(WordsForWer(reference), WordsForWer(hypothesis)), WordsForWer(reference).Count);

    public static int EditDistance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
        {
            previous[j] = j;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = comparer.Equals(a[i - 1], b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }

    private static List<string> TextElements(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            list.Add((string)e.Current);
        }

        return list;
    }

    private static bool IsPunctuationOrSymbol(UnicodeCategory c) => c is UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
        or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
}

/// <summary>Edits over reference units; aggregate by summing both before dividing.</summary>
public readonly record struct ErrorRate(int Edits, int ReferenceLength)
{
    public double Rate => ReferenceLength == 0 ? (Edits == 0 ? 0 : 1) : (double)Edits / ReferenceLength;

    public static ErrorRate operator +(ErrorRate a, ErrorRate b) => new(a.Edits + b.Edits, a.ReferenceLength + b.ReferenceLength);

    public static ErrorRate Add(ErrorRate a, ErrorRate b) => a + b;
}
