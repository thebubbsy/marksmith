using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MdToPdf.Converters;

/// <summary>
/// Converts a heading level (1–6) into a left-indent <see cref="Thickness"/> for the document
/// outline flyout (Task 17), so deeper headings nest visually beneath their parents.
/// </summary>
public sealed class LevelToIndentConverter : IValueConverter
{
    private const double IndentPerLevel = 14.0;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is int l ? l : 1;
        var left = Math.Max(0, level - 1) * IndentPerLevel;
        return new Thickness(left, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
