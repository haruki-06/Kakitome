using System.Text.Json;
using Kakitome.Application.Audio;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;

namespace Kakitome.Application.Import;

/// <summary>Downloads the media behind a URL (docs/01 "supported media URL input"). Implemented with yt-dlp.</summary>
public interface IMediaUrlDownloader
{
    /// <summary>True when a downloader executable is available (managed install or user-selected).</summary>
    bool IsAvailable { get; }

    /// <summary>Downloads a single media file into <paramref name="workDirectory"/>.</summary>
    /// <exception cref="MediaDownloadException">The download failed.</exception>
    Task<DownloadedMedia> DownloadAsync(Uri url, string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed record DownloadedMedia(string FilePath, string? Title);

/// <summary>Default when no downloader is wired (tests, headless tools): URL import reports itself unavailable.</summary>
internal sealed class UnavailableMediaUrlDownloader : IMediaUrlDownloader
{
    public bool IsAvailable => false;

    public Task<DownloadedMedia> DownloadAsync(Uri url, string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken) =>
        throw new MediaDownloadException("No media downloader is available.", isPermanent: true);
}

/// <summary>A failed URL download. <see cref="IsPermanent"/> = retrying will not help (unsupported URL, private video…).</summary>
public sealed class MediaDownloadException : Exception
{
    public MediaDownloadException()
    {
    }

    public MediaDownloadException(string message)
        : base(message)
    {
    }

    public MediaDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MediaDownloadException(string message, bool isPermanent)
        : base(message) => IsPermanent = isPermanent;

    public bool IsPermanent { get; }
}

/// <summary>Validation of user-entered media URLs before anything is created or any process is started.</summary>
public static class MediaUrl
{
    private const int MaxLength = 2048;

    /// <summary>Accepts absolute http(s) URLs with a host and without embedded credentials.</summary>
    public static bool TryParse(string? input, out Uri url)
    {
        url = null!;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(char.IsControl) || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        url = parsed;
        return true;
    }
}

/// <summary>
/// <c>import.url</c>: downloads the media to a cache work folder, moves it into the recording folder as
/// <c>audio.&lt;ext&gt;</c>, then finalizes like a file import. The pipeline starts when this job succeeds.
/// </summary>
public sealed class ImportUrlJobHandler(
    LibraryService library,
    IAudioSampleReaderFactory readers,
    IMediaUrlDownloader downloader,
    ImportWorkspace workspace) : IJobHandler
{
    public const string JobKind = "import.url";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        var payload = context.Job.Payload is { } p ? JsonSerializer.Deserialize<Payload>(p) : null;
        if (!MediaUrl.TryParse(payload?.Url, out var url))
        {
            throw new PermanentJobException("The URL is not valid.");
        }

        if (!downloader.IsAvailable)
        {
            throw new PermanentJobException("URL import needs yt-dlp. Install it or choose its location in Settings.");
        }

        var work = workspace.CreateFor(context.Job.Id);
        try
        {
            var last = -1.0;
            var progress = new Progress<double>(v =>
            {
                if (v - last >= 0.02 || v >= 1)
                {
                    last = v;
                    _ = context.ReportProgressAsync(v * 0.95);
                }
            });

            DownloadedMedia media;
            try
            {
                media = await downloader.DownloadAsync(url, work, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (MediaDownloadException ex) when (ex.IsPermanent)
            {
                throw new PermanentJobException(ex.Message, ex);
            }
            catch (MediaDownloadException ex)
            {
                throw new TransientJobException(ex.Message, ex);
            }

            var extension = Path.GetExtension(media.FilePath);
            if (!ImportService.IsSupported(media.FilePath))
            {
                throw new PermanentJobException($"The downloaded '{extension}' media is not supported.");
            }

            var folder = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
            var fileName = LibraryLayout.AudioFileName(extension);
            var temp = Path.Combine(folder, LibraryLayout.TempFilePrefix + fileName);
            File.Copy(media.FilePath, temp, overwrite: true);
            File.Move(temp, Path.Combine(folder, fileName), overwrite: true);

            await ImportFileJobHandler.FinalizeAsync(library, readers, id, fileName, cancellationToken).ConfigureAwait(false);
            if (media.Title is { Length: > 0 } title)
            {
                await library.UpdateMetadataAsync(id, m =>
                {
                    // Only replace the placeholder title (the URL host); never a title the user has typed meanwhile.
                    if (m.Title == payload!.PlaceholderTitle)
                    {
                        m.Title = CleanTitle(title);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }

            await context.SetEngineAsync("yt-dlp").ConfigureAwait(false);
        }
        finally
        {
            workspace.Delete(work);
        }
    }

    public sealed record Payload(string Url, string PlaceholderTitle);

    /// <summary>Titles come from the remote site: strip control characters and bound the length.</summary>
    internal static string CleanTitle(string title)
    {
        var clean = new string(title.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 200 ? clean : clean[..200].TrimEnd();
    }
}

/// <summary>Per-job scratch folders for downloads under the app cache (never inside the Library).</summary>
public sealed class ImportWorkspace(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string CreateFor(Guid jobId)
    {
        var dir = Path.Combine(Root, "url-" + jobId.ToString("N"));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Delete(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
        {
            try
            {
                Directory.Delete(full, recursive: true);
            }
            catch (IOException)
            {
                // Cache only; cleaned by maintenance later.
            }
        }
    }
}
