namespace Kakitome.Application.Jobs;

/// <summary>Durable storage of jobs (SQLite). App state: survives restarts, reset by Factory Reset.</summary>
public interface IJobStore
{
    Task AddAsync(JobRecord job, CancellationToken cancellationToken = default);

    Task UpdateAsync(JobRecord job, CancellationToken cancellationToken = default);

    Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>All jobs, newest first (terminal jobs older than <paramref name="terminalSince"/> excluded).</summary>
    Task<IReadOnlyList<JobRecord>> ListAsync(DateTimeOffset? terminalSince = null, CancellationToken cancellationToken = default);

    /// <summary>Non-terminal jobs (Pending/Running/Paused).</summary>
    Task<IReadOnlyList<JobRecord>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Jobs that directly depend on <paramref name="id"/>.</summary>
    Task<IReadOnlyList<JobRecord>> ListDependentsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes terminal job rows finished before <paramref name="before"/> (history cleanup; no user data).</summary>
    Task<int> PurgeFinishedAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}

/// <summary>Executes one kind of job. Handlers must be idempotent: a job may run again after a crash.</summary>
public interface IJobHandler
{
    string Kind { get; }

    JobResourceClass ResourceClass { get; }

    /// <summary>
    /// Does the work. Use <see cref="JobContext.SaveCheckpointAsync"/> to persist resume points. Throw
    /// <see cref="TransientJobException"/> for retryable problems; any other exception counts as a failed attempt.
    /// Cancellation means the user cancelled or the scheduler deferred the job; it will be resumed later if deferred.
    /// </summary>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>A problem worth retrying later (device busy, file locked, temporary resource shortage).</summary>
public sealed class TransientJobException : Exception
{
    public TransientJobException()
    {
    }

    public TransientJobException(string message)
        : base(message)
    {
    }

    public TransientJobException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Everything a handler may use while running.</summary>
public sealed class JobContext
{
    private readonly Func<JobRecord, Task> _persist;

    internal JobContext(JobRecord job, ResourceBudget budget, Func<JobRecord, Task> persist)
    {
        Job = job;
        Budget = budget;
        _persist = persist;
    }

    /// <summary>The job as of the last persisted update.</summary>
    public JobRecord Job { get; private set; }

    /// <summary>How much CPU the handler should use (threads) and whether to prefer efficiency cores.</summary>
    public ResourceBudget Budget { get; }

    public Task ReportProgressAsync(double? progress, string? text = null) =>
        UpdateAsync(Job with { Progress = progress is { } p ? Math.Clamp(p, 0, 1) : null, ProgressText = text });

    /// <summary>Persists a resume point (handler-defined JSON) so a crash or deferral does not redo finished work.</summary>
    public Task SaveCheckpointAsync(string checkpoint) => UpdateAsync(Job with { Checkpoint = checkpoint });

    /// <summary>Records the engine/model identity used, shown in the Processing Queue.</summary>
    public Task SetEngineAsync(string engine) => UpdateAsync(Job with { Engine = engine });

    private async Task UpdateAsync(JobRecord updated)
    {
        Job = updated;
        await _persist(updated).ConfigureAwait(false);
    }
}

/// <summary>Resources a running job may use under the current policy.</summary>
public sealed record ResourceBudget(int MaxThreads, bool PreferEfficiency)
{
    public static ResourceBudget Default { get; } = new(Math.Max(1, Environment.ProcessorCount / 2), PreferEfficiency: false);
}
