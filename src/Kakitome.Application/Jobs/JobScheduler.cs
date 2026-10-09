using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kakitome.Application.Settings;

namespace Kakitome.Application.Jobs;

/// <summary>
/// Durable job queue and resource-aware scheduler. Every state transition is persisted before it takes effect, so
/// a crash at any point leaves a recoverable queue: jobs found Running at startup resume from their checkpoint.
/// </summary>
public sealed partial class JobScheduler : BackgroundService
{
    public const int MaxLightConcurrency = 2;
    public const int MaxHeavyConcurrency = 1;

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A deferred job is not reconsidered sooner than this (avoids start/stop flapping).</summary>
    internal static readonly TimeSpan DeferralCooldown = TimeSpan.FromSeconds(5);

    private readonly IJobStore _store;
    private readonly Dictionary<string, IJobHandler> _handlers;
    private readonly IResourceMonitor _resources;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<JobScheduler> _logger;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RunningJob> _running = new();
    private readonly TaskCompletionSource _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public JobScheduler(
        IJobStore store,
        IEnumerable<IJobHandler> handlers,
        IResourceMonitor resources,
        ISettingsStore settings,
        TimeProvider time,
        ILogger<JobScheduler> logger)
    {
        _store = store;
        _handlers = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
        _resources = resources;
        _settings = settings;
        _time = time;
        _logger = logger;
        _resources.Changed += (_, _) => Signal();
    }

    /// <summary>Raised after any job is added or changes state/progress (arbitrary thread).</summary>
    public event EventHandler<JobChangedEventArgs>? JobChanged;

    /// <summary>Completes once interrupted jobs from a previous run have been recovered.</summary>
    public Task Recovered => _recovered.Task;

    public bool HasHandler(string kind) => _handlers.ContainsKey(kind);

    public async Task<JobRecord> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_handlers.TryGetValue(request.Kind, out var handler))
        {
            throw new InvalidOperationException($"No handler is registered for job kind '{request.Kind}'.");
        }

        var now = _time.GetUtcNow();
        var job = new JobRecord
        {
            Id = Guid.CreateVersion7(),
            Kind = request.Kind,
            RecordingId = request.RecordingId,
            ResourceClass = handler.ResourceClass,
            Priority = request.Priority,
            DependsOn = request.DependsOn,
            PipelineId = request.PipelineId,
            Payload = request.Payload,
            MaxAttempts = Math.Max(1, request.MaxAttempts),
            CreatedAt = now,
            UpdatedAt = now,
            WaitReason = request.DependsOn is null ? JobWaitReason.None : JobWaitReason.Dependency,
        };
        await _store.AddAsync(job, cancellationToken).ConfigureAwait(false);
        Raise(job);
        Signal();
        return job;
    }

    public Task<IReadOnlyList<JobRecord>> ListAsync(DateTimeOffset? terminalSince = null, CancellationToken cancellationToken = default) =>
        _store.ListAsync(terminalSince, cancellationToken);

    public Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => _store.GetAsync(id, cancellationToken);

    /// <summary>Cancels a job (stopping it if running) and every job that depends on it.</summary>
    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(id, out var running))
        {
            running.Stop(StopReason.UserCancelled);
            await running.Completion.ConfigureAwait(false);
        }
        else
        {
            var job = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job is not null && !job.IsTerminal)
            {
                await SaveAsync(job with { State = JobState.Cancelled, FinishedAt = _time.GetUtcNow(), WaitReason = JobWaitReason.None })
                    .ConfigureAwait(false);
            }
        }

        await CancelDependentsAsync(id, cancellationToken).ConfigureAwait(false);
        Signal();
    }

    /// <summary>Puts a failed or cancelled job (and its cancelled dependents) back in the queue.</summary>
    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null || job.State is not (JobState.Failed or JobState.Cancelled))
        {
            return;
        }

        await SaveAsync(job with
        {
            State = JobState.Pending,
            Attempts = 0,
            NotBefore = null,
            LastError = null,
            FinishedAt = null,
            WaitReason = JobWaitReason.None,
        }).ConfigureAwait(false);

        foreach (var dependent in await _store.ListDependentsAsync(id, cancellationToken).ConfigureAwait(false))
        {
            if (dependent.State == JobState.Cancelled)
            {
                await RetryAsync(dependent.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        Signal();
    }

    /// <summary>User pause: a running job is stopped at its next cancellation point and keeps its checkpoint.</summary>
    public async Task PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(id, out var running))
        {
            running.Stop(StopReason.UserPaused);
            await running.Completion.ConfigureAwait(false);
            return;
        }

        var job = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is { State: JobState.Pending })
        {
            await SaveAsync(job with { State = JobState.Paused, WaitReason = JobWaitReason.None }).ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is { State: JobState.Paused })
        {
            await SaveAsync(job with { State = JobState.Pending }).ConfigureAwait(false);
            Signal();
        }
    }

    /// <summary>Wakes the scheduler (new job, resource change).</summary>
    public void Signal() => _signal.Release();

    /// <summary>One scheduling pass. Public for deterministic tests; the background loop calls it continuously.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>Waits until no job is running (tests, shutdown).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Values.Select(r => r.Completion));

    public override void Dispose()
    {
        _signal.Dispose();
        _tickGate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
                await _signal.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The scheduler loop must survive any single failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTickFailed(ex);
                await Task.Delay(PollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }

        // App shutdown: stop running jobs; they stay resumable (Pending) for the next start.
        foreach (var running in _running.Values)
        {
            running.Stop(StopReason.Shutdown);
        }

        await WhenIdleAsync().ConfigureAwait(false);
    }

    /// <summary>Jobs left Running by a crash/kill go back to Pending; their checkpoints let them resume.</summary>
    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var job in await _store.ListActiveAsync(cancellationToken).ConfigureAwait(false))
            {
                if (job.State == JobState.Running && !_running.ContainsKey(job.Id))
                {
                    await SaveAsync(job with { State = JobState.Pending, ProgressText = null, WaitReason = JobWaitReason.None })
                        .ConfigureAwait(false);
                    LogRecovered(job.Id, job.Kind);
                }
            }
        }
        finally
        {
            _recovered.TrySetResult();
        }
    }

    private async Task TickCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = _resources.Current;
        var mode = _settings.Current.Processing.Mode;

        // 1. Preempt running jobs whose resource class is no longer allowed.
        foreach (var running in _running.Values)
        {
            var reason = ResourcePolicy.Evaluate(running.Job.ResourceClass, snapshot, mode);
            if (reason != JobWaitReason.None && ResourcePolicy.Preempts(reason))
            {
                running.Stop(StopReason.Deferred, reason);
            }
        }

        // 2. Start what may start, in priority order; record why the rest waits.
        var now = _time.GetUtcNow();
        var active = await _store.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var byId = active.ToDictionary(j => j.Id);
        var candidates = active
            .Where(j => j.State == JobState.Pending && !_running.ContainsKey(j.Id))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ToList();

        foreach (var job in candidates)
        {
            var wait = await WaitReasonAsync(job, byId, snapshot, mode, now, cancellationToken).ConfigureAwait(false);
            if (wait is null)
            {
                continue; // resolved terminally (e.g. missing handler)
            }

            if (wait != JobWaitReason.None)
            {
                if (wait != job.WaitReason)
                {
                    await SaveAsync(job with { WaitReason = wait.Value }).ConfigureAwait(false);
                }

                continue;
            }

            Start(job, ResourcePolicy.Budget(snapshot, mode, Environment.ProcessorCount));
        }
    }

    /// <summary>Job kinds of removed features (speaker diarization, ADR-037).</summary>
    internal static readonly IReadOnlySet<string> RetiredKinds = new HashSet<string>(StringComparer.Ordinal) { "diarize" };

    /// <returns>None = may start now; null = job was finalized here.</returns>
    private async Task<JobWaitReason?> WaitReasonAsync(
        JobRecord job, Dictionary<Guid, JobRecord> active, ResourceSnapshot snapshot, ProcessingMode mode, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (RetiredKinds.Contains(job.Kind))
        {
            // A stage removed from Kakitome (still queued from an older version): skipped, so later stages run.
            await SaveAsync(job with { State = JobState.Succeeded, FinishedAt = now, WaitReason = JobWaitReason.None }).ConfigureAwait(false);
            return null;
        }

        if (!_handlers.ContainsKey(job.Kind))
        {
            await SaveAsync(job with
            {
                State = JobState.Failed,
                FinishedAt = now,
                LastError = $"This version of Kakitome cannot run '{job.Kind}' jobs.",
            }).ConfigureAwait(false);
            return null;
        }

        if (job.DependsOn is { } dependencyId)
        {
            var dependency = active.GetValueOrDefault(dependencyId)
                ?? await _store.GetAsync(dependencyId, cancellationToken).ConfigureAwait(false);
            if (dependency is { State: JobState.Cancelled })
            {
                await SaveAsync(job with { State = JobState.Cancelled, FinishedAt = now, WaitReason = JobWaitReason.None }).ConfigureAwait(false);
                return null;
            }

            if (dependency is not null && dependency.State != JobState.Succeeded)
            {
                return JobWaitReason.Dependency;
            }
        }

        if (job.NotBefore is { } notBefore && notBefore > now)
        {
            return job.WaitReason is JobWaitReason.None or JobWaitReason.Dependency ? JobWaitReason.RetryBackoff : job.WaitReason;
        }

        var resource = ResourcePolicy.Evaluate(job.ResourceClass, snapshot, mode);
        if (resource != JobWaitReason.None)
        {
            return resource;
        }

        var limit = job.ResourceClass == JobResourceClass.Heavy ? MaxHeavyConcurrency : MaxLightConcurrency;
        return _running.Values.Count(r => r.Job.ResourceClass == job.ResourceClass) >= limit
            ? JobWaitReason.ConcurrencyLimit
            : JobWaitReason.None;
    }

    private void Start(JobRecord job, ResourceBudget budget)
    {
        var started = job with
        {
            State = JobState.Running,
            StartedAt = _time.GetUtcNow(),
            WaitReason = JobWaitReason.None,
            LastError = job.LastError,
        };
        var running = new RunningJob(started);
        _running[job.Id] = running;
        running.Completion = Task.Run(async () =>
        {
            try
            {
                // Persist "Running" before any work, so a crash during the job is detectable at next start.
                await SaveAsync(started).ConfigureAwait(false);
                await RunAsync(running, budget).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(job.Id, out _);
                running.Dispose();
                Signal();
            }
        });
    }

    private async Task RunAsync(RunningJob running, ResourceBudget budget)
    {
        var handler = _handlers[running.Job.Kind];
        var context = new JobContext(running.Job, budget, async updated =>
        {
            running.Job = updated with { UpdatedAt = _time.GetUtcNow() };
            await SaveAsync(running.Job).ConfigureAwait(false);
        });

        LogStarting(running.Job.Id, running.Job.Kind);
        try
        {
            await handler.ExecuteAsync(context, running.Token).ConfigureAwait(false);
            await SaveAsync(context.Job with
            {
                State = JobState.Succeeded,
                Progress = 1,
                FinishedAt = _time.GetUtcNow(),
                WaitReason = JobWaitReason.None,
                LastError = null,
            }).ConfigureAwait(false);
            LogSucceeded(running.Job.Id, running.Job.Kind);
            Signal(); // dependents may now run
        }
        catch (OperationCanceledException) when (running.Token.IsCancellationRequested)
        {
            await OnStoppedAsync(running, context.Job).ConfigureAwait(false);
        }
        catch (PermanentJobException ex)
        {
            await FailAsync(context.Job, ex, retry: false).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Handler failures become job state; they never crash the scheduler.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await FailAsync(context.Job, ex, retry: true).ConfigureAwait(false);
        }
    }

    private async Task OnStoppedAsync(RunningJob running, JobRecord job)
    {
        var now = _time.GetUtcNow();
        switch (running.StopReason)
        {
            case StopReason.UserCancelled:
                await SaveAsync(job with { State = JobState.Cancelled, FinishedAt = now, WaitReason = JobWaitReason.None }).ConfigureAwait(false);
                break;
            case StopReason.UserPaused:
                await SaveAsync(job with { State = JobState.Paused, WaitReason = JobWaitReason.None }).ConfigureAwait(false);
                break;
            case StopReason.Deferred:
                // Not a failure: the attempt is not counted; the checkpoint lets it resume.
                await SaveAsync(job with { State = JobState.Pending, NotBefore = now + DeferralCooldown, WaitReason = running.DeferReason })
                    .ConfigureAwait(false);
                LogDeferred(job.Id, job.Kind, running.DeferReason);
                break;
            default:
                await SaveAsync(job with { State = JobState.Pending, WaitReason = JobWaitReason.None }).ConfigureAwait(false);
                break;
        }
    }

    private async Task FailAsync(JobRecord job, Exception ex, bool retry)
    {
        var attempts = job.Attempts + 1;
        var now = _time.GetUtcNow();
        var message = ex.Message;
        if (retry && attempts < job.MaxAttempts)
        {
            var delay = Backoff(attempts);
            await SaveAsync(job with
            {
                State = JobState.Pending,
                Attempts = attempts,
                NotBefore = now + delay,
                LastError = message,
                WaitReason = JobWaitReason.RetryBackoff,
            }).ConfigureAwait(false);
            LogRetrying(ex, job.Id, job.Kind, attempts, delay);
        }
        else
        {
            await SaveAsync(job with { State = JobState.Failed, Attempts = attempts, FinishedAt = now, LastError = message, WaitReason = JobWaitReason.None })
                .ConfigureAwait(false);
            LogFailed(ex, job.Id, job.Kind);
        }
    }

    /// <summary>30 s, 2 min, 8 min, … capped at 1 h.</summary>
    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(4, Math.Max(0, attempt - 1))));

    private async Task CancelDependentsAsync(Guid id, CancellationToken cancellationToken)
    {
        foreach (var dependent in await _store.ListDependentsAsync(id, cancellationToken).ConfigureAwait(false))
        {
            if (!dependent.IsTerminal)
            {
                await CancelAsync(dependent.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SaveAsync(JobRecord job)
    {
        var stamped = job with { UpdatedAt = _time.GetUtcNow() };
        await _store.UpdateAsync(stamped).ConfigureAwait(false);
        Raise(stamped);
    }

    private void Raise(JobRecord job) => JobChanged?.Invoke(this, new JobChangedEventArgs(job));

    [LoggerMessage(Level = LogLevel.Error, Message = "Job scheduling pass failed")]
    private partial void LogTickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered interrupted job {Id} ({Kind})")]
    private partial void LogRecovered(Guid id, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting job {Id} ({Kind})")]
    private partial void LogStarting(Guid id, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Id} ({Kind}) succeeded")]
    private partial void LogSucceeded(Guid id, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Id} ({Kind}) deferred: {Reason}")]
    private partial void LogDeferred(Guid id, string kind, JobWaitReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Id} ({Kind}) attempt {Attempt} failed; retrying in {Delay}")]
    private partial void LogRetrying(Exception ex, Guid id, string kind, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Id} ({Kind}) failed")]
    private partial void LogFailed(Exception ex, Guid id, string kind);

    private enum StopReason
    {
        None,
        UserCancelled,
        UserPaused,
        Deferred,
        Shutdown,
    }

    private sealed class RunningJob(JobRecord job) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public JobRecord Job { get; set; } = job;

        public CancellationToken Token => _cts.Token;

        public Task Completion { get; set; } = Task.CompletedTask;

        public StopReason StopReason { get; private set; }

        public JobWaitReason DeferReason { get; private set; }

        public void Stop(StopReason reason, JobWaitReason deferReason = JobWaitReason.None)
        {
            if (StopReason != StopReason.None)
            {
                return;
            }

            StopReason = reason;
            DeferReason = deferReason;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished concurrently; nothing to stop.
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}

/// <summary>A failure that retrying cannot fix (corrupt input, unsupported format). Fails the job immediately.</summary>
public sealed class PermanentJobException : Exception
{
    public PermanentJobException()
    {
    }

    public PermanentJobException(string message)
        : base(message)
    {
    }

    public PermanentJobException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
