namespace Kakitome.Presentation.Services;

/// <summary>Marshals work onto the UI thread (WinUI DispatcherQueue in the app; immediate in tests).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Localized strings (resw in the app). Keys are the same as in <c>tools/strings.py</c>.</summary>
public interface ILocalizer
{
    string GetString(string key);

    string Format(string key, params object?[] args);
}

public enum PageKey
{
    Home,
    Library,
    Projects,
    Queue,
    Search,
    Settings,
    RecordingDetail,
}

/// <summary>Request to open a recording, optionally at a time position.</summary>
public sealed record RecordingNavigation(Kakitome.Domain.Recordings.RecordingId Id, double? AtSeconds = null);

public interface INavigationService
{
    void Navigate(PageKey page, object? parameter = null);
}

/// <summary>Shell interactions that need platform UI.</summary>
public interface IShellService
{
    /// <summary>Shows a confirmation dialog; true when the primary (confirm) button was chosen.</summary>
    Task<bool> ConfirmAsync(string title, string content, string primaryButton, string closeButton);

    /// <summary>Opens a folder in File Explorer (only folders inside Kakitome's known roots).</summary>
    void OpenFolder(string path);

    void OpenUri(Uri uri);

    /// <summary>Lets the user pick media files to import; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickMediaFilesAsync();

    /// <summary>Lets the user choose a .kakitome-backup file (null when cancelled).</summary>
    Task<string?> PickBackupFileAsync();

    /// <summary>Lets the user choose an .exe (null when cancelled).</summary>
    Task<string?> PickExecutableAsync();

    /// <summary>Lets the user choose where to save a file (null when cancelled).</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, string extension);

    /// <summary>Opens File Explorer with the file selected (a file the user just saved).</summary>
    void ShowFile(string path);

    /// <summary>Lets the user choose a .txt file (null when cancelled).</summary>
    Task<string?> PickTextFileAsync();

    /// <summary>Opens a .txt file inside Kakitome's known roots in the user's default text editor.</summary>
    void OpenTextFile(string path);

    /// <summary>Puts plain text on the clipboard.</summary>
    void CopyText(string text);

    /// <summary>The uninstall dialog: data breakdown and keep/remove choices; null when cancelled.</summary>
    Task<Kakitome.Application.Maintenance.UninstallChoices?> ChooseUninstallAsync(Kakitome.Presentation.ViewModels.UninstallPrompt prompt);

    /// <summary>Restarts Kakitome (e.g. to complete a Factory Reset).</summary>
    void RestartApp();

    /// <summary>Opens the Windows page where Kakitome is uninstalled.</summary>
    void OpenAppUninstall();

    /// <summary>Closes Kakitome (stops background work cleanly).</summary>
    void ExitApp();
}
