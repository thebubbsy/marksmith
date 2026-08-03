using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Core.Composer;
using MarkSmith.Core.Glox;

namespace MarkSmith.ViewModels.ShapeStudio;

public partial class ShapeCanvasItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _prst = "ellipse";

    [ObservableProperty]
    private string _name = "Shape";

    [ObservableProperty]
    private double _x = 100;

    [ObservableProperty]
    private double _y = 100;

    [ObservableProperty]
    private double _width = 90;

    [ObservableProperty]
    private double _height = 60;

    [ObservableProperty]
    private string _fill = "0078D4";

    [ObservableProperty]
    private int _rotation;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditingText;

    /// <summary>Curved-stroke polyline (0..100 local space) for sketch shapes.</summary>
    public System.Collections.Generic.List<(double X, double Y)>? PathPoints { get; set; }

    /// <summary>Stroke thickness in points (sketch shapes).</summary>
    public double StrokeWidthPt { get; set; } = 1.5;
}

/// <summary>
/// MLShape Design Studio — free-form canvas for composing native DrawingML shapes
/// (drag to place/move, select, inspect, then export as editable Word shapes).
/// Brand-new UI; reuses the composer + docx writer engine.
/// </summary>
public partial class ShapeDesignStudioViewModel : ObservableObject
{
    public static readonly string[] Palette = {
        "ellipse", "rect", "roundrect", "chevron", "diamond", "hexagon",
        "triangle", "parallelogram", "line", "arc", "cloud", "heart",
        "moon", "circulararrow", "smileyface"
    };

    /// <summary>Instance accessor so XAML {Binding} can reach the palette.</summary>
    public IReadOnlyList<string> PaletteItems => Palette;

    [ObservableProperty]
    private string _activeTool = "ellipse";

    [ObservableProperty]
    private ObservableCollection<ShapeCanvasItemViewModel> _shapes = new();

    [ObservableProperty]
    private ShapeCanvasItemViewModel? _selectedShape;

    [ObservableProperty]
    private string _statusMessage = "Ready — pick a shape, click the canvas to place.";

    public event EventHandler? CanvasChanged;

    partial void OnSelectedShapeChanged(ShapeCanvasItemViewModel? value)
    {
        foreach (var s in Shapes)
        {
            s.IsSelected = s == value;
        }
    }

    public ShapeCanvasItemViewModel AddShapeAt(string prst, double x, double y)
    {
        var item = new ShapeCanvasItemViewModel
        {
            Prst = prst,
            Name = prst,
            X = x,
            Y = y,
            IsSelected = true
        };
        Shapes.Add(item);
        SelectedShape = item;
        StatusMessage = $"Placed {prst} at ({x:F0}, {y:F0})";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    [RelayCommand]
    public void RemoveSelected()
    {
        if (SelectedShape == null) return;
        Shapes.Remove(SelectedShape);
        SelectedShape = null;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ClearAll()
    {
        Shapes.Clear();
        SelectedShape = null;
        StatusMessage = "Canvas cleared.";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadMarkdown(string markdownBlock)
    {
        try
        {
            var parsed = MarkSmith.Core.Composer.ShapeMarkdownCodec.Parse(markdownBlock);
            if (parsed.Count == 0)
            {
                StatusMessage = "No shapes found in the markdown (need a :::shapes block).";
                return;
            }

            Shapes.Clear();
            foreach (var s in parsed)
            {
                Shapes.Add(new ShapeCanvasItemViewModel
                {
                    Prst = s.Prst,
                    Name = s.Prst,
                    X = s.X,
                    Y = s.Y,
                    Width = s.W,
                    Height = s.H,
                    Fill = s.Fill,
                    Rotation = s.Rot,
                    PathPoints = s.PathPoints,
                    StrokeWidthPt = s.StrokeWidthPt
                });
            }
            SelectedShape = null;
            StatusMessage = $"Loaded {parsed.Count} shapes from markdown.";
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load error: {ex.Message}";
        }
    }

    public void ComposeSketchImage(string imagePath, int grid)
    {
        try
        {
            var composed = ImageShapeComposer.ComposeSketch(imagePath, new ShapeComposerOptions { Grid = grid });
            foreach (var s in composed)
            {
                Shapes.Add(new ShapeCanvasItemViewModel
                {
                    Prst = s.Prst,
                    Name = s.Prst,
                    X = s.X,
                    Y = s.Y,
                    Width = s.W,
                    Height = s.H,
                    Fill = s.Fill,
                    Rotation = s.Rot,
                    PathPoints = s.PathPoints,
                    StrokeWidthPt = s.StrokeWidthPt
                });
            }
            StatusMessage = $"Sketch: {composed.Count} curved strokes onto the canvas from {Path.GetFileName(imagePath)}";
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sketch error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ExportDocx()
    {
        try
        {
            if (Shapes.Count == 0) { StatusMessage = "Nothing to export."; return; }

            double maxX = Shapes.Max(s => s.X + s.Width);
            double maxY = Shapes.Max(s => s.Y + s.Height);
            double w = Math.Max(2, maxX + 0.5);
            double h = Math.Max(2, maxY + 0.5);

            var composed = Shapes.Select(s => new ComposedShape
            {
                Prst = s.Prst,
                X = s.X,
                Y = s.Y,
                W = s.Width,
                H = s.Height,
                Fill = s.Fill,
                Rot = s.Rotation,
                PathPoints = s.PathPoints,
                StrokeWidthPt = s.StrokeWidthPt
            }).ToList();

            string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"MLShape_Studio_{DateTime.Now:HHmmss}.docx");
            ShapeComposerDocxWriter.WriteDocx(outPath, composed, w, h,
                SmartArtLayoutCatalog.Shared.ThemeXml);
            StatusMessage = $"✓ Exported {Shapes.Count} native DrawingML shapes → {outPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    public void ComposeImage(string imagePath, int grid, IReadOnlyList<string> shapes)
    {
        try
        {
            var composed = ImageShapeComposer.Compose(imagePath, new ShapeComposerOptions
            {
                Grid = grid,
                Shapes = shapes.Any() ? shapes.ToList() : new List<string> { "ellipse" }
            });
            foreach (var s in composed)
            {
                Shapes.Add(new ShapeCanvasItemViewModel
                {
                    Prst = s.Prst,
                    Name = s.Prst,
                    X = s.X,
                    Y = s.Y,
                    Width = s.W,
                    Height = s.H,
                    Fill = s.Fill,
                    Rotation = s.Rot,
                    PathPoints = s.PathPoints,
                    StrokeWidthPt = s.StrokeWidthPt
                });
            }
            StatusMessage = $"Composed {composed.Count} shapes onto the canvas from {Path.GetFileName(imagePath)}";
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compose error: {ex.Message}";
        }
    }
}
