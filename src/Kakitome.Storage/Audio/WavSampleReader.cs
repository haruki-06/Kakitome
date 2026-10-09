using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Kakitome.Application.Audio;
using Kakitome.Application.Recording;

namespace Kakitome.Storage.Audio;

/// <summary>
/// Reads RIFF/RF64 WAV files with 32-bit float or 16-bit PCM samples as interleaved floats. The data length is
/// taken from the file size when the header is stale (crash) or RF64, so every sample on disk is readable.
/// </summary>
public sealed class WavSampleReader : IAudioSampleReader
{
    private readonly FileStream _stream;
    private readonly int _bitsPerSample;
    private readonly long _dataEnd;
    private byte[] _raw = [];

    public WavSampleReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024);
        try
        {
            (Format, _bitsPerSample, var dataStart, var declared) = ParseHeader(_stream, path);
            var available = _stream.Length - dataStart;
            var length = declared is { } d && d <= available ? d : available;
            var bytesPerFrame = Format.Channels * (_bitsPerSample / 8);
            length -= length % bytesPerFrame;
            TotalFrames = length / bytesPerFrame;
            _dataEnd = dataStart + length;
            _stream.Position = dataStart;
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public AudioFormat Format { get; }

    public long TotalFrames { get; }

    public int Read(Span<float> buffer)
    {
        var channels = Format.Channels;
        var bytesPerSample = _bitsPerSample / 8;
        var framesWanted = buffer.Length / channels;
        var bytesLeft = _dataEnd - _stream.Position;
        var bytes = (int)Math.Min(bytesLeft, (long)framesWanted * channels * bytesPerSample);
        bytes -= bytes % (channels * bytesPerSample);
        if (bytes <= 0)
        {
            return 0;
        }

        if (_raw.Length < bytes)
        {
            _raw = new byte[bytes];
        }

        _stream.ReadExactly(_raw, 0, bytes);
        var samples = bytes / bytesPerSample;
        if (_bitsPerSample == 32)
        {
            MemoryMarshal.Cast<byte, float>(_raw.AsSpan(0, bytes)).CopyTo(buffer);
        }
        else
        {
            var pcm = MemoryMarshal.Cast<byte, short>(_raw.AsSpan(0, bytes));
            for (var i = 0; i < samples; i++)
            {
                buffer[i] = pcm[i] / 32768f;
            }
        }

        return samples / channels;
    }

    public void Dispose() => _stream.Dispose();

    private static (AudioFormat Format, int Bits, long DataStart, long? DeclaredLength) ParseHeader(FileStream stream, string path)
    {
        Span<byte> head = stackalloc byte[12];
        stream.ReadExactly(head);
        var riff = Encoding.ASCII.GetString(head[..4]);
        if (riff is not ("RIFF" or "RF64") || Encoding.ASCII.GetString(head[8..12]) != "WAVE")
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a WAV file.");
        }

        long? ds64Data = null;
        AudioFormat? format = null;
        var bits = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            var id = Encoding.ASCII.GetString(chunk[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var bodyStart = stream.Position;
            switch (id)
            {
                case "ds64":
                {
                    Span<byte> body = stackalloc byte[16];
                    stream.ReadExactly(body);
                    ds64Data = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
                    break;
                }

                case "fmt ":
                {
                    Span<byte> body = stackalloc byte[16];
                    stream.ReadExactly(body);
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                    var supported = (tag == 3 && bits == 32) || (tag == 1 && bits == 16) || (tag == 0xFFFE && bits is 16 or 32);
                    if (!supported || channels is 0 or > 8 || rate <= 0)
                    {
                        throw new NotSupportedException($"Unsupported WAV encoding (tag {tag}, {bits} bit).");
                    }

                    format = new AudioFormat(rate, channels);
                    break;
                }

                case "data":
                {
                    if (format is null)
                    {
                        throw new InvalidDataException("WAV data chunk precedes the format chunk.");
                    }

                    long? declared = size == uint.MaxValue ? ds64Data : size;
                    return (format.Value, bits, bodyStart, declared == 0 ? null : declared);
                }
            }

            stream.Position = bodyStart + size + (size & 1);
        }

        throw new InvalidDataException($"'{Path.GetFileName(path)}' has no audio data.");
    }
}

public sealed class WavSampleReaderFactory : IAudioSampleReaderFactory
{
    public IAudioSampleReader Open(string path) => new WavSampleReader(path);
}
