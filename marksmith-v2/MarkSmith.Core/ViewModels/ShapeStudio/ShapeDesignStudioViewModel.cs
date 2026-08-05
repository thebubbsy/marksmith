using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Core.Composer;

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

    /// <summary>Curved-stroke polyline (0..100 local space) for sketch/trace lines.</summary>
    public System.Collections.Generic.List<(double X, double Y)>? PathPoints { get; set; }

    /// <summary>Stroke thickness in points (sketch/trace lines).</summary>
    public double StrokeWidthPt { get; set; } = 1.5;
}

/// <summary>
/// MLShape Design Studio — free-form canvas for composing native DrawingML shapes AND tracing
/// a picked picture into dense, non-overlapping line art. Every traced line is an individually
/// selectable line item that renders as a native Word line in .docx/.dotx export and as an SVG
/// path in the HTML preview.
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

    /// <summary>Above this many shapes the canvas switches to the raster line-art preview
    /// instead of one XAML Path per shape (which would freeze at trace densities).</summary>
    public const int DenseCanvasThreshold = 400;

    [ObservableProperty]
    private string _activeTool = "ellipse";

    [ObservableProperty]
    private ObservableCollection<ShapeCanvasItemViewModel> _shapes = new();

    [ObservableProperty]
    private ShapeCanvasItemViewModel? _selectedShape;

    [ObservableProperty]
    private string _statusMessage = "Ready — pick an image and trace it, or click a shape to draw.";

    // ---- trace controls ----

    [ObservableProperty]
    private double _traceDensity = 480;

    /// <summary>0 = Engraved, 1 = Edges, 2 = Silhouette, 3 = Scanlines.</summary>
    [ObservableProperty]
    private int _traceModeIndex;

    [ObservableProperty]
    private bool _traceMonochrome;

    // ---- canvas presentation ----

    /// <summary>"empty" | "editable" (one Path per shape) | "dense" (raster line-art preview).</summary>
    [ObservableProperty]
    private string _canvasMode = "empty";

    [ObservableProperty]
    private byte[]? _previewPng;

    [ObservableProperty]
    private string _lineStats = "";

    public bool IsEmpty => CanvasMode == "empty";
    public bool IsEditable => CanvasMode == "editable";
    public bool IsDense => CanvasMode == "dense";

    public event EventHandler? CanvasChanged;

    partial void OnCanvasModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsDense));
    }

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
        CanvasMode = "editable";
        PreviewPng = null;
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
        RefreshCanvasMode();
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ClearAll()
    {
        Shapes.Clear();
        SelectedShape = null;
        CanvasMode = "empty";
        PreviewPng = null;
        LineStats = "";
        StatusMessage = "Canvas cleared.";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Trace a picture into dense line items (the MLShape workflow).</summary>
    public void TraceImage(string imagePath)
    {
        try
        {
            var mode = TraceModeIndex switch
            {
                1 => LineTraceMode.Edges,
                2 => LineTraceMode.Silhouette,
                3 => LineTraceMode.Scanlines,
                _ => LineTraceMode.Engraved
            };
            var traced = ImageLineTracer.TraceLines(imagePath, new LineTraceOptions
            {
                Rows = Math.Clamp((int)Math.Round(TraceDensity), ImageLineTracer.MinRows, ImageLineTracer.MaxRows),
                Mode = mode,
                UseColor = !TraceMonochrome
            });
            Shapes.Clear();
            foreach (var s in traced) Shapes.Add(ToItem(s));
            SelectedShape = null;
            LineStats = $"{traced.Count:N0} lines";
            StatusMessage = $"✓ Traced {traced.Count:N0} lines from {Path.GetFileName(imagePath)}";
            RefreshCanvasMode();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Trace error: {ex.Message}";
        }
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
            foreach (var s in parsed) Shapes.Add(ToItem(s));
            SelectedShape = null;
            LineStats = $"{parsed.Count:N0} shapes";
            StatusMessage = $"Loaded {parsed.Count} shapes from markdown.";
            RefreshCanvasMode();
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
            foreach (var s in composed) Shapes.Add(ToItem(s));
            StatusMessage = $"Sketch: {composed.Count} curved strokes onto the canvas from {Path.GetFileName(imagePath)}";
            RefreshCanvasMode();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sketch error: {ex.Message}";
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
            foreach (var s in composed) Shapes.Add(ToItem(s));
            StatusMessage = $"Composed {composed.Count} shapes onto the canvas from {Path.GetFileName(imagePath)}";
            RefreshCanvasMode();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compose error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ExportDocx() => ExportToWord(template: false);

    [RelayCommand]
    public void ExportDotx() => ExportToWord(template: true);

    private void ExportToWord(bool template)
    {
        try
        {
            if (Shapes.Count == 0) { StatusMessage = "Nothing to export."; return; }

            double maxX = Shapes.Max(s => s.X + s.Width);
            double maxY = Shapes.Max(s => s.Y + s.Height);
            double w = Math.Max(2, maxX + 0.5);
            double h = Math.Max(2, maxY + 0.5);

            var composed = Shapes.Select(ToComposed).ToList();
            string ext = template ? ".dotx" : ".docx";
            string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"MLShape_Studio_{DateTime.Now:HHmmss}{ext}");
            if (template)
                ShapeComposerDocxWriter.WriteDotx(outPath, composed, w, h, SmartArtLayoutCatalog.Shared.ThemeXml);
            else
                ShapeComposerDocxWriter.WriteDocx(outPath, composed, w, h, SmartArtLayoutCatalog.Shared.ThemeXml);
            StatusMessage = $"✓ Exported {Shapes.Count:N0} native DrawingML shapes → {outPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    // ---- shared helpers ----

    /// <summary>Pick the canvas presentation that keeps the studio usable at any density:
    /// dense traces (or anything above the threshold) render as one raster line-art image
    /// (it literally looks like the picture), small hand-built sets stay fully editable.</summary>
    private void RefreshCanvasMode()
    {
        if (Shapes.Count == 0)
        {
            CanvasMode = "empty";
            PreviewPng = null;
            return;
        }
        bool hasLines = Shapes.Any(s => s.PathPoints is { Count: >= 2 });
        if (hasLines || Shapes.Count > DenseCanvasThreshold)
        {
            var (w, h) = MarkSmith.Core.Composer.ShapeMarkdownCodec.CanvasSize(Shapes.Select(ToComposed).ToList());
            PreviewPng = ImageLineTracer.RenderPreviewPng(
                Shapes.Select(ToComposed).ToList(), w, h, previewCap: 24000);
            CanvasMode = "dense";
        }
        else
        {
            PreviewPng = null;
            CanvasMode = "editable";
        }
    }

    private static ShapeCanvasItemViewModel ToItem(ComposedShape s) => new()
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
    };

    private static ComposedShape ToComposed(ShapeCanvasItemViewModel s) => new()
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
    };
}
