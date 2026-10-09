using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.Presentation.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class SearchPage : Page
{
    public SearchPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
    }

    public SearchViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void OnHitClicked(object sender, RoutedEventArgs e) => ViewModel.OpenCommand.Execute((sender as FrameworkElement)?.Tag);
}
