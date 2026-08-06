using MarkSmith.Services;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters;

/// <summary>Background brush for a diff cell: soft red for removed lines, soft green for added
/// lines, transparent for unchanged — GitHub/VS Code style.</summary>
public sealed class DiffKindBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Removed = new(Color.FromArgb(90, 229, 72, 77));
    private static readonly SolidColorBrush Added = new(Color.FromArgb(90, 71, 184, 77));
    private static readonly SolidColorBrush Neutral = new(Color.FromArgb(0, 0, 0, 0));

    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value switch
        {
            LineDiff.Kind.Removed => Removed,
            LineDiff.Kind.Added => Added,
            _ => Neutral,
        };

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotSupportedException();
}
