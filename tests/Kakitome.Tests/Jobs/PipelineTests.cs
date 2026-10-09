using System.Runtime.InteropServices;
using NAudio.Wave;
using Kakitome.Application.Audio;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Storage.Audio;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Jobs;

public sealed class PipelineTests
{
    [Fact]
    public async Task Stopping_a_recording_queues_and_runs_analysis_into_metadata()
    {
        await using var f = await LibraryFixture.CreateAsync();
        _ = f.Pipeline; // constructed at startup in the app
        var session = await f.Recording.StartAsync(new RecordingOptions());
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        for (var i = 0; i < 4; i++)
        {
            mic.PushSeconds(0.25, i % 2 == 0 ? 0.3f : 0.0001f);
            await f.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        }

        await session.StopAsync();
        // The pending steps are written to metadata.json first, then the jobs are queued.
        await WaitUntilAsync(async () => (await f.Jobs.ListActiveAsync()).Any(j => j.Kind == AnalyzeAudioJobHandler.JobKind));
        Assert.Contains((await f.Library.GetMetadataAsync(session.Id)).Processing,
            p => p.Stage == AnalyzeAudioJobHandler.JobKind && p.Status == ProcessingStepStatus.Pending);
        Assert.Single(await f.Jobs.ListActiveAsync(), j => j.Kind == AnalyzeAudioJobHandler.JobKind);

        await JobSchedulerTests.RunUntilSettledAsync(f);
        await WaitUntilAsync(async () =>
            (await f.Library.GetMetadataAsync(session.Id)).Processing.Single(p => p.Stage == AnalyzeAudioJobHandler.JobKind).Status
                == ProcessingStepStatus.Succeeded);

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        var analysis = metadata.Audio.Single().Analysis!;
        Assert.False(analysis.Silent);
        Assert.InRange(analysis.PeakDbfs, -11, -10);
        Assert.InRange(analysis.SpeechRatio, 0.4, 0.6);
        var step = metadata.Processing.Single(p => p.Stage == AnalyzeAudioJobHandler.JobKind);
        Assert.Equal("Kakitome level analysis (local)", step.Provider);
        Assert.NotNull(step.CompletedAt);
    }

    [Fact]
    public async Task Discarded_recordings_are_not_processed()
    {
        await using var f = await LibraryFixture.CreateAsync();
        _ = f.Pipeline;
        var session = await f.Recording.StartAsync(new RecordingOptions());
        await session.CancelAsync();
        await Task.Delay(100);

        Assert.Empty(await f.Jobs.ListAsync());
    }

    [Fact]
    public async Task Startup_catches_up_recordings_that_were_never_queued()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        var folder = await f.Library.GetRecordingPathAsync(entry.Id);
        using (var writer = new WavFileWriter(Path.Combine(folder, "audio.wav"), AudioFormat.Microphone))
        {
            writer.Write(new byte[48_000 * 4]);
            writer.Complete();
        }

        await f.Library.UpdateMetadataAsync(entry.Id, m =>
        {
            m.Capture!.Status = CaptureStatus.Completed;
            m.Audio = [new AudioStreamInfo { FileName = "audio.wav", Role = AudioStreamRole.Microphone }];
        });
        await f.Library.SynchronizeAsync();

        Assert.Equal(1, await f.Pipeline.EnsureQueuedAsync());
        Assert.Equal(0, await f.Pipeline.EnsureQueuedAsync());
        await JobSchedulerTests.RunUntilSettledAsync(f);
        await WaitUntilAsync(async () => (await f.Library.GetMetadataAsync(entry.Id)).Audio[0].Analysis is not null);
        Assert.True((await f.Library.GetMetadataAsync(entry.Id)).Audio[0].Analysis!.Silent);
    }

    [Fact]
    public void Analyzer_reports_silence_levels_and_speech_ratio()
    {
        var silence = AudioAnalyzer.Analyze(new ArrayReader(new float[48_000], AudioFormat.Microphone));
        Assert.True(silence.Silent);
        Assert.Equal(0, silence.SpeechRatio);

        // 1 s of tone at -6 dBFS then 3 s of quiet noise.
        var samples = new float[4 * 48_000];
        var rng = new Random(7);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i < 48_000 ? 0.5f * MathF.Sin(i * 0.1f) : (float)((rng.NextDouble() - 0.5) * 0.001);
        }

        var mixed = AudioAnalyzer.Analyze(new ArrayReader(samples, AudioFormat.Microphone));
        Assert.False(mixed.Silent);
        Assert.InRange(mixed.PeakDbfs, -6.1, -5.9);
        Assert.InRange(mixed.SpeechRatio, 0.22, 0.28);
        Assert.True(mixed.NoiseFloorDbfs < -60);
    }

    [Fact]
    public void Wav_reader_handles_float_pcm16_and_stale_crash_headers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "KakitomeTests", "reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var floatPath = Path.Combine(dir, "f.wav");
            var writer = new WavFileWriter(floatPath, AudioFormat.Loopback);
            writer.Write(MemoryMarshal.AsBytes(Enumerable.Repeat(0.5f, 9_600).ToArray().AsSpan()));
            writer.Dispose(); // no checkpoint: header still says 0 bytes

            using (var reader = new WavSampleReader(floatPath))
            {
                Assert.Equal(4_800, reader.TotalFrames);
                var buffer = new float[100];
                Assert.Equal(50, reader.Read(buffer));
                Assert.Equal(0.5f, buffer[0]);
            }

            var pcmPath = Path.Combine(dir, "p.wav");
            using (var pcm = new WaveFileWriter(pcmPath, new WaveFormat(16_000, 16, 1)))
            {
                pcm.WriteSamples([16384, -16384], 0, 2);
            }

            using (var reader = new WavSampleReader(pcmPath))
            {
                Assert.Equal(new AudioFormat(16_000, 1), reader.Format);
                var buffer = new float[4];
                Assert.Equal(2, reader.Read(buffer));
                Assert.Equal(0.5f, buffer[0]);
                Assert.Equal(-0.5f, buffer[1]);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition not reached");
    }

    private sealed class ArrayReader(float[] samples, AudioFormat format) : IAudioSampleReader
    {
        private int _position;

        public AudioFormat Format { get; } = format;

        public long TotalFrames => samples.Length / Format.Channels;

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n / Format.Channels;
        }

        public void Dispose()
        {
        }
    }
}
