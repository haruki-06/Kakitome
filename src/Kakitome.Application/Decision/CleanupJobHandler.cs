using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Decision;

/// <summary>
/// <c>cleanup</c>: Raw → Clean transcript (docs/04 "Transcript cleanup"). Applies only low-risk edits from the decision
/// engine, keeps the original ASR text in <see cref="TranscriptSegment.RawText"/>, never touches user-edited segments,
/// never removes segments, and records higher-risk edits as suggestions. Also tags suspicious segments and the
/// recording type (used to choose the summary profile). Corrections from the user's glossary (ADR-030) are applied
/// here too, so the original wording stays in <see cref="TranscriptSegment.RawText"/>.
/// </summary>
public sealed class CleanupJobHandler(LibraryService library, IDecisionEngine engine, GlossaryService glossaries, TimeProvider time) : IJobHandler
{
    public const string JobKind = "cleanup";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        var transcript = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentJobException("There is no transcript to clean up.");

        var source = $"cleanup:{engine.Id}";
        if (transcript.Lineage.Any(l => l.Source == source && l.Revision == transcript.Revision))
        {
            return; // already cleaned by this engine (idempotent re-run)
        }

        var metadata = await library.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        var glossary = await glossaries.LoadForProjectAsync(metadata.Project, cancellationToken).ConfigureAwait(false);
        var now = time.GetLocalNow();
        var suggestions = new List<EditSuggestion>();
        var labels = InventedLabels.Find(transcript.Segments.Where(s => !s.Edited).Select(s => s.RawText ?? s.Text), transcript.Language);
        var changed = 0;
        var corrected = 0;
        foreach (var segment in transcript.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segment.Flags = MergeFlags(segment.Flags, engine.AssessSegment(segment.Text, segment.Confidence,
                segment.EndSeconds - segment.StartSeconds, transcript.Language));

            if (segment.Edited)
            {
                continue; // the user's words are final
            }

            var original = segment.RawText ?? segment.Text;
            var candidates = engine.FindEditCandidates(original, transcript.Language)
                .Concat(InventedLabels.Candidates(original, labels))
                .ToList();
            var cleaned = Apply(original, candidates.Where(c => c.Risk == EditRisk.Low));
            var withGlossary = glossary.Apply(cleaned);
            if (!string.Equals(withGlossary, cleaned, StringComparison.Ordinal))
            {
                corrected++;
                cleaned = withGlossary;
            }

            foreach (var c in candidates.Where(c => c.Risk == EditRisk.High))
            {
                suggestions.Add(new EditSuggestion
                {
                    SegmentId = segment.Id,
                    Kind = c.Kind.ToString().ToLowerInvariant(),
                    Original = original.Substring(c.Start, c.Length),
                    Replacement = c.Replacement,
                    Reason = c.Reason,
                });
            }

            if (!string.Equals(cleaned, segment.Text, StringComparison.Ordinal))
            {
                segment.RawText = original;
                segment.Text = cleaned;
                changed++;
            }
        }

        transcript.Revision++;
        transcript.Kind = TranscriptKind.Clean;
        transcript.UpdatedAt = now;
        transcript.Suggestions = suggestions.Count == 0 ? null : suggestions;
        transcript.Lineage.Add(new TranscriptLineageEntry
        {
            Kind = TranscriptKind.Clean,
            Revision = transcript.Revision,
            At = now,
            Source = source,
            Note = corrected == 0
                ? $"{changed} segment(s) cleaned, {suggestions.Count} suggestion(s)"
                : $"{changed} segment(s) cleaned ({corrected} with glossary corrections), {suggestions.Count} suggestion(s)",
        });

        var saved = await library.SaveTranscriptAsync(transcript, ConflictPolicy.Fail, cancellationToken).ConfigureAwait(false);
        if (!saved.Saved)
        {
            throw new PermanentJobException(
                $"{string.Join(", ", saved.ConflictingFiles)} was edited outside Kakitome; cleanup did not overwrite it.");
        }

        var type = engine.ClassifyRecording(transcript.Segments.Select(s => s.Text).ToList(),
            Math.Max(1, transcript.Speakers.Count), transcript.Segments.Count == 0 ? 0 : transcript.Segments[^1].EndSeconds, transcript.Language);
        await library.UpdateMetadataAsync(id, m => m.ProcessingProfile ??= type.Type.ToString().ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);
        await context.SetEngineAsync($"Kakitome {engine.Id} (local)").ConfigureAwait(false);
    }

    /// <summary>
    /// Applies edits from the end so earlier indices stay valid. Overlapping edits (rules run independently) keep the one
    /// that starts first, the longer one on a tie.
    /// </summary>
    internal static string Apply(string text, IEnumerable<EditCandidate> edits)
    {
        var kept = new List<EditCandidate>();
        var end = 0;
        foreach (var e in edits.Where(e => e.Start >= 0 && e.Start + e.Length <= text.Length).OrderBy(e => e.Start).ThenByDescending(e => e.Length))
        {
            if (e.Start >= end)
            {
                kept.Add(e);
                end = e.Start + e.Length;
            }
        }

        var result = text;
        foreach (var e in Enumerable.Reverse(kept))
        {
            result = string.Concat(result.AsSpan(0, e.Start), e.Replacement, result.AsSpan(e.Start + e.Length));
        }

        return result.Trim();
    }

    private static List<string>? MergeFlags(List<string>? existing, IReadOnlyList<string> found)
    {
        var all = (existing ?? []).Concat(found).Distinct(StringComparer.Ordinal).ToList();
        return all.Count == 0 ? null : all;
    }
}
