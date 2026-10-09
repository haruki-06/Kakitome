using Microsoft.Extensions.Logging;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;

namespace Kakitome.Application.Recording;

/// <summary>
/// At startup, finds recordings whose capture never finished (crash, power loss, forced shutdown), repairs their
/// audio files so every captured sample is playable, and marks them <see cref="CaptureStatus.Interrupted"/>.
/// Nothing is deleted.
/// </summary>
public sealed partial class RecordingRecovery(
    LibraryService library,
    IAudioFileRepair repair,
    RecordingService recording,
    TimeProvider time,
    ILogger<RecordingRecovery> logger)
{
    public Task<int> RecoverAsync(CancellationToken cancellationToken = default) =>
        recording.ExcludingStartsAsync(() => RecoverCoreAsync(cancellationToken), cancellationToken);

    private async Task<int> RecoverCoreAsync(CancellationToken cancellationToken)
    {
        var active = recording.Current?.Id;
        var candidates = (await library.ListRecordingsAsync(cancellationToken).ConfigureAwait(false))
            .Where(e => e.CaptureStatus == CaptureStatus.InProgress && e.Id != active)
            .ToList();

        var recovered = 0;
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var folder = await library.GetRecordingPathAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                await library.UpdateMetadataAsync(entry.Id, metadata => Recover(metadata, folder), cancellationToken).ConfigureAwait(false);
                recovered++;
                LogRecovered(entry.Id.ToString(), entry.Folder);
            }
#pragma warning disable CA1031 // One damaged recording must not stop recovery of the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRecoveryFailed(ex, entry.Folder);
            }
        }

        return recovered;
    }

    private void Recover(RecordingMetadata metadata, string folder)
    {
        double duration = 0;
        DateTime? lastWrite = null;
        foreach (var stream in metadata.Audio)
        {
            var path = Path.Combine(folder, stream.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var result = repair.Repair(path);
            stream.DurationSeconds = result.DurationSeconds;
            duration = Math.Max(duration, result.DurationSeconds);
            var write = File.GetLastWriteTimeUtc(path);
            lastWrite = lastWrite is null || write > lastWrite ? write : lastWrite;
        }

        metadata.DurationSeconds = duration;
        metadata.Capture ??= new CaptureInfo { Status = CaptureStatus.InProgress };
        metadata.Capture.Status = CaptureStatus.Interrupted;
        metadata.Capture.EndedAt = lastWrite is { } w ? new DateTimeOffset(w).ToOffset(time.GetLocalNow().Offset) : time.GetLocalNow();
        metadata.Capture.Note = "Kakitome closed unexpectedly during this recording. All audio saved up to that point was kept.";
        metadata.Capture.Events.Add(new CaptureEvent
        {
            At = time.GetLocalNow(),
            OffsetSeconds = duration,
            Kind = CaptureEventKind.Recovered,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered interrupted recording {Id} in {Folder}")]
    private partial void LogRecovered(string id, string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not recover interrupted recording in {Folder}")]
    private partial void LogRecoveryFailed(Exception ex, string folder);
}
