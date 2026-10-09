using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Kakitome.Application.Import;
using Kakitome.Application.Models;
using Kakitome.Application.Settings;

namespace Kakitome.Infrastructure.Import;

/// <summary>
/// URL import through yt-dlp, started with an argument list (never a shell string, docs/07). The user's yt-dlp config
/// files are ignored so they cannot inject options such as <c>--exec</c>; the URL follows <c>--</c> so it can never be
/// parsed as an option. Only single-file formats are requested because Kakitome does not ship FFmpeg (ADR-024):
/// Windows Media Foundation decodes the result.
/// </summary>
public sealed partial class YtDlpDownloader(ISettingsStore settings, IModelStore models, ILogger<YtDlpDownloader> logger) : IMediaUrlDownloader
{
    /// <summary>Catalog id of the managed, hash-pinned yt-dlp install (installed through the Model Manager).</summary>
    public const string ManagedToolId = ModelCatalog.YtDlp;

    internal const string FileMarker = "KAKITOME_FILE ";
    internal const string TitleMarker = "KAKITOME_TITLE ";
    internal const string ProgressMarker = "KAKITOME_PROGRESS ";

    /// <summary>Audio-only single files first (small, decodable by Media Foundation); never formats that need merging.</summary>
    internal const string FormatSelector = "bestaudio[ext=m4a]/bestaudio[ext=mp3]/best[ext=mp4][acodec!=none]/bestaudio/best[acodec!=none]";

    public bool IsAvailable => ResolveExecutable() is not null;

    public async Task<DownloadedMedia> DownloadAsync(Uri url, string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var exe = ResolveExecutable() ?? throw new MediaDownloadException("yt-dlp is not available.", isPermanent: true);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDirectory,
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        foreach (var argument in BuildArguments(url, workDirectory))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        string? file = null;
        string? title = null;
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            Handle(e.Data);
            if (e.Data is { } line && line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                lock (errors)
                {
                    errors.AppendLine(line);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MediaDownloadException($"yt-dlp could not be started: {ex.Message}", isPermanent: true);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        process.WaitForExit(); // flush redirected output
        var error = errors.ToString().Trim();
        if (process.ExitCode != 0)
        {
            LogFailed(process.ExitCode);
            throw new MediaDownloadException(
                error.Length > 0 ? error : $"yt-dlp exited with code {process.ExitCode}.",
                IsPermanentError(error));
        }

        file = file is null ? null : Path.GetFullPath(file);
        var root = Path.GetFullPath(workDirectory) + Path.DirectorySeparatorChar;
        if (file is null || !file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            throw new MediaDownloadException("yt-dlp did not produce a media file.", isPermanent: false);
        }

        progress?.Report(1);
        return new DownloadedMedia(file, title);

        void Handle(string? line)
        {
            switch (ParseLine(line))
            {
                case (LineKind.File, var value):
                    file = value;
                    break;
                case (LineKind.Title, var value):
                    title = value;
                    break;
                case (LineKind.Progress, var value) when TryParseProgress(value, out var fraction):
                    progress?.Report(fraction);
                    break;
            }
        }
    }

    internal static IReadOnlyList<string> BuildArguments(Uri url, string workDirectory) =>
    [
        "--ignore-config",

        // The frozen yt-dlp.exe ignores PYTHONIOENCODING/PYTHONUTF8 and writes to a pipe in the ANSI code page
        // (cp932 on Japanese Windows), which garbled non-ASCII titles and paths. Only this option makes it UTF-8.
        "--encoding", "utf-8",
        "--no-playlist",
        "--no-mtime",
        "--no-part",
        "--newline",
        "--no-simulate",
        "--progress",
        "--progress-template", "download:" + ProgressMarker + "%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        "--print", "after_move:" + TitleMarker + "%(title)s",
        "--print", "after_move:" + FileMarker + "%(filepath)s",
        "-f", FormatSelector,
        "-P", workDirectory,
        "-o", "media.%(ext)s",
        "--",
        url.AbsoluteUri,
    ];

    internal enum LineKind
    {
        None,
        File,
        Title,
        Progress,
    }

    internal static (LineKind Kind, string Value) ParseLine(string? line)
    {
        if (line is null)
        {
            return (LineKind.None, string.Empty);
        }

        if (line.StartsWith(FileMarker, StringComparison.Ordinal))
        {
            return (LineKind.File, line[FileMarker.Length..].Trim());
        }

        if (line.StartsWith(TitleMarker, StringComparison.Ordinal))
        {
            return (LineKind.Title, line[TitleMarker.Length..].Trim());
        }

        return line.StartsWith(ProgressMarker, StringComparison.Ordinal)
            ? (LineKind.Progress, line[ProgressMarker.Length..].Trim())
            : (LineKind.None, string.Empty);
    }

    /// <summary>Parses "downloaded total estimate" (yt-dlp prints NA for unknown values).</summary>
    internal static bool TryParseProgress(string value, out double fraction)
    {
        fraction = 0;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var done))
        {
            return false;
        }

        var total = parts.Skip(1)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0)
            .FirstOrDefault(t => t > 0);
        if (total <= 0)
        {
            return false;
        }

        fraction = Math.Clamp(done / total, 0, 1);
        return true;
    }

    /// <summary>Errors that a retry cannot fix (the scheduler retries everything else with backoff).</summary>
    internal static bool IsPermanentError(string error) =>
        error.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)
        || error.Contains("is not a valid URL", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Private video", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
        || error.Contains("members-only", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
        || error.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase)
        || error.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
        || error.Contains("DRM", StringComparison.Ordinal);

    /// <summary>The managed install wins; otherwise a user-selected executable (validated, never searched on PATH).</summary>
    internal string? ResolveExecutable()
    {
        if (models.GetState(ManagedToolId) == ModelState.Installed)
        {
            var managed = Path.Combine(models.GetDirectory(ManagedToolId), "yt-dlp.exe");
            if (File.Exists(managed))
            {
                return managed;
            }
        }

        return IsUsableExecutable(settings.Current.Processing.YtDlpPath) ? Path.GetFullPath(settings.Current.Processing.YtDlpPath!) : null;
    }

    internal static bool IsUsableExecutable(string? path) =>
        path is { Length: > 0 }
        && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    // The error text can contain the URL/media id, so it goes to the job (shown to the user), not to the log.
    [LoggerMessage(Level = LogLevel.Warning, Message = "yt-dlp failed with exit code {ExitCode}")]
    private partial void LogFailed(int exitCode);
}
