using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MarkSmith.Converters;

/// <summary>
/// Inverse of <see cref="ShapeToVisibilityConverter"/>: returns <see cref="Visibility.Collapsed"/>
/// when the bound string value matches (case-insensitively) any entry in the comma-separated
/// <c>ConverterParameter</c> list, and Visible otherwise.
/// Applied to the generic rounded-rectangle fallback so that any shape that does NOT have a
/// dedicated visual still renders as a visible rectangle — no node can ever disappear.
/// </summary>
public sealed class ShapeFallbackVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string shape && parameter is string specialShapes)
        {
            var specials = specialShapes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (specials.Contains(shape, StringComparer.OrdinalIgnoreCase))
                return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
