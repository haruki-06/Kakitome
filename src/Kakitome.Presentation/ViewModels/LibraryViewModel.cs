using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Rendering;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

public sealed partial class RecordingItemViewModel(RecordingId id) : ObservableObject
{
    public RecordingId Id { get; } = id;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Project { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DurationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProblem { get; set; }

    [ObservableProperty]
    public partial string TagsText { get; set; } = string.Empty;

    public DateTimeOffset SortKey { get; set; }

    /// <summary>Screen-reader summary of the row.</summary>
    public string AccessibleName => $"{Title}, {Project}, {DateText}, {DurationText}, {StatusText}";
}

/// <summary>All recordings with search-as-you-type filter, project filter, and per-recording processing status.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly RecordingActions _actions;
    private readonly JobScheduler _jobs;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizer _text;
    private readonly INavigationService _navigation;
    private readonly IShellService _shell;
    private List<RecordingItemViewModel> _all = [];
    private int _reloadQueued;

    public LibraryViewModel(
        LibraryService library,
        RecordingActions actions,
        JobScheduler jobs,
        IUiDispatcher ui,
        ILocalizer text,
        INavigationService navigation,
        IShellService shell)
    {
        _library = library;
        _actions = actions;
        _jobs = jobs;
        _ui = ui;
        _text = text;
        _navigation = navigation;
        _shell = shell;
        _library.Changed += (_, _) => QueueReload();
        _jobs.JobChanged += (_, e) =>
        {
            if (e.Job.IsTerminal || e.Job.State == JobState.Running)
            {
                QueueReload();
            }
        };
    }

    public ObservableCollection<RecordingItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>Project display name to show only, or null for all.</summary>
    [ObservableProperty]
    public partial string? ProjectFilter { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public async Task LoadAsync()
    {
        var entries = await _library.ListRecordingsAsync().ConfigureAwait(false);
        var jobs = await _jobs.ListAsync().ConfigureAwait(false);
        var items = entries.Select(e => ToItem(e, jobs.Where(j => j.RecordingId == e.Id).ToList())).ToList();
        _ui.Post(() =>
        {
            _all = items;
            ApplyFilter();
        });
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnProjectFilterChanged(string? value) => ApplyFilter();

    [RelayCommand]
    private void Open(RecordingItemViewModel? item)
    {
        if (item is not null)
        {
            _navigation.Navigate(PageKey.RecordingDetail, new RecordingNavigation(item.Id));
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync(RecordingItemViewModel? item)
    {
        if (item is not null)
        {
            _shell.OpenFolder(await _library.GetRecordingPathAsync(item.Id).ConfigureAwait(true));
        }
    }

    [RelayCommand]
    private async Task MoveToRecycleBinAsync(RecordingItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var confirmed = await _shell.ConfirmAsync(
            _text.GetString("DeleteDialog_Title"),
            _text.Format("DeleteDialog_Content", item.Title),
            _text.GetString("DeleteDialog_Primary"),
            _text.GetString("Dialog_Cancel")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        Message = await _actions.MoveToRecycleBinAsync(item.Id).ConfigureAwait(true)
            ? _text.Format("Info_MovedToRecycleBin", item.Title)
            : _text.GetString("Error_RecycleBinUnavailable");
    }

    private void QueueReload()
    {
        // Coalesce bursts of change events (e.g. a Library sync) into one reload.
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300).ConfigureAwait(false);
                Interlocked.Exchange(ref _reloadQueued, 0);
                await LoadAsync().ConfigureAwait(false);
            });
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        var visible = _all
            .Where(i => ProjectFilter is null || string.Equals(i.Project, ProjectFilter, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter.Length == 0
                || i.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || i.Project.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || i.TagsText.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(i => i.SortKey)
            .ToList();

        Items.Clear();
        foreach (var item in visible)
        {
            Items.Add(item);
        }

        IsEmpty = Items.Count == 0;
    }

    private RecordingItemViewModel ToItem(RecordingIndexEntry e, List<JobRecord> jobs)
    {
        var when = e.RecordedAt ?? e.CreatedAt;
        var (status, problem) = Status(e, jobs);
        return new RecordingItemViewModel(e.Id)
        {
            Title = e.Title,
            Project = e.Project,
            DateText = TimeFormat.DateTime(when),
            DurationText = e.DurationSeconds is { } d ? TimeFormat.Clock(d) : "–",
            StatusText = status,
            IsProblem = problem,
            TagsText = string.Join(", ", e.Tags),
            SortKey = when,
        };
    }

    internal (string Text, bool Problem) Status(RecordingIndexEntry e, List<JobRecord> jobs)
    {
        if (e.CaptureStatus == CaptureStatus.InProgress)
        {
            return (_text.GetString("Status_Recording"), false);
        }

        var running = jobs.FirstOrDefault(j => j.State == JobState.Running) ?? jobs.FirstOrDefault(j => j.State == JobState.Pending);
        if (running is not null)
        {
            return (_text.Format("Status_Processing", _text.GetString($"Stage_{running.Kind.Replace('.', '_')}")), false);
        }

        var latest = jobs.OrderByDescending(j => j.UpdatedAt).FirstOrDefault();
        if (latest?.State == JobState.Failed)
        {
            return (_text.Format("Status_Failed", _text.GetString($"Stage_{latest.Kind.Replace('.', '_')}")), true);
        }

        if (e.HasExternalChanges)
        {
            return (_text.GetString("Status_ExternalChanges"), true);
        }

        return e.HasTranscript ? (_text.GetString(e.HasSummary ? "Status_Ready" : "Status_Transcribed"), false) : (_text.GetString("Status_NotTranscribed"), false);
    }
}
