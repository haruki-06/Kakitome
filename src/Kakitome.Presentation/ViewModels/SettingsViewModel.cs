using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Models;
using Kakitome.Application.Settings;
using Kakitome.Presentation.Services;
using JobsProcessingMode = Kakitome.Application.Jobs.ProcessingMode;

namespace Kakitome.Presentation.ViewModels;

public sealed record Choice(string? Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class ModelItemViewModel(ModelDescriptor model) : ObservableObject
{
    public ModelDescriptor Model { get; } = model;

    public string Name => Model.DisplayName;

    public string Details { get; init; } = string.Empty;

    /// <summary>What the model is good for, e.g. "for PCs without a GPU" (from the benchmarks).</summary>
    public string Note { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsRecommended { get; set; }

    /// <summary>Files are present but failed verification: installing again repairs them.</summary>
    [ObservableProperty]
    public partial bool NeedsRepair { get; set; }

    [ObservableProperty]
    public partial bool IsVerifying { get; set; }

    public bool CanInstall => !IsInstalled && !IsInstalling;

    public bool CanVerify => IsInstalled && !IsVerifying && !IsInstalling;

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanVerify));
    }

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanVerify));
    }

    partial void OnIsVerifyingChanged(bool value) => OnPropertyChanged(nameof(CanVerify));
}

/// <summary>Settings grouped as in docs/02: Recording, Transcription, AI/Summary, Storage, Models, Notifications, About.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IModelStore _models;
    private readonly JobScheduler _scheduler;
    private readonly LibraryService _library;
    private readonly IAccelerationProbe _acceleration;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizer _text;
    private readonly IShellService _shell;
    private bool _loading;

    public SettingsViewModel(
        ISettingsStore settings,
        IModelStore models,
        JobScheduler scheduler,
        LibraryService library,
        IAccelerationProbe acceleration,
        IUiDispatcher ui,
        ILocalizer text,
        IShellService shell,
        Kakitome.Application.Updates.UpdateChecker updates,
        string appVersion)
    {
        _updates = updates;
        _settings = settings;
        _models = models;
        _scheduler = scheduler;
        _library = library;
        _acceleration = acceleration;
        _ui = ui;
        _text = text;
        _shell = shell;
        AppVersion = appVersion;

        ProcessingModes = [
            new(nameof(JobsProcessingMode.Auto), text.GetString("Mode_Auto")),
            new(nameof(JobsProcessingMode.AlwaysProcess), text.GetString("Mode_AlwaysProcess")),
            new(nameof(JobsProcessingMode.BatterySaver), text.GetString("Mode_BatterySaver"))];
        Languages = [new(null, text.GetString("Language_Auto")), new("ja", "日本語"), new("en", "English")];
        UiLanguages = [new(null, text.GetString("UiLanguage_System")), new("ja-JP", "日本語"), new("en-US", "English")];
        SummaryEngines = [
            new(Kakitome.Application.Settings.SummaryEngines.Auto, text.GetString("SummaryEngine_auto")),
            new(Kakitome.Application.Settings.SummaryEngines.Extractive, text.GetString("SummaryEngine_extractive"))];
        Retentions = [.. Kakitome.Application.Audio.RetentionPolicy.All.Select(r => new Choice(r, text.GetString("Retention_" + r)))];
        Themes = [new("system", text.GetString("Theme_System")), new("light", text.GetString("Theme_Light")), new("dark", text.GetString("Theme_Dark"))];
        AsrModels = [new(null, text.GetString("AsrModel_Automatic")), .. ModelCatalog.All.Select(m => new Choice(m.Id, m.DisplayName))];

        _scheduler.JobChanged += (_, e) =>
        {
            if (e.Job.Kind == ModelInstallJobHandler.JobKind)
            {
                _ui.Post(() => OnInstallJob(e.Job));
            }
        };
        Load();
    }

    public string AppVersion { get; }

    public IReadOnlyList<Choice> ProcessingModes { get; }

    public IReadOnlyList<Choice> Languages { get; }

    public IReadOnlyList<Choice> UiLanguages { get; }

    public IReadOnlyList<Choice> Themes { get; }

    public IReadOnlyList<Choice> Retentions { get; }

    public IReadOnlyList<Choice> SummaryEngines { get; }

    [ObservableProperty]
    public partial Choice? SummaryEngine { get; set; }

    public IReadOnlyList<Choice> AsrModels { get; }

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];

    /// <summary>Speech recognition models (Settings › Models, first card group).</summary>
    public ObservableCollection<ModelItemViewModel> AsrModelItems { get; } = [];

    /// <summary>Optional summary AI models (second card group).</summary>
    public ObservableCollection<ModelItemViewModel> SummaryModelItems { get; } = [];

    public string LibraryPath => _library.LibraryRoot;

    public Uri ReleasesUri { get; } = new("https://github.com/haruki-06/Kakitome/releases");

    // Recording
    [ObservableProperty]
    public partial bool IncludeSystemAudio { get; set; }

    [ObservableProperty]
    public partial bool PreventSleep { get; set; }

    [ObservableProperty]
    public partial bool KeepRecordingWhenWindowClosed { get; set; }

    [ObservableProperty]
    public partial Choice? Retention { get; set; }

    [ObservableProperty]
    public partial bool AutomaticBackup { get; set; }

    [ObservableProperty]
    public partial bool LiveTranscript { get; set; }

    [ObservableProperty]
    public partial string ToggleHotkey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? HotkeyError { get; set; }

    // Transcription / AI
    [ObservableProperty]
    public partial Choice? TranscriptionLanguage { get; set; }

    [ObservableProperty]
    public partial Choice? AsrModel { get; set; }

    [ObservableProperty]
    public partial Choice? ProcessingMode { get; set; }

    [ObservableProperty]
    public partial bool AutoProcess { get; set; }

    /// <summary>Download the recommended model and yt-dlp automatically when missing (ADR-034).</summary>
    [ObservableProperty]
    public partial bool AutoDownloadModels { get; set; }

    // General
    [ObservableProperty]
    public partial Choice? Theme { get; set; }

    [ObservableProperty]
    public partial Choice? UiLanguage { get; set; }

    [ObservableProperty]
    public partial bool Notifications { get; set; }

    /// <summary>Daily update check (ADR-040).</summary>
    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    /// <summary>Result of the last "Check now".</summary>
    [ObservableProperty]
    public partial string? UpdateStatus { get; set; }

    private readonly Kakitome.Application.Updates.UpdateChecker _updates;

    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    // Import
    [ObservableProperty]
    public partial string YtDlpPathText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasYtDlpPath { get; set; }

    /// <summary>The managed (hash-pinned) yt-dlp is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallYtDlp))]
    public partial bool IsYtDlpInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallYtDlp))]
    public partial bool IsYtDlpInstalling { get; set; }

    [ObservableProperty]
    public partial string YtDlpStateText { get; set; } = string.Empty;

    public bool CanInstallYtDlp => !IsYtDlpInstalled && !IsYtDlpInstalling;

    /// <summary>Raised when the theme setting changes (the view applies it immediately).</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>Raised when model files were added or removed (the storage breakdown is refreshed).</summary>
    public event EventHandler? StorageChanged;

    [RelayCommand]
    private async Task InstallModelAsync(ModelItemViewModel? item)
    {
        if (item is null || !item.CanInstall)
        {
            return;
        }

        var confirmed = await _shell.ConfirmAsync(
            _text.GetString("ModelDialog_Title"),
            _text.Format("ModelDialog_Content", item.Name, FormatBytes(item.Model.TotalSize), item.Model.Source.Host, item.Model.License),
            _text.GetString("ModelDialog_Primary"),
            _text.GetString("Dialog_Cancel")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        item.IsInstalling = true;
        item.StateText = _text.GetString("Model_Downloading");
        await _scheduler.EnqueueAsync(new JobRequest(ModelInstallJobHandler.JobKind)
        {
            Payload = ModelInstallJobHandler.PayloadFor(item.Model.Id),
            Priority = 20,
            MaxAttempts = 5,
        }).ConfigureAwait(true);
    }

    /// <summary>Re-hashes the installed files against the pinned SHA-256 (Model Manager "verify").</summary>
    [RelayCommand]
    private async Task VerifyModelAsync(ModelItemViewModel? item)
    {
        if (item is null || !item.CanVerify)
        {
            return;
        }

        item.IsVerifying = true;
        item.StateText = _text.GetString("Model_Verifying");
        var ok = await _models.VerifyAsync(item.Model.Id).ConfigureAwait(true);
        item.IsVerifying = false;
        RefreshModels();
        if (Models.FirstOrDefault(m => m.Model.Id == item.Model.Id) is { } refreshed)
        {
            refreshed.StateText = _text.GetString(ok ? "Model_Verified" : "Model_VerifyFailed");
        }
    }

    [RelayCommand]
    private async Task RemoveModelAsync(ModelItemViewModel? item)
    {
        if (item is null || !item.IsInstalled)
        {
            return;
        }

        var confirmed = await _shell.ConfirmAsync(
            _text.GetString("RemoveModelDialog_Title"), _text.Format("RemoveModelDialog_Content", item.Name),
            _text.GetString("RemoveModelDialog_Primary"), _text.GetString("Dialog_Cancel")).ConfigureAwait(true);
        if (confirmed)
        {
            await _models.RemoveAsync(item.Model.Id).ConfigureAwait(true);
            RefreshModels();
            StorageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The user picks a yt-dlp.exe they obtained themselves (Kakitome never searches PATH or downloads it silently).</summary>
    [RelayCommand]
    private async Task ChooseYtDlpAsync()
    {
        if (await _shell.PickExecutableAsync().ConfigureAwait(true) is { Length: > 0 } path)
        {
            await SetYtDlpPathAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>Downloads the pinned yt-dlp after confirmation (verified by SHA-256 like a model; never silently).</summary>
    [RelayCommand]
    private async Task InstallYtDlpAsync()
    {
        var tool = ModelCatalog.Find(ModelCatalog.YtDlp)!;
        var confirmed = await _shell.ConfirmAsync(
            _text.GetString("YtDlpDialog_Title"),
            _text.Format("YtDlpDialog_Content", tool.DisplayName, FormatBytes(tool.TotalSize), tool.Source.Host, tool.License),
            _text.GetString("ModelDialog_Primary"),
            _text.GetString("Dialog_Cancel")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsYtDlpInstalling = true;
        YtDlpStateText = _text.GetString("Model_Downloading");
        await _scheduler.EnqueueAsync(new JobRequest(ModelInstallJobHandler.JobKind)
        {
            Payload = ModelInstallJobHandler.PayloadFor(ModelCatalog.YtDlp),
            Priority = 20,
            MaxAttempts = 5,
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveYtDlpAsync()
    {
        await _models.RemoveAsync(ModelCatalog.YtDlp).ConfigureAwait(true);
        RefreshYtDlp();
    }

    private void RefreshYtDlp()
    {
        var state = _models.GetState(ModelCatalog.YtDlp);
        IsYtDlpInstalled = state == ModelState.Installed;
        if (!IsYtDlpInstalling)
        {
            YtDlpStateText = IsYtDlpInstalled
                ? _text.Format("YtDlp_Installed", ModelCatalog.Find(ModelCatalog.YtDlp)!.DisplayName)
                : _text.GetString(state == ModelState.Invalid ? "ModelState_Invalid" : "YtDlp_NotInstalled");
        }
    }

    [RelayCommand]
    private Task ClearYtDlpAsync() => SetYtDlpPathAsync(null);

    private async Task SetYtDlpPathAsync(string? path)
    {
        await _settings.UpdateAsync(s => s.Processing.YtDlpPath = path).ConfigureAwait(true);
        HasYtDlpPath = path is not null;
        YtDlpPathText = path ?? _text.GetString("Settings_YtDlpNone");
    }

    [RelayCommand]
    private void OpenLibraryFolder() => _shell.OpenFolder(_library.LibraryRoot);

    [RelayCommand]
    private void OpenReleases() => _shell.OpenUri(ReleasesUri);

    partial void OnIncludeSystemAudioChanged(bool value) => Save(s => s.Recording.IncludeSystemAudio = value);

    partial void OnPreventSleepChanged(bool value) => Save(s => s.Recording.PreventSleep = value);

    partial void OnKeepRecordingWhenWindowClosedChanged(bool value) => Save(s => s.Recording.KeepRecordingWhenWindowClosed = value);

    partial void OnAutomaticBackupChanged(bool value) => Save(s => s.Backup.Automatic = value);

    partial void OnSummaryEngineChanged(Choice? value) => Save(s => s.Processing.SummaryEngine = value?.Value ?? Kakitome.Application.Settings.SummaryEngines.Auto);

    partial void OnLiveTranscriptChanged(bool value) => Save(s => s.Recording.LiveTranscript = value);

    partial void OnRetentionChanged(Choice? oldValue, Choice? newValue)
    {
        if (_loading || newValue?.Value is not { } policy)
        {
            return;
        }

        if (policy != Kakitome.Application.Audio.RetentionPolicy.DeleteAfterProcessing)
        {
            Save(s => s.Recording.Retention = policy);
            return;
        }

        _ = ConfirmDeleteAfterProcessingAsync(oldValue);
    }

    /// <summary>Removing audio is an explicit opt-in with a clear confirmation (docs/03); declining restores the choice.</summary>
    private async Task ConfirmDeleteAfterProcessingAsync(Choice? previous)
    {
        var confirmed = await _shell.ConfirmAsync(
            _text.GetString("RetentionDeleteDialog_Title"), _text.GetString("RetentionDeleteDialog_Content"),
            _text.GetString("RetentionDeleteDialog_Primary"), _text.GetString("Dialog_Cancel")).ConfigureAwait(true);
        if (confirmed)
        {
            Save(s => s.Recording.Retention = Kakitome.Application.Audio.RetentionPolicy.DeleteAfterProcessing);
            return;
        }

        _loading = true;
        try
        {
            Retention = previous ?? Retentions[0];
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnToggleHotkeyChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            HotkeyError = null;
            Save(s => s.Recording.ToggleHotkey = string.Empty);
        }
        else if (HotkeyGesture.TryParse(value, out var gesture))
        {
            HotkeyError = null;
            Save(s => s.Recording.ToggleHotkey = gesture.ToString());
        }
        else
        {
            HotkeyError = _text.GetString("Error_InvalidHotkey");
        }
    }

    partial void OnTranscriptionLanguageChanged(Choice? value) => Save(s => s.Processing.TranscriptionLanguage = value?.Value);

    partial void OnAsrModelChanged(Choice? value) => Save(s => s.Processing.AsrModelId = value?.Value);

    partial void OnProcessingModeChanged(Choice? value)
    {
        if (value?.Value is { } v && Enum.TryParse<JobsProcessingMode>(v, out var mode))
        {
            Save(s => s.Processing.Mode = mode);
        }
    }

    partial void OnAutoProcessChanged(bool value) => Save(s => s.Processing.AutoProcess = value);

    partial void OnAutoDownloadModelsChanged(bool value) => Save(s => s.Processing.AutoDownloadModels = value);

    partial void OnNotificationsChanged(bool value) => Save(s => s.General.Notifications = value);

    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s.General.CheckForUpdates = value);

    [RelayCommand]
    private async Task CheckUpdatesNowAsync()
    {
        UpdateStatus = _text.GetString("Update_Checking");
        var result = await _updates.CheckAsync(force: true).ConfigureAwait(true);
        UpdateStatus = result switch
        {
            Kakitome.Application.Updates.UpdateCheckResult.Available when _updates.Available is { } r =>
                _text.Format("Update_Available", r.Version, _updates.CurrentVersion),
            Kakitome.Application.Updates.UpdateCheckResult.UpToDate => _text.Format("Update_UpToDate", _updates.CurrentVersion),
            _ => _text.GetString("Update_Failed"),
        };
    }

    partial void OnThemeChanged(Choice? value)
    {
        Save(s => s.General.Theme = value?.Value ?? "system");
        if (!_loading)
        {
            ThemeChanged?.Invoke(this, value?.Value ?? "system");
        }
    }

    partial void OnUiLanguageChanged(Choice? value)
    {
        Save(s => s.General.UiLanguage = value?.Value);
        if (!_loading)
        {
            RestartRequired = true;
        }
    }

    /// <summary>Re-reads every value from the settings store (after Reset Config).</summary>
    public void Reload() => Load();

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            IncludeSystemAudio = s.Recording.IncludeSystemAudio;
            PreventSleep = s.Recording.PreventSleep;
            KeepRecordingWhenWindowClosed = s.Recording.KeepRecordingWhenWindowClosed;
            Retention = Retentions.FirstOrDefault(c => c.Value == s.Recording.Retention) ?? Retentions[0];
            AutomaticBackup = s.Backup.Automatic;
            SummaryEngine = SummaryEngines.FirstOrDefault(c => c.Value == s.Processing.SummaryEngine) ?? SummaryEngines[0];
            LiveTranscript = s.Recording.LiveTranscript;
            ToggleHotkey = s.Recording.ToggleHotkey;
            TranscriptionLanguage = Languages.FirstOrDefault(c => c.Value == s.Processing.TranscriptionLanguage) ?? Languages[0];
            AsrModel = AsrModels.FirstOrDefault(c => c.Value == s.Processing.AsrModelId) ?? AsrModels[0];
            ProcessingMode = ProcessingModes.First(c => c.Value == s.Processing.Mode.ToString());
            AutoProcess = s.Processing.AutoProcess;
            AutoDownloadModels = s.Processing.AutoDownloadModels;
            Theme = Themes.FirstOrDefault(c => c.Value == s.General.Theme) ?? Themes[0];
            UiLanguage = UiLanguages.FirstOrDefault(c => c.Value == s.General.UiLanguage) ?? UiLanguages[0];
            Notifications = s.General.Notifications;
            CheckForUpdates = s.General.CheckForUpdates;
            HasYtDlpPath = s.Processing.YtDlpPath is { Length: > 0 };
            YtDlpPathText = HasYtDlpPath ? s.Processing.YtDlpPath! : _text.GetString("Settings_YtDlpNone");
            RefreshModels();
            RefreshYtDlp();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshModels()
    {
        var recommendedAsr = AsrDefaults.RecommendedModel(_acceleration.HasCapableGpu);
        var recommendedSummary = SummaryDefaults.RecommendedModel(_acceleration.TotalMemoryBytes);
        Models.Clear();
        AsrModelItems.Clear();
        SummaryModelItems.Clear();
        foreach (var m in ModelCatalog.All.Concat(ModelCatalog.SummaryModels))
        {
            var state = _models.GetState(m.Id);
            var summary = ModelCatalog.SummaryModels.Contains(m);
            var item = new ModelItemViewModel(m)
            {
                Note = _text.GetString("ModelNote_" + m.Id),
                Details = _text.Format("Model_Details", FormatBytes(m.TotalSize), string.Join("/", m.Languages.Where(l => l != "*")), m.License),
                IsInstalled = state == ModelState.Installed,
                NeedsRepair = state == ModelState.Invalid,
                StateText = _text.GetString($"ModelState_{state}"),
                IsRecommended = m.Id == recommendedAsr || m.Id == recommendedSummary,
            };
            Models.Add(item);
            (summary ? SummaryModelItems : AsrModelItems).Add(item);
        }
    }

    private void OnInstallJob(JobRecord job)
    {
        if (job.Payload?.Contains($"\"{ModelCatalog.YtDlp}\"", StringComparison.Ordinal) == true)
        {
            IsYtDlpInstalling = !job.IsTerminal;
            YtDlpStateText = job.State == JobState.Failed
                ? _text.Format("Model_InstallFailed", JobErrorText.Localize(job.LastError, _text))
                : job.IsTerminal ? string.Empty : _text.Format("Model_DownloadingPercent", (int)((job.Progress ?? 0) * 100));
            if (job.IsTerminal && job.State != JobState.Failed)
            {
                RefreshYtDlp();
            }

            return;
        }

        RefreshModels();
        foreach (var item in Models.Where(m => job.Payload?.Contains(m.Model.Id, StringComparison.Ordinal) == true))
        {
            item.IsInstalling = !job.IsTerminal;
            item.Progress = (job.Progress ?? 0) * 100;
            if (job.State == JobState.Failed)
            {
                item.StateText = _text.Format("Model_InstallFailed", JobErrorText.Localize(job.LastError, _text));
            }
            else if (!job.IsTerminal)
            {
                item.StateText = _text.Format("Model_DownloadingPercent", (int)((job.Progress ?? 0) * 100));
            }
        }

        if (job.State == JobState.Succeeded)
        {
            StorageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Save(Action<AppSettings> change)
    {
        if (!_loading)
        {
            _ = _settings.UpdateAsync(change);
        }
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
