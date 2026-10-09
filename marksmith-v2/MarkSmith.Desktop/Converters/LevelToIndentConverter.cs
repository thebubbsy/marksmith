using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MarkSmith.Converters;

/// <summary>
/// Converts a heading level (1–6) into a left-indent <see cref="Thickness"/> for the document
/// outline flyout (Task 17), so deeper headings nest visually beneath their parents; with
/// ConverterParameter="depth", a 0-based tree depth.
/// </summary>
public sealed class LevelToIndentConverter : IValueConverter
{
    private const double IndentPerLevel = 14.0;

    /// <summary>Tree rows (SmartArt Studio outline) indent further, so each level reads at a glance.</summary>
    private const double IndentPerDepth = 20.0;

    /// <summary>
    /// ConverterParameter "depth" takes a 0-based tree depth instead of a 1-based heading level.
    /// The SmartArt outline bound its 0-based Depth here, so the root and its children both sat at
    /// the left edge and "CEO" read as a sibling of "Executive Board" while the chart drew it as a child.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var n = value is int l ? l : 0;
        var left = parameter as string == "depth"
            ? Math.Max(0, n) * IndentPerDepth
            : Math.Max(0, (value is int ? n : 1) - 1) * IndentPerLevel;
        return new Thickness(left, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
