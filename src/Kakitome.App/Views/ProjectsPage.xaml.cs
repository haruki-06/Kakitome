using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.Presentation.ViewModels;

namespace Kakitome.App.Views;

public sealed partial class ProjectsPage : Page
{
    public ProjectsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ProjectsViewModel>();
        InitializeComponent();
    }

    public ProjectsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void OnProjectClicked(object sender, ItemClickEventArgs e) => ViewModel.OpenCommand.Execute(e.ClickedItem);

#pragma warning disable CA1822 // x:Bind function bindings must be instance members.
    public InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
#pragma warning restore CA1822

    /// <summary>Enter creates the project (the IME consumes the Enter that commits Japanese input).</summary>
    private void OnNameKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.CreateCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.CreateCommand.Execute(null);
        }
    }
}
