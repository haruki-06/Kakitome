using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Maintenance;
using Kakitome.Domain.Rendering;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

/// <summary>Settings &gt; Storage and Maintenance (docs/02, docs/06).</summary>
public sealed partial class MaintenanceViewModel(
    MaintenanceService maintenance,
    IAppLocations locations,
    Kakitome.Application.Backup.BackupService backups,
    Kakitome.Application.Jobs.JobScheduler scheduler,
    IShellService shell,
    ILocalizer text,
    IUiDispatcher ui,
    Kakitome.Application.Diagnostics.DiagnosticsService diagnostics,
    string appVersion) : ObservableObject
{
    private StorageBreakdown? _sizes;

    [ObservableProperty]
    public partial string LibrarySize { get; set; } = "…";

    [ObservableProperty]
    public partial string ModelsSize { get; set; } = "…";

    [ObservableProperty]
    public partial string CacheSize { get; set; } = "…";

    [ObservableProperty]
    public partial string AppDataSize { get; set; } = "…";

    [ObservableProperty]
    public partial string BackupsSize { get; set; } = "…";

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsMessageError { get; set; }

    public string BackupsPath => locations.BackupsDirectory;

    [ObservableProperty]
    public partial string LastBackupText { get; set; } = string.Empty;

    /// <summary>Raised after Reset Config so the settings view reloads its values.</summary>
    public event EventHandler? ConfigReset;

    public async Task LoadAsync()
    {
        var sizes = await maintenance.MeasureAsync().ConfigureAwait(false);
        var last = backups.LastBackupAt();
        ui.Post(() =>
        {
            LastBackupText = last is { } at
                ? text.Format("Backup_Last", TimeFormat.DateTime(at))
                : text.GetString("Backup_None");
            _sizes = sizes;
            LibrarySize = SettingsViewModel.FormatBytes(sizes.Library);
            ModelsSize = SettingsViewModel.FormatBytes(sizes.Models);
            CacheSize = SettingsViewModel.FormatBytes(sizes.Cache);
            AppDataSize = SettingsViewModel.FormatBytes(sizes.AppData);
            BackupsSize = SettingsViewModel.FormatBytes(sizes.Backups);
        });
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var freed = await maintenance.ClearCacheAsync().ConfigureAwait(true);
        Show(text.Format("Maintenance_CacheCleared", SettingsViewModel.FormatBytes(freed)));
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        await scheduler.EnqueueAsync(new Kakitome.Application.Jobs.JobRequest(Kakitome.Application.Backup.BackupJobHandler.JobKind)
        {
            Payload = Kakitome.Application.Backup.BackupJobHandler.PayloadFor("manual", appVersion),
            Priority = 10,
        }).ConfigureAwait(true);
        Show(text.GetString("Backup_Queued"));
    }

    /// <summary>Restores recordings missing from the Library; existing recordings are never overwritten.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (await shell.PickBackupFileAsync().ConfigureAwait(true) is not { } file)
        {
            return;
        }

        if (!await shell.ConfirmAsync(
                text.GetString("Restore_Title"), text.Format("Restore_Content", Path.GetFileName(file)),
                text.GetString("Restore_Primary"), text.GetString("Dialog_Cancel")).ConfigureAwait(true))
        {
            return;
        }

        await scheduler.EnqueueAsync(new Kakitome.Application.Jobs.JobRequest(Kakitome.Application.Backup.RestoreJobHandler.JobKind)
        {
            Payload = Kakitome.Application.Backup.RestoreJobHandler.PayloadFor(file),
            Priority = 10,
        }).ConfigureAwait(true);
        Show(text.GetString("Restore_Queued"));
    }

    /// <summary>Saves the diagnostics zip (logs, PC, settings, processing history) for an issue or a field test report.</summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var suggested = Kakitome.Application.Diagnostics.DiagnosticsService.SuggestedFileName(DateTimeOffset.Now);
        if (await shell.PickSaveFileAsync(suggested, ".zip").ConfigureAwait(true) is not { } path)
        {
            return;
        }

        try
        {
            await diagnostics.ExportAsync(path).ConfigureAwait(true);
            Show(text.GetString("Diagnostics_Saved"));
            shell.ShowFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(text.Format("Diagnostics_Failed", ex.Message), error: true);
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(diagnostics.LogsDirectory);
        shell.OpenFolder(diagnostics.LogsDirectory);
    }

    [RelayCommand]
    private void OpenBackups()
    {
        Directory.CreateDirectory(locations.BackupsDirectory);
        shell.OpenFolder(locations.BackupsDirectory);
    }

    [RelayCommand]
    private async Task ResetConfigAsync()
    {
        if (!await shell.ConfirmAsync(
                text.GetString("ResetConfig_Title"), text.GetString("ResetConfig_Content"),
                text.GetString("ResetConfig_Primary"), text.GetString("Dialog_Cancel")).ConfigureAwait(true))
        {
            return;
        }

        await maintenance.ResetConfigAsync().ConfigureAwait(true);
        ConfigReset?.Invoke(this, EventArgs.Empty);
        Show(text.GetString("Maintenance_ConfigReset"));
    }

    [RelayCommand]
    private async Task FactoryResetAsync()
    {
        if (!await shell.ConfirmAsync(
                text.GetString("FactoryReset_Title"), text.Format("FactoryReset_Content", LibrarySize),
                text.GetString("FactoryReset_Primary"), text.GetString("Dialog_Cancel")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await maintenance.RequestFactoryResetAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            Show(text.GetString("Maintenance_StopRecordingFirst"), error: true);
            return;
        }

        shell.RestartApp();
    }

    /// <summary>
    /// Shows what Kakitome stores and lets the user choose what to remove, applies it, then opens the Windows
    /// uninstall page and closes Kakitome. Library and Backups are kept by default.
    /// </summary>
    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (_sizes is null)
        {
            await LoadAsync().ConfigureAwait(true);
        }

        var choices = await shell.ChooseUninstallAsync(new UninstallPrompt(
            LibrarySize, ModelsSize, BackupsSize, SettingsViewModel.FormatBytes((_sizes?.AppData ?? 0) + (_sizes?.Cache ?? 0)),
            ModelsRemovedWithApp: locations.IsPackaged)).ConfigureAwait(true);
        if (choices is null)
        {
            return;
        }

        UninstallResult result;
        try
        {
            result = await maintenance.PrepareUninstallAsync(choices).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            Show(text.GetString("Maintenance_StopRecordingFirst"), error: true);
            return;
        }

        if (result.Problems.Count > 0)
        {
            // Something could not be removed: stay open and say what, rather than continuing silently.
            Show(text.Format("Maintenance_UninstallProblems", string.Join(", ", result.Problems)), error: true);
            return;
        }

        shell.OpenAppUninstall();
        shell.ExitApp();
    }

    private void Show(string message, bool error = false)
    {
        IsMessageError = error;
        Message = message;
    }
}

/// <summary>Data shown in the uninstall dialog.</summary>
public sealed record UninstallPrompt(string LibrarySize, string ModelsSize, string BackupsSize, string AppStateSize, bool ModelsRemovedWithApp);
