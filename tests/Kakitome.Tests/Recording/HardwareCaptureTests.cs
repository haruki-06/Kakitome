using NAudio.Wave;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Infrastructure.Audio;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Recording;

/// <summary>
/// Exercises real WASAPI endpoints on the machine running the tests. Skipped (not failed) when the machine has no
/// such device; the untested matrix is recorded in the benchmark/test report.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class HardwareCaptureTests
{
    [Fact]
    public async Task Real_microphone_and_system_audio_record_to_aligned_files()
    {
        using var catalog = new WasapiDeviceCatalog();
        Assert.SkipWhen(catalog.GetMicrophones().Count == 0, "No microphone on this machine.");
        var hasOutput = catalog.GetOutputDevices().Count > 0;

        await using var f = await LibraryFixture.CreateAsync(realCapture: new WasapiCaptureFactory());
        var session = await f.Recording.StartAsync(new RecordingOptions { Project = "HW", IncludeSystemAudio = hasOutput });
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var status = session.GetStatus();
        await session.StopAsync();

        Assert.All(status.Streams, s => Assert.Equal(StreamState.Active, s.State));
        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(CaptureStatus.Completed, metadata.Capture!.Status);
        Assert.Empty(metadata.Capture.Events);

        foreach (var stream in metadata.Audio)
        {
            using var reader = new WaveFileReader(Path.Combine(session.FolderPath, stream.FileName));
            Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
            Assert.Equal(48_000, reader.WaveFormat.SampleRate);
            Assert.InRange(reader.TotalTime.TotalSeconds, session.Elapsed.TotalSeconds - 0.35, session.Elapsed.TotalSeconds + 0.35);
        }
    }

    [Fact]
    public void Device_catalog_reports_defaults_first()
    {
        using var catalog = new WasapiDeviceCatalog();
        var mics = catalog.GetMicrophones();
        Assert.SkipWhen(mics.Count == 0, "No microphone on this machine.");

        Assert.True(mics[0].IsDefault);
        Assert.All(mics, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        Assert.True(catalog.IsApplicationCaptureSupported);
    }

    [Fact]
    public void Audio_applications_are_listed_without_system_sounds_or_kakitome_itself()
    {
        using var catalog = new WasapiDeviceCatalog();
        Assert.SkipWhen(catalog.GetOutputDevices().Count == 0, "No output device on this machine.");

        var apps = catalog.GetAudioApplications();

        Assert.DoesNotContain(apps, a => a.ProcessId == 0 || a.ProcessId == Environment.ProcessId);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(apps.Count, apps.Select(a => a.ProcessId).Distinct().Count());
        Assert.Equal(apps.OrderByDescending(a => a.IsPlaying).Select(a => a.IsPlaying), apps.Select(a => a.IsPlaying)); // playing first
    }

    [Fact]
    public async Task Application_loopback_opens_for_a_process()
    {
        using var catalog = new WasapiDeviceCatalog();
        Assert.SkipUnless(catalog.IsApplicationCaptureSupported, "Process loopback is not supported on this OS build.");
        Assert.SkipWhen(catalog.GetOutputDevices().Count == 0, "No audio output device on this machine.");

        // Capturing this (silent) test process exercises activation + initialization of process loopback.
        using var source = await new WasapiCaptureFactory().CreateAsync(
            new CaptureSourceRequest(CaptureSourceKind.Application, ProcessId: Environment.ProcessId, ProcessName: "tests"),
            AudioFormat.Loopback,
            TestContext.Current.CancellationToken);
        var sink = new CountingSink();
        source.StartCapture(sink);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        source.StopCapture();

        Assert.Equal(CaptureSourceKind.Application, source.Kind);
        Assert.Equal("tests", source.DisplayName);
    }

    [Fact]
    public async Task Captured_samples_carry_real_audio_not_zeros()
    {
        using var catalog = new WasapiDeviceCatalog();
        Assert.SkipUnless(catalog.IsApplicationCaptureSupported, "Process loopback is not supported on this OS build.");
        Assert.SkipWhen(catalog.GetOutputDevices().Count == 0, "No audio output device on this machine.");

        // Plays a quiet (-34 dBFS) 440 Hz tone from this process and captures exactly this process' output, proving
        // the capture path delivers the actual signal (format conversion, buffer handling) rather than silence.
        using var source = await new WasapiCaptureFactory().CreateAsync(
            new CaptureSourceRequest(CaptureSourceKind.Application, ProcessId: Environment.ProcessId, ProcessName: "tests"),
            AudioFormat.Loopback,
            TestContext.Current.CancellationToken);
        var sink = new CountingSink();
        source.StartCapture(sink);

        var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48_000, 2)
        {
            Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin,
            Frequency = 440,
            Gain = 0.02,
        };
        using (var output = new NAudio.Wave.WasapiPlayerBuilder().WithSharedMode().Build())
        {
            output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(
                new NAudio.Wave.SampleProviders.OffsetSampleProvider(tone) { Take = TimeSpan.FromSeconds(1) }));
            output.Play();
            await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        }

        source.StopCapture();

        // Process loopback is post-volume (system/session volume applies), so only bound it: clearly present and
        // never louder than the source.
        Assert.True(sink.Bytes > 0, "no audio captured");
        Assert.InRange(sink.Peak, 0.0005f, 0.021f);
    }

    private sealed class CountingSink : IAudioSampleSink
    {
        public long Bytes { get; private set; }

        public float Peak { get; private set; }

        public void OnSamples(ReadOnlySpan<byte> interleavedFloat32)
        {
            Bytes += interleavedFloat32.Length;
            foreach (var s in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(interleavedFloat32))
            {
                Peak = Math.Max(Peak, Math.Abs(s));
            }
        }
    }

    [Fact]
    public async Task Unknown_device_is_reported_as_unavailable()
    {
        var factory = new WasapiCaptureFactory();

        await Assert.ThrowsAsync<AudioDeviceUnavailableException>(() =>
            factory.CreateAsync(new CaptureSourceRequest(CaptureSourceKind.Microphone, "{0.0.1.00000000}.{not-a-device}"), AudioFormat.Microphone, TestContext.Current.CancellationToken));
    }
}
