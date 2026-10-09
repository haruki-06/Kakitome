using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Kakitome.App.Services;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Application.Settings;

namespace Kakitome.App.ViewModels;

/// <summary>An endpoint choice; <see cref="Id"/> null means "follow the Windows default".</summary>
public sealed record DeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class StreamViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>0..100 on a -60..0 dBFS scale.</summary>
    [ObservableProperty]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial bool IsProblem { get; set; }
}

/// <summary>Recording controls shared by the main window, tray and hotkey.</summary>
public sealed partial class RecordingViewModel : ObservableObject, IDisposable
{
    private readonly RecordingService _recording;
    private readonly IAudioDeviceCatalog _devices;
    private readonly ISettingsStore _settings;
    private readonly RecordingActions _actions;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private bool _loadingDevices;

    public RecordingViewModel(RecordingService recording, IAudioDeviceCatalog devices, ISettingsStore settings, RecordingActions actions)
    {
        _recording = recording;
        _actions = actions;
        _devices = devices;
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => RefreshStatus();

        var s = settings.Current.Recording;
        IncludeSystemAudio = s.IncludeSystemAudio;
        Project = string.IsNullOrWhiteSpace(s.DefaultProject) ? Kakitome.Domain.Library.LibraryLayout.DefaultProjectName : s.DefaultProject;
        Projects.Add(Project);
        LoadDevices();

        // Applications are listed only when system audio is on (and on opening the list): enumerating audio sessions
        // and processes must not slow down start-up.
        if (SystemAudioSources.Count == 0)
        {
            SystemAudioSources.Add(new AudioSourceChoice(null, Strings.Get("Recording_AllPcAudio")));
            SelectedSystemAudioSource = SystemAudioSources[0];
        }

        _recording.StateChanged += (_, _) => _dispatcher.TryEnqueue(RefreshStatus);
        _devices.DevicesChanged += (_, _) => _dispatcher.TryEnqueue(LoadDevices);
        RefreshStatus();
    }

    public ObservableCollection<DeviceChoice> Microphones { get; } = [];

    public ObservableCollection<DeviceChoice> OutputDevices { get; } = [];

    /// <summary>"All PC audio" first, then applications that have audio (per-application capture, docs/03).</summary>
    public ObservableCollection<AudioSourceChoice> SystemAudioSources { get; } = [];

    public bool IsApplicationCaptureSupported => _devices.IsApplicationCaptureSupported;

    public ObservableCollection<StreamViewModel> Streams { get; } = [];

    [ObservableProperty]
    public partial DeviceChoice? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial DeviceChoice? SelectedOutputDevice { get; set; }

    [ObservableProperty]
    public partial bool IncludeSystemAudio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSystemAudio))]
    public partial AudioSourceChoice? SelectedSystemAudioSource { get; set; }

    /// <summary>The output-device choice only applies when recording all PC audio.</summary>
    public bool IsAllSystemAudio => SelectedSystemAudioSource?.ProcessId is null;

    /// <summary>Existing projects for the project drop-down (plus the current choice).</summary>
    public ObservableCollection<string> Projects { get; } = [];

    [ObservableProperty]
    public partial string Project { get; set; } = string.Empty;

    partial void OnProjectChanged(string oldValue, string newValue)
    {
        // Rebuilding the list briefly clears the ComboBox selection (null); keep the user's choice.
        if (newValue is null)
        {
            Project = oldValue ?? Kakitome.Domain.Library.LibraryLayout.DefaultProjectName;
            return;
        }

        // File and URL imports go to the project chosen here too, so the choice is saved right away (not only when a
        // recording starts).
        var chosen = string.IsNullOrWhiteSpace(newValue) ? null : newValue.Trim();

        // While recording, the choice applies to the recording in progress: it moves there when stopped.
        if (_recording?.Current is { } session)
        {
            session.TargetProject = chosen ?? Kakitome.Domain.Library.LibraryLayout.DefaultProjectName;
        }

        if (_settings is not null && !string.Equals(_settings.Current.Recording.DefaultProject, chosen, StringComparison.Ordinal))
        {
            _ = _settings.UpdateAsync(s => s.Recording.DefaultProject = chosen);
        }
    }

    /// <summary>Reloads the project list (projects may have been created on the Projects page or in Explorer).</summary>
    public async Task LoadProjectsAsync()
    {
        var current = string.IsNullOrWhiteSpace(Project) ? Kakitome.Domain.Library.LibraryLayout.DefaultProjectName : Project;
        var names = (await _actions.ListProjectsAsync().ConfigureAwait(true))
            .Select(p => p.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!names.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, current);
        }

        if (names.SequenceEqual(Projects, StringComparer.Ordinal))
        {
            return;
        }

        // Update in place: clearing the list would clear the drop-down's selection. The current project is always
        // in the new list, so its item is never removed.
        for (var i = 0; i < names.Count; i++)
        {
            var at = Projects.IndexOf(names[i]);
            if (at == i)
            {
                continue;
            }

            if (at > i)
            {
                Projects.Move(at, i);
            }
            else
            {
                Projects.Insert(i, names[i]);
            }
        }

        while (Projects.Count > names.Count)
        {
            Projects.RemoveAt(Projects.Count - 1);
        }

        Project = names.First(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates a project folder and selects it (for the recording in progress too).</summary>
    public void AddProject(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        _actions.CreateProject(trimmed);
        var existing = Projects.FirstOrDefault(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Projects.Add(trimmed);
            existing = trimmed;
        }

        Project = existing;
    }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsActive), nameof(PauseResumeText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseResumeCommand), nameof(StopCommand), nameof(CancelCommand))]
    public partial RecordingState? State { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsMessageError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => State is null;

    public bool IsActive => State is not null;

    public string PauseResumeText => Strings.Get(State is RecordingState.Paused ? "Recording_Resume" : "Recording_Pause");

    /// <summary>Raised when the recording ended by itself (disk full, write error).</summary>
    public event EventHandler? StoppedUnexpectedly;

    public void Dispose() => _timer.Stop();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var options = _settings.Current.Recording.ToOptions() with
            {
                Project = string.IsNullOrWhiteSpace(Project) ? null : Project.Trim(),
                Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
                MicrophoneId = SelectedMicrophone?.Id,
                IncludeSystemAudio = IncludeSystemAudio,
                SystemAudioDeviceId = SelectedOutputDevice?.Id,
                ApplicationProcessId = IncludeSystemAudio ? SelectedSystemAudioSource?.ProcessId : null,
                ApplicationName = IncludeSystemAudio ? SelectedSystemAudioSource?.Name : null,
            };
            await _recording.StartAsync(options).ConfigureAwait(true);
            if (_recording.Current is { } session)
            {
                session.EndedUnexpectedly += OnEndedUnexpectedly;
            }

            await _settings.UpdateAsync(s => s.Recording.DefaultProject = options.Project).ConfigureAwait(true);
        }
        catch (RecordingStartException ex)
        {
            ShowError(ex.Failure switch
            {
                RecordingStartFailure.InsufficientDiskSpace => Strings.Get("Error_InsufficientDisk"),
                RecordingStartFailure.DeviceUnavailable => Strings.Get("Error_DeviceUnavailable"),
                RecordingStartFailure.NoSources => Strings.Get("Error_NoSources"),
                _ => Strings.Get("Error_AlreadyRecording"),
            });
        }
#pragma warning disable CA1031 // Any other failure is reported to the user instead of crashing the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Write(ex);
            ShowError(Strings.Get("Error_StartFailed"));
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private bool CanStart() => IsIdle && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task PauseResumeAsync() =>
        State == RecordingState.Paused ? _recording.ResumeAsync() : _recording.PauseAsync();

    [RelayCommand(CanExecute = nameof(CanControl))]
    private async Task StopAsync()
    {
        var session = _recording.Current;
        try
        {
            await _recording.StopAsync().ConfigureAwait(true);
            if (session is not null)
            {
                // Read after stopping: the folder moves if the project was changed while recording.
                ShowInfo(Strings.Format("Info_Saved", session.FolderPath));
            }
        }
#pragma warning disable CA1031 // Reported to the user; the audio stays on disk and is recovered at next start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Write(ex);
            ShowError(Strings.Get("Error_StopFailed"));
        }
    }

    /// <summary>Discards the recording (Recycle Bin). The view asks for confirmation first.</summary>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task CancelAsync() => _recording.CancelAsync();

    private bool CanControl() => State is RecordingState.Recording or RecordingState.Paused or RecordingState.Suspended;

    partial void OnIncludeSystemAudioChanged(bool value)
    {
        Persist(s => s.IncludeSystemAudio = value);
        if (value)
        {
            RefreshAudioApplications();
        }
    }

    /// <summary>Re-lists applications with audio (on opening the list); keeps the current choice when still present.</summary>
    [RelayCommand]
    public void RefreshAudioApplications()
    {
        var selectedPid = SelectedSystemAudioSource?.ProcessId;
        SystemAudioSources.Clear();
        SystemAudioSources.Add(new AudioSourceChoice(null, Strings.Get("Recording_AllPcAudio")));
        if (_devices.IsApplicationCaptureSupported)
        {
            foreach (var app in _devices.GetAudioApplications())
            {
                SystemAudioSources.Add(new AudioSourceChoice(app.ProcessId, app.Name));
            }
        }

        SelectedSystemAudioSource = SystemAudioSources.FirstOrDefault(c => c.ProcessId == selectedPid) ?? SystemAudioSources[0];
    }

    partial void OnSelectedMicrophoneChanged(DeviceChoice? value) => Persist(s => s.MicrophoneId = value?.Id);

    partial void OnSelectedOutputDeviceChanged(DeviceChoice? value) => Persist(s => s.SystemAudioDeviceId = value?.Id);

    partial void OnIsBusyChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    private void Persist(Action<RecordingSettings> change)
    {
        if (!_loadingDevices)
        {
            _ = _settings.UpdateAsync(s => change(s.Recording));
        }
    }

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            var settings = _settings.Current.Recording;
            Fill(Microphones, _devices.GetMicrophones());
            Fill(OutputDevices, _devices.GetOutputDevices());
            SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == settings.MicrophoneId) ?? Microphones.FirstOrDefault();
            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == settings.SystemAudioDeviceId) ?? OutputDevices.FirstOrDefault();
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    private static void Fill(ObservableCollection<DeviceChoice> target, IReadOnlyList<AudioDeviceInfo> devices)
    {
        target.Clear();
        var defaultName = devices.FirstOrDefault(d => d.IsDefault)?.Name;
        target.Add(new DeviceChoice(null, defaultName is null ? Strings.Get("Recording_DefaultDeviceNone") : Strings.Format("Recording_DefaultDevice", defaultName)));
        foreach (var device in devices)
        {
            target.Add(new DeviceChoice(device.Id, device.Name));
        }
    }

    private void RefreshStatus()
    {
        var session = _recording.Current;
        if (session is null)
        {
            _timer.Stop();
            State = null;
            StatusText = Strings.Get("State_Idle");
            ElapsedText = "00:00:00";
            Streams.Clear();
            return;
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }

        var status = session.GetStatus();
        State = status.State;
        StatusText = Strings.Get($"State_{status.State}");
        ElapsedText = FormatElapsed(status.Elapsed);

        while (Streams.Count < status.Streams.Count)
        {
            Streams.Add(new StreamViewModel());
        }

        while (Streams.Count > status.Streams.Count)
        {
            Streams.RemoveAt(Streams.Count - 1);
        }

        for (var i = 0; i < status.Streams.Count; i++)
        {
            var source = status.Streams[i];
            var item = Streams[i];
            item.Name = $"{Strings.Get($"Kind_{source.Kind}")}: {source.DisplayName}";
            item.Detail = Strings.Get($"Stream_{source.State}");
            item.IsProblem = source.State is StreamState.Reconnecting or StreamState.Failed;
            item.Level = ToMeter(source.Peak);
        }
    }

    private void OnEndedUnexpectedly(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        ShowError(Strings.Get("Info_StoppedUnexpectedly"));
        StoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
    });

    private void ShowError(string text)
    {
        IsMessageError = true;
        Message = text;
    }

    private void ShowInfo(string text)
    {
        IsMessageError = false;
        Message = text;
    }

    internal static string FormatElapsed(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");

    private static double ToMeter(float peak)
    {
        if (peak <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1) * 100;
    }
}

/// <summary>A system-audio source: all PC audio (null process) or one application.</summary>
public sealed record AudioSourceChoice(int? ProcessId, string Name)
{
    public override string ToString() => Name;
}
