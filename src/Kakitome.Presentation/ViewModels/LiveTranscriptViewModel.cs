using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kakitome.Application.Live;
using Kakitome.Application.Recording;
using Kakitome.Domain.Rendering;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

/// <summary>A live transcript line; partial lines are shown dimmed until their final text arrives.</summary>
public sealed partial class LiveLineViewModel : ObservableObject
{
    public required long Id { get; init; }

    public required string TimeText { get; init; }

    public required string SourceText { get; init; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    public partial bool IsFinal { get; set; }

    public double Opacity => IsFinal ? 1.0 : 0.6;
}

/// <summary>
/// Live transcript shown while recording (docs/02 "Live transcript"): timestamp, source, current text, auto-scroll,
/// current utterance highlighted; explicitly labelled as a preview that the final transcript may differ from.
/// </summary>
public sealed partial class LiveTranscriptViewModel : ObservableObject
{
    /// <summary>Lines kept on screen; older ones scroll away (nothing is stored).</summary>
    public const int MaxLines = 200;

    private readonly LiveTranscriptionService _live;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizer _text;

    public LiveTranscriptViewModel(LiveTranscriptionService live, IUiDispatcher ui, ILocalizer text)
    {
        _live = live;
        _ui = ui;
        _text = text;
        live.SegmentChanged += (_, segment) => _ui.Post(() => Apply(segment));
        live.StateChanged += (_, _) => _ui.Post(UpdateState);
        UpdateState();
    }

    public ObservableCollection<LiveLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Raised after a line was added so the view can scroll to it.</summary>
    public event EventHandler<LiveLineViewModel>? LineAdded;

    internal void Apply(LiveSegment segment)
    {
        var existing = Lines.LastOrDefault(l => l.Id == segment.Id);
        if (existing is null)
        {
            if (segment.IsFinal && segment.Text.Length == 0)
            {
                return;
            }

            existing = new LiveLineViewModel
            {
                Id = segment.Id,
                TimeText = TimeFormat.Clock(segment.StartSeconds),
                SourceText = segment.Source == CaptureSourceKind.Microphone ? _text.GetString("Live_SourceMic") : _text.GetString("Live_SourceOthers"),
            };
            Lines.Add(existing);
            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
            }

            LineAdded?.Invoke(this, existing);
        }

        if (segment.IsFinal && segment.Text.Length == 0)
        {
            Lines.Remove(existing); // the utterance turned out to be noise
            return;
        }

        existing.Text = segment.Text;
        existing.IsFinal = segment.IsFinal;
    }

    private void UpdateState()
    {
        var state = _live.State;
        if (state == LiveTranscriptState.Starting)
        {
            Lines.Clear(); // a new recording starts with an empty preview
        }

        IsActive = state is LiveTranscriptState.Starting or LiveTranscriptState.Running;
        StatusText = state switch
        {
            LiveTranscriptState.Starting => _text.Format("Live_Starting", _live.ModelName),
            LiveTranscriptState.Running => _text.Format("Live_Running", _live.ModelName),
            LiveTranscriptState.NoModel => _text.GetString("Live_NoModel"),
            LiveTranscriptState.PowerSaving => _text.GetString("Live_PowerSaving"),
            _ => string.Empty,
        };
    }
}
