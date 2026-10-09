using NAudio.Wave;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Domain.Library;
using Kakitome.Domain.Serialization;
using Kakitome.Storage.Audio;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Recording;

public sealed class RecordingServiceTests
{
    [Fact]
    public async Task Microphone_recording_streams_to_disk_and_completes_metadata()
    {
        await using var f = await LibraryFixture.CreateAsync();

        var session = await f.Recording.StartAsync(new RecordingOptions { Project = "講義", Title = "第2回" });
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        Assert.True(mic.Started);
        Assert.Equal(1, f.KeepAwake.Held);

        for (var i = 0; i < 8; i++)
        {
            mic.PushSeconds(0.25);
            await f.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        }

        await session.StopAsync();

        Assert.Null(f.Recording.Current);
        Assert.Equal(RecordingState.Stopped, session.State);
        Assert.Equal(0, f.KeepAwake.Held);
        Assert.True(mic.Disposed);

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(CaptureStatus.Completed, metadata.Capture!.Status);
        Assert.Equal(2.0, metadata.DurationSeconds!.Value, 1);
        var stream = Assert.Single(metadata.Audio);
        Assert.Equal("audio.wav", stream.FileName);
        Assert.Equal(AudioStreamRole.Microphone, stream.Role);
        Assert.Equal("wav/pcm_f32le", stream.Format);
        Assert.Equal("wav", metadata.RetainedAudioFormat);

        using var reader = new WaveFileReader(Path.Combine(session.FolderPath, "audio.wav"));
        Assert.Equal(2.0, reader.TotalTime.TotalSeconds, 2);
        Assert.Equal(1, reader.WaveFormat.Channels);
    }

    [Fact]
    public async Task Project_changed_while_recording_moves_the_recording_there_on_stop()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        var startFolder = session.FolderPath;
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        mic.PushSeconds(0.5);
        await f.AdvanceAsync(TimeSpan.FromMilliseconds(500));

        session.TargetProject = "講義";
        Assert.Equal(startFolder, session.FolderPath); // nothing moves while the audio is open
        await session.StopAsync();

        Assert.False(Directory.Exists(startFolder));
        Assert.Equal("講義", Path.GetFileName(Path.GetDirectoryName(session.FolderPath)));
        Assert.True(File.Exists(Path.Combine(session.FolderPath, "audio.wav")));
        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal("講義", metadata.Project);
        Assert.StartsWith("講義 ", metadata.Title, StringComparison.Ordinal); // generated title follows the project
        Assert.Equal(CaptureStatus.Completed, metadata.Capture!.Status);
        var entry = await f.Library.FindAsync(session.Id);
        Assert.StartsWith("Projects/講義/", entry!.Folder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_to_another_project_keeps_a_user_title_and_is_a_no_op_for_the_same_project()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions { Project = "ゼミ", Title = "第3回" });
        f.Capture.Latest(CaptureSourceKind.Microphone).PushSeconds(0.25);
        await f.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        session.TargetProject = "ゼミ";
        await session.StopAsync();
        var folder = session.FolderPath;

        Assert.False(await f.Library.MoveToProjectAsync(session.Id, "ゼミ"));
        Assert.True(await f.Library.MoveToProjectAsync(session.Id, "講義"));

        Assert.False(Directory.Exists(folder));
        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal("講義", metadata.Project);
        Assert.Equal("第3回", metadata.Title);
    }

    [Fact]
    public async Task Paused_audio_is_not_recorded_and_pauses_are_logged()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);

        mic.PushSeconds(1);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        await session.PauseAsync();
        Assert.Equal(RecordingState.Paused, session.State);

        mic.PushSeconds(3); // dropped
        await f.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1.0, session.Elapsed.TotalSeconds, 1);

        await session.ResumeAsync();
        mic.PushSeconds(1);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        await session.StopAsync();

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(2.0, metadata.Audio[0].DurationSeconds!.Value, 1);
        Assert.Equal(
            [CaptureEventKind.Paused, CaptureEventKind.Resumed],
            metadata.Capture!.Events.Select(e => e.Kind));
        Assert.Equal(1.0, metadata.Capture.Events[0].OffsetSeconds, 1);
    }

    [Fact]
    public async Task System_audio_is_a_separate_stream_kept_aligned_while_silent()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions { IncludeSystemAudio = true });
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        var system = f.Capture.Latest(CaptureSourceKind.SystemAudio);

        // Loopback delivers nothing while nothing plays; the microphone runs continuously.
        for (var i = 0; i < 12; i++)
        {
            mic.PushSeconds(0.25);
            await f.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        }

        system.PushSeconds(0.1, 0.9f);
        await session.StopAsync();

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(["audio.wav", "audio.system.wav"], metadata.Audio.Select(a => a.FileName));
        Assert.Equal(AudioStreamRole.SystemAudio, metadata.Audio[1].Role);
        Assert.Equal(2, metadata.Audio[1].Channels);

        using var micReader = new WaveFileReader(Path.Combine(session.FolderPath, "audio.wav"));
        using var sysReader = new WaveFileReader(Path.Combine(session.FolderPath, "audio.system.wav"));
        Assert.Equal(micReader.TotalTime.TotalSeconds, sysReader.TotalTime.TotalSeconds, 0);
        Assert.InRange(sysReader.TotalTime.TotalSeconds, 2.8, 3.2);
    }

    [Fact]
    public async Task Unplugged_device_reconnects_into_the_same_file_with_the_gap_filled()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions { MicrophoneId = "mic-usb" });
        var first = f.Capture.Latest(CaptureSourceKind.Microphone);
        first.PushSeconds(1);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));

        f.Capture.Available.Remove("mic-usb");
        first.Fail();
        await f.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamState.Reconnecting, session.GetStatus().Streams[0].State);
        Assert.Equal(RecordingState.Recording, session.State);

        f.Capture.Available.Add("mic-usb");
        await f.AdvanceAsync(TimeSpan.FromSeconds(1.5));
        await WaitUntilAsync(() => session.GetStatus().Streams[0].State == StreamState.Active);
        var second = f.Capture.Latest(CaptureSourceKind.Microphone);
        Assert.NotSame(first, second);
        Assert.True(first.Disposed);

        second.PushSeconds(1);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        await session.StopAsync();

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(CaptureStatus.Completed, metadata.Capture!.Status);
        var kinds = metadata.Capture.Events.Select(e => e.Kind).ToList();
        Assert.Equal([CaptureEventKind.DeviceLost, CaptureEventKind.DeviceRestored], kinds);
        Assert.Single(Directory.GetFiles(session.FolderPath, "*.wav"));

        using var reader = new WaveFileReader(Path.Combine(session.FolderPath, "audio.wav"));
        Assert.InRange(reader.TotalTime.TotalSeconds, session.Elapsed.TotalSeconds - 0.3, session.Elapsed.TotalSeconds + 0.3);
    }

    [Fact]
    public async Task Missing_device_falls_back_to_the_default_after_a_while()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions { MicrophoneId = "mic-usb" });
        f.Capture.Available.Remove("mic-usb");
        f.Capture.Latest(CaptureSourceKind.Microphone).Fail();

        await f.AdvanceAsync(StreamRecorder.FallbackToDefaultAfter + TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.GetStatus().Streams[0].State == StreamState.Active);

        Assert.Equal("Default Microphone", session.GetStatus().Streams[0].DisplayName);
        await session.StopAsync();
        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Contains(metadata.Capture!.Events, e => e.Kind == CaptureEventKind.DeviceSwitched);
        Assert.Equal("Default Microphone", metadata.Audio[0].Device);
    }

    [Fact]
    public async Task Cancel_moves_the_recording_to_the_Recycle_Bin()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        f.Capture.Latest(CaptureSourceKind.Microphone).PushSeconds(1);

        await session.CancelAsync();

        Assert.Equal(RecordingState.Cancelled, session.State);
        Assert.False(Directory.Exists(session.FolderPath));
        var recycled = Assert.Single(f.RecycleBin.Recycled);
        Assert.True(File.Exists(Path.Combine(recycled, "audio.wav")));
        Assert.Null(await f.Library.FindAsync(session.Id));
        Assert.Null(f.Recording.Current);
    }

    [Fact]
    public async Task Cancel_keeps_the_audio_when_the_Recycle_Bin_is_unavailable()
    {
        await using var f = await LibraryFixture.CreateAsync();
        f.RecycleBin.Works = false;
        var session = await f.Recording.StartAsync(new RecordingOptions());
        f.Capture.Latest(CaptureSourceKind.Microphone).PushSeconds(1);

        await session.CancelAsync();

        Assert.True(File.Exists(Path.Combine(session.FolderPath, "audio.wav")));
        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(CaptureStatus.Cancelled, metadata.Capture!.Status);
    }

    [Fact]
    public async Task Start_is_refused_without_enough_disk_space_and_nothing_is_created()
    {
        await using var f = await LibraryFixture.CreateAsync();
        f.Disk.AvailableBytes = RecordingLimits.MinFreeBytesToStart - 1;

        var ex = await Assert.ThrowsAsync<RecordingStartException>(() => f.Recording.StartAsync(new RecordingOptions()));

        Assert.Equal(RecordingStartFailure.InsufficientDiskSpace, ex.Failure);
        Assert.Empty(f.Store.EnumerateRecordingFolders());
        Assert.Empty(f.Capture.Created);
    }

    [Fact]
    public async Task Start_with_a_missing_device_creates_nothing()
    {
        await using var f = await LibraryFixture.CreateAsync();

        var ex = await Assert.ThrowsAsync<RecordingStartException>(() =>
            f.Recording.StartAsync(new RecordingOptions { MicrophoneId = "not-plugged-in" }));

        Assert.Equal(RecordingStartFailure.DeviceUnavailable, ex.Failure);
        Assert.Empty(f.Store.EnumerateRecordingFolders());
    }

    [Fact]
    public async Task Only_one_recording_at_a_time_and_no_sources_is_rejected()
    {
        await using var f = await LibraryFixture.CreateAsync();

        var none = await Assert.ThrowsAsync<RecordingStartException>(() =>
            f.Recording.StartAsync(new RecordingOptions { IncludeMicrophone = false }));
        Assert.Equal(RecordingStartFailure.NoSources, none.Failure);

        var session = await f.Recording.StartAsync(new RecordingOptions());
        var again = await Assert.ThrowsAsync<RecordingStartException>(() => f.Recording.StartAsync(new RecordingOptions()));
        Assert.Equal(RecordingStartFailure.AlreadyRecording, again.Failure);
        await session.StopAsync();
    }

    [Fact]
    public async Task Low_disk_space_while_recording_stops_and_keeps_the_audio()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        mic.PushSeconds(1);

        f.Disk.AvailableBytes = RecordingLimits.MinFreeBytesWhileRecording - 1;
        await f.AdvanceAsync(RecordingLimits.MetadataInterval + TimeSpan.FromSeconds(1));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal(CaptureStatus.Completed, metadata.Capture!.Status);
        Assert.Contains("disk", metadata.Capture.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(metadata.Capture.Events, e => e.Kind == CaptureEventKind.LowDiskSpace);
        Assert.True(File.Exists(Path.Combine(session.FolderPath, "audio.wav")));
    }

    [Fact]
    public async Task System_sleep_suspends_and_wake_resumes_without_counting_the_sleep()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));

        f.Power.Suspend();
        await WaitUntilAsync(() => session.State == RecordingState.Suspended);
        await f.AdvanceAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        f.Power.Resume();
        await WaitUntilAsync(() => session.State == RecordingState.Recording);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        await session.StopAsync();

        Assert.InRange(session.Elapsed.TotalSeconds, 1.5, 2.5);
        var metadata = await f.Library.GetMetadataAsync(session.Id);
        Assert.Equal([CaptureEventKind.SystemSleep, CaptureEventKind.SystemWake], metadata.Capture!.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Crash_during_recording_is_recovered_at_next_startup()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P", RecordedAt = Samples.Jst });
        var folder = await f.Library.GetRecordingPathAsync(entry.Id);
        await f.Library.UpdateMetadataAsync(entry.Id, m => m.Audio =
        [
            new AudioStreamInfo { FileName = "audio.wav", Role = AudioStreamRole.Microphone, SampleRate = 48_000, Channels = 1 },
        ]);

        // The app wrote 3 s of audio, checkpointed after 1 s, then the process died.
        var writer = new WavFileWriter(Path.Combine(folder, "audio.wav"), AudioFormat.Microphone);
        writer.Write(new byte[48_000 * 4]);
        writer.Checkpoint();
        writer.Write(new byte[2 * 48_000 * 4]);
        writer.Dispose();
        var audioBefore = new FileInfo(Path.Combine(folder, "audio.wav")).Length;

        await f.RestartAsync();
        var recovered = await f.RecordingRecovery.RecoverAsync();

        Assert.Equal(1, recovered);
        var metadata = await f.Library.GetMetadataAsync(entry.Id);
        Assert.Equal(CaptureStatus.Interrupted, metadata.Capture!.Status);
        Assert.Equal(3.0, metadata.DurationSeconds!.Value, 2);
        Assert.Contains(metadata.Capture.Events, e => e.Kind == CaptureEventKind.Recovered);
        Assert.Equal(audioBefore, new FileInfo(Path.Combine(folder, "audio.wav")).Length);
        using var reader = new WaveFileReader(Path.Combine(folder, "audio.wav"));
        Assert.Equal(3.0, reader.TotalTime.TotalSeconds, 2);

        Assert.Equal(0, await f.RecordingRecovery.RecoverAsync());
    }

    [Fact]
    public async Task Metadata_is_checkpointed_while_recording_for_crash_safety()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var session = await f.Recording.StartAsync(new RecordingOptions());
        var mic = f.Capture.Latest(CaptureSourceKind.Microphone);
        for (var i = 0; i < 12; i++)
        {
            mic.PushSeconds(1);
            await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        }

        RecordingMetadata ReadOnDisk()
        {
            // The checkpoint replaces the file atomically while we read, like any external reader might; retry briefly.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return LibraryJson.DeserializeMetadata(File.ReadAllBytes(Path.Combine(session.FolderPath, LibraryLayout.MetadataFile)));
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(10);
                }
            }
        }

        // The background loop persists progress asynchronously; allow it to land.
        await WaitUntilAsync(() => ReadOnDisk().DurationSeconds >= 9);
        Assert.Equal(CaptureStatus.InProgress, ReadOnDisk().Capture!.Status);
        await session.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not reached");
    }
}
