using NAudio.MediaFoundation;
using NAudio.Wave;
using Kakitome.Application.Audio;

namespace Kakitome.Infrastructure.Audio;

/// <summary>
/// Retention encoding with the Windows Media Foundation AAC (M4A) and MP3 encoders, which ship with Windows (no FFmpeg,
/// ADR-024). Input is Kakitome's float capture, fed as 16-bit PCM at 44.1/48 kHz, at most stereo (encoder limits).
/// </summary>
public sealed class MediaFoundationAudioEncoder(IAudioSampleReaderFactory readers) : IAudioEncoder
{
    internal const int BitRate = 192_000;

    public Task EncodeAsync(string sourcePath, string targetPath, EncodedAudioFormat format, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            MediaFoundationApi.Startup();
            using var reader = readers.Open(sourcePath);
            var provider = new Pcm16Provider(reader, cancellationToken);
            try
            {
                if (format == EncodedAudioFormat.M4a)
                {
                    MediaFoundationEncoder.EncodeToAac(provider, targetPath, BitRate);
                }
                else
                {
                    MediaFoundationEncoder.EncodeToMp3(provider, targetPath, BitRate);
                }
            }
            catch
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                throw;
            }
        }, cancellationToken);

    /// <summary>Float frames → 16-bit PCM; resamples to 48 kHz when needed and folds more than two channels to stereo.</summary>
    internal sealed class Pcm16Provider : IWaveProvider
    {
        private readonly IAudioSampleReader _reader;
        private readonly CancellationToken _cancellationToken;
        private readonly int _inChannels;
        private readonly double _step;
        private const int BlockFrames = 4096;
        private float[] _in = [];
        private int _blockFrames;
        private int _blockIndex;
        private double _position;
        private float[] _previous;
        private float[] _current;
        private bool _ended;

        public Pcm16Provider(IAudioSampleReader reader, CancellationToken cancellationToken)
        {
            _reader = reader;
            _cancellationToken = cancellationToken;
            _inChannels = reader.Format.Channels;
            var outChannels = Math.Min(2, _inChannels);
            var outRate = reader.Format.SampleRate is 44_100 or 48_000 ? reader.Format.SampleRate : 48_000;
            _step = reader.Format.SampleRate / (double)outRate;
            _previous = new float[outChannels];
            _current = new float[outChannels];
            WaveFormat = new WaveFormat(outRate, 16, outChannels);
            _ended = !ReadFrame(_current);
            Array.Copy(_current, _previous, outChannels);
            _ended = _ended || !ReadFrame(_current);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var channels = WaveFormat.Channels;
            var frames = buffer.Length / (2 * channels);
            var written = 0;
            for (var f = 0; f < frames && !_ended; f++)
            {
                // Linear interpolation is enough here: rates other than 44.1/48 kHz are rare for captures.
                var t = (float)(_position - Math.Floor(_position));
                for (var c = 0; c < channels; c++)
                {
                    var sample = (_previous[c] * (1 - t)) + (_current[c] * t);
                    var value = (short)Math.Clamp(Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
                    buffer[written] = (byte)value;
                    buffer[written + 1] = (byte)(value >> 8);
                    written += 2;
                }

                _position += _step;
                while (_position >= 1 && !_ended)
                {
                    _position -= 1;
                    (_previous, _current) = (_current, _previous);
                    _ended = !ReadFrame(_current);
                }
            }

            return written;
        }

        private bool ReadFrame(float[] frame)
        {
            if (_blockIndex >= _blockFrames)
            {
                if (_in.Length == 0)
                {
                    _in = new float[BlockFrames * _inChannels];
                }

                _blockFrames = _reader.Read(_in);
                _blockIndex = 0;
                if (_blockFrames == 0)
                {
                    return false;
                }
            }

            var at = _blockIndex++ * _inChannels;
            if (_inChannels <= 2)
            {
                Array.Copy(_in, at, frame, 0, _inChannels);
                return true;
            }

            // Fold extra channels into left/right (even → left, odd → right).
            frame[0] = frame[1] = 0;
            for (var c = 0; c < _inChannels; c++)
            {
                frame[c % 2] += _in[at + c] * 2f / _inChannels;
            }

            return true;
        }
    }
}
