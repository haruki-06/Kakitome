using Kakitome.Application.Import;
using Kakitome.Application.Settings;
using Kakitome.Presentation.Services;

namespace Kakitome.App.Services;

/// <summary>Shared entry point for drag/drop, the file picker and URL import; reports problems to the user.</summary>
internal sealed class ImportCoordinator(
    ImportService import, IMediaUrlDownloader downloader, ISettingsStore settings, INavigationService navigation, IShellService shell)
{
    public event EventHandler<string>? Message;

    public async Task PickAndImportAsync() => await ImportAsync(await shell.PickMediaFilesAsync().ConfigureAwait(true)).ConfigureAwait(true);

    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var unsupported = paths.Where(p => !ImportService.IsSupported(p)).ToList();
        var project = settings.Current.Recording.DefaultProject;
        var imported = 0;
        foreach (var path in paths.Except(unsupported))
        {
            await import.ImportFileAsync(path, project).ConfigureAwait(true);
            imported++;
        }

        Message?.Invoke(this, unsupported.Count > 0
            ? Strings.Format("Error_ImportUnsupported", string.Join(", ", unsupported.Select(Path.GetFileName)))
            : Strings.Format("Info_Importing", imported, ProjectName(project)));
        if (imported > 0)
        {
            navigation.Navigate(PageKey.Library);
        }
    }

    /// <summary>Asks for a media URL (prefilled when a link was dropped) and queues the download.</summary>
    public async Task PromptAndImportUrlAsync(Microsoft.UI.Xaml.XamlRoot root, string? initial = null)
    {
        if (!downloader.IsAvailable)
        {
            navigation.Navigate(PageKey.Settings, "import");
            return;
        }

        var box = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Text = initial ?? string.Empty,
            PlaceholderText = Strings.Get("ImportUrl_Placeholder"),
            MinWidth = 420,
            InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Url) } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Strings.Get("ImportUrl_Title"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "ImportUrlBox");
        var error = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = Strings.Get("ImportUrl_Invalid"),
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8 };
        panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = Strings.Get("ImportUrl_Help"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 420 });
        panel.Children.Add(box);
        panel.Children.Add(error);

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = root,
            Title = Strings.Get("ImportUrl_Title"),
            Content = panel,
            PrimaryButtonText = Strings.Get("ImportUrl_Start"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (sender, e) =>
        {
            // Keep the dialog open until the address is acceptable.
            var valid = MediaUrl.TryParse(box.Text, out _);
            error.Visibility = valid ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
            e.Cancel = !valid;
        };

        if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || !MediaUrl.TryParse(box.Text, out var url))
        {
            return;
        }

        var project = settings.Current.Recording.DefaultProject;
        await import.ImportUrlAsync(url.AbsoluteUri, project).ConfigureAwait(true);
        Message?.Invoke(this, Strings.Format("Info_ImportingUrl", url.Host, ProjectName(project)));
        navigation.Navigate(PageKey.Library);
    }

    /// <summary>Imports go to the project chosen on Home (saved as the default project); none means Inbox.</summary>
    private static string ProjectName(string? project) =>
        string.IsNullOrWhiteSpace(project) ? Kakitome.Domain.Library.LibraryLayout.DefaultProjectName : project;
}
