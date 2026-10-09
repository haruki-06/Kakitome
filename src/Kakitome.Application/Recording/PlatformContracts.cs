namespace Kakitome.Application.Recording;

/// <summary>Streams canonical float PCM to a crash-tolerant audio file.</summary>
public interface IAudioFileWriter : IDisposable
{
    string Path { get; }

    AudioFormat Format { get; }

    long FramesWritten { get; }

    void Write(ReadOnlySpan<byte> interleavedFloat32);

    void WriteSilence(long frames);

    /// <summary>Flushes to disk and makes the header describe everything written so far.</summary>
    void Checkpoint();

    /// <summary>Final checkpoint and close. After a crash (no Complete) the file can be repaired.</summary>
    void Complete();
}

public interface IAudioFileWriterFactory
{
    /// <summary>File extension produced by <see cref="Create"/> (without dot), e.g. <c>wav</c>.</summary>
    string Extension { get; }

    /// <summary>Codec/container label stored in metadata, e.g. <c>wav/pcm_f32le</c>.</summary>
    string FormatLabel { get; }

    IAudioFileWriter Create(string path, AudioFormat format);
}

/// <summary>Repairs audio files left behind by a crash or power loss.</summary>
public interface IAudioFileRepair
{
    /// <summary>Makes the header match the audio actually on disk. Never removes audio.</summary>
    AudioRepairResult Repair(string path);
}

public sealed record AudioRepairResult(bool Changed, double DurationSeconds, AudioFormat? Format);

/// <summary>Keeps the system from idle-sleeping while a recording is running.</summary>
public interface IKeepAwake
{
    /// <summary>Returns a handle; disposing it releases the request.</summary>
    IDisposable Acquire(string reason);
}

/// <summary>System suspend/resume notifications.</summary>
public interface IPowerEvents
{
    event EventHandler? Suspending;

    event EventHandler? Resumed;
}

public interface IDiskSpaceProbe
{
    /// <summary>Free bytes available to the current user on the volume containing <paramref name="path"/>.</summary>
    long GetAvailableBytes(string path);
}

/// <summary>Moves a folder to the Recycle Bin (user-recoverable). Never deletes permanently.</summary>
public interface IRecycleBin
{
    /// <summary>Returns false (and leaves the folder in place) when the Recycle Bin cannot take it.</summary>
    bool TryMoveToRecycleBin(string folderPath);
}
