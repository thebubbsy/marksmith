using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MdToPdf.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    // Pass ConverterParameter=Invert to flip the mapping (true -> Collapsed, false -> Visible),
    // used for showing one of two mutually-exclusive icons without a second converter.
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool x && x;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility v && v == Visibility.Visible;
}
