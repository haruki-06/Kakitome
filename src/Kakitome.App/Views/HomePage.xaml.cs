using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.App.Services;
using Kakitome.Presentation.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class HomePage : Page
{
    private readonly ImportCoordinator _import;

    public HomePage()
    {
        ViewModel = App.Current.Services.GetRequiredService<HomeViewModel>();
        _import = App.Current.Services.GetRequiredService<ImportCoordinator>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

#pragma warning disable CA1822 // x:Bind function bindings must be instance members.
    public Microsoft.UI.Xaml.Visibility VisibleWhenFalse(bool value) => value ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
#pragma warning restore CA1822

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _import.Message += OnImportMessage;
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _import.Message -= OnImportMessage;
        base.OnNavigatedFrom(e);
    }

    private void OnImportMessage(object? sender, string message) => ViewModel.ImportMessage = message;

    private async void OnImportClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await _import.PickAndImportAsync();

    private async void OnImportUrlClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await _import.PromptAndImportUrlAsync(XamlRoot);

    private void OnRecentClicked(object sender, ItemClickEventArgs e) => ViewModel.OpenCommand.Execute(e.ClickedItem);
}
