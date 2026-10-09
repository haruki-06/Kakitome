using Kakitome.Application.Asr;
using Kakitome.Application.Decision;
using Kakitome.Application.Jobs;
using Kakitome.Application.Recording;
using Kakitome.Application.Search;
using Kakitome.Application.Summaries;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Library;

public sealed record ProjectInfo(string Name, int RecordingCount, DateTimeOffset? LastRecordedAt);

/// <summary>
/// User-initiated actions on recordings and projects (from Library, Recording detail and Projects pages). Every
/// destructive action here is explicit and recoverable: deletion goes to the Recycle Bin, never permanent.
/// </summary>
public sealed class RecordingActions(
    LibraryService library,
    ILibraryStore store,
    IRecycleBin recycleBin,
    JobScheduler scheduler,
    SearchService search,
    TimeProvider time)
{
    public async Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var recordings = await library.ListRecordingsAsync(cancellationToken).ConfigureAwait(false);
        var byFolder = recordings
            .GroupBy(r => r.Folder.Split('/')[1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return store.EnumerateProjectFolders()
            .Select(folder =>
            {
                var items = byFolder.GetValueOrDefault(folder) ?? [];
                var display = items.Select(i => i.Project).FirstOrDefault() ?? folder;
                return new ProjectInfo(display, items.Count, items.Count == 0 ? null : items.Max(i => i.RecordedAt ?? i.CreatedAt));
            })
            .OrderByDescending(p => p.LastRecordedAt ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string CreateProject(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return store.EnsureProjectFolder(name.Trim());
    }

    /// <summary>Moves a recording folder to the Recycle Bin. Returns false (nothing changed) when that is not possible.</summary>
    public async Task<bool> MoveToRecycleBinAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        foreach (var job in (await scheduler.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                     .Where(j => j.RecordingId == id && !j.IsTerminal))
        {
            await scheduler.CancelAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        var path = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
        if (!recycleBin.TryMoveToRecycleBin(path))
        {
            return false;
        }

        await library.ForgetAsync(id, cancellationToken).ConfigureAwait(false);
        await search.ReindexAsync(id, cancellationToken).ConfigureAwait(false); // removes the derived rows
        return true;
    }

    /// <summary>Re-runs ASR (replacing the transcript after the user confirmed) and the downstream stages.</summary>
    public Task<Guid> ReTranscribeAsync(RecordingId id, CancellationToken cancellationToken = default) =>
        EnqueueChainAsync(id, [(AsrJobHandler.JobKind, AsrJobHandler.ReplacePayload), (CleanupJobHandler.JobKind, null),
            (SummaryJobHandler.JobKind, "{\"force\":true}"), (IndexJobHandler.JobKind, null)], cancellationToken);

    public Task<Guid> RegenerateSummaryAsync(RecordingId id, CancellationToken cancellationToken = default) =>
        EnqueueChainAsync(id, [(SummaryJobHandler.JobKind, "{\"force\":true}"), (IndexJobHandler.JobKind, null)], cancellationToken);

    /// <summary>Saves a user correction of one segment (lineage: user edit; cleanup never overwrites it).</summary>
    public async Task EditSegmentAsync(RecordingId id, string segmentId, string newText, CancellationToken cancellationToken = default)
    {
        var transcript = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The recording has no transcript.");
        var segment = transcript.Segments.FirstOrDefault(s => s.Id == segmentId)
            ?? throw new KeyNotFoundException($"Segment {segmentId} not found.");
        var text = newText.Trim();
        if (string.Equals(text, segment.Text, StringComparison.Ordinal))
        {
            return;
        }

        segment.RawText ??= segment.Text;
        segment.Text = text;
        segment.Edited = true;
        transcript.Suggestions?.RemoveAll(s => s.SegmentId == segmentId);
        await SaveUserRevisionAsync(id, transcript, $"edited segment {segmentId}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies one cleanup suggestion the user accepted.</summary>
    public async Task AcceptSuggestionAsync(RecordingId id, EditSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        var transcript = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The recording has no transcript.");
        var segment = transcript.Segments.FirstOrDefault(s => s.Id == suggestion.SegmentId);
        var index = segment?.Text.IndexOf(suggestion.Original, StringComparison.Ordinal) ?? -1;
        transcript.Suggestions?.RemoveAll(s => s.SegmentId == suggestion.SegmentId && s.Original == suggestion.Original);
        if (segment is not null && index >= 0)
        {
            segment.RawText ??= segment.Text;
            segment.Text = string.Concat(segment.Text.AsSpan(0, index), suggestion.Replacement, segment.Text.AsSpan(index + suggestion.Original.Length)).Trim();
        }

        await SaveUserRevisionAsync(id, transcript, $"accepted suggestion in {suggestion.SegmentId}", cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveUserRevisionAsync(RecordingId id, TranscriptDocument transcript, string note, CancellationToken cancellationToken)
    {
        var now = time.GetLocalNow();
        transcript.Revision++;
        transcript.Kind = TranscriptKind.Edited;
        transcript.UpdatedAt = now;
        transcript.Lineage.Add(new TranscriptLineageEntry { Kind = TranscriptKind.Edited, Revision = transcript.Revision, At = now, Source = "user", Note = note });
        var saved = await library.SaveTranscriptAsync(transcript, ConflictPolicy.Fail, cancellationToken).ConfigureAwait(false);
        if (!saved.Saved)
        {
            throw new ExternalEditConflictException(saved.ConflictingFiles);
        }

        await search.ReindexAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> EnqueueChainAsync(RecordingId id, IReadOnlyList<(string Kind, string? Payload)> stages, CancellationToken cancellationToken)
    {
        var pipeline = Guid.CreateVersion7();
        Guid? previous = null;
        foreach (var (kind, payload) in stages.Where(s => scheduler.HasHandler(s.Kind)))
        {
            var job = await scheduler.EnqueueAsync(
                new JobRequest(kind) { RecordingId = id, DependsOn = previous, PipelineId = pipeline, Payload = payload, Priority = 10 },
                cancellationToken).ConfigureAwait(false);
            previous = job.Id;
        }

        return pipeline;
    }
}

/// <summary>A file the user edited outside Kakitome blocks the save; the UI offers to reload/reconcile.</summary>
public sealed class ExternalEditConflictException : Exception
{
    public ExternalEditConflictException()
    {
    }

    public ExternalEditConflictException(string message)
        : base(message)
    {
    }

    public ExternalEditConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ExternalEditConflictException(IReadOnlyList<string> files)
        : base($"Edited outside Kakitome: {string.Join(", ", files)}")
    {
        Files = files;
    }

    public IReadOnlyList<string> Files { get; } = [];
}
