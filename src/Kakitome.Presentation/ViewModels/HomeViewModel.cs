using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Models;
using Kakitome.Application.Settings;
using Kakitome.Application.Updates;
using Kakitome.Domain.Rendering;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

/// <summary>Home: recording first, plus recent recordings, processing status and first-run guidance.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly JobScheduler _jobs;
    private readonly IModelStore _models;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizer _text;
    private readonly INavigationService _navigation;
    private readonly UpdateChecker _updates;
    private readonly ISettingsStore _settings;
    private readonly IShellService _shell;

    public HomeViewModel(
        LibraryService library, JobScheduler jobs, IModelStore models, IUiDispatcher ui, ILocalizer text, INavigationService navigation,
        UpdateChecker updates, ISettingsStore settings, IShellService shell)
    {
        _updates = updates;
        _settings = settings;
        _shell = shell;
        _library = library;
        _jobs = jobs;
        _models = models;
        _ui = ui;
        _text = text;
        _navigation = navigation;
        _library.Changed += (_, _) => _ = LoadAsync();
        _jobs.JobChanged += (_, e) =>
        {
            if (e.Job.IsTerminal || e.Job.State == JobState.Running)
            {
                _ = LoadAsync();
            }
        };
        _updates.AvailableChanged += (_, _) => _ui.Post(ShowUpdate);
        ShowUpdate();
    }

    public ObservableCollection<RecordingItemViewModel> Recent { get; } = [];

    [ObservableProperty]
    public partial string ProcessingText { get; set; } = string.Empty;

    /// <summary>True when no ASR model is installed yet (first run): Home points to Settings › Models.</summary>
    [ObservableProperty]
    public partial bool NeedsModel { get; set; }

    /// <summary>What the "needs a model" notice says: the model being downloaded automatically, or how to get one.</summary>
    [ObservableProperty]
    public partial string NeedsModelText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasRecent { get; set; }

    /// <summary>Result of the last file import (drop or picker).</summary>
    [ObservableProperty]
    public partial string? ImportMessage { get; set; }

    /// <summary>A newer release is published and the user has not closed its notice.</summary>
    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; } = string.Empty;

    private void ShowUpdate()
    {
        var available = _updates.Available;
        HasUpdate = available is not null && available.Version != _settings.Current.General.DismissedUpdate;
        UpdateText = available is null ? string.Empty : _text.Format("Update_Available", available.Version, _updates.CurrentVersion);
    }

    /// <summary>Opens the release page in the browser; Kakitome downloads and installs nothing itself.</summary>
    [RelayCommand]
    private void OpenUpdate()
    {
        if (_updates.Available is { } release)
        {
            _shell.OpenUri(release.Page);
        }
    }

    [RelayCommand]
    private async Task DismissUpdateAsync()
    {
        if (_updates.Available is { } release)
        {
            await _settings.UpdateAsync(s => s.General.DismissedUpdate = release.Version).ConfigureAwait(true);
        }

        HasUpdate = false;
    }

    public async Task LoadAsync()
    {
        var recent = (await _library.ListRecordingsAsync().ConfigureAwait(false)).Take(5).ToList();
        var jobs = await _jobs.ListAsync().ConfigureAwait(false);
        var active = jobs.Count(j => !j.IsTerminal);
        // Nothing running and work held back by power: say why, so it does not look stuck.
        var held = jobs.Any(j => j.State == JobState.Running) ? JobWaitReason.None
            : jobs.Where(j => j.State == JobState.Pending).Select(j => j.WaitReason)
                .FirstOrDefault(r => r is JobWaitReason.OnBattery or JobWaitReason.LowBattery or JobWaitReason.EnergySaver);
        var needsModel = !ModelCatalog.All.Any(m => _models.GetState(m.Id) == ModelState.Installed);
        var downloading = ModelCatalog.All.FirstOrDefault(m => jobs.Any(j =>
            j.Kind == ModelInstallJobHandler.JobKind && !j.IsTerminal && j.Payload == ModelInstallJobHandler.PayloadFor(m.Id)));
        _ui.Post(() =>
        {
            Recent.Clear();
            foreach (var e in recent)
            {
                Recent.Add(new RecordingItemViewModel(e.Id)
                {
                    Title = e.Title,
                    Project = e.Project,
                    DateText = TimeFormat.DateTime(e.RecordedAt ?? e.CreatedAt),
                    DurationText = e.DurationSeconds is { } d ? TimeFormat.Clock(d) : "–",
                });
            }

            HasRecent = Recent.Count > 0;
            NeedsModel = needsModel;
            NeedsModelText = downloading is null
                ? _text.GetString("Home_NeedsModelText")
                : _text.Format("Home_ModelDownloading", downloading.DisplayName);
            ProcessingText = active == 0 ? _text.GetString("Queue_AllDone")
                : held != JobWaitReason.None ? _text.Format("Home_ProcessingWaiting", active, _text.GetString($"Wait_{held}"))
                : _text.Format("Queue_ActiveCount", active);
        });
    }

    [RelayCommand]
    private void Open(RecordingItemViewModel? item)
    {
        if (item is not null)
        {
            _navigation.Navigate(PageKey.RecordingDetail, new RecordingNavigation(item.Id));
        }
    }

    [RelayCommand]
    private void OpenQueue() => _navigation.Navigate(PageKey.Queue);

    [RelayCommand]
    private void OpenModels() => _navigation.Navigate(PageKey.Settings, "models");
}
