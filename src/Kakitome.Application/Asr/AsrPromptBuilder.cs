using System.Text;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Asr;

/// <summary>
/// Builds the context prompt for each ASR chunk (ADR-030): glossary terms as a short punctuated list, followed by the
/// end of the text recognized so far. Whisper only reads about 224 prompt tokens, so both parts are budgeted. When the
/// glossary is larger than the budget, terms are chosen by the current topic: first terms recognized in the last few
/// minutes, then terms that share distinctive character pairs with the recent text (a lecture that reaches
/// 「教科書検定」 brings 「家永教科書訴訟」 forward), and the rest take turns, so every term is offered over a long
/// recording.
/// </summary>
public sealed class AsrPromptBuilder
{
    /// <summary>Chunks (~25 s each) for which a recognized term counts as the current topic.</summary>
    private const int RecentChunks = 8;

    /// <summary>
    /// A neutral punctuated opening for Japanese when there is nothing else to say. Whisper copies the style of its
    /// prompt; without one, long Japanese speech often comes out with no 「。」「、」 at all (ADR-030 benchmark).
    /// </summary>
    internal const string JapaneseStyleSeed = "はい。では、始めます。";

    private readonly Glossary _glossary;
    private readonly int _hintBudget;
    private readonly int _contextBudget;
    private readonly string _separator;
    private readonly string _terminator;
    private readonly bool _japanese;
    private readonly Dictionary<string, int> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _bigramWeight = new(StringComparer.Ordinal);
    private int _chunk;
    private int _cursor;

    public AsrPromptBuilder(Glossary? glossary, string? language)
    {
        _glossary = glossary ?? Glossary.Empty;
        var cjk = language is null || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        // Japanese is roughly one token per character, English roughly four characters per token.
        _hintBudget = cjk ? 110 : 400;
        _contextBudget = cjk ? 90 : 300;
        _separator = cjk ? "、" : ", ";
        _terminator = cjk ? "。" : ".";
        _japanese = language is null || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

        // Pairs shared by many terms (「事件」「訴訟」) say little about the topic: weight each by 1 / (terms containing it).
        foreach (var bigram in _glossary.Terms.SelectMany(t => Bigrams(t).Distinct()))
        {
            _bigramWeight[bigram] = _bigramWeight.GetValueOrDefault(bigram) + 1;
        }

        foreach (var key in _bigramWeight.Keys.ToList())
        {
            _bigramWeight[key] = 1 / _bigramWeight[key];
        }
    }

    /// <summary>Prompt for the next chunk; null when there is nothing to say.</summary>
    public string? Next(string? previousText)
    {
        var sb = new StringBuilder();
        var hints = SelectTerms(previousText);
        if (hints.Count > 0)
        {
            sb.Append(string.Join(_separator, hints)).Append(_terminator);
        }

        if (!string.IsNullOrWhiteSpace(previousText))
        {
            var context = previousText.Trim();
            sb.Append(context.Length > _contextBudget ? context[^_contextBudget..] : context);
        }

        if (sb.Length == 0)
        {
            return _japanese ? JapaneseStyleSeed : null;
        }

        return sb.ToString();
    }

    /// <summary>Records which terms the last chunk contained (after corrections) and moves to the next chunk.</summary>
    public void Observe(string recognizedText)
    {
        ArgumentNullException.ThrowIfNull(recognizedText);
        foreach (var term in _glossary.Terms)
        {
            if (recognizedText.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                _lastSeen[term] = _chunk;
            }
        }

        _chunk++;
    }

    private List<string> SelectTerms(string? recentText)
    {
        var terms = _glossary.Terms;
        var selected = new List<string>();
        if (terms.Count == 0)
        {
            return selected;
        }

        var used = 0;
        bool TryAdd(string term)
        {
            var cost = term.Length + _separator.Length;
            if (used + cost > _hintBudget)
            {
                return false;
            }

            selected.Add(term);
            used += cost;
            return true;
        }

        foreach (var term in _lastSeen.Where(kv => _chunk - kv.Value <= RecentChunks).OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
        {
            TryAdd(term);
        }

        if (!string.IsNullOrEmpty(recentText) && used < _hintBudget)
        {
            var context = Bigrams(recentText).ToHashSet(StringComparer.Ordinal);
            var related = terms
                .Where(t => !selected.Contains(t, StringComparer.Ordinal))
                .Select(t => (Term: t, Score: Bigrams(t).Distinct().Where(context.Contains).Sum(b => _bigramWeight.GetValueOrDefault(b))))
                .Where(x => x.Score >= 0.5)
                .OrderByDescending(x => x.Score);
            foreach (var (term, _) in related)
            {
                // Topic terms may use at most two thirds of the budget; the rest keeps rotating.
                if (used + term.Length + _separator.Length > _hintBudget * 2 / 3)
                {
                    break;
                }

                TryAdd(term);
            }
        }

        var start = _cursor % terms.Count;
        var advanced = 0;
        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[(start + i) % terms.Count];
            if (selected.Contains(term, StringComparer.Ordinal))
            {
                continue;
            }

            if (!TryAdd(term))
            {
                break;
            }

            advanced = i + 1;
        }

        // A glossary that fits the budget is offered whole every time; a larger one rotates.
        _cursor = start + advanced;
        return selected;
    }

    /// <summary>Adjacent character pairs of letters/digits (lower-cased), e.g. 「教科書」 → 教科, 科書.</summary>
    private static IEnumerable<string> Bigrams(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]) && char.IsLetterOrDigit(text[i + 1]))
            {
                yield return string.Concat(char.ToLowerInvariant(text[i]), char.ToLowerInvariant(text[i + 1]));
            }
        }
    }
}
