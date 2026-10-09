using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kakitome.App.Views;

/// <summary>x:Bind helper functions used in data templates (static so templates can call them).</summary>
public static class Bind
{
    public static Visibility VisibleIfBoth(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfAndNot(bool a, bool b) => a && !b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Colour of a processing step's state icon: green when done, red on a problem, otherwise secondary.</summary>
    public static Microsoft.UI.Xaml.Media.Brush StateBrush(bool problem, Kakitome.Application.Jobs.JobState state) =>
        ThemeBrush(problem ? "SystemFillColorCriticalBrush"
            : state == Kakitome.Application.Jobs.JobState.Succeeded ? "SystemFillColorSuccessBrush"
            : "TextFillColorSecondaryBrush");

    /// <summary>Red for problems (failed step, error message), otherwise the secondary text colour.</summary>
    public static Microsoft.UI.Xaml.Media.Brush DetailBrush(bool problem) =>
        ThemeBrush(problem ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");

    private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

    /// <summary>
    /// Renders a search snippet whose matches are wrapped in [ ] (FTS5 snippet markers) with the matches in bold.
    /// </summary>
    public static readonly DependencyProperty MarkedTextProperty = DependencyProperty.RegisterAttached(
        "MarkedText", typeof(string), typeof(Bind), new PropertyMetadata(null, OnMarkedTextChanged));

    public static string? GetMarkedText(DependencyObject element) => (string?)element.GetValue(MarkedTextProperty);

    public static void SetMarkedText(DependencyObject element, string? value) => element.SetValue(MarkedTextProperty, value);

    private static void OnMarkedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        var text = e.NewValue as string ?? string.Empty;
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('[', position);
            var close = open < 0 ? -1 : text.IndexOf(']', open + 1);
            if (open < 0 || close < 0)
            {
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[position..] });
                break;
            }

            if (open > position)
            {
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[position..open] });
            }

            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text[(open + 1)..close],
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            });
            position = close + 1;
        }
    }
}
