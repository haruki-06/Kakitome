using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Search;

/// <summary>What part of a recording a search hit came from.</summary>
public enum SearchHitKind
{
    Title = 0,
    Transcript = 1,
    Summary = 2,
}

/// <param name="StartSeconds">Position in the recording (transcript hits), for "play from here".</param>
/// <param name="Snippet">Matched text with the match wrapped in [ ].</param>
public sealed record SearchHit(RecordingId RecordingId, SearchHitKind Kind, string? SegmentId, double? StartSeconds, string Snippet, double Rank);

/// <summary>One searchable unit of a recording.</summary>
public sealed record SearchDocument(SearchHitKind Kind, string? SegmentId, double? StartSeconds, string Text);

/// <summary>Local full-text index (SQLite FTS5, docs/01 "Search: Phase 1"). Derived and rebuildable from the Library.</summary>
public interface ISearchIndex
{
    Task ReplaceAsync(RecordingId id, IReadOnlyList<SearchDocument> documents, CancellationToken cancellationToken = default);

    Task RemoveAsync(RecordingId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 100, CancellationToken cancellationToken = default);

    Task<int> CountIndexedRecordingsAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
