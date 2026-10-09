using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Models;
using Kakitome.Application.Summaries;
using Kakitome.Infrastructure.Summaries;
using Kakitome.Storage.Models;

namespace Kakitome.Bench;

/// <summary>
/// Summary benchmark (docs/08 "Local LLM"): runs the extractive provider and every installed local LLM over
/// benchmarks/summary-v1/cases.json and scores recall of reference decisions/actions/questions/facts, spurious items,
/// invented numbers, citations, language, latency and peak memory. Writes docs/benchmarks/summary-v1/README.md.
/// </summary>
internal static partial class SummaryBenchmark
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task RunAsync(string root, ModelStore store, bool includeCpu)
    {
        var casesFile = Path.Combine(root, "benchmarks", "summary-v1", "cases.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(casesFile));
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().Select(LoadCase).ToList();

        var runs = new List<(string Name, ISummaryProvider Provider)> { ("extractive-v1", new ExtractiveSummaryProvider()) };
        foreach (var model in ModelCatalog.SummaryModels.Where(m => store.GetState(m.Id) == ModelState.Installed))
        {
            runs.Add(($"{model.Id} (GPU)", Llama(store, model.Id, gpu: true)));
            if (includeCpu)
            {
                runs.Add(($"{model.Id} (CPU)", Llama(store, model.Id, gpu: false)));
            }
        }

        var outDir = Path.Combine(root, "benchmarks", "runs", "summary-v1");
        Directory.CreateDirectory(outDir);
        var rows = new List<Row>();
        foreach (var (name, provider) in runs)
        {
            foreach (var c in cases)
            {
                Console.WriteLine($"{name} / {c.Id} …");
                var budget = new ResourceBudget(Math.Clamp(Environment.ProcessorCount - 2, 1, 8), PreferEfficiency: false);
                using var sampler = new PeakMemory();
                var clock = Stopwatch.StartNew();
                SummaryDraft? draft = null;
                string? error = null;
                try
                {
                    draft = await provider.SummarizeAsync(c.Input, budget);
                }
#pragma warning disable CA1031 // Benchmark records failures instead of stopping.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    error = ex.Message;
                }

                clock.Stop();
                var row = draft is null
                    ? new Row(name, c.Id, Failed: true, Error: error)
                    : Score(name, c, draft) with { Seconds = clock.Elapsed.TotalSeconds, PeakMb = sampler.PeakMb };
                rows.Add(row);
                var file = Path.Combine(outDir, $"{Safe(name)}__{c.Id}.json");
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(draft, JsonOptions));
                Console.WriteLine($"  {row}");
            }
        }

        await WriteReportAsync(root, cases, runs.Select(r => r.Name).ToList(), rows, outDir);
    }

    /// <summary>Summarizes one Library transcript.json (speakers by name, as the app does) and prints the draft.</summary>
    public static async Task ProbeFileAsync(ModelStore store, string transcriptPath)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(transcriptPath));
        var root = doc.RootElement;
        var labels = root.TryGetProperty("speakers", out var sp)
            ? sp.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "")
            : [];
        var segments = root.GetProperty("segments").EnumerateArray()
            .Select(x => new SummarySourceSegment(
                x.GetProperty("id").GetString()!, x.GetProperty("startSeconds").GetDouble(), x.GetProperty("endSeconds").GetDouble(),
                x.GetProperty("text").GetString() ?? "",
                x.TryGetProperty("speaker", out var s) && s.GetString() is { } id ? labels.GetValueOrDefault(id, id) : null))
            .Where(x => x.Text.Length > 0).ToList();
        var language = root.TryGetProperty("language", out var lang) ? lang.GetString() : "ja";
        var model = ModelCatalog.SummaryModels.First(m => store.GetState(m.Id) == ModelState.Installed).Id;
        var clock = Stopwatch.StartNew();
        var draft = await Llama(store, model, gpu: true).SummarizeAsync(new SummaryInput(Path.GetFileName(Path.GetDirectoryName(transcriptPath)!), language, null, segments),
            new ResourceBudget(8, PreferEfficiency: false));
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"kind: {draft.Kind} ({clock.Elapsed.TotalSeconds:F0} s, {model})");
        Console.WriteLine($"title: {draft.Title}");
        Console.WriteLine($"overview: {draft.Overview}");
        void List(string name, IEnumerable<DraftItem> items)
        {
            foreach (var i in items)
            {
                Console.WriteLine($"{name}: {i.Text}{(i is DraftAction a ? $" (owner: {a.Owner}, due: {a.Due})" : string.Empty)}");
            }
        }

        List("key", draft.KeyPoints);
        List("decision", draft.Decisions);
        List("action", draft.ActionItems);
        List("question", draft.Questions);
        Console.WriteLine($"topics: {string.Join(", ", draft.Topics)}");
    }

    private static LlamaSummaryProvider Llama(ModelStore store, string model, bool gpu) =>
        new(store, new FixedAcceleration(gpu), new AcPower(), NullLogger<LlamaSummaryProvider>.Instance)
        {
            ModelOverride = model,
            UseGpuOverride = gpu,
            RawOutputObserver = raw => File.AppendAllText(Path.Combine(Path.GetTempPath(), $"kakitome-llm-raw-{model}.txt"), raw + Environment.NewLine + "---" + Environment.NewLine),
        };

    internal sealed record Case(string Id, string Language, SummaryInput Input, string SourceText, Expected Expected);

    internal sealed record Expected(
        List<List<string[]>> Decisions, List<ExpectedAction> Actions, List<List<string[]>> Questions, List<List<string[]>> Facts);

    internal sealed record ExpectedAction(List<string[]> Keywords, string[]? Owner, string[]? Due);

    internal sealed record Row(
        string Provider, string Case, bool Failed = false, string? Error = null,
        int DecisionHits = 0, int Decisions = 0, int ActionHits = 0, int Actions = 0, int OwnerHits = 0, int DueHits = 0, int OwnersExpected = 0, int DuesExpected = 0,
        int QuestionHits = 0, int Questions = 0, int FactHits = 0, int Facts = 0, int Spurious = 0, int InventedNumbers = 0,
        int Items = 0, int CitedItems = 0, double LanguageShare = 1, double Seconds = 0, double PeakMb = 0)
    {
        public override string ToString() => Failed
            ? $"FAILED: {Error}"
            : $"dec {DecisionHits}/{Decisions} act {ActionHits}/{Actions} owner {OwnerHits}/{OwnersExpected} due {DueHits}/{DuesExpected} q {QuestionHits}/{Questions} facts {FactHits}/{Facts} spurious {Spurious} invented# {InventedNumbers} cited {CitedItems}/{Items} lang {LanguageShare:P0} {Seconds:F1}s {PeakMb:F0}MB";
    }

    private static Case LoadCase(JsonElement e)
    {
        var language = e.GetProperty("language").GetString()!;
        var repeat = e.TryGetProperty("repeat", out var r) ? r.GetInt32() : 1;
        var raw = e.GetProperty("segments").EnumerateArray()
            .Select(s => (Speaker: s.GetProperty("speaker").ValueKind == JsonValueKind.String ? s.GetProperty("speaker").GetString() : null, Text: s.GetProperty("text").GetString()!))
            .ToList();
        var segments = new List<SummarySourceSegment>();
        for (var i = 0; i < repeat; i++)
        {
            foreach (var (speaker, text) in raw)
            {
                var n = segments.Count;
                segments.Add(new SummarySourceSegment($"s{n + 1}", n * 6.0, (n * 6.0) + 5.5, text, speaker));
            }
        }

        var x = e.GetProperty("expected");
        static List<List<string[]>> Groups(JsonElement parent, string name) => parent.TryGetProperty(name, out var arr)
            ? [.. arr.EnumerateArray().Select(item => item.EnumerateArray().Select(g => g.EnumerateArray().Select(k => k.GetString()!).ToArray()).ToList())]
            : [];
        static string[]? Words(JsonElement parent, string name) => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(k => k.GetString()!)]
            : null;
        var actions = x.GetProperty("actions").EnumerateArray().Select(a => new ExpectedAction(
            [.. a.GetProperty("keywords").EnumerateArray().Select(g => g.EnumerateArray().Select(k => k.GetString()!).ToArray())],
            Words(a, "owner"),
            Words(a, "due"))).ToList();

        var input = new SummaryInput(e.GetProperty("title").GetString()!, language, null, segments);
        return new Case(e.GetProperty("id").GetString()!, language, input, string.Join("\n", raw.Select(s => s.Text)),
            new Expected(Groups(x, "decisionItems"), actions, Groups(x, "questions"), Groups(x, "facts")));
    }

    internal static Row Score(string provider, Case c, SummaryDraft d)
    {
        bool Matches(string text, List<string[]> groups) => groups.All(g => g.Any(k => Normalize(text).Contains(Normalize(k), StringComparison.Ordinal)));

        var decisionTexts = d.Decisions.Select(i => i.Text).ToList();
        var questionTexts = d.Questions.Select(i => i.Text).ToList();
        var everything = string.Join("\n", new[] { d.Title ?? string.Empty, d.Overview ?? string.Empty }
            .Concat(d.KeyPoints.Select(i => i.Text)).Concat(decisionTexts).Concat(d.ActionItems.Select(a => a.Text)).Concat(questionTexts));

        var decisionHits = c.Expected.Decisions.Count(g => decisionTexts.Any(t => Matches(t, g)));
        var questionHits = c.Expected.Questions.Count(g => questionTexts.Any(t => Matches(t, g)));
        var factHits = c.Expected.Facts.Count(g => Matches(everything, g));
        int actionHits = 0, ownerHits = 0, dueHits = 0;
        foreach (var expected in c.Expected.Actions)
        {
            var match = d.ActionItems.FirstOrDefault(a => Matches(a.Text, expected.Keywords));
            if (match is null)
            {
                continue;
            }

            actionHits++;
            if (expected.Owner is { } owners && owners.Any(o => Normalize($"{match.Owner} {match.Text}").Contains(Normalize(o), StringComparison.Ordinal)))
            {
                ownerHits++;
            }

            if (expected.Due is { } dues && dues.Any(o => Normalize($"{match.Due} {match.Text}").Contains(Normalize(o), StringComparison.Ordinal)))
            {
                dueHits++;
            }
        }

        // Spurious: decisions or actions where the reference has none (interviews, lectures without decisions).
        var spurious = (c.Expected.Decisions.Count == 0 ? d.Decisions.Count : 0) + (c.Expected.Actions.Count == 0 ? d.ActionItems.Count : 0);

        var sourceNumbers = Numbers(c.SourceText);
        var invented = Numbers(everything).Count(n => !sourceNumbers.Contains(n));

        var items = d.KeyPoints.Concat(d.Decisions).Concat(d.ActionItems).Concat(d.Questions).ToList();
        var letters = everything.Where(char.IsLetter).ToList();
        var cjk = letters.Count(ch => ch is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿');
        var share = letters.Count == 0 ? 0 : c.Language == "ja" ? cjk / (double)letters.Count : (letters.Count - cjk) / (double)letters.Count;

        return new Row(provider, c.Id,
            DecisionHits: decisionHits, Decisions: c.Expected.Decisions.Count,
            ActionHits: actionHits, Actions: c.Expected.Actions.Count,
            OwnerHits: ownerHits, OwnersExpected: c.Expected.Actions.Count(a => a.Owner is not null),
            DueHits: dueHits, DuesExpected: c.Expected.Actions.Count(a => a.Due is not null),
            QuestionHits: questionHits, Questions: c.Expected.Questions.Count,
            FactHits: factHits, Facts: c.Expected.Facts.Count,
            Spurious: spurious, InventedNumbers: invented,
            Items: items.Count, CitedItems: items.Count(i => i.SegmentIds is { Count: > 0 }),
            LanguageShare: share);
    }

    internal static string Normalize(string text) => text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>Arabic numbers plus kanji numerals converted to digits (十一 → 11, 二千二十六 → 2026).</summary>
    internal static HashSet<long> Numbers(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var set = new HashSet<long>();
        foreach (Match m in DigitsRegex().Matches(normalized))
        {
            if (long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                set.Add(n);
            }
        }

        foreach (Match m in KanjiRegex().Matches(normalized))
        {
            if (KanjiNumber(m.Value) is { } n)
            {
                set.Add(n);
            }
        }

        return set;
    }

    private static long? KanjiNumber(string text)
    {
        const string digits = "〇一二三四五六七八九";
        long total = 0, section = 0, current = 0;
        var any = false;
        foreach (var ch in text)
        {
            var d = digits.IndexOf(ch, StringComparison.Ordinal);
            if (d >= 0)
            {
                current = (current * 10) + d;
                any = true;
                continue;
            }

            var unit = ch switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10_000, '億' => 100_000_000, _ => 0 };
            if (unit == 0)
            {
                return null;
            }

            any = true;
            if (unit >= 10_000)
            {
                total += (section + current) * unit;
                section = 0;
            }
            else
            {
                section += (current == 0 ? 1 : current) * unit;
            }

            current = 0;
        }

        return any ? total + section + current : null;
    }

    private static async Task WriteReportAsync(string root, List<Case> cases, List<string> providers, List<Row> rows, string runsDir)
    {
        static string Pct(int hit, int total) => total == 0 ? "–" : $"{100.0 * hit / total:F0} %";
        var sb = new StringBuilder();
        sb.AppendLine("# Summary benchmark v1");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} by `Kakitome.Bench summary` on {Hardware()}.");
        sb.AppendLine("Cases: `benchmarks/summary-v1/cases.json` (7 synthetic transcripts: 6 Japanese incl. one long multi-chunk meeting, 1 English).");
        sb.AppendLine("Recall = reference items found (keyword groups). Spurious = decisions/actions produced where the reference has none.");
        sb.AppendLine("Invented # = numbers in the summary that do not occur in the transcript (kanji numerals normalized). Raw outputs: `benchmarks/runs/summary-v1/` (not committed).");
        sb.AppendLine();
        sb.AppendLine("| Provider | Decisions | Actions | Owner | Due | Questions | Facts | Spurious | Invented # | Cited | Language | Avg s | Max s | Peak MB | Failed |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in providers)
        {
            var r = rows.Where(x => x.Provider == p).ToList();
            var ok = r.Where(x => !x.Failed).ToList();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {p} | {Pct(ok.Sum(x => x.DecisionHits), ok.Sum(x => x.Decisions))} | {Pct(ok.Sum(x => x.ActionHits), ok.Sum(x => x.Actions))} | {Pct(ok.Sum(x => x.OwnerHits), ok.Sum(x => x.OwnersExpected))} | {Pct(ok.Sum(x => x.DueHits), ok.Sum(x => x.DuesExpected))} | {Pct(ok.Sum(x => x.QuestionHits), ok.Sum(x => x.Questions))} | {Pct(ok.Sum(x => x.FactHits), ok.Sum(x => x.Facts))} | {ok.Sum(x => x.Spurious)} | {ok.Sum(x => x.InventedNumbers)} | {Pct(ok.Sum(x => x.CitedItems), ok.Sum(x => x.Items))} | {(ok.Count == 0 ? 0 : ok.Average(x => x.LanguageShare)):P0} | {(ok.Count == 0 ? 0 : ok.Average(x => x.Seconds)):F1} | {(ok.Count == 0 ? 0 : ok.Max(x => x.Seconds)):F1} | {(ok.Count == 0 ? 0 : ok.Max(x => x.PeakMb)):F0} | {r.Count(x => x.Failed)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Per case");
        sb.AppendLine();
        foreach (var c in cases)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {c.Id} ({c.Language}, {c.Input.Segments.Count} lines)");
            sb.AppendLine();
            foreach (var r in rows.Where(x => x.Case == c.Id))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {r.Provider}: {r}");
            }

            sb.AppendLine();
        }

        var dir = Path.Combine(root, "docs", "benchmarks", "summary-v1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), sb.ToString());
        Console.WriteLine($"Report: {Path.Combine(dir, "README.md")} (raw outputs in {runsDir})");
    }

    private static string Hardware() =>
        $"{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")} ({Environment.ProcessorCount} logical CPUs)";

    private static string Safe(string name) => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    [GeneratedRegex("[0-9]+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex("[〇一二三四五六七八九十百千万億]+")]
    private static partial Regex KanjiRegex();

    internal sealed class FixedAcceleration(bool gpu) : IAccelerationProbe
    {
        public bool HasCapableGpu => gpu;

        public long GpuMemoryBytes => gpu ? 16L * 1024 * 1024 * 1024 : 0;

        public long TotalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    internal sealed class AcPower : ISystemResourceProbe
    {
        public SystemResources Current { get; } = new(OnAcPower: true, BatteryPercent: 100, EnergySaverOn: false, CpuLoadOthers: 0, AvailableMemoryBytes: 8L << 30);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>Samples this process's working set every 100 ms (model weights live in-process).</summary>
    private sealed class PeakMemory : IDisposable
    {
        private readonly Timer _timer;
        private long _peak;

        public PeakMemory()
        {
            Sample();
            _timer = new Timer(_ => Sample(), null, 100, 100);
        }

        public double PeakMb => Interlocked.Read(ref _peak) / 1024.0 / 1024.0;

        public void Dispose()
        {
            _timer.Dispose();
            Sample();
        }

        private void Sample()
        {
            using var process = Process.GetCurrentProcess();
            var current = process.WorkingSet64;
            long seen;
            while ((seen = Interlocked.Read(ref _peak)) < current && Interlocked.CompareExchange(ref _peak, current, seen) != seen)
            {
            }
        }
    }
}
