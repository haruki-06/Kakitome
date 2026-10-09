using Kakitome.Application.Jobs;
using Kakitome.Application.Models;
using Microsoft.Extensions.Logging;

namespace Kakitome.Application.Asr;

/// <summary>
/// Transcriptions that failed only because no speech recognition model was installed are queued again once one is:
/// on a fresh install the recommended model is still downloading (ADR-034) when the first recording or import is
/// processed, and the user should not have to find and retry those jobs by hand.
/// </summary>
public sealed partial class AsrModelReadyRetry
{
    private readonly JobScheduler _scheduler;
    private readonly IModelStore _models;
    private readonly ILogger<AsrModelReadyRetry> _logger;

    public AsrModelReadyRetry(JobScheduler scheduler, IModelStore models, ILogger<AsrModelReadyRetry> logger)
    {
        _scheduler = scheduler;
        _models = models;
        _logger = logger;
        scheduler.JobChanged += (_, e) =>
        {
            if (e.Job is { Kind: ModelInstallJobHandler.JobKind, State: JobState.Succeeded }
                && ModelInstallJobHandler.ModelIdOf(e.Job.Payload) is { } id
                && ModelCatalog.All.Any(m => m.Id == id))
            {
                _ = RetryAsync();
            }
        };
    }

    /// <summary>Retries the waiting transcriptions when an ASR model is installed (also called at startup).</summary>
    public async Task<int> RetryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ModelCatalog.All.Any(m => _models.GetState(m.Id) == ModelState.Installed))
            {
                return 0;
            }

            var waiting = (await _scheduler.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                .Where(j => j is { Kind: AsrJobHandler.JobKind, State: JobState.Failed }
                    && j.LastError?.StartsWith(AsrJobHandler.NoModelMessage, StringComparison.Ordinal) == true)
                .ToList();
            foreach (var job in waiting)
            {
                await _scheduler.RetryAsync(job.Id, cancellationToken).ConfigureAwait(false);
            }

            if (waiting.Count > 0)
            {
                LogRetried(waiting.Count);
            }

            return waiting.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A speech recognition model is installed; retrying {Count} transcription(s)")]
    private partial void LogRetried(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying transcriptions that waited for a model failed")]
    private partial void LogFailed(Exception ex);
}
