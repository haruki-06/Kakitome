using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Storage.Persistence;

namespace Kakitome.Storage;

/// <summary>
/// Startup work off the UI thread: open (or recover) the database, then rebuild the Library index when it is new
/// or was recovered from corruption, otherwise synchronize it with external changes; finally repair recordings
/// left unfinished by a crash and queue processing for recordings that never got it.
/// </summary>
public sealed partial class LibraryBootstrapper(
    KakitomeDatabase database,
    LibraryService library,
    RecordingRecovery recordingRecovery,
    ProcessingPipeline pipeline,
    IJobStore jobs,
    Kakitome.Application.Search.SearchService search,
    Kakitome.Application.Search.ISearchIndex searchIndex,
    Kakitome.Application.Backup.AutomaticBackupPolicy automaticBackup,
    TimeProvider time,
    ILogger<LibraryBootstrapper> logger) : BackgroundService
{
    private readonly TaskCompletionSource<LibraryScanReport> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the index is usable (or faults if startup failed).</summary>
    public Task<LibraryScanReport> Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var init = await database.InitializeAsync(stoppingToken).ConfigureAwait(false);
            var report = init.NeedsIndexRebuild
                ? await library.RebuildIndexAsync(stoppingToken).ConfigureAwait(false)
                : await library.SynchronizeAsync(stoppingToken).ConfigureAwait(false);
            await recordingRecovery.RecoverAsync(stoppingToken).ConfigureAwait(false);

            // The search index is derived: rebuild it from the Library when the database is new or was recovered.
            if (init.NeedsIndexRebuild || (report.Total > 0 && await searchIndex.CountIndexedRecordingsAsync(stoppingToken).ConfigureAwait(false) == 0))
            {
                await search.RebuildAsync(stoppingToken).ConfigureAwait(false);
            }

            await pipeline.EnsureQueuedAsync(stoppingToken).ConfigureAwait(false);

            // Reading versions (transcript.txt / summary.txt) follow the current renderer, also for older recordings.
            await library.RefreshReadableTextAsync(stoppingToken).ConfigureAwait(false);

            // Job history (app state, no user content) is kept for 30 days.
            await jobs.PurgeFinishedAsync(time.GetUtcNow().AddDays(-30), stoppingToken).ConfigureAwait(false);
            _ready.TrySetResult(report);

            // Automatic backups (opt-in): check now and periodically while Kakitome runs.
            var version = typeof(LibraryBootstrapper).Assembly.GetName().Version?.ToString(3);
            using var timer = new PeriodicTimer(Kakitome.Application.Backup.AutomaticBackupPolicy.CheckInterval, time);
            do
            {
                await automaticBackup.EnqueueIfDueAsync(version, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stoppingToken);
        }
#pragma warning disable CA1031 // Startup failures are surfaced through Ready, not by crashing the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStartupFailed(ex);
            _ready.TrySetException(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Library startup failed")]
    private partial void LogStartupFailed(Exception ex);
}
