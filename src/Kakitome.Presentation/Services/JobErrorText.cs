using System.Text.RegularExpressions;

namespace Kakitome.Presentation.Services;

/// <summary>
/// Job errors are stored in English (logs, metadata.json); the queue and Settings show them in the UI language. Known
/// messages map to resource strings (<c>JobError_*</c>, with the variable parts carried over); anything else — e.g. a
/// message from yt-dlp or Windows — is shown as is.
/// </summary>
public static partial class JobErrorText
{
    private static readonly (Regex Pattern, string Key)[] Known =
    [
        (NoAsrModel(), "JobError_NoAsrModel"),
        (EditedOutside(), "JobError_EditedOutside"),
        (AudioUnreadable(), "JobError_AudioUnreadable"),
        (ImportUnreadable(), "JobError_AudioUnreadable"),
        (NoTranscript(), "JobError_NoTranscript"),
        (SourceMissing(), "JobError_SourceMissing"),
        (BadUrl(), "JobError_BadUrl"),
        (NoYtDlp(), "JobError_NoYtDlp"),
        (UnsupportedMedia(), "JobError_UnsupportedMedia"),
        (YtDlpFailed(), "JobError_YtDlpFailed"),
        (DownloadFailed(), "JobError_DownloadFailed"),
        (NoEncoder(), "JobError_NoEncoder"),
        (RetentionCheck(), "JobError_RetentionCheck"),
        (BackupWrite(), "JobError_BackupWrite"),
        (BackupMissing(), "JobError_BackupMissing"),
        (NoSummary(), "JobError_NoSummary"),
        (UnknownKind(), "JobError_UnknownKind"),
    ];

    /// <summary>The error in the UI language; null for null.</summary>
    public static string? Localize(string? error, ILocalizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        foreach (var (pattern, key) in Known)
        {
            var match = pattern.Match(error);
            if (match.Success)
            {
                var args = match.Groups.Cast<Group>().Skip(1).Select(g => (object?)g.Value.Trim()).ToArray();
                return args.Length == 0 ? text.GetString(key) : text.Format(key, args);
            }
        }

        return error;
    }

    [GeneratedRegex(@"^No speech recognition model is installed\.")]
    private static partial Regex NoAsrModel();

    [GeneratedRegex(@"^(.+?) was edited outside Kakitome")]
    private static partial Regex EditedOutside();

    [GeneratedRegex(@"^(.+?) could not be read: (.*)$", RegexOptions.Singleline)]
    private static partial Regex AudioUnreadable();

    [GeneratedRegex(@"^This file's audio (?:cannot be read): (.*)$", RegexOptions.Singleline)]
    private static partial Regex ImportUnreadable();

    [GeneratedRegex(@"^There is no transcript to (?:clean up|summarize)\.$")]
    private static partial Regex NoTranscript();

    [GeneratedRegex(@"^The original file is no longer available\.$")]
    private static partial Regex SourceMissing();

    [GeneratedRegex(@"^The URL is not valid\.$")]
    private static partial Regex BadUrl();

    [GeneratedRegex(@"^(?:URL import needs yt-dlp|yt-dlp is not available|No media downloader is available)")]
    private static partial Regex NoYtDlp();

    [GeneratedRegex(@"^The downloaded '(.*)' media is not supported\.$")]
    private static partial Regex UnsupportedMedia();

    [GeneratedRegex(@"^(?:yt-dlp did not produce a media file\.|yt-dlp exited with code \d+\.|yt-dlp could not be started: .*)$", RegexOptions.Singleline)]
    private static partial Regex YtDlpFailed();

    [GeneratedRegex(@"^Download of (.+?) failed: (.*)$", RegexOptions.Singleline)]
    private static partial Regex DownloadFailed();

    [GeneratedRegex(@"^No audio encoder is available\.$")]
    private static partial Regex NoEncoder();

    [GeneratedRegex(@"^The converted audio (?:could not be read back|is .* long but the recording is)")]
    private static partial Regex RetentionCheck();

    [GeneratedRegex(@"^The backup could not be written: (.*)$", RegexOptions.Singleline)]
    private static partial Regex BackupWrite();

    [GeneratedRegex(@"^The backup file is no longer available\.$")]
    private static partial Regex BackupMissing();

    [GeneratedRegex(@"^No summary provider is available\.$")]
    private static partial Regex NoSummary();

    [GeneratedRegex(@"^This version of Kakitome cannot run '(.+)' jobs\.$")]
    private static partial Regex UnknownKind();
}
