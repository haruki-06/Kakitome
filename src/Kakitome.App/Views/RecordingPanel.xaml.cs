using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Kakitome.App.Services;
using Kakitome.App.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class RecordingPanel : UserControl
{
    public RecordingPanel()
    {
        ViewModel = App.Current.Services.GetRequiredService<RecordingViewModel>();
        Live = App.Current.Services.GetRequiredService<Kakitome.Presentation.ViewModels.LiveTranscriptViewModel>();
        InitializeComponent();
        Live.LineAdded += OnLiveLineAdded;
        Unloaded += (_, _) => Live.LineAdded -= OnLiveLineAdded;
        Loaded += async (_, _) => await ViewModel.LoadProjectsAsync();
    }

    public RecordingViewModel ViewModel { get; }

    public Kakitome.Presentation.ViewModels.LiveTranscriptViewModel Live { get; }

    /// <summary>Auto-scroll to the newest line.</summary>
    private void OnLiveLineAdded(object? sender, Kakitome.Presentation.ViewModels.LiveLineViewModel line) =>
        LiveList.ScrollIntoView(line, ScrollIntoViewAlignment.Default);

#pragma warning disable CA1822 // x:Bind function bindings must be instance members.
    public InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public double OpacityOf(bool enabled) => enabled ? 1.0 : 0.6;

    public Visibility ShowAppChoice(bool includeSystemAudio) =>
        includeSystemAudio && ViewModel.IsApplicationCaptureSupported ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowOutputDevice(bool includeSystemAudio, bool allSystemAudio) =>
        includeSystemAudio && allSystemAudio ? Visibility.Visible : Visibility.Collapsed;
#pragma warning restore CA1822

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Message = null;

    private void OnAudioSourcesOpened(object? sender, object e)
    {
        // Refresh only when the list would change, so the open dropdown is not reset needlessly.
        var before = ViewModel.SystemAudioSources.Select(c => c.ProcessId).ToList();
        var now = App.Current.Services.GetRequiredService<Kakitome.Application.Recording.IAudioDeviceCatalog>()
            .GetAudioApplications().Select(a => (int?)a.ProcessId).Prepend(null).ToList();
        if (!before.SequenceEqual(now))
        {
            ViewModel.RefreshAudioApplications();
        }
    }

    private async void OnNewProjectClicked(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = Strings.Get("NewProjectDialog_Placeholder"), MinWidth = 320 };
        name.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "NewProjectDialogName");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("NewProjectDialog_Title"),
            Content = name,
            PrimaryButtonText = Strings.Get("NewProjectDialog_Primary"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        // The text is read when the dialog closes (after any Japanese input has been committed), not bound per keystroke.
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text))
        {
            ViewModel.AddProject(name.Text);
        }
    }

    private async void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        // Discarding is user-directed but still needs an explicit confirmation (docs/00: no silent destruction).
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("CancelDialog_Title"),
            Content = Strings.Get("CancelDialog_Content"),
            PrimaryButtonText = Strings.Get("CancelDialog_Primary"),
            CloseButtonText = Strings.Get("CancelDialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.CancelCommand.ExecuteAsync(null);
        }
    }
}
