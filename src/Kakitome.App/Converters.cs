using Microsoft.UI.Xaml.Data;

namespace Kakitome.App;

/// <summary>True when the value is a non-empty string / non-null object.</summary>
public sealed partial class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s ? !string.IsNullOrEmpty(s) : value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
