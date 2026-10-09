using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kakitome.Domain.Transcripts;
using Kakitome.Storage.Models;

namespace Kakitome.Bench;

/// <summary>
/// Glossary drafts from a transcript (ADR-031). The recording, its transcript and the reference list stay on the PC;
/// only aggregate numbers are reported.
///   glossary-draft --transcript transcript.json --out draft.txt [--model id] [--cpu]
///   glossary-score --text file.txt --ref ref.txt [--glossary draft.txt]
/// The reference file has one line per known misrecognition: <c>wrong1|wrong2 -> right</c>.
/// </summary>
internal static class GlossaryBenchmark
{
    public static async Task<int> DraftAsync(ModelStore store, Dictionary<string, string> options)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(options["transcript"]));
        var lines = doc.RootElement.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("text").GetString() ?? "").ToList();
        var gpu = !options.ContainsKey("cpu");
        var raw = options["out"] + ".raw.txt";
        File.Delete(raw);
        var drafter = new LlamaGlossaryDrafter(store, options.GetValueOrDefault("model"), gpu, json => File.AppendAllText(raw, json + Environment.NewLine));
        var clock = Stopwatch.StartNew();
        var draft = await drafter.DraftAsync(options.GetValueOrDefault("title", "lecture"), lines, Math.Clamp(Environment.ProcessorCount - 2, 1, 8));
        await File.WriteAllTextAsync(options["out"], draft.ToGlossaryText($"draft: {draft.Engine}{Environment.NewLine}topic: {draft.Topic}"), Encoding.UTF8);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{draft.Engine}: {draft.Terms.Count} terms, {draft.Fixes.Count} fixes, {clock.Elapsed.TotalSeconds:F1} s"));
        return 0;
    }

    public static async Task<int> ScoreAsync(Dictionary<string, string> options)
    {
        var text = await File.ReadAllTextAsync(options["text"]);
        var glossary = options.TryGetValue("glossary", out var g) ? Glossary.Parse(await File.ReadAllTextAsync(g)) : Glossary.Empty;
        var corrected = glossary.Apply(text);
        var reference = (await File.ReadAllLinesAsync(options["ref"]))
            .Where(x => x.Contains("->", StringComparison.Ordinal) && !x.StartsWith('#'))
            .Select(x => x.Split("->", 2, StringSplitOptions.TrimEntries))
            .Select(p => (Wrongs: p[0].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), Right: p[1]))
            .ToList();

        int right = 0, wrong = 0;
        foreach (var (wrongs, target) in reference)
        {
            right += GlossaryDraftFormat.CountOccurrences(corrected, target);
            wrong += wrongs.Sum(w => GlossaryDraftFormat.CountOccurrences(corrected, w));
        }

        var known = reference.SelectMany(r => r.Wrongs.Select(w => (w, r.Right))).ToList();
        var unknownFixes = glossary.Replacements.Where(r => GlossaryDraftFormat.CountOccurrences(text, r.From) > 0
            && !known.Any(k => k.w.Contains(r.From, StringComparison.Ordinal) || r.From.Contains(k.w, StringComparison.Ordinal))).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"pairs {reference.Count}: right {right}, wrong {wrong}, accuracy {(right + wrong == 0 ? 0 : 100.0 * right / (right + wrong)):F0} %; fixes applied outside the reference: {unknownFixes.Count}"));
        foreach (var fix in unknownFixes)
        {
            Console.WriteLine($"  ? {fix.From} -> {fix.To} ({GlossaryDraftFormat.CountOccurrences(text, fix.From)})");
        }

        return 0;
    }
}
