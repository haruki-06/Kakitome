using Kakitome.Application.Library;
using Kakitome.Application.Settings;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Asr;

/// <summary>What the "make a glossary" tip needs to show for one recording.</summary>
public sealed record GlossaryTip(string Title, string Project, IReadOnlyList<string> Topics, int DoubtfulSegments);

/// <summary>
/// Decides when Kakitome suggests a glossary for a finished recording (ADR-031). Misrecognized technical terms are
/// usually confident homophones, so the recognizer's confidence cannot tell when they happened (measured: a lecture
/// with ~90 such errors had mean confidence 0.89 and no low-confidence segment), and the local AI model could not
/// draft reliable corrections. Kakitome therefore suggests a glossary for every longer transcript that no glossary
/// applies to, and explains how to make one; it never builds or applies one by itself.
/// </summary>
public sealed class GlossaryTips(LibraryService library, GlossaryService glossaries, ISettingsStore settings)
{
    /// <summary>Short notes rarely contain enough technical vocabulary for a glossary to matter.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(5);

    /// <summary>The tip for <paramref name="id"/>, or null when it does not apply (or the user turned tips off).</summary>
    public async Task<GlossaryTip?> GetAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        if (!settings.Current.Processing.GlossaryTips)
        {
            return null;
        }

        RecordingMetadata metadata;
        try
        {
            metadata = await library.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        var transcript = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false);
        var glossary = await glossaries.LoadForProjectAsync(metadata.Project, cancellationToken).ConfigureAwait(false);
        if (!Applies(metadata, transcript, glossary))
        {
            return null;
        }

        var summary = await library.LoadSummaryAsync(id, cancellationToken).ConfigureAwait(false);
        return new GlossaryTip(metadata.Title, metadata.Project, summary?.Topics ?? [], transcript!.Segments.Count(s => s.Flags?.Count > 0));
    }

    /// <summary>A transcript of at least <see cref="MinimumDuration"/> exists and no glossary applies to its project.</summary>
    public static bool Applies(RecordingMetadata metadata, TranscriptDocument? transcript, Glossary glossary)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(glossary);
        return transcript is { Segments.Count: > 0 }
               && glossary.IsEmpty
               && (metadata.DurationSeconds ?? transcript.Segments[^1].EndSeconds) >= MinimumDuration.TotalSeconds;
    }

    public Task HideAsync(CancellationToken cancellationToken = default) =>
        settings.UpdateAsync(s => s.Processing.GlossaryTips = false, cancellationToken);
}
