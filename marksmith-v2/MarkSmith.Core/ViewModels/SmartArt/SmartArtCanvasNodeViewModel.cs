using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MarkSmith.ViewModels.SmartArt;

public partial class SmartArtCanvasNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _text = "New Shape";

    [ObservableProperty]
    private double _x = 100;

    [ObservableProperty]
    private double _y = 100;

    [ObservableProperty]
    private double _width = 120;

    [ObservableProperty]
    private double _height = 60;

    [ObservableProperty]
    private string _shapeType = "roundRect"; // roundRect, circle, diamond, hexagon, process, hierarchy, cycle, pyramid, venn, picture, mosaic

    [ObservableProperty]
    private string _category = "General";

    [ObservableProperty]
    private string? _imagePath;

    [ObservableProperty]
    private string _color = "#0078d4";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditingText;

    public ObservableCollection<SmartArtCanvasNodeViewModel> Children { get; } = new();

    public SmartArtCanvasNodeViewModel? ParentNode { get; set; }
}

public class SmartArtPaletteItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string ShapeType { get; set; } = "roundRect";
    public string Category { get; set; } = "Basic";
    public string DefaultText { get; set; } = "Shape";
    public string Color { get; set; } = "#0078d4";
    public string Tooltip { get; set; } = string.Empty;
}

public class GloxLayoutItem
{
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
}
