using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Storage.Persistence;

/// <summary>SQLite implementation of the derived Library index.</summary>
public sealed class EfLibraryIndex(IDbContextFactory<KakitomeDbContext> contextFactory, KakitomeDatabase database) : ILibraryIndex
{
    /// <summary>Every query waits for migration/recovery, so early UI actions cannot hit an unmigrated database.</summary>
    private async Task<KakitomeDbContext> OpenAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordingIndexEntry?> FindAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = id.ToString();
        var row = await db.Recordings.AsNoTracking().Include(r => r.Tags)
            .FirstOrDefaultAsync(r => r.Id == key, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    public async Task<RecordingIndexEntry?> FindByFolderAsync(string recordingFolder, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Recordings.AsNoTracking().Include(r => r.Tags)
            .FirstOrDefaultAsync(r => r.Folder == recordingFolder, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    public async Task<IReadOnlyList<RecordingIndexEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Recordings.AsNoTracking().Include(r => r.Tags)
            .OrderByDescending(r => r.CreatedAtUtcTicks)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToEntry).ToList();
    }

    public async Task UpsertAsync(RecordingIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = entry.Id.ToString();

        // A folder can only belong to one recording; a stale row for the same folder is derived data.
        await db.Recordings.Where(r => r.Folder == entry.Folder && r.Id != key)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.Recordings.Include(r => r.Tags).FirstOrDefaultAsync(r => r.Id == key, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new RecordingRow { Id = key, Folder = entry.Folder, Title = entry.Title, Project = entry.Project };
            db.Recordings.Add(row);
        }

        row.Folder = entry.Folder;
        row.Title = entry.Title;
        row.Project = entry.Project;
        row.CreatedAt = entry.CreatedAt;
        row.CreatedAtUtcTicks = entry.CreatedAt.UtcTicks;
        row.RecordedAt = entry.RecordedAt;
        row.DurationSeconds = entry.DurationSeconds;
        row.SourceType = (int)entry.SourceType;
        row.CaptureStatus = entry.CaptureStatus is { } status ? (int)status : null;
        row.Language = entry.Language;
        row.HasAudio = entry.HasAudio;
        row.HasTranscript = entry.HasTranscript;
        row.HasSummary = entry.HasSummary;
        row.HasExternalChanges = entry.HasExternalChanges;

        row.Tags.Clear();
        var position = 0;
        foreach (var tag in entry.Tags.Distinct(StringComparer.Ordinal))
        {
            row.Tags.Add(new RecordingTagRow { RecordingId = key, Tag = tag, Position = position++ });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = id.ToString();
        await db.Artifacts.Where(a => a.RecordingId == key).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.RecordingTags.Where(t => t.RecordingId == key).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Recordings.Where(r => r.Id == key).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileStamp?> GetArtifactStampAsync(RecordingId id, string fileName, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = id.ToString();
        var row = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.RecordingId == key && a.FileName == fileName, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToStamp(row);
    }

    public async Task SetArtifactStampAsync(RecordingId id, string fileName, FileStamp? stamp, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = id.ToString();
        var row = await db.Artifacts.FirstOrDefaultAsync(a => a.RecordingId == key && a.FileName == fileName, cancellationToken)
            .ConfigureAwait(false);

        if (stamp is null)
        {
            if (row is not null)
            {
                db.Artifacts.Remove(row);
            }
        }
        else
        {
            if (row is null)
            {
                row = new ArtifactRow { RecordingId = key, FileName = fileName };
                db.Artifacts.Add(row);
            }

            row.Length = stamp.Length;
            row.LastWriteUtcTicks = stamp.LastWriteUtc.Ticks;
            row.Sha256 = stamp.Sha256;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, FileStamp>> GetArtifactStampsAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var key = id.ToString();
        var rows = await db.Artifacts.AsNoTracking().Where(a => a.RecordingId == key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.FileName, ToStamp, StringComparer.OrdinalIgnoreCase);
    }

    public async Task ReplaceIssuesAsync(IReadOnlyList<LibraryIssue> issues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await db.LibraryIssues.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.LibraryIssues.AddRange(issues.Select(i => new LibraryIssueRow
        {
            Kind = (int)i.Kind,
            Folder = i.Folder,
            FileName = i.FileName,
            Message = i.Message,
            DetectedAt = i.DetectedAt,
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LibraryIssue>> ListIssuesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.LibraryIssues.AsNoTracking().OrderBy(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new LibraryIssue((LibraryIssueKind)r.Kind, r.Folder, r.FileName, r.Message, r.DetectedAt)).ToList();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await db.Artifacts.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.RecordingTags.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Recordings.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.LibraryIssues.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RecordingIndexEntry ToEntry(RecordingRow row) => new()
    {
        Id = RecordingId.Parse(row.Id, CultureInfo.InvariantCulture),
        Folder = row.Folder,
        Title = row.Title,
        Project = row.Project,
        Tags = row.Tags.OrderBy(t => t.Position).Select(t => t.Tag).ToList(),
        CreatedAt = row.CreatedAt,
        RecordedAt = row.RecordedAt,
        DurationSeconds = row.DurationSeconds,
        SourceType = (RecordingSourceType)row.SourceType,
        CaptureStatus = row.CaptureStatus is { } s ? (CaptureStatus)s : null,
        Language = row.Language,
        HasAudio = row.HasAudio,
        HasTranscript = row.HasTranscript,
        HasSummary = row.HasSummary,
        HasExternalChanges = row.HasExternalChanges,
    };

    private static FileStamp ToStamp(ArtifactRow row) =>
        new(row.Length, new DateTime(row.LastWriteUtcTicks, DateTimeKind.Utc), row.Sha256);
}
