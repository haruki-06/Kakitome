using Microsoft.Extensions.Logging;
using Kakitome.Application.Audio;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Application.Settings;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Jobs;

/// <summary>
/// Turns a finished recording/import into a chain of durable jobs (one per stage, each depending on the previous)
/// and mirrors each stage's outcome into <c>metadata.json</c> <c>processing[]</c>, so the Library records what ran.
/// Stages without a registered handler in this build are skipped.
/// </summary>
public sealed partial class ProcessingPipeline
{
    /// <summary>Stage order (docs/01 "Processing"): analyze → ASR → speakers (ADR-037) → cleanup → summary → index.</summary>
    public static readonly IReadOnlyList<string> StageOrder = ["audio.analyze", "asr", "cleanup", "summary", "index"];

    private readonly JobScheduler _scheduler;
    private readonly LibraryService _library;
    private readonly IJobStore _jobs;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ProcessingPipeline> _logger;

    public ProcessingPipeline(
        JobScheduler scheduler,
        LibraryService library,
        RecordingService recording,
        IJobStore jobs,
        ISettingsStore settings,
        ILogger<ProcessingPipeline> logger)
    {
        _scheduler = scheduler;
        _library = library;
        _jobs = jobs;
        _settings = settings;
        _logger = logger;
        _scheduler.JobChanged += OnJobChanged;
        recording.RecordingCompleted += (_, id) => _ = EnqueueAutomaticallyAsync(id);
    }

    /// <summary>Queues all available stages for a recording. Returns the pipeline id, or null if nothing to run.</summary>
    public async Task<Guid?> EnqueueAsync(RecordingId id, int priority = 0, CancellationToken cancellationToken = default)
    {
        var stages = StageOrder.Where(_scheduler.HasHandler).ToList();
        if (stages.Count == 0)
        {
            return null;
        }

        // Mark the steps pending first: a fast stage can finish (and be mirrored into metadata.json) before this
        // method returns, and writing "pending" afterwards would overwrite its result.
        await _library.UpdateMetadataAsync(id, metadata =>
        {
            foreach (var stage in stages)
            {
                var step = metadata.Processing.FirstOrDefault(p => p.Stage == stage);
                if (step is null)
                {
                    metadata.Processing.Add(new ProcessingStepInfo { Stage = stage, Status = ProcessingStepStatus.Pending });
                }
                else
                {
                    step.Status = ProcessingStepStatus.Pending;
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        var pipelineId = Guid.CreateVersion7();
        Guid? previous = null;
        foreach (var stage in stages)
        {
            var job = await _scheduler.EnqueueAsync(
                new JobRequest(stage) { RecordingId = id, DependsOn = previous, PipelineId = pipelineId, Priority = priority },
                cancellationToken).ConfigureAwait(false);
            previous = job.Id;
        }

        var stageList = string.Join(" → ", stages);
        LogQueued(id, stageList);
        return pipelineId;
    }

    /// <summary>
    /// Startup catch-up: recordings that finished (or were recovered after a crash) but were never queued — e.g. the
    /// app closed right after Stop — get their pipeline now.
    /// </summary>
    public async Task<int> EnsureQueuedAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.Processing.AutoProcess)
        {
            return 0;
        }

        var withJobs = (await _jobs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(j => j.RecordingId is not null)
            .Select(j => j.RecordingId!.Value)
            .ToHashSet();

        var queued = 0;
        foreach (var entry in await _library.ListRecordingsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (withJobs.Contains(entry.Id) || entry.CaptureStatus is CaptureStatus.InProgress or CaptureStatus.Cancelled || !entry.HasAudio)
            {
                continue;
            }

            var metadata = await _library.GetMetadataAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            if (metadata.Processing.Count == 0)
            {
                await EnqueueAsync(entry.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
                queued++;
            }
        }

        return queued;
    }

    private async Task EnqueueAutomaticallyAsync(RecordingId id)
    {
        if (!_settings.Current.Processing.AutoProcess)
        {
            return;
        }

        try
        {
            await EnqueueAsync(id).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // EnsureQueuedAsync at next startup catches anything missed here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogQueueFailed(ex, id);
        }
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        var job = e.Job;
        if (job.Kind is (Kakitome.Application.Import.ImportFileJobHandler.JobKind or Kakitome.Application.Import.ImportUrlJobHandler.JobKind) && job.State == JobState.Succeeded && job.RecordingId is { } imported)
        {
            _ = EnqueueAutomaticallyAsync(imported);
            return;
        }

        if (job.RecordingId is not { } id || !job.IsTerminal || !StageOrder.Contains(job.Kind))
        {
            return;
        }

        _ = MirrorThenRetainAsync(id, job);
    }

    private async Task MirrorThenRetainAsync(RecordingId id, JobRecord job)
    {
        await MirrorAsync(id, job).ConfigureAwait(false);

        // Only after the outcome is in metadata.json: the retention handler checks every step there.
        if (job.Kind == StageOrder[^1] && job.State == JobState.Succeeded)
        {
            await ApplyRetentionAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>After the last stage succeeded, queue the retention step unless the setting keeps the capture as is.</summary>
    private async Task ApplyRetentionAsync(RecordingId id)
    {
        var policy = _settings.Current.Recording.Retention;
        if (policy == RetentionPolicy.Raw || !RetentionPolicy.IsValid(policy) || !_scheduler.HasHandler(RetentionJobHandler.JobKind))
        {
            return;
        }

        try
        {
            // Mirroring runs concurrently; the handler re-checks that every step succeeded before touching audio.
            await _scheduler.EnqueueAsync(new JobRequest(RetentionJobHandler.JobKind)
            {
                RecordingId = id,
                Payload = RetentionJobHandler.PayloadFor(policy),
                Priority = -5,
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Retention can be applied later; never disturb the pipeline.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogQueueFailed(ex, id);
        }
    }

    private async Task MirrorAsync(RecordingId id, JobRecord job)
    {
        try
        {
            await _library.UpdateMetadataAsync(id, metadata =>
            {
                var step = metadata.Processing.FirstOrDefault(p => p.Stage == job.Kind);
                if (step is null)
                {
                    step = new ProcessingStepInfo { Stage = job.Kind, Status = ProcessingStepStatus.Pending };
                    metadata.Processing.Add(step);
                }

                step.Status = job.State switch
                {
                    JobState.Succeeded => ProcessingStepStatus.Succeeded,
                    JobState.Failed => ProcessingStepStatus.Failed,
                    _ => ProcessingStepStatus.Skipped,
                };
                step.CompletedAt = job.FinishedAt;
                step.Provider = job.Engine ?? step.Provider;
                step.Locality ??= "local";
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The recording may have been removed meanwhile; job state remains authoritative.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMirrorFailed(ex, id, job.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued processing for {Id}: {Stages}")]
    private partial void LogQueued(RecordingId id, string stages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not queue processing for {Id}")]
    private partial void LogQueueFailed(Exception ex, RecordingId id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record {Stage} outcome in metadata for {Id}")]
    private partial void LogMirrorFailed(Exception ex, RecordingId id, string stage);
}
