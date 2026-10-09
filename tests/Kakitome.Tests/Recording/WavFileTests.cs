using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using Kakitome.Application.Recording;
using Kakitome.Storage.Audio;

namespace Kakitome.Tests.Recording;

public sealed class WavFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "KakitomeTests", "wav-" + Guid.NewGuid().ToString("N"));

    public WavFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Completed_file_is_a_standard_float_wav()
    {
        var path = Path.Combine(_dir, "a.wav");
        using (var writer = new WavFileWriter(path, AudioFormat.Loopback))
        {
            writer.Write(Frames(AudioFormat.Loopback, 48_000, 0.5f));
            writer.WriteSilence(24_000);
            writer.Complete();
        }

        using var reader = new WaveFileReader(path);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
        Assert.Equal(48_000, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(1.5, reader.TotalTime.TotalSeconds, 3);

        var first = reader.ReadNextSampleFrame();
        Assert.Equal(0.5f, first[0]);
    }

    [Fact]
    public void Header_follows_checkpoints_and_crash_loses_nothing_after_repair()
    {
        var path = Path.Combine(_dir, "crash.wav");
        var writer = new WavFileWriter(path, AudioFormat.Microphone);
        writer.Write(Frames(AudioFormat.Microphone, 48_000));
        writer.Checkpoint();
        Assert.Equal(1.0, HeaderSeconds(path), 3);

        // More audio reaches the file but the app "crashes" before the next checkpoint.
        writer.Write(Frames(AudioFormat.Microphone, 24_000));
        writer.Dispose();
        Assert.Equal(1.0, HeaderSeconds(path), 3);

        var result = new WavRepair().Repair(path);

        Assert.True(result.Changed);
        Assert.Equal(1.5, result.DurationSeconds, 3);
        using var reader = new WaveFileReader(path);
        Assert.Equal(1.5, reader.TotalTime.TotalSeconds, 3);
    }

    [Fact]
    public void Repair_ignores_a_trailing_partial_frame_and_is_idempotent()
    {
        var path = Path.Combine(_dir, "partial.wav");
        using (var writer = new WavFileWriter(path, AudioFormat.Loopback))
        {
            writer.Write(Frames(AudioFormat.Loopback, 4_800));
        }

        using (var fs = new FileStream(path, FileMode.Append))
        {
            fs.Write([1, 2, 3]); // torn write
        }

        var repair = new WavRepair();
        Assert.Equal(0.1, repair.Repair(path).DurationSeconds, 4);
        Assert.False(repair.Repair(path).Changed);
        Assert.Equal(WavFileWriter.DataOffset + (4_800 * 8) + 3, new FileInfo(path).Length); // audio bytes untouched
    }

    [Fact]
    public void Large_files_switch_to_RF64()
    {
        var path = Path.Combine(_dir, "big.wav");
        using (var writer = new WavFileWriter(path, AudioFormat.Microphone, rf64ThresholdBytes: 1000))
        {
            writer.Write(Frames(AudioFormat.Microphone, 1_000));
            writer.Complete();
            Assert.True(writer.IsRf64);
        }

        var header = File.ReadAllBytes(path).AsSpan(0, WavFileWriter.DataOffset);
        Assert.Equal("RF64", Encoding.ASCII.GetString(header[..4]));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
        Assert.Equal("ds64", Encoding.ASCII.GetString(header.Slice(12, 4)));
        Assert.Equal(4_000, BinaryPrimitives.ReadInt64LittleEndian(header[(20 + 8)..]));
        Assert.Equal(1_000, BinaryPrimitives.ReadInt64LittleEndian(header[(20 + 16)..]));

        // Repair keeps the RF64 form.
        Assert.False(new WavRepair().Repair(path).Changed);
    }

    [Fact]
    public void Repair_refuses_foreign_files_without_touching_them()
    {
        var path = Path.Combine(_dir, "foreign.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(44_100, 16, 1)))
        {
            writer.Write(new byte[882], 0, 882);
        }

        var before = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => new WavRepair().Repair(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Writer_never_overwrites_an_existing_file()
    {
        var path = Path.Combine(_dir, "exists.wav");
        File.WriteAllText(path, "precious");

        Assert.Throws<IOException>(() => new WavFileWriter(path, AudioFormat.Microphone));
        Assert.Equal("precious", File.ReadAllText(path));
    }

    private static byte[] Frames(AudioFormat format, int frames, float value = 0.25f)
    {
        var samples = new float[frames * format.Channels];
        Array.Fill(samples, value);
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static double HeaderSeconds(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[WavFileWriter.DataOffset];
        fs.ReadExactly(header);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(68));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(90));
        return (double)dataSize / blockAlign / rate;
    }
}
