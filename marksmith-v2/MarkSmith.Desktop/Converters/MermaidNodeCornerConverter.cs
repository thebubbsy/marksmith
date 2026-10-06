using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MarkSmith.Converters;

/// <summary>
/// A node's <c>BoxCornerRadius</c> (a plain double from Core) as a uniform <see cref="CornerRadius"/>.
/// Every box-shaped Diagram Studio node used one fixed 10 px radius, so Rectangle, Rounded
/// Rectangle, Stadium and Subroutine all looked identical on the canvas.
/// </summary>
public sealed class MermaidNodeCornerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new CornerRadius(value is double d ? d : 2);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
