using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Asr;
using Kakitome.Application.Audio;
using Kakitome.Application.Jobs;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Infrastructure.Audio;
using Kakitome.Storage.Audio;
using Kakitome.Tests.Asr;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Recording;

/// <summary>Retention (docs/03): conversion with Windows Media Foundation and explicit delete-after-processing.</summary>
public sealed class RetentionTests
{
    [Theory]
    [InlineData(RetentionPolicy.M4a, "m4a")]
    [InlineData(RetentionPolicy.Mp3, "mp3")]
    public async Task After_processing_the_capture_is_converted_and_verified(string policy, string extension)
    {
        await using var f = await CreateAsync();
        await f.Settings.UpdateAsync(s => s.Recording.Retention = policy, TestContext.Current.CancellationToken);
        var id = await AsrPipelineTests.CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 20));

        await f.Pipeline.EnqueueAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        var metadata = await RunUntilAsync(f, id, m => m.RetainedAudioFormat == extension);

        var folder = await f.Library.GetRecordingPathAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal($"audio.{extension}", Assert.Single(metadata.Audio).FileName);
        Assert.False(File.Exists(Path.Combine(folder, "audio.wav")));
        Assert.Empty(Directory.EnumerateFiles(folder, ".kakitome-tmp-*"));
        using var reader = new MediaAudioSampleReaderFactory().Open(Path.Combine(folder, $"audio.{extension}"));
        Assert.InRange(reader.TotalFrames / (double)reader.Format.SampleRate, 19.5, 20.5);
        Assert.NotNull(await f.Library.LoadTranscriptAsync(id, TestContext.Current.CancellationToken)); // processing results untouched
    }

    [Fact]
    public async Task Delete_after_processing_removes_audio_and_records_why()
    {
        await using var f = await CreateAsync();
        await f.Settings.UpdateAsync(s => s.Recording.Retention = RetentionPolicy.DeleteAfterProcessing, TestContext.Current.CancellationToken);
        var id = await AsrPipelineTests.CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 10), ("audio.system.wav", AudioStreamRole.SystemAudio, 10));

        await f.Pipeline.EnqueueAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        var metadata = await RunUntilAsync(f, id, m => m.AudioRemoval is not null);

        var folder = await f.Library.GetRecordingPathAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(RetentionPolicy.DeleteAfterProcessing, metadata.AudioRemoval!.Setting);
        Assert.Equal(["audio.wav", "audio.system.wav"], metadata.AudioRemoval.RemovedFiles);
        Assert.Empty(metadata.Audio);
        Assert.Empty(Directory.EnumerateFiles(folder, "audio*"));
        Assert.NotNull(await f.Library.LoadTranscriptAsync(id, TestContext.Current.CancellationToken));
        Assert.NotNull(await f.Library.LoadSummaryAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Audio_is_kept_when_any_processing_step_did_not_succeed()
    {
        await using var f = await CreateAsync();
        var id = await AsrPipelineTests.CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 5));
        await f.Library.UpdateMetadataAsync(id, m => m.Processing =
        [
            new ProcessingStepInfo { Stage = "asr", Status = ProcessingStepStatus.Succeeded },
            new ProcessingStepInfo { Stage = "summary", Status = ProcessingStepStatus.Failed },
        ], TestContext.Current.CancellationToken);

        var job = await f.Scheduler.EnqueueAsync(new JobRequest(RetentionJobHandler.JobKind)
        {
            RecordingId = id,
            Payload = RetentionJobHandler.PayloadFor(RetentionPolicy.DeleteAfterProcessing),
        }, TestContext.Current.CancellationToken);
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        Assert.Equal("kept", (await f.Jobs.GetAsync(job.Id, TestContext.Current.CancellationToken))!.Engine);
        var folder = await f.Library.GetRecordingPathAsync(id, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(folder, "audio.wav")));
        Assert.Null((await f.Library.GetMetadataAsync(id, TestContext.Current.CancellationToken)).AudioRemoval);
    }

    [Fact]
    public async Task Raw_retention_never_queues_a_retention_job()
    {
        await using var f = await CreateAsync();
        var id = await AsrPipelineTests.CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 5));

        await f.Pipeline.EnqueueAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        await RunUntilAsync(f, id, m => m.Processing.All(p => p.Status == ProcessingStepStatus.Succeeded));
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);

        Assert.DoesNotContain(await f.Jobs.ListAsync(cancellationToken: TestContext.Current.CancellationToken), j => j.Kind == RetentionJobHandler.JobKind);
        Assert.True(File.Exists(Path.Combine(await f.Library.GetRecordingPathAsync(id, TestContext.Current.CancellationToken), "audio.wav")));
    }

    [Theory]
    [InlineData(16_000, 2, EncodedAudioFormat.Mp3)]
    [InlineData(48_000, 4, EncodedAudioFormat.M4a)]
    [InlineData(44_100, 1, EncodedAudioFormat.M4a)]
    public async Task Encoder_handles_other_rates_and_channel_counts(int rate, int channels, EncodedAudioFormat format)
    {
        var dir = Path.Combine(Path.GetTempPath(), "KakitomeTests", "enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "in.wav");
            using (var writer = new WavFileWriter(source, new AudioFormat(rate, channels)))
            {
                var samples = new float[rate * 3 * channels];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * (i / channels) / rate);
                }

                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
                writer.Complete();
            }

            var target = Path.Combine(dir, format == EncodedAudioFormat.M4a ? "out.m4a" : "out.mp3");
            var readers = new MediaAudioSampleReaderFactory();
            await new MediaFoundationAudioEncoder(readers).EncodeAsync(source, target, format, TestContext.Current.CancellationToken);

            using var reader = readers.Open(target);
            Assert.InRange(reader.TotalFrames / (double)reader.Format.SampleRate, 2.8, 3.2);
            Assert.InRange(reader.Format.Channels, 1, 2);
            var analysis = AudioAnalyzer.Analyze(reader, TestContext.Current.CancellationToken);
            Assert.InRange(analysis.RmsDbfs, -16, -8); // a 0.3 sine is about -13.5 dBFS RMS
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Encoding_can_be_cancelled_and_leaves_no_partial_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "KakitomeTests", "enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "in.wav");
            using (var writer = new WavFileWriter(source, new AudioFormat(48_000, 1)))
            {
                writer.Write(new byte[48_000 * 4 * 30]);
                writer.Complete();
            }

            var target = Path.Combine(dir, "out.m4a");
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new MediaFoundationAudioEncoder(new MediaAudioSampleReaderFactory()).EncodeAsync(source, target, EncodedAudioFormat.M4a, cts.Token));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Task<LibraryFixture> CreateAsync()
    {
        var asr = new FakeAsrProvider();
        return LibraryFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IFinalAsrProvider>(asr);
            services.AddSingleton<IAsrEngineSelector>(new FakeEngineSelector(asr) { Available = true });
            services.AddSingleton<IAudioSampleReaderFactory, MediaAudioSampleReaderFactory>();
            services.AddSingleton<IAudioEncoder, MediaFoundationAudioEncoder>();
        });
    }

    private static async Task<RecordingMetadata> RunUntilAsync(LibraryFixture f, Kakitome.Domain.Recordings.RecordingId id, Func<RecordingMetadata, bool> done)
    {
        for (var i = 0; i < 40; i++)
        {
            await JobSchedulerTests.RunUntilSettledAsync(f, passes: 2);
            var metadata = await f.Library.GetMetadataAsync(id);
            if (done(metadata))
            {
                return metadata;
            }

            await Task.Delay(50);
        }

        var last = await f.Library.GetMetadataAsync(id);
        var jobs = string.Join(", ", (await f.Jobs.ListAsync()).Select(j => $"{j.Kind}={j.State}:{j.LastError}"));
        Assert.Fail($"Condition not reached. Steps: {string.Join(", ", last.Processing.Select(p => $"{p.Stage}={p.Status}"))}; jobs: {jobs}");
        return last;
    }
}
