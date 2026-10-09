using Kakitome.Application.Recording;
using Kakitome.Domain.Library;

namespace Kakitome.Application.Audio;

/// <summary>Sequential reader of interleaved float samples from an audio file.</summary>
public interface IAudioSampleReader : IDisposable
{
    AudioFormat Format { get; }

    long TotalFrames { get; }

    /// <summary>Reads up to <c>buffer.Length / Channels</c> frames; returns frames read (0 at end).</summary>
    int Read(Span<float> buffer);
}

public interface IAudioSampleReaderFactory
{
    /// <summary>Opens a file Kakitome can read directly (WAV float32 / PCM16). Throws for unsupported formats.</summary>
    IAudioSampleReader Open(string path);
}

/// <summary>Streaming level and speech-activity analysis with constant memory per minute of audio.</summary>
public static class AudioAnalyzer
{
    public const double WindowSeconds = 0.03;
    public const double SilenceDbfs = -60;
    private const double Floor = -120;

    public static AudioAnalysis Analyze(IAudioSampleReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var channels = reader.Format.Channels;
        var windowFrames = Math.Max(1, (int)(reader.Format.SampleRate * WindowSeconds));
        var buffer = new float[windowFrames * channels * 32];
        var windowLevels = new List<float>();

        double sumSquares = 0;
        long frames = 0;
        float peak = 0;
        double windowSquares = 0;
        var windowCount = 0;

        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var f = 0; f < read; f++)
            {
                // Downmix to mono for activity; peak is taken over all channels.
                double mono = 0;
                for (var c = 0; c < channels; c++)
                {
                    var sample = buffer[(f * channels) + c];
                    var abs = MathF.Abs(sample);
                    if (abs > peak)
                    {
                        peak = abs;
                    }

                    mono += sample;
                }

                mono /= channels;
                var square = mono * mono;
                sumSquares += square;
                windowSquares += square;
                frames++;
                if (++windowCount == windowFrames)
                {
                    windowLevels.Add((float)ToDb(Math.Sqrt(windowSquares / windowFrames)));
                    windowSquares = 0;
                    windowCount = 0;
                }
            }
        }

        if (frames == 0)
        {
            return new AudioAnalysis { PeakDbfs = Floor, RmsDbfs = Floor, NoiseFloorDbfs = Floor, SpeechRatio = 0, Silent = true };
        }

        var peakDb = ToDb(peak);
        var rmsDb = ToDb(Math.Sqrt(sumSquares / frames));
        windowLevels.Sort();
        var noiseFloor = windowLevels.Count == 0 ? rmsDb : windowLevels[(int)(windowLevels.Count * 0.10)];

        // "Speech" = clearly above the background and not inaudibly quiet.
        var threshold = Math.Max(noiseFloor + 10, -50);
        var active = windowLevels.Count(l => l > threshold);
        return new AudioAnalysis
        {
            PeakDbfs = Math.Round(peakDb, 1),
            RmsDbfs = Math.Round(rmsDb, 1),
            NoiseFloorDbfs = Math.Round(noiseFloor, 1),
            SpeechRatio = windowLevels.Count == 0 ? 0 : Math.Round((double)active / windowLevels.Count, 3),
            Silent = peakDb < SilenceDbfs,
        };
    }

    private static double ToDb(double amplitude) => amplitude <= 1e-6 ? Floor : Math.Max(Floor, 20 * Math.Log10(amplitude));
}
