using NAudio.Wave;
using Kakitome.Application.Audio;
using Kakitome.Application.Recording;
using Kakitome.Storage.Audio;

namespace Kakitome.Infrastructure.Audio;

/// <summary>
/// Decodes compressed audio and the audio track of video files (MP3, AAC/M4A, WMA, FLAC, MP4/MOV, …) with Windows
/// Media Foundation, as float samples. No bundled codec binaries are needed (ADR-024).
/// </summary>
public sealed class MediaFoundationSampleReader : IAudioSampleReader
{
    private readonly MediaFoundationReader _reader;
    private readonly ISampleProvider _samples;

    public MediaFoundationSampleReader(string path)
    {
        try
        {
            _reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            throw new NotSupportedException($"'{Path.GetFileName(path)}' has no audio Windows can decode.", ex);
        }

        _samples = _reader.ToSampleProvider();
        Format = new AudioFormat(_samples.WaveFormat.SampleRate, _samples.WaveFormat.Channels);
        TotalFrames = _reader.Length / Math.Max(1, _reader.WaveFormat.BlockAlign);
    }

    public AudioFormat Format { get; }

    public long TotalFrames { get; }

    public int Read(Span<float> buffer)
    {
        var count = buffer.Length - (buffer.Length % Format.Channels);
        var read = _samples.Read(buffer[..count]);
        return read / Format.Channels;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>Kakitome's own WAV files are read directly; everything else goes through Media Foundation.</summary>
public sealed class MediaAudioSampleReaderFactory : IAudioSampleReaderFactory
{
    public IAudioSampleReader Open(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new WavSampleReader(path);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
                // e.g. 24-bit or ADPCM WAV: let Media Foundation decode it.
            }
        }

        return new MediaFoundationSampleReader(path);
    }
}
