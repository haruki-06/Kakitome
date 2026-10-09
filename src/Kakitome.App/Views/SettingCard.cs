using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Kakitome.App.Views;

/// <summary>
/// One setting in the style of Windows Settings: a card with a title and an optional description on the left and the
/// control on the right. The control is named after the title for screen readers unless it has a name of its own.
/// </summary>
public sealed partial class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata(null, (d, _) => ((SettingCard)d).Refresh()));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(null, (d, _) => ((SettingCard)d).Refresh()));

    private TextBlock? _description;

    public SettingCard()
    {
        DefaultStyleKey = typeof(SettingCard);
        Loaded += (_, _) => Refresh();
    }

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _description = GetTemplateChild("PART_Description") as TextBlock;
        Refresh();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        Refresh();
    }

    private void Refresh()
    {
        if (_description is not null)
        {
            _description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (Content is UIElement control && !string.IsNullOrEmpty(Header) && string.IsNullOrEmpty(AutomationProperties.GetName(control)))
        {
            AutomationProperties.SetName(control, Header);
        }
    }
}
