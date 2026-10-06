using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Converters;

/// <summary>
/// Outline of a Diagram Studio palette shape, drawn in a 20×14 box, for the palette rows. The rows
/// used Fluent glyphs that only approximated the shapes (a document icon for Rectangle, the same
/// square for Rounded Rectangle and Stadium, a grid for Hexagon); a stroked path shows exactly what
/// lands on the canvas. A fresh Geometry per call: a Geometry can't be shared between elements.
/// </summary>
public sealed class MermaidShapeThumbnailConverter : IValueConverter
{
    private const string Rect = "M1,1 H19 V13 H1 Z";

    public static string PathFor(string? shapeType) => shapeType switch
    {
        "RoundedRectangle" or "NormalState" or "BranchNode" =>
            "M5,1 H15 A4,4 0 0 1 19,5 V9 A4,4 0 0 1 15,13 H5 A4,4 0 0 1 1,9 V5 A4,4 0 0 1 5,1 Z",
        "Stadium" => "M7,1 H13 A6,6 0 0 1 13,13 H7 A6,6 0 0 1 7,1 Z",
        "Subroutine" => Rect + " M4,1 V13 M16,1 V13",
        "CylindricalDatabase" => "M3,3 A7,2 0 0 1 17,3 V11 A7,2 0 0 1 3,11 Z M3,3 A7,2 0 0 0 17,3",
        "Circle" or "RootNode" => "M4,7 A6,6 0 1 1 16,7 A6,6 0 1 1 4,7 Z",
        "Rhombus" or "ChoiceState" => "M10,0.5 L19,7 L10,13.5 L1,7 Z",
        "Hexagon" => "M5,1 H15 L19,7 L15,13 H5 L1,7 Z",
        "Actor" => "M8,3.5 A2,2 0 1 1 12,3.5 A2,2 0 1 1 8,3.5 Z M10,5.5 V9.5 M6.5,7.5 H13.5 M10,9.5 L7.5,13.5 M10,9.5 L12.5,13.5",
        "ClassBox" => Rect + " M1,5 H19 M1,9 H19",
        "Interface" => Rect + " M1,5 H19",
        "Entity" => Rect + " M1,5 H19 M8,5 V13",
        "TaskBar" => "M1,4 H15 A3,3 0 0 1 15,10 H1 Z",
        "Milestone" => "M10,2 L15,7 L10,12 L5,7 Z",
        _ => Rect, // Rectangle, Participant, anything new
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        XamlBindingHelper.ConvertValue(typeof(Geometry), PathFor(value as string));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
