using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Summaries;

/// <summary>
/// <c>summary</c>: local structured summary written to <c>summary.json/.md</c>. Works from the current (clean or
/// user-edited) transcript in its language. A failure here never touches the transcript (docs/09).
/// </summary>
public sealed partial class SummaryJobHandler(
    LibraryService library,
    IEnumerable<ISummaryProvider> providers,
    Kakitome.Application.Settings.ISettingsStore settings,
    TimeProvider time,
    Microsoft.Extensions.Logging.ILogger<SummaryJobHandler> logger) : IJobHandler
{
    public const string JobKind = "summary";

    private readonly List<ISummaryProvider> _providers = providers.OrderByDescending(p => p.Preference).ToList();

    public string Kind => JobKind;

    /// <summary>Heavy only when an LLM provider would run; extractive summaries are cheap.</summary>
    public JobResourceClass ResourceClass => Candidates(null).FirstOrDefault()?.ResourceClass ?? JobResourceClass.Light;

    /// <summary>Available providers in preference order; "extractive" in Settings excludes local LLMs.</summary>
    private IEnumerable<ISummaryProvider> Candidates(string? language) =>
        _providers.Where(p => p.IsAvailable(language)
                              && (settings.Current.Processing.SummaryEngine != Kakitome.Application.Settings.SummaryEngines.Extractive
                                  || p.ResourceClass == JobResourceClass.Light));

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        var transcript = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentJobException("There is no transcript to summarize.");

        var existing = await library.LoadSummaryAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing?.Generation.TranscriptRevision == transcript.Revision && context.Job.Payload is null)
        {
            return; // up to date
        }

        var metadata = await library.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        var candidates = Candidates(transcript.Language).ToList();
        if (candidates.Count == 0)
        {
            throw new PermanentJobException("No summary provider is available.");
        }

        // Speakers by name ("Speaker 1", or the name the user gave), never internal ids such as "audio-50".
        var speakers = transcript.Speakers.ToDictionary(s => s.Id, s => s.Label ?? s.Id, StringComparer.Ordinal);
        var input = new SummaryInput(
            metadata.Title,
            transcript.Language,
            metadata.ProcessingProfile,
            transcript.Segments
                .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                .Select(s => new SummarySourceSegment(s.Id, s.StartSeconds, s.EndSeconds, s.Text, s.Speaker is { } sp ? speakers.GetValueOrDefault(sp, sp) : null))
                .ToList());

        // A local LLM can fail (memory, damaged model); the next provider (ultimately the extractive one) still
        // produces a summary instead of leaving the recording without one.
        SummaryDraft? draft = null;
        for (var i = 0; draft is null; i++)
        {
            var provider = candidates[i];
            await context.SetEngineAsync(provider.Id).ConfigureAwait(false);
            try
            {
                draft = await provider.SummarizeAsync(input, context.Budget, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && i < candidates.Count - 1)
            {
                LogProviderFailed(ex, provider.Id);
            }
        }

        var summary = new SummaryDocument
        {
            RecordingId = id,
            Language = transcript.Language,
            Title = draft.Title,
            Overview = draft.Overview,
            KeyPoints = draft.KeyPoints.Select(ToItem).ToList(),
            Decisions = draft.Decisions.Select(ToItem).ToList(),
            ActionItems = draft.ActionItems.Select(a => new ActionItem
            {
                Text = a.Text,
                AtSeconds = Round(a.AtSeconds),
                SegmentIds = a.SegmentIds?.ToList(),
                Owner = a.Owner,
                Due = a.Due,
            }).ToList(),
            Questions = draft.Questions.Select(ToItem).ToList(),
            Topics = draft.Topics.ToList(),
            Generation = new SummaryGeneration
            {
                Engine = new EngineInfo { Provider = draft.Engine, Model = draft.Model, Locality = "local" },
                CreatedAt = time.GetLocalNow(),
                TranscriptRevision = transcript.Revision,
                Profile = draft.Kind ?? metadata.ProcessingProfile,
            },
        };

        var saved = await library.SaveSummaryAsync(summary, ConflictPolicy.Fail, cancellationToken).ConfigureAwait(false);
        if (!saved.Saved)
        {
            throw new PermanentJobException(
                $"{string.Join(", ", saved.ConflictingFiles)} was edited outside Kakitome; the summary was not overwritten.");
        }

        await context.SetEngineAsync(draft.Model is null ? draft.Engine : $"{draft.Engine} / {draft.Model}").ConfigureAwait(false);
    }

    private static SummaryItem ToItem(DraftItem d) => new() { Text = d.Text, AtSeconds = Round(d.AtSeconds), SegmentIds = d.SegmentIds?.ToList() };

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Summary provider {Provider} failed; trying the next one")]
    private partial void LogProviderFailed(Exception ex, string provider);
}
