using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Library;
using Kakitome.Application.Search;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Rendering;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

public sealed record SearchHitViewModel(RecordingId RecordingId, string KindText, string Snippet, double? AtSeconds)
{
    public string TimeText => AtSeconds is { } s ? TimeFormat.Stamp(s) : string.Empty;

    public string AccessibleName => $"{KindText} {TimeText} {Snippet}";
}

public sealed record SearchGroupViewModel(RecordingId RecordingId, string Title, string Subtitle, IReadOnlyList<SearchHitViewModel> Hits);

/// <summary>Full-text search over titles, transcripts and summaries (local FTS5). Results grouped by recording.</summary>
public sealed partial class SearchViewModel(
    SearchService search,
    LibraryService library,
    IUiDispatcher ui,
    ILocalizer text,
    INavigationService navigation) : ObservableObject
{
    private CancellationTokenSource? _pending;

    public ObservableCollection<SearchGroupViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>Searches 250 ms after typing stops.</summary>
    partial void OnQueryChanged(string value)
    {
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        var token = _pending.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                await RunAsync(value, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    [RelayCommand]
    private Task SearchAsync() => RunAsync(Query, CancellationToken.None);

    [RelayCommand]
    private void Open(SearchHitViewModel? hit)
    {
        if (hit is not null)
        {
            navigation.Navigate(PageKey.RecordingDetail, new RecordingNavigation(hit.RecordingId, hit.AtSeconds));
        }
    }

    internal async Task RunAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            ui.Post(() =>
            {
                Results.Clear();
                StatusText = null;
            });
            return;
        }

        var hits = await search.SearchAsync(query, 200, cancellationToken).ConfigureAwait(false);
        var groups = new List<SearchGroupViewModel>();
        foreach (var group in hits.GroupBy(h => h.RecordingId))
        {
            var entry = await library.FindAsync(group.Key, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue; // stale index row; the next reindex removes it
            }

            groups.Add(new SearchGroupViewModel(
                group.Key,
                entry.Title,
                $"{entry.Project} · {TimeFormat.DateTime(entry.RecordedAt ?? entry.CreatedAt)}",
                group.OrderBy(h => h.Kind).ThenBy(h => h.StartSeconds ?? 0)
                    .Select(h => new SearchHitViewModel(h.RecordingId, text.GetString($"HitKind_{h.Kind}"), h.Snippet, h.StartSeconds))
                    .Take(20)
                    .ToList()));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ui.Post(() =>
        {
            Results.Clear();
            foreach (var g in groups)
            {
                Results.Add(g);
            }

            StatusText = groups.Count == 0 ? text.GetString("Search_NoResults") : text.Format("Search_ResultCount", groups.Count, hits.Count);
        });
    }
}

public sealed record ProjectItemViewModel(string Name, int RecordingCount, string LastText)
{
    public string AccessibleName => $"{Name}, {LastText}";
}

/// <summary>Projects page: the primary organizational unit (docs/01). Create, list, open filtered Library.</summary>
public sealed partial class ProjectsViewModel(RecordingActions actions, IUiDispatcher ui, ILocalizer text, INavigationService navigation)
    : ObservableObject
{
    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];

    [ObservableProperty]
    public partial string NewProjectName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    public async Task LoadAsync()
    {
        var projects = await actions.ListProjectsAsync().ConfigureAwait(false);
        ui.Post(() =>
        {
            Projects.Clear();
            foreach (var p in projects)
            {
                Projects.Add(new ProjectItemViewModel(p.Name, p.RecordingCount,
                    p.LastRecordedAt is { } last
                        ? text.Format("Projects_CountAndLast", p.RecordingCount, TimeFormat.DateTime(last))
                        : text.GetString("Projects_Empty")));
            }
        });
    }

    /// <summary>
    /// Always enabled: a disabled button next to an empty box looked broken, and with Japanese input the text is only
    /// committed when focus leaves the box. An empty or existing name is explained instead.
    /// </summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0)
        {
            MessageIsError = true;
            Message = text.GetString("Projects_NameRequired");
            return;
        }

        if (Projects.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageIsError = true;
            Message = text.Format("Projects_AlreadyExists", name);
            return;
        }

        actions.CreateProject(name);
        NewProjectName = string.Empty;
        MessageIsError = false;
        Message = text.Format("Info_ProjectCreated", name);
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Open(ProjectItemViewModel? project)
    {
        if (project is not null)
        {
            navigation.Navigate(PageKey.Library, project.Name);
        }
    }
}
