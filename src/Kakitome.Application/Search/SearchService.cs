using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Search;

/// <summary>Builds search documents from Library files and keeps the FTS index in step.</summary>
public sealed class SearchService(LibraryService library, ISearchIndex index)
{
    /// <summary>Re-reads title/tags, transcript and summary of one recording from the Library and re-indexes it.</summary>
    public async Task ReindexAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        var entry = await library.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            await index.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var docs = new List<SearchDocument>
        {
            new(SearchHitKind.Title, null, null, string.Join(' ', new[] { entry.Title, entry.Project }.Concat(entry.Tags))),
        };

        var transcript = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false);
        if (transcript is not null)
        {
            docs.AddRange(transcript.Segments
                .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                .Select(s => new SearchDocument(SearchHitKind.Transcript, s.Id, s.StartSeconds, s.Text)));
        }

        var summary = await library.LoadSummaryAsync(id, cancellationToken).ConfigureAwait(false);
        if (summary is not null)
        {
            var parts = new[] { summary.Title, summary.Overview }
                .Concat(summary.KeyPoints.Select(k => k.Text))
                .Concat(summary.Decisions.Select(k => k.Text))
                .Concat(summary.ActionItems.Select(k => k.Text))
                .Concat(summary.Questions.Select(k => k.Text))
                .Concat(summary.Topics)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            docs.Add(new SearchDocument(SearchHitKind.Summary, null, null, string.Join('\n', parts)));
        }

        await index.ReplaceAsync(id, docs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rebuilds the whole index from the Library (after database loss or from Settings).</summary>
    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await index.ClearAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var entry in await library.ListRecordingsAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReindexAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 100, CancellationToken cancellationToken = default) =>
        index.SearchAsync(query, limit, cancellationToken);
}

/// <summary><c>index</c>: last pipeline stage; refreshes the search index for the recording.</summary>
public sealed class IndexJobHandler(SearchService search) : IJobHandler
{
    public const string JobKind = "index";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        await search.ReindexAsync(id, cancellationToken).ConfigureAwait(false);
        await context.SetEngineAsync("SQLite FTS5 (trigram)").ConfigureAwait(false);
    }
}
