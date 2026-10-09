using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Asr;
using Kakitome.Application.Live;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Tests.Asr;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Recording;

public sealed class LiveTranscriptTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);

    [Fact]
    public async Task Utterances_become_lines_with_partials_while_speaking()
    {
        var decoder = new FakeDecoder();
        var lines = new ConcurrentQueue<LiveSegment>();
        long id = 0;
        await using var live = new LiveTranscriber(Stereo48k, CaptureSourceKind.Microphone, decoder, () => Interlocked.Increment(ref id), maxBacklogSeconds: 1000);
        live.SegmentChanged += (_, s) => lines.Enqueue(s);

        Feed(live, silence: 1.0);
        Feed(live, speech: 1.5);
        Feed(live, silence: 1.0);
        Feed(live, speech: 5.0); // long enough for partial updates
        Feed(live, silence: 1.0);
        await live.CompleteAsync();

        var finals = lines.Where(l => l.IsFinal).ToList();
        Assert.Equal(2, finals.Count);
        Assert.InRange(finals[0].StartSeconds, 0.7, 1.05);  // speech at 1.0 s (with up to 200 ms pre-roll)
        Assert.InRange(finals[1].StartSeconds, 3.2, 3.55);  // speech at 3.5 s
        Assert.NotEqual(finals[0].Id, finals[1].Id);
        Assert.Contains(lines, l => !l.IsFinal && l.Id == finals[1].Id); // partial text before the final line
        Assert.All(finals, f => Assert.Equal(CaptureSourceKind.Microphone, f.Source));
        Assert.InRange(decoder.LastFinalSeconds, 5.0, 6.0);  // the whole 5 s utterance (+ pre-roll/pause tail)
    }

    [Fact]
    public async Task Long_speech_is_split_into_bounded_lines()
    {
        var decoder = new FakeDecoder();
        var lines = new ConcurrentQueue<LiveSegment>();
        long id = 0;
        await using var live = new LiveTranscriber(Stereo48k, CaptureSourceKind.SystemAudio, decoder, () => Interlocked.Increment(ref id), maxBacklogSeconds: 1000);
        live.SegmentChanged += (_, s) => lines.Enqueue(s);

        Feed(live, speech: 25);
        await live.CompleteAsync();

        Assert.True(lines.Count(l => l.IsFinal) >= 3);
        Assert.All(decoder.Lengths, l => Assert.True(l <= (10 * LiveTranscriber.SampleRate) + 320));
    }

    [Fact]
    public async Task Falling_behind_drops_audio_instead_of_lagging_and_failures_are_contained()
    {
        var gate = new TaskCompletionSource();
        var decoder = new FakeDecoder { Gate = gate.Task, FailFirst = true };
        long id = 0;
        await using var live = new LiveTranscriber(Stereo48k, CaptureSourceKind.Microphone, decoder, () => Interlocked.Increment(ref id), maxBacklogSeconds: 2);

        Feed(live, speech: 3); // the decoder blocks on the first partial
        Feed(live, silence: 1);
        Feed(live, speech: 10);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        gate.SetResult();
        await live.CompleteAsync();

        Assert.True(live.DroppedSeconds > 0);
    }

    [Fact]
    public async Task Recording_shows_live_lines_and_stops_cleanly()
    {
        var models = new FakeModelStore();
        models.Installed.Add(ModelCatalog.ReazonSpeechK2V2Int8);
        var provider = new NamedProvider("sherpa-onnx", new FakeAsrProvider());
        await using var f = await LibraryFixture.CreateAsync(configure: s =>
        {
            s.AddSingleton<IModelStore>(models);
            s.AddSingleton<IFinalAsrProvider>(provider);
        });
        var live = f.Services.GetRequiredService<LiveTranscriptionService>();
        var lines = new ConcurrentQueue<LiveSegment>();
        live.SegmentChanged += (_, s) => lines.Enqueue(s);

        var session = await f.Recording.StartAsync(new RecordingOptions());
        Assert.Equal("ReazonSpeech k2 v2 (Japanese, int8)", live.ModelName);
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        mic.PushSeconds(2, 0.2f);
        mic.PushSeconds(1, 0f);
        for (var i = 0; i < 200 && !lines.Any(l => l.IsFinal); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Contains(lines, l => l.IsFinal && l.Text.Length > 0);
        Assert.Equal(LiveTranscriptState.Running, live.State);

        await session.StopAsync();
        for (var i = 0; i < 100 && live.State != LiveTranscriptState.Off; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(LiveTranscriptState.Off, live.State);
        Assert.Null(await f.Library.LoadTranscriptAsync(session.Id, TestContext.Current.CancellationToken)); // nothing stored
    }

    [Fact]
    public async Task Preview_is_off_without_a_model_when_disabled_or_when_saving_power()
    {
        var models = new FakeModelStore();
        await using var f = await LibraryFixture.CreateAsync(configure: s => s.AddSingleton<IModelStore>(models));
        var live = f.Services.GetRequiredService<LiveTranscriptionService>();

        var session = await f.Recording.StartAsync(new RecordingOptions());
        Assert.Equal(LiveTranscriptState.NoModel, live.State);
        await session.StopAsync();

        models.Installed.Add(ModelCatalog.WhisperSmallQ5);
        await f.Settings.UpdateAsync(s => s.Recording.LiveTranscript = false, TestContext.Current.CancellationToken);
        session = await f.Recording.StartAsync(new RecordingOptions());
        Assert.Equal(LiveTranscriptState.Off, live.State);
        await session.StopAsync();

        await f.Settings.UpdateAsync(s => s.Recording.LiveTranscript = true, TestContext.Current.CancellationToken);
        f.SystemResources.Update(r => r with { OnAcPower = false, EnergySaverOn = true });
        session = await f.Recording.StartAsync(new RecordingOptions());
        Assert.Equal(LiveTranscriptState.PowerSaving, live.State);
        await session.StopAsync();
    }

    [Theory]
    [InlineData("ja", false, new[] { ModelCatalog.ReazonSpeechK2V2Int8, ModelCatalog.WhisperSmallQ5 })]
    [InlineData(null, true, new[] { ModelCatalog.WhisperLargeV3TurboQ5, ModelCatalog.ReazonSpeechK2V2Int8, ModelCatalog.WhisperSmallQ5 })]
    [InlineData("en", false, new[] { ModelCatalog.WhisperSmallQ5 })]
    public void Live_model_preference_favours_responsiveness(string? language, bool gpu, string[] expected) =>
        Assert.Equal(expected, LiveTranscriptionService.LivePreference(language, gpu));

    private static void Feed(LiveTranscriber live, double speech = 0, double silence = 0)
    {
        var seconds = speech > 0 ? speech : silence;
        var frames = (int)(seconds * Stereo48k.SampleRate);
        const int chunk = 480; // 10 ms
        var buffer = new float[chunk * Stereo48k.Channels];
        var random = new Random(1);
        for (var start = 0; start < frames; start += chunk)
        {
            for (var i = 0; i < chunk; i++)
            {
                var t = (start + i) / (double)Stereo48k.SampleRate;
                var value = speech > 0 ? (float)(0.2 * Math.Sin(2 * Math.PI * 220 * t)) : (float)((random.NextDouble() - 0.5) * 0.001);
                buffer[2 * i] = buffer[(2 * i) + 1] = value;
            }

            live.OnSamples(MemoryMarshal.AsBytes(buffer.AsSpan()));
            Thread.Sleep(0);
        }
    }

    private sealed class FakeDecoder : ILiveDecoder
    {
        public ConcurrentBag<int> Lengths { get; } = [];

        public double LastFinalSeconds { get; private set; }

        public Task? Gate { get; set; }

        public bool FailFirst { get; set; }

        public async Task<string> DecodeAsync(float[] samples16k, CancellationToken cancellationToken)
        {
            if (Gate is not null)
            {
                await Gate.WaitAsync(cancellationToken);
            }

            if (FailFirst)
            {
                FailFirst = false;
                throw new IOException("decoder crashed");
            }

            Lengths.Add(samples16k.Length);
            LastFinalSeconds = samples16k.Length / (double)LiveTranscriber.SampleRate;
            return $"{samples16k.Length / (double)LiveTranscriber.SampleRate:F1}s";
        }
    }

    private sealed class NamedProvider(string id, FakeAsrProvider inner) : IFinalAsrProvider
    {
        public string Id { get; } = id;

        public IReadOnlyList<AsrModelInfo> Models => inner.Models;

        public Task<IAsrSession> OpenAsync(AsrSessionOptions options, CancellationToken cancellationToken = default) => inner.OpenAsync(options, cancellationToken);
    }
}
