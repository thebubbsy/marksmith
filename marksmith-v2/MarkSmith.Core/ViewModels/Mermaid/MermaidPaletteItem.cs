using CommunityToolkit.Mvvm.ComponentModel;

namespace MarkSmith.ViewModels.Mermaid;

public partial class MermaidPaletteItem : ObservableObject
{
    [ObservableProperty]
    private string _category = "Flowchart";

    [ObservableProperty]
    private string _displayName = "Rectangle";

    [ObservableProperty]
    private string _shapeType = "Rectangle";

    [ObservableProperty]
    private string _iconGlyph = "\uE8A5";

    [ObservableProperty]
    private string _defaultText = "Node";

    [ObservableProperty]
    private string _tooltip = "Add shape to canvas";
}
