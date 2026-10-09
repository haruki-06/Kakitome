using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.App.Services;
using Kakitome.Presentation.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SettingsViewModel>();
        Maintenance = App.Current.Services.GetRequiredService<MaintenanceViewModel>();
        Glossary = App.Current.Services.GetRequiredService<GlossaryViewModel>();
        InitializeComponent();
        ViewModel.ThemeChanged += OnThemeChanged;
        ViewModel.StorageChanged += OnStorageChanged;
        Maintenance.ConfigReset += OnConfigReset;
    }

    public SettingsViewModel ViewModel { get; }

    public MaintenanceViewModel Maintenance { get; }

    public GlossaryViewModel Glossary { get; }

#pragma warning disable CA1822 // x:Bind function bindings must be instance members.
    public InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
#pragma warning restore CA1822

    public string VersionText => Strings.Format("Settings_Version", ViewModel.AppVersion);

    public string BackupFolderText => Strings.Format("Settings_BackupFolder", Maintenance.BackupsPath);

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter as string == "models")
        {
            Tabs.SelectedItem = ModelsTab;
        }
        else if (e.Parameter as string == "import")
        {
            // Arrived from URL import without yt-dlp: explain why, next to the setting that fixes it.
            Tabs.SelectedItem = ModelsTab;
            YtDlpNotice.Message = Services.Strings.Get("ImportUrl_NeedsTool");
            YtDlpNotice.IsOpen = true;
            ImportHeader.StartBringIntoView();
        }

        await Maintenance.LoadAsync();
        await Glossary.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ThemeChanged -= OnThemeChanged;
        ViewModel.StorageChanged -= OnStorageChanged;
        Maintenance.ConfigReset -= OnConfigReset;
        base.OnNavigatedFrom(e);
    }

    private void OnThemeChanged(object? sender, string theme) => App.Current.ApplyTheme(theme);

    /// <summary>Shows the panel of the selected tab and scrolls back to its top.</summary>
    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        RecordingPanel.Visibility = Show(tag == "recording");
        TranscriptionPanel.Visibility = Show(tag == "transcription");
        ModelsPanel.Visibility = Show(tag == "models");
        DataPanel.Visibility = Show(tag == "data");
        GeneralPanel.Visibility = Show(tag == "general");
        Scroller.ChangeView(null, 0, null, disableAnimation: true);

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Third-party notices (docs/10), read from the file installed with the app.</summary>
    private async void OnShowNotices(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path) : Strings.Get("Notices_Missing");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("Notices_Title"),
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
            },
            CloseButtonText = Strings.Get("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private void OnStorageChanged(object? sender, EventArgs e) => _ = Maintenance.LoadAsync();

    private void OnConfigReset(object? sender, EventArgs e)
    {
        ViewModel.Reload();
        App.Current.ApplyTheme(ViewModel.Theme?.Value ?? "system");
    }

    private void OnInstall(object sender, RoutedEventArgs e) => ViewModel.InstallModelCommand.Execute((sender as FrameworkElement)?.Tag);

    private void OnVerify(object sender, RoutedEventArgs e) => ViewModel.VerifyModelCommand.Execute((sender as FrameworkElement)?.Tag);

    private void OnRemove(object sender, RoutedEventArgs e) => ViewModel.RemoveModelCommand.Execute((sender as FrameworkElement)?.Tag);
}
