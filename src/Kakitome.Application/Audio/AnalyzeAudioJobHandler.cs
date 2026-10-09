using System.Text.Json;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;

namespace Kakitome.Application.Audio;

/// <summary>
/// <c>audio.analyze</c>: measures level/noise/speech activity of every audio stream of a recording and records it in
/// <c>metadata.json</c>. Read-only on audio; resumable per stream.
/// </summary>
public sealed class AnalyzeAudioJobHandler(LibraryService library, IAudioSampleReaderFactory readers) : IJobHandler
{
    public const string JobKind = "audio.analyze";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");

        var metadata = await library.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        var folder = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
        var done = context.Job.Checkpoint is { } cp ? JsonSerializer.Deserialize<List<string>>(cp) ?? [] : [];
        var streams = metadata.Audio.Where(a => File.Exists(Path.Combine(folder, a.FileName))).ToList();

        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            if (done.Contains(stream.FileName))
            {
                continue;
            }

            await context.ReportProgressAsync((double)i / streams.Count, stream.FileName).ConfigureAwait(false);
            var path = Path.Combine(folder, stream.FileName);
            Domain.Library.AudioAnalysis analysis;
            try
            {
                using var reader = readers.Open(path);
                analysis = await Task.Run(() => AudioAnalyzer.Analyze(reader, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                throw new PermanentJobException($"{stream.FileName} could not be read: {ex.Message}", ex);
            }

            await library.UpdateMetadataAsync(id, m =>
            {
                var target = m.Audio.FirstOrDefault(a => a.FileName == stream.FileName);
                if (target is not null)
                {
                    target.Analysis = analysis;
                }
            }, cancellationToken).ConfigureAwait(false);

            done.Add(stream.FileName);
            await context.SaveCheckpointAsync(JsonSerializer.Serialize(done)).ConfigureAwait(false);
        }

        await context.SetEngineAsync("Kakitome level analysis (local)").ConfigureAwait(false);
    }
}
