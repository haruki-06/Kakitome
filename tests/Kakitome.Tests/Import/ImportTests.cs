using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Audio;
using Kakitome.Application.Import;
using Kakitome.Application.Jobs;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Infrastructure.Import;
using Kakitome.Storage.Audio;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Import;

public sealed class ImportTests
{
    [Fact]
    public async Task File_import_copies_the_media_records_duration_and_starts_the_pipeline()
    {
        await using var f = await LibraryFixture.CreateAsync();
        _ = f.Pipeline;
        var source = Path.Combine(f.Root, "週次定例.wav");
        WriteTone(source, seconds: 3);
        var before = File.ReadAllBytes(source);

        var id = await f.Services.GetRequiredService<ImportService>().ImportFileAsync(source, "会議");
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        var metadata = (await f.Library.GetMetadataAsync(id));
        Assert.Equal("週次定例", metadata.Title);
        Assert.Equal(RecordingSourceType.FileImport, metadata.SourceType);
        Assert.Equal(3, metadata.DurationSeconds!.Value, 2);
        Assert.Equal("audio.wav", Assert.Single(metadata.Audio).FileName);
        Assert.True(File.Exists(Path.Combine(await f.Library.GetRecordingPathAsync(id), "audio.wav")));
        Assert.Equal(before, File.ReadAllBytes(source)); // the user's original is never modified
        Assert.True(await PipelineQueuedAsync(f, id), "the processing pipeline was not queued");
    }

    [Fact]
    public async Task Url_import_downloads_into_the_recording_and_takes_the_remote_title()
    {
        var downloader = new FakeDownloader((work, _) =>
        {
            var file = Path.Combine(work, "media.wav");
            WriteTone(file, seconds: 2);
            return new DownloadedMedia(file, "Keynote\n2026");
        });
        await using var f = await LibraryFixture.CreateAsync(configure: s => s.AddSingleton<IMediaUrlDownloader>(downloader));
        _ = f.Pipeline;

        var id = await f.Services.GetRequiredService<ImportService>().ImportUrlAsync("https://media.example.com/watch?v=1", null);
        Assert.Equal("media.example.com", (await f.Library.GetMetadataAsync(id)).Title); // visible immediately
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        var metadata = (await f.Library.GetMetadataAsync(id));
        Assert.Equal("Keynote2026", metadata.Title);
        Assert.Equal(RecordingSourceType.UrlImport, metadata.SourceType);
        Assert.Equal("https://media.example.com/watch?v=1", metadata.Import!.SourceUrl);
        Assert.Equal(2, metadata.DurationSeconds!.Value, 2);
        Assert.Equal(new Uri("https://media.example.com/watch?v=1"), downloader.LastUrl);
        Assert.Empty(Directory.EnumerateDirectories(f.Services.GetRequiredService<ImportWorkspace>().Root));
        Assert.True(await PipelineQueuedAsync(f, id), "the processing pipeline was not queued");
    }

    [Fact]
    public async Task Unsupported_url_fails_permanently_and_keeps_the_recording()
    {
        var downloader = new FakeDownloader((_, _) => throw new MediaDownloadException("ERROR: Unsupported URL", isPermanent: true));
        await using var f = await LibraryFixture.CreateAsync(configure: s => s.AddSingleton<IMediaUrlDownloader>(downloader));

        var id = await f.Services.GetRequiredService<ImportService>().ImportUrlAsync("https://example.com/page", null);
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        var job = Assert.Single(await f.Jobs.ListAsync(), j => j.Kind == ImportUrlJobHandler.JobKind);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Unsupported URL", job.LastError, StringComparison.Ordinal);
        Assert.NotNull(await f.Library.FindAsync(id)); // nothing is deleted on failure
    }

    [Fact]
    public async Task Url_import_without_a_downloader_fails_with_guidance()
    {
        await using var f = await LibraryFixture.CreateAsync();
        await f.Services.GetRequiredService<ImportService>().ImportUrlAsync("https://example.com/a.mp3", null);
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        var job = Assert.Single(await f.Jobs.ListAsync(), j => j.Kind == ImportUrlJobHandler.JobKind);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("yt-dlp", job.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_media_fails_with_a_readable_error_and_keeps_the_recording()
    {
        await using var f = await LibraryFixture.CreateAsync(configure: s =>
            s.AddSingleton<IAudioSampleReaderFactory, Kakitome.Infrastructure.Audio.MediaAudioSampleReaderFactory>());
        var source = Path.Combine(f.Root, "broken.mp3");
        await File.WriteAllTextAsync(source, "this is not audio", TestContext.Current.CancellationToken);

        var id = await f.Services.GetRequiredService<ImportService>().ImportFileAsync(source, null, TestContext.Current.CancellationToken);
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        var job = Assert.Single(await f.Jobs.ListAsync(cancellationToken: TestContext.Current.CancellationToken), j => j.Kind == ImportFileJobHandler.JobKind);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("cannot be read", job.LastError, StringComparison.Ordinal);
        Assert.Equal(1, job.Attempts); // permanent: no pointless retries
        Assert.NotNull(await f.Library.FindAsync(id, TestContext.Current.CancellationToken));
        Assert.Equal("this is not audio", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken)); // original untouched
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", true)]
    [InlineData("  http://example.com/a.mp3  ", true)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ftp://example.com/a.mp3", false)]
    [InlineData("https://user:pass@example.com/a", false)]
    [InlineData("--exec calc", false)]
    [InlineData("https://example.com/a b", false)]
    [InlineData("example.com/a.mp3", false)]
    [InlineData("", false)]
    public void Media_urls_are_validated(string input, bool valid) => Assert.Equal(valid, MediaUrl.TryParse(input, out _));

    [Fact]
    public async Task Invalid_url_creates_nothing()
    {
        await using var f = await LibraryFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Services.GetRequiredService<ImportService>().ImportUrlAsync("javascript:alert(1)", null));
        Assert.Empty(await f.Library.ListRecordingsAsync());
    }

    [Fact]
    public void YtDlp_arguments_ignore_user_config_and_end_with_the_url_after_a_separator()
    {
        var args = YtDlpDownloader.BuildArguments(new Uri("https://example.com/v?id=--exec"), @"C:\work");

        Assert.Contains("--ignore-config", args);
        Assert.Contains("--no-playlist", args);
        Assert.Equal("utf-8", args[args.ToList().IndexOf("--encoding") + 1]); // titles are read as UTF-8
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://example.com/v?id=--exec", args[^1]);
        Assert.DoesNotContain(args, a => a.Contains("--exec", StringComparison.Ordinal) && a != args[^1]);
    }

    [Fact]
    public void YtDlp_output_lines_are_parsed()
    {
        Assert.Equal((YtDlpDownloader.LineKind.File, @"C:\work\media.m4a"), YtDlpDownloader.ParseLine(YtDlpDownloader.FileMarker + @"C:\work\media.m4a"));
        Assert.Equal((YtDlpDownloader.LineKind.Title, "講演 #1"), YtDlpDownloader.ParseLine(YtDlpDownloader.TitleMarker + "講演 #1"));
        Assert.Equal(YtDlpDownloader.LineKind.None, YtDlpDownloader.ParseLine("[youtube] abc: Downloading webpage").Kind);

        Assert.True(YtDlpDownloader.TryParseProgress("500 1000 NA", out var a));
        Assert.Equal(0.5, a);
        Assert.True(YtDlpDownloader.TryParseProgress("250 NA 1000", out var b));
        Assert.Equal(0.25, b);
        Assert.False(YtDlpDownloader.TryParseProgress("250 NA NA", out _));
    }

    [Theory]
    [InlineData("ERROR: Unsupported URL: https://example.com/", true)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", true)]
    [InlineData("ERROR: unable to download video data: HTTP Error 503: Service Unavailable", false)]
    [InlineData("ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", false)]
    public void YtDlp_errors_are_classified(string error, bool permanent) => Assert.Equal(permanent, YtDlpDownloader.IsPermanentError(error));

    [Fact]
    public void Only_fully_qualified_existing_exe_files_are_used_as_yt_dlp()
    {
        Assert.False(YtDlpDownloader.IsUsableExecutable(null));
        Assert.False(YtDlpDownloader.IsUsableExecutable("yt-dlp.exe"));
        Assert.False(YtDlpDownloader.IsUsableExecutable(@"C:\does-not-exist\yt-dlp.exe"));
        Assert.False(YtDlpDownloader.IsUsableExecutable(typeof(ImportTests).Assembly.Location)); // a .dll
        Assert.True(YtDlpDownloader.IsUsableExecutable(Path.Combine(Environment.SystemDirectory, "where.exe")));
    }

    [Fact]
    public void Remote_titles_are_cleaned_and_bounded() =>
        Assert.Equal(new string('a', 200), ImportUrlJobHandler.CleanTitle(" \u0007" + new string('a', 300)));

    /// <summary>The pipeline is queued asynchronously after the import job succeeds.</summary>
    private static async Task<bool> PipelineQueuedAsync(LibraryFixture f, Kakitome.Domain.Recordings.RecordingId id)
    {
        for (var i = 0; i < 200; i++)
        {
            if ((await f.Jobs.ListAsync()).Any(j => j.RecordingId == id && j.Kind == "audio.analyze"))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    private static void WriteTone(string path, double seconds)
    {
        var format = new AudioFormat(48_000, 1);
        using var writer = new WavFileWriter(path, format);
        var samples = new float[(int)(format.SampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.2f * MathF.Sin(2 * MathF.PI * 440 * i / format.SampleRate);
        }

        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        writer.Complete();
    }

    private sealed class FakeDownloader(Func<string, Uri, DownloadedMedia> download) : IMediaUrlDownloader
    {
        public Uri? LastUrl { get; private set; }

        public bool IsAvailable => true;

        public Task<DownloadedMedia> DownloadAsync(Uri url, string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            LastUrl = url;
            return Task.FromResult(download(workDirectory, url));
        }
    }
}
