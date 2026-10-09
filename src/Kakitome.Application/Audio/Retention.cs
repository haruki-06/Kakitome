using System.Text.Json;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;

namespace Kakitome.Application.Audio;

/// <summary>Recording retention settings (docs/03 "Retention"). The default keeps the lossless capture.</summary>
public static class RetentionPolicy
{
    public const string Raw = "raw";
    public const string M4a = "m4a";
    public const string Mp3 = "mp3";

    /// <summary>Explicit opt-in: audio is removed once every processing step succeeded; metadata records it.</summary>
    public const string DeleteAfterProcessing = "deleteAfterProcessing";

    public static readonly IReadOnlyList<string> All = [Raw, M4a, Mp3, DeleteAfterProcessing];

    public static bool IsValid(string? policy) => policy is not null && All.Contains(policy);
}

/// <summary>Default when no encoder is wired (tests, headless tools).</summary>
internal sealed class UnavailableAudioEncoder : IAudioEncoder
{
    public Task EncodeAsync(string sourcePath, string targetPath, EncodedAudioFormat format, CancellationToken cancellationToken) =>
        throw new PermanentJobException("No audio encoder is available.");
}

public enum EncodedAudioFormat
{
    M4a,
    Mp3,
}

/// <summary>Encodes a captured WAV into a compressed retained format (Windows Media Foundation in the app).</summary>
public interface IAudioEncoder
{
    Task EncodeAsync(string sourcePath, string targetPath, EncodedAudioFormat format, CancellationToken cancellationToken);
}

/// <summary>
/// <c>audio.retain</c>: applies the retention setting to a finished recording. It never acts before every processing
/// step has succeeded (the canonical input stays until then), converts only after the encoded copy has been verified,
/// and records what happened in <c>metadata.json</c> so the Library stays self-describing.
/// </summary>
public sealed class RetentionJobHandler(LibraryService library, IAudioEncoder encoder, IAudioSampleReaderFactory readers, TimeProvider time) : IJobHandler
{
    public const string JobKind = "audio.retain";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Heavy;

    public static string PayloadFor(string policy) => JsonSerializer.Serialize(new Payload(policy));

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        var policy = context.Job.Payload is { } p ? JsonSerializer.Deserialize<Payload>(p)?.Policy : null;
        if (!RetentionPolicy.IsValid(policy))
        {
            throw new PermanentJobException("Unknown retention setting.");
        }

        var metadata = await library.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        if (policy == RetentionPolicy.Raw || !AppliesTo(metadata))
        {
            await context.SetEngineAsync("kept").ConfigureAwait(false);
            return;
        }

        var folder = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
        if (policy == RetentionPolicy.DeleteAfterProcessing)
        {
            await RemoveAudioAsync(id, folder, metadata, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ConvertAsync(id, folder, metadata, policy == RetentionPolicy.M4a ? EncodedAudioFormat.M4a : EncodedAudioFormat.Mp3, context, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Recordings only (imports keep the user's file as imported), with audio, after every step succeeded.</summary>
    internal static bool AppliesTo(RecordingMetadata metadata) =>
        metadata.SourceType == RecordingSourceType.Recording
        && metadata.AudioRemoval is null
        && metadata.Audio.Count > 0
        && metadata.Processing.Count > 0
        && metadata.Processing.All(s => s.Status is ProcessingStepStatus.Succeeded or ProcessingStepStatus.Skipped);

    private async Task ConvertAsync(
        RecordingId id, string folder, RecordingMetadata metadata, EncodedAudioFormat format, JobContext context, CancellationToken cancellationToken)
    {
        var extension = format == EncodedAudioFormat.M4a ? ".m4a" : ".mp3";
        var streams = metadata.Audio.Where(s => string.Equals(Path.GetExtension(s.FileName), ".wav", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            var source = Path.Combine(folder, stream.FileName);
            var targetName = Path.ChangeExtension(stream.FileName, extension);
            var target = Path.Combine(folder, targetName);
            if (!File.Exists(source))
            {
                continue;
            }

            var temp = Path.Combine(folder, LibraryLayout.TempFilePrefix + targetName);
            await encoder.EncodeAsync(source, temp, format, cancellationToken).ConfigureAwait(false);
            Verify(source, temp);
            File.Move(temp, target, overwrite: true);

            // Metadata points at the verified copy before the capture is removed, so a crash in between leaves both.
            var retained = extension.TrimStart('.');
            await library.UpdateMetadataAsync(id, m =>
            {
                var entry = m.Audio.First(a => a.FileName == stream.FileName);
                entry.FileName = targetName;
                entry.Format = retained;
                m.RetainedAudioFormat = retained;
            }, cancellationToken).ConfigureAwait(false);
            File.Delete(source);
            await context.ReportProgressAsync((i + 1) / (double)streams.Count).ConfigureAwait(false);
        }

        await context.SetEngineAsync("Windows Media Foundation " + extension.TrimStart('.').ToUpperInvariant()).ConfigureAwait(false);
    }

    /// <summary>The encoded copy must decode and last as long as the capture (within 1 % or 0.5 s).</summary>
    private void Verify(string source, string encoded)
    {
        double Duration(string path)
        {
            using var reader = readers.Open(path);
            return reader.TotalFrames / (double)reader.Format.SampleRate;
        }

        var expected = Duration(source);
        double actual;
        try
        {
            actual = Duration(encoded);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            File.Delete(encoded);
            throw new TransientJobException($"The converted audio could not be read back: {ex.Message}", ex);
        }

        if (Math.Abs(actual - expected) > Math.Max(0.5, expected * 0.01))
        {
            File.Delete(encoded);
            throw new TransientJobException($"The converted audio is {actual:F1} s long but the recording is {expected:F1} s; the original was kept.");
        }
    }

    private async Task RemoveAudioAsync(RecordingId id, string folder, RecordingMetadata metadata, CancellationToken cancellationToken)
    {
        var files = metadata.Audio.Select(a => a.FileName).ToList();

        // Record the removal first: if the app stops halfway, the Library already says why audio is missing.
        await library.UpdateMetadataAsync(id, m =>
        {
            m.AudioRemoval = new AudioRemovalInfo
            {
                RemovedAt = time.GetLocalNow(),
                Setting = RetentionPolicy.DeleteAfterProcessing,
                Reason = "Removed after all processing succeeded, as chosen in Settings.",
                RemovedFiles = files,
            };
            m.Audio = [];
        }, cancellationToken).ConfigureAwait(false);

        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record Payload(string Policy);
}
