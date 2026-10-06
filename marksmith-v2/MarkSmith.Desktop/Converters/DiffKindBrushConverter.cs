using MarkSmith.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Converters;

/// <summary>Background brush for a diff cell: soft red for removed lines, soft green for added
/// lines, transparent for unchanged — GitHub/VS Code style. Resolves themed brushes at runtime so
/// the diff reads correctly in both light and dark themes.</summary>
public sealed class DiffKindBrushConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value switch
        {
            // The *Background* brushes are the tints Fluent designs for text to sit on. The plain
            // SystemFillColorCritical/Success brushes are solid foreground colours: in the dark
            // theme they painted removed lines bright pink under white text.
            LineDiff.Kind.Removed => Themed("SystemFillColorCriticalBackgroundBrush"),
            LineDiff.Kind.Added => Themed("SystemFillColorSuccessBackgroundBrush"),
            _ => new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

    private static Brush Themed(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var b) && b is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotSupportedException();
}
