using Kakitome.Presentation.Services;

namespace Kakitome.Tests.Presentation;

public sealed class JobErrorTextTests
{
    [Theory]
    [InlineData("No speech recognition model is installed. Install one in Settings › Models; this job is retried when it is ready.", "JobError_NoAsrModel")]
    [InlineData("transcript.md was edited outside Kakitome; the summary was not overwritten.", "JobError_EditedOutside|transcript.md")]
    [InlineData("audio.wav could not be read: bad header", "JobError_AudioUnreadable|audio.wav|bad header")]
    [InlineData("This file's audio cannot be read: codec", "JobError_AudioUnreadable|codec")]
    [InlineData("There is no transcript to summarize.", "JobError_NoTranscript")]
    [InlineData("URL import needs yt-dlp. Install it or choose its location in Settings.", "JobError_NoYtDlp")]
    [InlineData("yt-dlp is not available.", "JobError_NoYtDlp")]
    [InlineData("The downloaded '.mkv' media is not supported.", "JobError_UnsupportedMedia|.mkv")]
    [InlineData("yt-dlp exited with code 1.", "JobError_YtDlpFailed")]
    [InlineData("Download of Whisper small failed: timeout", "JobError_DownloadFailed|Whisper small|timeout")]
    [InlineData("The converted audio is 10.0 s long but the recording is 12.0 s; the original is kept.", "JobError_RetentionCheck")]
    [InlineData("This version of Kakitome cannot run 'x.y' jobs.", "JobError_UnknownKind|x.y")]
    public void Known_errors_are_shown_in_the_UI_language(string error, string expected) =>
        Assert.Equal(expected, JobErrorText.Localize(error, new EchoLocalizer()));

    [Fact]
    public void Other_errors_are_shown_as_they_are()
    {
        Assert.Equal("ERROR: [youtube] Video unavailable", JobErrorText.Localize("ERROR: [youtube] Video unavailable", new EchoLocalizer()));
        Assert.Null(JobErrorText.Localize(null, new EchoLocalizer()));
    }

    /// <summary>"key|arg|arg" so the test sees which string and which values were chosen.</summary>
    private sealed class EchoLocalizer : ILocalizer
    {
        public string GetString(string key) => key;

        public string Format(string key, params object?[] args) => string.Join("|", [key, .. args.Select(a => a?.ToString())]);
    }
}
