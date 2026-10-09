using System.Text.Json;
using Kakitome.Application.Audio;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Import;

/// <summary>
/// File import (drag/drop or picker, docs/01): creates the recording immediately (visible in the Library) and copies the
/// media in a durable job. The user's original file is only read, never moved or modified.
/// </summary>
public sealed class ImportService(LibraryService library, JobScheduler scheduler, TimeProvider time)
{
    /// <summary>Extensions offered by the picker / accepted on drop (decoded via Media Foundation).</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".ogg", ".opus",
    };

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public async Task<RecordingId> ImportFileAsync(string sourcePath, string? project, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("The file to import does not exist.", full);
        }

        if (!IsSupported(full))
        {
            throw new NotSupportedException($"'{Path.GetExtension(full)}' files are not supported.");
        }

        var recordedAt = new DateTimeOffset(File.GetLastWriteTime(full)).ToOffset(time.GetLocalNow().Offset);
        var entry = await library.CreateRecordingAsync(
            new NewRecording
            {
                Title = Path.GetFileNameWithoutExtension(full),
                Project = project,
                SourceType = RecordingSourceType.FileImport,
                RecordedAt = recordedAt,
                Import = new ImportInfo { OriginalFileName = Path.GetFileName(full), ImportedAt = time.GetLocalNow() },
            },
            cancellationToken).ConfigureAwait(false);

        await scheduler.EnqueueAsync(
            new JobRequest(ImportFileJobHandler.JobKind)
            {
                RecordingId = entry.Id,
                Payload = JsonSerializer.Serialize(new ImportFileJobHandler.Payload(full)),
                Priority = 5,
            },
            cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }

    /// <summary>
    /// URL import: the recording appears immediately (titled with the host until the download reports the real title)
    /// and the download runs as a durable <c>import.url</c> job.
    /// </summary>
    public async Task<RecordingId> ImportUrlAsync(string url, string? project, CancellationToken cancellationToken = default)
    {
        if (!MediaUrl.TryParse(url, out var parsed))
        {
            throw new ArgumentException("Enter an http(s) media URL.", nameof(url));
        }

        var placeholder = parsed.Host;
        var entry = await library.CreateRecordingAsync(
            new NewRecording
            {
                Title = placeholder,
                Project = project,
                SourceType = RecordingSourceType.UrlImport,
                RecordedAt = time.GetLocalNow(),
                Import = new ImportInfo { SourceUrl = parsed.AbsoluteUri, ImportedAt = time.GetLocalNow() },
            },
            cancellationToken).ConfigureAwait(false);

        await scheduler.EnqueueAsync(
            new JobRequest(ImportUrlJobHandler.JobKind)
            {
                RecordingId = entry.Id,
                Payload = JsonSerializer.Serialize(new ImportUrlJobHandler.Payload(parsed.AbsoluteUri, placeholder)),
                Priority = 5,
            },
            cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }
}

/// <summary>
/// <c>import.file</c>: copies the source into the recording folder as <c>audio.&lt;ext&gt;</c> (via a temporary name, so a
/// crash never leaves a half file under the canonical name), verifies Windows can decode it and records duration. The
/// processing pipeline starts when this job succeeds.
/// </summary>
public sealed class ImportFileJobHandler(LibraryService library, IAudioSampleReaderFactory readers) : IJobHandler
{
    public const string JobKind = "import.file";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        var payload = context.Job.Payload is { } p ? JsonSerializer.Deserialize<Payload>(p) : null;
        if (payload?.Source is not { Length: > 0 } source || !File.Exists(source))
        {
            throw new PermanentJobException("The original file is no longer available.");
        }

        var folder = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
        var fileName = LibraryLayout.AudioFileName(Path.GetExtension(source));
        var target = Path.Combine(folder, fileName);

        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
        {
            var temp = Path.Combine(folder, LibraryLayout.TempFilePrefix + fileName);
            await CopyAsync(source, temp, context, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }

        await FinalizeAsync(library, readers, id, fileName, cancellationToken).ConfigureAwait(false);

        // ProcessingPipeline queues the stages when this job succeeds (it observes JobChanged).
        await context.SetEngineAsync("Windows Media Foundation").ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies Windows can decode the imported <paramref name="fileName"/> and records its duration/format in metadata.
    /// </summary>
    internal static async Task FinalizeAsync(
        LibraryService library, IAudioSampleReaderFactory readers, RecordingId id, string fileName, CancellationToken cancellationToken)
    {
        var folder = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
        var target = Path.Combine(folder, fileName);
        double duration;
        AudioFormatInfo info;
        try
        {
            using var reader = readers.Open(target);
            duration = reader.TotalFrames / (double)reader.Format.SampleRate;
            info = new AudioFormatInfo(reader.Format.SampleRate, reader.Format.Channels);
            var probe = new float[reader.Format.Channels * 1024];
            if (reader.Read(probe) == 0 && reader.TotalFrames > 0)
            {
                throw new InvalidDataException("No audio could be decoded.");
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
        {
            throw new PermanentJobException($"This file's audio cannot be read: {ex.Message}", ex);
        }

        var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        await library.UpdateMetadataAsync(id, m =>
        {
            m.DurationSeconds = Math.Round(duration, 3);
            m.RetainedAudioFormat = format;
            m.Audio =
            [
                new AudioStreamInfo
                {
                    FileName = fileName,
                    Role = AudioStreamRole.Imported,
                    Format = format,
                    SampleRate = info.SampleRate,
                    Channels = info.Channels,
                    DurationSeconds = Math.Round(duration, 3),
                },
            ];
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyAsync(string source, string target, JobContext context, CancellationToken cancellationToken)
    {
        var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                var lastReport = 0.0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    var progress = input.Length == 0 ? 1 : (double)copied / input.Length;
                    if (progress - lastReport >= 0.05)
                    {
                        lastReport = progress;
                        await context.ReportProgressAsync(progress).ConfigureAwait(false);
                    }
                }

                output.Flush(flushToDisk: true);
            }
        }
    }

    public sealed record Payload(string Source);

    private sealed record AudioFormatInfo(int SampleRate, int Channels);
}
