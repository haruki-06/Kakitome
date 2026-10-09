using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Library;

/// <summary>
/// Rebuildable, derived index of the Library (SQLite). Losing it never loses user content: everything here
/// can be recomputed from the Library files by <see cref="LibraryService.RebuildIndexAsync"/>.
/// </summary>
public interface ILibraryIndex
{
    Task<RecordingIndexEntry?> FindAsync(RecordingId id, CancellationToken cancellationToken = default);

    Task<RecordingIndexEntry?> FindByFolderAsync(string recordingFolder, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecordingIndexEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(RecordingIndexEntry entry, CancellationToken cancellationToken = default);

    Task RemoveAsync(RecordingId id, CancellationToken cancellationToken = default);

    /// <summary>Last known fingerprint of an artifact as written or accepted by Kakitome.</summary>
    Task<FileStamp?> GetArtifactStampAsync(RecordingId id, string fileName, CancellationToken cancellationToken = default);

    Task SetArtifactStampAsync(RecordingId id, string fileName, FileStamp? stamp, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, FileStamp>> GetArtifactStampsAsync(RecordingId id, CancellationToken cancellationToken = default);

    Task ReplaceIssuesAsync(IReadOnlyList<LibraryIssue> issues, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryIssue>> ListIssuesAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every derived row (recordings, artifacts, issues). App state such as jobs is kept.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Index row for one recording, derived from its <c>metadata.json</c> and folder contents.</summary>
public sealed record RecordingIndexEntry
{
    public required RecordingId Id { get; init; }

    public required string Folder { get; init; }

    public required string Title { get; init; }

    public required string Project { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RecordedAt { get; init; }

    public double? DurationSeconds { get; init; }

    public required RecordingSourceType SourceType { get; init; }

    public CaptureStatus? CaptureStatus { get; init; }

    public string? Language { get; init; }

    public bool HasAudio { get; init; }

    public bool HasTranscript { get; init; }

    public bool HasSummary { get; init; }

    /// <summary>True when a canonical artifact changed outside Kakitome since it was last written/accepted.</summary>
    public bool HasExternalChanges { get; init; }
}

public enum LibraryIssueKind
{
    /// <summary>A recording folder has no <c>metadata.json</c>.</summary>
    MissingMetadata,

    /// <summary><c>metadata.json</c> (or another JSON artifact) exists but cannot be parsed.</summary>
    UnreadableMetadata,

    /// <summary>Two folders claim the same recording id (for example after a manual copy).</summary>
    DuplicateId,

    /// <summary>A canonical artifact was edited outside Kakitome and differs from what Kakitome last wrote.</summary>
    ExternalChange,
}

/// <summary>Something the user may need to look at. Issues never cause files to be modified or deleted.</summary>
public sealed record LibraryIssue(LibraryIssueKind Kind, string Folder, string? FileName, string Message, DateTimeOffset DetectedAt);
