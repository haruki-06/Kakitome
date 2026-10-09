using Microsoft.Extensions.Logging;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Application.Settings;

namespace Kakitome.Application.Live;

public enum LiveTranscriptState
{
    /// <summary>Turned off in Settings, or no recording.</summary>
    Off,

    /// <summary>No installed model can run the preview.</summary>
    NoModel,

    /// <summary>Paused to save power (battery with Energy Saver / Battery Saver mode).</summary>
    PowerSaving,

    Starting,
    Running,
}

/// <summary>
/// Runs the live transcript while recording (docs/04 "Live ASR"). It attaches to each captured stream through the
/// session's tap, uses the fastest suitable installed local model, and never stores anything: the final transcript is
/// produced afterwards by the durable pipeline. Any failure here only turns the preview off.
/// </summary>
public sealed partial class LiveTranscriptionService : IDisposable
{
    private readonly RecordingService _recording;
    private readonly IEnumerable<IFinalAsrProvider> _providers;
    private readonly IModelStore _models;
    private readonly ISettingsStore _settings;
    private readonly IAccelerationProbe _acceleration;
    private readonly ISystemResourceProbe _resources;
    private readonly ILogger<LiveTranscriptionService> _logger;
    private readonly Lock _gate = new();
    private Run? _run;
    private long _nextId;

    public LiveTranscriptionService(
        RecordingService recording,
        IEnumerable<IFinalAsrProvider> providers,
        IModelStore models,
        ISettingsStore settings,
        IAccelerationProbe acceleration,
        ISystemResourceProbe resources,
        ILogger<LiveTranscriptionService> logger)
    {
        _recording = recording;
        _providers = providers;
        _models = models;
        _settings = settings;
        _acceleration = acceleration;
        _resources = resources;
        _logger = logger;
        _recording.SessionStarted += OnSessionStarted;
        _recording.StateChanged += OnStateChanged;
    }

    public LiveTranscriptState State { get; private set; } = LiveTranscriptState.Off;

    /// <summary>Model used for the preview (for display), or null.</summary>
    public string? ModelName { get; private set; }

    /// <summary>Raised from worker threads when a line appears or changes.</summary>
    public event EventHandler<LiveSegment>? SegmentChanged;

    /// <summary>Raised when <see cref="State"/> changes; a new recording also clears previous lines.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Live-preview model preference: responsiveness first (docs/04), within what is installed.</summary>
    public static IReadOnlyList<string> LivePreference(string? language, bool hasCapableGpu)
    {
        var english = language is not null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        List<string> order = [];
        if (hasCapableGpu)
        {
            order.Add(ModelCatalog.WhisperLargeV3TurboQ5);
        }

        if (!english)
        {
            order.Add(ModelCatalog.ReazonSpeechK2V2Int8); // Japanese is the primary language; fastest on CPU
        }

        order.Add(ModelCatalog.WhisperSmallQ5);
        return order;
    }

    public void Dispose()
    {
        _recording.SessionStarted -= OnSessionStarted;
        _recording.StateChanged -= OnStateChanged;
        _ = StopAsync();
    }

    private void OnSessionStarted(object? sender, RecordingSession session)
    {
        _ = StopAsync(); // a previous run (should not exist) is ended first
        var settings = _settings.Current;
        if (!settings.Recording.LiveTranscript)
        {
            SetState(LiveTranscriptState.Off);
            return;
        }

        var power = _resources.Current;
        if (!power.OnAcPower && (power.EnergySaverOn || settings.Processing.Mode == ProcessingMode.BatterySaver))
        {
            SetState(LiveTranscriptState.PowerSaving);
            return;
        }

        var language = settings.Processing.TranscriptionLanguage;

        // The GPU only on AC power: on battery the preview stays on the CPU's fastest model.
        var useGpu = _acceleration.HasCapableGpu && power.OnAcPower;
        var choice = LivePreference(language, useGpu)
            .Select(ModelCatalog.Find)
            .Where(m => m is not null && _models.GetState(m.Id) == ModelState.Installed)
            .Select(m => (Model: m!, Provider: _providers.FirstOrDefault(p => p.Id == m!.ProviderId)))
            .FirstOrDefault(c => c.Provider is not null);
        if (choice.Provider is null)
        {
            SetState(LiveTranscriptState.NoModel);
            return;
        }

        // Attach the taps now (samples queue up) and load the model in the background.
        var run = new Run();
        var decoder = new DeferredDecoder(run);
        foreach (var stream in session.LiveStreams)
        {
            var transcriber = new LiveTranscriber(stream.Format, stream.Kind, decoder, () => Interlocked.Increment(ref _nextId));
            transcriber.SegmentChanged += (_, segment) => SegmentChanged?.Invoke(this, segment);
            session.SetTap(stream.Index, transcriber);
            run.Transcribers.Add(transcriber);
        }

        run.Session = session;
        lock (_gate)
        {
            _run = run;
        }

        ModelName = choice.Model.DisplayName;
        SetState(LiveTranscriptState.Starting);
        _ = OpenAsync(run, choice.Provider, choice.Model.Id, language, useGpu);
    }

    private async Task OpenAsync(Run run, IFinalAsrProvider provider, string modelId, string? language, bool gpu)
    {
        try
        {
            // Few threads: the preview must leave room for the recording and the rest of the PC.
            var asr = await provider.OpenAsync(new AsrSessionOptions(modelId, language, Threads: 2, PreferEfficiency: !gpu, ShortUtterances: true), run.Cancellation.Token)
                .ConfigureAwait(false);
            run.Ready(asr);
            if (!run.Cancellation.IsCancellationRequested)
            {
                SetState(LiveTranscriptState.Running);
            }
        }
#pragma warning disable CA1031 // The preview is optional: any failure only turns it off.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogOpenFailed(ex, modelId);
            run.Fail();
            await StopAsync().ConfigureAwait(false);
            SetState(LiveTranscriptState.Off);
        }
    }

    private void OnStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        if (e.Status is null || e.Status.State is RecordingState.Stopping or RecordingState.Stopped or RecordingState.Cancelled)
        {
            _ = StopAsync();
        }
    }

    private async Task StopAsync()
    {
        Run? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
        }

        if (run is null)
        {
            return;
        }

        try
        {
            for (var i = 0; i < run.Transcribers.Count; i++)
            {
                run.Session?.SetTap(i, null);
            }

            foreach (var transcriber in run.Transcribers)
            {
                await transcriber.DisposeAsync().ConfigureAwait(false);
            }

            await run.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup of an optional preview must never disturb stopping a recording.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStopFailed(ex);
        }

        if (State is LiveTranscriptState.Running or LiveTranscriptState.Starting)
        {
            SetState(LiveTranscriptState.Off);
        }
    }

    private void SetState(LiveTranscriptState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live transcript could not start with {ModelId}")]
    private partial void LogOpenFailed(Exception ex, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live transcript cleanup failed")]
    private partial void LogStopFailed(Exception ex);

    /// <summary>One recording's preview: transcribers, the shared ASR session (once loaded) and its decoder.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly TaskCompletionSource<AsrSessionLiveDecoder?> _decoder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IAsrSession? _asr;

        public List<LiveTranscriber> Transcribers { get; } = [];

        public RecordingSession? Session { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<AsrSessionLiveDecoder?> Decoder => _decoder.Task;

        public void Ready(IAsrSession asr)
        {
            _asr = asr;
            _decoder.TrySetResult(new AsrSessionLiveDecoder(asr));
        }

        public void Fail() => _decoder.TrySetResult(null);

        public async ValueTask DisposeAsync()
        {
            await Cancellation.CancelAsync().ConfigureAwait(false);
            _decoder.TrySetResult(null);
            (await _decoder.Task.ConfigureAwait(false))?.Dispose();
            if (_asr is not null)
            {
                await _asr.DisposeAsync().ConfigureAwait(false);
            }

            Cancellation.Dispose();
        }
    }

    /// <summary>Waits for the model to load; utterances spoken meanwhile are decoded once it is ready.</summary>
    private sealed class DeferredDecoder(Run run) : ILiveDecoder
    {
        public async Task<string> DecodeAsync(float[] samples16k, CancellationToken cancellationToken)
        {
            var decoder = await run.Decoder.WaitAsync(cancellationToken).ConfigureAwait(false);
            return decoder is null ? string.Empty : await decoder.DecodeAsync(samples16k, cancellationToken).ConfigureAwait(false);
        }
    }
}
