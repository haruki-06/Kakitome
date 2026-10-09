using System.Runtime.InteropServices;
using System.Threading.Channels;
using Kakitome.Application.Asr;
using Kakitome.Application.Audio;
using Kakitome.Application.Recording;

namespace Kakitome.Application.Live;

/// <summary>A line of the live transcript. Partial lines are replaced by the final line with the same id.</summary>
public sealed record LiveSegment(long Id, CaptureSourceKind Source, double StartSeconds, string Text, bool IsFinal);

/// <summary>Decodes one utterance (16 kHz mono). Implementations serialize access to the underlying engine.</summary>
public interface ILiveDecoder
{
    Task<string> DecodeAsync(float[] samples16k, CancellationToken cancellationToken);
}

/// <summary>
/// Live transcript for one captured stream (docs/04 "Live ASR": a responsive preview, not the final transcript).
/// The capture thread only copies into a bounded queue; a worker downmixes, resamples to 16 kHz, finds utterances with
/// an adaptive energy detector and decodes them, publishing partial text while someone speaks and a final line at each
/// pause. When decoding cannot keep up, queued audio is dropped (the recording itself is never affected).
/// </summary>
public sealed class LiveTranscriber : IAudioSampleSink, IAsyncDisposable
{
    public const int SampleRate = 16_000;
    private const int FrameSamples = SampleRate / 50;          // 20 ms
    private const int PreRollFrames = 10;                      // 200 ms kept before speech starts
    private const int EndPauseFrames = 30;                     // a 0.6 s pause ends an utterance
    private const int MaxUtteranceSamples = 10 * SampleRate;   // force a line after 10 s
    private const int PartialEverySamples = 2 * SampleRate;    // refresh partial text every 2 s of speech
    private const int MinPartialSamples = SampleRate;

    private readonly Channel<float[]> _queue;
    private readonly AudioFormat _format;
    private readonly CaptureSourceKind _source;
    private readonly ILiveDecoder _decoder;
    private readonly Func<long> _nextId;
    private readonly Resampler _resampler;
    private readonly Task _worker;
    private readonly CancellationTokenSource _cts = new();
    private readonly double _maxBacklogSeconds;
    private readonly List<float> _resampled = [];
    private readonly List<float> _utterance = [];
    private readonly Queue<float[]> _preRoll = new();
    private float[] _frame = new float[FrameSamples];
    private int _frameFill;
    private long _queuedFrames;
    private long _samplesSeen;
    private long _utteranceStart;
    private long _currentId;
    private bool _inSpeech;
    private int _silentFrames;
    private int _sinceLastPartial;
    private float _noiseFloor = 0.002f;

    public LiveTranscriber(AudioFormat format, CaptureSourceKind source, ILiveDecoder decoder, Func<long> nextId, double maxBacklogSeconds = 6)
    {
        _format = format;
        _source = source;
        _decoder = decoder;
        _nextId = nextId;
        _maxBacklogSeconds = maxBacklogSeconds;
        _resampler = new Resampler(format.SampleRate, SampleRate);
        _queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Raised on the worker thread whenever a line appears or changes.</summary>
    public event EventHandler<LiveSegment>? SegmentChanged;

    /// <summary>Audio dropped because decoding fell behind (seconds), for diagnostics.</summary>
    public double DroppedSeconds { get; private set; }

    /// <summary>Capture thread: downmix to mono and copy only.</summary>
    public void OnSamples(ReadOnlySpan<byte> interleavedFloat32)
    {
        var samples = MemoryMarshal.Cast<byte, float>(interleavedFloat32);
        var channels = _format.Channels;
        var frames = samples.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += samples[(f * channels) + c];
            }

            mono[f] = sum / channels;
        }

        if (_queue.Writer.TryWrite(mono))
        {
            Interlocked.Add(ref _queuedFrames, frames);
        }
    }

    /// <summary>Finishes the current line and stops.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;
        await foreach (var chunk in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            var backlog = Interlocked.Add(ref _queuedFrames, -chunk.Length);
            if (backlog / (double)_format.SampleRate > _maxBacklogSeconds)
            {
                // Too far behind: drop what is queued and start fresh rather than lag ever further.
                while (_queue.Reader.TryRead(out var dropped))
                {
                    Interlocked.Add(ref _queuedFrames, -dropped.Length);
                    DroppedSeconds += dropped.Length / (double)_format.SampleRate;
                }

                await EndUtteranceAsync(token).ConfigureAwait(false);
                continue;
            }

            _resampled.Clear();
            _resampler.Process(chunk, _resampled);
            foreach (var sample in _resampled)
            {
                _frame[_frameFill++] = sample;
                if (_frameFill == FrameSamples)
                {
                    _frameFill = 0;
                    await OnFrameAsync(_frame, token).ConfigureAwait(false);
                    _frame = new float[FrameSamples];
                }
            }
        }

        await EndUtteranceAsync(token).ConfigureAwait(false);
    }

    private async Task OnFrameAsync(float[] frame, CancellationToken token)
    {
        double energy = 0;
        foreach (var s in frame)
        {
            energy += s * s;
        }

        var rms = (float)Math.Sqrt(energy / frame.Length);
        var speech = IsSpeech(rms);
        _samplesSeen += frame.Length;

        if (!_inSpeech)
        {
            if (!speech)
            {
                _preRoll.Enqueue(frame);
                if (_preRoll.Count > PreRollFrames)
                {
                    _preRoll.Dequeue();
                }

                return;
            }

            _inSpeech = true;
            _currentId = _nextId();
            _utteranceStart = _samplesSeen - frame.Length - (_preRoll.Count * (long)FrameSamples);
            foreach (var pre in _preRoll)
            {
                _utterance.AddRange(pre);
            }

            _preRoll.Clear();
            _silentFrames = 0;
            _sinceLastPartial = 0;
        }

        _utterance.AddRange(frame);
        _sinceLastPartial += frame.Length;
        _silentFrames = speech ? 0 : _silentFrames + 1;

        if (_silentFrames >= EndPauseFrames || _utterance.Count >= MaxUtteranceSamples)
        {
            await EndUtteranceAsync(token).ConfigureAwait(false);
        }
        else if (_sinceLastPartial >= PartialEverySamples && _utterance.Count >= MinPartialSamples)
        {
            _sinceLastPartial = 0;
            await PublishAsync(isFinal: false, token).ConfigureAwait(false);
        }
    }

    /// <summary>Adaptive threshold: speech is clearly above the tracked noise floor (and above about -50 dBFS).</summary>
    internal bool IsSpeech(float rms)
    {
        var speech = rms > Math.Max(_noiseFloor * 4, 0.003f);
        _noiseFloor = speech ? Math.Min(_noiseFloor * 1.0005f, 0.05f) : Math.Clamp((_noiseFloor * 0.98f) + (rms * 0.02f), 0.0003f, 0.05f);
        return speech;
    }

    private async Task EndUtteranceAsync(CancellationToken token)
    {
        if (!_inSpeech)
        {
            return;
        }

        await PublishAsync(isFinal: true, token).ConfigureAwait(false);
        _utterance.Clear();
        _inSpeech = false;
        _silentFrames = 0;
    }

    private async Task PublishAsync(bool isFinal, CancellationToken token)
    {
        string text;
        try
        {
            text = (await _decoder.DecodeAsync([.. _utterance], token).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The preview is best effort; a decode failure never reaches the recording.
            return;
        }

        if (text.Length > 0 || isFinal)
        {
            SegmentChanged?.Invoke(this, new LiveSegment(_currentId, _source, _utteranceStart / (double)SampleRate, text, isFinal));
        }
    }
}

/// <summary>Serializes decoding on one <see cref="IAsrSession"/> shared by all live streams.</summary>
public sealed class AsrSessionLiveDecoder(IAsrSession session) : ILiveDecoder, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> DecodeAsync(float[] samples16k, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await session.TranscribeAsync(samples16k, prompt: null, cancellationToken).ConfigureAwait(false);
            return string.Concat(result.Segments.Select(s => s.Text));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
