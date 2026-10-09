using System.Text;
using System.Text.Json;

namespace Kakitome.Bench;

/// <summary>A suspected misrecognition: <see cref="Wrong"/> occurs in the transcript, <see cref="Right"/> is meant.</summary>
internal sealed record GlossaryFix(string Wrong, string Right, int Occurrences);

internal sealed record ChunkAdvice(string? Topic, IReadOnlyList<string> Terms, IReadOnlyList<(string Wrong, string Right)> Fixes);

internal sealed record GlossaryDraft(string? Topic, IReadOnlyList<string> Terms, IReadOnlyList<GlossaryFix> Fixes, string Engine)
{
    /// <summary>The draft in the plain-text glossary format (docs/glossary.md).</summary>
    public string ToGlossaryText(string header)
    {
        var builder = new StringBuilder();
        foreach (var line in header.Split('\n'))
        {
            builder.Append("# ").AppendLine(line.TrimEnd('\r'));
        }

        foreach (var term in Terms)
        {
            builder.AppendLine(term);
        }

        foreach (var fix in Fixes)
        {
            builder.Append(fix.Wrong).Append(" -> ").AppendLine(fix.Right);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Experiment (ADR-031, not adopted): a local LLM reads the transcript and proposes terms and "wrong → right" fixes.
/// <see cref="Combine"/> keeps a fix only when its wrong side occurs in the text and no other chunk proposed a different
/// right side, and a term only when it occurs in the text or is a kept fix's right side.
/// </summary>
internal static class GlossaryDraftFormat
{
    public const int MaxTerms = 60;
    public const int MaxFixes = 80;

    /// <summary>JSON shape enforced during decoding (llama.cpp GBNF).</summary>
    public const string Grammar = """
        root    ::= "{" ws "\"topic\":" ws string "," ws "\"terms\":" ws strings "," ws "\"fixes\":" ws fixes ws "}"
        strings ::= "[" ws ( string ( "," ws string ){0,39} )? ws "]"
        fixes   ::= "[" ws ( fix ( "," ws fix ){0,39} )? ws "]"
        fix     ::= "{" ws "\"wrong\":" ws string "," ws "\"right\":" ws string ws "}"
        string  ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" ["\\/bfnrt] ){0,60} "\""
        ws      ::= [ \t\n]{0,8}
        """;

    public const string SystemPrompt = """
        You proofread Japanese speech-recognition output. The recognizer often writes technical terms, names, case
        names and law names as other words with the same reading (homophones), e.g. 信教の自由 → 新居の自由,
        争議権 → 葬儀権, 内申書 → 内心書.
        Tasks:
        - topic: the subject of the recording in a few Japanese words.
        - terms: up to 40 important technical terms, proper nouns and names of this subject that the speaker uses,
          written correctly.
        - fixes: words in the text that are misrecognized. "wrong" must be copied exactly from the text (2-15
          characters). "right" is the term the speaker meant, with the same or almost the same reading. Only include a
          fix when the context makes it clear; never rewrite style, grammar or fillers.
        Answer with compact JSON only: {"topic":"...","terms":["..."],"fixes":[{"wrong":"...","right":"..."}]}
        """;

    public static string UserPrompt(string title, IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        builder.Append("タイトル: ").AppendLine(title).AppendLine("文字起こし:");
        foreach (var line in lines)
        {
            builder.AppendLine(line.Replace('\n', ' ').Trim());
        }

        return builder.ToString();
    }

    public static ChunkAdvice Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var topic = root.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
            var terms = root.TryGetProperty("terms", out var termArray) && termArray.ValueKind == JsonValueKind.Array
                ? termArray.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).ToList()
                : [];
            var fixes = new List<(string, string)>();
            if (root.TryGetProperty("fixes", out var fixArray) && fixArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var fix in fixArray.EnumerateArray())
                {
                    if (fix.ValueKind == JsonValueKind.Object
                        && fix.TryGetProperty("wrong", out var w) && w.ValueKind == JsonValueKind.String
                        && fix.TryGetProperty("right", out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        fixes.Add((w.GetString()!.Trim(), r.GetString()!.Trim()));
                    }
                }
            }

            return new ChunkAdvice(string.IsNullOrEmpty(topic) ? null : topic, terms, fixes);
        }
        catch (JsonException)
        {
            return new ChunkAdvice(null, [], []);
        }
    }

    public static GlossaryDraft Combine(IReadOnlyList<ChunkAdvice> chunks, string transcript, string engine)
    {
        var fixes = new List<GlossaryFix>();
        foreach (var group in chunks.SelectMany(c => c.Fixes)
                     .Where(f => f.Wrong.Length >= 2 && f.Right.Length >= 2 && f.Wrong != f.Right && transcript.Contains(f.Wrong, StringComparison.Ordinal))
                     .GroupBy(f => f.Wrong, StringComparer.Ordinal))
        {
            var rights = group.Select(f => f.Right).Distinct(StringComparer.Ordinal).ToList();
            if (rights.Count == 1)
            {
                fixes.Add(new GlossaryFix(group.Key, rights[0], CountOccurrences(transcript, group.Key)));
            }
        }

        fixes = [.. fixes.OrderByDescending(f => f.Occurrences).Take(MaxFixes)];
        var targets = fixes.Select(f => f.Right).ToHashSet(StringComparer.Ordinal);
        var terms = chunks.SelectMany(c => c.Terms.Distinct(StringComparer.Ordinal))
            .Where(t => t.Length >= 2 && (targets.Contains(t) || transcript.Contains(t, StringComparison.Ordinal)))
            .GroupBy(t => t, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(MaxTerms)
            .ToList();
        var topic = chunks.Select(c => c.Topic).OfType<string>().GroupBy(t => t, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return new GlossaryDraft(topic, terms, fixes, engine);
    }

    public static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
