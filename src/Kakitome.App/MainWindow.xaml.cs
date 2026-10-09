using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.App.Services;
using Kakitome.Application.Settings;
using Kakitome.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Kakitome.App;

public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private bool _syncingSelection;

    public MainWindow(NavigationService navigation, ISettingsStore settings)
    {
        _navigation = navigation;
        InitializeComponent();
        Title = Strings.Get("AppDisplayName");
        // Absolute path: a relative one resolves against the working directory, which is not the install folder when
        // Kakitome is started by sign-in, a notification or another program (the title bar then had no icon).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Kakitome.ico"));
        ExtendsContentIntoTitleBar = false;

        _navigation.Frame = ContentFrame;
        Nav.Loaded += (_, _) =>
        {
            // The built-in settings item has no stable automation id; give it one like the other nav items.
            if (Nav.SettingsItem is DependencyObject settingsItem)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(settingsItem, "NavSettings");
            }
        };
        ApplyTheme(settings.Current.General.Theme);
        _navigation.Navigate(PageKey.Home);
    }

    /// <summary>Applies system / light / dark immediately (Settings).</summary>
    public void ApplyTheme(string theme) => Root.RequestedTheme = theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            _navigation.Navigate(PageKey.Settings);
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<PageKey>(tag, out var page))
        {
            _navigation.Navigate(page);
        }
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    /// <summary>Keeps the selected menu item in sync with the visible page (also after Back).</summary>
    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        _syncingSelection = true;
        try
        {
            var key = NavigationService.KeyOf(e.SourcePageType);
            Nav.SelectedItem = key switch
            {
                PageKey.Settings => Nav.SettingsItem,
                PageKey.RecordingDetail or null => Nav.SelectedItem,
                _ => Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key.ToString()),
            };
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Strings.Get("Import_Button.Content").TrimEnd('…');
        }
        else if (e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = Strings.Get("ImportUrl_Button.Content").TrimEnd('…');
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var import = App.Current.Services.GetRequiredService<ImportCoordinator>();
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await import.ImportAsync(items.OfType<StorageFile>().Select(f => f.Path).ToList());
        }
        else if (e.DataView.Contains(StandardDataFormats.WebLink))
        {
            // A dropped link only prefills the dialog; the user confirms before anything is downloaded.
            var link = await e.DataView.GetWebLinkAsync();
            await import.PromptAndImportUrlAsync(Content.XamlRoot, link.AbsoluteUri);
        }
    }
}
