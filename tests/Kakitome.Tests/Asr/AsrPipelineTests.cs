using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Asr;
using Kakitome.Application.Audio;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Domain.Transcripts;
using Kakitome.Storage.Audio;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Asr;

public sealed class AsrPipelineTests
{
    [Fact]
    public void Resampler_preserves_in_band_tones_and_removes_aliasing_frequencies()
    {
        static (double Rms, int Crossings) Measure(List<float> s)
        {
            double sum = 0;
            var crossings = 0;
            for (var i = 1000; i < s.Count - 1000; i++)
            {
                sum += s[i] * s[i];
                if ((s[i - 1] < 0) != (s[i] < 0))
                {
                    crossings++;
                }
            }

            return (Math.Sqrt(sum / (s.Count - 2000)), crossings);
        }

        var inBand = Tone(48_000, 1_000, 2.0, 0.5f);
        var aliasing = Tone(48_000, 12_000, 2.0, 0.5f);
        var outIn = new List<float>();
        var outAlias = new List<float>();
        var r1 = new Resampler(48_000, 16_000);
        r1.Process(inBand, outIn);
        r1.Flush(outIn);
        var r2 = new Resampler(48_000, 16_000);
        r2.Process(aliasing, outAlias);
        r2.Flush(outAlias);

        Assert.InRange(outIn.Count, 31_900, 32_100);
        var (rms, crossings) = Measure(outIn);
        Assert.InRange(rms, 0.33, 0.38);                      // 0.5/√2 ≈ 0.354
        Assert.InRange(crossings / ((outIn.Count - 2000) / 16_000.0), 1_990, 2_010); // 1 kHz → 2000 crossings/s
        Assert.True(Measure(outAlias).Rms < 0.02, "12 kHz must not alias into the 16 kHz output");
    }

    [Fact]
    public void Chunker_cuts_at_pauses_within_limits_and_skips_silence()
    {
        // 0–20 s speech-like bursts, 20–60 s silence, 60–100 s bursts. 16 kHz mono input.
        var samples = new float[100 * 16_000];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / 16_000.0;
            var speaking = (t < 20 || t >= 60) && (t % 4) < 3.2; // 0.8 s pause every 4 s
            samples[i] = speaking ? 0.3f * MathF.Sin((float)(2 * Math.PI * 220 * t)) : 0;
        }

        var chunks = SpeechChunker.Split(new ArrayReader(samples, new AudioFormat(16_000, 1))).ToList();

        Assert.All(chunks, c => Assert.True(c.DurationSeconds <= SpeechChunker.MaxSeconds + 0.01));
        Assert.True(chunks.Select(c => c.Index).SequenceEqual(chunks.Select(c => c.Index).Order()));
        // No chunk lies entirely inside the 20–60 s silence.
        Assert.DoesNotContain(chunks, c => c.StartSeconds >= 21 && c.StartSeconds + c.DurationSeconds <= 59);
        Assert.Contains(chunks, c => c.StartSeconds < 1);
        Assert.Contains(chunks, c => c.StartSeconds + c.DurationSeconds > 95);
        // Cuts land in pauses (the 0.8 s gaps), not mid-burst.
        foreach (var c in chunks.Skip(1))
        {
            var t = c.StartSeconds;
            Assert.True((t % 4) >= 3.1 || t is >= 20 and < 60, $"cut at {t:F2}s is inside speech");
        }
    }

    [Fact]
    public async Task Asr_job_writes_transcript_files_with_absolute_times_and_engine_lineage()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr);
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 70));

        await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        var transcript = await f.Library.LoadTranscriptAsync(id);
        Assert.NotNull(transcript);
        Assert.Equal(TranscriptKind.Raw, transcript.Kind);
        Assert.Equal("ja", transcript.Language);
        Assert.Equal("fake-asr 1.0", transcript.Engine!.Provider);
        Assert.Equal("fake-model", transcript.Engine.Model);
        Assert.Equal(asr.Calls, transcript.Segments.Count);
        Assert.True(transcript.Segments.Count >= 3);
        Assert.Equal(0.5, transcript.Segments[0].StartSeconds, 1);
        Assert.True(transcript.Segments.Zip(transcript.Segments.Skip(1)).All(p => p.Second.StartSeconds > p.First.StartSeconds + 20));
        Assert.Empty(transcript.Speakers);
        Assert.Equal("asr", transcript.Lineage.Single().Source);

        var folder = await f.Library.GetRecordingPathAsync(id);
        var md = await File.ReadAllTextAsync(Path.Combine(folder, LibraryLayout.TranscriptMarkdownFile));
        Assert.Contains("[00:00:00] 発話1", md, StringComparison.Ordinal);
        Assert.Equal("ja", (await f.Library.GetMetadataAsync(id)).Language);

        // Previous text is offered as context for the next chunk; the first gets a punctuated Japanese opening.
        Assert.Equal(AsrPromptBuilder.JapaneseStyleSeed, asr.Prompts[0]);
        Assert.Equal("発話1", asr.Prompts[1]);
    }

    [Fact]
    public async Task Project_glossary_is_offered_as_hints_and_its_corrections_are_applied_by_cleanup()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr);
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 40));
        await File.WriteAllTextAsync(Path.Combine(f.LibraryRoot, "Projects", "ASR", GlossaryService.FileName), "堀木訴訟\n発話 -> 発言\n");

        await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        await f.Scheduler.EnqueueAsync(new JobRequest(Kakitome.Application.Decision.CleanupJobHandler.JobKind) { RecordingId = id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        Assert.StartsWith("堀木訴訟、発言。", asr.Prompts[0], StringComparison.Ordinal);
        var transcript = await f.Library.LoadTranscriptAsync(id);
        Assert.Equal("発言1", transcript!.Segments[0].Text);
        Assert.Equal("発話1", transcript.Segments[0].RawText);
        Assert.Contains("glossary", transcript.Lineage[^1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asr_resumes_after_a_crash_without_redoing_finished_chunks()
    {
        var asr = new FakeAsrProvider { FailOnCall = 2 };
        await using var f = await CreateAsync(asr);
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 70));
        var job = await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });

        await JobSchedulerTests.RunUntilSettledAsync(f);
        var failed = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Pending, failed.State);
        Assert.Contains("simulated engine crash", failed.LastError, StringComparison.Ordinal);
        Assert.Contains("\"nextChunk\":1", failed.Checkpoint, StringComparison.Ordinal);

        f.Time.Advance(JobScheduler.Backoff(1) + TimeSpan.FromSeconds(1));
        await JobSchedulerTests.RunUntilSettledAsync(f);

        var transcript = await f.Library.LoadTranscriptAsync(id);
        Assert.NotNull(transcript);
        Assert.Equal(asr.Calls - 1, transcript.Segments.Count); // one call was the crash
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
    }

    [Fact]
    public async Task Multiple_sources_are_transcribed_separately_and_labelled_and_silent_ones_skipped()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr);
        var id = await CreateRecordingAsync(f,
            ("audio.wav", AudioStreamRole.Microphone, 20),
            ("audio.system.wav", AudioStreamRole.SystemAudio, 20),
            ("audio.app.wav", AudioStreamRole.Application, 20));
        await f.Library.UpdateMetadataAsync(id, m => m.Audio[2].Analysis = new AudioAnalysis { Silent = true });

        await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        var transcript = (await f.Library.LoadTranscriptAsync(id))!;
        Assert.Equal(["mic", "system"], transcript.Speakers.Select(s => s.Id));
        Assert.Equal(["マイク", "システム音声"], transcript.Speakers.Select(s => s.Label));
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal(["mic", "system"], transcript.Segments.Select(s => s.Speaker));
        Assert.Equal(2, asr.Calls);
    }

    [Fact]
    public async Task Missing_model_fails_with_an_actionable_message()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr, selectorAvailable: false);
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 5));
        var job = await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });

        await JobSchedulerTests.RunUntilSettledAsync(f);

        var failed = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("Settings › Models", failed.LastError, StringComparison.Ordinal);
        Assert.Null(await f.Library.LoadTranscriptAsync(id));
    }

    [Fact]
    public async Task A_transcription_that_waited_for_a_model_is_retried_when_the_model_is_installed()
    {
        var asr = new FakeAsrProvider();
        var selector = new FakeEngineSelector(asr) { Available = false };
        var store = new FakeModelStore();
        await using var f = await LibraryFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IFinalAsrProvider>(asr);
            services.AddSingleton<IAsrEngineSelector>(selector);
            services.AddSingleton<Kakitome.Application.Models.IModelStore>(store);
        });
        _ = f.Services.GetRequiredService<AsrModelReadyRetry>(); // listens from startup, as in the app
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 5));
        var job = await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        Assert.Equal(JobState.Failed, (await f.Jobs.GetAsync(job.Id))!.State); // first recording while the model downloads

        selector.Available = true;
        await f.Scheduler.EnqueueAsync(new JobRequest(Kakitome.Application.Models.ModelInstallJobHandler.JobKind)
        {
            Payload = Kakitome.Application.Models.ModelInstallJobHandler.PayloadFor(Kakitome.Application.Models.ModelCatalog.WhisperSmallQ5),
        });
        for (var i = 0; i < 50 && (await f.Jobs.GetAsync(job.Id))!.State != JobState.Succeeded; i++)
        {
            await JobSchedulerTests.RunUntilSettledAsync(f); // the retry is queued from the install's completion event
            await Task.Delay(20);
        }

        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);

        Assert.NotNull(await f.Library.LoadTranscriptAsync(id));
    }

    [Fact]
    public async Task Existing_transcript_is_kept_unless_replacement_is_requested()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr);
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 5));
        await f.Library.SaveTranscriptAsync(Samples.Transcript(id));

        await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        Assert.Equal(0, asr.Calls);
        Assert.Equal("それでは始めます。", (await f.Library.LoadTranscriptAsync(id))!.Segments[0].Text);

        await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id, Payload = AsrJobHandler.ReplacePayload });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        Assert.Equal("発話1", (await f.Library.LoadTranscriptAsync(id))!.Segments[0].Text);
    }

    [Fact]
    public async Task Hand_edited_markdown_is_not_overwritten_by_asr()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr);
        var id = await CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 5));
        var folder = await f.Library.GetRecordingPathAsync(id);
        await File.WriteAllTextAsync(Path.Combine(folder, LibraryLayout.TranscriptMarkdownFile), "my own notes");
        var job = await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = id });

        await JobSchedulerTests.RunUntilSettledAsync(f);

        Assert.Equal(JobState.Failed, (await f.Jobs.GetAsync(job.Id))!.State);
        Assert.Equal("my own notes", await File.ReadAllTextAsync(Path.Combine(folder, LibraryLayout.TranscriptMarkdownFile)));
    }

    [Fact]
    public async Task Pipeline_runs_analysis_then_asr_after_recording()
    {
        var asr = new FakeAsrProvider();
        await using var f = await CreateAsync(asr);
        _ = f.Pipeline;
        var session = await f.Recording.StartAsync(new RecordingOptions());
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        for (var i = 0; i < 8; i++)
        {
            mic.PushSeconds(0.5, 0.3f);
            await f.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        }

        await session.StopAsync();
        for (var i = 0; i < 50 && (await f.Jobs.ListActiveAsync()).Count < 2; i++)
        {
            await Task.Delay(10);
        }

        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 10);

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(ProcessingStepStatus.Succeeded, metadata.Processing.Single(p => p.Stage == "audio.analyze").Status);
        await WaitUntilAsync(async () => (await f.Library.GetMetadataAsync(session.Id)).Processing.Single(p => p.Stage == "asr").Status == ProcessingStepStatus.Succeeded);
        Assert.NotNull(await f.Library.LoadTranscriptAsync(session.Id));
    }

    internal static Task<LibraryFixture> CreateAsync(FakeAsrProvider asr, bool selectorAvailable = true) =>
        LibraryFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IFinalAsrProvider>(asr);
            services.AddSingleton<IAsrEngineSelector>(new FakeEngineSelector(asr) { Available = selectorAvailable });
        });

    /// <summary>Creates a completed recording whose streams contain speech-like bursts.</summary>
    internal static async Task<Kakitome.Domain.Recordings.RecordingId> CreateRecordingAsync(
        LibraryFixture f, params (string File, AudioStreamRole Role, double Seconds)[] streams)
    {
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "ASR", RecordedAt = Samples.Jst });
        var folder = await f.Library.GetRecordingPathAsync(entry.Id);
        foreach (var (file, role, seconds) in streams)
        {
            var format = role == AudioStreamRole.Microphone ? AudioFormat.Microphone : AudioFormat.Loopback;
            using var writer = new WavFileWriter(Path.Combine(folder, file), format);
            var mono = Tone(format.SampleRate, 220, seconds, 0.3f, burstSeconds: 4, gapSeconds: 0.8);
            var interleaved = new float[mono.Length * format.Channels];
            for (var i = 0; i < mono.Length; i++)
            {
                for (var c = 0; c < format.Channels; c++)
                {
                    interleaved[(i * format.Channels) + c] = mono[i];
                }
            }

            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved.AsSpan()));
            writer.Complete();
        }

        await f.Library.UpdateMetadataAsync(entry.Id, m =>
        {
            m.Capture!.Status = CaptureStatus.Completed;
            m.DurationSeconds = streams.Max(s => s.Seconds);
            m.Audio = streams.Select(s => new AudioStreamInfo { FileName = s.File, Role = s.Role }).ToList();
        });
        return entry.Id;
    }

    internal static float[] Tone(int rate, double hz, double seconds, float amplitude, double burstSeconds = 0, double gapSeconds = 0)
    {
        var samples = new float[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)rate;
            var on = burstSeconds <= 0 || (t % (burstSeconds + gapSeconds)) < burstSeconds;
            samples[i] = on ? amplitude * MathF.Sin((float)(2 * Math.PI * hz * t)) : 0;
        }

        return samples;
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

    internal sealed class ArrayReader(float[] samples, AudioFormat format) : IAudioSampleReader
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
