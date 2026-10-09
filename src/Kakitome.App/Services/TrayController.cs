using Microsoft.UI.Dispatching;
using Kakitome.App.ViewModels;
using Kakitome.Application.Recording;
using Kakitome.Application.Settings;
using Kakitome.Infrastructure.Shell;

namespace Kakitome.App.Services;

/// <summary>Notification-area icon, its menu and the global start/stop hotkey. UI thread only.</summary>
internal sealed class TrayController : IDisposable
{
    private const int ToggleHotkeyId = 1;
    private const int MenuStart = 1, MenuPauseResume = 2, MenuStop = 3, MenuOpen = 4, MenuExit = 5;

    private readonly NotificationAreaHost _host;
    private readonly RecordingService _recording;
    private readonly RecordingViewModel _viewModel;
    private readonly ISettingsStore _settings;
    private readonly Action _showWindow;
    private readonly Func<Task> _exit;
    private readonly DispatcherQueue _dispatcher;
    private string? _registeredHotkey;

    public TrayController(
        RecordingService recording,
        RecordingViewModel viewModel,
        ISettingsStore settings,
        Action showWindow,
        Func<Task> exit)
    {
        _recording = recording;
        _viewModel = viewModel;
        _settings = settings;
        _showWindow = showWindow;
        _exit = exit;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _host = new NotificationAreaHost(IconPath(recording: false), Strings.Get("Tray_Tooltip_Idle"));
        _host.Invoked += (_, _) => _showWindow();
        _host.MenuRequested += (_, _) => ShowMenu();
        _host.HotkeyPressed += (_, id) =>
        {
            if (id == ToggleHotkeyId)
            {
                _ = ToggleAsync();
            }
        };

        _recording.StateChanged += (_, _) => _dispatcher.TryEnqueue(UpdateIcon);
        _settings.Changed += (_, _) => _dispatcher.TryEnqueue(RegisterHotkey);
        RegisterHotkey();
    }

    public void Dispose() => _host.Dispose();

    private void ShowMenu()
    {
        var state = _recording.Current?.State;
        var items = new List<TrayMenuItem>();
        if (state is null)
        {
            items.Add(new TrayMenuItem(MenuStart, Strings.Get("Tray_Start"), _viewModel.StartCommand.CanExecute(null)));
        }
        else
        {
            items.Add(new TrayMenuItem(MenuPauseResume, Strings.Get(state == RecordingState.Paused ? "Recording_Resume" : "Recording_Pause")));
            items.Add(new TrayMenuItem(MenuStop, Strings.Get("Tray_Stop")));
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(MenuOpen, Strings.Get("Tray_Open")));
        items.Add(new TrayMenuItem(MenuExit, Strings.Get("Tray_Exit")));

        switch (_host.ShowMenu(items))
        {
            case MenuStart:
                _ = StartAsync();
                break;
            case MenuPauseResume:
                _ = _viewModel.PauseResumeCommand.ExecuteAsync(null);
                break;
            case MenuStop:
                _ = _viewModel.StopCommand.ExecuteAsync(null);
                break;
            case MenuOpen:
                _showWindow();
                break;
            case MenuExit:
                _ = _exit();
                break;
        }
    }

    private async Task ToggleAsync()
    {
        if (_recording.Current is null)
        {
            await StartAsync().ConfigureAwait(true);
        }
        else
        {
            await _viewModel.StopCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    private async Task StartAsync()
    {
        await _viewModel.StartCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (_recording.Current is null)
        {
            // Start failed: surface the error message in the window.
            _showWindow();
        }
    }

    private void UpdateIcon()
    {
        var recording = _recording.Current is not null;
        _host.SetIcon(IconPath(recording));
        _host.SetTooltip(Strings.Get(recording ? "Tray_Tooltip_Recording" : "Tray_Tooltip_Idle"));
    }

    private void RegisterHotkey()
    {
        var text = _settings.Current.Recording.ToggleHotkey;
        if (string.Equals(text, _registeredHotkey, StringComparison.Ordinal))
        {
            return;
        }

        _host.UnregisterHotkey(ToggleHotkeyId);
        _registeredHotkey = text;
        if (HotkeyGesture.TryParse(text, out var gesture) && !_host.RegisterHotkey(ToggleHotkeyId, gesture))
        {
            _viewModel.Message = Strings.Format("Info_HotkeyUnavailable", gesture.ToString());
            _viewModel.IsMessageError = false;
        }
    }

    private static string IconPath(bool recording) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", recording ? "KakitomeRecording.ico" : "Kakitome.ico");
}
