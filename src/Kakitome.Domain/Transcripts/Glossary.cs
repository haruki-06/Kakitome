using System.Text;
using System.Text.RegularExpressions;

namespace Kakitome.Domain.Transcripts;

/// <summary>A user correction: text the recognizer tends to produce → the intended term.</summary>
public sealed record GlossaryReplacement(string From, string To);

/// <summary>A line of a glossary file that was ignored, with the reason (shown to the user after an import).</summary>
public sealed record GlossaryIssue(int Line, string Text, string Reason);

/// <summary>
/// A user-provided glossary (ADR-030): domain terms that are given to the recognizer as hints, and corrections that
/// are applied to the recognized text. Plain UTF-8 text, one entry per line, so that it can be written by hand or by
/// any AI assistant:
/// <code>
/// # comment
/// 堀木訴訟
/// 新居の自由 -> 信教の自由
/// </code>
/// A line with <c>-&gt;</c> (also <c>→</c> or <c>=&gt;</c>) is a correction; its right side is also a term.
/// </summary>
public sealed class Glossary
{
    public const int MaxTermLength = 40;
    public const int MaxEntries = 2000;

    private static readonly string[] Arrows = ["->", "=>", "→"];

    private Regex? _matcher;
    private Dictionary<string, string>? _lookup;

    private Glossary(IReadOnlyList<string> terms, IReadOnlyList<GlossaryReplacement> replacements, IReadOnlyList<GlossaryIssue> issues)
    {
        Terms = terms;
        Replacements = replacements;
        Issues = issues;
    }

    public static Glossary Empty { get; } = new([], [], []);

    /// <summary>Hint terms in file order (correction targets included), without duplicates.</summary>
    public IReadOnlyList<string> Terms { get; }

    public IReadOnlyList<GlossaryReplacement> Replacements { get; }

    public IReadOnlyList<GlossaryIssue> Issues { get; }

    public bool IsEmpty => Terms.Count == 0 && Replacements.Count == 0;

    public static Glossary Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var terms = new List<string>();
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        var replacements = new List<GlossaryReplacement>();
        var seenFrom = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<GlossaryIssue>();
        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = Clean(rawLine);
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (terms.Count + replacements.Count >= MaxEntries)
            {
                issues.Add(new GlossaryIssue(lineNo, line, "tooMany"));
                break;
            }

            var arrow = Arrows.Select(a => (Arrow: a, Index: line.IndexOf(a, StringComparison.Ordinal))).FirstOrDefault(x => x.Index >= 0);
            if (arrow.Arrow is null)
            {
                if (line.Length > MaxTermLength)
                {
                    issues.Add(new GlossaryIssue(lineNo, line, "tooLong"));
                }
                else if (seenTerms.Add(line))
                {
                    terms.Add(line);
                }

                continue;
            }

            var from = Clean(line[..arrow.Index]);
            var to = Clean(line[(arrow.Index + arrow.Arrow.Length)..]);
            if (from.Length == 0 || to.Length == 0)
            {
                issues.Add(new GlossaryIssue(lineNo, line, "emptySide"));
            }
            else if (from.Length > MaxTermLength || to.Length > MaxTermLength)
            {
                issues.Add(new GlossaryIssue(lineNo, line, "tooLong"));
            }
            else if (from.Length < 2)
            {
                // One character would rewrite far too much of the text.
                issues.Add(new GlossaryIssue(lineNo, line, "tooShort"));
            }
            else if (string.Equals(from, to, StringComparison.Ordinal) || !seenFrom.Add(from))
            {
                issues.Add(new GlossaryIssue(lineNo, line, "duplicate"));
            }
            else
            {
                replacements.Add(new GlossaryReplacement(from, to));
                if (seenTerms.Add(to))
                {
                    terms.Add(to);
                }
            }
        }

        return new Glossary(terms, replacements, issues);
    }

    /// <summary>Combines glossaries; earlier ones win on conflicting corrections and come first in the hints.</summary>
    public static Glossary Merge(params Glossary[] glossaries)
    {
        ArgumentNullException.ThrowIfNull(glossaries);
        var parts = glossaries.Where(g => g is { IsEmpty: false }).ToList();
        if (parts.Count <= 1)
        {
            return parts.Count == 1 ? parts[0] : Empty;
        }

        var terms = parts.SelectMany(g => g.Terms).Distinct(StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var replacements = parts.SelectMany(g => g.Replacements).Where(r => seen.Add(r.From)).ToList();
        return new Glossary(terms, replacements, []);
    }

    /// <summary>
    /// Applies all corrections in one left-to-right pass (longest match first), so a replacement never feeds
    /// another. Words written in Latin letters only match as whole words (<c>sequel</c> does not touch <c>sequels</c>).
    /// </summary>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Replacements.Count == 0 || text.Length == 0)
        {
            return text;
        }

        if (_matcher is null)
        {
            _lookup = Replacements.ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal);
            var alternatives = Replacements
                .Select(r => r.From)
                .OrderByDescending(f => f.Length)
                .Select(f => IsLatinWord(f) ? $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(f)}(?![\p{{L}}\p{{N}}])" : Regex.Escape(f));
            _matcher = new Regex(string.Join('|', alternatives), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        return _matcher.Replace(text, m => _lookup!.TryGetValue(m.Value, out var to) ? to : m.Value);
    }

    /// <summary>Text for a new, empty glossary file: the format in comments.</summary>
    public static string Template(bool japanese) => japanese
        ? """
          # Kakitome 用語辞書（UTF-8 のテキスト）
          # 1 行に 1 つ書きます。# で始まる行はメモとして無視されます。
          #
          # 専門用語・人名・固有名詞（文字起こしのヒントになります）
          # 例: 罪刑法定主義
          #
          # よく間違える書き方の修正（「誤 -> 正」。文字起こしの後に置き換えます）
          # 例: 新居の自由 -> 信教の自由

          """
        : """
          # Kakitome glossary (UTF-8 text)
          # One entry per line. Lines starting with # are notes and are ignored.
          #
          # Terms, names and proper nouns (used as hints for transcription)
          # e.g. Kubernetes
          #
          # Corrections for frequent mistakes ("wrong -> right", applied after transcription)
          # e.g. cube cuddle -> kubectl

          """;

    private static bool IsLatinWord(string s) => s.All(c => c < 0x3000);

    private static string Clean(string s)
    {
        var trimmed = s.Trim().Trim('﻿').Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        // Normalize full-width spaces and collapse runs of whitespace, so "a  b" and "a　b" are the same entry.
        var sb = new StringBuilder(trimmed.Length);
        var space = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
