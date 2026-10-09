using Kakitome.Application.Audio;

namespace Kakitome.Application.Asr;

/// <summary>A span of 16 kHz mono audio to transcribe, with its absolute position in the recording.</summary>
public sealed record SpeechChunk(int Index, double StartSeconds, float[] Samples)
{
    public double DurationSeconds => Samples.Length / (double)SpeechChunker.SampleRate;
}

/// <summary>
/// Splits a recording into ASR chunks at pauses using an energy VAD (docs/03: VAD for chunking and workload
/// reduction). Chunks that contain no speech are skipped entirely; the canonical audio is never modified. Streams the
/// file, so memory stays bounded for multi-hour recordings.
/// </summary>
public static class SpeechChunker
{
    public const int SampleRate = 16_000;

    /// <summary>Preferred chunk length; a cut happens at the quietest frame after this point.</summary>
    public const double TargetSeconds = 25;

    /// <summary>Hard limit (the cut is forced here if no pause was found).</summary>
    public const double MaxSeconds = 30;

    private const int FrameSamples = SampleRate / 50; // 20 ms

    public static IEnumerable<SpeechChunk> Split(IAudioSampleReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var resampler = new Resampler(reader.Format.SampleRate, SampleRate);
        var channels = reader.Format.Channels;
        var input = new float[reader.Format.SampleRate * channels];
        var mono = new List<float>();
        var pending = new List<float>();
        var chunkStartSample = 0L;
        var index = 0;
        var noiseFloor = new NoiseFloor();

        int frames;
        while ((frames = reader.Read(input)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mono.Clear();
            Resampler.Downmix(input.AsSpan(0, frames * channels), channels, mono);
            resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mono), pending);

            while (pending.Count >= (int)(MaxSeconds * SampleRate))
            {
                var cut = FindCut(pending, noiseFloor);
                var chunk = pending.GetRange(0, cut).ToArray();
                pending.RemoveRange(0, cut);
                if (ContainsSpeech(chunk, noiseFloor))
                {
                    yield return new SpeechChunk(index++, chunkStartSample / (double)SampleRate, chunk);
                }

                chunkStartSample += cut;
            }
        }

        resampler.Flush(pending);
        if (pending.Count > 0)
        {
            var tail = pending.ToArray();
            if (ContainsSpeech(tail, noiseFloor))
            {
                yield return new SpeechChunk(index, chunkStartSample / (double)SampleRate, tail);
            }
        }
    }

    /// <summary>Cut at the quietest 20 ms frame between Target and Max seconds.</summary>
    private static int FindCut(List<float> samples, NoiseFloor floor)
    {
        var from = (int)(TargetSeconds * SampleRate);
        var to = Math.Min(samples.Count, (int)(MaxSeconds * SampleRate)) - FrameSamples;
        var best = to;
        var bestEnergy = double.MaxValue;
        for (var start = from; start <= to; start += FrameSamples)
        {
            var e = Energy(samples, start, FrameSamples);
            floor.Observe(e);
            if (e < bestEnergy)
            {
                bestEnergy = e;
                best = start + (FrameSamples / 2);
            }
        }

        return Math.Clamp(best, 1, samples.Count);
    }

    private static bool ContainsSpeech(float[] chunk, NoiseFloor floor)
    {
        var activeFrames = 0;
        for (var start = 0; start + FrameSamples <= chunk.Length; start += FrameSamples)
        {
            var e = Energy(chunk, start, FrameSamples);
            floor.Observe(e);
            if (e > floor.SpeechThreshold)
            {
                // ~200 ms of clearly-above-floor energy counts as possible speech.
                if (++activeFrames >= 10)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double Energy(IReadOnlyList<float> s, int start, int length)
    {
        double sum = 0;
        for (var i = start; i < start + length && i < s.Count; i++)
        {
            sum += s[i] * s[i];
        }

        return sum / length;
    }

    /// <summary>Tracks a slowly adapting background level; speech must exceed it by ~12 dB and -55 dBFS.</summary>
    private sealed class NoiseFloor
    {
        private const double MinSpeechEnergy = 3.2e-6; // ≈ -55 dBFS
        private double _floor = 1e-7;

        public double SpeechThreshold => Math.Max(_floor * 16, MinSpeechEnergy);

        public void Observe(double energy)
        {
            // Falls quickly, rises slowly: follows the quiet parts, not the speech.
            _floor = energy < _floor ? (_floor * 0.7) + (energy * 0.3) : (_floor * 0.999) + (energy * 0.001);
        }
    }
}
