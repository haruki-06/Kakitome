using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Kakitome.Presentation.Services;
using Windows.Storage.Pickers;

namespace Kakitome.App.Services;

internal sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }
}

internal sealed class ResourceLocalizer : ILocalizer
{
    public string GetString(string key) => Strings.Get(key);

    public string Format(string key, params object?[] args) => Strings.Format(key, args);
}

/// <summary>Frame-based navigation; the shell registers the frame once the window exists.</summary>
public sealed class NavigationService : INavigationService
{
    private static readonly Dictionary<PageKey, Type> Pages = new()
    {
        [PageKey.Home] = typeof(Views.HomePage),
        [PageKey.Library] = typeof(Views.LibraryPage),
        [PageKey.Projects] = typeof(Views.ProjectsPage),
        [PageKey.Queue] = typeof(Views.QueuePage),
        [PageKey.Search] = typeof(Views.SearchPage),
        [PageKey.Settings] = typeof(Views.SettingsPage),
        [PageKey.RecordingDetail] = typeof(Views.RecordingDetailPage),
    };

    public Frame? Frame { get; set; }

    public event EventHandler<PageKey>? Navigated;

    public void Navigate(PageKey page, object? parameter = null)
    {
        if (Frame is null)
        {
            return;
        }

        Frame.Navigate(Pages[page], parameter);
        Navigated?.Invoke(this, page);
    }

    public static PageKey? KeyOf(Type pageType) => Pages.FirstOrDefault(p => p.Value == pageType) is { Value: not null } kv ? kv.Key : null;
}

/// <summary>Dialogs, Explorer, browser and file picker for the main window.</summary>
internal sealed class ShellService(Func<Window?> window, Func<IEnumerable<string>> allowedRoots, Action restart, Action exit) : IShellService
{
    public void RestartApp() => restart();

    public void ExitApp() => exit();

    public void OpenAppUninstall()
    {
        // Packaged: Kakitome's own page in Installed apps; otherwise the Installed apps list.
        string? family = null;
        try
        {
            family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(family is null ? "ms-settings:appsfeatures" : "ms-settings:appsfeatures-app?" + family));
    }

    public async Task<Kakitome.Application.Maintenance.UninstallChoices?> ChooseUninstallAsync(Kakitome.Presentation.ViewModels.UninstallPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (window()?.Content is not FrameworkElement root)
        {
            return null;
        }

        CheckBox Option(string text, string id, bool isChecked = false, bool enabled = true)
        {
            var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = isChecked, IsEnabled = enabled };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, text);
            return box;
        }

        var library = Option(Strings.Format("Uninstall_RemoveLibrary", prompt.LibrarySize), "UninstallLibrary");
        var models = prompt.ModelsRemovedWithApp
            ? Option(Strings.Format("Uninstall_ModelsWithApp", prompt.ModelsSize), "UninstallModels", isChecked: true, enabled: false)
            : Option(Strings.Format("Uninstall_RemoveModels", prompt.ModelsSize), "UninstallModels");
        var backups = Option(Strings.Format("Uninstall_RemoveBackups", prompt.BackupsSize), "UninstallBackups");
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = Strings.Get("Uninstall_Intro"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(library);
        panel.Children.Add(models);
        panel.Children.Add(backups);
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Format("Uninstall_AppState", prompt.AppStateSize),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = Strings.Get("Uninstall_Title"),
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = Strings.Get("Uninstall_Primary"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return new Kakitome.Application.Maintenance.UninstallChoices(
            library.IsChecked == true, models.IsChecked == true && models.IsEnabled, backups.IsChecked == true);
    }

    public async Task<bool> ConfirmAsync(string title, string content, string primaryButton, string closeButton)
    {
        if (window()?.Content is not FrameworkElement root)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryButton,
            CloseButtonText = closeButton,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        // Only folders Kakitome manages; never an arbitrary string from content.
        if (!Directory.Exists(full) || !allowedRoots().Any(root => full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(full);
        Process.Start(psi)?.Dispose();
    }

    public void OpenUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is "https")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    public async Task<IReadOnlyList<string>> PickMediaFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary, ViewMode = PickerViewMode.List };
        foreach (var ext in Import.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        if (window() is { } w)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(w));
        }

        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? [];
    }

    public async Task<string?> PickBackupFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(Kakitome.Application.Backup.BackupService.Extension);
        if (window() is { } w)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(w));
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Desktop, SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        if (window() is { } w)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(w));
        }

        return (await picker.PickSaveFileAsync())?.Path;
    }

    public void ShowFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            return;
        }

        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/select," + full);
        Process.Start(psi)?.Dispose();
    }

    public async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".exe");
        if (window() is { } w)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(w));
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickTextFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".txt");
        if (window() is { } w)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(w));
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    public void OpenTextFile(string path)
    {
        var full = Path.GetFullPath(path);
        // Only text files in folders Kakitome manages, so this never launches arbitrary content.
        if (!File.Exists(full) || !full.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || !allowedRoots().Any(root => full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
    }

    public void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        try
        {
            // Hand the text to Windows now: otherwise the clipboard asks Kakitome for it on exit, which crashed the app
            // when the installer closed it.
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Clipboard busy: the text is still on it while Kakitome runs.
        }
    }
}

/// <summary>Media types Kakitome imports (decoded by Windows Media Foundation).</summary>
internal static class Import
{
    public static readonly string[] SupportedExtensions = [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".ogg", ".opus"];
}
