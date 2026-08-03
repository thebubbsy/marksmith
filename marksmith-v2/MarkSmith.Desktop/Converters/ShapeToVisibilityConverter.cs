using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MarkSmith.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound string value matches (case-insensitively)
/// any entry in the comma-separated <c>ConverterParameter</c> list; otherwise Collapsed.
/// Used to switch a specific shape visual on for the node shapes it represents.
/// </summary>
public sealed class ShapeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string shape && parameter is string allowedShapes)
        {
            var allowed = allowedShapes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowed.Contains(shape, StringComparer.OrdinalIgnoreCase))
                return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
