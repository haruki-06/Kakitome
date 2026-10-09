using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Rendering;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

/// <summary>One processing step (job) of a recording or file.</summary>
public sealed partial class JobItemViewModel(Guid id) : ObservableObject
{
    public Guid Id { get; } = id;

    public RecordingId? RecordingId { get; set; }

    [ObservableProperty]
    public partial string StageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecordingTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    /// <summary>Why it waits / what failed / current progress text (concise by default).</summary>
    [ObservableProperty]
    public partial string? DetailText { get; set; }

    /// <summary>Engine/model identity and locality (expandable detail).</summary>
    [ObservableProperty]
    public partial string? EngineText { get; set; }

    /// <summary>Segoe Fluent Icons glyph for the state (not used while running: a progress ring is shown instead).</summary>
    [ObservableProperty]
    public partial string Glyph { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    [ObservableProperty]
    public partial bool CanRetry { get; set; }

    [ObservableProperty]
    public partial bool CanPause { get; set; }

    [ObservableProperty]
    public partial bool CanResume { get; set; }

    [ObservableProperty]
    public partial bool IsProblem { get; set; }

    public DateTimeOffset SortKey { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public JobState State { get; set; }

    /// <summary>Recording, or pipeline/job for work that has no recording (yet).</summary>
    internal string GroupKey { get; set; } = string.Empty;

    internal bool IsActive => State is JobState.Pending or JobState.Running or JobState.Paused;
}

/// <summary>All processing steps of one recording or file, shown together so the boundary between files is clear.</summary>
public sealed partial class JobGroupViewModel(string key) : ObservableObject
{
    internal string Key { get; } = key;

    public RecordingId? RecordingId { get; set; }

    public ObservableCollection<JobItemViewModel> Jobs { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Overall state, e.g. "Running (2 of 5 steps done)".</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Last activity time.</summary>
    [ObservableProperty]
    public partial string TimeText { get; set; } = string.Empty;

    /// <summary>Share of finished steps, 0..100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool HasProblem { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool CanOpen { get; set; }

    internal DateTimeOffset FirstCreated { get; set; }

    internal DateTimeOffset LastUpdated { get; set; }

    /// <summary>Whether it is listed under "in progress / needs attention" (active, or a step failed).</summary>
    internal bool NeedsAttention => IsActive || HasProblem;
}

/// <summary>
/// Processing Queue page (docs/02): stage, concise status, expandable detail, retry/cancel, locality, engine. Steps are
/// grouped per recording/file; groups in progress or with a failed step come first, finished ones below.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly JobScheduler _scheduler;
    private readonly LibraryService _library;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizer _text;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly Dictionary<RecordingId, string> _titles = [];
    private readonly HashSet<RecordingId> _titleLookups = [];
    private readonly Dictionary<Guid, JobItemViewModel> _jobs = [];
    private readonly Dictionary<string, JobGroupViewModel> _groups = [];

    public QueueViewModel(JobScheduler scheduler, LibraryService library, IUiDispatcher ui, ILocalizer text, INavigationService navigation, TimeProvider time)
    {
        _scheduler = scheduler;
        _library = library;
        _ui = ui;
        _text = text;
        _navigation = navigation;
        _time = time;
        _scheduler.JobChanged += (_, e) => _ui.Post(() => Upsert(e.Job));
    }

    /// <summary>Recordings/files with a step in progress, waiting, paused or failed (oldest first).</summary>
    public ObservableCollection<JobGroupViewModel> ActiveGroups { get; } = [];

    /// <summary>Recordings/files whose processing finished in the last 7 days (newest first).</summary>
    public ObservableCollection<JobGroupViewModel> FinishedGroups { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool HasActive { get; set; }

    [ObservableProperty]
    public partial bool HasFinished { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public async Task LoadAsync()
    {
        var jobs = await _scheduler.ListAsync(_time.GetUtcNow().AddDays(-7)).ConfigureAwait(false);
        foreach (var entry in await _library.ListRecordingsAsync().ConfigureAwait(false))
        {
            lock (_titles)
            {
                _titles[entry.Id] = entry.Title;
            }
        }

        _ui.Post(() =>
        {
            _jobs.Clear();
            _groups.Clear();
            ActiveGroups.Clear();
            FinishedGroups.Clear();
            foreach (var job in jobs)
            {
                Apply(job);
            }

            Regroup();
        });
    }

    [RelayCommand]
    private Task CancelAsync(JobItemViewModel? item) => item is null ? Task.CompletedTask : _scheduler.CancelAsync(item.Id);

    [RelayCommand]
    private Task RetryAsync(JobItemViewModel? item) => item is null ? Task.CompletedTask : _scheduler.RetryAsync(item.Id);

    [RelayCommand]
    private Task PauseAsync(JobItemViewModel? item) => item is null ? Task.CompletedTask : _scheduler.PauseAsync(item.Id);

    [RelayCommand]
    private Task ResumeAsync(JobItemViewModel? item) => item is null ? Task.CompletedTask : _scheduler.ResumeAsync(item.Id);

    [RelayCommand]
    private void OpenRecording(JobGroupViewModel? group)
    {
        if (group?.RecordingId is { } id)
        {
            _navigation.Navigate(PageKey.RecordingDetail, new RecordingNavigation(id));
        }
    }

    internal void Upsert(JobRecord job)
    {
        Apply(job);
        Regroup();
    }

    /// <summary>Updates (or adds) the step's view model from the job record.</summary>
    private void Apply(JobRecord job)
    {
        if (!_jobs.TryGetValue(job.Id, out var item))
        {
            item = new JobItemViewModel(job.Id);
            _jobs[job.Id] = item;
        }

        item.RecordingId = job.RecordingId;
        item.GroupKey = job.RecordingId is { } recording ? $"r:{recording}" : $"p:{job.PipelineId ?? job.Id}";
        item.State = job.State;
        item.SortKey = job.CreatedAt;
        item.UpdatedAt = job.FinishedAt ?? job.UpdatedAt;
        item.StageText = _text.GetString($"Stage_{job.Kind.Replace('.', '_')}");
        item.RecordingTitle = job.RecordingId is { } rid ? TitleOf(rid) : string.Empty;
        item.StateText = _text.GetString($"JobState_{job.State}");
        item.IsRunning = job.State == JobState.Running;
        item.Progress = (job.Progress ?? 0) * 100;
        item.IsIndeterminate = job.State == JobState.Running && job.Progress is null;
        item.IsProblem = job.State == JobState.Failed || (job.State == JobState.Pending && job.LastError is not null);
        item.Glyph = job.State switch
        {
            JobState.Succeeded => "", // check mark
            JobState.Failed => "",    // error badge
            JobState.Cancelled => "", // cancel
            JobState.Paused => "",    // pause
            _ => "",                  // clock (waiting)
        };
        item.DetailText = job.State switch
        {
            JobState.Pending when job.WaitReason != JobWaitReason.None => _text.GetString($"Wait_{job.WaitReason}"),
            JobState.Failed => JobErrorText.Localize(job.LastError, _text),
            JobState.Running when job.Progress is { } p => string.Create(CultureInfo.CurrentCulture, $"{p:P0}"),
            _ => null,
        };
        item.EngineText = job.Engine is null ? null : _text.Format("Queue_Engine", job.Engine, _text.GetString("Locality_Local"));
        item.CanCancel = !job.IsTerminal;
        item.CanRetry = job.State is JobState.Failed or JobState.Cancelled;
        item.CanPause = job.State is JobState.Pending or JobState.Running;
        item.CanResume = job.State == JobState.Paused;
    }

    /// <summary>Rebuilds groups and their order in place (view models are reused so the list does not flicker).</summary>
    private void Regroup()
    {
        var byKey = _jobs.Values.GroupBy(j => j.GroupKey).ToDictionary(g => g.Key, g => g.OrderBy(j => j.SortKey).ToList());
        foreach (var stale in _groups.Keys.Where(k => !byKey.ContainsKey(k)).ToList())
        {
            _groups.Remove(stale);
        }

        foreach (var (key, steps) in byKey)
        {
            var isNew = !_groups.TryGetValue(key, out var group);
            if (group is null)
            {
                group = new JobGroupViewModel(key);
                _groups[key] = group;
            }

            Sync(group.Jobs, steps);
            Summarize(group, steps);
            if (isNew)
            {
                group.IsExpanded = group.NeedsAttention;
            }
        }

        Sync(ActiveGroups, _groups.Values.Where(g => g.NeedsAttention).OrderBy(g => g.FirstCreated).ToList());
        Sync(FinishedGroups, _groups.Values.Where(g => !g.NeedsAttention).OrderByDescending(g => g.LastUpdated).ToList());

        var active = _jobs.Values.Count(j => j.IsActive);
        Summary = active == 0 ? _text.GetString("Queue_AllDone") : _text.Format("Queue_ActiveCount", active);
        HasActive = ActiveGroups.Count > 0;
        HasFinished = FinishedGroups.Count > 0;
        IsEmpty = _groups.Count == 0;
    }

    private void Summarize(JobGroupViewModel group, List<JobItemViewModel> steps)
    {
        var first = steps[0];
        group.RecordingId = first.RecordingId;
        group.CanOpen = first.RecordingId is not null;
        group.Title = first.RecordingId is { } id ? TitleOf(id) : first.StageText;
        if (group.Title.Length == 0)
        {
            group.Title = first.StageText;
        }

        var done = steps.Count(s => s.State == JobState.Succeeded);
        var failed = steps.Any(s => s.State == JobState.Failed);
        group.IsActive = steps.Any(s => s.IsActive);
        group.HasProblem = failed;
        group.Progress = 100.0 * steps.Count(s => s.State is JobState.Succeeded or JobState.Cancelled) / steps.Count;
        group.FirstCreated = first.SortKey;
        group.LastUpdated = steps.Max(s => s.UpdatedAt);
        group.TimeText = TimeFormat.DateTime(group.LastUpdated.ToLocalTime());
        group.StatusText =
            failed ? _text.GetString("Queue_GroupFailed")
            : steps.Any(s => s.State == JobState.Running) ? _text.Format("Queue_GroupRunning", done, steps.Count)
            : steps.Any(s => s.State == JobState.Pending) ? _text.Format("Queue_GroupWaiting", done, steps.Count)
            : steps.Any(s => s.State == JobState.Paused) ? _text.GetString("Queue_GroupPaused")
            : steps.All(s => s.State == JobState.Cancelled) ? _text.GetString("JobState_Cancelled")
            : _text.Format("Queue_GroupDone", steps.Count);
    }

    /// <summary>The recording's title; looked up once in the background when it is not known yet (new recordings).</summary>
    private string TitleOf(RecordingId id)
    {
        lock (_titles)
        {
            if (_titles.TryGetValue(id, out var title))
            {
                return title;
            }

            if (!_titleLookups.Add(id))
            {
                return string.Empty;
            }
        }

        _ = LookUpTitleAsync(id);
        return string.Empty;
    }

    private async Task LookUpTitleAsync(RecordingId id)
    {
        var entry = await _library.FindAsync(id).ConfigureAwait(false);
        if (entry is null)
        {
            return;
        }

        lock (_titles)
        {
            _titles[id] = entry.Title;
        }

        _ui.Post(() =>
        {
            foreach (var job in _jobs.Values.Where(j => j.RecordingId == id))
            {
                job.RecordingTitle = entry.Title;
            }

            Regroup();
        });
    }

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="items"/> with moves/inserts/removals only.</summary>
    private static void Sync<T>(ObservableCollection<T> target, List<T> items)
        where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            var at = target.IndexOf(items[i]);
            if (at == i)
            {
                continue;
            }

            if (at > i)
            {
                target.Move(at, i);
            }
            else
            {
                target.Insert(i, items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
