namespace Kakitome.Application.Audio;

/// <summary>
/// Streaming mono resampler using a windowed-sinc (Blackman) kernel with anti-alias cutoff at the lower Nyquist.
/// Quality is far beyond what ASR needs; used to produce the 16 kHz mono input ASR engines expect from 48 kHz captures.
/// </summary>
public sealed class Resampler
{
    private const int HalfTaps = 32;

    private readonly double _step;
    private readonly double _cutoff;
    private readonly List<float> _history = [];
    private double _position;

    public Resampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        _cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.95;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Feeds input samples; appends produced output samples to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (InputRate == OutputRate)
        {
            foreach (var s in input)
            {
                output.Add(s);
            }

            return;
        }

        foreach (var s in input)
        {
            _history.Add(s);
        }

        // Produce every output sample whose kernel window is fully available.
        while (_position + HalfTaps < _history.Count)
        {
            output.Add(Interpolate(_position));
            _position += _step;
        }

        // Drop history no longer needed by future windows.
        var discard = (int)Math.Floor(_position) - HalfTaps;
        if (discard > 0)
        {
            _history.RemoveRange(0, discard);
            _position -= discard;
        }
    }

    /// <summary>Flushes the tail (zero-padded) at end of stream.</summary>
    public void Flush(List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (InputRate == OutputRate)
        {
            return;
        }

        var end = _history.Count;
        for (var i = 0; i < HalfTaps; i++)
        {
            _history.Add(0);
        }

        while (_position < end)
        {
            output.Add(Interpolate(_position));
            _position += _step;
        }

        _history.Clear();
        _position = 0;
    }

    /// <summary>Downmixes interleaved samples to mono.</summary>
    public static void Downmix(ReadOnlySpan<float> interleaved, int channels, List<float> mono)
    {
        ArgumentNullException.ThrowIfNull(mono);
        if (channels == 1)
        {
            foreach (var s in interleaved)
            {
                mono.Add(s);
            }

            return;
        }

        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[i + c];
            }

            mono.Add(sum / channels);
        }
    }

    private float Interpolate(double position)
    {
        var center = (int)Math.Floor(position);
        var frac = position - center;
        double acc = 0;
        double norm = 0;
        for (var k = -HalfTaps + 1; k <= HalfTaps; k++)
        {
            var index = center + k;
            if (index < 0 || index >= _history.Count)
            {
                continue;
            }

            var x = k - frac;
            var w = Kernel(x);
            acc += _history[index] * w;
            norm += w;
        }

        return norm == 0 ? 0 : (float)(acc / norm);
    }

    private double Kernel(double x)
    {
        var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x * _cutoff) / (Math.PI * x * _cutoff);
        var t = (x + HalfTaps) / (2.0 * HalfTaps);
        var window = t is < 0 or > 1 ? 0 : 0.42 - (0.5 * Math.Cos(2 * Math.PI * t)) + (0.08 * Math.Cos(4 * Math.PI * t));
        return sinc * window;
    }
}
