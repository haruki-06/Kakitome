using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Asr;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Rendering;
using Kakitome.Domain.Serialization;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

public sealed partial class SegmentViewModel : ObservableObject
{
    public required string Id { get; init; }

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string TimeText => TimeFormat.Stamp(StartSeconds);

    public string? Speaker { get; init; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;
}

public sealed record SummaryLine(string Text, double? AtSeconds)
{
    public string TimeText => AtSeconds is { } s ? TimeFormat.Stamp(s) : string.Empty;

    public bool HasTime => AtSeconds is not null;
}

public sealed record SummarySection(string Heading, IReadOnlyList<SummaryLine> Lines);

public sealed record SuggestionViewModel(EditSuggestion Suggestion, string Description);

/// <summary>
/// Recording detail: audio player with the transcript or the summary (switched, ADR-032); playback highlights the
/// segment being heard and clicking the text seeks the audio (docs/02 "Recording detail").
/// </summary>
public sealed partial class RecordingDetailViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly RecordingActions _actions;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizer _text;
    private readonly IShellService _shell;
    private readonly GlossaryTips _tips;
    private readonly GlossaryService _glossaries;
    private RecordingId? _id;
    private GlossaryTip? _tip;
    private bool _tipClosed;

    public RecordingDetailViewModel(
        LibraryService library, RecordingActions actions, IUiDispatcher ui, ILocalizer text, IShellService shell, GlossaryTips tips, GlossaryService glossaries)
    {
        _library = library;
        _actions = actions;
        _ui = ui;
        _text = text;
        _shell = shell;
        _tips = tips;
        _glossaries = glossaries;
        _library.Changed += (_, e) =>
        {
            if (_id is { } id && (e.RecordingId is null || e.RecordingId == id))
            {
                _ = ReloadAsync();
            }
        };
    }

    public ObservableCollection<SegmentViewModel> Segments { get; } = [];

    /// <summary>
    /// The transcript laid out for reading (<see cref="ReadingLayout"/>: no time stamps, sentence ends added at pauses,
    /// short paragraphs); rebuilt with <see cref="Segments"/>.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<ReadingBlock<SegmentViewModel>> ReadingBlocks { get; set; } = [];

    /// <summary>Text between segments of a paragraph: a space for spaced languages, nothing for Japanese.</summary>
    public string SegmentSeparator { get; private set; } = string.Empty;

    /// <summary>Speaker names are shown when the transcript has more than one speaker.</summary>
    [ObservableProperty]
    public partial bool ShowSpeakers { get; private set; }

    public ObservableCollection<SummarySection> SummarySections { get; } = [];

    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>Absolute path of the primary audio stream, or null when audio was removed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReTranscribeCommand))]
    public partial string? AudioPath { get; set; }

    /// <summary>Why there is no player: removed by the retention setting (with date) or simply missing.</summary>
    [ObservableProperty]
    public partial string NoAudioText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SummaryOverview { get; set; }

    [ObservableProperty]
    public partial bool HasTranscript { get; set; }

    [ObservableProperty]
    public partial bool HasSummary { get; set; }

    [ObservableProperty]
    public partial string? TranscriptInfo { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsMessageError { get; set; }

    [ObservableProperty]
    public partial SegmentViewModel? CurrentSegment { get; set; }

    /// <summary>The "make a glossary" tip (ADR-031); closing it hides it until the page is opened again.</summary>
    [ObservableProperty]
    public partial bool ShowGlossaryTip { get; set; }

    [ObservableProperty]
    public partial string GlossaryTipText { get; set; } = string.Empty;

    /// <summary>The view seeks the player to this position (seconds).</summary>
    public event EventHandler<double>? SeekRequested;

    public async Task LoadAsync(RecordingNavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _id = navigation.Id;
        _tipClosed = false;
        await ReloadAsync().ConfigureAwait(false);
        if (navigation.AtSeconds is { } at)
        {
            _ui.Post(() => SeekRequested?.Invoke(this, at));
        }
    }

    /// <summary>Called by the view as playback advances; highlights the segment being heard.</summary>
    public void UpdatePosition(double seconds) =>
        CurrentSegment = Segments.LastOrDefault(s => s.StartSeconds <= seconds + 0.05);

    [RelayCommand]
    private void Seek(object? target)
    {
        var seconds = target switch
        {
            SegmentViewModel s => s.StartSeconds,
            SummaryLine { AtSeconds: { } at } => at,
            double d => d,
            _ => (double?)null,
        };
        if (seconds is { } value)
        {
            SeekRequested?.Invoke(this, value);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (_id is { } id)
        {
            _shell.OpenFolder(await _library.GetRecordingPathAsync(id).ConfigureAwait(true));
        }
    }

    private bool CanReTranscribe() => AudioPath is not null;

    [RelayCommand(CanExecute = nameof(CanReTranscribe))]
    private async Task ReTranscribeAsync()
    {
        if (_id is not { } id)
        {
            return;
        }

        if (HasTranscript && !await _shell.ConfirmAsync(
                _text.GetString("RetranscribeDialog_Title"), _text.GetString("RetranscribeDialog_Content"),
                _text.GetString("RetranscribeDialog_Primary"), _text.GetString("Dialog_Cancel")).ConfigureAwait(true))
        {
            return;
        }

        await _actions.ReTranscribeAsync(id).ConfigureAwait(true);
        ShowInfo(_text.GetString("Info_Queued"));
    }

    [RelayCommand]
    private async Task RegenerateSummaryAsync()
    {
        if (_id is { } id)
        {
            await _actions.RegenerateSummaryAsync(id).ConfigureAwait(true);
            ShowInfo(_text.GetString("Info_Queued"));
        }
    }

    partial void OnShowGlossaryTipChanged(bool value)
    {
        if (!value)
        {
            _tipClosed = true;
        }
    }

    /// <summary>Copies a request for an AI assistant of the user's choice; Kakitome itself sends nothing.</summary>
    [RelayCommand]
    private void CopyGlossaryRequest()
    {
        if (_tip is not { } tip)
        {
            return;
        }

        var topics = tip.Topics.Count > 0 ? _text.Format("GlossaryTip_Topics", string.Join(_text.GetString("GlossaryTip_TopicSeparator"), tip.Topics)) : string.Empty;
        _shell.CopyText(_text.Format("GlossaryTip_Request", tip.Title, topics));
        ShowInfo(_text.GetString("GlossaryTip_Copied"));
    }

    [RelayCommand]
    private async Task ShowTranscriptFileAsync()
    {
        if (_id is { } id)
        {
            _shell.OpenFolder(await _library.GetRecordingPathAsync(id).ConfigureAwait(true));
        }
    }

    /// <summary>Imports the glossary the user made for this recording's project, then offers to transcribe again.</summary>
    [RelayCommand]
    private async Task ImportGlossaryAsync()
    {
        if (_id is not { } id || _tip is not { } tip || await _shell.PickTextFileAsync().ConfigureAwait(true) is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            var result = await _glossaries.ImportAsync(new GlossaryScope(tip.Project), path).ConfigureAwait(true);
            var imported = _text.Format("GlossaryTip_Imported", tip.Project, result.Glossary.Terms.Count, result.Glossary.Replacements.Count);
            if (AudioPath is not null && await _shell.ConfirmAsync(
                    _text.GetString("RetranscribeDialog_Title"), imported + Environment.NewLine + Environment.NewLine + _text.GetString("RetranscribeDialog_Content"),
                    _text.GetString("RetranscribeDialog_Primary"), _text.GetString("Dialog_Cancel")).ConfigureAwait(true))
            {
                await _actions.ReTranscribeAsync(id).ConfigureAwait(true);
                ShowInfo(imported + " " + _text.GetString("Info_Queued"));
            }
            else
            {
                ShowInfo(imported + " " + _text.GetString("Glossary_ApplyHint"));
            }

            ShowGlossaryTip = false;
        }
        catch (InvalidDataException ex)
        {
            ShowError(_text.GetString("Glossary_Invalid_" + ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(_text.Format("Glossary_Failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task HideGlossaryTipsAsync()
    {
        await _tips.HideAsync().ConfigureAwait(true);
        ShowGlossaryTip = false;
        ShowInfo(_text.GetString("GlossaryTip_Hidden"));
    }

    /// <summary>Saves a corrected line (called by the view when an inline edit is committed).</summary>
    public async Task SaveSegmentEditAsync(SegmentViewModel segment, string text)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (_id is not { } id)
        {
            return;
        }

        try
        {
            await _actions.EditSegmentAsync(id, segment.Id, text).ConfigureAwait(true);
        }
        catch (ExternalEditConflictException)
        {
            ShowError(_text.GetString("Error_ExternalEditConflict"));
        }
    }

    [RelayCommand]
    private async Task AcceptSuggestionAsync(SuggestionViewModel? suggestion)
    {
        if (_id is { } id && suggestion is not null)
        {
            try
            {
                await _actions.AcceptSuggestionAsync(id, suggestion.Suggestion).ConfigureAwait(true);
            }
            catch (ExternalEditConflictException)
            {
                ShowError(_text.GetString("Error_ExternalEditConflict"));
            }
        }
    }

    private async Task ReloadAsync()
    {
        if (_id is not { } id)
        {
            return;
        }

        RecordingMetadata metadata;
        TranscriptDocument? transcript;
        SummaryDocument? summary;
        string folder;
        GlossaryTip? tip;
        try
        {
            metadata = await _library.GetMetadataAsync(id).ConfigureAwait(false);
            transcript = await _library.LoadTranscriptAsync(id).ConfigureAwait(false);
            summary = await _library.LoadSummaryAsync(id).ConfigureAwait(false);
            folder = await _library.GetRecordingPathAsync(id).ConfigureAwait(false);
            tip = await _tips.GetAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or LibraryFormatException or IOException or UnauthorizedAccessException)
        {
            // Deleted or moved outside Kakitome (the index may still list it), or files that cannot be read: say so on
            // the page instead of letting the exception end the app (it surfaced through async OnNavigatedTo).
            var missing = ex is KeyNotFoundException
                || (ex is LibraryFormatException && ex.Message.Contains("no metadata.json", StringComparison.Ordinal));
            _ui.Post(() => ShowError(missing ? _text.GetString("Error_RecordingMissing") : _text.Format("Error_RecordingUnreadable", ex.Message)));
            return;
        }

        var audio = metadata.Audio.FirstOrDefault(a => File.Exists(Path.Combine(folder, a.FileName)));

        _ui.Post(() =>
        {
            Apply(metadata, transcript, summary, audio is null ? null : Path.Combine(folder, audio.FileName));
            _tip = tip;
            if (tip is not null)
            {
                GlossaryTipText = _text.Format("GlossaryTip_Text", tip.Project);
            }

            ShowGlossaryTip = tip is not null && !_tipClosed;
        });
    }

    private void Apply(RecordingMetadata metadata, TranscriptDocument? transcript, SummaryDocument? summary, string? audioPath)
    {
        Title = metadata.Title;
        var duration = metadata.DurationSeconds is { } d ? TimeFormat.Clock(d) : "–";
        Subtitle = $"{metadata.Project} · {TimeFormat.DateTime(metadata.RecordedAt ?? metadata.CreatedAt)} · {duration}";
        AudioPath = audioPath;
        NoAudioText = metadata.AudioRemoval is { } removal
            ? _text.Format("Detail_AudioRemoved", TimeFormat.DateTime(removal.RemovedAt))
            : _text.GetString("Detail_NoAudio");

        Segments.Clear();
        if (transcript is not null)
        {
            var speakers = transcript.Speakers.ToDictionary(s => s.Id, s => s.Label ?? s.Id, StringComparer.Ordinal);
            foreach (var s in transcript.Segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                Segments.Add(new SegmentViewModel
                {
                    Id = s.Id,
                    StartSeconds = s.StartSeconds,
                    EndSeconds = s.EndSeconds,
                    Text = s.Text,
                    Speaker = s.Speaker is { } sp ? speakers.GetValueOrDefault(sp, sp) : null,
                });
            }

            SegmentSeparator = TimeFormat.UsesSpaces(transcript.Language) ? " " : string.Empty;
            TranscriptInfo = _text.Format("Detail_TranscriptInfo",
                _text.GetString($"TranscriptKind_{transcript.Kind}"), transcript.Engine?.Model ?? transcript.Engine?.Provider ?? "–");
        }
        else
        {
            TranscriptInfo = null;
        }

        ShowSpeakers = Segments.Select(x => x.Speaker).Where(x => x is not null).Distinct(StringComparer.Ordinal).Count() > 1;
        ReadingBlocks = ReadingLayout.Blocks(Segments, x => x.StartSeconds, x => x.EndSeconds, x => x.Speaker, x => x.Text,
            japanese: SegmentSeparator.Length == 0);
        HasTranscript = transcript is not null;
        Suggestions.Clear();
        foreach (var s in transcript?.Suggestions ?? [])
        {
            Suggestions.Add(new SuggestionViewModel(s, _text.Format("Detail_SuggestionText", s.Original.Trim(), s.Replacement.Length == 0 ? "∅" : s.Replacement)));
        }

        SummarySections.Clear();
        SummaryOverview = summary?.Overview;
        HasSummary = summary is not null;
        if (summary is not null)
        {
            Add("Summary_KeyPoints", summary.KeyPoints.Select(k => new SummaryLine(k.Text, k.AtSeconds)));
            Add("Summary_Decisions", summary.Decisions.Select(k => new SummaryLine(k.Text, k.AtSeconds)));
            Add("Summary_ActionItems", summary.ActionItems.Select(a => new SummaryLine(ActionText(a), a.AtSeconds)));
            Add("Summary_Questions", summary.Questions.Select(k => new SummaryLine(k.Text, k.AtSeconds)));
            if (summary.Topics.Count > 0)
            {
                Add("Summary_Topics", [new SummaryLine(string.Join(", ", summary.Topics), null)]);
            }
        }
    }

    private string ActionText(ActionItem a)
    {
        var extra = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Owner))
        {
            extra.Add(_text.Format("Summary_Owner", a.Owner));
        }

        if (!string.IsNullOrWhiteSpace(a.Due))
        {
            extra.Add(_text.Format("Summary_Due", a.Due));
        }

        return extra.Count == 0 ? a.Text : $"{a.Text} ({string.Join(", ", extra)})";
    }

    private void Add(string headingKey, IEnumerable<SummaryLine> lines)
    {
        var list = lines.ToList();
        if (list.Count > 0)
        {
            SummarySections.Add(new SummarySection(_text.GetString(headingKey), list));
        }
    }

    private void ShowInfo(string text)
    {
        IsMessageError = false;
        Message = text;
    }

    private void ShowError(string text)
    {
        IsMessageError = true;
        Message = text;
    }
}
