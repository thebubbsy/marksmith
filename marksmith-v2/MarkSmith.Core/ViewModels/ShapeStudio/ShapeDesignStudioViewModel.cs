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

public class DiagramPreset
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "📐";
    public string Description { get; set; } = "";
    public Action<ShapeDesignStudioViewModel> Generate { get; set; } = _ => { };
}

public partial class ShapeCanvasItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelInsets))]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    private string _prst = "ellipse";

    [ObservableProperty]
    private string _name = "Shape";

    [ObservableProperty]
    private double _x = 100;

    [ObservableProperty]
    private double _y = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelInsets))]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    private double _width = 90;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelInsets))]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    private double _height = 60;

    [ObservableProperty]
    private string _fill = ShapeDesignStudioViewModel.ThemeAccentHex();

    /// <summary>Optional explicit label colour (#RRGGBB); null = auto-guarded against the fill.</summary>
    public string? TextColor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    private string _text = "";

    [ObservableProperty]
    private int _rotation;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditingText;

    /// <summary>Curved-stroke polyline (0..100 local space) for sketch/trace lines.</summary>
    public System.Collections.Generic.List<(double X, double Y)>? PathPoints { get; set; }

    /// <summary>Stroke thickness in points (connectors, sketch/trace lines). Editable in the
    /// inspector as Weight; it used to be fixed at whatever the preset or tracer chose.</summary>
    public double StrokeWidthPt
    {
        get => _strokeWidthPt;
        set
        {
            // An emptied NumberBox writes NaN; keep the last weight rather than lose the line.
            if (!double.IsFinite(value)) { OnPropertyChanged(); return; }
            SetProperty(ref _strokeWidthPt, Math.Clamp(value, MinStrokeWidthPt, MaxStrokeWidthPt));
        }
    }
    private double _strokeWidthPt = 1.5;

    public const double MinStrokeWidthPt = 0.25;
    public const double MaxStrokeWidthPt = 24;

    /// <summary>A connector or drawn line: it has a colour and a weight rather than a fill.</summary>
    public bool IsLine => PathPoints is { Count: >= 2 } || string.Equals(Prst, "line", StringComparison.OrdinalIgnoreCase);

    /// <summary>The inspector's colour row: "Fill" for shapes, "Colour" for lines.</summary>
    public string ColourLabel => IsLine ? "Colour" : "Fill";

    partial void OnFillChanged(string value)
    {
        // HARD RULE (ContrastGuard.EnsureVisibleFill): a shape fill must NEVER blend into the
        // studio canvas (#1B1B1F) — if the user picks (or a theme supplies) a fill that is the
        // same colour as the background, the rule pushes it to a visible shade so the shape is
        // always distinguishable. Reentrancy-safe: sets the backing field, not the property.
        if (!string.IsNullOrWhiteSpace(value))
        {
            string guarded = Services.ContrastGuard.EnsureVisibleFill(value, ShapeDesignStudioViewModel.CanvasBackgroundHex);
#pragma warning disable MVVMTK0034
            if (guarded != value) _fill = guarded;
#pragma warning restore MVVMTK0034
        }
        OnPropertyChanged(nameof(TextForegroundHex));
    }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(TextForegroundHex));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ListSubtitle));
    }

    /// <summary>Tells the canvas the line's points changed (they're a plain list, not observable).</summary>
    public void NotifyPathChanged()
    {
        OnPropertyChanged(nameof(PathPoints));
        OnPropertyChanged(nameof(IsLine));
        OnPropertyChanged(nameof(ColourLabel));
    }

    partial void OnPrstChanged(string value)
    {
        OnPropertyChanged(nameof(IsLine));
        OnPropertyChanged(nameof(ColourLabel));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ListSubtitle));
    }

    /// <summary>Shapes-list headline: the label's first line ("Executive Board"), or the shape type
    /// when it has no label — five rows of "Rounded rectangle" told the user nothing. An unlabelled
    /// line says what it joins ("Executive Board → CEO / Operations"): an org chart listed eleven
    /// rows of plain "Line".</summary>
    public string ListTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text)) return _joins.Length > 0 ? _joins : DisplayName;
            var first = Text.Trim().Split('\n')[0].Trim();
            return first.Length == 0 ? DisplayName : first;
        }
    }

    /// <summary>Shapes-list second line: the shape type under a label or a "joins" title; for a
    /// line that joins nothing, which way it runs.</summary>
    public string ListSubtitle =>
        !string.IsNullOrWhiteSpace(Text) || _joins.Length > 0 ? DisplayName : _direction;

    private string _joins = "";
    private string _direction = "";

    /// <summary>Set by the studio, which knows the other shapes (<see cref="ShapeDesignStudioViewModel.DescribeLines"/>).</summary>
    internal void SetLineDescription(string joins, string direction)
    {
        if (_joins == joins && _direction == direction) return;
        _joins = joins;
        _direction = direction;
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ListSubtitle));
    }

    // The inspector's NumberBoxes write NaN when cleared; a NaN coordinate or size would make the
    // shape vanish (and poison the export), so an emptied box restores the previous value.
#pragma warning disable MVVMTK0034
    partial void OnXChanged(double oldValue, double newValue) { if (!double.IsFinite(newValue)) { _x = oldValue; OnPropertyChanged(nameof(X)); } }
    partial void OnYChanged(double oldValue, double newValue) { if (!double.IsFinite(newValue)) { _y = oldValue; OnPropertyChanged(nameof(Y)); } }
    partial void OnWidthChanged(double oldValue, double newValue) { if (!double.IsFinite(newValue)) { _width = oldValue; OnPropertyChanged(nameof(Width)); } }
    partial void OnHeightChanged(double oldValue, double newValue) { if (!double.IsFinite(newValue)) { _height = oldValue; OnPropertyChanged(nameof(Height)); } }
#pragma warning restore MVVMTK0034

    /// <summary>Human-readable shape type for the inspector and shapes list ("roundrect" →
    /// "Rounded rectangle"); the DrawingML preset token stays in <see cref="Prst"/>.</summary>
    public string DisplayName => DisplayNameFor(Prst);

    private static readonly Dictionary<string, string> PresetDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ellipse"] = "Ellipse",
        ["rect"] = "Rectangle",
        ["roundrect"] = "Rounded rectangle",
        ["trapezoid"] = "Trapezoid",
        ["cylinder"] = "Cylinder",
        ["can"] = "Cylinder",
        ["chevron"] = "Chevron",
        ["diamond"] = "Diamond",
        ["hexagon"] = "Hexagon",
        ["triangle"] = "Triangle",
        ["parallelogram"] = "Parallelogram",
        ["line"] = "Line",
        ["arc"] = "Arc",
        ["cloud"] = "Cloud",
        ["heart"] = "Heart",
        ["moon"] = "Moon",
        ["circulararrow"] = "Circular arrow",
        ["smileyface"] = "Smiley face",
        ["rightarrow"] = "Right arrow",
        ["leftarrow"] = "Left arrow",
        ["uparrow"] = "Up arrow",
        ["downarrow"] = "Down arrow",
        ["pentagon"] = "Pentagon",
        ["octagon"] = "Octagon",
        ["star5"] = "Star",
        ["sketch"] = "Sketch stroke",
    };

    /// <summary>Friendly name for a DrawingML preset token; unknown tokens are split on
    /// camel-case/digits and sentence-cased so nothing ever shows as a raw lowercase token.</summary>
    public static string DisplayNameFor(string? prst)
    {
        if (string.IsNullOrWhiteSpace(prst)) return "Shape";
        if (PresetDisplayNames.TryGetValue(prst.Trim(), out var name)) return name;
        var spaced = System.Text.RegularExpressions.Regex.Replace(prst.Trim(), "(?<=[a-z])(?=[A-Z0-9])", " ").ToLowerInvariant();
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    /// <summary>
    /// Where the label sits inside the shape, as left/top/right/bottom insets in canvas px — the
    /// same text box Word gives the preset geometry, so the canvas shows the label where the
    /// exported document will. Centring every label on the bounding box put a triangle's text in
    /// its narrow apex, spilling over the edges ("1 · Vision &amp; Strategy" on the pyramid preset).
    /// </summary>
    public double[] LabelInsets => PresetGeometry.TextInsets(Prst, Width, Height);

    /// <summary>Label size in canvas px: the size the Word export will use (PresetGeometry.FitLabel),
    /// so the canvas no longer shows a label at a third of its exported size.</summary>
    public double LabelFontSize => PresetGeometry.FitLabel(Text ?? "", Prst, Width, Height).Pt * 96 / 72;

    /// <summary>Label colour guaranteed to contrast with THIS shape's fill (the CONTRAST RULE for
    /// font on top of shapes): WCAG 4.5:1 vs the fill — never against the page background.</summary>
    public string TextForegroundHex =>
        Services.ContrastGuard.EnsureLegibleText(TextColor ?? "121212", "#" + Fill);
}

/// <summary>
/// MLShape Design Studio — free-form canvas for composing native DrawingML shapes AND tracing
/// a picked picture into dense, non-overlapping line art. Every traced line is an individually
/// selectable line item that renders as a native Word line in .docx/.dotx export and as an SVG
/// path in the HTML preview.
/// </summary>
public partial class ShapeDesignStudioViewModel : ObservableObject
{
    /// <summary>Studio canvas background — the contrast rules measure fills/labels against this.</summary>
    public const string CanvasBackgroundHex = "1B1B1F";

    private static readonly object ThemeAccentLock = new();
    private static string? _themeAccentCacheKey;
    private static string _themeAccentCache = "0078D4";

    /// <summary>
    /// Theme-governed default fill: the THEME is the governing palette, so new shapes take the
    /// selected theme's accent (Primary, falling back to Heading). An explicit user-picked fill
    /// still overrides per shape — the theme only supplies the DEFAULT.
    /// HARD RULE: the default is filtered by ContrastGuard so it can NEVER blend into the studio
    /// canvas (#1B1B1F) — a dark theme whose Primary is near-black (e.g. GitHub Light's #000000)
    /// falls back to a visible theme color (Secondary/Line) instead of spawning invisible shapes.
    /// Cached per theme name: a 16k-row trace constructs one item per line, and each used to pay
    /// for a full theme lookup + contrast loop — now only the first shape of a theme does.
    /// </summary>
    public static string ThemeAccentHex()
    {
        try
        {
            string themeKey = AppServices.Settings.Current.Theme ?? "";
            lock (ThemeAccentLock)
            {
                if (_themeAccentCacheKey == themeKey) return _themeAccentCache;
            }
            var theme = AppServices.Themes.GetOrDefault(themeKey);
            string[] candidates = { theme.Primary, theme.Secondary, theme.Line, theme.Heading, "FFFFFF", "121212" };
            string best = "0078D4";
            double bestRatio = 0;
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                string hex = c.TrimStart('#');
                if (hex.Length != 6) continue;
                double r = Services.ContrastGuard.GetContrastRatio(hex, CanvasBackgroundHex);
                if (r > bestRatio) { bestRatio = r; best = hex; }
            }
            lock (ThemeAccentLock)
            {
                _themeAccentCacheKey = themeKey;
                _themeAccentCache = best;
            }
            return best;
        }
        catch { }
        return "0078D4";
    }

    public static readonly string[] Palette = {
        "ellipse", "rect", "roundrect", "trapezoid", "cylinder", "chevron", "diamond", "hexagon",
        "triangle", "parallelogram", "line", "arc", "cloud", "heart",
        "moon", "circulararrow", "smileyface"
    };

    public static readonly Dictionary<string, string[]> ColorPalettes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Office Blue"] = new[] { "0078D4", "107C41", "D13438", "FF8C00", "5C2D91", "008272" },
        ["Ocean Gradient"] = new[] { "0078D4", "00B7C3", "0099BC", "005A9E", "2886DE", "038387" },
        ["Sunset Coral"] = new[] { "D13438", "FF8C00", "F7630C", "E81123", "EA005E", "FFB900" },
        ["Emerald Forest"] = new[] { "107C41", "008272", "2D7D9A", "6B8E23", "4A7C59", "13A10E" },
        ["Modern Violet"] = new[] { "5C2D91", "8764B8", "B146C2", "744DA9", "881798", "6B69D6" },
        ["Slate Monochrome"] = new[] { "24292F", "32383F", "424A53", "57606A", "6E7781", "8C959F" }
    };

    [ObservableProperty]
    private string _selectedPaletteName = "Office Blue";

    [ObservableProperty]
    private ObservableCollection<string> _paletteNames = new()
    {
        "Office Blue", "Ocean Gradient", "Sunset Coral", "Emerald Forest", "Modern Violet", "Slate Monochrome"
    };

    /// <summary>Instance accessor so XAML {Binding} can reach the palette.</summary>
    public IReadOnlyList<string> PaletteItems => Palette;

    /// <summary>Above this many shapes the canvas switches to the raster line-art preview
    /// instead of one XAML Path per shape (which would freeze at trace densities).</summary>
    public const int DenseCanvasThreshold = 400;

    /// <summary>The shape the next canvas click (or drag) places; null = the Select tool. Placing
    /// disarms it again — the old always-armed "roundrect" tool dropped a new shape on every click
    /// of empty canvas, so there was no way to click away a selection without adding a shape.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlacing))]
    [NotifyPropertyChangedFor(nameof(PlacementHint))]
    private string? _armedTool;

    public bool IsPlacing => ArmedTool is not null;

    public string PlacementHint => ArmedTool is null
        ? ""
        : $"Click or drag on the canvas to place a {ShapeCanvasItemViewModel.DisplayNameFor(ArmedTool).ToLowerInvariant()} · Esc to cancel";

    [ObservableProperty]
    private ObservableCollection<ShapeCanvasItemViewModel> _shapes = new();

    partial void OnShapesChanged(ObservableCollection<ShapeCanvasItemViewModel>? oldValue, ObservableCollection<ShapeCanvasItemViewModel> newValue)
    {
        if (oldValue is not null) oldValue.CollectionChanged -= OnShapesCollectionChanged;
        if (newValue is not null) newValue.CollectionChanged += OnShapesCollectionChanged;
        OnShapesMutated();
    }

    private void OnShapesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnShapesMutated();
    }

    // Lines are named after the shapes they touch, so moving or relabelling a shape renames them.
    // Kept as a set because Clear() raises Reset without the removed items.
    private readonly HashSet<ShapeCanvasItemViewModel> _watchedShapes = new();

    private void WatchShapes()
    {
        // A traced picture is thousands of lines that are never named; don't watch those.
        var current = Shapes.Count > MaxDescribedLines * 4
            ? new HashSet<ShapeCanvasItemViewModel>()
            : new HashSet<ShapeCanvasItemViewModel>(Shapes);
        foreach (var gone in _watchedShapes.Where(s => !current.Contains(s)).ToList())
        {
            gone.PropertyChanged -= OnShapeGeometryChanged;
            _watchedShapes.Remove(gone);
        }
        foreach (var item in current)
        {
            if (_watchedShapes.Add(item)) item.PropertyChanged += OnShapeGeometryChanged;
        }
    }

    public bool HasShapes => Shapes.Count > 0;

    private void OnShapeGeometryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShapeCanvasItemViewModel.X) or nameof(ShapeCanvasItemViewModel.Y)
            or nameof(ShapeCanvasItemViewModel.Width) or nameof(ShapeCanvasItemViewModel.Height)
            or nameof(ShapeCanvasItemViewModel.Text) or nameof(ShapeCanvasItemViewModel.Prst)
            or nameof(ShapeCanvasItemViewModel.PathPoints))
            DescribeLines();
    }

    /// <summary>Above this many lines (traced line art) the shapes list keeps plain "Line" rows.</summary>
    private const int MaxDescribedLines = 300;

    /// <summary>Distance (canvas px) within which a line's end counts as touching a shape.</summary>
    private const double TouchTolerance = 6;

    /// <summary>
    /// Names every unlabelled line in the shapes list by the labelled shapes its ends touch:
    /// "A → B", "From A", "To B", or, touching none, just "Line" over its direction.
    /// </summary>
    internal void DescribeLines()
    {
        var lines = Shapes.Where(IsUnlabelledLine).ToList();
        if (lines.Count == 0) return;
        if (lines.Count > MaxDescribedLines)
        {
            foreach (var line in lines) line.SetLineDescription("", "");
            return;
        }
        var targets = Shapes.Where(s => !IsUnlabelledLine(s) && !string.IsNullOrWhiteSpace(s.Text)).ToList();
        foreach (var line in lines)
        {
            var (start, end) = LineEnds(line);
            var from = Touching(start, targets);
            var to = Touching(end, targets);
            var joins = from is not null && to is not null && from != to ? $"{from.ListTitle} → {to.ListTitle}"
                : from is not null ? $"From {from.ListTitle}"
                : to is not null ? $"To {to.ListTitle}"
                : "";
            line.SetLineDescription(joins, Direction(line));
        }
    }

    private static bool IsUnlabelledLine(ShapeCanvasItemViewModel s) =>
        string.Equals(s.Prst, "line", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(s.Text);

    /// <summary>The line's two ends in canvas coordinates (a plain "line" box runs corner to corner).</summary>
    internal static ((double X, double Y) Start, (double X, double Y) End) LineEnds(ShapeCanvasItemViewModel line)
    {
        (double X, double Y) At((double X, double Y) local) => (line.X + local.X / 100 * line.Width, line.Y + local.Y / 100 * line.Height);
        return line.PathPoints is { Count: >= 2 } pts
            ? (At(pts[0]), At(pts[^1]))
            : ((line.X, line.Y), (line.X + line.Width, line.Y + line.Height));
    }

    /// <summary>The smallest labelled shape the point touches: a card inside a lane, not the lane.</summary>
    private static ShapeCanvasItemViewModel? Touching((double X, double Y) p, List<ShapeCanvasItemViewModel> shapes) =>
        shapes.Where(s => p.X >= s.X - TouchTolerance && p.X <= s.X + s.Width + TouchTolerance
                       && p.Y >= s.Y - TouchTolerance && p.Y <= s.Y + s.Height + TouchTolerance)
              .OrderBy(s => s.Width * s.Height)
              .FirstOrDefault();

    private static string Direction(ShapeCanvasItemViewModel line)
    {
        if (line.PathPoints is { Count: > 2 }) return "Elbow";
        var (a, b) = LineEnds(line);
        double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        return dy <= 2 || dy < dx * 0.05 ? "Horizontal" : dx <= 2 || dx < dy * 0.05 ? "Vertical" : "Diagonal";
    }

    private void OnShapesMutated()
    {
        WatchShapes();
        DescribeLines();
        RefreshLineStats();
        OnPropertyChanged(nameof(HasShapes));
        InsertIntoDocumentCommand.NotifyCanExecuteChanged();
        ExportDocxCommand.NotifyCanExecuteChanged();
        ExportDotxCommand.NotifyCanExecuteChanged();
        ApplyPaletteThemeCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        RaiseSelectionChanged();
    }

    [ObservableProperty]
    private ShapeCanvasItemViewModel? _selectedShape;

    // ---- multi-selection ----
    // SelectedShape is the primary (the one the inspector edits); IsSelected marks every shape in
    // the selection. Align / distribute act on the selection only — they used to move EVERY shape
    // on the canvas, so "Align left" on a pyramid stacked the whole diagram against one edge.

    private bool _additiveSelect;

    /// <summary>Number of shapes currently selected (primary + Ctrl-click additions).</summary>
    public int SelectionCount => Shapes.Count(s => s.IsSelected);

    public bool HasSelection => SelectedShape is not null;
    public bool CanAlign => SelectionCount >= 2;
    public bool IsMultiSelect => SelectionCount >= 2;
    public bool CanDistribute => SelectionCount >= 3;

    /// <summary>Ctrl+click: add the shape to the selection, or take it out again.</summary>
    public void ToggleSelection(ShapeCanvasItemViewModel shape)
    {
        if (!Shapes.Contains(shape)) return;
        _additiveSelect = true;
        try
        {
            if (shape.IsSelected)
            {
                shape.IsSelected = false;
                if (ReferenceEquals(SelectedShape, shape))
                    SelectedShape = Shapes.LastOrDefault(s => s.IsSelected);
            }
            else
            {
                shape.IsSelected = true;
                SelectedShape = shape;
            }
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(HasShapes))]
    public void SelectAll()
    {
        if (Shapes.Count == 0 || IsDense) return;
        _additiveSelect = true;
        try
        {
            foreach (var s in Shapes) s.IsSelected = true;
            SelectedShape = Shapes[^1];
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        StatusMessage = $"Selected all {Shapes.Count} shapes.";
    }

    public void ClearSelection() => SelectedShape = null;

    /// <summary>Plain click on a shape: an unselected shape becomes the only selection; a shape
    /// already in a multi-selection becomes the primary WITHOUT dropping the others, so the whole
    /// group can be dragged.</summary>
    public void ClickSelect(ShapeCanvasItemViewModel shape)
    {
        if (!shape.IsSelected) { SelectedShape = shape; return; }
        _additiveSelect = true;
        try { SelectedShape = shape; }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
    }

    /// <summary>Selected shapes, in canvas order.</summary>
    public IReadOnlyList<ShapeCanvasItemViewModel> Selection => Shapes.Where(s => s.IsSelected).ToList();

    private IEnumerable<ShapeCanvasItemViewModel> SelectedItems => Shapes.Where(s => s.IsSelected);

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanAlign));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(CanDistribute));
        OnPropertyChanged(nameof(SelectionSummary));
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        AlignLeftCommand.NotifyCanExecuteChanged();
        AlignCenterCommand.NotifyCanExecuteChanged();
        AlignRightCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged();
        AlignMiddleCommand.NotifyCanExecuteChanged();
        AlignBottomCommand.NotifyCanExecuteChanged();
        DistributeHorizontalCommand.NotifyCanExecuteChanged();
        DistributeVerticalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Toolbar readout: what the align/distribute buttons will act on.</summary>
    public string SelectionSummary => SelectionCount switch
    {
        0 => Shapes.Count == 0 ? "" : "Ctrl+click shapes to select several",
        1 => "1 selected · Ctrl+click to add more",
        var n => $"{n} selected",
    };

    // ---- work that hasn't gone anywhere ----
    // The canvas as it last left the studio: inserted into the document, exported to Word,
    // copied as Markdown or loaded from Markdown. Closing the window used to drop a diagram
    // without a word; the window asks first while the canvas differs from this.

    private List<ComposedShape> _keptCanvas = new();

    /// <summary>True when the canvas has shapes that haven't been inserted, exported or copied
    /// since they last changed. Closing the studio would lose them.</summary>
    public bool HasUnkeptWork => Shapes.Count > 0 && !SameCanvas(_keptCanvas, SnapshotComposed());

    /// <summary>Record that the canvas as it stands has gone somewhere safe.</summary>
    public void MarkKept() => _keptCanvas = SnapshotComposed();

    // ---- undo / redo ----
    // Snapshot-based: every structural change (add, delete, duplicate, clear, preset, align,
    // recolour, move, trace, load) records the canvas first. Clear used to say "can't be undone".

    private const int UndoDepth = 40;
    private readonly List<List<ComposedShape>> _undo = new();
    private readonly List<List<ComposedShape>> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Record the current canvas so the next change can be undone.</summary>
    public void RecordUndo()
    {
        var snap = SnapshotComposed();
        if (_undo.Count > 0 && SameCanvas(_undo[^1], snap)) return;
        _undo.Add(snap);
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        RaiseUndoChanged();
    }

    private static bool SameCanvas(List<ComposedShape> a, List<ComposedShape> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Prst != y.Prst || x.X != y.X || x.Y != y.Y || x.W != y.W || x.H != y.H ||
                x.Fill != y.Fill || x.Rot != y.Rot || x.Text != y.Text || x.StrokeWidthPt != y.StrokeWidthPt || !ReferenceEquals(x.PathPoints, y.PathPoints))
                return false;
        }
        return true;
    }

    private void RaiseUndoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public System.Threading.Tasks.Task UndoAsync() => StepHistoryAsync(_undo, _redo, "Undid the last change");

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public System.Threading.Tasks.Task RedoAsync() => StepHistoryAsync(_redo, _undo, "Redid the change");

    private async System.Threading.Tasks.Task StepHistoryAsync(List<List<ComposedShape>> from, List<List<ComposedShape>> to, string message)
    {
        // Skip entries identical to the canvas (e.g. recorded when the inspector took focus but
        // nothing was edited) so every Ctrl+Z visibly changes something.
        var current = SnapshotComposed();
        while (from.Count > 0 && SameCanvas(from[^1], current)) from.RemoveAt(from.Count - 1);
        if (from.Count == 0) { RaiseUndoChanged(); return; }
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        Shapes = new ObservableCollection<ShapeCanvasItemViewModel>(target.Select(ToItem));
        SelectedShape = null;
        await RefreshCanvasModeAsync();
        StatusMessage = $"{message} — {Shapes.Count:N0} shape{(Shapes.Count == 1 ? "" : "s")} on the canvas.";
        RaiseUndoChanged();
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Move every selected shape by (dx, dy), stopping the whole group at the canvas
    /// origin. Returns the distance actually moved. Each shape used to be clamped on its own, so
    /// dragging a group into the left edge squashed it into one column.</summary>
    public (double Dx, double Dy) NudgeSelection(double dx, double dy)
    {
        var selected = SelectedItems.ToList();
        if (selected.Count == 0) return (0, 0);
        dx = Math.Max(dx, -selected.Min(s => s.X));
        dy = Math.Max(dy, -selected.Min(s => s.Y));
        if (dx == 0 && dy == 0) return (0, 0);
        foreach (var s in selected)
        {
            s.X = Math.Max(0, s.X + dx);
            s.Y = Math.Max(0, s.Y + dy);
        }
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (dx, dy);
    }

    // ---- canvas resize handles ----

    /// <summary>Smallest width or height a shape can be dragged down to.</summary>
    public const double MinShapeSize = 8;

    /// <summary>Which edges a resize handle moves.</summary>
    [Flags]
    public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    /// <summary>The rectangle a shape takes when one of its handles is dragged by (dx, dy) from
    /// where it started. The opposite edge stays put, the shape never shrinks below
    /// <see cref="MinShapeSize"/> or crosses the canvas origin, and <paramref name="keepAspect"/>
    /// (Shift on a corner) keeps its proportions.</summary>
    public static (double X, double Y, double W, double H) ResizeRect(
        double x, double y, double w, double h, ResizeEdges edges, double dx, double dy, bool keepAspect)
    {
        double left = x, top = y, right = x + w, bottom = y + h;
        if (edges.HasFlag(ResizeEdges.Left)) left = Math.Clamp(x + dx, 0, right - MinShapeSize);
        if (edges.HasFlag(ResizeEdges.Right)) right = Math.Max(left + MinShapeSize, x + w + dx);
        if (edges.HasFlag(ResizeEdges.Top)) top = Math.Clamp(y + dy, 0, bottom - MinShapeSize);
        if (edges.HasFlag(ResizeEdges.Bottom)) bottom = Math.Max(top + MinShapeSize, y + h + dy);

        bool corner = (edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0 && (edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;
        if (keepAspect && corner && w > 0 && h > 0)
        {
            // Follow whichever side the pointer stretched more, and size the other to match.
            double nw = right - left, nh = bottom - top, ratio = w / h;
            if (nw / w >= nh / h) nh = nw / ratio; else nw = nh * ratio;
            if (edges.HasFlag(ResizeEdges.Left)) left = Math.Max(0, right - nw); else right = left + nw;
            if (edges.HasFlag(ResizeEdges.Top)) top = Math.Max(0, bottom - nh); else bottom = top + nh;
        }
        return (Math.Round(left, 1), Math.Round(top, 1), Math.Round(right - left, 1), Math.Round(bottom - top, 1));
    }

    /// <summary>
    /// <see cref="ResizeRect"/> for a shape turned by <paramref name="rotation"/> degrees about its
    /// centre. The pointer's drag is read in the shape's own (turned) frame, so dragging the right
    /// handle of a 90°-turned shape downwards widens it; and the handle opposite the one dragged
    /// stays where it is on screen, as in Word and PowerPoint. Unrotated shapes take the
    /// <see cref="ResizeRect"/> path unchanged.
    /// </summary>
    public static (double X, double Y, double W, double H) ResizeRotatedRect(
        double x, double y, double w, double h, double rotation, ResizeEdges edges, double dx, double dy, bool keepAspect)
    {
        double turn = ((rotation % 360) + 360) % 360;
        if (turn == 0) return ResizeRect(x, y, w, h, edges, dx, dy, keepAspect);

        double rad = turn * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        // The drag in the shape's frame (rotate the screen delta back by the shape's angle).
        double ldx = dx * cos + dy * sin, ldy = -dx * sin + dy * cos;

        double left = -w / 2, top = -h / 2, right = w / 2, bottom = h / 2;
        if (edges.HasFlag(ResizeEdges.Left)) left = Math.Min(right - MinShapeSize, left + ldx);
        if (edges.HasFlag(ResizeEdges.Right)) right = Math.Max(left + MinShapeSize, right + ldx);
        if (edges.HasFlag(ResizeEdges.Top)) top = Math.Min(bottom - MinShapeSize, top + ldy);
        if (edges.HasFlag(ResizeEdges.Bottom)) bottom = Math.Max(top + MinShapeSize, bottom + ldy);

        bool corner = (edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0 && (edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;
        if (keepAspect && corner && w > 0 && h > 0)
        {
            double nw = right - left, nh = bottom - top, ratio = w / h;
            if (nw / w >= nh / h) nh = nw / ratio; else nw = nh * ratio;
            if (edges.HasFlag(ResizeEdges.Left)) left = right - nw; else right = left + nw;
            if (edges.HasFlag(ResizeEdges.Top)) top = bottom - nh; else bottom = top + nh;
        }

        // The new box's centre, in the shape's frame, turned back onto the canvas.
        double lcx = (left + right) / 2, lcy = (top + bottom) / 2;
        double cx = x + w / 2 + lcx * cos - lcy * sin, cy = y + h / 2 + lcx * sin + lcy * cos;
        double nwFinal = right - left, nhFinal = bottom - top;
        return (Math.Round(cx - nwFinal / 2, 1), Math.Round(cy - nhFinal / 2, 1), Math.Round(nwFinal, 1), Math.Round(nhFinal, 1));
    }

    /// <summary>Where the handle for <paramref name="edges"/> sits on the canvas for a shape turned
    /// by <paramref name="rotation"/> degrees about its centre.</summary>
    public static (double X, double Y) HandlePosition(double x, double y, double w, double h, double rotation, ResizeEdges edges)
    {
        double lx = edges.HasFlag(ResizeEdges.Left) ? -w / 2 : edges.HasFlag(ResizeEdges.Right) ? w / 2 : 0;
        double ly = edges.HasFlag(ResizeEdges.Top) ? -h / 2 : edges.HasFlag(ResizeEdges.Bottom) ? h / 2 : 0;
        double rad = rotation * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        return (x + w / 2 + lx * cos - ly * sin, y + h / 2 + lx * sin + ly * cos);
    }

    /// <summary>The resize cursor that matches a handle's direction once the shape is turned:
    /// 0 = west-east, 1 = northwest-southeast, 2 = north-south, 3 = northeast-southwest.</summary>
    public static int HandleCursorAxis(ResizeEdges edges, double rotation)
    {
        double lx = edges.HasFlag(ResizeEdges.Left) ? -1 : edges.HasFlag(ResizeEdges.Right) ? 1 : 0;
        double ly = edges.HasFlag(ResizeEdges.Top) ? -1 : edges.HasFlag(ResizeEdges.Bottom) ? 1 : 0;
        double angle = Math.Atan2(ly, lx) * 180 / Math.PI + rotation;      // screen y points down
        double axis = ((angle % 180) + 180) % 180;                          // a handle and its opposite share a cursor
        return (int)Math.Round(axis / 45) % 4;
    }

    /// <summary>Applies a handle drag to <paramref name="shape"/> from its starting rectangle,
    /// in the shape's own frame when it is turned.</summary>
    // ---- connector ends ----

    /// <summary>A line's points in canvas coordinates (its 0..100 local points placed in its box).</summary>
    public static List<(double X, double Y)> ConnectorPoints(ShapeCanvasItemViewModel s)
    {
        if (s.PathPoints is null) return new();
        double cx = s.X + s.Width / 2, cy = s.Y + s.Height / 2;
        double rad = s.Rotation * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        // A turned line is drawn turned about its box's centre: so are its points.
        return s.PathPoints.Select(p =>
        {
            double x = s.X + p.X / 100 * s.Width - cx, y = s.Y + p.Y / 100 * s.Height - cy;
            return (cx + x * cos - y * sin, cy + x * sin + y * cos);
        }).ToList();
    }

    /// <summary>Puts a line through <paramref name="points"/> (canvas coordinates): its box becomes
    /// their bounds and the points are stored 0..100 inside it, as every connector is. A straight
    /// horizontal or vertical line gets a 2 px box with its points on the centre line.</summary>
    public static void SetConnectorPoints(ShapeCanvasItemViewModel s, IReadOnlyList<(double X, double Y)> points)
    {
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        (double Start, double Size, Func<double, double> Local) Axis(double min, double max)
        {
            double span = max - min;
            if (span < 0.5) { double c = (min + max) / 2; return (c - 1, 2, _ => 50); }
            return (min, span, v => (v - min) / span * 100);
        }
        var ax = Axis(minX, maxX);
        var ay = Axis(minY, maxY);
        s.X = ax.Start;
        s.Y = ay.Start;
        s.Width = ax.Size;
        s.Height = ay.Size;
        // The points are where the line is drawn, so a turn is now part of them.
        s.Rotation = 0;
        s.PathPoints = points.Select(p => (ax.Local(p.X), ay.Local(p.Y))).ToList();
        s.NotifyPathChanged();
    }

    /// <summary>The nearest connection point (a side's midpoint or the centre) of a shape within
    /// <paramref name="radius"/> of (x, y), turned with the shape, or null. Lines aren't targets.</summary>
    public (double X, double Y, ShapeCanvasItemViewModel Shape)? SnapTarget(double x, double y, ShapeCanvasItemViewModel? except, double radius = 14)
    {
        (double X, double Y, ShapeCanvasItemViewModel Shape)? best = null;
        double bestDist = radius;
        foreach (var shape in Shapes)
        {
            if (ReferenceEquals(shape, except) || shape.PathPoints is { Count: >= 2 }) continue;
            foreach (var edges in new[] { ResizeEdges.Top, ResizeEdges.Right, ResizeEdges.Bottom, ResizeEdges.Left, ResizeEdges.None })
            {
                var (px, py) = HandlePosition(shape.X, shape.Y, shape.Width, shape.Height, shape.Rotation, edges);
                double d = Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
                if (d <= bestDist) { bestDist = d; best = (px, py, shape); }
            }
        }
        return best;
    }

    /// <summary>
    /// Re-routes a connector: moves point <paramref name="index"/> (an end, or a bend of an elbow
    /// line) to (x, y), snapping onto a shape's side or centre when one is close, so a line can be
    /// moved from one shape to another. Returns the shape it snapped to, if any.
    /// </summary>
    public ShapeCanvasItemViewModel? MoveConnectorPoint(ShapeCanvasItemViewModel line, int index, double x, double y, bool snap = true)
    {
        var points = ConnectorPoints(line);
        if (index < 0 || index >= points.Count) return null;
        ShapeCanvasItemViewModel? target = null;
        // Only the two ends attach to shapes; a bend goes where it's put.
        if (snap && (index == 0 || index == points.Count - 1) && SnapTarget(x, y, line) is { } hit)
            (x, y, target) = hit;
        points[index] = (x, y);
        SetConnectorPoints(line, points);
        CanvasMode = "editable";
        PreviewPng = null;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return target;
    }

    public void ResizeShape(ShapeCanvasItemViewModel shape, (double X, double Y, double W, double H) start,
        ResizeEdges edges, double dx, double dy, bool keepAspect)
    {
        var r = ResizeRotatedRect(start.X, start.Y, start.W, start.H, shape.Rotation, edges, dx, dy, keepAspect);
        shape.X = r.X;
        shape.Y = r.Y;
        shape.Width = r.W;
        shape.Height = r.H;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- marquee selection ----

    /// <summary>Selects every shape the dragged rectangle touches (adding to the selection when
    /// <paramref name="additive"/>, i.e. Ctrl or Shift is held). Returns how many it touched.</summary>
    public int SelectInRect(double x, double y, double w, double h, bool additive)
    {
        if (IsDense) return 0;
        var hits = Shapes.Where(s => s.X < x + w && s.X + s.Width > x && s.Y < y + h && s.Y + s.Height > y).ToList();
        _additiveSelect = true;
        try
        {
            foreach (var s in Shapes)
            {
                bool on = hits.Contains(s) || (additive && s.IsSelected);
                if (s.IsSelected != on) s.IsSelected = on;
            }
            SelectedShape = hits.Count > 0 ? hits[^1] : Shapes.LastOrDefault(s => s.IsSelected);
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        return hits.Count;
    }

    [ObservableProperty]
    private string _statusMessage = "Ready — pick a preset, draw a shape, or convert a picture.";

    public event EventHandler<string>? InsertToDocumentRequested;

    // ---- trace controls ----

    [ObservableProperty]
    private double _traceDensity = 480;

    /// <summary>Log-scale slider position 0..100 mapping to <see cref="TraceDensity"/> (32→16,384
    /// scanlines) so low densities stay reachable next to the extreme ones.</summary>
    [ObservableProperty]
    private double _traceDensityLog = 43;

    /// <summary>0 = Engraved, 1 = Edges, 2 = Silhouette, 3 = Scanlines.</summary>
    [ObservableProperty]
    private int _traceModeIndex;

    [ObservableProperty]
    private bool _traceMonochrome;

    [ObservableProperty]
    private bool _hasImage;

    partial void OnTraceDensityLogChanged(double value)
    {
        double t = Math.Clamp(value, 0, 100) / 100.0;
        double min = Math.Log10(ImageLineTracer.MinRows);
        double max = Math.Log10(ImageLineTracer.MaxRows);
        TraceDensity = Math.Round(Math.Pow(10, min + t * (max - min)));
    }

    // ---- canvas presentation ----

    /// <summary>"empty" | "editable" (one Path per shape) | "dense" (raster line-art preview).</summary>
    [ObservableProperty]
    private string _canvasMode = "empty";

    [ObservableProperty]
    private byte[]? _previewPng;

    [ObservableProperty]
    private string _lineStats = "";

    /// <summary>The count beside "Shapes on canvas". It was set by undo and the picture
    /// converters only, so after a preset or a drawn shape the header showed no count at all.</summary>
    private void RefreshLineStats()
    {
        int n = Shapes.Count;
        LineStats = n == 0 ? ""
            : IsDense ? $"{n:N0} line{(n == 1 ? "" : "s")}"
            : $"{n:N0} shape{(n == 1 ? "" : "s")}";
    }

    public bool IsEmpty => CanvasMode == "empty";
    public bool IsEditable => CanvasMode == "editable";
    public bool IsDense => CanvasMode == "dense";

    public event EventHandler? CanvasChanged;

    partial void OnCanvasModeChanged(string value)
    {
        RefreshLineStats();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsDense));
        OnPropertyChanged(nameof(InspectorEmptyTitle));
        OnPropertyChanged(nameof(InspectorEmptyHint));
        OnPropertyChanged(nameof(ShapesListHint));
    }

    public string ShapesListHint => IsEmpty
        ? "No shapes yet — everything you draw is listed here."
        : IsDense
            ? "Traced line art — select a line here to inspect it."
            : "Click any shape to select and inspect it.";

    public bool HasSelectedShape => SelectedShape is not null;

    /// <summary>Inspector empty state — differs between a blank canvas and an unselected one.</summary>
    public string InspectorEmptyTitle => IsEmpty ? "Nothing to inspect yet" : "No shape selected";

    public string InspectorEmptyHint => IsEmpty
        ? "Pick a preset or draw a shape — its properties appear here."
        : "Click a shape on the canvas or in the list below to edit its type, position, size, fill and label.";

    partial void OnSelectedShapeChanged(ShapeCanvasItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedShape));
        if (!_additiveSelect)
        {
            foreach (var s in Shapes)
            {
                if (s.IsSelected != (s == value)) s.IsSelected = s == value;
            }
        }
        RaiseSelectionChanged();
    }

    [ObservableProperty]
    private string _selectedPresetCategory = "All Categories";

    public static readonly string[] PresetCategoriesList =
    {
        "All Categories",
        "Hierarchy & Structure",
        "Process & Workflow",
        "Cycles & Loops",
        "Matrices & Strategy",
        "Relationships & Venns",
        "Roadmaps & Timelines",
        "Architecture & Cloud",
        "Funnels & Pipelines",
        "Lists & Dashboards"
    };

    public IReadOnlyList<string> PresetCategories => PresetCategoriesList;

    public ObservableCollection<DiagramPreset> AllPresets { get; } = new();

    [ObservableProperty]
    private ObservableCollection<DiagramPreset> _filteredPresets = new();

    partial void OnSelectedPresetCategoryChanged(string value)
    {
        UpdateFilteredPresets();
    }

    public void UpdateFilteredPresets()
    {
        var list = string.IsNullOrWhiteSpace(SelectedPresetCategory) || SelectedPresetCategory == "All Categories"
            ? AllPresets
            : new ObservableCollection<DiagramPreset>(AllPresets.Where(p => p.Category == SelectedPresetCategory));
        FilteredPresets = new ObservableCollection<DiagramPreset>(list);
    }

    [RelayCommand]
    public void ApplyPreset(DiagramPreset? preset)
    {
        if (preset == null) return;
        preset.Generate(this);
        // A fresh diagram opens with nothing selected — the generator's last shape used to come up
        // selected (dashed), which read as "this one is special".
        SelectedShape = null;
    }

    public string[] GetPaletteColors() =>
        ColorPalettes.TryGetValue(SelectedPaletteName ?? "Office Blue", out var colors)
            ? colors
            : ColorPalettes["Office Blue"];

    public ShapeDesignStudioViewModel() : this(registerPresets: true)
    {
    }

    private ShapeDesignStudioViewModel(bool registerPresets)
    {
        if (registerPresets)
        {
            RegisterPresets();
            UpdateFilteredPresets();
        }
        // The initial collection is assigned to the field, so OnShapesChanged never saw it.
        Shapes.CollectionChanged += OnShapesCollectionChanged;
    }

    /// <summary>The shapes <paramref name="preset"/> builds in <paramref name="paletteName"/>,
    /// generated on a throwaway studio so the live canvas, its undo history and its status line
    /// are untouched. The preset gallery draws its miniatures from these.</summary>
    public static IReadOnlyList<ShapeCanvasItemViewModel> PreviewPreset(DiagramPreset preset, string? paletteName)
    {
        var scratch = new ShapeDesignStudioViewModel(registerPresets: false)
        {
            SelectedPaletteName = string.IsNullOrWhiteSpace(paletteName) ? "Office Blue" : paletteName,
        };
        preset.Generate(scratch);
        return scratch.Shapes.ToList();
    }

    public ShapeCanvasItemViewModel AddShapeAt(string prst, double x, double y, double width = 120, double height = 70, string? fill = null, string text = "", int rot = 0)
    {
        var colors = GetPaletteColors();
        string resolvedFill = fill ?? colors[Shapes.Count % colors.Length];
        var item = new ShapeCanvasItemViewModel
        {
            Prst = prst,
            Name = prst,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Fill = resolvedFill,
            Text = text,
            Rotation = rot,
            IsSelected = true
        };
        Shapes.Add(item);
        SelectedShape = item;
        CanvasMode = "editable";
        PreviewPng = null;
        StatusMessage = $"Placed {item.DisplayName.ToLowerInvariant()} at ({x:F0}, {y:F0})";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public ShapeCanvasItemViewModel AddConnectorLine(double x1, double y1, double x2, double y2, string color = "8E9297", double strokeWidthPt = 2.0)
    {
        double minX = Math.Min(x1, x2);
        double minY = Math.Min(y1, y2);
        double w = Math.Max(2.0, Math.Abs(x2 - x1));
        double h = Math.Max(2.0, Math.Abs(y2 - y1));

        var pts = new List<(double X, double Y)>();
        if (Math.Abs(x2 - x1) < 0.5)
        {
            pts.Add((50, 0));
            pts.Add((50, 100));
        }
        else if (Math.Abs(y2 - y1) < 0.5)
        {
            pts.Add((0, 50));
            pts.Add((100, 50));
        }
        else if ((x2 >= x1 && y2 >= y1) || (x2 <= x1 && y2 <= y1))
        {
            pts.Add((0, 0));
            pts.Add((100, 100));
        }
        else
        {
            pts.Add((0, 100));
            pts.Add((100, 0));
        }

        var item = new ShapeCanvasItemViewModel
        {
            Prst = "line",
            Name = "Connector",
            X = minX,
            Y = minY,
            Width = w,
            Height = h,
            Fill = color,
            PathPoints = pts,
            StrokeWidthPt = strokeWidthPt,
            IsSelected = false
        };
        Shapes.Add(item);
        return item;
    }

    /// <summary>An open polyline through absolute canvas points (elbow connectors, curves), stored
    /// like any connector: a bounding box plus 0..100 local points.</summary>
    public ShapeCanvasItemViewModel AddPolylinePath(IReadOnlyList<(double X, double Y)> points, string color = "8E9297", double strokeWidthPt = 2.0)
    {
        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        double w = Math.Max(2.0, points.Max(p => p.X) - minX);
        double h = Math.Max(2.0, points.Max(p => p.Y) - minY);
        var item = new ShapeCanvasItemViewModel
        {
            Prst = "line",
            Name = "Connector",
            X = minX,
            Y = minY,
            Width = w,
            Height = h,
            Fill = color,
            PathPoints = points.Select(p => ((p.X - minX) / w * 100, (p.Y - minY) / h * 100)).ToList(),
            StrokeWidthPt = strokeWidthPt,
            IsSelected = false
        };
        Shapes.Add(item);
        return item;
    }

    /// <summary>
    /// Concentric rings (outermost first) with one labelled callout per ring to the right, joined
    /// by a leader line from the ring's own band. A label centred in a ring sits under the rings
    /// inside it, so the bullseye and onion presets used to hide every label but the centre's.
    /// </summary>
    private void AddRingsWithCallouts(double cx, double cy, double[] radii, double aspect, string[] labels)
    {
        var colors = GetPaletteColors();
        int n = radii.Length;
        for (int i = 0; i < n; i++)
        {
            double rx = radii[i], ry = radii[i] * aspect;
            AddShapeAt("ellipse", cx - rx, cy - ry, rx * 2, ry * 2, colors[i % colors.Length]);
        }

        const double calloutW = 230, calloutH = 56, gap = 14;
        double calloutX = cx + radii[0] + 70;
        double top = cy - (n * calloutH + (n - 1) * gap) / 2;
        for (int i = 0; i < n; i++)
        {
            // Anchor in the middle of the ring's visible band, fanning from upper right to lower right.
            double band = i < n - 1 ? (radii[i] + radii[i + 1]) / 2 : radii[i] * 0.45;
            double angle = (n == 1 ? 0 : -50 + 80.0 * i / (n - 1)) * Math.PI / 180;
            double ax = cx + band * Math.Cos(angle), ay = cy + band * aspect * Math.Sin(angle);
            double ty = top + i * (calloutH + gap) + calloutH / 2;
            AddPolylinePath(new[] { (ax, ay), (calloutX - 24, ty), (calloutX, ty) }, "8E9297", 1.5);
        }
        for (int i = 0; i < n; i++)
        {
            AddShapeAt("roundrect", calloutX, top + i * (calloutH + gap), calloutW, calloutH, colors[i % colors.Length], labels[i]);
        }
    }

    /// <summary>Duplicates every selected shape (offset 20 px) and selects the copies.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void DuplicateSelected()
    {
        var sources = SelectedItems.ToList();
        if (sources.Count == 0) return;
        RecordUndo();
        var clones = sources.Select(s => new ShapeCanvasItemViewModel
        {
            Prst = s.Prst,
            Name = s.Name,
            X = s.X + 20,
            Y = s.Y + 20,
            Width = s.Width,
            Height = s.Height,
            Fill = s.Fill,
            Rotation = s.Rotation,
            Text = s.Text,
            TextColor = s.TextColor,
            PathPoints = s.PathPoints is null ? null : new List<(double X, double Y)>(s.PathPoints),
            StrokeWidthPt = s.StrokeWidthPt,
        }).ToList();
        SelectedShape = null;
        foreach (var c in clones) Shapes.Add(c);
        _additiveSelect = true;
        try
        {
            foreach (var c in clones) c.IsSelected = true;
            SelectedShape = clones[^1];
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        StatusMessage = clones.Count == 1
            ? $"Duplicated {clones[0].DisplayName.ToLowerInvariant()}"
            : $"Duplicated {clones.Count} shapes";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes every selected shape.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async System.Threading.Tasks.Task RemoveSelectedAsync()
    {
        var doomed = SelectedItems.ToList();
        if (doomed.Count == 0) return;
        RecordUndo();
        SelectedShape = null;
        foreach (var s in doomed) Shapes.Remove(s);
        await RefreshCanvasModeAsync();
        StatusMessage = doomed.Count == 1 ? "Deleted 1 shape · Ctrl+Z to undo" : $"Deleted {doomed.Count} shapes · Ctrl+Z to undo";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ClearAll()
    {
        RecordUndo();
        Shapes.Clear();
        SelectedShape = null;
        CanvasMode = "empty";
        PreviewPng = null;
        StatusMessage = "Canvas cleared.";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasShapes))]
    public void InsertIntoDocument()
    {
        if (Shapes.Count == 0)
        {
            StatusMessage = "Nothing to insert — add shapes or a SmartArt template first.";
            return;
        }
        var composed = SnapshotComposed();
        string block = ShapeMarkdownCodec.Serialize(composed);
        InsertToDocumentRequested?.Invoke(this, block);
        MarkKept();
        StatusMessage = $"✓ Inserted {composed.Count} native DrawingML shapes into document.";
    }

    [RelayCommand(CanExecute = nameof(HasShapes))]
    public void ApplyPaletteTheme()
    {
        if (Shapes.Count == 0) return;
        RecordUndo();
        var colors = GetPaletteColors();
        // Swap colour for colour: a shape in the old scheme's k-th colour (or its lane tint) takes
        // the new scheme's k-th. Recolouring by position used to scramble every preset whose
        // colours mean something — white swimlane cards turned blue, RACI and risk cells shuffled,
        // ring callouts stopped matching their rings.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ColorPalettes.TryGetValue(_previousPaletteName, out var old))
        {
            for (int k = 0; k < old.Length; k++)
            {
                map.TryAdd(old[k], colors[k % colors.Length]);
                map.TryAdd(Tint(old[k], LaneTint), Tint(colors[k % colors.Length], LaneTint));
            }
        }
        int changed = 0;
        foreach (var s in Shapes)
        {
            if (s.PathPoints is { Count: >= 2 }) continue;
            if (map.TryGetValue((s.Fill ?? "").TrimStart('#'), out var fill)) { s.Fill = fill; changed++; }
        }
        if (changed == 0)
        {
            // Nothing came from the previous scheme (an imported or hand-coloured diagram): paint
            // the shapes in order, as before.
            int idx = 0;
            foreach (var s in Shapes)
            {
                if (s.PathPoints is { Count: >= 2 }) continue;
                s.Fill = colors[idx++ % colors.Length];
            }
        }
        _previousPaletteName = SelectedPaletteName ?? "Office Blue";
        StatusMessage = $"✓ Applied '{SelectedPaletteName}' colours across {Shapes.Count} shapes · Ctrl+Z to undo";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    // The scheme the canvas was last painted in — ApplyPaletteTheme maps from it.
    private string _previousPaletteName = "Office Blue";

    partial void OnSelectedPaletteNameChanging(string value) => _previousPaletteName = SelectedPaletteName ?? "Office Blue";

    /// <summary>How far toward white a swimlane body is lightened from its lane colour.</summary>
    private const double LaneTint = 0.8;

    /// <summary><paramref name="hex"/> blended <paramref name="toWhite"/> (0..1) of the way to white.</summary>
    public static string Tint(string hex, double toWhite)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return hex;
        int Mix(int c) => (int)Math.Round(c + (255 - c) * toWhite);
        return $"{Mix((rgb >> 16) & 255):X2}{Mix((rgb >> 8) & 255):X2}{Mix(rgb & 255):X2}";
    }

    // =========================================================================
    // SmartArt Composite Diagram Templates (1-Click Generation)
    // =========================================================================

    // =========================================================================
    // SmartArt Composite Diagram Templates & Presets (40+ Professional Layouts)
    // =========================================================================

    public void RegisterPresets()
    {
        AllPresets.Clear();

        // --- Hierarchy & Structure ---
        AllPresets.Add(new DiagramPreset { Name = "4-Tier Strategy Pyramid", Category = "Hierarchy & Structure", Icon = "🔺", Description = "4-tier strategic hierarchy from vision to foundation.", Generate = vm => vm.GeneratePyramidTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "5-Tier Maturity Pyramid", Category = "Hierarchy & Structure", Icon = "🔺", Description = "5-level capability and organizational maturity model.", Generate = vm => vm.Generate5TierPyramidTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Inverted Filter Pyramid", Category = "Hierarchy & Structure", Icon = "🔻", Description = "Top-down filtering and qualification model.", Generate = vm => vm.GenerateInvertedPyramidTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Executive Org Chart", Category = "Hierarchy & Structure", Icon = "🏢", Description = "Board, executive leadership, and functional teams.", Generate = vm => vm.GenerateOrgChartTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Matrix Divisional Org Chart", Category = "Hierarchy & Structure", Icon = "🏢", Description = "Cross-functional reporting matrix across 2 divisions.", Generate = vm => vm.GenerateMatrixOrgChartTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Horizontal Tree Hierarchy", Category = "Hierarchy & Structure", Icon = "🌲", Description = "Left-to-right branching breakdown tree.", Generate = vm => vm.GenerateHorizontalTreeTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Agile Squads & Chapters", Category = "Hierarchy & Structure", Icon = "👥", Description = "Spotify model with tribe leads, squads, and chapter pills.", Generate = vm => vm.GenerateAgileSquadsTemplate() });

        // --- Process & Workflow ---
        AllPresets.Add(new DiagramPreset { Name = "4-Step Chevron Flow", Category = "Process & Workflow", Icon = "➡️", Description = "Sequential 4-phase delivery pipeline.", Generate = vm => vm.GenerateTimelineTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "5-Stage Pipeline Process", Category = "Process & Workflow", Icon = "🔄", Description = "5-stage progression with color transitions.", Generate = vm => vm.GeneratePipeline5StepTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Alternating Stepped Workflow", Category = "Process & Workflow", Icon = "🪜", Description = "Top-and-bottom alternating milestone process.", Generate = vm => vm.GenerateAlternatingProcessTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Stage-Gate Decision Process", Category = "Process & Workflow", Icon = "🚦", Description = "Phased workflow with diamond go/no-go decision gates.", Generate = vm => vm.GenerateStageGateProcessTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Linear Milestone Timeline", Category = "Process & Workflow", Icon = "📅", Description = "Chronological timeline track with date badges.", Generate = vm => vm.GenerateMilestoneTimelineTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Incident Response Flow", Category = "Process & Workflow", Icon = "*", Description = "Detect, triage, severity branch, and post-incident review.", Generate = vm => vm.GenerateIncidentResponseTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Swimlane Workflow (3 Lanes)", Category = "Process & Workflow", Icon = "🏊", Description = "Cross-departmental multi-lane flow.", Generate = vm => vm.GenerateSwimlaneWorkflowTemplate() });

        // --- Cycles & Loops ---
        AllPresets.Add(new DiagramPreset { Name = "PDCA Continuous Cycle", Category = "Cycles & Loops", Icon = "🔄", Description = "Plan, Do, Check, Act Deming quality loop.", Generate = vm => vm.GenerateCycleTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Build-Measure-Learn Loop", Category = "Cycles & Loops", Icon = "🔁", Description = "Lean startup iterative validation loop.", Generate = vm => vm.GenerateBuildMeasureLearnTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Design Thinking 5-Phase", Category = "Cycles & Loops", Icon = "💡", Description = "Empathize, Define, Ideate, Prototype, Test.", Generate = vm => vm.GenerateDesignThinkingTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "DevOps Infinity Loop", Category = "Cycles & Loops", Icon = "♾️", Description = "Continuous integration and delivery lifecycle.", Generate = vm => vm.GenerateDevOpsLoopTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "OODA Decision Loop", Category = "Cycles & Loops", Icon = "*", Description = "Observe, Orient, Decide, Act as a closed decision cycle.", Generate = vm => vm.GenerateOodaLoopTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Continuous Feedback Spiral", Category = "Cycles & Loops", Icon = "🌀", Description = "Iterative concentric improvement spiral.", Generate = vm => vm.GenerateFeedbackSpiralTemplate() });

        // --- Matrices & Strategy ---
        AllPresets.Add(new DiagramPreset { Name = "2x2 SWOT Matrix", Category = "Matrices & Strategy", Icon = "🔲", Description = "Strengths, Weaknesses, Opportunities, Threats.", Generate = vm => vm.GenerateSwotMatrixTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Eisenhower Priority Matrix", Category = "Matrices & Strategy", Icon = "⏰", Description = "Urgent vs Important decision prioritization.", Generate = vm => vm.GenerateEisenhowerMatrixTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "BCG Growth-Share Matrix", Category = "Matrices & Strategy", Icon = "⭐", Description = "Stars, Question Marks, Cash Cows, and Dogs.", Generate = vm => vm.GenerateBcgGrowthMatrixTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Risk Impact vs Likelihood 3x3", Category = "Matrices & Strategy", Icon = "⚠️", Description = "Heatmap grid for risk assessment.", Generate = vm => vm.GenerateRiskMatrixTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Ansoff Market Expansion", Category = "Matrices & Strategy", Icon = "📈", Description = "Market Penetration, Development, Diversification.", Generate = vm => vm.GenerateAnsoffMatrixTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Porter Five Forces", Category = "Matrices & Strategy", Icon = "*", Description = "Rivalry at the centre with the four surrounding forces.", Generate = vm => vm.GeneratePortersFiveForcesTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "RACI Accountability Grid", Category = "Matrices & Strategy", Icon = "📋", Description = "Responsible, Accountable, Consulted, Informed.", Generate = vm => vm.GenerateRaciGridTemplate() });

        // --- Relationships & Venns ---
        AllPresets.Add(new DiagramPreset { Name = "3-Set Venn Diagram", Category = "Relationships & Venns", Icon = "⭕", Description = "Desirability, Feasibility, Viability intersection.", Generate = vm => vm.GenerateVennTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "2-Set Core Overlap Venn", Category = "Relationships & Venns", Icon = "⭕", Description = "Two overlapping core competency sets.", Generate = vm => vm.GenerateVenn2SetTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Concentric Bullseye Target", Category = "Relationships & Venns", Icon = "🎯", Description = "Target rings for focus, growth, and vision.", Generate = vm => vm.GenerateBullseyeTargetTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Hub & Spoke Ecosystem", Category = "Relationships & Venns", Icon = "🌐", Description = "Central core platform with 6 radial satellites.", Generate = vm => vm.GenerateHubAndSpokeTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Onion Security Model", Category = "Relationships & Venns", Icon = "🧅", Description = "Defense-in-depth concentric layers.", Generate = vm => vm.GenerateOnionSecurityTemplate() });

        // --- Roadmaps & Timelines ---
        AllPresets.Add(new DiagramPreset { Name = "Quarterly Release Roadmap (Q1-Q4)", Category = "Roadmaps & Timelines", Icon = "🗺️", Description = "4-quarter product horizon roadmap.", Generate = vm => vm.GenerateQuarterlyRoadmapTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Chevron Phase Gantt", Category = "Roadmaps & Timelines", Icon = "📅", Description = "Staggered phase chevron tracks over time.", Generate = vm => vm.GenerateChevronGanttTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Alternating Timeline Events", Category = "Roadmaps & Timelines", Icon = "📍", Description = "Vertical alternating historical milestones.", Generate = vm => vm.GenerateAlternatingTimelineTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Now / Next / Later Board", Category = "Roadmaps & Timelines", Icon = "*", Description = "Horizon roadmap without false precision on dates.", Generate = vm => vm.GenerateNowNextLaterTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Customer Journey (5 Touchpoints)", Category = "Roadmaps & Timelines", Icon = "🚶", Description = "Awareness, Consideration, Purchase, Retention, Advocacy.", Generate = vm => vm.GenerateCustomerJourneyTemplate() });

        // --- Architecture & Cloud ---
        AllPresets.Add(new DiagramPreset { Name = "Enterprise Architecture Pillars", Category = "Architecture & Cloud", Icon = "🏛️", Description = "Header architrave, 3 core pillars, foundation rect.", Generate = vm => vm.GeneratePillarsTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "3-Tier Cloud Stack", Category = "Architecture & Cloud", Icon = "☁️", Description = "Presentation, Application Services, Data persistence.", Generate = vm => vm.GenerateCloudStack3TierTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Hexagonal Clean Architecture", Category = "Architecture & Cloud", Icon = "⬡", Description = "Core domain hexagon with driving/driven adapters.", Generate = vm => vm.GenerateHexagonalArchitectureTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Microservices Event Bus", Category = "Architecture & Cloud", Icon = "⚡", Description = "Event stream backbone with decoupled microservices.", Generate = vm => vm.GenerateMicroservicesBusTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Data Engineering ETL Pipeline", Category = "Architecture & Cloud", Icon = "📊", Description = "Extract, Load, Transform, Lakehouse, BI layer.", Generate = vm => vm.GenerateDataEtlPipelineTemplate() });

        // --- Funnels & Pipelines ---
        AllPresets.Add(new DiagramPreset { Name = "Marketing Acquisition Funnel", Category = "Funnels & Pipelines", Icon = "🔻", Description = "Awareness, Interest, Decision, Action stages.", Generate = vm => vm.GenerateFunnelTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Enterprise Sales Pipeline (5-Stage)", Category = "Funnels & Pipelines", Icon = "💰", Description = "Prospecting, Qualification, Proposal, Negotiation, Closed.", Generate = vm => vm.GenerateSalesPipelineTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Recruitment Pipeline (6 Stages)", Category = "Funnels & Pipelines", Icon = "*", Description = "Applied through hired, with headcount at every stage.", Generate = vm => vm.GenerateRecruitmentPipelineTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Hourglass Growth Funnel", Category = "Funnels & Pipelines", Icon = "⏳", Description = "Acquisition funnel meeting expansion and referral.", Generate = vm => vm.GenerateHourglassFunnelTemplate() });

        // --- Lists & Dashboards ---
        AllPresets.Add(new DiagramPreset { Name = "3-Column Value Cards", Category = "Lists & Dashboards", Icon = "📋", Description = "3 featured value proposition highlight panels.", Generate = vm => vm.GenerateValuePropCardsTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "4-Card Executive KPI Dashboard", Category = "Lists & Dashboards", Icon = "📊", Description = "4 executive metric scorecards with accent headers.", Generate = vm => vm.GenerateExecutiveKpiDashboardTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Hexagonal Honeycomb Matrix", Category = "Lists & Dashboards", Icon = "🐝", Description = "7 interlocking hexagon feature modules.", Generate = vm => vm.GenerateHoneycombMatrixTemplate() });
        AllPresets.Add(new DiagramPreset { Name = "Tier Comparison Matrix", Category = "Lists & Dashboards", Icon = "⚖️", Description = "Starter, Professional, and Enterprise comparison tiers.", Generate = vm => vm.GenerateFeatureComparisonTemplate() });
    }

    [RelayCommand]
    public void GeneratePyramidTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("triangle", 270, 40, 180, 85, colors[0 % colors.Length], "1 · Vision & Strategy");
        AddShapeAt("trapezoid", 215, 125, 290, 75, colors[1 % colors.Length], "2 · Core Objectives");
        AddShapeAt("trapezoid", 160, 200, 400, 75, colors[2 % colors.Length], "3 · Tactical Operations");
        AddShapeAt("trapezoid", 105, 275, 510, 75, colors[3 % colors.Length], "4 · Core Foundation");
        StatusMessage = "✓ Generated 4-Tier Strategy Pyramid";
    }

    [RelayCommand]
    public void Generate5TierPyramidTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("triangle", 280, 30, 160, 70, colors[0 % colors.Length], "Level 5 · Optimizing");
        AddShapeAt("trapezoid", 235, 100, 250, 65, colors[1 % colors.Length], "Level 4 · Quantitatively Managed");
        AddShapeAt("trapezoid", 190, 165, 340, 65, colors[2 % colors.Length], "Level 3 · Defined");
        AddShapeAt("trapezoid", 145, 230, 430, 65, colors[3 % colors.Length], "Level 2 · Managed");
        AddShapeAt("trapezoid", 100, 295, 520, 65, colors[4 % colors.Length], "Level 1 · Initial / Ad-hoc");
        StatusMessage = "✓ Generated 5-Tier Maturity Pyramid";
    }

    [RelayCommand]
    public void GenerateInvertedPyramidTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("trapezoid", 100, 40, 520, 75, colors[0 % colors.Length], "BROAD TOPIC · Market Overview", 180);
        AddShapeAt("trapezoid", 145, 115, 430, 75, colors[1 % colors.Length], "SUB-SEGMENT · Key Drivers", 180);
        AddShapeAt("trapezoid", 190, 190, 340, 75, colors[2 % colors.Length], "FOCUS AREA · Solution Scope", 180);
        AddShapeAt("triangle", 235, 265, 250, 95, colors[3 % colors.Length], "CORE INSIGHT · Recommendation", 180);
        StatusMessage = "✓ Generated Inverted Filter Pyramid";
    }

    [RelayCommand]
    public void GenerateOrgChartTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 270, 40, 180, 60, colors[0 % colors.Length], "Executive Board");
        AddShapeAt("roundrect", 70, 150, 160, 55, colors[1 % colors.Length], "CEO / Operations");
        AddShapeAt("roundrect", 280, 150, 160, 55, colors[2 % colors.Length], "CTO / Technology");
        AddShapeAt("roundrect", 490, 150, 160, 55, colors[3 % colors.Length], "CFO / Finance");
        AddShapeAt("roundrect", 210, 255, 140, 50, colors[4 % colors.Length], "Engineering");
        AddShapeAt("roundrect", 370, 255, 140, 50, colors[5 % colors.Length], "Product Team");

        AddConnectorLine(360, 100, 360, 125);
        AddConnectorLine(150, 125, 570, 125);
        AddConnectorLine(150, 125, 150, 150);
        AddConnectorLine(360, 125, 360, 150);
        AddConnectorLine(570, 125, 570, 150);
        AddConnectorLine(360, 205, 360, 230);
        AddConnectorLine(280, 230, 440, 230);
        AddConnectorLine(280, 230, 280, 255);
        AddConnectorLine(440, 230, 440, 255);
        StatusMessage = "✓ Generated Executive Organization Chart";
    }

    [RelayCommand]
    public void GenerateMatrixOrgChartTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 260, 30, 200, 55, colors[0 % colors.Length], "Executive Leadership");
        AddShapeAt("roundrect", 60, 120, 180, 50, colors[1 % colors.Length], "Division A · Products");
        AddShapeAt("roundrect", 480, 120, 180, 50, colors[2 % colors.Length], "Division B · Services");
        AddShapeAt("roundrect", 60, 210, 180, 50, colors[3 % colors.Length], "Engineering Lead");
        AddShapeAt("roundrect", 270, 210, 180, 50, colors[4 % colors.Length], "Quality Lead");
        AddShapeAt("roundrect", 480, 210, 180, 50, colors[5 % colors.Length], "Design Lead");
        AddShapeAt("roundrect", 160, 300, 180, 50, colors[0 % colors.Length], "Squad 1 · Platform");
        AddShapeAt("roundrect", 380, 300, 180, 50, colors[1 % colors.Length], "Squad 2 · Mobile");

        AddConnectorLine(360, 85, 360, 105);
        AddConnectorLine(150, 105, 570, 105);
        AddConnectorLine(150, 105, 150, 120);
        AddConnectorLine(570, 105, 570, 120);
        AddConnectorLine(150, 170, 150, 210);
        AddConnectorLine(570, 170, 570, 210);
        AddConnectorLine(360, 85, 360, 210);
        StatusMessage = "✓ Generated Matrix Divisional Org Chart";
    }

    [RelayCommand]
    public void GenerateHorizontalTreeTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 40, 170, 140, 60, colors[0 % colors.Length], "Root Strategy");
        AddShapeAt("roundrect", 240, 60, 150, 55, colors[1 % colors.Length], "Branch A · Growth");
        AddShapeAt("roundrect", 240, 170, 150, 55, colors[2 % colors.Length], "Branch B · Scale");
        AddShapeAt("roundrect", 240, 280, 150, 55, colors[3 % colors.Length], "Branch C · Risk");
        AddShapeAt("roundrect", 450, 35, 140, 45, colors[4 % colors.Length], "Deliverable 1");
        AddShapeAt("roundrect", 450, 95, 140, 45, colors[5 % colors.Length], "Deliverable 2");
        AddShapeAt("roundrect", 450, 175, 140, 45, colors[0 % colors.Length], "Deliverable 3");
        AddShapeAt("roundrect", 450, 285, 140, 45, colors[1 % colors.Length], "Deliverable 4");

        AddConnectorLine(180, 200, 210, 200);
        AddConnectorLine(210, 87, 210, 307);
        AddConnectorLine(210, 87, 240, 87);
        AddConnectorLine(210, 200, 240, 200);
        AddConnectorLine(210, 307, 240, 307);
        AddConnectorLine(390, 87, 450, 57);
        AddConnectorLine(390, 87, 450, 117);
        AddConnectorLine(390, 197, 450, 197);
        AddConnectorLine(390, 307, 450, 307);
        StatusMessage = "✓ Generated Horizontal Tree Hierarchy";
    }

    [RelayCommand]
    public void GenerateAgileSquadsTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 260, 30, 200, 55, colors[0 % colors.Length], "Tribe Lead &\nAgile Coach");
        AddShapeAt("roundrect", 40, 120, 190, 190, colors[1 % colors.Length], "SQUAD ALPHA\n\n• Tech Lead\n• 3 Engineers\n• 1 Designer");
        AddShapeAt("roundrect", 265, 120, 190, 190, colors[2 % colors.Length], "SQUAD BETA\n\n• Tech Lead\n• 4 Engineers\n• 1 QA Spec");
        AddShapeAt("roundrect", 490, 120, 190, 190, colors[3 % colors.Length], "SQUAD GAMMA\n\n• Tech Lead\n• 3 Engineers\n• 1 Data Analyst");
        AddShapeAt("chevron", 40, 330, 640, 45, colors[4 % colors.Length], "CROSS-SQUAD CHAPTER: Architecture, Security & Reliability Guild");
        StatusMessage = "✓ Generated Agile Squads & Chapters Model";
    }

    [RelayCommand]
    public void GenerateSwotMatrixTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 80, 50, 260, 160, colors[0 % colors.Length], "STRENGTHS\n• Speed & Agility\n• Modern Architecture\n• High Quality Standard");
        AddShapeAt("roundrect", 360, 50, 260, 160, colors[2 % colors.Length], "WEAKNESSES\n• New Market Presence\n• Resource Allocation\n• Global Brand Reach");
        AddShapeAt("roundrect", 80, 230, 260, 160, colors[1 % colors.Length], "OPPORTUNITIES\n• Enterprise Adoption\n• Document Automation\n• Open Ecosystem Scale");
        AddShapeAt("roundrect", 360, 230, 260, 160, colors[4 % colors.Length], "THREATS\n• Legacy Monoliths\n• Rapid Market Shifts\n• Direct Imitators");
        StatusMessage = "✓ Generated 2x2 SWOT Analysis Matrix";
    }

    [RelayCommand]
    public void GenerateEisenhowerMatrixTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 80, 50, 260, 160, colors[2 % colors.Length], "DO FIRST (Urgent & Important)\n• Critical Production Incidents\n• High-Impact Customer Deadlines");
        AddShapeAt("roundrect", 360, 50, 260, 160, colors[0 % colors.Length], "SCHEDULE (Not Urgent / Important)\n• Strategic Architecture\n• Team Upskilling & Culture");
        AddShapeAt("roundrect", 80, 230, 260, 160, colors[1 % colors.Length], "DELEGATE (Urgent / Not Important)\n• Routine Status Inquiries\n• Interruptive Meetings");
        AddShapeAt("roundrect", 360, 230, 260, 160, colors[5 % colors.Length], "ELIMINATE (Neither)\n• Vanity Tasks\n• Redundant Workflows");
        StatusMessage = "✓ Generated Eisenhower Priority Matrix";
    }

    [RelayCommand]
    public void GenerateBcgGrowthMatrixTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 80, 50, 260, 160, colors[0 % colors.Length], "STARS (High Growth / High Share)\n• Flagship Vector Engine\n• Fast-growing Core SaaS");
        AddShapeAt("roundrect", 360, 50, 260, 160, colors[3 % colors.Length], "QUESTION MARKS (High Growth / Low Share)\n• Experimental AI Features\n• New Marketplace Offerings");
        AddShapeAt("roundrect", 80, 230, 260, 160, colors[1 % colors.Length], "CASH COWS (Low Growth / High Share)\n• Enterprise Subscriptions\n• Foundation Licenses");
        AddShapeAt("roundrect", 360, 230, 260, 160, colors[4 % colors.Length], "DOGS (Low Growth / Low Share)\n• Legacy Importers\n• Deprecated Plugins");
        StatusMessage = "✓ Generated BCG Growth-Share Matrix";
    }

    [RelayCommand]
    public void GenerateRiskMatrixTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 70, 45, 175, 95, colors[3 % colors.Length], "MED RISK\nHigh Imp / Low Prob");
        AddShapeAt("roundrect", 265, 45, 175, 95, colors[2 % colors.Length], "HIGH RISK\nHigh Imp / Med Prob");
        AddShapeAt("roundrect", 460, 45, 175, 95, colors[2 % colors.Length], "CRITICAL RISK\nHigh Imp / High Prob");

        AddShapeAt("roundrect", 70, 155, 175, 95, colors[1 % colors.Length], "LOW RISK\nMed Imp / Low Prob");
        AddShapeAt("roundrect", 265, 155, 175, 95, colors[3 % colors.Length], "MED RISK\nMed Imp / Med Prob");
        AddShapeAt("roundrect", 460, 155, 175, 95, colors[2 % colors.Length], "HIGH RISK\nMed Imp / High Prob");

        AddShapeAt("roundrect", 70, 265, 175, 95, colors[1 % colors.Length], "NEGLIGIBLE\nLow Imp / Low Prob");
        AddShapeAt("roundrect", 265, 265, 175, 95, colors[1 % colors.Length], "LOW RISK\nLow Imp / Med Prob");
        AddShapeAt("roundrect", 460, 265, 175, 95, colors[3 % colors.Length], "MED RISK\nLow Imp / High Prob");
        StatusMessage = "✓ Generated Risk Likelihood & Impact 3x3 Matrix";
    }

    [RelayCommand]
    public void GenerateAnsoffMatrixTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 80, 50, 260, 160, colors[0 % colors.Length], "MARKET PENETRATION\nExisting Market / Existing Product\n• Increase market share\n• Boost user loyalty");
        AddShapeAt("roundrect", 360, 50, 260, 160, colors[1 % colors.Length], "PRODUCT DEVELOPMENT\nExisting Market / New Product\n• Launch adjacent toolkits\n• Add AI capabilities");
        AddShapeAt("roundrect", 80, 230, 260, 160, colors[2 % colors.Length], "MARKET DEVELOPMENT\nNew Market / Existing Product\n• Expand internationally\n• Enter education sector");
        AddShapeAt("roundrect", 360, 230, 260, 160, colors[4 % colors.Length], "DIVERSIFICATION\nNew Market / New Product\n• Novel industry platforms\n• Vertical solutions");
        StatusMessage = "✓ Generated Ansoff Market Strategy Matrix";
    }

    [RelayCommand]
    public void GenerateRaciGridTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 60, 40, 130, 50, colors[0 % colors.Length], "Task / Role");
        AddShapeAt("roundrect", 200, 40, 110, 50, colors[1 % colors.Length], "PM");
        AddShapeAt("roundrect", 320, 40, 110, 50, colors[2 % colors.Length], "Tech Lead");
        AddShapeAt("roundrect", 440, 40, 110, 50, colors[3 % colors.Length], "Engineer");
        AddShapeAt("roundrect", 560, 40, 110, 50, colors[4 % colors.Length], "Designer");

        AddShapeAt("roundrect", 60, 100, 130, 50, colors[5 % colors.Length], "1. Architecture");
        AddShapeAt("roundrect", 200, 100, 110, 50, colors[0 % colors.Length], "Consulted (C)");
        AddShapeAt("roundrect", 320, 100, 110, 50, colors[2 % colors.Length], "Accountable (A)");
        AddShapeAt("roundrect", 440, 100, 110, 50, colors[1 % colors.Length], "Responsible (R)");
        AddShapeAt("roundrect", 560, 100, 110, 50, colors[3 % colors.Length], "Informed (I)");

        AddShapeAt("roundrect", 60, 160, 130, 50, colors[5 % colors.Length], "2. Development");
        AddShapeAt("roundrect", 200, 160, 110, 50, colors[3 % colors.Length], "Informed (I)");
        AddShapeAt("roundrect", 320, 160, 110, 50, colors[2 % colors.Length], "Accountable (A)");
        AddShapeAt("roundrect", 440, 160, 110, 50, colors[1 % colors.Length], "Responsible (R)");
        AddShapeAt("roundrect", 560, 160, 110, 50, colors[0 % colors.Length], "Consulted (C)");

        AddShapeAt("roundrect", 60, 220, 130, 50, colors[5 % colors.Length], "3. Release");
        AddShapeAt("roundrect", 200, 220, 110, 50, colors[2 % colors.Length], "Accountable (A)");
        AddShapeAt("roundrect", 320, 220, 110, 50, colors[1 % colors.Length], "Responsible (R)");
        AddShapeAt("roundrect", 440, 220, 110, 50, colors[0 % colors.Length], "Consulted (C)");
        AddShapeAt("roundrect", 560, 220, 110, 50, colors[3 % colors.Length], "Informed (I)");
        StatusMessage = "✓ Generated RACI Accountability Grid";
    }

    /// <summary>A small chevron midway between two stages, rotated to point from
    /// <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal ShapeCanvasItemViewModel AddFlowChevron(ShapeCanvasItemViewModel from, ShapeCanvasItemViewModel to, string? fill, double size = 44)
    {
        double fx = from.X + from.Width / 2.0, fy = from.Y + from.Height / 2.0;
        double tx = to.X + to.Width / 2.0, ty = to.Y + to.Height / 2.0;
        var angle = (int)Math.Round(Math.Atan2(ty - fy, tx - fx) * 180.0 / Math.PI);
        if (angle < 0) angle += 360;
        double w = size, h = size * 0.6;
        return AddShapeAt("chevron", (fx + tx) / 2.0 - w / 2.0, (fy + ty) / 2.0 - h / 2.0, w, h, fill, rot: angle);
    }

    [RelayCommand]
    public void GenerateCycleTemplate()
    {
        // Stage -> stage chevrons, each turned to point at the next stage. These used to be four
        // identical circular-arrow icons, which read as "refresh" four times rather than as a
        // direction of travel around the loop.
        ClearAll();
        var colors = GetPaletteColors();
        var plan = AddShapeAt("roundrect", 270, 40, 180, 65, colors[0 % colors.Length], "1 · PLAN\nStrategy & Goals");
        var execute = AddShapeAt("roundrect", 480, 190, 180, 65, colors[1 % colors.Length], "2 · DO\nBuild & Execute");
        var check = AddShapeAt("roundrect", 270, 340, 180, 65, colors[2 % colors.Length], "3 · CHECK\nTest & Validate");
        var act = AddShapeAt("roundrect", 60, 190, 180, 65, colors[3 % colors.Length], "4 · ACT\nDeploy & Improve");

        AddFlowChevron(plan, execute, colors[0 % colors.Length]);
        AddFlowChevron(execute, check, colors[1 % colors.Length]);
        AddFlowChevron(check, act, colors[2 % colors.Length]);
        AddFlowChevron(act, plan, colors[3 % colors.Length]);
        StatusMessage = "✓ Generated PDCA Continuous Cycle";
    }

    [RelayCommand]
    public void GenerateBuildMeasureLearnTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        var build = AddShapeAt("roundrect", 270, 50, 180, 65, colors[0 % colors.Length], "1 · BUILD\nCode & Features");
        var measure = AddShapeAt("roundrect", 450, 250, 180, 65, colors[1 % colors.Length], "2 · MEASURE\nMetrics & Signals");
        var learn = AddShapeAt("roundrect", 90, 250, 180, 65, colors[2 % colors.Length], "3 · LEARN\nInsights & Pivot");

        AddFlowChevron(build, measure, colors[0 % colors.Length]);
        AddFlowChevron(measure, learn, colors[1 % colors.Length]);
        AddFlowChevron(learn, build, colors[2 % colors.Length]);
        StatusMessage = "✓ Generated Build-Measure-Learn Loop";
    }

    [RelayCommand]
    public void GenerateDesignThinkingTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // Interlocking hexagons stepping up and down, each wide enough for its two-line label.
        const double w = 140, h = 110, gap = 6;
        double dx = w - Math.Min(w, h) / 4 + gap;
        string[] phases = { "1 · EMPATHIZE\nUser needs", "2 · DEFINE\nProblem frame", "3 · IDEATE\nBrainstorm", "4 · PROTOTYPE\nMockups & code", "5 · TEST\nUser feedback" };
        for (int i = 0; i < phases.Length; i++)
        {
            AddShapeAt("hexagon", 30 + i * dx, i % 2 == 0 ? 120 : 120 + (h + gap) / 2, w, h, colors[i % colors.Length], phases[i]);
        }
        StatusMessage = "✓ Generated Design Thinking 5-Phase Cycle";
    }

    [RelayCommand]
    public void GenerateDevOpsLoopTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // A real figure-eight (lemniscate of Bernoulli) with the stages riding the curve: Dev loop
        // on the left, Ops on the right. The old layout was seven loose boxes with no loop at all.
        const double cx = 370, cy = 200, a = 260, stretch = 1.4;
        (double X, double Y) At(double t)
        {
            double d = 1 + Math.Sin(t) * Math.Sin(t);
            return (cx + a * Math.Cos(t) / d, cy + stretch * a * Math.Sin(t) * Math.Cos(t) / d);
        }
        var curve = Enumerable.Range(0, 97).Select(i => At(Math.PI / 2 + i * 2 * Math.PI / 96)).ToList();
        AddPolylinePath(curve, "8E9297", 4.0);
        AddShapeAt("ellipse", cx - a * 0.62 - 38, cy - 22, 76, 44, colors[5 % colors.Length], "DEV");
        AddShapeAt("ellipse", cx + a * 0.62 - 38, cy - 22, 76, 44, colors[4 % colors.Length], "OPS");
        string[] stages = { "1 · PLAN", "2 · CODE", "3 · BUILD", "4 · TEST", "5 · RELEASE", "6 · DEPLOY", "7 · OPERATE", "8 · MONITOR" };
        double[] turns = { 0.65, 0.88, 1.12, 1.35, 1.65, 1.88, 2.12, 2.35 };
        for (int i = 0; i < stages.Length; i++)
        {
            var (x, y) = At(turns[i] * Math.PI);
            AddShapeAt("roundrect", x - 54, y - 20, 108, 40, colors[i % 4], stages[i]);
        }
        StatusMessage = "✓ Generated DevOps Infinity Delivery Loop";
    }

    [RelayCommand]
    public void GenerateFeedbackSpiralTemplate()
    {
        ClearAll();
        AddRingsWithCallouts(220, 200, new[] { 170d, 112d, 56d }, 0.82, new[]
        {
            "OUTER LOOP\nQuarterly strategy review",
            "MIDDLE LOOP\nSprint retrospectives",
            "CORE INSIGHT\nImmediate customer signal",
        });
        StatusMessage = "✓ Generated Continuous Feedback Spiral";
    }

    [RelayCommand]
    public void GenerateTimelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("chevron", 40, 160, 180, 60, colors[0 % colors.Length], "PHASE 1\nDiscovery");
        AddShapeAt("chevron", 196, 160, 180, 60, colors[1 % colors.Length], "PHASE 2\nPrototype");
        AddShapeAt("chevron", 352, 160, 180, 60, colors[2 % colors.Length], "PHASE 3\nBeta Launch");
        AddShapeAt("chevron", 508, 160, 180, 60, colors[4 % colors.Length], "PHASE 4\nScale Out");
        StatusMessage = "✓ Generated 4-Phase Roadmap Timeline";
    }

    [RelayCommand]
    public void GeneratePipeline5StepTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("chevron", 30, 160, 150, 60, colors[0 % colors.Length], "STAGE 1\nIntake");
        AddShapeAt("chevron", 156, 160, 150, 60, colors[1 % colors.Length], "STAGE 2\nTriage");
        AddShapeAt("chevron", 282, 160, 150, 60, colors[2 % colors.Length], "STAGE 3\nDesign");
        AddShapeAt("chevron", 408, 160, 150, 60, colors[3 % colors.Length], "STAGE 4\nVerify");
        AddShapeAt("chevron", 534, 160, 150, 60, colors[4 % colors.Length], "STAGE 5\nDeliver");
        StatusMessage = "✓ Generated 5-Stage Pipeline Process";
    }

    [RelayCommand]
    public void GenerateAlternatingProcessTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 60, 60, 160, 90, colors[0 % colors.Length], "STEP 1 · INITIATE\nRequirements &\nScope Definition");
        AddShapeAt("roundrect", 240, 230, 160, 90, colors[1 % colors.Length], "STEP 2 · DEVELOP\nSprint Cycles &\nContinuous Tests");
        AddShapeAt("roundrect", 420, 60, 160, 90, colors[2 % colors.Length], "STEP 3 · VALIDATE\nStaging Validation &\nSecurity Audits");
        AddShapeAt("roundrect", 520, 230, 160, 90, colors[3 % colors.Length], "STEP 4 · DEPLOY\nProduction Launch &\nMonitoring");

        AddConnectorLine(142, 155, 142, 235, "777777", 2.0);
        AddConnectorLine(322, 155, 322, 235, "777777", 2.0);
        AddConnectorLine(502, 155, 502, 235, "777777", 2.0);
        StatusMessage = "✓ Generated Alternating Stepped Workflow";
    }

    [RelayCommand]
    public void GenerateStageGateProcessTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // Phases and gates joined by connectors along one centre line; the gates are large enough
        // for their label to fit the diamond's text area on one line.
        const double cy = 187.5;
        AddConnectorLine(160, cy, 590, cy, "8E9297", 2.0);
        AddShapeAt("roundrect", 30, cy - 37.5, 130, 75, colors[0 % colors.Length], "Phase 1\nConcept");
        AddShapeAt("diamond", 180, cy - 50, 110, 100, colors[2 % colors.Length], "Gate 1");
        AddShapeAt("roundrect", 310, cy - 37.5, 130, 75, colors[1 % colors.Length], "Phase 2\nBuild");
        AddShapeAt("diamond", 460, cy - 50, 110, 100, colors[2 % colors.Length], "Gate 2");
        AddShapeAt("roundrect", 590, cy - 37.5, 130, 75, colors[4 % colors.Length], "Phase 3\nLaunch");
        StatusMessage = "✓ Generated Stage-Gate Decision Process";
    }

    [RelayCommand]
    public void GenerateRecruitmentPipelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // A funnel has to narrow: each stage is inset so the taper carries the meaning even
        // before the reader gets to the counts.
        string[] stages = { "Applied · 480", "Screened · 210", "Interviewed · 84", "Onsite · 26", "Offer · 9", "Hired · 6" };
        for (int i = 0; i < stages.Length; i++)
        {
            double inset = i * 44;
            AddShapeAt("trapezoid", 60 + inset, 40 + i * 52, 600 - inset * 2, 44,
                colors[i % colors.Length], stages[i]);
        }
        StatusMessage = "✓ Generated Recruitment Pipeline";
    }

    [RelayCommand]
    public void GenerateNowNextLaterTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        string[] columns = { "NOW", "NEXT", "LATER" };
        string[][] cards =
        {
            new[] { "Checkout rewrite", "SSO rollout", "Latency budget" },
            new[] { "Usage analytics", "Bulk import", "Audit log" },
            new[] { "Mobile client", "Partner API", "Offline mode" },
        };
        for (int c = 0; c < columns.Length; c++)
        {
            double x = 50 + c * 220;
            AddShapeAt("roundrect", x, 30, 190, 44, colors[c % colors.Length], columns[c]);
            for (int r = 0; r < cards[c].Length; r++)
            {
                AddShapeAt("roundrect", x, 90 + r * 62, 190, 50, "FFFFFF", cards[c][r]);
            }
        }
        StatusMessage = "✓ Generated Now / Next / Later board";
    }

    [RelayCommand]
    public void GenerateOodaLoopTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // Four nodes on a diamond, closed into a loop with real stroked connectors.
        AddShapeAt("ellipse", 280, 30, 150, 90, colors[0 % colors.Length], "OBSERVE");
        AddShapeAt("ellipse", 470, 170, 150, 90, colors[1 % colors.Length], "ORIENT");
        AddShapeAt("ellipse", 280, 310, 150, 90, colors[2 % colors.Length], "DECIDE");
        AddShapeAt("ellipse", 90, 170, 150, 90, colors[3 % colors.Length], "ACT");

        AddConnectorLine(430, 90, 480, 180);
        AddConnectorLine(545, 260, 420, 330);
        AddConnectorLine(280, 350, 180, 260);
        AddConnectorLine(165, 170, 285, 95);
        StatusMessage = "✓ Generated OODA decision loop";
    }

    [RelayCommand]
    public void GeneratePortersFiveForcesTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 265, 175, 190, 90, colors[0 % colors.Length], "COMPETITIVE RIVALRY");
        AddShapeAt("roundrect", 265, 30, 190, 75, colors[1 % colors.Length], "New Entrants");
        AddShapeAt("roundrect", 265, 330, 190, 75, colors[2 % colors.Length], "Substitutes");
        AddShapeAt("roundrect", 30, 175, 190, 90, colors[3 % colors.Length], "Supplier Power");
        AddShapeAt("roundrect", 500, 175, 190, 90, colors[4 % colors.Length], "Buyer Power");

        AddConnectorLine(360, 105, 360, 175);
        AddConnectorLine(360, 265, 360, 330);
        AddConnectorLine(220, 220, 265, 220);
        AddConnectorLine(455, 220, 500, 220);
        StatusMessage = "✓ Generated Porter's Five Forces";
    }

    [RelayCommand]
    public void GenerateIncidentResponseTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 30, 150, 140, 70, colors[0 % colors.Length], "Detect · alert fires");
        AddShapeAt("roundrect", 200, 150, 140, 70, colors[1 % colors.Length], "Triage · severity call");
        AddShapeAt("diamond", 370, 140, 110, 90, colors[2 % colors.Length], "SEV 1?");
        AddShapeAt("roundrect", 520, 40, 170, 70, colors[3 % colors.Length], "Page on-call, open bridge");
        AddShapeAt("roundrect", 520, 260, 170, 70, colors[4 % colors.Length], "Queue for next day");
        AddShapeAt("roundrect", 200, 330, 280, 70, colors[5 % colors.Length], "Post-incident review");

        AddConnectorLine(170, 185, 200, 185);
        AddConnectorLine(340, 185, 370, 185);
        AddConnectorLine(480, 165, 520, 90);
        AddConnectorLine(480, 205, 520, 285);
        AddConnectorLine(605, 110, 605, 250);
        AddConnectorLine(520, 295, 480, 365);
        StatusMessage = "✓ Generated Incident Response flow";
    }

    [RelayCommand]
    public void GenerateMilestoneTimelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // A real stroked connector, not a "line" prst laid out as a box: prstGeom "line"
        // draws corner-to-corner, so a 640x8 box came out as a skewed filled slab.
        AddConnectorLine(40, 184, 680, 184, "777777", 3.0);
        AddShapeAt("roundrect", 50, 80, 130, 75, colors[0 % colors.Length], "Q1 2026\nKernel Overhaul");
        AddShapeAt("roundrect", 210, 210, 130, 75, colors[1 % colors.Length], "Q2 2026\nVector Studio");
        AddShapeAt("roundrect", 370, 80, 130, 75, colors[2 % colors.Length], "Q3 2026\nWord Interop");
        AddShapeAt("roundrect", 530, 210, 130, 75, colors[3 % colors.Length], "Q4 2026\nEnterprise GA");
        StatusMessage = "✓ Generated Linear Milestone Timeline";
    }

    [RelayCommand]
    public void GenerateSwimlaneWorkflowTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // Each lane: a solid header with its name, a tinted body, and its step card in the lane colour.
        // The lane names used to sit in the middle of the lane, under the white step cards.
        string[] lanes = { "PRODUCT\nMANAGEMENT", "ENGINEERING\n& QA", "OPERATIONS\n& SECURITY" };
        string[] steps = { "User Stories", "Build & Test", "Release & Monitor" };
        double[] cardX = { 220, 380, 540 };
        for (int i = 0; i < 3; i++)
        {
            double y = 40 + i * 100;
            AddShapeAt("rect", 40, y, 150, 90, colors[i % colors.Length], lanes[i]);
            AddShapeAt("rect", 190, y, 520, 90, Tint(colors[i % colors.Length], LaneTint));
        }
        for (int i = 0; i < 2; i++)
        {
            // Elbow from the bottom of one step to the side of the next.
            double fromX = cardX[i] + 70, fromY = 40 + i * 100 + 73, toY = 40 + (i + 1) * 100 + 45;
            AddPolylinePath(new[] { (fromX, fromY), (fromX, toY), (cardX[i + 1], toY) }, "595959", 2.0);
        }
        for (int i = 0; i < 3; i++)
        {
            AddShapeAt("roundrect", cardX[i], 40 + i * 100 + 17, 140, 56, colors[i % colors.Length], steps[i]);
        }
        StatusMessage = "✓ Generated Swimlane Workflow";
    }

    [RelayCommand]
    public void GenerateVennTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("ellipse", 150, 60, 230, 230, colors[0 % colors.Length], "Desirability\n(User Needs)");
        AddShapeAt("ellipse", 320, 60, 230, 230, colors[1 % colors.Length], "Feasibility\n(Technology)");
        AddShapeAt("ellipse", 235, 190, 230, 230, colors[2 % colors.Length], "Viability\n(Business)");
        StatusMessage = "✓ Generated 3-Set Venn Diagram";
    }

    [RelayCommand]
    public void GenerateVenn2SetTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // The overlap is narrow enough that each set's label stays clear of the other circle, and the
        // overlap is called out below the lens. A box laid over the middle hid both set labels.
        AddShapeAt("ellipse", 100, 50, 260, 260, colors[0 % colors.Length], "STRATEGY\nMarket reach &\npositioning");
        AddShapeAt("ellipse", 300, 50, 260, 260, colors[1 % colors.Length], "EXECUTION\nDelivery speed &\nquality");
        AddConnectorLine(330, 230, 330, 345, "8E9297", 1.5);
        AddShapeAt("roundrect", 245, 345, 170, 52, colors[2 % colors.Length], "CORE OVERLAP\nCompetitive moat");
        StatusMessage = "✓ Generated 2-Set Core Overlap Venn";
    }

    [RelayCommand]
    public void GenerateBullseyeTargetTemplate()
    {
        ClearAll();
        AddRingsWithCallouts(220, 200, new[] { 170d, 112d, 56d }, 1.0, new[]
        {
            "OUTER RING\nLong-term frontier vision",
            "MIDDLE RING\nMid-term growth drivers",
            "BULLSEYE\nCore focus",
        });
        StatusMessage = "✓ Generated Concentric Bullseye Strategy Target";
    }

    [RelayCommand]
    public void GenerateHubAndSpokeTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("ellipse", 260, 140, 180, 120, colors[0 % colors.Length], "CENTRAL PLATFORM\nCore API Engine");

        AddShapeAt("roundrect", 60, 30, 150, 55, colors[1 % colors.Length], "Client App");
        AddShapeAt("roundrect", 490, 30, 150, 55, colors[2 % colors.Length], "Web Portal");
        AddShapeAt("roundrect", 40, 172, 150, 55, colors[3 % colors.Length], "Auth Service");
        AddShapeAt("roundrect", 510, 172, 150, 55, colors[4 % colors.Length], "Reporting Engine");
        AddShapeAt("roundrect", 60, 315, 150, 55, colors[5 % colors.Length], "Data Warehouse");
        AddShapeAt("roundrect", 490, 315, 150, 55, colors[0 % colors.Length], "Integrations");

        AddConnectorLine(210, 57, 280, 150);
        AddConnectorLine(490, 57, 420, 150);
        AddConnectorLine(190, 200, 260, 200);
        AddConnectorLine(440, 200, 510, 200);
        AddConnectorLine(210, 342, 280, 250);
        AddConnectorLine(490, 342, 420, 250);
        StatusMessage = "✓ Generated Hub & Spoke Ecosystem";
    }

    [RelayCommand]
    public void GenerateOnionSecurityTemplate()
    {
        ClearAll();
        AddRingsWithCallouts(250, 200, new[] { 200d, 150d, 100d, 50d }, 0.78, new[]
        {
            "LAYER 1 · PERIMETER\nFirewalls & cloud WAF",
            "LAYER 2 · NETWORK\nZero-trust access",
            "LAYER 3 · IDENTITY\nAccess management",
            "LAYER 4 · DATA\nEncrypted at rest",
        });
        StatusMessage = "✓ Generated Onion Layer Security Model";
    }

    [RelayCommand]
    public void GenerateQuarterlyRoadmapTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 40, 40, 150, 320, colors[0 % colors.Length], "Q1 2026\nFOUNDATION\n\n• Core Pipeline\n• Unit Tests\n• Dark Theme\n• Settings UI");
        AddShapeAt("roundrect", 205, 40, 150, 320, colors[1 % colors.Length], "Q2 2026\nDRAWINGML\n\n• Vector Studio\n• SmartArt Glox\n• Line Tracer\n• SVG Exporter");
        AddShapeAt("roundrect", 370, 40, 150, 320, colors[2 % colors.Length], "Q3 2026\nINTEROP\n\n• Word Templates\n• PDF Generator\n• Teams Sharing\n• Extension Sync");
        AddShapeAt("roundrect", 535, 40, 150, 320, colors[3 % colors.Length], "Q4 2026\nSCALE & AI\n\n• Realtime Collab\n• Cloud Backup\n• AI Copilot\n• Enterprise GA");
        StatusMessage = "✓ Generated Quarterly Horizon Roadmap (Q1-Q4)";
    }

    [RelayCommand]
    public void GenerateChevronGanttTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("chevron", 40, 50, 260, 55, colors[0 % colors.Length], "Phase 1 · Research (Jan-Feb)");
        AddShapeAt("chevron", 180, 125, 280, 55, colors[1 % colors.Length], "Phase 2 · Prototyping (Feb-Apr)");
        AddShapeAt("chevron", 320, 200, 260, 55, colors[2 % colors.Length], "Phase 3 · Alpha Test (Apr-May)");
        AddShapeAt("chevron", 440, 275, 240, 55, colors[3 % colors.Length], "Phase 4 · GA Release (Jun)");
        StatusMessage = "✓ Generated Chevron Gantt Roadmap";
    }

    [RelayCommand]
    public void GenerateAlternatingTimelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddConnectorLine(350, 30, 350, 370, "8E9297", 3.0);
        AddShapeAt("roundrect", 80, 40, 220, 60, colors[0 % colors.Length], "2024 · Conception\nInitial architecture & design");
        AddConnectorLine(300, 70, 350, 70);
        AddShapeAt("roundrect", 400, 120, 220, 60, colors[1 % colors.Length], "2025 · MVP Release\nCore parsing & markdown preview");
        AddConnectorLine(350, 150, 400, 150);
        AddShapeAt("roundrect", 80, 200, 220, 60, colors[2 % colors.Length], "2026 · Vector Studio\nDrawingML & SmartArt engine");
        AddConnectorLine(300, 230, 350, 230);
        AddShapeAt("roundrect", 400, 280, 220, 60, colors[3 % colors.Length], "2027 · Enterprise GA\nGlobal ecosystem expansion");
        AddConnectorLine(350, 310, 400, 310);
        StatusMessage = "✓ Generated Alternating Milestone Timeline";
    }

    [RelayCommand]
    public void GenerateCustomerJourneyTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 30, 80, 120, 240, colors[0 % colors.Length], "1. AWARENESS\n\n• SEO Search\n• Social Proof\n• Tech Blogs\n\nGoal: Discovery");
        AddShapeAt("roundrect", 160, 80, 120, 240, colors[1 % colors.Length], "2. CONSIDER\n\n• Feature Tour\n• Live Demos\n• Docs Review\n\nGoal: Trial");
        AddShapeAt("roundrect", 290, 80, 120, 240, colors[2 % colors.Length], "3. PURCHASE\n\n• Self-serve\n• Enterprise PoC\n• Onboarding\n\nGoal: Convert");
        AddShapeAt("roundrect", 420, 80, 120, 240, colors[3 % colors.Length], "4. RETENTION\n\n• Daily Workflow\n• Speed & Power\n• Support Help\n\nGoal: Adoption");
        AddShapeAt("roundrect", 550, 80, 120, 240, colors[4 % colors.Length], "5. ADVOCACY\n\n• Team Invites\n• Public Shares\n• Community\n\nGoal: Champion");
        StatusMessage = "✓ Generated Customer Journey Map (5 Touchpoints)";
    }

    [RelayCommand]
    public void GeneratePillarsTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("trapezoid", 60, 40, 580, 65, colors[0 % colors.Length], "ENTERPRISE ARCHITECTURE");
        AddShapeAt("cylinder", 90, 120, 130, 190, colors[1 % colors.Length], "Security &\nGovernance");
        AddShapeAt("cylinder", 285, 120, 130, 190, colors[2 % colors.Length], "Performance\nEngine");
        AddShapeAt("cylinder", 480, 120, 130, 190, colors[3 % colors.Length], "Native Word\nExport");
        AddShapeAt("rect", 60, 325, 580, 60, colors[4 % colors.Length], "CORE PLATFORM FOUNDATION");
        StatusMessage = "✓ Generated Architecture Pillars Template";
    }

    [RelayCommand]
    public void GenerateCloudStack3TierTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 80, 40, 540, 85, colors[0 % colors.Length], "PRESENTATION TIER\n• WinUI 3 Desktop App · Next.js Web App · VS Code Extension");
        AddShapeAt("roundrect", 80, 145, 540, 85, colors[1 % colors.Length], "APPLICATION & LOGIC TIER\n• .NET 8 Core Engine · Markdig Pipeline · DrawingML Generator");
        AddShapeAt("roundrect", 80, 250, 540, 85, colors[2 % colors.Length], "DATA & STORAGE TIER\n• OpenXML Package · SQLite Local DB · Cloud Storage Syncer");
        StatusMessage = "✓ Generated 3-Tier Cloud Stack";
    }

    [RelayCommand]
    public void GenerateHexagonalArchitectureTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("hexagon", 250, 110, 200, 180, colors[0 % colors.Length], "CORE DOMAIN\n\nBusiness Logic &\nDomain Models");
        AddShapeAt("roundrect", 40, 80, 160, 65, colors[1 % colors.Length], "Driving Adapter\nREST API / UI");
        AddShapeAt("roundrect", 40, 240, 160, 65, colors[2 % colors.Length], "Driving Adapter\nCLI & Automation");
        AddShapeAt("roundrect", 500, 80, 160, 65, colors[3 % colors.Length], "Driven Adapter\nDocx OpenXml");
        AddShapeAt("roundrect", 500, 240, 160, 65, colors[4 % colors.Length], "Driven Adapter\nDatabase / Cache");

        AddConnectorLine(200, 112, 270, 150);
        AddConnectorLine(200, 272, 270, 250);
        AddConnectorLine(430, 150, 500, 112);
        AddConnectorLine(430, 250, 500, 272);
        StatusMessage = "✓ Generated Hexagonal Ports & Adapters Architecture";
    }

    [RelayCommand]
    public void GenerateMicroservicesBusTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("rect", 60, 175, 580, 45, colors[0 % colors.Length], "EVENT STREAM & MESSAGE BUS (Kafka / EventHub)");
        AddShapeAt("roundrect", 60, 55, 130, 80, colors[1 % colors.Length], "Auth Service\n(OAuth2 / OIDC)");
        AddShapeAt("roundrect", 210, 55, 130, 80, colors[2 % colors.Length], "Doc Service\n(Markdown AST)");
        AddShapeAt("roundrect", 360, 55, 130, 80, colors[3 % colors.Length], "Export Engine\n(OpenXML/PDF)");
        AddShapeAt("roundrect", 510, 55, 130, 80, colors[4 % colors.Length], "Sync Service\n(WebSockets)");

        AddShapeAt("roundrect", 130, 260, 180, 75, colors[5 % colors.Length], "Analytics Pipeline\n(ClickHouse / DuckDB)");
        AddShapeAt("roundrect", 390, 260, 180, 75, colors[0 % colors.Length], "Notification Hub\n(Push / Email / Webhook)");
        StatusMessage = "✓ Generated Microservices Event-Driven Bus";
    }

    [RelayCommand]
    public void GenerateDataEtlPipelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("chevron", 30, 162, 154, 56, colors[0 % colors.Length], "1. EXTRACT\nRaw Sources");
        AddShapeAt("chevron", 162, 162, 154, 56, colors[1 % colors.Length], "2. INGEST\nKafka Queue");
        AddShapeAt("chevron", 294, 162, 154, 56, colors[2 % colors.Length], "3. TRANSFORM\nSpark Engine");
        AddShapeAt("chevron", 426, 162, 154, 56, colors[3 % colors.Length], "4. STORE\nIceberg Lake");
        AddShapeAt("chevron", 558, 162, 154, 56, colors[4 % colors.Length], "5. SERVE\nBI Dashboard");
        StatusMessage = "✓ Generated Data Engineering ETL Pipeline";
    }

    [RelayCommand]
    public void GenerateFunnelTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("trapezoid", 80, 45, 540, 75, colors[0 % colors.Length], "AWARENESS · 10,000 Visitors", 180);
        AddShapeAt("trapezoid", 135, 120, 430, 75, colors[1 % colors.Length], "INTEREST · 4,500 Engaged", 180);
        AddShapeAt("trapezoid", 190, 195, 320, 75, colors[2 % colors.Length], "DECISION · 1,200 Trials", 180);
        AddShapeAt("trapezoid", 245, 270, 210, 75, colors[3 % colors.Length], "ACTION · 600 Customers", 180);
        StatusMessage = "✓ Generated Marketing Acquisition Funnel";
    }

    [RelayCommand]
    public void GenerateSalesPipelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("trapezoid", 60, 40, 580, 60, colors[0 % colors.Length], "1. PROSPECTING · 500 Accounts");
        AddShapeAt("trapezoid", 110, 110, 480, 60, colors[1 % colors.Length], "2. QUALIFIED · 220 Meetings");
        AddShapeAt("trapezoid", 160, 180, 380, 60, colors[2 % colors.Length], "3. PROPOSAL · 90 Demos");
        AddShapeAt("trapezoid", 210, 250, 280, 60, colors[3 % colors.Length], "4. NEGOTIATION · 45 Contracts");
        AddShapeAt("trapezoid", 260, 320, 180, 60, colors[4 % colors.Length], "5. CLOSED WON · 32 Customers");
        StatusMessage = "✓ Generated Enterprise Sales Pipeline (5-Stage)";
    }

    [RelayCommand]
    public void GenerateHourglassFunnelTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // Narrows to a retention neck, then widens again. The neck used to be a small triangle with
        // a label three times its width.
        AddShapeAt("trapezoid", 100, 40, 500, 60, colors[0 % colors.Length], "ACQUISITION · Inbound leads", 180);
        AddShapeAt("trapezoid", 170, 106, 360, 60, colors[1 % colors.Length], "ACTIVATION · Onboarded users", 180);
        AddShapeAt("roundrect", 235, 172, 230, 50, colors[2 % colors.Length], "RETENTION · Core power users");
        AddShapeAt("trapezoid", 170, 228, 360, 60, colors[3 % colors.Length], "REFERRALS · Word of mouth");
        AddShapeAt("trapezoid", 100, 294, 500, 60, colors[4 % colors.Length], "EXPANSION · Enterprise champions");
        StatusMessage = "✓ Generated Hourglass Growth Funnel";
    }

    [RelayCommand]
    public void GenerateValuePropCardsTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 60, 50, 175, 290, colors[0 % colors.Length], "FAST & LIGHT\n\n⚡ Instant startup\n⚡ SAX OpenXML streaming\n⚡ 0 latency preview\n⚡ Native WPF rendering");
        AddShapeAt("roundrect", 262, 50, 175, 290, colors[1 % colors.Length], "100% NATIVE WORD\n\n📄 Native DrawingML\n📄 SmartArt layout catalog\n📄 Word-safe vector shapes\n📄 No raster blur");
        AddShapeAt("roundrect", 465, 50, 175, 290, colors[2 % colors.Length], "EXTENSIBLE\n\n🛠️ Vector Design Studio\n🛠️ Image line-art tracer\n🛠️ Shape mosaic composer\n🛠️ Hybrid vector fusion");
        StatusMessage = "✓ Generated 3-Column Value Proposition Cards";
    }

    [RelayCommand]
    public void GenerateExecutiveKpiDashboardTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 60, 50, 270, 130, colors[0 % colors.Length], "ANNUAL RECURRING REVENUE\n\n$14.2M\n▲ +42% Year-over-Year Growth");
        AddShapeAt("roundrect", 370, 50, 270, 130, colors[1 % colors.Length], "ACTIVE DEVELOPERS\n\n128,400\n▲ +85% Community Expansion");
        AddShapeAt("roundrect", 60, 200, 270, 130, colors[2 % colors.Length], "NET PROMOTER SCORE\n\n+78 NPS\n★ Industry-leading satisfaction");
        AddShapeAt("roundrect", 370, 200, 270, 130, colors[4 % colors.Length], "SYSTEM RELIABILITY\n\n99.99%\n✓ Zero Sev-1 Outages in 2026");
        StatusMessage = "✓ Generated 4-Card Executive KPI Dashboard";
    }

    [RelayCommand]
    public void GenerateHoneycombMatrixTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        // A true flat-top hexagon tiling: neighbours sit (w - x1 + gap) across and half a cell up or
        // down, so the cells interlock with an even gutter instead of overlapping one another.
        const double w = 140, h = 120, gap = 6, cx = 300, cy = 160;
        double dx = w - Math.Min(w, h) / 4 + gap, dy = h + gap;
        (double X, double Y, string Label)[] cells =
        {
            (0, 0, "CORE\nENGINE"),
            (0, -dy, "Security"),
            (dx, -dy / 2, "Speed"),
            (dx, dy / 2, "Vector"),
            (0, dy, "Export"),
            (-dx, dy / 2, "SmartArt"),
            (-dx, -dy / 2, "InterOp"),
        };
        for (int i = 0; i < cells.Length; i++)
        {
            AddShapeAt("hexagon", cx + cells[i].X, cy + cells[i].Y, w, h, colors[i % colors.Length], cells[i].Label);
        }
        StatusMessage = "✓ Generated Hexagonal Honeycomb Matrix";
    }

    [RelayCommand]
    public void GenerateFeatureComparisonTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 60, 50, 175, 290, colors[0 % colors.Length], "STARTER\nFree Forever\n\n✓ Standard Markdown\n✓ Live HTML Preview\n✓ Basic Themes\n✓ Community Support");
        AddShapeAt("roundrect", 262, 50, 175, 290, colors[1 % colors.Length], "PROFESSIONAL\n$19 / month\n\n✓ Native OpenXml DOCX\n✓ Vector Shape Studio\n✓ 40+ SmartArt Presets\n✓ Line-Art Image Tracer");
        AddShapeAt("roundrect", 465, 50, 175, 290, colors[2 % colors.Length], "ENTERPRISE\nCustom Scale\n\n✓ All Pro Features\n✓ Custom GLOX Styles\n✓ Dedicated SLA Support\n✓ Volume Licensing");
        StatusMessage = "✓ Generated Tier Comparison Matrix";
    }

    // =========================================================================
    // Alignment & Distribution Tools
    // =========================================================================

    // Align / distribute act on the SELECTION (Ctrl+click or Ctrl+A), never the whole canvas.

    private bool BeginArrange(int minimum, out List<ShapeCanvasItemViewModel> targets)
    {
        targets = SelectedItems.ToList();
        if (targets.Count < minimum) return false;
        RecordUndo();
        return true;
    }

    private void EndArrange(string what, int count)
    {
        StatusMessage = $"{what} {count} shapes";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanAlign))]
    public void AlignLeft()
    {
        if (!BeginArrange(2, out var t)) return;
        double minX = t.Min(s => s.X);
        foreach (var s in t) s.X = minX;
        EndArrange("Aligned left edges of", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanAlign))]
    public void AlignCenter()
    {
        if (!BeginArrange(2, out var t)) return;
        double avgCenter = t.Average(s => s.X + s.Width / 2.0);
        foreach (var s in t) s.X = Math.Max(0, avgCenter - s.Width / 2.0);
        EndArrange("Centred", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanAlign))]
    public void AlignRight()
    {
        if (!BeginArrange(2, out var t)) return;
        double maxRight = t.Max(s => s.X + s.Width);
        foreach (var s in t) s.X = Math.Max(0, maxRight - s.Width);
        EndArrange("Aligned right edges of", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanAlign))]
    public void AlignTop()
    {
        if (!BeginArrange(2, out var t)) return;
        double minY = t.Min(s => s.Y);
        foreach (var s in t) s.Y = minY;
        EndArrange("Aligned top edges of", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanAlign))]
    public void AlignMiddle()
    {
        if (!BeginArrange(2, out var t)) return;
        double avgMiddle = t.Average(s => s.Y + s.Height / 2.0);
        foreach (var s in t) s.Y = Math.Max(0, avgMiddle - s.Height / 2.0);
        EndArrange("Middle-aligned", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanAlign))]
    public void AlignBottom()
    {
        if (!BeginArrange(2, out var t)) return;
        double maxBottom = t.Max(s => s.Y + s.Height);
        foreach (var s in t) s.Y = Math.Max(0, maxBottom - s.Height);
        EndArrange("Aligned bottom edges of", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanDistribute))]
    public void DistributeHorizontal()
    {
        if (!BeginArrange(3, out var t)) return;
        var ordered = t.OrderBy(s => s.X).ToList();
        double start = ordered[0].X;
        double end = ordered[^1].X;
        double step = (end - start) / (ordered.Count - 1);
        for (int i = 0; i < ordered.Count; i++) ordered[i].X = start + i * step;
        EndArrange("Spaced out horizontally:", t.Count);
    }

    [RelayCommand(CanExecute = nameof(CanDistribute))]
    public void DistributeVertical()
    {
        if (!BeginArrange(3, out var t)) return;
        var ordered = t.OrderBy(s => s.Y).ToList();
        double start = ordered[0].Y;
        double end = ordered[^1].Y;
        double step = (end - start) / (ordered.Count - 1);
        for (int i = 0; i < ordered.Count; i++) ordered[i].Y = start + i * step;
        EndArrange("Spaced out vertically:", t.Count);
    }

    private CancellationTokenSource? _generationCts;
    private CancellationToken NextGenerationToken()
    {
        _generationCts?.Cancel();
        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        return _generationCts.Token;
    }

    /// <summary>Trace a picture into dense line items (the MLShape workflow). Heavy work runs on
    /// the thread pool; the collection is replaced wholesale so a 16k-row trace doesn't fire tens
    /// of thousands of per-item change notifications on the UI thread.</summary>
    public async System.Threading.Tasks.Task TraceImageAsync(string imagePath)
    {
        var ct = NextGenerationToken();
        try
        {
            var mode = TraceModeIndex switch
            {
                1 => LineTraceMode.Edges,
                2 => LineTraceMode.Silhouette,
                3 => LineTraceMode.Scanlines,
                _ => LineTraceMode.Engraved
            };
            var opt = new LineTraceOptions
            {
                Rows = Math.Clamp((int)Math.Round(TraceDensity), ImageLineTracer.MinRows, ImageLineTracer.MaxRows),
                Mode = mode,
                UseColor = !TraceMonochrome
            };
            StatusMessage = $"Tracing {Path.GetFileName(imagePath)}…";

            var traced = await System.Threading.Tasks.Task.Run(() => ImageLineTracer.TraceLines(imagePath, opt), ct);
            if (ct.IsCancellationRequested) return;

            RecordUndo();
            Shapes = new ObservableCollection<ShapeCanvasItemViewModel>(traced.Select(ToItem));
            SelectedShape = null;

            byte[]? png = null;
            if (traced.Count > 0)
            {
                var (w, h) = MarkSmith.Core.Composer.ShapeMarkdownCodec.CanvasSize(traced);
                var snapshot = traced;
                png = await System.Threading.Tasks.Task.Run(
                    () => ImageLineTracer.RenderPreviewPng(snapshot, w, h, previewCap: 24000), ct);
            }
            if (ct.IsCancellationRequested) return;

            PreviewPng = png;
            CanvasMode = traced.Count == 0 ? "empty" : "dense";
            StatusMessage = $"✓ Traced {traced.Count:N0} lines from {Path.GetFileName(imagePath)}";
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Trace error: {ex.Message}";
        }
    }

    /// <summary>Load a :::shapes markdown block. Parsing runs on the thread pool and the
    /// collection is replaced wholesale (one change notification, not one per shape).</summary>
    public async System.Threading.Tasks.Task LoadMarkdownAsync(string markdownBlock)
    {
        try
        {
            var parsed = await System.Threading.Tasks.Task.Run(
                () => MarkSmith.Core.Composer.ShapeMarkdownCodec.Parse(markdownBlock));
            if (parsed.Count == 0)
            {
                StatusMessage = "No shapes found in the markdown (need a :::shapes block).";
                return;
            }

            RecordUndo();
            Shapes = new ObservableCollection<ShapeCanvasItemViewModel>(parsed.Select(ToItem));
            SelectedShape = null;
            StatusMessage = $"Loaded {parsed.Count} shapes from markdown.";
            MarkKept();
            await RefreshCanvasModeAsync();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load error: {ex.Message}";
        }
    }

    public async System.Threading.Tasks.Task ComposeSketchImageAsync(string imagePath, int grid)
    {
        try
        {
            StatusMessage = $"Tracing {Path.GetFileName(imagePath)}…";
            var composed = await System.Threading.Tasks.Task.Run(
                () => ImageLineTracer.TraceLines(imagePath, new LineTraceOptions { Rows = grid, Mode = LineTraceMode.TopographicWaves }));
            ClearAll();
            AppendComposed(composed);
            StatusMessage = $"Trace: {composed.Count:N0} vector strokes onto the canvas from {Path.GetFileName(imagePath)}";
            await RefreshCanvasModeAsync();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Trace error: {ex.Message}";
        }
    }

    public async System.Threading.Tasks.Task ComposeImageAsync(string imagePath, int grid, IReadOnlyList<string> shapes)
    {
        try
        {
            StatusMessage = $"Composing {Path.GetFileName(imagePath)}…";
            var composed = await System.Threading.Tasks.Task.Run(
                () => ImageShapeComposer.Compose(imagePath, new ShapeComposerOptions
                {
                    Grid = grid,
                    Shapes = shapes.Any() ? shapes.ToList() : new List<string> { "ellipse" },
                    InsetInches = 0.0
                }));
            ClearAll();
            AppendComposed(composed);
            StatusMessage = $"Composed {composed.Count} shapes onto the canvas from {Path.GetFileName(imagePath)}";
            await RefreshCanvasModeAsync();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compose error: {ex.Message}";
        }
    }

    public async System.Threading.Tasks.Task ComposeHybridFusionAsync(
        string imagePath,
        int mosaicGrid,
        int lineDensity,
        IReadOnlyList<string> shapes,
        LineTraceMode lineMode = LineTraceMode.Edges,
        bool monochromeLines = true,
        int edgeThreshold = 30,
        double maxThicknessPt = 1.8)
    {
        var ct = NextGenerationToken();
        try
        {
            if (mosaicGrid <= 0 && lineDensity <= 0)
            {
                StatusMessage = "Set Shape Density or Line Density above 0.";
                return;
            }

            StatusMessage = $"⚡ Vector Fusion: Processing {Path.GetFileName(imagePath)}…";
            var fused = await System.Threading.Tasks.Task.Run(() =>
            {
                var result = new List<ComposedShape>();

                // Layer 1: Seamless background shape mosaic (if density > 0 and shapes provided)
                if (mosaicGrid > 0 && shapes.Count > 0)
                {
                    var baseShapes = ImageShapeComposer.Compose(imagePath, new ShapeComposerOptions
                    {
                        Grid = mosaicGrid,
                        Shapes = shapes.ToList(),
                        InsetInches = 0.0
                    });
                    result.AddRange(baseShapes);
                }

                // Layer 2: Contours / Edges line art overlay (if density > 0)
                if (lineDensity > 0)
                {
                    var edgeLines = ImageLineTracer.TraceLines(imagePath, new LineTraceOptions
                    {
                        Mode = lineMode,
                        Rows = lineDensity,
                        UseColor = !monochromeLines,
                        EdgeThreshold = edgeThreshold,
                        MaxThicknessPt = maxThicknessPt
                    });
                    result.AddRange(edgeLines);
                }

                return result;
            }, ct);

            if (ct.IsCancellationRequested) return;

            if (fused.Count == 0)
            {
                StatusMessage = "No vector elements generated with selected settings.";
                return;
            }

            ClearAll();
            AppendComposed(fused);
            int shapeCount = fused.Count(s => s.PathPoints == null);
            int lineCount = fused.Count(s => s.PathPoints != null);
            StatusMessage = $"⚡ Vector Fusion: {fused.Count:N0} elements ({shapeCount:N0} shapes + {lineCount:N0} contour lines)";
            await RefreshCanvasModeAsync();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Clean cancellation of superseded generation
        }
        catch (Exception ex)
        {
            StatusMessage = $"Fusion error: {ex.Message}";
        }
    }

    /// <summary>Append composed shapes with ONE wholesale collection replace instead of one
    /// per-item Add (tens of thousands of collection-change notifications on the UI thread).</summary>
    private void AppendComposed(List<ComposedShape> composed)
    {
        var items = new List<ShapeCanvasItemViewModel>(Shapes.Count + composed.Count);
        items.AddRange(Shapes);
        items.AddRange(composed.Select(ToItem));
        Shapes = new ObservableCollection<ShapeCanvasItemViewModel>(items);
    }

    [RelayCommand(CanExecute = nameof(HasShapes))]
    public System.Threading.Tasks.Task ExportDocxAsync() => ExportToWordAsync(template: false);

    [RelayCommand(CanExecute = nameof(HasShapes))]
    public System.Threading.Tasks.Task ExportDotxAsync() => ExportToWordAsync(template: true);

    /// <summary>Default export file name (no folder) — the window offers it in a save dialog.</summary>
    public static string SuggestedExportName(bool template) =>
        $"Shape Studio {DateTime.Now:yyyy-MM-dd HHmm}{(template ? ".dotx" : ".docx")}";

    /// <summary>Path of the last successful export, for "Open" / "Show in folder".</summary>
    [ObservableProperty]
    private string? _lastExportPath;

    /// <summary>Write the canvas to <paramref name="outPath"/> (null = a dated file on the Desktop).
    /// Returns true on success.</summary>
    public async System.Threading.Tasks.Task<bool> ExportToWordAsync(bool template, string? outPath = null)
    {
        try
        {
            if (Shapes.Count == 0) { StatusMessage = "Nothing to export."; return false; }

            var composed = SnapshotComposed();
            double maxX = composed.Max(s => s.X + s.W);
            double maxY = composed.Max(s => s.Y + s.H);
            double w = Math.Max(2, maxX + 0.5);
            double h = Math.Max(2, maxY + 0.5);

            string ext = template ? ".dotx" : ".docx";
            // Full date stamp, not just HHmmss — an export today must not silently overwrite a
            // leftover file from a previous day that happened at the same clock time.
            outPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"MLShape_Studio_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            string themeXml = SmartArtLayoutCatalog.Shared.ThemeXml;
            StatusMessage = $"Exporting {composed.Count:N0} shapes…";
            await System.Threading.Tasks.Task.Run(() =>
            {
                if (template)
                    ShapeComposerDocxWriter.WriteDotx(outPath, composed, w, h, themeXml);
                else
                    ShapeComposerDocxWriter.WriteDocx(outPath, composed, w, h, themeXml);
            });
            LastExportPath = outPath;
            _keptCanvas = composed;
            StatusMessage = $"✓ Exported {composed.Count:N0} native Word shapes → {Path.GetFileName(outPath)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Snapshot the canvas as ComposedShapes (incl. Text/TextColor) — the single source
    /// used by export AND copy-as-markdown so both paths round-trip identically.</summary>
    public List<ComposedShape> SnapshotComposed() => Shapes.Select(ToComposed).ToList();

    // ---- shared helpers ----

    /// <summary>Pick the canvas presentation that keeps the studio usable at any density:
    /// dense traces (or anything above the threshold) render as one raster line-art image
    /// (it literally looks like the picture), small hand-built sets stay fully editable.
    /// The preview raster runs on the thread pool — rendering 24k lines to PNG on the UI
    /// thread used to freeze the studio after every load/compose.</summary>
    private async System.Threading.Tasks.Task RefreshCanvasModeAsync()
    {
        if (Shapes.Count == 0)
        {
            CanvasMode = "empty";
            PreviewPng = null;
            return;
        }
        var composed = SnapshotComposed();
        bool isDenseTrace = composed.Count > DenseCanvasThreshold || composed.Count(s => s.PathPoints is { Count: >= 2 }) > 25;
        if (isDenseTrace)
        {
            var (w, h) = MarkSmith.Core.Composer.ShapeMarkdownCodec.CanvasSize(composed);
            PreviewPng = await System.Threading.Tasks.Task.Run(
                () => ImageLineTracer.RenderPreviewPng(composed, w, h, previewCap: 24000));
            CanvasMode = "dense";
        }
        else
        {
            PreviewPng = null;
            CanvasMode = "editable";
        }
    }

    public const double Dpi = 96.0;

    private static ShapeCanvasItemViewModel ToItem(ComposedShape s) => new()
    {
        Prst = s.Prst,
        Name = s.Prst,
        X = s.X * Dpi,
        Y = s.Y * Dpi,
        Width = Math.Max(0.01, s.W * Dpi),
        Height = Math.Max(0.01, s.H * Dpi),
        Fill = s.Fill,
        Rotation = s.Rot,
        PathPoints = s.PathPoints,
        StrokeWidthPt = s.StrokeWidthPt,
        Text = s.Text ?? "",
        TextColor = s.TextColor
    };

    private static ComposedShape ToComposed(ShapeCanvasItemViewModel s) => new()
    {
        Prst = s.Prst,
        X = s.X / Dpi,
        Y = s.Y / Dpi,
        W = s.Width / Dpi,
        H = s.Height / Dpi,
        Fill = s.Fill,
        Rot = s.Rotation,
        PathPoints = s.PathPoints,
        StrokeWidthPt = s.StrokeWidthPt,
        Text = string.IsNullOrWhiteSpace(s.Text) ? null : s.Text,
        TextColor = s.TextColor
    };
}
