using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Kakitome.Application.Jobs;
using Kakitome.Domain.Recordings;

namespace Kakitome.Storage.Persistence;

/// <summary>SQLite-backed <see cref="IJobStore"/>. Every update is its own transaction (durable before returning).</summary>
public sealed class EfJobStore(IDbContextFactory<KakitomeDbContext> contextFactory, KakitomeDatabase database) : IJobStore
{
    private static readonly int[] ActiveStates = [(int)JobState.Pending, (int)JobState.Running, (int)JobState.Paused];

    public async Task AddAsync(JobRecord job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = new JobRow { Kind = job.Kind };
        Apply(row, job);
        db.Jobs.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(JobRecord job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Jobs.FirstOrDefaultAsync(j => j.Id == job.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new JobRow { Kind = job.Kind };
            db.Jobs.Add(row);
        }

        Apply(row, job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(DateTimeOffset? terminalSince = null, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Jobs.AsNoTracking().OrderByDescending(j => j.CreatedAtUtcTicks).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Select(ToRecord)
            .Where(j => terminalSince is null || !j.IsTerminal || (j.FinishedAt ?? j.UpdatedAt) >= terminalSince)
            .ToList();
    }

    public async Task<IReadOnlyList<JobRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Jobs.AsNoTracking().Where(j => ActiveStates.Contains(j.State))
            .OrderBy(j => j.CreatedAtUtcTicks).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<JobRecord>> ListDependentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Jobs.AsNoTracking().Where(j => j.DependsOn == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<int> PurgeFinishedAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var terminal = await db.Jobs.Where(j => !ActiveStates.Contains(j.State)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var old = terminal.Where(j => (j.FinishedAt ?? j.UpdatedAt) < before).ToList();
        db.Jobs.RemoveRange(old);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return old.Count;
    }

    private async Task<KakitomeDbContext> OpenAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(JobRow row, JobRecord job)
    {
        row.Id = job.Id;
        row.Kind = job.Kind;
        row.RecordingId = job.RecordingId?.ToString();
        row.State = (int)job.State;
        row.ResourceClass = (int)job.ResourceClass;
        row.Priority = job.Priority;
        row.DependsOn = job.DependsOn;
        row.PipelineId = job.PipelineId;
        row.Payload = job.Payload;
        row.Checkpoint = job.Checkpoint;
        row.Attempts = job.Attempts;
        row.MaxAttempts = job.MaxAttempts;
        row.CreatedAt = job.CreatedAt;
        row.CreatedAtUtcTicks = job.CreatedAt.UtcTicks;
        row.UpdatedAt = job.UpdatedAt;
        row.StartedAt = job.StartedAt;
        row.FinishedAt = job.FinishedAt;
        row.NotBefore = job.NotBefore;
        row.Progress = job.Progress;
        row.ProgressText = job.ProgressText;
        row.WaitReason = (int)job.WaitReason;
        row.LastError = job.LastError;
        row.Engine = job.Engine;
    }

    private static JobRecord ToRecord(JobRow row) => new()
    {
        Id = row.Id,
        Kind = row.Kind,
        RecordingId = row.RecordingId is null ? null : RecordingId.Parse(row.RecordingId, CultureInfo.InvariantCulture),
        State = (JobState)row.State,
        ResourceClass = (JobResourceClass)row.ResourceClass,
        Priority = row.Priority,
        DependsOn = row.DependsOn,
        PipelineId = row.PipelineId,
        Payload = row.Payload,
        Checkpoint = row.Checkpoint,
        Attempts = row.Attempts,
        MaxAttempts = row.MaxAttempts,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        StartedAt = row.StartedAt,
        FinishedAt = row.FinishedAt,
        NotBefore = row.NotBefore,
        Progress = row.Progress,
        ProgressText = row.ProgressText,
        WaitReason = (JobWaitReason)row.WaitReason,
        LastError = row.LastError,
        Engine = row.Engine,
    };
}
