using System.Buffers.Binary;
using System.Text;
using Kakitome.Application.Recording;

namespace Kakitome.Storage.Audio;

/// <summary>
/// 32-bit float WAV writer designed for long, crash-tolerant recordings:
/// <list type="bullet">
/// <item>audio is appended as it arrives (nothing is held in memory);</item>
/// <item>each <see cref="Checkpoint"/> fsyncs and rewrites the header, so after a crash at most the last second
/// needs <see cref="WavRepair"/>;</item>
/// <item>a reserved <c>JUNK</c> chunk becomes <c>ds64</c> and the file becomes RF64 (EBU Tech 3306) once it passes
/// 4 GiB, so multi-hour stereo recordings keep working.</item>
/// </list>
/// Layout: RIFF(12) JUNK/ds64(36) fmt(26) fact(12) data(8) samples...
/// </summary>
public sealed class WavFileWriter : IAudioFileWriter
{
    internal const int Ds64Offset = 12;
    internal const int FmtOffset = 48;
    internal const int FactOffset = 74;
    internal const int DataHeaderOffset = 86;
    internal const int DataOffset = 94;
    internal const int HeaderBytesBeforeData = DataOffset - 8;

    private static readonly byte[] Zeros = new byte[64 * 1024];

    private readonly FileStream _stream;
    private readonly long _rf64Threshold;
    private long _dataBytes;
    private bool _completed;

    public WavFileWriter(string path, AudioFormat format)
        : this(path, format, uint.MaxValue - HeaderBytesBeforeData)
    {
    }

    /// <param name="rf64ThresholdBytes">Data size above which the file switches to RF64 (lowered in tests).</param>
    internal WavFileWriter(string path, AudioFormat format, long rf64ThresholdBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (format.SampleRate <= 0 || format.Channels is <= 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio format.");
        }

        Path = path;
        Format = format;
        _rf64Threshold = rf64ThresholdBytes;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.None);
        var header = new byte[DataOffset];
        WavHeader.Write(header, format, dataBytes: 0, rf64: false);
        _stream.Write(header);
        _stream.Flush(flushToDisk: true);
    }

    public string Path { get; }

    public AudioFormat Format { get; }

    public long FramesWritten => _dataBytes / Format.BytesPerFrame;

    public bool IsRf64 { get; private set; }

    public void Write(ReadOnlySpan<byte> interleavedFloat32)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var usable = interleavedFloat32.Length - (interleavedFloat32.Length % Format.BytesPerFrame);
        _stream.Write(interleavedFloat32[..usable]);
        _dataBytes += usable;
    }

    public void WriteSilence(long frames)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var remaining = frames * Format.BytesPerFrame;
        while (remaining > 0)
        {
            var n = (int)Math.Min(remaining, Zeros.Length);
            _stream.Write(Zeros, 0, n);
            remaining -= n;
            _dataBytes += n;
        }
    }

    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _stream.Flush(flushToDisk: true);
        UpdateHeader();
        _stream.Flush(flushToDisk: false);
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _stream.Flush(flushToDisk: true);
        UpdateHeader();
        _stream.Flush(flushToDisk: true);
        _completed = true;
        _stream.Dispose();
    }

    /// <summary>Closes the file. Without <see cref="Complete"/> the header reflects the last checkpoint.</summary>
    public void Dispose()
    {
        _completed = true;
        _stream.Dispose();
    }

    private void UpdateHeader()
    {
        IsRf64 |= _dataBytes > _rf64Threshold;
        var header = new byte[DataOffset];
        WavHeader.Write(header, Format, _dataBytes, IsRf64);
        var position = _stream.Position;
        _stream.Position = 0;
        _stream.Write(header, 0, HeaderBytesBeforeData + 8);
        _stream.Position = position;
    }
}

/// <summary>Header encoding shared by the writer and repair.</summary>
internal static class WavHeader
{
    public static void Write(Span<byte> header, AudioFormat format, long dataBytes, bool rf64)
    {
        var frames = dataBytes / format.BytesPerFrame;
        var riffSize = WavFileWriter.HeaderBytesBeforeData + dataBytes;

        Ascii(header, 0, rf64 ? "RF64" : "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], rf64 ? uint.MaxValue : checked((uint)riffSize));
        Ascii(header, 8, "WAVE");

        Ascii(header, WavFileWriter.Ds64Offset, rf64 ? "ds64" : "JUNK");
        BinaryPrimitives.WriteUInt32LittleEndian(header[(WavFileWriter.Ds64Offset + 4)..], 28);
        var ds64 = header.Slice(WavFileWriter.Ds64Offset + 8, 28);
        ds64.Clear();
        if (rf64)
        {
            BinaryPrimitives.WriteInt64LittleEndian(ds64, riffSize);
            BinaryPrimitives.WriteInt64LittleEndian(ds64[8..], dataBytes);
            BinaryPrimitives.WriteInt64LittleEndian(ds64[16..], frames);
        }

        var fmt = header[WavFileWriter.FmtOffset..];
        Ascii(header, WavFileWriter.FmtOffset, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], 18);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[8..], 3); // WAVE_FORMAT_IEEE_FLOAT
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[10..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[12..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[16..], (uint)format.BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[20..], (ushort)format.BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[22..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[24..], 0);

        Ascii(header, WavFileWriter.FactOffset, "fact");
        BinaryPrimitives.WriteUInt32LittleEndian(header[(WavFileWriter.FactOffset + 4)..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(WavFileWriter.FactOffset + 8)..], rf64 || frames > uint.MaxValue ? uint.MaxValue : (uint)frames);

        Ascii(header, WavFileWriter.DataHeaderOffset, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(header[(WavFileWriter.DataHeaderOffset + 4)..], rf64 ? uint.MaxValue : checked((uint)dataBytes));
    }

    private static void Ascii(Span<byte> target, int offset, string text) => Encoding.ASCII.GetBytes(text, target[offset..]);
}

/// <summary>
/// Rewrites the header of a Kakitome WAV/RF64 file to cover all whole frames on disk (after a crash the header
/// may describe only up to the last checkpoint). Audio bytes are never modified or removed.
/// </summary>
public sealed class WavRepair : IAudioFileRepair
{
    public AudioRepairResult Repair(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var existing = new byte[WavFileWriter.DataOffset];
        if (stream.Length < WavFileWriter.DataOffset || stream.Read(existing) != existing.Length)
        {
            throw new InvalidDataException($"'{path}' is too short to be a Kakitome WAV file.");
        }

        var format = ReadFormat(existing, path);
        var dataBytes = stream.Length - WavFileWriter.DataOffset;
        dataBytes -= dataBytes % format.BytesPerFrame;
        var rf64 = dataBytes > uint.MaxValue - WavFileWriter.HeaderBytesBeforeData
            || Encoding.ASCII.GetString(existing, 0, 4) == "RF64";

        var header = new byte[WavFileWriter.DataOffset];
        WavHeader.Write(header, format, dataBytes, rf64);
        var changed = !header.AsSpan().SequenceEqual(existing);
        if (changed)
        {
            stream.Position = 0;
            stream.Write(header);
            stream.Flush(flushToDisk: true);
        }

        return new AudioRepairResult(changed, (double)(dataBytes / format.BytesPerFrame) / format.SampleRate, format);
    }

    private static AudioFormat ReadFormat(ReadOnlySpan<byte> header, string path)
    {
        var riff = Encoding.ASCII.GetString(header[..4]);
        var fmtId = Encoding.ASCII.GetString(header.Slice(WavFileWriter.FmtOffset, 4));
        var dataId = Encoding.ASCII.GetString(header.Slice(WavFileWriter.DataHeaderOffset, 4));
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(header[(WavFileWriter.FmtOffset + 8)..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(header[(WavFileWriter.FmtOffset + 22)..]);
        if (riff is not ("RIFF" or "RF64") || fmtId != "fmt " || dataId != "data" || tag != 3 || bits != 32)
        {
            throw new InvalidDataException($"'{path}' is not a Kakitome float WAV file; it was left unchanged.");
        }

        var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[(WavFileWriter.FmtOffset + 10)..]);
        var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[(WavFileWriter.FmtOffset + 12)..]);
        return new AudioFormat(rate, channels);
    }
}

public sealed class WavFileWriterFactory : IAudioFileWriterFactory
{
    public string Extension => "wav";

    public string FormatLabel => "wav/pcm_f32le";

    public IAudioFileWriter Create(string path, AudioFormat format) => new WavFileWriter(path, format);
}

public sealed class DriveDiskSpaceProbe : IDiskSpaceProbe
{
    public long GetAvailableBytes(string path)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))
            ?? throw new IOException($"Cannot determine the drive of '{path}'.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
