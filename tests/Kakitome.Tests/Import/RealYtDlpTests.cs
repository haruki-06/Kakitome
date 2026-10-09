using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Audio;
using Kakitome.Application.Import;
using Kakitome.Application.Jobs;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Infrastructure.Audio;
using Kakitome.Infrastructure.Import;
using Kakitome.Storage;
using Kakitome.Storage.Audio;
using Kakitome.Storage.Models;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Import;

/// <summary>
/// URL import with the real, hash-pinned yt-dlp (installed in the user's model folder) against a media file served on
/// localhost — exercises the real process invocation and output parsing without touching the Internet.
/// </summary>
[Trait("Category", "Tool")]
public sealed class RealYtDlpTests
{
    [Fact]
    public async Task Real_yt_dlp_imports_a_served_media_file_into_the_library()
    {
        var store = new ModelStore(AppDataPaths.Default, new SharedHttpClientFactory(), NullLogger<ModelStore>.Instance);
        Assert.SkipUnless(store.GetState(ModelCatalog.YtDlp) == ModelState.Installed, "yt-dlp is not installed on this machine.");

        using var server = new MediaServer(WriteTone(3));
        await using var f = await LibraryFixture.CreateAsync(configure: s =>
        {
            s.AddSingleton<IModelStore>(store);
            s.AddSingleton<IMediaUrlDownloader, YtDlpDownloader>();
            s.AddSingleton<IAudioSampleReaderFactory, MediaAudioSampleReaderFactory>();
        });

        var id = await f.Services.GetRequiredService<ImportService>().ImportUrlAsync(server.Url, null, TestContext.Current.CancellationToken);
        for (var i = 0; i < 60; i++)
        {
            await JobSchedulerTests.RunUntilSettledAsync(f, passes: 1);
            if ((await f.Jobs.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Any(j => j.Kind == ImportUrlJobHandler.JobKind && j.IsTerminal))
            {
                break;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        var job = Assert.Single(await f.Jobs.ListAsync(cancellationToken: TestContext.Current.CancellationToken), j => j.Kind == ImportUrlJobHandler.JobKind);
        Assert.True(job.State == JobState.Succeeded, $"{job.State} after {job.Attempts} attempt(s): {job.LastError}");
        Assert.Equal("yt-dlp", job.Engine);
        var metadata = await f.Library.GetMetadataAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(RecordingSourceType.UrlImport, metadata.SourceType);
        Assert.Equal(server.Url, metadata.Import!.SourceUrl);
        Assert.InRange(metadata.DurationSeconds!.Value, 2.9, 3.1);
        Assert.Equal("talk", metadata.Title); // title reported by yt-dlp for a direct file
        Assert.Empty(Directory.EnumerateDirectories(f.Services.GetRequiredService<ImportWorkspace>().Root));
    }

    private static byte[] WriteTone(double seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), "KakitomeTests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = new AudioFormat(48_000, 1);
        using (var writer = new WavFileWriter(path, format))
        {
            var samples = new float[(int)(format.SampleRate * seconds)];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.2f * MathF.Sin(2 * MathF.PI * 440 * i / format.SampleRate);
            }

            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
            writer.Complete();
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    /// <summary>Serves one WAV at http://localhost:&lt;port&gt;/talk.wav (localhost needs no URL ACL).</summary>
    private sealed class MediaServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public MediaServer(byte[] content)
        {
            var port = FreePort();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Url = $"http://localhost:{port}/talk.wav";
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    using var response = context.Response;
                    if (context.Request.Url?.AbsolutePath != "/talk.wav")
                    {
                        response.StatusCode = 404;
                        continue;
                    }

                    response.ContentType = "audio/wav";
                    response.ContentLength64 = content.Length;
                    if (context.Request.HttpMethod != "HEAD")
                    {
                        try
                        {
                            await response.OutputStream.WriteAsync(content);
                        }
                        catch (HttpListenerException)
                        {
                            // The client closed the connection early (e.g. after probing); fine for a test server.
                        }
                    }
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Shutdown races are irrelevant to the test result.
            }
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
