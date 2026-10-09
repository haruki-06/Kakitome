using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Settings;
using Microsoft.Extensions.Logging;

namespace Kakitome.Application.Models;

/// <summary>Whether the current internet connection is metered (mobile data, tethering): large downloads wait.</summary>
public interface INetworkCostProbe
{
    bool IsMetered { get; }
}

/// <summary>
/// Downloads what Kakitome needs when it is missing (ADR-034, user request): the speech recognition model recommended
/// for this PC when no ASR model is installed, and yt-dlp when neither the managed copy nor a user-selected exe is
/// available, and the recommended summary model when none is installed, the PC has enough memory and summaries are not
/// set to extraction only. Runs at startup as ordinary <c>model.install</c> jobs (visible in the queue, resumable,
/// cancelable, notified). Not on a metered connection; turned off with <see cref="ProcessingSettings.AutoDownloadModels"/>.
/// </summary>
public sealed partial class AutoModelInstaller(
    IModelStore store,
    IAccelerationProbe acceleration,
    INetworkCostProbe network,
    JobScheduler scheduler,
    ISettingsStore settings,
    ILogger<AutoModelInstaller> logger)
{
    /// <summary>What would be downloaded now (empty when nothing is missing or the feature is off).</summary>
    public IReadOnlyList<string> MissingDownloads()
    {
        var processing = settings.Current.Processing;
        if (!processing.AutoDownloadModels)
        {
            return [];
        }

        var missing = new List<string>();
        if (!ModelCatalog.All.Any(m => store.GetState(m.Id) == ModelState.Installed))
        {
            missing.Add(AsrDefaults.RecommendedModel(acceleration.HasCapableGpu));
        }

        if (store.GetState(ModelCatalog.YtDlp) != ModelState.Installed && string.IsNullOrWhiteSpace(processing.YtDlpPath))
        {
            missing.Add(ModelCatalog.YtDlp);
        }

        if (processing.SummaryEngine != SummaryEngines.Extractive
            && !ModelCatalog.SummaryModels.Any(m => store.GetState(m.Id) == ModelState.Installed)
            && SummaryDefaults.RecommendedModel(acceleration.TotalMemoryBytes) is { } summary)
        {
            missing.Add(summary);
        }

        return missing;
    }

    /// <summary>Queues the missing downloads (once: a download already queued or running is not queued again).</summary>
    public async Task<IReadOnlyList<string>> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var missing = MissingDownloads();
        if (missing.Count == 0)
        {
            return [];
        }

        if (network.IsMetered)
        {
            LogMetered(missing);
            return [];
        }

        var queued = (await scheduler.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(j => j.Kind == ModelInstallJobHandler.JobKind && !j.IsTerminal)
            .Select(j => j.Payload)
            .ToHashSet(StringComparer.Ordinal);
        var started = new List<string>();
        foreach (var id in missing)
        {
            var payload = ModelInstallJobHandler.PayloadFor(id);
            if (queued.Contains(payload))
            {
                continue;
            }

            await scheduler.EnqueueAsync(new JobRequest(ModelInstallJobHandler.JobKind)
            {
                Payload = payload,
                Priority = 20,
                MaxAttempts = 5,
            }, cancellationToken).ConfigureAwait(false);
            started.Add(id);
        }

        if (started.Count > 0)
        {
            LogQueued(started);
        }

        return started;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading missing models/tools automatically: {Ids}")]
    private partial void LogQueued(IReadOnlyList<string> ids);

    [LoggerMessage(Level = LogLevel.Information, Message = "Missing models/tools not downloaded on a metered connection: {Ids}")]
    private partial void LogMetered(IReadOnlyList<string> ids);
}
