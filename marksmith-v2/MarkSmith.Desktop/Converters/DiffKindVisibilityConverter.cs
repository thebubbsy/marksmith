using MarkSmith.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MarkSmith.Converters;

/// <summary>Visible when a diff cell's kind is the one named by ConverterParameter ("Removed" or
/// "Added"). The History window stacks a soft red and a soft green ThemeResource layer under each
/// line and shows the matching one, GitHub/VS Code style. The tints used to come from a converter
/// that returned the brush itself, which kept the old theme's colours after a light/dark switch
/// (ItemsRepeater recycles rows with the same data, so the conversion never ran again). Use the
/// *Background* brushes: the plain SystemFillColorCritical/Success brushes are solid foreground
/// colours and painted removed lines bright pink under white text in the dark theme.</summary>
public sealed class DiffKindVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value is LineDiff.Kind kind && parameter is string wanted
           && string.Equals(kind.ToString(), wanted, System.StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotSupportedException();
}
