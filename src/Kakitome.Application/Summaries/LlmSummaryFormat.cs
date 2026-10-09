using System.Globalization;
using System.Text;
using System.Text.Json;
using Kakitome.Domain.Rendering;

namespace Kakitome.Application.Summaries;

/// <summary>
/// Prompt, output grammar and result mapping for local-LLM summaries (engine-independent, unit-tested). The model sees
/// numbered transcript lines and must answer with JSON that cites those numbers; decoding is constrained by a GBNF
/// grammar so the output always parses. Citations that do not exist are dropped, and items are timed from the
/// first cited line — the model never invents timestamps.
/// </summary>
public static class LlmSummaryFormat
{
    /// <summary>Recording kinds the model chooses from first; they decide which sections apply (<see cref="ApplyKind"/>).</summary>
    public const string Meeting = "meeting";

    public const string Lecture = "lecture";

    /// <summary>JSON shape enforced during decoding (llama.cpp GBNF).</summary>
    public static string Grammar { get; } = BuildGrammar(minKeyPoints: 0);

    /// <summary>
    /// At least 3 key points: the 4B model often wrote a single one even for an hour of talk, despite the prompt.
    /// </summary>
    public static string ReduceGrammar { get; } = BuildGrammar(minKeyPoints: 3);

    /// <summary>The grammar for a pass over <paramref name="lines"/> lines: 3+ key points unless there is little to say.</summary>
    public static string GrammarFor(int lines) => lines >= 10 ? ReduceGrammar : Grammar;

    private static string BuildGrammar(int minKeyPoints) => """
        root    ::= "{" ws "\"kind\":" ws kind "," ws "\"title\":" ws string "," ws "\"overview\":" ws string "," ws "\"keyPoints\":" ws KEYPOINTS "," ws "\"decisions\":" ws items "," ws "\"actionItems\":" ws actions "," ws "\"questions\":" ws items "," ws "\"topics\":" ws strings ws "}"
        kind    ::= "\"meeting\"" | "\"lecture\"" | "\"conversation\"" | "\"other\""
        items   ::= "[" ws ( item ( "," ws item ){0,9} )? ws "]"
        kitems  ::= "[" ws item ( "," ws item ){MIN,7} ws "]"
        item    ::= "{" ws "\"text\":" ws string "," ws "\"lines\":" ws ids ws "}"
        actions ::= "[" ws ( action ( "," ws action ){0,9} )? ws "]"
        action  ::= "{" ws "\"text\":" ws string "," ws "\"owner\":" ws nstring "," ws "\"due\":" ws nstring "," ws "\"lines\":" ws ids ws "}"
        ids     ::= "[" ws ( id ( "," ws id ){0,5} )? ws "]"
        id      ::= [1-9] [0-9]{0,3}
        strings ::= "[" ws ( string ( "," ws string ){0,5} )? ws "]"
        nstring ::= string | "null"
        string  ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" ["\\/bfnrt] ){0,300} "\""
        ws      ::= [ \t\n]{0,24}
        """
        .Replace("KEYPOINTS", minKeyPoints > 0 ? "kitems" : "items", StringComparison.Ordinal)
        .Replace("{MIN,7}", "{" + Math.Max(0, minKeyPoints - 1).ToString(CultureInfo.InvariantCulture) + ",7}", StringComparison.Ordinal);

    public static string SystemPrompt(string? language)
    {
        var japanese = IsJapanese(language);
        var outputLanguage = japanese ? "Japanese" : language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "English" : "the transcript's language";
        return $$"""
            You summarize a recording transcript for Kakitome. Lines are numbered like [12].
            Rules:
            - Write every text in {{outputLanguage}}.
            - Use only what is said in the transcript. Never invent names, dates, numbers or tasks.
            - kind: what the recording is. "meeting" = any talk about work, study or plans: meetings, 1-on-1s,
              calls, consultations, interviews; "lecture" = a talk, class or presentation; "conversation" = casual
              chat or entertainment: variety videos, vlogs, games, podcasts for fun; "other" = anything else.
            - title: a short title of the recording.
            - overview: 1-3 sentences.
            - keyPoints: the 3-7 most important points.
            - decisions: only for a meeting - things explicitly decided or agreed. Otherwise empty.
            - actionItems: only for a meeting or lecture - tasks someone committed to do after the recording (not what people do
              during it). owner (a speaker name as written in the transcript) and due only when stated, otherwise
              null. Otherwise empty.
            - questions: only for a meeting or lecture - important questions left unanswered. Otherwise empty.
            - topics: 1-5 short topic words.
            - lines: the numbers of the transcript lines that support the item.
            Answer with compact JSON only (no line breaks), in exactly this shape:
            {"kind":"meeting","title":"...","overview":"...","keyPoints":[{"text":"...","lines":[1,2]}],"decisions":[{"text":"...","lines":[3]}],"actionItems":[{"text":"...","owner":"who or null","due":"when or null","lines":[4]}],"questions":[{"text":"...","lines":[5]}],"topics":["..."]}
            """;
    }

    /// <summary>The transcript as numbered lines: <c>[n] (hh:mm:ss) Speaker: text</c>.</summary>
    public static string UserPrompt(SummaryInput input, int firstLine = 1, IReadOnlyList<SummarySourceSegment>? segments = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var builder = new StringBuilder();
        builder.Append(IsJapanese(input.Language) ? "タイトル: " : "Title: ").AppendLine(input.Title);
        builder.AppendLine(IsJapanese(input.Language) ? "文字起こし:" : "Transcript:");
        var number = firstLine;
        foreach (var segment in segments ?? input.Segments)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[{number++}] ({TimeFormat.Clock(segment.StartSeconds)}) ");
            if (!string.IsNullOrWhiteSpace(segment.Speaker))
            {
                builder.Append(segment.Speaker).Append(": ");
            }

            builder.AppendLine(segment.Text.Replace('\n', ' ').Trim());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Maps the model's JSON to a draft. Line numbers are 1-based indexes into <paramref name="segments"/>; unknown
    /// numbers are dropped and items with empty text are skipped.
    /// </summary>
    /// <exception cref="JsonException">The output is not the expected JSON.</exception>
    public static SummaryDraft Parse(string json, IReadOnlyList<SummarySourceSegment> segments, string engine, string model)
    {
        ArgumentNullException.ThrowIfNull(segments);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        List<DraftItem> Items(string name) => [.. Array(root, name).Select(e => Item(e, segments)).OfType<DraftItem>()];

        return new SummaryDraft
        {
            Kind = Text(root, "kind"),
            Title = Text(root, "title"),
            Overview = Text(root, "overview"),
            KeyPoints = Items("keyPoints"),
            Decisions = Items("decisions"),
            ActionItems = [.. Array(root, "actionItems").Select(e => Action(e, segments)).OfType<DraftAction>()],
            Questions = Items("questions"),
            Topics = [.. Array(root, "topics").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()?.Trim() : null)
                .OfType<string>().Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)],
            Engine = engine,
            Model = model,
        };
    }

    /// <summary>
    /// Drops sections that do not fit the recording: decisions only for meetings, action items and open questions only
    /// for meetings and lectures. A small model otherwise fills the meeting template from any chat or video ("ate one
    /// more" as an action item).
    /// </summary>
    public static SummaryDraft ApplyKind(SummaryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var meeting = draft.Kind is null or Meeting;
        var lecture = draft.Kind == Lecture;
        return draft with
        {
            Decisions = meeting ? draft.Decisions : [],
            ActionItems = meeting || lecture ? draft.ActionItems : [], // assignments ("report by next week")
            Questions = meeting || lecture ? draft.Questions : [],
        };
    }

    /// <summary>Japanese is the primary language (CLAUDE.md); unknown language defaults to it.</summary>
    public static bool IsJapanese(string? language) => language is null || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

#pragma warning disable CA1859 // Returns an empty sequence when the property is missing.
    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
#pragma warning restore CA1859

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    /// <summary>Owner/due: models sometimes write "null" or "none" as text instead of JSON null.</summary>
    private static string? Optional(JsonElement element, string name) =>
        Text(element, name) is { } value && !Kakitome.Domain.Rendering.ReadableText.IsMissing(value) ? value : null;

    private static DraftItem? Item(JsonElement element, IReadOnlyList<SummarySourceSegment> segments)
    {
        var text = Text(element, "text");
        if (text is null)
        {
            return null;
        }

        var cited = Cited(element, segments);
        return new DraftItem(text, cited.FirstOrDefault()?.StartSeconds, cited.Count > 0 ? [.. cited.Select(s => s.Id)] : null);
    }

    private static DraftAction? Action(JsonElement element, IReadOnlyList<SummarySourceSegment> segments)
    {
        var text = Text(element, "text");
        if (text is null)
        {
            return null;
        }

        var cited = Cited(element, segments);
        return new DraftAction(
            text,
            cited.FirstOrDefault()?.StartSeconds,
            cited.Count > 0 ? [.. cited.Select(s => s.Id)] : null,
            Optional(element, "owner"),
            Optional(element, "due"));
    }

    private static List<SummarySourceSegment> Cited(JsonElement element, IReadOnlyList<SummarySourceSegment> segments) =>
        [.. Array(element, "lines")
            .Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : 0)
            .Where(n => n >= 1 && n <= segments.Count)
            .Distinct()
            .Order()
            .Select(n => segments[n - 1])];
}
