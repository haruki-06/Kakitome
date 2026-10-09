using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.App.Services;
using Kakitome.Presentation.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();
    }

    public LibraryViewModel ViewModel { get; }

#pragma warning disable CA1822 // x:Bind function bindings must be instance members.
    public Visibility HasProjectFilter(string? project) => project is null ? Visibility.Collapsed : Visibility.Visible;
#pragma warning restore CA1822

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.ProjectFilter = e.Parameter as string;
        await ViewModel.LoadAsync();
    }

    private void OnShowAll(object sender, RoutedEventArgs e) => ViewModel.ProjectFilter = null;

    private void OnItemClicked(object sender, ItemClickEventArgs e) => ViewModel.OpenCommand.Execute(e.ClickedItem);

    private void OnOpenClicked(object sender, RoutedEventArgs e) => ViewModel.OpenCommand.Execute(ItemOf(sender));

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) => ViewModel.OpenFolderCommand.Execute(ItemOf(sender));

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => ViewModel.MoveToRecycleBinCommand.Execute(ItemOf(sender));

    private async void OnImportClicked(object sender, RoutedEventArgs e) =>
        await App.Current.Services.GetRequiredService<ImportCoordinator>().PickAndImportAsync();

    private async void OnImportUrlClicked(object sender, RoutedEventArgs e) =>
        await App.Current.Services.GetRequiredService<ImportCoordinator>().PromptAndImportUrlAsync(XamlRoot);

    private static RecordingItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.Tag as RecordingItemViewModel ?? (sender as FrameworkElement)?.DataContext as RecordingItemViewModel;
}
