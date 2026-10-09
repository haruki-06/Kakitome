using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.Presentation.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class QueuePage : Page
{
    public QueuePage()
    {
        ViewModel = App.Current.Services.GetRequiredService<QueueViewModel>();
        InitializeComponent();
    }

    public QueueViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void OnPause(object sender, RoutedEventArgs e) => ViewModel.PauseCommand.Execute(ItemOf(sender));

    private void OnResume(object sender, RoutedEventArgs e) => ViewModel.ResumeCommand.Execute(ItemOf(sender));

    private void OnRetry(object sender, RoutedEventArgs e) => ViewModel.RetryCommand.Execute(ItemOf(sender));

    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelCommand.Execute(ItemOf(sender));

    private void OnOpen(object sender, RoutedEventArgs e) => ViewModel.OpenRecordingCommand.Execute((sender as FrameworkElement)?.Tag as JobGroupViewModel);

    private static JobItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as JobItemViewModel;
}
