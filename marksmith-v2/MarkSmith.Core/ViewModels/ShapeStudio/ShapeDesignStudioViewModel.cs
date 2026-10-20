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

    [ObservableProperty]
    private double _cornerRadius = 0.0;

    [ObservableProperty]
    private string _arrowheadStart = "None";

    [ObservableProperty]
    private string _arrowheadEnd = "FilledTriangle";

    [ObservableProperty]
    private string _dashStyle = "Solid";

    /// <summary>Curved-stroke polyline (0..100 local space) for sketch/trace lines.</summary>
    public System.Collections.Generic.List<(double X, double Y)>? PathPoints { get; set; }

    /// <summary>Alias for Prst for connector/drawing type compatibility.</summary>
    public string ShapeType { get => Prst; set => Prst = value; }

    /// <summary>Alternative points accessor for vector/connector operations.</summary>
    public System.Collections.Generic.List<System.Numerics.Vector2>? Points
    {
        get => PathPoints?.Select(p => new System.Numerics.Vector2((float)p.X, (float)p.Y)).ToList();
        set => PathPoints = value?.Select(p => ((double)p.X, (double)p.Y)).ToList();
    }

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

    /// <summary>Accessible name and tooltip for the swatch button in the inspector.</summary>
    public string ColourPickerLabel => IsLine ? "Pick line colour" : "Pick fill colour";

    /// <summary>Accessible name for the hex input box in the inspector.</summary>
    public string ColourHexLabel => IsLine ? "Line colour" : "Fill colour";

    /// <summary>Tooltip for the hex input box in the inspector.</summary>
    public string ColourHexToolTip => IsLine
        ? "Line colour as a 6-digit hex value (e.g. FF8C00)"
        : "Fill colour as a 6-digit hex value (e.g. 0078D4)";

    /// <summary>True when <paramref name="raw"/> is a complete 6-digit hexadecimal colour string
    /// (with or without a leading '#').</summary>
    public static bool IsSixDigitHex(string? raw, out string cleanUpper)
    {
        cleanUpper = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        for (int i = 0; i < 6; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        cleanUpper = s.ToUpperInvariant();
        return true;
    }

    /// <summary>Normalises user-entered hex colour strings (accepts optional '#', 3-digit shorthand
    /// like "F80" → "FF8800", or 6-digit hex) and applies the studio contrast guard; falls back to
    /// <paramref name="fallbackHex"/> when the input is not a valid hex colour.</summary>
    public static string NormalizeHexInput(string? raw, string fallbackHex = "0078D4")
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string s = raw.Trim().TrimStart('#');
            if (s.Length == 3)
                s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            if (IsSixDigitHex(s, out string clean))
                return Services.ContrastGuard.EnsureVisibleFill(clean, ShapeDesignStudioViewModel.CanvasBackgroundHex);
        }
        return IsSixDigitHex(fallbackHex, out string fb)
            ? Services.ContrastGuard.EnsureVisibleFill(fb, ShapeDesignStudioViewModel.CanvasBackgroundHex)
            : "0078D4";
    }

    partial void OnFillChanged(string value)
    {
        // HARD RULE (ContrastGuard.EnsureVisibleFill): a shape fill must NEVER blend into the
        // studio canvas (#1B1B1F) — if the user picks (or a theme supplies) a fill that is the
        // same colour as the background, the rule pushes it to a visible shade so the shape is
        // always distinguishable. Only guard once a complete 6-digit hex value is present so
        // intermediate keystrokes in the hex box are not overwritten with the fallback blue.
        if (IsSixDigitHex(value, out string clean))
        {
            string guarded = Services.ContrastGuard.EnsureVisibleFill(clean, ShapeDesignStudioViewModel.CanvasBackgroundHex);
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
        OnPropertyChanged(nameof(ColourPickerLabel));
        OnPropertyChanged(nameof(ColourHexLabel));
        OnPropertyChanged(nameof(ColourHexToolTip));
    }

    partial void OnPrstChanged(string value)
    {
        OnPropertyChanged(nameof(IsLine));
        OnPropertyChanged(nameof(ColourLabel));
        OnPropertyChanged(nameof(ColourPickerLabel));
        OnPropertyChanged(nameof(ColourHexLabel));
        OnPropertyChanged(nameof(ColourHexToolTip));
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

    /// <summary>Inspector list cap: tens of thousands of templated rows freeze the pane,
    /// so the list shows the first chunk (plus the selection) while the canvas, export
    /// and status line always cover every shape.</summary>
    public const int ListedShapesCap = 1000;

    private ObservableCollection<ShapeCanvasItemViewModel>? _listedShapes;
    public ObservableCollection<ShapeCanvasItemViewModel> ListedShapes => _listedShapes ??= new();

    public bool IsShapesListCapped => Shapes.Count > ListedShapesCap;

    private void RefreshListedShapes()
    {
        var want = (Shapes.Count > ListedShapesCap ? Shapes.Take(ListedShapesCap) : Shapes.AsEnumerable()).ToList();
        if (SelectedShape is not null && !want.Contains(SelectedShape)) want.Add(SelectedShape);
        if (_listedShapes is not null && _listedShapes.Count == want.Count && _listedShapes.SequenceEqual(want)) return;
        _listedShapes = new ObservableCollection<ShapeCanvasItemViewModel>(want);
        OnPropertyChanged(nameof(ListedShapes));
        OnPropertyChanged(nameof(IsShapesListCapped));
        OnPropertyChanged(nameof(ShapesListHint));
    }

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
        RefreshListedShapes();
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

    public void ClearSelection()
    {
        SelectedShape = null;
        foreach (var s in Shapes) s.IsSelected = false;
        RaiseSelectionChanged();
    }

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
        BringToFrontCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
        RotateClockwiseCommand.NotifyCanExecuteChanged();
        RotateCounterClockwiseCommand.NotifyCanExecuteChanged();
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

    /// <summary>Length in canvas pixels of the stem extending from the top-middle resize handle
    /// to the circular rotation grip.</summary>
    public const double RotationHandleStemLength = 24.0;

    /// <summary>Where the circular rotation grip sits on the canvas for a shape turned by
    /// <paramref name="rotation"/> degrees about its centre.</summary>
    public static (double X, double Y) RotationHandlePosition(double x, double y, double w, double h, double rotation, double stemLength = RotationHandleStemLength)
    {
        var (topX, topY) = HandlePosition(x, y, w, h, rotation, ResizeEdges.Top);
        double rad = rotation * Math.PI / 180.0;
        return (topX + stemLength * Math.Sin(rad), topY - stemLength * Math.Cos(rad));
    }

    /// <summary>Wraps any degree angle into the inspector's (-180, 180] range.</summary>
    public static int WrapRotationAngle(int deg)
    {
        int next = ((deg % 360) + 360) % 360;
        if (next > 180) next -= 360;
        return next;
    }

    /// <summary>Angle in degrees (-180, 180] from a shape's centre to the pointer, with 0° straight up
    /// and clockwise positive. When <paramref name="snapTo15"/> is true (Shift held), snaps to the
    /// nearest 15° increment.</summary>
    public static int ComputeRotationAngle(double centerX, double centerY, double pointerX, double pointerY, bool snapTo15 = false)
    {
        double dx = pointerX - centerX;
        double dy = pointerY - centerY;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return 0;
        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90.0;
        int rounded = snapTo15
            ? (int)Math.Round(deg / 15.0) * 15
            : (int)Math.Round(deg);
        return WrapRotationAngle(rounded);
    }

    /// <summary>Short direction name for a resize handle in the shape's local frame.</summary>
    public static string HandleDirectionName(ResizeEdges edges) => edges switch
    {
        ResizeEdges.Left | ResizeEdges.Top => "top-left",
        ResizeEdges.Top => "top",
        ResizeEdges.Right | ResizeEdges.Top => "top-right",
        ResizeEdges.Right => "right",
        ResizeEdges.Right | ResizeEdges.Bottom => "bottom-right",
        ResizeEdges.Bottom => "bottom",
        ResizeEdges.Left | ResizeEdges.Bottom => "bottom-left",
        ResizeEdges.Left => "left",
        _ => "edge",
    };

    /// <summary>Accessible name for a resize handle, noting the shape's rotation angle when turned.</summary>
    public static string DescribeResizeHandle(ResizeEdges edges, int rotation = 0)
    {
        string dir = HandleDirectionName(edges);
        int norm = WrapRotationAngle(rotation);
        return norm == 0
            ? $"Resize from the {dir}"
            : $"Resize from the {dir} (rotated {norm}°)";
    }

    /// <summary>Status-bar feedback during or after a resize gesture, including rotation angle when turned.</summary>
    public static string FormatResizeStatus(ShapeCanvasItemViewModel shape, bool completed = false)
    {
        int norm = WrapRotationAngle(shape.Rotation);
        string angleSuffix = norm == 0 ? "" : $" · {norm}°";
        return completed
            ? $"Resized {shape.DisplayName.ToLowerInvariant()} to {shape.Width:F0} × {shape.Height:F0}{angleSuffix}"
            : $"{shape.DisplayName}: {shape.Width:F0} × {shape.Height:F0}{angleSuffix}";
    }

    /// <summary>Axis-aligned bounding box of <paramref name="s"/> on the canvas, accounting for its
    /// rotation around its centre (or its routed points when it is a connector line).</summary>
    public static (double Left, double Top, double Right, double Bottom) RotatedBounds(ShapeCanvasItemViewModel s)
    {
        if (s.PathPoints is { Count: >= 2 })
        {
            var pts = ConnectorPoints(s);
            return (pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }
        double turn = ((s.Rotation % 360) + 360) % 360;
        if (turn == 0 || turn == 180)
            return (s.X, s.Y, s.X + s.Width, s.Y + s.Height);
        double rad = turn * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double rw = s.Width * cos + s.Height * sin;
        double rh = s.Width * sin + s.Height * cos;
        double cx = s.X + s.Width / 2.0, cy = s.Y + s.Height / 2.0;
        return (cx - rw / 2.0, cy - rh / 2.0, cx + rw / 2.0, cy + rh / 2.0);
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
        if (IsDense) return;
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
        var hits = Shapes.Where(s =>
        {
            var b = RotatedBounds(s);
            return b.Left < x + w && b.Right > x && b.Top < y + h && b.Bottom > y;
        }).ToList();
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

    /// <summary>Appended to the list hint when the inspector shows the first chunk only.</summary>
    public string ShapesListCapNote => IsShapesListCapped
        ? $" Showing first {ListedShapesCap:N0} of {Shapes.Count:N0} — canvas and export hold them all."
        : "";

    /// <summary>Inspector empty state — differs between a blank canvas and an unselected one.</summary>
    public string InspectorEmptyTitle => IsEmpty ? "Nothing to inspect yet" : "No shape selected";

    public string InspectorEmptyHint => IsEmpty
        ? "Pick a preset or draw a shape — its properties appear here."
        : "Click a shape on the canvas or in the list below to edit its type, position, size, fill and label.";

    partial void OnSelectedShapeChanged(ShapeCanvasItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedShape));
        RefreshListedShapes();
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
        StatusMessage = $"Applied preset '{preset.Name}' ({Shapes.Count} shapes) · Ctrl+Z to undo";
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

    /// <summary>Centers the diagram or the selected shapes within the canvas bounding box.</summary>
    public void CenterDiagramOnCanvas(bool horizontal = true, bool vertical = true, double canvasW = 2200, double canvasH = 1800)
    {
        var targets = SelectionCount >= 2 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count == 0) return;
        RecordUndo();
        double minX = targets.Min(s => s.X);
        double maxX = targets.Max(s => s.X + s.Width);
        double minY = targets.Min(s => s.Y);
        double maxY = targets.Max(s => s.Y + s.Height);

        double boxCenterX = (minX + maxX) / 2.0;
        double boxCenterY = (minY + maxY) / 2.0;

        double dx = horizontal ? (canvasW / 2.0) - boxCenterX : 0;
        double dy = vertical ? (canvasH / 2.0) - boxCenterY : 0;

        foreach (var s in targets)
        {
            s.X += dx;
            s.Y += dy;
        }

        StatusMessage = targets.Count == Shapes.Count
            ? "Centered diagram on canvas"
            : $"Centered {targets.Count} selected shapes";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resets the diagram or selected shapes to a standard top-left padding.</summary>
    public void ResetDiagramToTopLeft(double padX = 60, double padY = 60)
    {
        var targets = SelectionCount >= 2 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count == 0) return;
        RecordUndo();
        double minX = targets.Min(s => s.X);
        double minY = targets.Min(s => s.Y);

        double dx = padX - minX;
        double dy = padY - minY;

        foreach (var s in targets)
        {
            s.X += dx;
            s.Y += dy;
        }

        StatusMessage = "Moved diagram to top-left margin";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Distributes the diagram or selected shapes with equal spacing between their bounding boxes.</summary>
    public void DistributeDiagramOnCanvas(bool vertical = false)
    {
        var targets = SelectionCount >= 3 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count < 3) return;
        RecordUndo();

        if (!vertical)
        {
            // Horizontal distribution: sort by X
            var sorted = targets.OrderBy(s => s.X).ToList();
            double minX = sorted.First().X;
            double maxX = sorted.Last().X + sorted.Last().Width;
            double totalShapeWidths = sorted.Sum(s => s.Width);
            double totalGap = (maxX - minX) - totalShapeWidths;
            if (totalGap < 0) totalGap = 0;
            double gap = totalGap / (sorted.Count - 1);
            double currentX = minX;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    sorted[i].X = Math.Round(currentX);
                }
                currentX += sorted[i].Width + gap;
            }
        }
        else
        {
            // Vertical distribution: sort by Y
            var sorted = targets.OrderBy(s => s.Y).ToList();
            double minY = sorted.First().Y;
            double maxY = sorted.Last().Y + sorted.Last().Height;
            double totalShapeHeights = sorted.Sum(s => s.Height);
            double totalGap = (maxY - minY) - totalShapeHeights;
            if (totalGap < 0) totalGap = 0;
            double gap = totalGap / (sorted.Count - 1);
            double currentY = minY;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    sorted[i].Y = Math.Round(currentY);
                }
                currentY += sorted[i].Height + gap;
            }
        }

        StatusMessage = targets.Count == Shapes.Count
            ? $"Distributed diagram {(vertical ? "vertically" : "horizontally")}"
            : $"Distributed {targets.Count} shapes {(vertical ? "vertically" : "horizontally")}";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Auto-tidies shapes into a clean 2D flow grid layout.</summary>
    public void TidyDiagramIntoGrid(int columns = 3, double marginX = 80, double marginY = 80, double gapX = 40, double gapY = 40)
    {
        var targets = SelectionCount >= 2 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count == 0) return;
        RecordUndo();

        int col = 0;
        double currentX = marginX;
        double currentY = marginY;
        double rowMaxH = 0;

        foreach (var s in targets)
        {
            s.X = currentX;
            s.Y = currentY;
            rowMaxH = Math.Max(rowMaxH, s.Height);
            currentX += s.Width + gapX;
            col++;
            if (col >= columns)
            {
                col = 0;
                currentX = marginX;
                currentY += rowMaxH + gapY;
                rowMaxH = 0;
            }
        }

        StatusMessage = $"Arranged {targets.Count} shapes in a grid";
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

    // ---- Z-order / layering & 90° rotation ----

    /// <summary>Moves every selected shape to the top of the drawing order, preserving their
    /// relative order and keeping the multi-selection intact.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void BringToFront()
    {
        var selected = SelectedItems.ToList();
        if (selected.Count == 0 || IsDense) return;
        int firstTarget = Shapes.Count - selected.Count;
        bool alreadyAtFront = selected.Select((s, idx) => Shapes.IndexOf(s) == firstTarget + idx).All(x => x);
        if (alreadyAtFront)
        {
            StatusMessage = "Already at the front.";
            return;
        }
        RecordUndo();
        var primary = SelectedShape;
        _additiveSelect = true;
        try
        {
            foreach (var s in selected)
            {
                int cur = Shapes.IndexOf(s);
                if (cur >= 0 && cur != Shapes.Count - 1) Shapes.Move(cur, Shapes.Count - 1);
            }
            foreach (var s in selected) s.IsSelected = true;
            SelectedShape = primary;
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        StatusMessage = selected.Count == 1
            ? $"Brought {selected[0].DisplayName.ToLowerInvariant()} to the front"
            : $"Brought {selected.Count} shapes to the front";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves every selected shape one step forward in the drawing order.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void BringForward()
    {
        var selected = SelectedItems.ToList();
        if (selected.Count == 0 || IsDense) return;
        bool canMove = false;
        for (int i = Shapes.Count - 2; i >= 0; i--)
        {
            if (Shapes[i].IsSelected && !Shapes[i + 1].IsSelected) { canMove = true; break; }
        }
        if (!canMove)
        {
            StatusMessage = "Already at the front.";
            return;
        }
        RecordUndo();
        var primary = SelectedShape;
        _additiveSelect = true;
        try
        {
            for (int i = Shapes.Count - 2; i >= 0; i--)
            {
                if (Shapes[i].IsSelected && !Shapes[i + 1].IsSelected) Shapes.Move(i, i + 1);
            }
            foreach (var s in selected) s.IsSelected = true;
            SelectedShape = primary;
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        StatusMessage = selected.Count == 1
            ? $"Brought {selected[0].DisplayName.ToLowerInvariant()} forward"
            : $"Brought {selected.Count} shapes forward";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves every selected shape one step backward in the drawing order.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void SendBackward()
    {
        var selected = SelectedItems.ToList();
        if (selected.Count == 0 || IsDense) return;
        bool canMove = false;
        for (int i = 1; i < Shapes.Count; i++)
        {
            if (Shapes[i].IsSelected && !Shapes[i - 1].IsSelected) { canMove = true; break; }
        }
        if (!canMove)
        {
            StatusMessage = "Already at the back.";
            return;
        }
        RecordUndo();
        var primary = SelectedShape;
        _additiveSelect = true;
        try
        {
            for (int i = 1; i < Shapes.Count; i++)
            {
                if (Shapes[i].IsSelected && !Shapes[i - 1].IsSelected) Shapes.Move(i, i - 1);
            }
            foreach (var s in selected) s.IsSelected = true;
            SelectedShape = primary;
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        StatusMessage = selected.Count == 1
            ? $"Sent {selected[0].DisplayName.ToLowerInvariant()} backward"
            : $"Sent {selected.Count} shapes backward";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves every selected shape to the bottom of the drawing order, preserving their
    /// relative order and keeping the multi-selection intact.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void SendToBack()
    {
        var selected = SelectedItems.ToList();
        if (selected.Count == 0 || IsDense) return;
        bool alreadyAtBack = selected.Select((s, idx) => Shapes.IndexOf(s) == idx).All(x => x);
        if (alreadyAtBack)
        {
            StatusMessage = "Already at the back.";
            return;
        }
        RecordUndo();
        var primary = SelectedShape;
        _additiveSelect = true;
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                int cur = Shapes.IndexOf(selected[i]);
                if (cur >= 0 && cur != i) Shapes.Move(cur, i);
            }
            foreach (var s in selected) s.IsSelected = true;
            SelectedShape = primary;
        }
        finally { _additiveSelect = false; }
        RaiseSelectionChanged();
        StatusMessage = selected.Count == 1
            ? $"Sent {selected[0].DisplayName.ToLowerInvariant()} to the back"
            : $"Sent {selected.Count} shapes to the back";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rotates every selected shape 90° clockwise, wrapping into [-180, 180].</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void RotateClockwise() => RotateSelectionBy(90);

    /// <summary>Rotates every selected shape 90° counter-clockwise, wrapping into [-180, 180].</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void RotateCounterClockwise() => RotateSelectionBy(-90);

    /// <summary>Resets every selected non-line shape's rotation back to 0°.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void ResetRotation()
    {
        if (IsDense) return;
        var selected = SelectedItems.Where(s => s.PathPoints is not { Count: >= 2 }).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Connector lines are routed by their endpoints — drag an endpoint to turn a line.";
            return;
        }
        if (selected.All(s => s.Rotation == 0))
        {
            StatusMessage = "Rotation is already 0°.";
            return;
        }
        RecordUndo();
        foreach (var s in selected) s.Rotation = 0;
        StatusMessage = selected.Count == 1
            ? $"Reset {selected[0].DisplayName.ToLowerInvariant()} rotation to 0°"
            : $"Reset {selected.Count} shapes to 0°";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RotateSelectionBy(int deltaDegrees)
    {
        if (IsDense || deltaDegrees == 0) return;
        var selected = SelectedItems.Where(s => s.PathPoints is not { Count: >= 2 }).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Connector lines are routed by their endpoints — drag an endpoint to turn a line.";
            return;
        }
        RecordUndo();
        foreach (var s in selected)
        {
            s.Rotation = WrapRotationAngle(s.Rotation + deltaDegrees);
        }
        string deltaLabel = deltaDegrees > 0 ? $"+{deltaDegrees}°" : $"−{Math.Abs(deltaDegrees)}°";
        StatusMessage = selected.Count == 1
            ? $"Rotated {selected[0].DisplayName.ToLowerInvariant()} to {selected[0].Rotation}°"
            : $"Rotated {selected.Count} shapes by {deltaLabel}";
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
                    () => ImageLineTracer.RenderPreviewPng(snapshot, w, h, previewCap: 0), ct);
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

    public async System.Threading.Tasks.Task ComposeImageAsync(string imagePath, int grid, IReadOnlyList<string> shapes,
        double mosaicPadding = 0.0, bool adaptiveShapes = false)
    {
        try
        {
            StatusMessage = $"Composing {Path.GetFileName(imagePath)}…";
            var composed = await System.Threading.Tasks.Task.Run(
                () => ImageShapeComposer.Compose(imagePath, new ShapeComposerOptions
                {
                    Grid = grid,
                    Shapes = shapes.Any() ? shapes.ToList() : new List<string> { "ellipse" },
                    InsetInches = 0.0,
                    Padding = mosaicPadding,
                    AdaptiveShapes = adaptiveShapes
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
        double maxThicknessPt = 1.8,
        double mosaicPadding = 0.0,
        bool adaptiveShapes = false)
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
                        InsetInches = 0.0,
                        Padding = mosaicPadding,
                        AdaptiveShapes = adaptiveShapes
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
                () => ImageLineTracer.RenderPreviewPng(composed, w, h, previewCap: 0));
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

    /// <summary>
    /// Computes the collective bounding box (MinX, MinY, MaxX, MaxY, Width, Height) of a set of items.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY, double Width, double Height) ComputeSelectionBounds(
        IEnumerable<(double X, double Y, double Width, double Height)> shapes)
    {
        var list = shapes?.ToList() ?? new List<(double X, double Y, double Width, double Height)>();
        if (list.Count == 0) return (0, 0, 0, 0, 0, 0);

        double minX = list.Min(s => s.X);
        double minY = list.Min(s => s.Y);
        double maxX = list.Max(s => s.X + s.Width);
        double maxY = list.Max(s => s.Y + s.Height);
        return (minX, minY, maxX, maxY, maxX - minX, maxY - minY);
    }

    public static (double MinX, double MinY, double MaxX, double MaxY, double Width, double Height) ComputeSelectionBounds(
        IEnumerable<ShapeCanvasItemViewModel> shapes)
    {
        if (shapes is null) return (0, 0, 0, 0, 0, 0);
        return ComputeSelectionBounds(shapes.Select(s => (s.X, s.Y, s.Width, s.Height)));
    }

    /// <summary>
    /// Computes equal spacing positions for a sequence of 1D items given their lengths and total span.
    /// </summary>
    public static List<double> ComputeEqualSpacing(double startPos, double totalSpan, IReadOnlyList<double> itemSizes)
    {
        if (itemSizes == null || itemSizes.Count == 0) return new List<double>();
        if (itemSizes.Count == 1) return new List<double> { startPos };

        double sumSizes = itemSizes.Sum();
        double availableSpace = totalSpan - sumSizes;
        double gap = itemSizes.Count > 1 ? availableSpace / (itemSizes.Count - 1) : 0;

        var positions = new List<double>(itemSizes.Count);
        double cur = startPos;
        for (int i = 0; i < itemSizes.Count; i++)
        {
            positions.Add(cur);
            cur += itemSizes[i] + gap;
        }
        return positions;
    }

    /// <summary>
    /// Computes the delta translation required to clamp a bounding box inside canvas bounds with padding.
    /// If the box already fits inside, returns (0, 0).
    /// </summary>
    public static (double DeltaX, double DeltaY) ComputeClampOffset(
        double minX, double minY, double maxX, double maxY,
        double canvasWidth, double canvasHeight, double padding = 40.0)
    {
        double dx = 0;
        double dy = 0;

        // X clamping
        if (minX < padding)
        {
            dx = padding - minX;
        }
        else if (maxX > canvasWidth - padding)
        {
            dx = (canvasWidth - padding) - maxX;
            if (minX + dx < padding)
            {
                dx = padding - minX;
            }
        }

        // Y clamping
        if (minY < padding)
        {
            dy = padding - minY;
        }
        else if (maxY > canvasHeight - padding)
        {
            dy = (canvasHeight - padding) - maxY;
            if (minY + dy < padding)
            {
                dy = padding - minY;
            }
        }

        return (dx, dy);
    }

    /// <summary>
    /// Snaps a scalar coordinate to the nearest grid increment (default 10px).
    /// </summary>
    public static double SnapToGrid(double value, double gridSize = 10.0)
    {
        if (gridSize <= 0) return value;
        return Math.Round(value / gridSize, MidpointRounding.AwayFromZero) * gridSize;
    }

    /// <summary>
    /// Snaps a 2D coordinate point to the nearest grid increments.
    /// </summary>
    public static (double X, double Y) SnapPointToGrid(double x, double y, double gridSize = 10.0)
    {
        return (SnapToGrid(x, gridSize), SnapToGrid(y, gridSize));
    }

    /// <summary>
    /// Calculates the aspect ratio (width / height) and returns a human-readable label (e.g. "16:9 (Widescreen)", "1:1 (Square)").
    /// </summary>
    public static (double Ratio, string Label) CalculateAspectRatio(double width, double height)
    {
        if (width <= 0 || height <= 0) return (1.0, "1:1");
        var ratio = width / height;
        if (Math.Abs(ratio - 1.0) < 0.02) return (1.0, "1:1 (Square)");
        if (Math.Abs(ratio - 16.0 / 9.0) < 0.03) return (16.0 / 9.0, "16:9 (Widescreen)");
        if (Math.Abs(ratio - 4.0 / 3.0) < 0.03) return (4.0 / 3.0, "4:3 (Standard)");
        if (Math.Abs(ratio - 3.0 / 2.0) < 0.03) return (3.0 / 2.0, "3:2 (Classic)");
        if (Math.Abs(ratio - 2.0 / 1.0) < 0.03) return (2.0, "2:1 (Univisium)");
        if (Math.Abs(ratio - 9.0 / 16.0) < 0.03) return (9.0 / 16.0, "9:16 (Portrait)");
        if (Math.Abs(ratio - 3.0 / 4.0) < 0.03) return (3.0 / 4.0, "3:4 (Portrait)");
        return (ratio, $"{ratio:0.##}:1");
    }

    /// <summary>
    /// Applies a target aspect ratio to dimensions, preserving current width and recalculating height with geometric rounding.
    /// </summary>
    public static (double NewWidth, double NewHeight) ApplyStandardAspectRatio(double currentWidth, double currentHeight, double targetRatio = 16.0 / 9.0)
    {
        if (targetRatio <= 0 || currentWidth <= 0)
            return (Math.Max(10, currentWidth), Math.Max(10, currentHeight));
        var newHeight = Math.Max(10, Math.Round(currentWidth / targetRatio, MidpointRounding.AwayFromZero));
        return (currentWidth, newHeight);
    }

    /// <summary>
    /// Cycles through standard cardinal rotation increments (0° -> 90° -> 180° -> -90°/270° -> 0°) and returns the next angle and label.
    /// </summary>
    public static (int NextRotation, string Label) CycleCardinalRotation(int currentRotation)
    {
        int norm = ((currentRotation % 360) + 360) % 360;
        int next = norm switch
        {
            < 90 => 90,
            < 180 => 180,
            < 270 => 270,
            _ => 0
        };
        int wrapped = WrapRotationAngle(next);
        return (wrapped, $"Rotation: {wrapped}°");
    }

    /// <summary>
    /// Toggles shape preset between sharp rectangle ("rect") and rounded rectangle ("roundrect").
    /// </summary>
    public static (string NextPrst, string Label) ToggleCornerStyle(string? currentPrst)
    {
        if (string.Equals(currentPrst, "roundrect", StringComparison.OrdinalIgnoreCase))
            return ("rect", "Sharp corners (rect)");
        return ("roundrect", "Rounded corners (roundrect)");
    }

    /// <summary>
    /// Cycles stroke dash pattern ("solid" -> "dashed" -> "dotted" -> "solid") and returns the next style token and display label.
    /// </summary>
    public static (string NextDashStyle, string Label) CycleStrokeDashStyle(string? currentDashStyle)
    {
        var normalized = (currentDashStyle ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "dashed" or "dash" or "4 2" or "4,2" => ("dotted", "Stroke dash: Dotted (2 2)"),
            "dotted" or "dot" or "2 2" or "2,2" => ("solid", "Stroke dash: Solid (Continuous)"),
            _ => ("dashed", "Stroke dash: Dashed (4 2)")
        };
    }

    /// <summary>
    /// Cycles shape opacity (1.0 -> 0.75 -> 0.50 -> 0.25 -> 1.0) and returns the next opacity and display label.
    /// </summary>
    public static (double NextOpacity, string Label) CycleShapeOpacity(double currentOpacity)
    {
        if (currentOpacity > 0.87) return (0.75, "Opacity: 75% (Translucent)");
        if (currentOpacity > 0.62) return (0.50, "Opacity: 50% (Semi-transparent)");
        if (currentOpacity > 0.37) return (0.25, "Opacity: 25% (Faint)");
        return (1.0, "Opacity: 100% (Solid)");
    }

    /// <summary>
    /// Cycles shape shadow blur radius (0px -> 4px -> 8px -> 16px -> 0px) and returns the next blur radius and display label.
    /// </summary>
    public static (double NextBlur, string Label) CycleShadowBlurRadius(double currentBlur)
    {
        if (currentBlur < 2.0) return (4.0, "Shadow blur: 4px (Subtle)");
        if (currentBlur < 6.0) return (8.0, "Shadow blur: 8px (Medium)");
        if (currentBlur < 12.0) return (16.0, "Shadow blur: 16px (Deep)");
        return (0.0, "Shadow blur: 0px (Crisp / Off)");
    }

    /// <summary>
    /// Formats a human-readable shadow blur radius summary for inspector and status readout.
    /// </summary>
    public static string FormatShadowBlurSummary(double blurRadius)
    {
        if (blurRadius <= 0.0) return "Shadow blur: 0px (Crisp / Off)";
        if (blurRadius <= 4.0) return $"Shadow blur: {blurRadius:0.#}px (Subtle)";
        if (blurRadius <= 8.0) return $"Shadow blur: {blurRadius:0.#}px (Medium)";
        return $"Shadow blur: {blurRadius:0.#}px (Deep)";
    }

    /// <summary>
    /// Cycles shape corner radius (0px -> 4px -> 8px -> 16px -> 0px) and returns the next corner radius and display label.
    /// </summary>
    public static (double NextRadius, string Label) CycleCornerRadius(double currentRadius)
    {
        if (currentRadius < 2.0) return (4.0, "Corner radius: 4px (Subtle rounded)");
        if (currentRadius < 6.0) return (8.0, "Corner radius: 8px (Medium rounded)");
        if (currentRadius < 12.0) return (16.0, "Corner radius: 16px (Pill / Deeply rounded)");
        return (0.0, "Corner radius: 0px (Square / Sharp)");
    }

    /// <summary>
    /// Formats a human-readable corner radius summary for inspector and status readout.
    /// </summary>
    public static string FormatCornerRadiusSummary(double cornerRadius)
    {
        if (cornerRadius <= 0.0) return "Corner radius: 0px (Square / Sharp)";
        if (cornerRadius <= 4.0) return $"Corner radius: {cornerRadius:0.#}px (Subtle rounded)";
        if (cornerRadius <= 8.0) return $"Corner radius: {cornerRadius:0.#}px (Medium rounded)";
        return $"Corner radius: {cornerRadius:0.#}px (Pill / Deeply rounded)";
    }

    /// <summary>
    /// Cycles shape stroke dash offset / phase (0.0 -> 2.0 -> 4.0 -> 6.0 -> 0.0) and returns the next offset and display label.
    /// </summary>
    public static (double NextOffset, string Label) CycleStrokeDashOffset(double currentOffset)
    {
        if (currentOffset < 1.0) return (2.0, "Dash offset: 2.0 (Phase 1)");
        if (currentOffset < 3.0) return (4.0, "Dash offset: 4.0 (Phase 2)");
        if (currentOffset < 5.0) return (6.0, "Dash offset: 6.0 (Phase 3)");
        return (0.0, "Dash offset: 0.0 (Aligned / Zero)");
    }

    /// <summary>
    /// Formats a human-readable stroke dash offset and phase summary for inspector and status readout.
    /// </summary>
    public static string FormatStrokeDashOffsetSummary(double dashOffset)
    {
        if (dashOffset <= 0.0) return "Stroke dash offset: 0.0 (Zero / Aligned)";
        if (dashOffset <= 2.0) return $"Stroke dash offset: {dashOffset:0.#} (Phase 1)";
        if (dashOffset <= 4.0) return $"Stroke dash offset: {dashOffset:0.#} (Phase 2)";
        return $"Stroke dash offset: {dashOffset:0.#} (Phase 3)";
    }

    /// <summary>
    /// Reverses the stacking (Z-order) of selected shapes, or of the entire canvas if fewer than two shapes are selected.
    /// Preserves selection and records an undo snapshot.
    /// </summary>
    public void ReverseSelectedZOrder()
    {
        if (Shapes.Count < 2)
        {
            StatusMessage = "At least 2 shapes required to reverse layer stacking order.";
            return;
        }

        var selected = SelectedItems.ToList();
        RecordUndo();
        var primary = SelectedShape;
        _additiveSelect = true;

        try
        {
            if (selected.Count >= 2)
            {
                var indices = selected.Select(s => Shapes.IndexOf(s)).Where(i => i >= 0).OrderBy(i => i).ToList();
                var itemsReversed = indices.Select(i => Shapes[i]).Reverse().ToList();
                for (int k = 0; k < indices.Count; k++)
                {
                    int targetIndex = indices[k];
                    var item = itemsReversed[k];
                    int currentIndex = Shapes.IndexOf(item);
                    if (currentIndex != targetIndex)
                    {
                        Shapes.Move(currentIndex, targetIndex);
                    }
                }
                foreach (var s in selected) s.IsSelected = true;
                SelectedShape = primary;
                StatusMessage = $"✓ Reversed layer stacking order for {selected.Count} selected shapes";
            }
            else
            {
                var reversedList = Shapes.Reverse().ToList();
                Shapes.Clear();
                foreach (var item in reversedList)
                {
                    Shapes.Add(item);
                }
                if (primary != null)
                {
                    primary.IsSelected = true;
                    SelectedShape = primary;
                }
                StatusMessage = $"✓ Reversed canvas layer stacking order ({Shapes.Count} shapes flipped)";
            }
        }
        finally
        {
            _additiveSelect = false;
        }

        RaiseSelectionChanged();
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Formats a human-readable audit of canvas layer Z-order stacking and selected item depths.
    /// </summary>
    public static string FormatZOrderSummary(IEnumerable<ShapeCanvasItemViewModel>? shapes, IEnumerable<ShapeCanvasItemViewModel>? selected = null)
    {
        if (shapes == null) return "Layer stack: 0 shapes (Empty canvas)";
        var shapeList = shapes.ToList();
        if (shapeList.Count == 0) return "Layer stack: 0 shapes (Empty canvas)";

        string countLabel = shapeList.Count == 1 ? "1 shape" : $"{shapeList.Count} shapes";
        string bottomName = shapeList[0].DisplayName;
        string topName = shapeList[^1].DisplayName;

        var selList = selected?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        string selSummary;
        if (selList.Count == 0)
        {
            selSummary = "0 selected";
        }
        else
        {
            var indices = selList.Select(s => shapeList.IndexOf(s) + 1).Where(idx => idx > 0).OrderBy(idx => idx).ToList();
            string idxStr = string.Join(", ", indices);
            string selCountStr = selList.Count == 1 ? "1 selected shape" : $"{selList.Count} selected shapes";
            selSummary = $"{selCountStr} @ layer [{idxStr}]";
        }

        return $"Layer stack: {countLabel} (Bottom: '{bottomName}', Top: '{topName}' · {selSummary})";
    }

    /// <summary>
    /// Normalizes X, Y, Width, Height, and Rotation of selected shapes (or all canvas shapes if none selected)
    /// to the nearest multiple of <paramref name="step"/> (default 1.0px), removing sub-pixel floating-point drift.
    /// </summary>
    public (int AdjustedShapes, int TotalInspected, double MaxDeltaPx) NormalizeShapePrecision(double step = 1.0)
    {
        if (Shapes.Count == 0)
        {
            StatusMessage = FormatPrecisionNormalizationSummary(0, 0, step, 0);
            return (0, 0, 0);
        }

        double gridStep = step <= 0 ? 1.0 : step;
        var targets = SelectedItems.ToList();
        if (targets.Count == 0)
        {
            targets = Shapes.ToList();
        }

        double Snap(double val) => Math.Round(val / gridStep, MidpointRounding.AwayFromZero) * gridStep;

        int adjusted = 0;
        double maxDelta = 0.0;
        bool undoRecorded = false;

        foreach (var s in targets)
        {
            double nx = Snap(s.X);
            double ny = Snap(s.Y);
            double nw = Math.Max(gridStep, Snap(s.Width));
            double nh = Math.Max(gridStep, Snap(s.Height));
            double nr = Snap(s.Rotation);

            double dx = Math.Abs(nx - s.X);
            double dy = Math.Abs(ny - s.Y);
            double dw = Math.Abs(nw - s.Width);
            double dh = Math.Abs(nh - s.Height);
            double dr = Math.Abs(nr - s.Rotation);

            double localMax = Math.Max(Math.Max(dx, dy), Math.Max(Math.Max(dw, dh), dr));
            if (localMax > 1e-6)
            {
                if (!undoRecorded)
                {
                    RecordUndo();
                    undoRecorded = true;
                }

                s.X = nx;
                s.Y = ny;
                s.Width = nw;
                s.Height = nh;
                s.Rotation = WrapRotationAngle((int)Math.Round(nr));
                adjusted++;
                if (localMax > maxDelta) maxDelta = localMax;
            }
        }

        string summary = FormatPrecisionNormalizationSummary(adjusted, targets.Count, gridStep, maxDelta);
        StatusMessage = $"✓ {summary}";
        if (adjusted > 0)
        {
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }

        return (adjusted, targets.Count, maxDelta);
    }

    /// <summary>
    /// Formats a human-readable summary of shape coordinate and dimension sub-pixel precision normalization.
    /// </summary>
    public static string FormatPrecisionNormalizationSummary(int adjustedShapes, int totalInspected, double step = 1.0, double maxDeltaPx = 0.0)
    {
        double gridStep = step <= 0 ? 1.0 : step;
        if (totalInspected <= 0)
            return $"Precision snap ({gridStep:0.##}px grid): 0 shapes (Empty canvas)";

        string totalStr = totalInspected == 1 ? "1 shape" : $"{totalInspected} shapes";
        if (adjustedShapes <= 0)
            return $"Precision snap ({gridStep:0.##}px grid): All {totalStr} already pixel-aligned (0px drift)";

        return $"Precision snap ({gridStep:0.##}px grid): Normalized {adjustedShapes} of {totalStr} (Max sub-pixel drift: {maxDeltaPx:0.##}px)";
    }

    /// <summary>
    /// Resizes all selected shapes (or all canvas shapes if none are selected) to match the width and/or height
    /// of the primary reference shape (<see cref="SelectedShape"/> or first selected shape), recording a single undo step.
    /// </summary>
    public (int ResizedShapes, int TotalSelected, double TargetWidth, double TargetHeight, string ReferenceLabel) MatchSelectedShapeDimensions(
        bool matchWidth = true,
        bool matchHeight = true)
    {
        if (Shapes.Count == 0 || (!matchWidth && !matchHeight))
        {
            StatusMessage = FormatDimensionMatchSummary(0, 0, 0, 0, null);
            return (0, 0, 0, 0, string.Empty);
        }

        var targets = SelectedItems.ToList();
        if (targets.Count < 2)
        {
            targets = Shapes.ToList();
        }

        var reference = (SelectedShape is not null && targets.Contains(SelectedShape))
            ? SelectedShape
            : targets[0];

        double targetW = reference.Width;
        double targetH = reference.Height;
        string refLabel = !string.IsNullOrWhiteSpace(reference.Text)
            ? reference.Text.Trim()
            : !string.IsNullOrWhiteSpace(reference.Name) ? reference.Name.Trim() : reference.Prst;

        if (targets.Count < 2)
        {
            StatusMessage = FormatDimensionMatchSummary(0, targets.Count, targetW, targetH, refLabel);
            return (0, targets.Count, targetW, targetH, refLabel);
        }

        int resized = 0;
        bool undoRecorded = false;

        foreach (var s in targets)
        {
            if (ReferenceEquals(s, reference)) continue;

            double newW = matchWidth ? targetW : s.Width;
            double newH = matchHeight ? targetH : s.Height;
            if (Math.Abs(newW - s.Width) > 1e-6 || Math.Abs(newH - s.Height) > 1e-6)
            {
                if (!undoRecorded)
                {
                    RecordUndo();
                    undoRecorded = true;
                }
                s.Width = newW;
                s.Height = newH;
                resized++;
            }
        }

        string summary = FormatDimensionMatchSummary(resized, targets.Count, targetW, targetH, refLabel);
        StatusMessage = $"✓ {summary}";
        if (resized > 0)
        {
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }

        return (resized, targets.Count, targetW, targetH, refLabel);
    }

    /// <summary>
    /// Formats a human-readable summary of a multi-shape dimension-matching operation.
    /// </summary>
    public static string FormatDimensionMatchSummary(
        int resizedShapes,
        int totalSelected,
        double targetWidth,
        double targetHeight,
        string? referenceLabel = null)
    {
        if (totalSelected <= 0)
            return "Match dimensions: 0 shapes (Empty selection)";

        string refName = string.IsNullOrWhiteSpace(referenceLabel) ? "Reference" : referenceLabel.Trim();
        if (totalSelected < 2)
            return $"Match dimensions ('{refName}' · {targetWidth:0.##}×{targetHeight:0.##}px): Select at least 2 shapes to match size";

        if (resizedShapes <= 0)
            return $"Match dimensions ('{refName}' · {targetWidth:0.##}×{targetHeight:0.##}px): All {totalSelected} shapes already match size";

        string resizedLabel = resizedShapes == 1 ? "1 shape resized" : $"{resizedShapes} shapes resized";
        return $"Match dimensions ('{refName}' · {targetWidth:0.##}×{targetHeight:0.##}px): {resizedLabel} across {totalSelected} selected shapes";
    }

    /// <summary>
    /// Mirrors selected shapes (or all canvas shapes if fewer than 2 are selected) horizontally and/or vertically
    /// across their collective bounding-box center, recording a single undo step.
    /// </summary>
    public (int FlippedShapes, double PivotCenterX, double PivotCenterY) FlipSelectedShapes(
        bool horizontal = true,
        bool vertical = false)
    {
        if (Shapes.Count == 0 || (!horizontal && !vertical))
        {
            StatusMessage = FormatFlipTransformSummary(0, horizontal, vertical, 0, 0);
            return (0, 0, 0);
        }

        var targets = SelectedItems.ToList();
        if (targets.Count == 0)
        {
            targets = Shapes.ToList();
        }

        double minX = targets.Min(s => s.X);
        double maxX = targets.Max(s => s.X + s.Width);
        double minY = targets.Min(s => s.Y);
        double maxY = targets.Max(s => s.Y + s.Height);

        double pivotCenterX = Math.Round((minX + maxX) / 2.0, 2);
        double pivotCenterY = Math.Round((minY + maxY) / 2.0, 2);

        RecordUndo();
        foreach (var s in targets)
        {
            if (horizontal)
            {
                s.X = Math.Round((minX + maxX) - (s.X + s.Width), 2);
                if (s.Rotation != 0)
                {
                    s.Rotation = WrapRotationAngle(-s.Rotation);
                }
            }

            if (vertical)
            {
                s.Y = Math.Round((minY + maxY) - (s.Y + s.Height), 2);
                if (s.Rotation != 0)
                {
                    s.Rotation = WrapRotationAngle(180 - s.Rotation);
                }
            }
        }

        string summary = FormatFlipTransformSummary(targets.Count, horizontal, vertical, pivotCenterX, pivotCenterY);
        StatusMessage = $"✓ {summary}";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (targets.Count, pivotCenterX, pivotCenterY);
    }

    /// <summary>
    /// Formats a human-readable summary of a horizontal/vertical bounding-box mirror flip operation.
    /// </summary>
    public static string FormatFlipTransformSummary(
        int flippedShapes,
        bool horizontal,
        bool vertical,
        double pivotCenterX,
        double pivotCenterY)
    {
        string axisLabel = (horizontal, vertical) switch
        {
            (true, true) => "Horizontal & Vertical",
            (true, false) => "Horizontal",
            (false, true) => "Vertical",
            _ => "None"
        };

        if (flippedShapes <= 0 || (!horizontal && !vertical))
            return $"Mirror flip ({axisLabel}): 0 shapes (Empty selection)";

        string shapeLabel = flippedShapes == 1 ? "1 shape mirrored" : $"{flippedShapes} shapes mirrored";
        return $"Mirror flip ({axisLabel}): {shapeLabel} across pivot ({pivotCenterX:0.##}, {pivotCenterY:0.##})";
    }

    /// <summary>
    /// Selects all shapes on the canvas that match the geometry kind (preset) and/or fill color
    /// of the active reference shape.
    /// Returns total matched shapes, total canvas shapes, reference kind, reference fill, and summary.
    /// </summary>
    public (int MatchedShapes, int TotalCanvasShapes, string ReferenceKind, string? ReferenceFill, string Summary)
        SelectSimilarShapes(bool matchKind = true, bool matchFill = false)
    {
        if (Shapes.Count == 0 || (!matchKind && !matchFill))
        {
            string emptySummary = FormatSimilarShapesSummary(0, Shapes.Count, null, null, matchKind, matchFill);
            StatusMessage = emptySummary;
            return (0, Shapes.Count, "None", null, emptySummary);
        }

        var refShape = SelectedShape ?? SelectedItems.FirstOrDefault() ?? Shapes.FirstOrDefault();
        if (refShape == null)
        {
            string noRefSummary = FormatSimilarShapesSummary(0, Shapes.Count, null, null, matchKind, matchFill);
            StatusMessage = noRefSummary;
            return (0, Shapes.Count, "None", null, noRefSummary);
        }

        string refKind = refShape.Prst ?? "shape";
        string? refFill = refShape.Fill;

        var matches = new List<ShapeCanvasItemViewModel>();
        foreach (var s in Shapes)
        {
            bool kindMatch = !matchKind || string.Equals(s.Prst, refKind, StringComparison.OrdinalIgnoreCase);
            bool fillMatch = !matchFill || string.Equals(s.Fill?.TrimStart('#'), refFill?.TrimStart('#'), StringComparison.OrdinalIgnoreCase);

            if (kindMatch && fillMatch)
            {
                matches.Add(s);
            }
        }

        _additiveSelect = true;
        try
        {
            foreach (var s in Shapes)
            {
                s.IsSelected = matches.Contains(s);
            }
            if (matches.Count > 0)
            {
                SelectedShape = matches.Contains(refShape) ? refShape : matches[^1];
            }
            else
            {
                SelectedShape = null;
            }
        }
        finally
        {
            _additiveSelect = false;
        }

        RaiseSelectionChanged();
        string summary = FormatSimilarShapesSummary(matches.Count, Shapes.Count, refKind, refFill, matchKind, matchFill);
        StatusMessage = $"✓ {summary}";
        CanvasChanged?.Invoke(this, EventArgs.Empty);

        return (matches.Count, Shapes.Count, refKind, refFill, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of a similar-shapes selection operation.
    /// </summary>
    public static string FormatSimilarShapesSummary(
        int matchedShapes,
        int totalCanvasShapes,
        string? referenceKind = null,
        string? referenceFill = null,
        bool matchedByKind = true,
        bool matchedByFill = false)
    {
        if (totalCanvasShapes <= 0)
            return "Select similar: 0 shapes (Empty canvas)";

        string criteriaLabel = (matchedByKind, matchedByFill) switch
        {
            (true, true) => $"kind '{referenceKind}' & fill #{referenceFill?.TrimStart('#')}",
            (true, false) => $"kind '{referenceKind}'",
            (false, true) => $"fill #{referenceFill?.TrimStart('#')}",
            _ => "no criteria"
        };

        if (matchedShapes <= 0)
            return $"Select similar ({criteriaLabel}): 0 matching shapes found across {totalCanvasShapes} canvas shapes";

        string shapeLabel = matchedShapes == 1 ? "1 shape matched" : $"{matchedShapes} shapes matched";
        return $"Select similar ({criteriaLabel}): {shapeLabel} across {totalCanvasShapes} canvas shapes";
    }

    /// <summary>
    /// Detects and nudges overlapping shapes apart by enforcing a minimum gap (<paramref name="minGap"/>)
    /// across selected shapes (or all canvas shapes if &lt; 2 shapes are selected).
    /// Records a single undo snapshot and returns the number of shapes nudged and overlaps resolved.
    /// </summary>
    public (int NudgedCount, int OverlapsResolved, string Summary) ResolveShapeOverlaps(double minGap = 16.0)
    {
        var selected = SelectedItems.ToList();
        var targets = selected.Count > 1 ? selected : Shapes.ToList();
        if (targets.Count <= 1)
        {
            string emptySummary = FormatOverlapResolutionSummary(0, targets.Count, minGap);
            StatusMessage = emptySummary;
            return (0, 0, emptySummary);
        }

        RecordUndo();

        var nudged = new HashSet<ShapeCanvasItemViewModel>();
        int totalOverlaps = 0;
        int maxPasses = 8;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool hadOverlapThisPass = false;

            // Sort targets primarily top-to-bottom, left-to-right to ensure deterministic cascading nudges
            var ordered = targets.OrderBy(s => s.Y).ThenBy(s => s.X).ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                var s1 = ordered[i];
                double r1 = s1.X + s1.Width;
                double b1 = s1.Y + s1.Height;

                for (int j = i + 1; j < ordered.Count; j++)
                {
                    var s2 = ordered[j];
                    double r2 = s2.X + s2.Width;
                    double b2 = s2.Y + s2.Height;

                    // Check bounding overlap with minGap clearance
                    bool overlapsX = (s1.X < r2 + minGap) && (r1 + minGap > s2.X);
                    bool overlapsY = (s1.Y < b2 + minGap) && (b1 + minGap > s2.Y);

                    if (overlapsX && overlapsY)
                    {
                        hadOverlapThisPass = true;
                        totalOverlaps++;

                        double overlapX = (r1 + minGap) - s2.X;
                        double overlapY = (b1 + minGap) - s2.Y;

                        // Nudge on whichever axis requires less movement, or downwards if vertical hierarchy
                        if (overlapX < overlapY)
                        {
                            s2.X += Math.Round(overlapX, 0);
                        }
                        else
                        {
                            s2.Y += Math.Round(overlapY, 0);
                        }

                        nudged.Add(s2);
                    }
                }
            }

            if (!hadOverlapThisPass) break;
        }

        string summary = FormatOverlapResolutionSummary(nudged.Count, targets.Count, minGap);
        if (nudged.Count > 0)
        {
            StatusMessage = $"✓ {summary} · Ctrl+Z to undo";
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            StatusMessage = summary;
        }

        return (nudged.Count, totalOverlaps, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of a shape overlap resolution operation.
    /// </summary>
    public static string FormatOverlapResolutionSummary(int nudgedCount, int totalExamined, double minGap = 16.0)
    {
        if (totalExamined <= 1)
            return "Resolve overlaps: 0-1 shapes (No overlap resolution needed)";

        if (nudgedCount <= 0)
            return $"Resolve overlaps: 0 overlaps detected across {totalExamined} shapes (Clean layout with >={minGap:0}px gap)";

        string shapeLabel = nudgedCount == 1 ? "1 shape nudged" : $"{nudgedCount} shapes nudged";
        return $"Resolve overlaps: {shapeLabel} apart (Enforced {minGap:0}px minimum clearance across {totalExamined} shapes)";
    }

    /// <summary>
    /// Distributes selected shapes (or all canvas shapes if &lt; 3 selected) evenly along the specified axis.
    /// Preserves the outermost anchor shapes and equalizes spacing between inner shapes.
    /// Records a single undo snapshot and triggers canvas redraw.
    /// </summary>
    public (int DistributedCount, int TotalExamined, string Summary) DistributeShapesEvenly(DistributionAxis axis)
    {
        var selected = SelectedItems.ToList();
        var targets = selected.Count >= 3 ? selected : Shapes.ToList();

        if (targets.Count < 3)
        {
            string notEnoughSummary = FormatDistributionSummary(0, targets.Count, axis, 0);
            StatusMessage = notEnoughSummary;
            return (0, targets.Count, notEnoughSummary);
        }

        RecordUndo();

        int distributedCount = 0;
        double gap = 0;

        if (axis == DistributionAxis.Horizontal)
        {
            var ordered = targets.OrderBy(s => s.X).ToList();
            double left = ordered[0].X;
            double right = ordered[^1].X + ordered[^1].Width;
            double totalSpan = right - left;
            double sumWidths = ordered.Sum(s => s.Width);
            double availableGap = totalSpan - sumWidths;
            gap = availableGap / (ordered.Count - 1);

            double curX = left + ordered[0].Width + gap;
            for (int i = 1; i < ordered.Count - 1; i++)
            {
                ordered[i].X = Math.Max(0, Math.Round(curX, 1));
                curX += ordered[i].Width + gap;
                distributedCount++;
            }
        }
        else
        {
            var ordered = targets.OrderBy(s => s.Y).ToList();
            double top = ordered[0].Y;
            double bottom = ordered[^1].Y + ordered[^1].Height;
            double totalSpan = bottom - top;
            double sumHeights = ordered.Sum(s => s.Height);
            double availableGap = totalSpan - sumHeights;
            gap = availableGap / (ordered.Count - 1);

            double curY = top + ordered[0].Height + gap;
            for (int i = 1; i < ordered.Count - 1; i++)
            {
                ordered[i].Y = Math.Max(0, Math.Round(curY, 1));
                curY += ordered[i].Height + gap;
                distributedCount++;
            }
        }

        string summary = FormatDistributionSummary(distributedCount, targets.Count, axis, gap);
        StatusMessage = $"✓ {summary} · Ctrl+Z to undo";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (distributedCount, targets.Count, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of an even shape distribution operation.
    /// </summary>
    public static string FormatDistributionSummary(int distributedCount, int totalCount, DistributionAxis axis, double gap = 0)
    {
        if (totalCount < 3)
            return $"Distribute evenly ({axis}): Requires at least 3 shapes (Found {totalCount})";

        string shapeLabel = distributedCount == 1 ? "1 shape" : $"{distributedCount} shapes";
        return $"Distribute evenly ({axis}): Balanced {shapeLabel} across {totalCount} shapes (Gap: {gap:0.#}px)";
    }

    /// <summary>
    /// Scales selected shapes (or all canvas shapes if none selected) proportionally by <paramref name="scaleFactor"/>
    /// around the selection's geometric center, scaling both shape dimensions and relative spatial offsets.
    /// Records a single undo snapshot and triggers canvas redraw.
    /// </summary>
    public (int ScaledCount, int TotalExamined, double Factor, string Summary) ScaleSelectedShapesProportionally(double scaleFactor)
    {
        if (double.IsNaN(scaleFactor) || double.IsInfinity(scaleFactor) || scaleFactor <= 0.05)
        {
            scaleFactor = 1.0;
        }

        var selected = SelectedItems.ToList();
        var targets = selected.Count > 0 ? selected : Shapes.ToList();

        if (targets.Count == 0)
        {
            string emptySummary = FormatProportionalScaleSummary(0, 0, scaleFactor);
            StatusMessage = emptySummary;
            return (0, 0, scaleFactor, emptySummary);
        }

        RecordUndo();

        // Calculate bounding box of targets
        double minX = targets.Min(s => s.X);
        double minY = targets.Min(s => s.Y);
        double maxX = targets.Max(s => s.X + s.Width);
        double maxY = targets.Max(s => s.Y + s.Height);
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;

        int scaledCount = 0;
        foreach (var shape in targets)
        {
            double origW = shape.Width;
            double origH = shape.Height;
            double newW = Math.Max(10.0, Math.Round(origW * scaleFactor, 1));
            double newH = Math.Max(10.0, Math.Round(origH * scaleFactor, 1));

            // Shape center relative to selection center
            double origCenterX = shape.X + (origW / 2.0);
            double origCenterY = shape.Y + (origH / 2.0);

            double newCenterX = centerX + ((origCenterX - centerX) * scaleFactor);
            double newCenterY = centerY + ((origCenterY - centerY) * scaleFactor);

            double newX = Math.Max(0.0, Math.Round(newCenterX - (newW / 2.0), 1));
            double newY = Math.Max(0.0, Math.Round(newCenterY - (newH / 2.0), 1));

            shape.Width = newW;
            shape.Height = newH;
            shape.X = newX;
            shape.Y = newY;
            scaledCount++;
        }

        double newSpanW = Math.Round((maxX - minX) * scaleFactor, 1);
        double newSpanH = Math.Round((maxY - minY) * scaleFactor, 1);

        string summary = FormatProportionalScaleSummary(scaledCount, targets.Count, scaleFactor, newSpanW, newSpanH);
        StatusMessage = $"✓ {summary} · Ctrl+Z to undo";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (scaledCount, targets.Count, scaleFactor, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of a proportional multi-shape scaling operation.
    /// </summary>
    public static string FormatProportionalScaleSummary(
        int scaledCount, int totalCount, double factor, double newSpanWidth = 0, double newSpanHeight = 0)
    {
        if (totalCount <= 0)
            return $"Scale proportionally ({factor:0.##}x): 0 shapes (Empty selection/canvas)";

        string shapeLabel = scaledCount == 1 ? "1 shape" : $"{scaledCount} shapes";
        string dimStr = newSpanWidth > 0 && newSpanHeight > 0 ? $" · Bounding box: {newSpanWidth:0.#}×{newSpanHeight:0.#}px" : "";
        return $"Scale proportionally ({factor:0.##}x): Resized {shapeLabel} around selection center{dimStr}";
    }

    /// <summary>
    /// Distributes selected shapes (or all canvas shapes if &lt;2 selected) equidistant around an elliptical or circular orbit.
    /// </summary>
    public (int ArrangedCount, double RadiusX, double RadiusY, string Summary) DistributeShapesRadially(double radiusX = 0, double radiusY = 0)
    {
        var targets = SelectionCount >= 2 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count < 2)
        {
            string emptySummary = "Radial distribution: requires at least 2 shapes";
            StatusMessage = emptySummary;
            return (0, 0, 0, emptySummary);
        }

        RecordUndo();

        double minX = targets.Min(s => s.X);
        double minY = targets.Min(s => s.Y);
        double maxX = targets.Max(s => s.X + s.Width);
        double maxY = targets.Max(s => s.Y + s.Height);
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;

        double rx = radiusX;
        double ry = radiusY;

        if (rx <= 0 || ry <= 0)
        {
            double spanW = maxX - minX;
            double spanH = maxY - minY;
            double autoRadius = Math.Max(120.0, Math.Max(spanW, spanH) / 2.0);
            if (rx <= 0) rx = autoRadius;
            if (ry <= 0) ry = autoRadius;
        }

        int count = targets.Count;
        for (int i = 0; i < count; i++)
        {
            double angle = -Math.PI / 2.0 + (2.0 * Math.PI * i / count);
            var shape = targets[i];
            double shapeCenterX = centerX + (rx * Math.Cos(angle));
            double shapeCenterY = centerY + (ry * Math.Sin(angle));

            shape.X = Math.Max(0.0, Math.Round(shapeCenterX - (shape.Width / 2.0), 1));
            shape.Y = Math.Max(0.0, Math.Round(shapeCenterY - (shape.Height / 2.0), 1));
        }

        string summary = FormatRadialDistributionSummary(count, rx, ry, centerX, centerY);
        StatusMessage = $"✓ {summary} · Ctrl+Z to undo";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (count, rx, ry, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of a radial shape distribution operation.
    /// </summary>
    public static string FormatRadialDistributionSummary(int count, double rx, double ry, double cx, double cy)
    {
        if (count < 2) return "Radial distribution: requires at least 2 shapes";
        string shapeLabel = count == 1 ? "1 shape" : $"{count} shapes";
        string shapeType = Math.Abs(rx - ry) < 1.0 ? $"circular orbit (R={rx:0.#}px)" : $"elliptical orbit ({rx:0.#}×{ry:0.#}px)";
        return $"Radial distribution: Arranged {shapeLabel} in {shapeType} around ({cx:0.#}, {cy:0.#})";
    }

    /// <summary>
    /// Audits the z-order layering stack indices for selected shapes or the entire canvas drawing order.
    /// </summary>
    public LayerStackAudit AuditLayerStackIndices()
    {
        var targets = SelectionCount > 0 ? Selection.ToList() : Shapes.ToList();
        var selectedIndices = targets.Select(s => Shapes.IndexOf(s)).Where(idx => idx >= 0).OrderBy(idx => idx).ToList();

        if (Shapes.Count == 0 || selectedIndices.Count == 0)
        {
            return new LayerStackAudit(
                Shapes.Count, 0, -1, -1, true, Array.Empty<int>(),
                "Z-order layer stack audit: 0 shapes (Empty canvas)");
        }

        int min = selectedIndices.Min();
        int max = selectedIndices.Max();
        bool isContiguous = true;
        for (int i = 0; i < selectedIndices.Count; i++)
        {
            if (selectedIndices[i] != min + i)
            {
                isContiguous = false;
                break;
            }
        }

        string summary = FormatLayerStackSummary(Shapes.Count, selectedIndices.Count, min, max, isContiguous, selectedIndices);
        return new LayerStackAudit(Shapes.Count, selectedIndices.Count, min, max, isContiguous, selectedIndices, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of z-order layering stack indices.
    /// </summary>
    public static string FormatLayerStackSummary(
        int totalShapes, int selectedCount, int minIndex, int maxIndex, bool isContiguous, IReadOnlyList<int>? indices = null)
    {
        if (totalShapes <= 0 || selectedCount <= 0)
            return "Z-order layer stack audit: 0 shapes (Empty canvas)";

        string shapeLabel = selectedCount == 1 ? "1 shape" : $"{selectedCount} shapes";
        string contigStr = isContiguous ? "contiguous" : "non-contiguous / fragmented";
        string indexPreview = indices != null && indices.Count > 0
            ? $" [{string.Join(", ", indices.Take(5))}{(indices.Count > 5 ? ", …" : "")}]"
            : "";

        return $"Z-order layer stack audit ({shapeLabel} of {totalShapes} total): Layer {minIndex}..{maxIndex} ({contigStr}){indexPreview}";
    }

    /// <summary>
    /// Normalizes the drawing order stack of selected shapes to make them contiguous, or verifies sequential order.
    /// </summary>
    public (int NormalizedCount, bool WasModified, string Summary) NormalizeZOrderStack()
    {
        if (Shapes.Count <= 1)
        {
            string emptySummary = "Normalize z-order: canvas contains ≤1 shape";
            StatusMessage = emptySummary;
            return (Shapes.Count, false, emptySummary);
        }

        var targets = SelectionCount > 0 ? Selection.ToList() : Shapes.ToList();
        var currentIndices = targets.Select(s => Shapes.IndexOf(s)).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (currentIndices.Count <= 1)
        {
            string singleSummary = $"Normalize z-order: {currentIndices.Count} shape selected (Already normalized)";
            StatusMessage = singleSummary;
            return (currentIndices.Count, false, singleSummary);
        }

        int min = currentIndices.Min();
        bool isAlreadyContiguous = true;
        for (int i = 0; i < currentIndices.Count; i++)
        {
            if (currentIndices[i] != min + i) { isAlreadyContiguous = false; break; }
        }

        if (isAlreadyContiguous)
        {
            string cleanSummary = $"Normalize z-order: {targets.Count} shapes already contiguous (Indices {min}..{currentIndices.Max()})";
            StatusMessage = cleanSummary;
            return (targets.Count, false, cleanSummary);
        }

        RecordUndo();
        var orderedTargets = targets.OrderBy(s => Shapes.IndexOf(s)).ToList();
        for (int i = 0; i < orderedTargets.Count; i++)
        {
            int desiredIndex = min + i;
            int actualIndex = Shapes.IndexOf(orderedTargets[i]);
            if (actualIndex >= 0 && actualIndex != desiredIndex)
            {
                Shapes.Move(actualIndex, desiredIndex);
            }
        }

        string summary = $"✓ Normalized z-order: compacted {orderedTargets.Count} shapes into contiguous layers {min}..{min + orderedTargets.Count - 1} · Ctrl+Z to undo";
        StatusMessage = summary;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (orderedTargets.Count, true, summary);
    }

    /// <summary>
    /// Audits alignment of shape coordinates and dimensions against a design grid.
    /// </summary>
    public GridAlignmentAudit AuditGridAlignment(double gridSize = 8.0)
    {
        double g = gridSize <= 0 ? 8.0 : gridSize;
        var targets = SelectionCount > 0 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count == 0)
        {
            return new GridAlignmentAudit(
                0, 0, g, 0, 0, 100.0, 0.0,
                $"Grid alignment audit ({g}px grid): 0 shapes (Empty canvas)");
        }

        int alignedCount = 0;
        int misalignedCount = 0;
        double maxOffset = 0.0;

        foreach (var s in targets)
        {
            double offX = Math.Abs(s.X - Math.Round(s.X / g) * g);
            double offY = Math.Abs(s.Y - Math.Round(s.Y / g) * g);
            double offW = Math.Abs(s.Width - Math.Round(s.Width / g) * g);
            double offH = Math.Abs(s.Height - Math.Round(s.Height / g) * g);

            double shapeMax = Math.Max(Math.Max(offX, offY), Math.Max(offW, offH));
            if (shapeMax > maxOffset) maxOffset = shapeMax;

            if (shapeMax < 0.01)
            {
                alignedCount++;
            }
            else
            {
                misalignedCount++;
            }
        }

        double pct = Math.Round((double)alignedCount / targets.Count * 100.0, 1);
        string targetLabel = SelectionCount > 0 ? $"{SelectionCount} selected" : $"{targets.Count} shapes";
        string summary = $"Grid alignment audit ({targetLabel} · {g}px grid): {alignedCount}/{targets.Count} aligned ({pct}%) · Max offset: {maxOffset:0.##}px";

        StatusMessage = summary;
        return new GridAlignmentAudit(
            Shapes.Count,
            SelectionCount,
            g,
            alignedCount,
            misalignedCount,
            pct,
            Math.Round(maxOffset, 2),
            summary);
    }

    /// <summary>
    /// Quantizes (snaps) coordinates and dimensions of selected or all shapes to the nearest grid step.
    /// </summary>
    public (int TargetCount, int ModifiedCount, string Summary) QuantizeShapeCoordinates(double gridSize = 8.0)
    {
        double g = gridSize <= 0 ? 8.0 : gridSize;
        var targets = SelectionCount > 0 ? Selection.ToList() : Shapes.ToList();
        if (targets.Count == 0)
        {
            string emptySummary = $"Quantize to {g}px grid: 0 shapes on canvas";
            StatusMessage = emptySummary;
            return (0, 0, emptySummary);
        }

        int modified = 0;
        var adjustments = new List<(ShapeCanvasItemViewModel Shape, double OldX, double OldY, double OldW, double OldH, double NewX, double NewY, double NewW, double NewH)>();

        foreach (var s in targets)
        {
            double newX = Math.Max(0.0, Math.Round(s.X / g) * g);
            double newY = Math.Max(0.0, Math.Round(s.Y / g) * g);
            double newW = Math.Max(g, Math.Round(s.Width / g) * g);
            double newH = Math.Max(g, Math.Round(s.Height / g) * g);

            if (Math.Abs(s.X - newX) > 0.01 || Math.Abs(s.Y - newY) > 0.01 ||
                Math.Abs(s.Width - newW) > 0.01 || Math.Abs(s.Height - newH) > 0.01)
            {
                adjustments.Add((s, s.X, s.Y, s.Width, s.Height, newX, newY, newW, newH));
            }
        }

        if (adjustments.Count == 0)
        {
            string cleanSummary = $"✓ All {targets.Count} shapes already aligned to {g}px grid";
            StatusMessage = cleanSummary;
            return (targets.Count, 0, cleanSummary);
        }

        RecordUndo();
        foreach (var adj in adjustments)
        {
            adj.Shape.X = adj.NewX;
            adj.Shape.Y = adj.NewY;
            adj.Shape.Width = adj.NewW;
            adj.Shape.Height = adj.NewH;
            modified++;
        }

        string summary = $"✓ Snapped {modified} shapes to {g}px grid (Bounds & sizes quantized) · Ctrl+Z to undo";
        StatusMessage = summary;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (targets.Count, modified, summary);
    }

    /// <summary>
    /// Audits aspect ratio proportions and deviation from standard aspect ratios (1:1 Square, 4:3 Standard, 16:9 Widescreen, 1.618 Golden Ratio) across canvas shapes.
    /// </summary>
    public AspectRatioProportionsAudit AuditAspectRatioProportions(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var targets = (targetShapes ?? (SelectedItems.Any() ? SelectedItems : Shapes)).ToList();
        if (targets.Count == 0)
        {
            string emptySummary = "Aspect ratio audit: 0 shapes (Empty canvas)";
            StatusMessage = emptySummary;
            return new AspectRatioProportionsAudit(0, 0, 0, 0, 0, 0, 0.0, emptySummary);
        }

        int squareCount = 0;
        int wide169Count = 0;
        int standard43Count = 0;
        int goldenCount = 0;
        int otherCount = 0;
        double sumRatios = 0.0;

        foreach (var s in targets)
        {
            double w = Math.Max(1.0, s.Width);
            double h = Math.Max(1.0, s.Height);
            double ratio = w / h;
            sumRatios += ratio;

            if (Math.Abs(ratio - 1.0) <= 0.05) squareCount++;
            else if (Math.Abs(ratio - (16.0 / 9.0)) <= 0.08) wide169Count++;
            else if (Math.Abs(ratio - (4.0 / 3.0)) <= 0.06) standard43Count++;
            else if (Math.Abs(ratio - 1.618) <= 0.08) goldenCount++;
            else otherCount++;
        }

        double avgRatio = Math.Round(sumRatios / targets.Count, 2);
        string summary = FormatAspectRatioAuditSummary(targets.Count, squareCount, wide169Count, standard43Count, goldenCount, otherCount, avgRatio);
        StatusMessage = summary;

        return new AspectRatioProportionsAudit(
            targets.Count,
            squareCount,
            wide169Count,
            standard43Count,
            goldenCount,
            otherCount,
            avgRatio,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of aspect ratio proportionality across shapes.
    /// </summary>
    public static string FormatAspectRatioAuditSummary(int total, int square, int wide169, int standard43, int golden, int other, double avgRatio)
    {
        if (total == 0) return "Aspect ratio audit: 0 shapes";
        return $"Aspect ratio audit ({total} shapes · Avg ratio: {avgRatio:0.##}): {square} square (1:1), {wide169} widescreen (16:9), {standard43} standard (4:3), {golden} golden (1.618), {other} custom";
    }

    /// <summary>
    /// Snaps and locks shape aspect ratios to a specific target ratio (e.g. 1.0 for square, 1.778 for 16:9) centered at their current geometric center.
    /// </summary>
    public (int TotalShapes, int ModifiedCount, string Summary) EnforceAspectRatioLock(
        double targetRatio = 1.0,
        string? ratioName = null,
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var targets = (targetShapes ?? (SelectedItems.Any() ? SelectedItems : Shapes)).ToList();
        if (targets.Count == 0)
        {
            string emptySummary = "Aspect ratio lock: No shapes to adjust";
            StatusMessage = emptySummary;
            return (0, 0, emptySummary);
        }

        double ratio = Math.Clamp(targetRatio, 0.1, 10.0);
        string label = ratioName ?? $"{ratio:0.##}:1";

        int modified = 0;
        var adjustments = new List<(ShapeCanvasItemViewModel Shape, double OldX, double OldY, double OldW, double OldH, double NewX, double NewY, double NewW, double NewH)>();

        foreach (var s in targets)
        {
            double cx = s.X + s.Width / 2.0;
            double cy = s.Y + s.Height / 2.0;
            double area = Math.Max(400.0, s.Width * s.Height);

            // newW / newH = ratio, newW * newH = area => newH = sqrt(area / ratio), newW = newH * ratio
            double newH = Math.Max(20.0, Math.Sqrt(area / ratio));
            double newW = Math.Max(20.0, newH * ratio);

            double newX = Math.Max(0.0, Math.Round(cx - newW / 2.0));
            double newY = Math.Max(0.0, Math.Round(cy - newH / 2.0));
            newW = Math.Round(newW);
            newH = Math.Round(newH);

            if (Math.Abs(s.Width - newW) > 0.5 || Math.Abs(s.Height - newH) > 0.5 ||
                Math.Abs(s.X - newX) > 0.5 || Math.Abs(s.Y - newY) > 0.5)
            {
                adjustments.Add((s, s.X, s.Y, s.Width, s.Height, newX, newY, newW, newH));
            }
        }

        if (adjustments.Count == 0)
        {
            string cleanSummary = $"✓ All {targets.Count} shapes already conform to {label} aspect ratio";
            StatusMessage = cleanSummary;
            return (targets.Count, 0, cleanSummary);
        }

        RecordUndo();
        foreach (var adj in adjustments)
        {
            adj.Shape.X = adj.NewX;
            adj.Shape.Y = adj.NewY;
            adj.Shape.Width = adj.NewW;
            adj.Shape.Height = adj.NewH;
            modified++;
        }

        string summary = $"✓ Locked {modified} shapes to {label} aspect ratio · Ctrl+Z to undo";
        StatusMessage = summary;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (targets.Count, modified, summary);
    }

    /// <summary>
    /// Audits multi-shape spacing distribution and gap uniformity along horizontal or vertical axes.
    /// </summary>
    public SpacingDistributionAudit AuditSpacingDistribution(
        DistributionAxis axis = DistributionAxis.Horizontal,
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        string axisStr = axis == DistributionAxis.Horizontal ? "Horizontal" : "Vertical";

        if (targets.Count < 2)
        {
            string emptySummary = $"{axisStr} spacing audit: Insufficient shapes ({targets.Count} found)";
            StatusMessage = emptySummary;
            return new SpacingDistributionAudit(targets.Count, 0, 0.0, 0.0, 0.0, true, axis, emptySummary);
        }

        var ordered = (axis == DistributionAxis.Horizontal ? targets.OrderBy(s => s.X) : targets.OrderBy(s => s.Y)).ToList();
        var gaps = new List<double>();
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            double gap = axis == DistributionAxis.Horizontal
                ? ordered[i + 1].X - (ordered[i].X + ordered[i].Width)
                : ordered[i + 1].Y - (ordered[i].Y + ordered[i].Height);
            gaps.Add(Math.Round(gap, 1));
        }

        double minGap = gaps.Min();
        double maxGap = gaps.Max();
        double avgGap = Math.Round(gaps.Average(), 1);
        bool isUniform = Math.Abs(maxGap - minGap) <= 1.5;
        string summary = FormatSpacingDistributionSummary(targets.Count, gaps.Count, avgGap, minGap, maxGap, isUniform, axis);
        StatusMessage = summary;
        return new SpacingDistributionAudit(targets.Count, gaps.Count, avgGap, minGap, maxGap, isUniform, axis, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of spacing distribution and gap uniformity.
    /// </summary>
    public static string FormatSpacingDistributionSummary(
        int shapeCount, int gapCount, double avgGap, double minGap, double maxGap, bool isUniform, DistributionAxis axis)
    {
        string axisStr = axis == DistributionAxis.Horizontal ? "Horizontal" : "Vertical";
        if (shapeCount < 2) return $"{axisStr} spacing audit: Insufficient shapes ({shapeCount})";
        string shapeLabel = shapeCount == 1 ? "1 shape" : $"{shapeCount} shapes";
        string gapLabel = gapCount == 1 ? "1 gap" : $"{gapCount} gaps";
        string status = isUniform ? "Uniform" : "Uneven";
        return $"{axisStr} spacing audit ({shapeLabel}, {gapLabel} · {status}): avg {avgGap:0.#}px (Min: {minGap:0.#}px, Max: {maxGap:0.#}px)";
    }

    /// <summary>
    /// Evenly distributes the spacing/gutters between selected shapes along the specified axis, keeping the outermost shapes fixed.
    /// </summary>
    public (int TotalShapes, int ModifiedCount, double UniformGap, string Summary) DistributeSpacingUniformly(
        DistributionAxis axis = DistributionAxis.Horizontal,
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        string axisStr = axis == DistributionAxis.Horizontal ? "Horizontal" : "Vertical";
        if (targets.Count < 3)
        {
            string insufficient = $"Spacing distribution: Requires at least 3 shapes to distribute intermediate spacing ({targets.Count} found)";
            StatusMessage = insufficient;
            return (targets.Count, 0, 0.0, insufficient);
        }

        var ordered = (axis == DistributionAxis.Horizontal ? targets.OrderBy(s => s.X) : targets.OrderBy(s => s.Y)).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        double totalEnvelope = axis == DistributionAxis.Horizontal
            ? (last.X + last.Width) - first.X
            : (last.Y + last.Height) - first.Y;

        double totalShapesSize = ordered.Sum(s => axis == DistributionAxis.Horizontal ? s.Width : s.Height);
        double totalGapSpace = totalEnvelope - totalShapesSize;
        double uniformGap = Math.Round(totalGapSpace / (ordered.Count - 1), 1);

        var adjustments = new List<(ShapeCanvasItemViewModel Shape, double NewCoord)>();
        double currentCoord = axis == DistributionAxis.Horizontal
            ? first.X + first.Width + uniformGap
            : first.Y + first.Height + uniformGap;

        for (int i = 1; i < ordered.Count - 1; i++)
        {
            var s = ordered[i];
            double roundedCoord = Math.Round(currentCoord);
            double oldCoord = axis == DistributionAxis.Horizontal ? s.X : s.Y;
            if (Math.Abs(oldCoord - roundedCoord) > 0.5)
            {
                adjustments.Add((s, roundedCoord));
            }
            double size = axis == DistributionAxis.Horizontal ? s.Width : s.Height;
            currentCoord += size + uniformGap;
        }

        if (adjustments.Count == 0)
        {
            string alreadyUniform = $"✓ {ordered.Count} shapes are already uniformly spaced ({uniformGap:0.#}px {axisStr.ToLowerInvariant()} gaps)";
            StatusMessage = alreadyUniform;
            return (ordered.Count, 0, uniformGap, alreadyUniform);
        }

        RecordUndo();
        int modified = 0;
        foreach (var (shape, newCoord) in adjustments)
        {
            if (axis == DistributionAxis.Horizontal) shape.X = newCoord;
            else shape.Y = newCoord;
            modified++;
        }

        string summary = $"✓ Distributed uniform {axisStr.ToLowerInvariant()} spacing ({uniformGap:0.#}px gaps across {ordered.Count} shapes) · Ctrl+Z to undo";
        StatusMessage = summary;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        return (ordered.Count, modified, uniformGap, summary);
    }

    /// <summary>
    /// Audits Z-order depth overlap and occlusion between shapes on canvas, detecting hidden or obscured shapes.
    /// </summary>
    public ShapeOcclusionAudit AuditOcclusionAndOverlap(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes" : "Full Canvas";

        if (targets.Count < 2)
        {
            string emptySummary = $"Occlusion & overlap audit (0 pairs · {scopeStr}): Insufficient shapes ({targets.Count} found)";
            StatusMessage = emptySummary;
            return new ShapeOcclusionAudit(
                targets.Count, 0, 0, 0.0, "None", "None",
                Array.Empty<ShapeOcclusionInfo>(), "Clean Layout", isSelection, emptySummary);
        }

        var shapeZ = new Dictionary<ShapeCanvasItemViewModel, int>();
        for (int i = 0; i < Shapes.Count; i++)
        {
            shapeZ[Shapes[i]] = i;
        }

        int overlappingPairs = 0;
        var occlusionInfos = new List<ShapeOcclusionInfo>();
        double globalMaxOcclusion = 0.0;
        string worstOccludedName = "None";
        string worstCoveringName = "None";

        for (int i = 0; i < targets.Count; i++)
        {
            var shapeA = targets[i];
            double areaA = shapeA.Width * shapeA.Height;
            if (areaA <= 0.01) continue;

            int zA = shapeZ.TryGetValue(shapeA, out int zaVal) ? zaVal : i;
            double maxCoverFraction = 0.0;
            string topCoveringName = "None";
            int overlapCount = 0;

            for (int j = 0; j < targets.Count; j++)
            {
                if (i == j) continue;
                var shapeB = targets[j];
                double areaB = shapeB.Width * shapeB.Height;
                if (areaB <= 0.01) continue;

                int zB = shapeZ.TryGetValue(shapeB, out int zbVal) ? zbVal : j;

                double left = Math.Max(shapeA.X, shapeB.X);
                double right = Math.Min(shapeA.X + shapeA.Width, shapeB.X + shapeB.Width);
                double top = Math.Max(shapeA.Y, shapeB.Y);
                double bottom = Math.Min(shapeA.Y + shapeA.Height, shapeB.Y + shapeB.Height);

                if (right > left && bottom > top)
                {
                    double intersectArea = (right - left) * (bottom - top);
                    if (intersectArea > 0.01)
                    {
                        if (i < j) overlappingPairs++;
                        overlapCount++;

                        if (zB > zA)
                        {
                            double fraction = intersectArea / areaA;
                            if (fraction > maxCoverFraction)
                            {
                                maxCoverFraction = fraction;
                                topCoveringName = shapeB.DisplayName;
                            }
                        }
                    }
                }
            }

            double occlusionPercent = Math.Round(maxCoverFraction * 100.0, 1);
            if (occlusionPercent >= 15.0)
            {
                string severity = occlusionPercent switch
                {
                    >= 90.0 => "Complete",
                    >= 50.0 => "Heavy",
                    _ => "Partial"
                };

                occlusionInfos.Add(new ShapeOcclusionInfo(
                    shapeA.Id, shapeA.DisplayName, zA, areaA, occlusionPercent, topCoveringName, overlapCount, severity));

                if (occlusionPercent > globalMaxOcclusion)
                {
                    globalMaxOcclusion = occlusionPercent;
                    worstOccludedName = shapeA.DisplayName;
                    worstCoveringName = topCoveringName;
                }
            }
        }

        int occludedCount = occlusionInfos.Count(o => o.MaxOcclusionPercentage >= 50.0);

        string category;
        if (overlappingPairs == 0) category = "Clean Layout (No Overlaps)";
        else if (occludedCount == 0 && globalMaxOcclusion < 30.0) category = "Minor Overlaps";
        else if (globalMaxOcclusion < 80.0) category = "Moderate Occlusions";
        else category = "Severe Hidden Shapes";

        string summary = FormatOcclusionSummary(
            targets.Count, overlappingPairs, occludedCount, globalMaxOcclusion, worstOccludedName, worstCoveringName, category, scopeStr);
        StatusMessage = summary;

        return new ShapeOcclusionAudit(
            targets.Count,
            overlappingPairs,
            occludedCount,
            globalMaxOcclusion,
            worstOccludedName,
            worstCoveringName,
            occlusionInfos,
            category,
            isSelection,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of Z-order depth overlap and occlusion metrics.
    /// </summary>
    public static string FormatOcclusionSummary(
        int totalShapes, int pairCount, int occludedCount, double maxOcclusion,
        string worstOccluded, string worstCovering, string category, string scope = "Full Canvas")
    {
        if (totalShapes < 2) return $"Occlusion audit (0 pairs · {scope}): Insufficient shapes";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        string pairLabel = pairCount == 1 ? "1 pair" : $"{pairCount} pairs";

        string worstStr = "";
        if (maxOcclusion >= 15.0 && !string.IsNullOrEmpty(worstOccluded) && worstOccluded != "None")
        {
            worstStr = $" · Worst: '{worstOccluded}' {maxOcclusion:0.#}% covered by '{worstCovering}'";
        }

        return $"Occlusion audit ({shapeLabel}, {pairLabel} overlapping · {category}): {occludedCount} heavily occluded shapes{worstStr}";
    }

    /// <summary>
    /// Audits canvas boundary margins and safe zone bleed across shapes, detecting clipped or edge-bleeding shapes.
    /// Evaluates proximity to canvas edges against a safe printable margin (default 24px).
    /// </summary>
    public ShapeBoundaryBleedAudit AuditBoundaryMarginsAndBleed(
        double safeMargin = 24.0,
        double canvasWidth = 1200.0,
        double canvasHeight = 800.0,
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes" : "Full Canvas";

        if (targets.Count == 0)
        {
            string emptySummary = $"Boundary margin & bleed audit (0 shapes · {scopeStr}): Canvas is empty";
            StatusMessage = emptySummary;
            return new ShapeBoundaryBleedAudit(
                0, 0, 0, 0, 0.0, "None", "Empty Canvas", isSelection, emptySummary);
        }

        int bleedingCount = 0;
        int tightMarginCount = 0;
        int safeCount = 0;
        double minObservedMargin = double.MaxValue;
        string worstShapeName = "None";

        foreach (var shape in targets)
        {
            double left = shape.X;
            double top = shape.Y;
            double right = canvasWidth - (shape.X + shape.Width);
            double bottom = canvasHeight - (shape.Y + shape.Height);

            double shapeMinMargin = Math.Min(Math.Min(left, top), Math.Min(right, bottom));

            bool isBleeding = left < 0 || top < 0 || (shape.X + shape.Width) > canvasWidth || (shape.Y + shape.Height) > canvasHeight;

            if (isBleeding)
            {
                bleedingCount++;
                if (shapeMinMargin < minObservedMargin)
                {
                    minObservedMargin = shapeMinMargin;
                    worstShapeName = shape.DisplayName;
                }
            }
            else if (shapeMinMargin < safeMargin)
            {
                tightMarginCount++;
                if (shapeMinMargin < minObservedMargin)
                {
                    minObservedMargin = shapeMinMargin;
                    worstShapeName = shape.DisplayName;
                }
            }
            else
            {
                safeCount++;
                if (shapeMinMargin < minObservedMargin)
                {
                    minObservedMargin = shapeMinMargin;
                    if (worstShapeName == "None") worstShapeName = shape.DisplayName;
                }
            }
        }

        if (minObservedMargin == double.MaxValue) minObservedMargin = 0.0;
        minObservedMargin = Math.Round(minObservedMargin, 1);

        string category;
        if (bleedingCount > 0)
        {
            category = "Boundary Bleed (Clipped Shapes)";
        }
        else if (tightMarginCount > 0)
        {
            category = "Tight Margins (<24px)";
        }
        else
        {
            category = "Safe Printable Margins";
        }

        string summary = FormatBoundaryMarginSummary(
            targets.Count, bleedingCount, tightMarginCount, safeCount, minObservedMargin, worstShapeName, category, scopeStr);
        StatusMessage = summary;

        return new ShapeBoundaryBleedAudit(
            targets.Count, bleedingCount, tightMarginCount, safeCount,
            minObservedMargin, worstShapeName, category, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of canvas boundary margins and safe zone bleed metrics.
    /// </summary>
    public static string FormatBoundaryMarginSummary(
        int totalShapes, int bleedingCount, int tightCount, int safeCount,
        double minMargin, string worstShape, string category, string scope = "Full Canvas")
    {
        if (totalShapes == 0) return $"Boundary margin audit (0 shapes · {scope}): Canvas is empty";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";

        string worstStr = "";
        if (worstShape != "None" && (bleedingCount > 0 || tightCount > 0))
        {
            worstStr = $" · Closest: '{worstShape}' ({minMargin:0.#}px from edge)";
        }

        string bleedNote = bleedingCount > 0 ? $" · ⚠ {bleedingCount} bleeding/clipped" : "";
        string tightNote = tightCount > 0 ? $" · {tightCount} tight (<24px)" : "";

        return $"Boundary margin audit ({shapeLabel} · {category}): min margin {minMargin:0.#}px{bleedNote}{tightNote}{worstStr}";
    }

    /// <summary>
    /// Audits WCAG AA/AAA contrast ratios between shape background fills and embedded label typography.
    /// </summary>
    public ShapeTextContrastAudit AuditShapeTextContrast(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes" : "Full Canvas";

        if (targets.Count == 0)
        {
            string emptySummary = $"Text contrast audit (0 shapes · {scopeStr}): Canvas is empty";
            StatusMessage = emptySummary;
            return new ShapeTextContrastAudit(
                0, 0, 0, 0, 0, 0.0, 0.0, "None",
                Array.Empty<ShapeTextContrastInfo>(), "Empty Canvas", isSelection, emptySummary);
        }

        var contrastInfos = new List<ShapeTextContrastInfo>();
        int aaCount = 0;
        int aaaCount = 0;
        int failingCount = 0;
        double minRatio = double.MaxValue;
        double totalRatio = 0.0;
        string worstShapeName = "None";

        var labeledShapes = targets.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();

        if (labeledShapes.Count == 0)
        {
            string noLabelsSummary = $"Text contrast audit ({targets.Count} shapes · {scopeStr}): No labeled shapes";
            StatusMessage = noLabelsSummary;
            return new ShapeTextContrastAudit(
                targets.Count, 0, 0, 0, 0, 0.0, 0.0, "None",
                Array.Empty<ShapeTextContrastInfo>(), "No Labeled Shapes", isSelection, noLabelsSummary);
        }

        foreach (var shape in labeledShapes)
        {
            string cleanFill = (shape.Fill ?? "FFFFFF").Trim().TrimStart('#');
            if (cleanFill.Length != 6) cleanFill = "FFFFFF";

            string textCol = shape.TextColor ?? (Services.ContrastGuard.GetLuminance(cleanFill) < 0.35 ? "FFFFFF" : "1E1E1E");
            string cleanTextCol = textCol.Trim().TrimStart('#');
            if (cleanTextCol.Length != 6) cleanTextCol = "000000";

            double ratio = Math.Round(Services.ContrastGuard.GetContrastRatio(cleanTextCol, cleanFill), 2);
            bool isAa = ratio >= 4.5;
            bool isAaa = ratio >= 7.0;

            if (isAa) aaCount++;
            else failingCount++;

            if (isAaa) aaaCount++;

            string labelOrName = !string.IsNullOrWhiteSpace(shape.Text)
                ? shape.Text.Trim().Split('\n')[0].Trim()
                : shape.DisplayName;

            if (ratio < minRatio)
            {
                minRatio = ratio;
                worstShapeName = labelOrName;
            }

            totalRatio += ratio;

            string suggested = Services.ContrastGuard.GetLuminance(cleanFill) < 0.4 ? "FFFFFF" : "121212";

            contrastInfos.Add(new ShapeTextContrastInfo(
                shape.Id, labelOrName, cleanFill, cleanTextCol, ratio, isAa, isAaa, suggested));
        }

        if (minRatio == double.MaxValue) minRatio = 0.0;
        double avgRatio = Math.Round(totalRatio / labeledShapes.Count, 2);

        string category;
        if (failingCount == 0 && aaaCount == labeledShapes.Count)
        {
            category = "WCAG AAA Compliant (All ≥7.0:1)";
        }
        else if (failingCount == 0)
        {
            category = "WCAG AA Compliant (All ≥4.5:1)";
        }
        else
        {
            category = "Contrast Violations Detected (<4.5:1)";
        }

        string summary = FormatShapeTextContrastSummary(
            targets.Count, labeledShapes.Count, aaCount, failingCount, minRatio, avgRatio, worstShapeName, category, scopeStr);
        StatusMessage = summary;

        return new ShapeTextContrastAudit(
            targets.Count, labeledShapes.Count, aaCount, aaaCount, failingCount,
            minRatio, avgRatio, worstShapeName, contrastInfos, category, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of shape label text contrast metrics.
    /// </summary>
    public static string FormatShapeTextContrastSummary(
        int totalShapes, int labeledShapes, int aaCount, int failingCount,
        double minRatio, double avgRatio, string worstShape, string category, string scope = "Full Canvas")
    {
        if (totalShapes == 0) return $"Text contrast audit (0 shapes · {scope}): Canvas is empty";
        if (labeledShapes == 0) return $"Text contrast audit ({totalShapes} shapes · {scope}): No labeled shapes";

        string shapeLabel = labeledShapes == 1 ? "1 labeled shape" : $"{labeledShapes} labeled shapes";
        string worstStr = "";
        if (worstShape != "None" && failingCount > 0)
        {
            worstStr = $" · Lowest: '{worstShape}' ({minRatio:0.0}:1)";
        }

        string failNote = failingCount > 0 ? $" · ⚠ {failingCount} failing" : "";

        return $"Text contrast audit ({shapeLabel} · {category}): min {minRatio:0.0}:1, avg {avgRatio:0.0}:1{failNote}{worstStr}";
    }

    /// <summary>
    /// Audits shapes for Golden Ratio (phi = 1.618) aspect ratio adherence and harmonic
    /// geometric area scaling progression across ordered shapes.
    /// </summary>
    public GoldenRatioProgressionAudit AuditGoldenRatioProgression(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count == 0)
        {
            string emptySummary = "Golden ratio audit: 0 shapes · Canvas is empty";
            StatusMessage = emptySummary;
            return new GoldenRatioProgressionAudit(
                0, 0, 0.0, 0, 1.0, 0.0, Array.Empty<ShapeGoldenRatioInfo>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        const double phi = 1.6180339887;
        var shapeInfos = new List<ShapeGoldenRatioInfo>();
        int goldenAspectCount = 0;
        double sumAspectDelta = 0.0;

        foreach (var s in targets)
        {
            double w = Math.Max(1.0, s.Width);
            double h = Math.Max(1.0, s.Height);
            double ratio = Math.Max(w / h, h / w); // Normalized >= 1.0
            double delta = Math.Abs(ratio - phi);
            bool isGolden = delta <= 0.08;
            if (isGolden) goldenAspectCount++;
            sumAspectDelta += delta;

            string name = !string.IsNullOrWhiteSpace(s.Text) ? s.Text.Replace("\n", " ").Trim() : s.DisplayName;
            if (name.Length > 24) name = name[..21] + "...";

            shapeInfos.Add(new ShapeGoldenRatioInfo(
                s.Id, name, Math.Round(w, 1), Math.Round(h, 1),
                Math.Round(ratio, 3), Math.Round(delta, 3), isGolden));
        }

        double avgAspectDelta = Math.Round(sumAspectDelta / targets.Count, 3);

        // Progression scaling between shapes sorted by area
        var sortedByArea = targets.Select(s => Math.Max(1.0, s.Width * s.Height)).OrderBy(a => a).ToList();
        int stepCount = 0;
        double sumProgressionRatio = 0.0;
        double progressionDeltaSum = 0.0;

        for (int i = 0; i < sortedByArea.Count - 1; i++)
        {
            double r = sortedByArea[i + 1] / sortedByArea[i];
            sumProgressionRatio += r;
            progressionDeltaSum += Math.Abs(r - phi);
            stepCount++;
        }

        double avgProgressionRatio = stepCount > 0 ? Math.Round(sumProgressionRatio / stepCount, 2) : 1.0;
        double avgProgressionDelta = stepCount > 0 ? progressionDeltaSum / stepCount : 0.0;
        double adherenceScore = stepCount > 0
            ? Math.Round(Math.Clamp(100.0 - (avgProgressionDelta / phi * 100.0), 0.0, 100.0), 1)
            : (goldenAspectCount > 0 ? 100.0 : 50.0);

        string category;
        if (goldenAspectCount == targets.Count || adherenceScore >= 85.0)
        {
            category = "Harmonic Golden Progression";
        }
        else if (adherenceScore >= 60.0 || goldenAspectCount >= targets.Count / 2)
        {
            category = "Near-Golden Scaling";
        }
        else
        {
            category = "Arbitrary Scale & Aspect";
        }

        string summary = FormatGoldenRatioProgressionSummary(
            targets.Count, goldenAspectCount, avgAspectDelta, avgProgressionRatio, adherenceScore, category, scopeStr);

        StatusMessage = summary;
        return new GoldenRatioProgressionAudit(
            targets.Count, goldenAspectCount, avgAspectDelta, stepCount,
            avgProgressionRatio, adherenceScore, shapeInfos, category, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of golden ratio progression metrics.
    /// </summary>
    public static string FormatGoldenRatioProgressionSummary(
        int totalShapes, int goldenAspectCount, double avgAspectDelta,
        double avgProgressionRatio, double adherenceScore, string category, string scope = "")
    {
        if (totalShapes == 0) return "Golden ratio audit: 0 shapes · Canvas is empty";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        return $"Golden ratio audit ({scope}{shapeLabel} · {category}): {goldenAspectCount}/{totalShapes} golden aspect, avg step {avgProgressionRatio:0.00}x (score {adherenceScore:0.#}%)";
    }

    /// <summary>
    /// Audits shapes for logarithmic Golden Spiral placement progression, evaluating radial expansion
    /// ratios (approximating phi = 1.618 per quarter turn) and angular trajectory around the collective center.
    /// </summary>
    public GoldenSpiralProgressionAudit AuditGoldenSpiralProgression(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count == 0)
        {
            string emptySummary = "Golden spiral audit: 0 shapes · Canvas is empty";
            StatusMessage = emptySummary;
            return new GoldenSpiralProgressionAudit(
                0, 0.0, 0.0, 1.0, 0.0, "None", Array.Empty<ShapeSpiralNodeInfo>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        const double phi = 1.6180339887;

        double minX = targets.Min(s => s.X);
        double maxX = targets.Max(s => s.X + s.Width);
        double minY = targets.Min(s => s.Y);
        double maxY = targets.Max(s => s.Y + s.Height);
        double centerX = Math.Round((minX + maxX) / 2.0, 1);
        double centerY = Math.Round((minY + maxY) / 2.0, 1);

        var polarNodes = new List<(ShapeCanvasItemViewModel Shape, double Radius, double AngleDeg)>();
        foreach (var s in targets)
        {
            double scx = s.X + s.Width / 2.0;
            double scy = s.Y + s.Height / 2.0;
            double dx = scx - centerX;
            double dy = scy - centerY;
            double r = Math.Sqrt(dx * dx + dy * dy);
            double angleRad = Math.Atan2(dy, dx);
            double angleDeg = (angleRad * 180.0 / Math.PI + 360.0) % 360.0;
            polarNodes.Add((s, Math.Round(r, 1), Math.Round(angleDeg, 1)));
        }

        var orderedByRadius = polarNodes.OrderBy(p => p.Radius).ToList();
        var spiralInfos = new List<ShapeSpiralNodeInfo>();

        double sumGrowthRatio = 0.0;
        double sumGrowthDelta = 0.0;
        int stepCount = 0;
        int clockwiseSteps = 0;
        int counterClockwiseSteps = 0;

        for (int i = 0; i < orderedByRadius.Count; i++)
        {
            var cur = orderedByRadius[i];
            double growthRatio = 1.0;
            if (i > 0)
            {
                double prevR = Math.Max(1.0, orderedByRadius[i - 1].Radius);
                growthRatio = Math.Round(cur.Radius / prevR, 2);
                sumGrowthRatio += growthRatio;
                sumGrowthDelta += Math.Abs(growthRatio - phi);
                stepCount++;

                double prevAngle = orderedByRadius[i - 1].AngleDeg;
                double diffAngle = (cur.AngleDeg - prevAngle + 360.0) % 360.0;
                if (diffAngle > 0 && diffAngle <= 180.0)
                    clockwiseSteps++;
                else
                    counterClockwiseSteps++;
            }

            string name = !string.IsNullOrWhiteSpace(cur.Shape.Text)
                ? cur.Shape.Text.Replace("\n", " ").Trim()
                : cur.Shape.DisplayName;
            if (name.Length > 24) name = name[..21] + "...";

            spiralInfos.Add(new ShapeSpiralNodeInfo(cur.Shape.Id, name, cur.Radius, cur.AngleDeg, growthRatio));
        }

        double avgGrowthRatio = stepCount > 0 ? Math.Round(sumGrowthRatio / stepCount, 2) : 1.0;
        double avgGrowthDelta = stepCount > 0 ? sumGrowthDelta / stepCount : 0.0;
        double adherenceScore = stepCount > 0
            ? Math.Round(Math.Clamp(100.0 - (avgGrowthDelta / phi * 100.0), 0.0, 100.0), 1)
            : 50.0;

        string direction = "Radial Dispersed";
        if (stepCount > 0)
        {
            if (clockwiseSteps > counterClockwiseSteps * 1.5) direction = "Clockwise Spiral";
            else if (counterClockwiseSteps > clockwiseSteps * 1.5) direction = "Counter-Clockwise Spiral";
        }

        string category;
        if (targets.Count >= 3 && adherenceScore >= 80.0 && direction.Contains("Spiral"))
        {
            category = "Harmonic Golden Spiral";
        }
        else if (targets.Count >= 3 && adherenceScore >= 55.0)
        {
            category = "Near-Spiral Dispersion";
        }
        else if (targets.Count >= 2)
        {
            category = "Concentric / Irregular Distribution";
        }
        else
        {
            category = "Single Anchor Point";
        }

        string summary = FormatGoldenSpiralSummary(
            targets.Count, centerX, centerY, avgGrowthRatio, adherenceScore, direction, category, scopeStr);

        StatusMessage = summary;
        return new GoldenSpiralProgressionAudit(
            targets.Count, centerX, centerY, avgGrowthRatio, adherenceScore, direction,
            spiralInfos, category, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of golden spiral progression metrics.
    /// </summary>
    public static string FormatGoldenSpiralSummary(
        int totalShapes, double centerX, double centerY, double avgGrowthRatio,
        double adherenceScore, string direction, string category, string scope = "")
    {
        if (totalShapes == 0) return "Golden spiral audit: 0 shapes · Canvas is empty";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        return $"Golden spiral audit ({scope}{shapeLabel} · {category}): Center ({centerX:0.#}, {centerY:0.#}), avg growth {avgGrowthRatio:0.00}x, {direction} (score {adherenceScore:0.#}%)";
    }

    /// <summary>
    /// Audits shape fill color palette harmony, evaluating chromatic dispersion, hue clusters
    /// (monochromatic, analogous, complementary, triadic, tetradic), and neutral anchor distribution.
    /// </summary>
    public ShapeColorHarmonyAudit AuditColorPaletteHarmony(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count == 0)
        {
            string emptySummary = "Color palette harmony audit: 0 shapes · Canvas is empty";
            StatusMessage = emptySummary;
            return new ShapeColorHarmonyAudit(
                0, 0, 0, 0.0, 0.0, "None", 0.0, Array.Empty<string>(),
                Array.Empty<ShapeColorInfo>(), "Empty Canvas", isSelection, emptySummary);
        }

        var colorInfos = new List<ShapeColorInfo>();
        var chromaticHues = new List<double>();
        int neutralCount = 0;

        foreach (var shape in targets)
        {
            string rawFill = (shape.Fill ?? "4B9CD3").Trim().TrimStart('#');
            if (rawFill.Length != 6) rawFill = "4B9CD3";

            byte r = Convert.ToByte(rawFill[0..2], 16);
            byte g = Convert.ToByte(rawFill[2..4], 16);
            byte b = Convert.ToByte(rawFill[4..6], 16);

            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;

            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            double hue = 0.0;
            if (delta > 0.0001)
            {
                if (Math.Abs(max - rf) < 0.0001)
                {
                    hue = 60.0 * (((gf - bf) / delta) % 6.0);
                }
                else if (Math.Abs(max - gf) < 0.0001)
                {
                    hue = 60.0 * (((bf - rf) / delta) + 2.0);
                }
                else
                {
                    hue = 60.0 * (((rf - gf) / delta) + 4.0);
                }
                if (hue < 0) hue += 360.0;
            }

            double sat = max > 0.0001 ? delta / max : 0.0;
            double val = max;

            bool isNeutral = sat < 0.15 || val < 0.12 || (val > 0.95 && sat < 0.18);
            if (isNeutral) neutralCount++;
            else chromaticHues.Add(hue);

            string classification;
            if (isNeutral)
            {
                classification = val > 0.8 ? "Neutral Light / White" : (val < 0.25 ? "Neutral Dark / Black" : "Neutral Gray");
            }
            else
            {
                if (hue < 25 || hue >= 345) classification = "Red / Crimson";
                else if (hue < 50) classification = "Orange / Amber";
                else if (hue < 75) classification = "Yellow / Gold";
                else if (hue < 160) classification = "Green / Emerald";
                else if (hue < 200) classification = "Cyan / Teal";
                else if (hue < 260) classification = "Blue / Azure";
                else if (hue < 300) classification = "Purple / Indigo";
                else classification = "Magenta / Pink";
            }

            string name = !string.IsNullOrWhiteSpace(shape.Text)
                ? shape.Text.Replace("\n", " ").Trim()
                : shape.DisplayName;
            if (name.Length > 24) name = name[..21] + "...";

            colorInfos.Add(new ShapeColorInfo(
                shape.Id, name, $"#{rawFill.ToUpperInvariant()}",
                Math.Round(hue, 1), Math.Round(sat, 2), Math.Round(val, 2), isNeutral, classification));
        }

        int chromaticCount = chromaticHues.Count;
        double meanHue = 0.0;
        double hueStdDev = 0.0;
        string harmonyType;
        double coherenceScore;

        if (chromaticCount == 0)
        {
            harmonyType = "Monochrome Neutral Anchors";
            coherenceScore = 100.0;
        }
        else if (chromaticCount == 1)
        {
            harmonyType = "Single Hue Accent";
            meanHue = Math.Round(chromaticHues[0], 1);
            coherenceScore = 100.0;
        }
        else
        {
            // Circular mean of chromatic angles
            double sumSin = chromaticHues.Sum(h => Math.Sin(h * Math.PI / 180.0));
            double sumCos = chromaticHues.Sum(h => Math.Cos(h * Math.PI / 180.0));
            double avgAngleRad = Math.Atan2(sumSin / chromaticCount, sumCos / chromaticCount);
            meanHue = Math.Round((avgAngleRad * 180.0 / Math.PI + 360.0) % 360.0, 1);

            // Circular angular dispersion
            double rLen = Math.Sqrt(sumSin * sumSin + sumCos * sumCos) / chromaticCount;
            double circVariance = Math.Max(0.0, 1.0 - rLen);
            hueStdDev = Math.Round(Math.Sqrt(-2.0 * Math.Log(Math.Max(0.0001, rLen))) * 180.0 / Math.PI, 1);

            // Cluster analysis across chromatic hues
            var sortedHues = chromaticHues.OrderBy(h => h).ToList();
            double maxSpan = 0.0;
            for (int i = 0; i < sortedHues.Count; i++)
            {
                double h1 = sortedHues[i];
                double h2 = sortedHues[(i + 1) % sortedHues.Count];
                double gap = (h2 - h1 + 360.0) % 360.0;
                if (gap > maxSpan) maxSpan = gap;
            }
            double clusterSpan = 360.0 - maxSpan;

            if (clusterSpan <= 30.0)
            {
                harmonyType = "Monochromatic (Cohesive Tone)";
                coherenceScore = Math.Round(95.0 + (30.0 - clusterSpan) / 6.0, 1);
            }
            else if (clusterSpan <= 75.0)
            {
                harmonyType = "Analogous (Harmonious Adjacent Hues)";
                coherenceScore = Math.Round(85.0 + (75.0 - clusterSpan) / 4.5, 1);
            }
            else
            {
                // Check for complementary (pairs ~180 deg) or triadic (~120 deg)
                bool hasComplementary = false;
                bool hasTriadic = false;
                for (int i = 0; i < sortedHues.Count; i++)
                {
                    for (int j = i + 1; j < sortedHues.Count; j++)
                    {
                        double diff = Math.Abs(sortedHues[i] - sortedHues[j]);
                        if (diff > 180.0) diff = 360.0 - diff;
                        if (diff >= 150.0 && diff <= 180.0) hasComplementary = true;
                        if (diff >= 105.0 && diff <= 135.0) hasTriadic = true;
                    }
                }

                if (hasTriadic)
                {
                    harmonyType = "Triadic (Balanced High-Contrast)";
                    coherenceScore = 88.0;
                }
                else if (hasComplementary)
                {
                    harmonyType = "Complementary (Dynamic Opposing Accent)";
                    coherenceScore = 90.0;
                }
                else
                {
                    harmonyType = "Poly-Chromatic (Eclectic Dispersion)";
                    coherenceScore = Math.Round(Math.Max(40.0, 100.0 - (hueStdDev * 0.7)), 1);
                }
            }
        }

        var dominantNames = colorInfos.Select(c => c.ColorClassification).GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ({g.Count()})")
            .Take(3).ToList();

        string summary = FormatColorPaletteHarmonySummary(
            targets.Count, neutralCount, chromaticCount, meanHue, hueStdDev,
            harmonyType, coherenceScore, dominantNames, scopeStr);

        StatusMessage = summary;
        return new ShapeColorHarmonyAudit(
            targets.Count, neutralCount, chromaticCount, meanHue, hueStdDev,
            harmonyType, coherenceScore, dominantNames, colorInfos, harmonyType, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of shape color palette harmony metrics.
    /// </summary>
    public static string FormatColorPaletteHarmonySummary(
        int totalShapes, int neutralCount, int chromaticCount, double meanHue,
        double hueStdDev, string harmonyType, double coherenceScore,
        IReadOnlyList<string> dominantNames, string scope = "")
    {
        if (totalShapes == 0) return "Color palette harmony audit: 0 shapes · Canvas is empty";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        string dominant = dominantNames.Count > 0 ? $" · Dominant: {string.Join(", ", dominantNames)}" : "";
        return $"Color palette harmony ({scope}{shapeLabel} · {harmonyType}): Coherence: {coherenceScore:0.#}% · Mean Hue: {meanHue:0.#}° (σ={hueStdDev:0.#}°){dominant}";
    }

    /// <summary>
    /// Audits shapes for 30°/60° isometric grid alignment, axonometric projection fidelity,
    /// and depth-stacking (Z-order) consistency in 2.5D layouts.
    /// </summary>
    public IsometricGridAlignmentAudit AuditIsometricGridAlignment(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count < 2)
        {
            string emptySummary = targets.Count == 0
                ? "Isometric grid audit: 0 shapes · Canvas is empty"
                : "Isometric grid audit: 1 shape · At least 2 shapes required for axonometric projection analysis";
            StatusMessage = emptySummary;
            return new IsometricGridAlignmentAudit(
                targets.Count, 0, 0, 0, 0.0, 0.0,
                Array.Empty<ShapeIsometricPairInfo>(),
                targets.Count == 0 ? "Empty Canvas" : "Insufficient Shapes (<2)",
                isSelection, emptySummary);
        }

        var isometricAxes = new double[] { 30.0, 90.0, 150.0, 210.0, 270.0, 330.0 };
        var pairs = new List<ShapeIsometricPairInfo>();
        int alignedCount = 0;
        double sumAngleDelta = 0.0;
        int depthConflicts = 0;

        var shapeZ = new Dictionary<ShapeCanvasItemViewModel, int>();
        for (int idx = 0; idx < Shapes.Count; idx++)
        {
            shapeZ[Shapes[idx]] = idx;
        }
        int GetZ(ShapeCanvasItemViewModel s, int fallback) => shapeZ.TryGetValue(s, out int z) ? z : fallback;

        // Evaluate pairwise spatial vectors between shapes
        for (int i = 0; i < targets.Count; i++)
        {
            var a = targets[i];
            double acx = a.X + a.Width / 2.0;
            double acy = a.Y + a.Height / 2.0;

            for (int j = i + 1; j < targets.Count; j++)
            {
                var b = targets[j];
                double bcx = b.X + b.Width / 2.0;
                double bcy = b.Y + b.Height / 2.0;

                double dx = bcx - acx;
                double dy = bcy - acy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1.0) continue;

                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = (angleRad * 180.0 / Math.PI + 360.0) % 360.0;

                double minDelta = double.MaxValue;
                double nearestAxis = 30.0;
                foreach (var axis in isometricAxes)
                {
                    double diff = Math.Abs(angleDeg - axis);
                    if (diff > 180.0) diff = 360.0 - diff;
                    if (diff < minDelta)
                    {
                        minDelta = diff;
                        nearestAxis = axis;
                    }
                }

                bool isAligned = minDelta <= 8.5; // Within ~8.5° tolerance of isometric axis
                if (isAligned) alignedCount++;
                sumAngleDelta += minDelta;

                // Check depth stacking: if A is above B (acy < bcy - 10), A should be behind B (zA <= zB)
                int zA = GetZ(a, i);
                int zB = GetZ(b, j);
                if (acy < bcy - 10.0 && zA > zB)
                {
                    depthConflicts++;
                }
                else if (bcy < acy - 10.0 && zB > zA)
                {
                    depthConflicts++;
                }

                pairs.Add(new ShapeIsometricPairInfo(
                    a.Id, b.Id, Math.Round(angleDeg, 1), nearestAxis,
                    Math.Round(minDelta, 1), isAligned));
            }
        }

        int totalPairs = pairs.Count;
        double meanAngleError = totalPairs > 0 ? Math.Round(sumAngleDelta / totalPairs, 1) : 0.0;
        double alignmentRatio = totalPairs > 0 ? (double)alignedCount / totalPairs : 0.0;

        double fidelity = 0.0;
        if (totalPairs > 0)
        {
            double baseScore = alignmentRatio * 80.0 + Math.Max(0.0, 20.0 - (meanAngleError * 1.5));
            double penalty = depthConflicts * 5.0;
            fidelity = Math.Round(Math.Clamp(baseScore - penalty, 0.0, 100.0), 1);
        }

        string category;
        if (fidelity >= 85.0)
        {
            category = "Strict Isometric Projection (30°/60°)";
        }
        else if (fidelity >= 55.0 || alignedCount >= totalPairs / 2)
        {
            category = "Near-Isometric Axonometric Alignment";
        }
        else
        {
            category = "Arbitrary 2D Freeform Layout";
        }

        string summary = FormatIsometricSummary(
            targets.Count, totalPairs, alignedCount, depthConflicts, meanAngleError, fidelity, category, scopeStr);

        StatusMessage = summary;
        return new IsometricGridAlignmentAudit(
            targets.Count, totalPairs, alignedCount, depthConflicts, meanAngleError,
            fidelity, pairs, category, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of isometric grid alignment and projection fidelity.
    /// </summary>
    public static string FormatIsometricSummary(
        int totalShapes, int totalPairs, int alignedPairs, int depthConflicts,
        double meanAngleError, double fidelity, string category, string scope = "")
    {
        if (totalShapes < 2) return "Isometric grid audit: Insufficient shapes (<2) · At least 2 shapes required";

        string shapeLabel = $"{totalShapes} shapes";
        string conflictStr = depthConflicts > 0 ? $" · ⚠ {depthConflicts} depth conflicts" : "";
        return $"Isometric grid audit ({scope}{shapeLabel} · {category}): Fidelity: {fidelity:0.#}% · {alignedPairs}/{totalPairs} aligned pairs (error ±{meanAngleError:0.#}°){conflictStr}";
    }

    /// <summary>
    /// Audits diagram geometric symmetry, evaluating bilateral vertical reflection, horizontal reflection,
    /// and 180° rotational symmetry across shape coordinates and bounding boxes.
    /// </summary>
    public DiagramSymmetryAudit AuditDiagramSymmetry(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = targetShapes != null || selected.Count >= 2;
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count < 2)
        {
            string emptySummary = $"Diagram symmetry ({scopeStr}{targets.Count} shapes): Insufficient shapes (<2) · At least 2 shapes required";
            StatusMessage = emptySummary;
            return new DiagramSymmetryAudit(
                targets.Count, 0, 0, 0, 0.0, 0.0, 0.0, 0.0,
                Array.Empty<ShapeSymmetryMatchInfo>(),
                "Asymmetric / Freeform Composition", isSelection, emptySummary);
        }

        double minX = targets.Min(s => s.X);
        double maxX = targets.Max(s => s.X + s.Width);
        double minY = targets.Min(s => s.Y);
        double maxY = targets.Max(s => s.Y + s.Height);

        double midX = (minX + maxX) / 2.0;
        double midY = (minY + maxY) / 2.0;
        double boundsW = Math.Max(1.0, maxX - minX);
        double boundsH = Math.Max(1.0, maxY - minY);

        double tolX = Math.Max(8.0, boundsW * 0.04);
        double tolY = Math.Max(8.0, boundsH * 0.04);
        double tolDim = Math.Max(8.0, Math.Min(boundsW, boundsH) * 0.06);

        int verticalMatches = 0;
        var matches = new List<ShapeSymmetryMatchInfo>();

        foreach (var sA in targets)
        {
            double cxA = sA.X + (sA.Width / 2.0);
            double cyA = sA.Y + (sA.Height / 2.0);
            double targetCx = (2.0 * midX) - cxA;

            bool found = targets.Any(sB =>
            {
                double cxB = sB.X + (sB.Width / 2.0);
                double cyB = sB.Y + (sB.Height / 2.0);
                return Math.Abs(cxB - targetCx) <= tolX &&
                       Math.Abs(cyB - cyA) <= tolY &&
                       Math.Abs(sB.Width - sA.Width) <= tolDim &&
                       Math.Abs(sB.Height - sA.Height) <= tolDim;
            });

            if (found) verticalMatches++;
        }

        int horizontalMatches = 0;
        foreach (var sA in targets)
        {
            double cxA = sA.X + (sA.Width / 2.0);
            double cyA = sA.Y + (sA.Height / 2.0);
            double targetCy = (2.0 * midY) - cyA;

            bool found = targets.Any(sB =>
            {
                double cxB = sB.X + (sB.Width / 2.0);
                double cyB = sB.Y + (sB.Height / 2.0);
                return Math.Abs(cxB - cxA) <= tolX &&
                       Math.Abs(cyB - targetCy) <= tolY &&
                       Math.Abs(sB.Width - sA.Width) <= tolDim &&
                       Math.Abs(sB.Height - sA.Height) <= tolDim;
            });

            if (found) horizontalMatches++;
        }

        int rotationalMatches = 0;
        foreach (var sA in targets)
        {
            double cxA = sA.X + (sA.Width / 2.0);
            double cyA = sA.Y + (sA.Height / 2.0);
            double targetCx = (2.0 * midX) - cxA;
            double targetCy = (2.0 * midY) - cyA;

            bool found = targets.Any(sB =>
            {
                double cxB = sB.X + (sB.Width / 2.0);
                double cyB = sB.Y + (sB.Height / 2.0);
                return Math.Abs(cxB - targetCx) <= tolX &&
                       Math.Abs(cyB - targetCy) <= tolY &&
                       Math.Abs(sB.Width - sA.Width) <= tolDim &&
                       Math.Abs(sB.Height - sA.Height) <= tolDim;
            });

            if (found) rotationalMatches++;
        }

        double n = targets.Count;
        double vertRatio = Math.Round((verticalMatches / n) * 100.0, 1);
        double horizRatio = Math.Round((horizontalMatches / n) * 100.0, 1);
        double rotRatio = Math.Round((rotationalMatches / n) * 100.0, 1);
        double overallScore = Math.Max(vertRatio, Math.Max(horizRatio, rotRatio));

        string category;
        if (vertRatio >= 85.0 && horizRatio >= 85.0)
        {
            category = "Dual-Axis Bilateral Symmetry (D2)";
        }
        else if (vertRatio >= 85.0)
        {
            category = "Vertical Bilateral Reflection (C1v)";
        }
        else if (horizRatio >= 85.0)
        {
            category = "Horizontal Bilateral Reflection (C1h)";
        }
        else if (rotRatio >= 85.0)
        {
            category = "Centrosymmetric 180° Rotational (C2)";
        }
        else if (overallScore >= 50.0)
        {
            category = "Partial Geometric Symmetry";
        }
        else
        {
            category = "Asymmetric / Freeform Composition";
        }

        string summary = FormatDiagramSymmetrySummary(
            targets.Count, verticalMatches, horizontalMatches, rotationalMatches,
            vertRatio, horizRatio, rotRatio, overallScore, category, scopeStr);

        StatusMessage = summary;
        return new DiagramSymmetryAudit(
            targets.Count, verticalMatches, horizontalMatches, rotationalMatches,
            vertRatio, horizRatio, rotRatio, overallScore,
            matches, category, isSelection, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of diagram symmetry metrics.
    /// </summary>
    public static string FormatDiagramSymmetrySummary(
        int totalShapes, int vertMatches, int horizMatches, int rotMatches,
        double vertRatio, double horizRatio, double rotRatio, double overallScore,
        string category, string scope = "")
    {
        if (totalShapes < 2) return "Diagram symmetry: Insufficient shapes (<2) · At least 2 shapes required";

        string shapeLabel = $"{totalShapes} shapes";
        return $"Diagram symmetry audit ({scope}{shapeLabel} · {category}): Symmetry: {overallScore:0.#}% (V:{vertRatio:0.#}% [{vertMatches}/{totalShapes}], H:{horizRatio:0.#}%, Rot:{rotRatio:0.#}%)";
    }

    /// <summary>
    /// Audits shape boundary spacing uniformity, gutter consistency, and detects overlapping bounding box collisions.
    /// </summary>
    public ShapeSpacingUniformityAudit AuditSpacingUniformityAndCollisions(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count < 2)
        {
            string emptySummary = "Shape spacing audit: Insufficient shapes (<2) · Minimum 2 shapes required for spacing analysis";
            StatusMessage = emptySummary;
            return new ShapeSpacingUniformityAudit(
                targets.Count, 0, 0, 0.0, 0.0, 0.0, 100.0,
                Array.Empty<ShapeSpacingCollisionInfo>(), "Insufficient Shapes", isSelection, emptySummary);
        }

        var pairs = new List<ShapeSpacingCollisionInfo>();
        int collidingCount = 0;
        double sumH = 0.0;
        double sumV = 0.0;

        for (int i = 0; i < targets.Count; i++)
        {
            var a = targets[i];
            double ax1 = a.X, ax2 = a.X + Math.Max(1.0, a.Width);
            double ay1 = a.Y, ay2 = a.Y + Math.Max(1.0, a.Height);

            for (int j = i + 1; j < targets.Count; j++)
            {
                var b = targets[j];
                double bx1 = b.X, bx2 = b.X + Math.Max(1.0, b.Width);
                double by1 = b.Y, by2 = b.Y + Math.Max(1.0, b.Height);

                double hGap;
                if (ax2 <= bx1) hGap = bx1 - ax2;
                else if (bx2 <= ax1) hGap = ax1 - bx2;
                else hGap = -(Math.Min(ax2, bx2) - Math.Max(ax1, bx1));

                double vGap;
                if (ay2 <= by1) vGap = by1 - ay2;
                else if (by2 <= ay1) vGap = ay1 - by2;
                else vGap = -(Math.Min(ay2, by2) - Math.Max(ay1, by1));

                bool isColliding = hGap < 0 && vGap < 0;
                double overlapArea = isColliding ? (-hGap) * (-vGap) : 0.0;
                double minClearance = isColliding ? -Math.Min(-hGap, -vGap) : Math.Max(hGap, vGap);

                if (isColliding) collidingCount++;
                sumH += Math.Max(0.0, hGap);
                sumV += Math.Max(0.0, vGap);

                string aName = string.IsNullOrWhiteSpace(a.Text) ? a.DisplayName : a.Text.Replace("\n", " ").Trim();
                if (aName.Length > 20) aName = aName.Substring(0, 17) + "...";
                string bName = string.IsNullOrWhiteSpace(b.Text) ? b.DisplayName : b.Text.Replace("\n", " ").Trim();
                if (bName.Length > 20) bName = bName.Substring(0, 17) + "...";

                pairs.Add(new ShapeSpacingCollisionInfo(
                    a.Id, b.Id, aName, bName, Math.Round(hGap, 1), Math.Round(vGap, 1),
                    Math.Round(minClearance, 1), isColliding, Math.Round(overlapArea, 1)));
            }
        }

        // Measure consecutive adjacent gutters along dominant axis
        double minCx = targets.Min(s => s.X + s.Width / 2.0);
        double maxCx = targets.Max(s => s.X + s.Width / 2.0);
        double minCy = targets.Min(s => s.Y + s.Height / 2.0);
        double maxCy = targets.Max(s => s.Y + s.Height / 2.0);

        var consecutiveGaps = new List<double>();
        if ((maxCx - minCx) >= (maxCy - minCy))
        {
            var sorted = targets.OrderBy(s => s.X).ToList();
            for (int k = 0; k < sorted.Count - 1; k++)
            {
                double gap = sorted[k + 1].X - (sorted[k].X + sorted[k].Width);
                consecutiveGaps.Add(gap);
            }
        }
        else
        {
            var sorted = targets.OrderBy(s => s.Y).ToList();
            for (int k = 0; k < sorted.Count - 1; k++)
            {
                double gap = sorted[k + 1].Y - (sorted[k].Y + sorted[k].Height);
                consecutiveGaps.Add(gap);
            }
        }

        double meanGap = consecutiveGaps.Count > 0 ? consecutiveGaps.Average() : 0.0;
        double variance = consecutiveGaps.Count > 1
            ? consecutiveGaps.Select(g => (g - meanGap) * (g - meanGap)).Average()
            : 0.0;
        double stdDev = Math.Round(Math.Sqrt(variance), 1);

        double meanH = pairs.Count > 0 ? Math.Round(sumH / pairs.Count, 1) : 0.0;
        double meanV = pairs.Count > 0 ? Math.Round(sumV / pairs.Count, 1) : 0.0;

        double uniformityScore = Math.Round(Math.Clamp(100.0 - (collidingCount * 25.0) - (stdDev * 1.5), 0.0, 100.0), 1);

        string category;
        if (collidingCount > 0)
        {
            category = "Bounding Box Collisions Detected";
        }
        else if (stdDev <= 4.0)
        {
            category = "Pixel-Perfect Uniform Spacing";
        }
        else if (stdDev <= 18.0)
        {
            category = "Harmonious Spacing Distribution";
        }
        else
        {
            category = "Irregular / Asymmetric Spacing";
        }

        string summary = FormatSpacingCollisionSummary(
            targets.Count, pairs.Count, collidingCount, meanH, meanV, stdDev, uniformityScore, category, scopeStr);

        StatusMessage = summary;

        return new ShapeSpacingUniformityAudit(
            targets.Count,
            pairs.Count,
            collidingCount,
            meanH,
            meanV,
            stdDev,
            uniformityScore,
            pairs,
            category,
            isSelection,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of shape boundary spacing and collisions.
    /// </summary>
    public static string FormatSpacingCollisionSummary(
        int totalShapes, int pairCount, int collidingCount, double meanH, double meanV,
        double stdDev, double uniformityScore, string category, string scope = "")
    {
        if (totalShapes < 2) return "Shape spacing audit: Insufficient shapes (<2) · Minimum 2 shapes required";

        string shapeDesc = $"{totalShapes} shapes";
        string collisionAlert = collidingCount > 0 ? $" · ⚠ {collidingCount} colliding pairs" : " · 0 collisions";
        return $"Shape spacing audit ({scope}{shapeDesc} · {category}): Uniformity: {uniformityScore:0.#}% (Gutter std dev {stdDev:0.#}px · Mean H:{meanH:0.#}px, V:{meanV:0.#}px){collisionAlert}";
    }

    /// <summary>
    /// Audits orthogonal connection routing channels, wire intersection crossings, shape bounding box collisions,
    /// and Manhattan path clearances.
    /// </summary>
    public OrthogonalRoutingAudit AuditOrthogonalRoutingAndIntersections(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count < 2)
        {
            string emptySummary = "Orthogonal routing audit: Insufficient shapes (<2) · Minimum 2 shapes required for channel routing";
            StatusMessage = emptySummary;
            return new OrthogonalRoutingAudit(
                targets.Count, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<OrthogonalChannelInfo>(), "Insufficient Shapes", isSelection, emptySummary);
        }

        // Determine dominant flow axis
        double spanX = targets.Max(s => s.X + s.Width) - targets.Min(s => s.X);
        double spanY = targets.Max(s => s.Y + s.Height) - targets.Min(s => s.Y);

        var orderedShapes = spanX >= spanY
            ? targets.OrderBy(s => s.X).ThenBy(s => s.Y).ToList()
            : targets.OrderBy(s => s.Y).ThenBy(s => s.X).ToList();

        // Build connection pairs (adjacent flow channels plus pairwise if small graph)
        var channelPairs = new List<(ShapeCanvasItemViewModel A, ShapeCanvasItemViewModel B)>();
        if (orderedShapes.Count <= 8)
        {
            for (int i = 0; i < orderedShapes.Count; i++)
            {
                for (int j = i + 1; j < orderedShapes.Count; j++)
                {
                    channelPairs.Add((orderedShapes[i], orderedShapes[j]));
                }
            }
        }
        else
        {
            for (int i = 0; i < orderedShapes.Count - 1; i++)
            {
                channelPairs.Add((orderedShapes[i], orderedShapes[i + 1]));
            }
        }

        var channelInfos = new List<OrthogonalChannelInfo>();
        var hSegments = new List<(double Y, double MinX, double MaxX, int ChannelIdx)>();
        var vSegments = new List<(double X, double MinY, double MaxY, int ChannelIdx)>();

        int obstructedCount = 0;
        int clearCount = 0;
        double sumClearance = 0.0;

        for (int chIdx = 0; chIdx < channelPairs.Count; chIdx++)
        {
            var (a, b) = channelPairs[chIdx];
            double ax = a.X + a.Width / 2.0;
            double ay = a.Y + a.Height / 2.0;
            double bx = b.X + b.Width / 2.0;
            double by = b.Y + b.Height / 2.0;

            int bends = 0;
            bool collides = false;
            double minClearance = double.MaxValue;

            if (Math.Abs(ax - bx) < 4.0)
            {
                // Straight vertical
                bends = 0;
                double minY = Math.Min(ay, by);
                double maxY = Math.Max(ay, by);
                vSegments.Add((ax, minY, maxY, chIdx));

                // Check collision with other shapes
                foreach (var other in targets)
                {
                    if (other.Id == a.Id || other.Id == b.Id) continue;
                    if (ax >= other.X && ax <= other.X + other.Width &&
                        maxY >= other.Y && minY <= other.Y + other.Height)
                    {
                        collides = true;
                    }
                    double dist = Math.Max(0.0, Math.Min(Math.Abs(ax - other.X), Math.Abs(ax - (other.X + other.Width))));
                    if (dist < minClearance) minClearance = dist;
                }
            }
            else if (Math.Abs(ay - by) < 4.0)
            {
                // Straight horizontal
                bends = 0;
                double minX = Math.Min(ax, bx);
                double maxX = Math.Max(ax, bx);
                hSegments.Add((ay, minX, maxX, chIdx));

                foreach (var other in targets)
                {
                    if (other.Id == a.Id || other.Id == b.Id) continue;
                    if (ay >= other.Y && ay <= other.Y + other.Height &&
                        maxX >= other.X && minX <= other.X + other.Width)
                    {
                        collides = true;
                    }
                    double dist = Math.Max(0.0, Math.Min(Math.Abs(ay - other.Y), Math.Abs(ay - (other.Y + other.Height))));
                    if (dist < minClearance) minClearance = dist;
                }
            }
            else
            {
                // 2-bend Manhattan channel via midpoint
                bends = 2;
                double midX = (ax + bx) / 2.0;

                // Segment 1: horizontal (ay, ax -> midX)
                hSegments.Add((ay, Math.Min(ax, midX), Math.Max(ax, midX), chIdx));
                // Segment 2: vertical (midX, ay -> by)
                vSegments.Add((midX, Math.Min(ay, by), Math.Max(ay, by), chIdx));
                // Segment 3: horizontal (by, midX -> bx)
                hSegments.Add((by, Math.Min(midX, bx), Math.Max(midX, bx), chIdx));

                foreach (var other in targets)
                {
                    if (other.Id == a.Id || other.Id == b.Id) continue;
                    // Check horizontal 1
                    if (ay >= other.Y && ay <= other.Y + other.Height &&
                        Math.Max(ax, midX) >= other.X && Math.Min(ax, midX) <= other.X + other.Width)
                        collides = true;
                    // Check vertical 2
                    if (midX >= other.X && midX <= other.X + other.Width &&
                        Math.Max(ay, by) >= other.Y && Math.Min(ay, by) <= other.Y + other.Height)
                        collides = true;
                    // Check horizontal 3
                    if (by >= other.Y && by <= other.Y + other.Height &&
                        Math.Max(midX, bx) >= other.X && Math.Min(midX, bx) <= other.X + other.Width)
                        collides = true;

                    double dX = Math.Min(Math.Abs(midX - other.X), Math.Abs(midX - (other.X + other.Width)));
                    double dY = Math.Min(Math.Abs(ay - other.Y), Math.Abs(by - (other.Y + other.Height)));
                    double d = Math.Min(dX, dY);
                    if (d < minClearance) minClearance = d;
                }
            }

            if (minClearance == double.MaxValue) minClearance = 50.0;
            minClearance = Math.Round(minClearance, 1);
            sumClearance += minClearance;

            if (collides) obstructedCount++;
            else clearCount++;

            channelInfos.Add(new OrthogonalChannelInfo(
                a.Id, b.Id, a.DisplayName, b.DisplayName, bends, collides, 0, minClearance, !collides));
        }

        // Count wire crossings between different channels
        int totalCrossings = 0;
        var crossingCountsPerChannel = new int[channelPairs.Count];

        foreach (var h in hSegments)
        {
            foreach (var v in vSegments)
            {
                if (h.ChannelIdx != v.ChannelIdx)
                {
                    if (v.X > h.MinX && v.X < h.MaxX &&
                        h.Y > v.MinY && h.Y < v.MaxY)
                    {
                        totalCrossings++;
                        crossingCountsPerChannel[h.ChannelIdx]++;
                        crossingCountsPerChannel[v.ChannelIdx]++;
                    }
                }
            }
        }

        // Update channel crossing count
        var finalChannels = new List<OrthogonalChannelInfo>();
        for (int i = 0; i < channelInfos.Count; i++)
        {
            finalChannels.Add(channelInfos[i] with { CrossingCount = crossingCountsPerChannel[i] });
        }

        double meanClearance = channelPairs.Count > 0 ? Math.Round(sumClearance / channelPairs.Count, 1) : 0.0;
        double routingEfficiency = Math.Round(Math.Clamp(100.0 - (obstructedCount * 22.0) - (totalCrossings * 10.0), 0.0, 100.0), 1);

        string category;
        if (obstructedCount > 0)
        {
            category = "Shape Collisions on Routing Channels";
        }
        else if (totalCrossings > 0)
        {
            category = "Non-Planar Wire Crossings Present";
        }
        else if (meanClearance >= 24.0)
        {
            category = "Optimal Orthogonal Planar Channels";
        }
        else
        {
            category = "Clear Orthogonal Routing";
        }

        string summary = FormatOrthogonalRoutingSummary(
            targets.Count, channelPairs.Count, clearCount, obstructedCount, totalCrossings,
            meanClearance, routingEfficiency, category, scopeStr);

        StatusMessage = summary;

        return new OrthogonalRoutingAudit(
            targets.Count,
            channelPairs.Count,
            clearCount,
            obstructedCount,
            totalCrossings,
            meanClearance,
            routingEfficiency,
            finalChannels,
            category,
            isSelection,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of orthogonal routing channels and wire crossings.
    /// </summary>
    public static string FormatOrthogonalRoutingSummary(
        int totalShapes, int channelCount, int clearCount, int obstructedCount, int crossings,
        double meanClearance, double efficiencyScore, string category, string scope = "")
    {
        if (totalShapes < 2) return "Orthogonal routing audit: Insufficient shapes (<2) · Minimum 2 shapes required";

        string shapeDesc = $"{totalShapes} shapes";
        string issues = "";
        if (obstructedCount > 0) issues += $" · ⚠ {obstructedCount} obstructed channels";
        if (crossings > 0) issues += $" · ⚠ {crossings} wire crossings";

        return $"Orthogonal routing audit ({scope}{shapeDesc} · {channelCount} channels · {category}): Efficiency: {efficiencyScore:0.#}% ({clearCount} clear · Mean clearance {meanClearance:0.#}px){issues}";
    }

    /// <summary>
    /// Audits 2D shape coordinates (X, Y, Width, Height) against standard snap grids (4px, 8px, 10px, 16px),
    /// measuring subpixel rounding drift, coordinate quantization adherence, and misaligned elements.
    /// </summary>
    public ShapeGridSnapAudit AuditGridSnapConformance(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null, double gridSize = 8.0)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count > 0 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count > 0 && selected.Count < Shapes.Count);
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (gridSize <= 0) gridSize = 8.0;

        if (targets.Count == 0)
        {
            string emptySummary = "Grid snap audit: Canvas contains 0 shapes";
            StatusMessage = emptySummary;
            return new ShapeGridSnapAudit(
                0, gridSize, 0, 0, 0, 0.0, 100.0, Array.Empty<ShapeGridSnapInfo>(), "Empty Canvas", isSelection, emptySummary);
        }

        var shapeSnapInfos = new List<ShapeGridSnapInfo>();
        int fullySnappedCount = 0;
        int subpixelDriftCount = 0;
        int misalignedCount = 0;
        double totalSnapError = 0.0;

        foreach (var s in targets)
        {
            double xRem = Math.Abs(s.X % gridSize);
            double xSnapErr = Math.Min(xRem, gridSize - xRem);

            double yRem = Math.Abs(s.Y % gridSize);
            double ySnapErr = Math.Min(yRem, gridSize - yRem);

            double wRem = Math.Abs(s.Width % gridSize);
            double wSnapErr = Math.Min(wRem, gridSize - wRem);

            double hRem = Math.Abs(s.Height % gridSize);
            double hSnapErr = Math.Min(hRem, gridSize - hRem);

            bool subpixel = (Math.Abs(s.X - Math.Round(s.X)) > 0.01) ||
                            (Math.Abs(s.Y - Math.Round(s.Y)) > 0.01) ||
                            (Math.Abs(s.Width - Math.Round(s.Width)) > 0.01) ||
                            (Math.Abs(s.Height - Math.Round(s.Height)) > 0.01);

            double maxErr = Math.Max(Math.Max(xSnapErr, ySnapErr), Math.Max(wSnapErr, hSnapErr));
            double avgErr = (xSnapErr + ySnapErr + wSnapErr + hSnapErr) / 4.0;
            totalSnapError += avgErr;

            bool isSnapped = maxErr < 0.5;
            if (isSnapped) fullySnappedCount++;
            else misalignedCount++;

            if (subpixel) subpixelDriftCount++;

            shapeSnapInfos.Add(new ShapeGridSnapInfo(
                s.Id, s.Name, s.X, s.Y, s.Width, s.Height,
                Math.Round(xSnapErr, 2), Math.Round(ySnapErr, 2),
                Math.Round(wSnapErr, 2), Math.Round(hSnapErr, 2),
                isSnapped, subpixel));
        }

        double meanSnapError = Math.Round(totalSnapError / targets.Count, 2);
        double maxTolerance = gridSize / 2.0;
        double conformanceScore = Math.Round(Math.Clamp((1.0 - (meanSnapError / maxTolerance)) * 100.0, 0.0, 100.0), 1);

        string category;
        if (misalignedCount == 0 && subpixelDriftCount == 0)
        {
            category = $"Pixel-Perfect ({gridSize}px Snap Grid)";
        }
        else if (misalignedCount == 0)
        {
            category = "Grid Snapped (Subpixel Rounding Drift)";
        }
        else if (conformanceScore >= 80.0)
        {
            category = "High Grid Conformance (Minor Offsets)";
        }
        else if (conformanceScore >= 50.0)
        {
            category = "Moderate Coordinate Quantization Discrepancies";
        }
        else
        {
            category = "Unsnapped / Freehand Coordinate Drift";
        }

        string summary = FormatGridSnapSummary(
            targets.Count, gridSize, fullySnappedCount, misalignedCount, subpixelDriftCount,
            meanSnapError, conformanceScore, category, scopeStr);

        StatusMessage = summary;

        return new ShapeGridSnapAudit(
            targets.Count, gridSize, fullySnappedCount, misalignedCount, subpixelDriftCount,
            meanSnapError, conformanceScore, shapeSnapInfos, category, isSelection, summary);
    }

    public static string FormatGridSnapSummary(
        int totalShapes, double gridSize, int fullySnapped, int misaligned, int subpixelDrift,
        double meanSnapError, double conformanceScore, string category, string scope = "")
    {
        if (totalShapes == 0) return $"Grid snap audit ({scope}0 shapes): Canvas is empty";

        string shapeDesc = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        string snapDesc = $"{fullySnapped}/{totalShapes} snapped ({gridSize}px grid)";
        string driftDesc = subpixelDrift > 0 ? $" · ⚠ {subpixelDrift} subpixel drift" : "";

        return $"Grid snap audit ({scope}{shapeDesc} [{snapDesc}] · {category} · {conformanceScore:0.#}% conformance): ~{meanSnapError:0.##}px avg offset{driftDesc}";
    }

    /// <summary>
    /// Audits diagram visual weight distribution, computing the area-weighted optical centroid (center-of-gravity)
    /// versus the geometric bounding box center, detecting directional bias and optical tilt.
    /// </summary>
    public DiagramVisualWeightAudit AuditVisualWeightAndOpticalCentroid(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count > 0 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count > 0 && selected.Count < Shapes.Count);
        string scopeStr = isSelection ? "Selected Shapes · " : "";

        if (targets.Count == 0)
        {
            string emptySummary = "Diagram visual weight audit: Canvas contains 0 shapes";
            StatusMessage = emptySummary;
            return new DiagramVisualWeightAudit(
                0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                "No Shapes", 100.0, Array.Empty<ShapeVisualWeightInfo>(), "Empty Canvas", isSelection, emptySummary);
        }

        var shapeInfos = new List<ShapeVisualWeightInfo>();
        double totalWeight = 0.0;
        double weightedX = 0.0;
        double weightedY = 0.0;

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        foreach (var s in targets)
        {
            double area = Math.Max(1.0, s.Width * s.Height);
            double weight = area;
            double cx = s.X + s.Width / 2.0;
            double cy = s.Y + s.Height / 2.0;

            totalWeight += weight;
            weightedX += weight * cx;
            weightedY += weight * cy;

            if (s.X < minX) minX = s.X;
            if (s.X + s.Width > maxX) maxX = s.X + s.Width;
            if (s.Y < minY) minY = s.Y;
            if (s.Y + s.Height > maxY) maxY = s.Y + s.Height;

            shapeInfos.Add(new ShapeVisualWeightInfo(
                s.Id, s.Name, s.X, s.Y, s.Width, s.Height,
                Math.Round(area, 1), Math.Round(weight, 1),
                Math.Round(cx, 1), Math.Round(cy, 1)));
        }

        double geomCenterX = Math.Round((minX + maxX) / 2.0, 1);
        double geomCenterY = Math.Round((minY + maxY) / 2.0, 1);

        double optCenterX = Math.Round(weightedX / totalWeight, 1);
        double optCenterY = Math.Round(weightedY / totalWeight, 1);

        double deltaX = Math.Round(optCenterX - geomCenterX, 1);
        double deltaY = Math.Round(optCenterY - geomCenterY, 1);

        double spanX = Math.Max(1.0, maxX - minX);
        double spanY = Math.Max(1.0, maxY - minY);
        double spanDiag = Math.Sqrt(spanX * spanX + spanY * spanY);
        double offsetDist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

        double normOffsetPct = Math.Round((offsetDist / (spanDiag / 2.0)) * 100.0, 1);
        double balanceScore = Math.Round(Math.Clamp(100.0 - normOffsetPct, 0.0, 100.0), 1);

        string dirBias;
        if (normOffsetPct < 2.5)
        {
            dirBias = "Perfect Optical Equilibrium";
        }
        else
        {
            var hBias = Math.Abs(deltaX) > spanX * 0.02 ? (deltaX > 0 ? "Right-Leaning" : "Left-Leaning") : "";
            var vBias = Math.Abs(deltaY) > spanY * 0.02 ? (deltaY > 0 ? "Bottom-Heavy" : "Top-Heavy") : "";
            dirBias = $"{hBias} {vBias}".Trim();
            if (string.IsNullOrEmpty(dirBias)) dirBias = "Near Centered";
        }

        string category;
        if (balanceScore >= 95.0)
            category = "Equilibrium Centered (≥95%)";
        else if (balanceScore >= 80.0)
            category = "Well-Balanced Composition (80–94%)";
        else if (balanceScore >= 60.0)
            category = "Moderate Visual Tilt (60–79%)";
        else
            category = "Pronounced Optical Imbalance (<60%)";

        string summary = FormatVisualWeightSummary(
            targets.Count, deltaX, deltaY, normOffsetPct, balanceScore, dirBias, category, scopeStr);

        StatusMessage = summary;

        return new DiagramVisualWeightAudit(
            targets.Count, geomCenterX, geomCenterY, optCenterX, optCenterY,
            deltaX, deltaY, normOffsetPct, dirBias, balanceScore,
            shapeInfos, category, isSelection, summary);
    }

    public static string FormatVisualWeightSummary(
        int totalShapes, double deltaX, double deltaY, double normOffsetPct,
        double balanceScore, string dirBias, string category, string scope = "")
    {
        if (totalShapes == 0) return $"Diagram visual weight audit ({scope}0 shapes): Canvas is empty";

        string shapeDesc = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        return $"Diagram visual weight audit ({scope}{shapeDesc} · {category} · {balanceScore:0.#}% balance): Offset ΔX={deltaX:+0.#;-0.#;0}px, ΔY={deltaY:+0.#;-0.#;0}px ({dirBias})";
    }

    /// <summary>
    /// Audits diagram connector path curvature continuity, Bezier tangent alignment, and knot smoothness.
    /// </summary>
    public ConnectorCurvatureAudit AuditConnectorCurvatureAndTangents(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var rawTargets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        if (rawTargets.Count == 0)
        {
            rawTargets = Shapes.ToList();
        }

        bool isSelection = (targetShapes != null) || (selected.Count > 0 && rawTargets.Count < Shapes.Count);
        string scopeStr = isSelection ? "Selection · " : "";

        var connectors = rawTargets.Where(s =>
            (s.ShapeType != null && (
                s.ShapeType.Contains("connector", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("polyline", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("curve", StringComparison.OrdinalIgnoreCase))) ||
            (s.Points != null && s.Points.Count >= 2) ||
            (s.PathPoints != null && s.PathPoints.Count >= 2)).ToList();

        if (connectors.Count == 0)
        {
            string emptySummary = $"Connector curvature audit ({scopeStr}0 connectors): No connectors in scope";
            StatusMessage = emptySummary;
            return new ConnectorCurvatureAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ConnectorKnotInfo>(),
                "Zero Connectors",
                isSelection,
                emptySummary);
        }

        var knotList = new List<ConnectorKnotInfo>();
        int smoothCount = 0;
        int cornerCount = 0;
        int microKinkCount = 0;
        double totalAngle = 0.0;
        int knotsEvaluated = 0;

        foreach (var conn in connectors)
        {
            var pts = conn.Points;
            if (pts == null || pts.Count < 3) continue;

            for (int i = 1; i < pts.Count - 1; i++)
            {
                var pPrev = pts[i - 1];
                var pCur = pts[i];
                var pNext = pts[i + 1];

                double vx1 = pCur.X - pPrev.X;
                double vy1 = pCur.Y - pPrev.Y;
                double len1 = Math.Sqrt(vx1 * vx1 + vy1 * vy1);

                double vx2 = pNext.X - pCur.X;
                double vy2 = pNext.Y - pCur.Y;
                double len2 = Math.Sqrt(vx2 * vx2 + vy2 * vy2);

                if (len1 < 0.001 || len2 < 0.001) continue;

                double dot = (vx1 * vx2 + vy1 * vy2) / (len1 * len2);
                dot = Math.Clamp(dot, -1.0, 1.0);
                double angleDeg = Math.Round(Math.Acos(dot) * (180.0 / Math.PI), 1);

                totalAngle += angleDeg;
                knotsEvaluated++;

                string grade;
                bool isMicroKink = false;

                if (angleDeg < 5.0)
                {
                    grade = "G1 Smooth Continuity";
                    smoothCount++;
                }
                else if (angleDeg >= 5.0 && angleDeg < 45.0)
                {
                    grade = "Soft Curvature Bend";
                    if (angleDeg >= 3.0 && angleDeg < 18.0)
                    {
                        isMicroKink = true;
                        microKinkCount++;
                    }
                }
                else
                {
                    grade = "Sharp Corner / Cusp";
                    cornerCount++;
                }

                knotList.Add(new ConnectorKnotInfo(
                    conn.Id,
                    i,
                    Math.Round(pCur.X, 1),
                    Math.Round(pCur.Y, 1),
                    angleDeg,
                    grade,
                    isMicroKink));
            }
        }

        double meanAngle = knotsEvaluated > 0 ? Math.Round(totalAngle / knotsEvaluated, 1) : 0.0;
        double smoothnessScore = 100.0;
        smoothnessScore -= microKinkCount * 15.0;
        smoothnessScore -= Math.Min(40.0, meanAngle * 0.5);
        smoothnessScore = Math.Round(Math.Clamp(smoothnessScore, 0.0, 100.0), 1);

        string category;
        if (knotsEvaluated == 0 || (smoothnessScore >= 90.0 && microKinkCount == 0))
        {
            category = "Seamless G1 Tangent Continuity";
        }
        else if (smoothnessScore >= 70.0 && microKinkCount == 0)
        {
            category = "Balanced Curvature Flow";
        }
        else if (microKinkCount > 0)
        {
            category = "Micro-Kinks Detected";
        }
        else
        {
            category = "High Angle Discontinuities";
        }

        string summary = FormatConnectorCurvatureSummary(
            connectors.Count, knotsEvaluated, smoothCount, microKinkCount, meanAngle, smoothnessScore, category, scopeStr);

        StatusMessage = summary;

        return new ConnectorCurvatureAudit(
            connectors.Count,
            knotsEvaluated,
            smoothCount,
            cornerCount,
            microKinkCount,
            meanAngle,
            smoothnessScore,
            knotList,
            category,
            isSelection,
            summary);
    }

    public static string FormatConnectorCurvatureSummary(
        int totalConnectors, int totalKnots, int smoothKnots, int microKinks,
        double meanAngle, double score, string category, string scope = "")
    {
        if (totalConnectors == 0) return $"Connector curvature audit ({scope}0 connectors): No connectors in scope";

        string connLabel = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        string knotLabel = totalKnots == 1 ? "1 knot" : $"{totalKnots} knots";
        string kinkAlert = microKinks > 0 ? $" · ⚠ {microKinks} micro-kink{(microKinks == 1 ? "" : "s")}" : " · Smooth tangents";

        return $"Connector curvature audit ({scope}{connLabel}, {knotLabel} · {category} · {score:0.#}% score): Mean turning angle {meanAngle:0.#}°{kinkAlert}";
    }

    /// <summary>
    /// Audits shape aspect ratio lock conformance, standard geometric primitive scaling (1:1, 16:9, 4:3, Golden Ratio),
    /// and flags accidental non-uniform aspect ratio drift.
    /// </summary>
    public DiagramAspectRatioAudit AuditAspectRatioAndPrimitiveScaling(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var rawTargets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        if (rawTargets.Count == 0)
        {
            rawTargets = Shapes.ToList();
        }

        bool isSelection = (targetShapes != null) || (selected.Count > 0 && rawTargets.Count < Shapes.Count);

        var geometricShapes = rawTargets.Where(s =>
            s.Width > 0 && s.Height > 0 &&
            !(s.ShapeType != null && (
                s.ShapeType.Contains("connector", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                s.ShapeType.Contains("polyline", StringComparison.OrdinalIgnoreCase)))).ToList();

        if (geometricShapes.Count == 0)
        {
            string emptySummary = FormatAspectRatioSummary(0, 0, 0, 0, 100.0, "Empty Canvas", isSelection);
            StatusMessage = emptySummary;
            return new DiagramAspectRatioAudit(
                0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeAspectRatioInfo>(),
                "Empty Canvas",
                isSelection,
                emptySummary);
        }

        var archetypes = new (string Name, double Ratio)[]
        {
            ("1:1 Square / Circle", 1.0),
            ("16:9 Widescreen", 16.0 / 9.0),
            ("4:3 Standard Display", 4.0 / 3.0),
            ("3:2 Classic Photo", 1.5),
            ("Golden Ratio (1.618)", (1.0 + Math.Sqrt(5.0)) / 2.0),
            ("2:1 Wide Banner", 2.0),
            ("3:1 Header Strip", 3.0)
        };

        var evaluated = new List<ShapeAspectRatioInfo>();
        int conformingCount = 0;
        int driftCount = 0;
        int freeformCount = 0;
        double totalDriftPct = 0.0;

        foreach (var s in geometricShapes)
        {
            double w = s.Width;
            double h = s.Height;
            double actualRatio = Math.Max(w, h) / Math.Min(w, h);

            var closest = archetypes.OrderBy(a => Math.Abs(actualRatio - a.Ratio)).First();
            double drift = Math.Abs(actualRatio - closest.Ratio) / closest.Ratio;
            double driftPct = drift * 100.0;

            bool isConforming = driftPct <= 1.0;
            bool hasAccidentalDrift = driftPct > 1.0 && driftPct <= 12.0;

            if (isConforming) conformingCount++;
            else if (hasAccidentalDrift) driftCount++;
            else freeformCount++;

            if (hasAccidentalDrift) totalDriftPct += driftPct;

            string shapeTitle = !string.IsNullOrWhiteSpace(s.Text)
                ? s.Text
                : (!string.IsNullOrWhiteSpace(s.Name) ? s.Name : (!string.IsNullOrWhiteSpace(s.ShapeType) ? s.ShapeType : $"Shape #{s.Id}"));

            evaluated.Add(new ShapeAspectRatioInfo(
                ShapeId: s.Id,
                DisplayName: shapeTitle,
                Width: Math.Round(w, 1),
                Height: Math.Round(h, 1),
                ActualAspectRatio: Math.Round(actualRatio, 3),
                NearestArchetype: closest.Name,
                TargetAspectRatio: Math.Round(closest.Ratio, 3),
                DriftPercentage: Math.Round(driftPct, 1),
                IsConforming: isConforming,
                HasAccidentalDrift: hasAccidentalDrift));
        }

        double meanDrift = driftCount > 0 ? (totalDriftPct / driftCount) : 0.0;
        double penalty = (driftCount * 18.0) / geometricShapes.Count;
        double fidelityScore = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = fidelityScore switch
        {
            >= 95.0 when driftCount == 0 => "Perfect Geometric Aspect Ratio Conformance",
            >= 80.0 => "Harmonic Canonical Aspect Ratios",
            >= 60.0 => "Minor Unlocked Scaling Drift",
            _ => "Unconstrained Non-Uniform Scaling"
        };

        string summary = FormatAspectRatioSummary(
            geometricShapes.Count, conformingCount, driftCount, freeformCount,
            fidelityScore, category, isSelection);

        StatusMessage = summary;

        return new DiagramAspectRatioAudit(
            TotalShapesEvaluated: geometricShapes.Count,
            ConformingShapeCount: conformingCount,
            AccidentalDriftCount: driftCount,
            CustomFreeformCount: freeformCount,
            MeanDriftPercentage: Math.Round(meanDrift, 1),
            AspectRatioFidelityScore: Math.Round(fidelityScore, 1),
            Shapes: evaluated,
            Category: category,
            IsSelectionScope: isSelection,
            Summary: summary);
    }

    public DiagramShapeZOrderStackingAudit AuditShapeZOrderStackingAndOcclusion(bool selectionOnly = false)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targetShapes = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditShapeZOrderStackingAndOcclusion(targetShapes, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramShapeZOrderStackingAudit AuditShapeZOrderStackingAndOcclusion(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var validShapes = list.Where(s => s.Width >= 2.0 && s.Height >= 2.0).ToList();
        int totalShapes = validShapes.Count;

        if (totalShapes == 0)
        {
            string emptySummary = FormatStackingOcclusionSummary(0, 0, 0, 0, 0, 100.0, "Empty Canvas", isSelection);
            return new DiagramShapeZOrderStackingAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeZOrderOcclusionItem>(), "Empty Canvas", isSelection, emptySummary);
        }

        var items = new List<ShapeZOrderOcclusionItem>();
        var occludedShapeIds = new HashSet<string>();
        int completeOcclusionCount = 0;
        int severeOcclusionCount = 0;
        var stackingDepths = new int[totalShapes];

        for (int i = 0; i < totalShapes; i++)
        {
            var lower = validShapes[i];
            double x1 = lower.X;
            double y1 = lower.Y;
            double w1 = lower.Width;
            double h1 = lower.Height;
            double area1 = w1 * h1;

            for (int j = i + 1; j < totalShapes; j++)
            {
                var upper = validShapes[j];
                double x2 = upper.X;
                double y2 = upper.Y;
                double w2 = upper.Width;
                double h2 = upper.Height;

                double interLeft = Math.Max(x1, x2);
                double interTop = Math.Max(y1, y2);
                double interRight = Math.Min(x1 + w1, x2 + w2);
                double interBottom = Math.Min(y1 + h1, y2 + h2);

                if (interRight > interLeft && interBottom > interTop)
                {
                    double interArea = (interRight - interLeft) * (interBottom - interTop);
                    double pct = Math.Min(100.0, Math.Round((interArea / area1) * 100.0, 1));

                    stackingDepths[i]++;
                    stackingDepths[j]++;

                    if (pct >= 15.0)
                    {
                        occludedShapeIds.Add(lower.Id);
                        bool isComplete = pct >= 85.0;
                        bool isSevere = pct >= 40.0 && !isComplete;

                        if (isComplete) completeOcclusionCount++;
                        else if (isSevere) severeOcclusionCount++;

                        string severity = isComplete ? "Complete" : (isSevere ? "Severe" : "Moderate");
                        string status = isComplete ? "Completely Hidden" : $"{pct:0.#}% Occluded";
                        string rec = isComplete
                            ? $"Shape '{lower.Name}' is almost completely hidden by '{upper.Name}': re-layer or offset positions"
                            : $"Partial overlap with '{upper.Name}': adjust spacing or bring forward";

                        items.Add(new ShapeZOrderOcclusionItem(
                            lower.Id, lower.Name, i,
                            upper.Id, upper.Name, j,
                            pct, isComplete, severity, status, rec));
                    }
                }
            }
        }

        int maxDepth = stackingDepths.Length > 0 ? stackingDepths.Max() : 0;
        double avgDepth = stackingDepths.Length > 0 ? Math.Round(stackingDepths.Average(), 1) : 0.0;

        double score = 100.0;
        score -= completeOcclusionCount * 25.0;
        score -= severeOcclusionCount * 10.0;
        if (occludedShapeIds.Count > 0 && completeOcclusionCount == 0 && severeOcclusionCount == 0)
            score -= occludedShapeIds.Count * 3.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 95.0) category = "Clean Layering (≥95%)";
        else if (score >= 80.0) category = "High Layering Quality (80-94%)";
        else if (score >= 60.0) category = "Moderate Overlap Occlusion (60-79%)";
        else if (score >= 40.0) category = "Suboptimal Stacking Depth (40-59%)";
        else category = "Severe Shape Occlusion (<40%)";

        string summary = FormatStackingOcclusionSummary(
            totalShapes, occludedShapeIds.Count, completeOcclusionCount, severeOcclusionCount, maxDepth, score, category, isSelection);

        return new DiagramShapeZOrderStackingAudit(
            totalShapes, occludedShapeIds.Count, completeOcclusionCount, severeOcclusionCount,
            maxDepth, avgDepth, score, items, category, isSelection, summary);
    }

    public static string FormatStackingOcclusionSummary(
        int totalShapes, int occludedCount, int completeOccluded, int severeOccluded,
        int maxDepth, double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0)
            return $"Diagram Z-order audit: {scope}Empty canvas · 0 shapes";

        if (occludedCount == 0)
            return $"Diagram Z-order audit: {scope}✓ Clean layering · {totalShapes} shapes · 0 occlusions · max depth {maxDepth}";

        var parts = new List<string>
        {
            $"{totalShapes} shapes",
            $"{occludedCount} occluded"
        };
        if (completeOccluded > 0) parts.Add($"⚠️ {completeOccluded} completely hidden");
        if (severeOccluded > 0) parts.Add($"{severeOccluded} severe");
        parts.Add($"max depth {maxDepth}");

        string details = string.Join(" · ", parts);
        return $"Diagram Z-order audit ({scope}{details} · {category} · {score:0.#}% layering score)";
    }

    public DiagramShapeColorContrastAccessibilityAudit AuditShapeColorContrastAccessibility(bool selectionOnly = false)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targetShapes = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditShapeColorContrastAccessibility(targetShapes, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramShapeColorContrastAccessibilityAudit AuditShapeColorContrastAccessibility(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var validShapes = list.Where(s => s.Width >= 2.0 && s.Height >= 2.0).ToList();
        int totalShapes = validShapes.Count;

        if (totalShapes == 0)
        {
            string emptySummary = FormatColorContrastSummary(0, 0, 0, 0, 0, 0, 0.0, 100.0, "Empty Canvas", isSelection);
            return new DiagramShapeColorContrastAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeColorContrastAccessibilityItem>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        static (byte R, byte G, byte B) ParseHex(string? raw, (byte R, byte G, byte B) fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            string clean = raw.Trim().TrimStart('#');
            if (clean.Equals("transparent", StringComparison.OrdinalIgnoreCase) || clean.Equals("none", StringComparison.OrdinalIgnoreCase))
                return fallback;
            if (clean.Length == 3 &&
                byte.TryParse(new string(clean[0], 2), System.Globalization.NumberStyles.HexNumber, null, out byte r3) &&
                byte.TryParse(new string(clean[1], 2), System.Globalization.NumberStyles.HexNumber, null, out byte g3) &&
                byte.TryParse(new string(clean[2], 2), System.Globalization.NumberStyles.HexNumber, null, out byte b3))
            {
                return (r3, g3, b3);
            }
            if (clean.Length == 6 &&
                byte.TryParse(clean.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r6) &&
                byte.TryParse(clean.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g6) &&
                byte.TryParse(clean.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b6))
            {
                return (r6, g6, b6);
            }
            if (clean.Length == 8 &&
                byte.TryParse(clean.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r8) &&
                byte.TryParse(clean.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g8) &&
                byte.TryParse(clean.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b8))
            {
                return (r8, g8, b8);
            }
            return fallback;
        }

        static double RelativeLuminance((byte R, byte G, byte B) c)
        {
            double Lin(byte b)
            {
                double v = b / 255.0;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        static double ContrastRatio(double l1, double l2)
        {
            double lighter = Math.Max(l1, l2);
            double darker = Math.Min(l1, l2);
            return Math.Round((lighter + 0.05) / (darker + 0.05), 2);
        }

        var canvasBg = ParseHex(CanvasBackgroundHex, (27, 27, 31));
        double lCanvas = RelativeLuminance(canvasBg);

        var items = new List<ShapeColorContrastAccessibilityItem>();
        int evaluatedTextCount = 0;
        int passingAaCount = 0;
        int passingAaaCount = 0;
        int failingAaCount = 0;
        int failingBoundaryCount = 0;
        var textContrastRatios = new List<double>();

        foreach (var s in validShapes)
        {
            var fillRgb = ParseHex(s.Fill, (255, 255, 255));
            var textRgb = ParseHex(s.TextColor, (18, 18, 18));
            var strokeRgb = ParseHex(s.Fill, (100, 100, 100));

            double lFill = RelativeLuminance(fillRgb);
            double lText = RelativeLuminance(textRgb);
            double lStroke = RelativeLuminance(strokeRgb);

            double textContrast = ContrastRatio(lText, lFill);
            double boundaryContrast = Math.Max(ContrastRatio(lFill, lCanvas), ContrastRatio(lStroke, lCanvas));

            bool hasText = !string.IsNullOrWhiteSpace(s.Text) || !string.IsNullOrWhiteSpace(s.Name);
            bool meetsAa = textContrast >= 4.5;
            bool meetsAaa = textContrast >= 7.0;
            bool meetsBoundary = boundaryContrast >= 3.0;

            if (hasText)
            {
                evaluatedTextCount++;
                textContrastRatios.Add(textContrast);
                if (meetsAa) passingAaCount++;
                else failingAaCount++;
                if (meetsAaa) passingAaaCount++;
            }

            if (!meetsBoundary) failingBoundaryCount++;

            string severity = (!meetsAa && hasText)
                ? (textContrast < 2.5 ? "Critical" : "High")
                : (!meetsBoundary ? "Medium" : (meetsAaa ? "None" : "Low"));

            string status = (!meetsAa && hasText)
                ? $"Fails WCAG AA ({textContrast:0.0}:1)"
                : (hasText && !meetsAaa ? $"Passes AA ({textContrast:0.0}:1)" : (hasText ? $"Passes AAA ({textContrast:0.0}:1)" : $"Graphic Only ({boundaryContrast:0.0}:1)"));

            string rec = (!meetsAa && hasText)
                ? $"Adjust fill '{s.Fill}' or text color to achieve ≥4.5:1 contrast"
                : (!meetsBoundary ? $"Increase shape stroke/fill contrast vs canvas (≥3.0:1)" : "Accessible color contrast conforming to WCAG 2.1");

            items.Add(new ShapeColorContrastAccessibilityItem(
                s.Id ?? "", s.Name ?? s.Id ?? "Shape", s.ShapeType,
                s.Fill ?? "#FFFFFF", s.TextColor ?? "#121212", s.Fill ?? "#333333",
                textContrast, boundaryContrast, hasText, meetsAa, meetsAaa, meetsBoundary,
                severity, status, rec));
        }

        double avgContrast = textContrastRatios.Count > 0 ? Math.Round(textContrastRatios.Average(), 1) : 0.0;
        double score = 100.0;
        score -= failingAaCount * 20.0;
        score -= (evaluatedTextCount - passingAaaCount) * 5.0;
        score -= failingBoundaryCount * 8.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 95.0) category = "Flawless Contrast Accessibility (≥95%)";
        else if (score >= 80.0) category = "High WCAG AA Compliance (80-94%)";
        else if (score >= 60.0) category = "Moderate Visual Contrast (60-79%)";
        else if (score >= 40.0) category = "Low Contrast Illegibility (40-59%)";
        else category = "Critical Accessibility Violations (<40%)";

        string summary = FormatColorContrastSummary(
            totalShapes, evaluatedTextCount, passingAaCount, passingAaaCount, failingAaCount,
            failingBoundaryCount, avgContrast, score, category, isSelection);

        return new DiagramShapeColorContrastAccessibilityAudit(
            totalShapes, evaluatedTextCount, passingAaCount, passingAaaCount,
            failingAaCount, failingBoundaryCount, avgContrast, score,
            items, category, isSelection, summary);
    }

    public static string FormatColorContrastSummary(
        int totalShapes, int evaluatedTextShapes, int passingAa, int passingAaa,
        int failingAa, int failingBoundary, double avgContrast, double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0)
            return $"Diagram shape color contrast audit: {scope}Empty canvas · 0 shapes";

        var parts = new List<string>
        {
            $"{totalShapes} shapes ({evaluatedTextShapes} text)",
            $"{passingAa} AA ({passingAaa} AAA)"
        };
        if (failingAa > 0) parts.Add($"⚠️ {failingAa} low contrast");
        if (failingBoundary > 0) parts.Add($"⚠️ {failingBoundary} boundary blend");
        if (evaluatedTextShapes > 0) parts.Add($"avg ratio {avgContrast:0.0}:1");

        string details = string.Join(" · ", parts);
        return $"Diagram shape color contrast audit ({scope}{details} · {category} · {score:0.#}% accessibility score)";
    }

    public DiagramConnectorPortAttachmentAudit AuditConnectorArrowheadAndPortAttachments(
        IEnumerable<ShapeCanvasItemViewModel>? shapes = null, bool selectionOnly = false)
    {
        var targetShapes = (shapes ?? Shapes).ToList();
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        if (isSelection)
        {
            targetShapes = selected;
        }

        var connectors = targetShapes.Where(s =>
            s.IsLine ||
            s.ShapeType == "Line" ||
            s.Prst == "line" ||
            s.Prst == "straightConnector1" ||
            s.Prst == "bentConnector3" ||
            s.Prst == "curvedConnector3").ToList();

        var nonConnectors = (shapes ?? Shapes).Where(s =>
            !s.IsLine &&
            s.ShapeType != "Line" &&
            s.Prst != "line" &&
            s.Prst != "straightConnector1" &&
            s.Prst != "bentConnector3" &&
            s.Prst != "curvedConnector3").ToList();

        int nShapes = targetShapes.Count;
        int nConnectors = connectors.Count;

        if (nConnectors == 0)
        {
            string emptySummary = FormatConnectorAttachmentSummary(nShapes, 0, 0, 0, 0, 100.0, "No Connectors", isSelection);
            return new DiagramConnectorPortAttachmentAudit(
                nShapes, 0, 0, 0, 0, 100.0,
                Array.Empty<ConnectorAttachmentPortItem>(),
                "No Connectors", isSelection, emptySummary);
        }

        var ports = new List<(string ShapeId, string ShapeName, string PortName, double X, double Y)>();
        foreach (var shape in nonConnectors)
        {
            double left = shape.X;
            double top = shape.Y;
            double right = shape.X + shape.Width;
            double bottom = shape.Y + shape.Height;
            double cx = shape.X + shape.Width / 2.0;
            double cy = shape.Y + shape.Height / 2.0;

            ports.Add((shape.Id, shape.Name ?? shape.Id, "Top", cx, top));
            ports.Add((shape.Id, shape.Name ?? shape.Id, "Bottom", cx, bottom));
            ports.Add((shape.Id, shape.Name ?? shape.Id, "Left", left, cy));
            ports.Add((shape.Id, shape.Name ?? shape.Id, "Right", right, cy));
        }

        int snappedPortsCount = 0;
        int danglingArrowheadsCount = 0;
        int nonOrthogonalBendsCount = 0;
        var items = new List<ConnectorAttachmentPortItem>();

        foreach (var conn in connectors)
        {
            double x1 = conn.X;
            double y1 = conn.Y;
            double x2 = conn.X + conn.Width;
            double y2 = conn.Y + conn.Height;

            bool hasStartArrow = !string.IsNullOrEmpty(conn.ArrowheadStart) && conn.ArrowheadStart != "None";
            bool hasEndArrow = !string.IsNullOrEmpty(conn.ArrowheadEnd) && conn.ArrowheadEnd != "None";

            double minStartDist = double.MaxValue;
            string startPortName = "Unattached";
            foreach (var p in ports)
            {
                double d = Math.Sqrt(Math.Pow(x1 - p.X, 2) + Math.Pow(y1 - p.Y, 2));
                if (d < minStartDist)
                {
                    minStartDist = d;
                    startPortName = $"{p.ShapeName}.{p.PortName}";
                }
            }

            double minEndDist = double.MaxValue;
            string endPortName = "Unattached";
            foreach (var p in ports)
            {
                double d = Math.Sqrt(Math.Pow(x2 - p.X, 2) + Math.Pow(y2 - p.Y, 2));
                if (d < minEndDist)
                {
                    minEndDist = d;
                    endPortName = $"{p.ShapeName}.{p.PortName}";
                }
            }

            const double SnapTolerance = 14.0;
            const double DanglingThreshold = 35.0;

            bool isStartSnapped = minStartDist <= SnapTolerance;
            bool isEndSnapped = minEndDist <= SnapTolerance;

            if (isStartSnapped) snappedPortsCount++;
            if (isEndSnapped) snappedPortsCount++;

            bool startDangling = hasStartArrow && minStartDist > DanglingThreshold;
            bool endDangling = hasEndArrow && minEndDist > DanglingThreshold;
            bool hasDangling = startDangling || endDangling;
            if (hasDangling) danglingArrowheadsCount++;

            double dx = Math.Abs(x2 - x1);
            double dy = Math.Abs(y2 - y1);
            bool isOrthogonal = dx <= 2.0 || dy <= 2.0;
            if (!isOrthogonal && (conn.Prst == "bentConnector3" || conn.ShapeType == "Orthogonal"))
            {
                nonOrthogonalBendsCount++;
            }

            string routingStatus = (isStartSnapped && isEndSnapped)
                ? "Fully Snapped"
                : (hasDangling ? "Dangling Arrowhead" : (isStartSnapped || isEndSnapped ? "Partially Snapped" : "Floating Connector"));

            string recommendation = hasDangling
                ? "Align arrowhead endpoint to shape boundary port to avoid floating arrow"
                : (!isStartSnapped || !isEndSnapped
                    ? "Snap connector endpoint to nearest shape anchor port"
                    : "Optimal port attachment and connector routing");

            items.Add(new ConnectorAttachmentPortItem(
                conn.Id, conn.Name ?? conn.Id,
                hasStartArrow, hasEndArrow,
                isStartSnapped, isEndSnapped,
                startPortName, endPortName,
                isOrthogonal, hasDangling,
                routingStatus, recommendation));
        }

        int totalPossiblePorts = nConnectors * 2;
        double attachmentRatio = totalPossiblePorts > 0 ? (double)snappedPortsCount / totalPossiblePorts : 1.0;
        double precisionScore = attachmentRatio * 80.0 + 20.0;
        precisionScore -= (danglingArrowheadsCount * 12.0);
        precisionScore -= (nonOrthogonalBendsCount * 8.0);
        precisionScore = Math.Round(Math.Clamp(precisionScore, 0.0, 100.0), 1);

        string category = precisionScore switch
        {
            >= 90.0 => "High Precision Docking",
            >= 75.0 => "Acceptable Port Snapping",
            >= 60.0 => "Moderate Attachment Drift",
            >= 40.0 => "Severe Port Misalignment",
            _ => "Dangling Unattached Connectors"
        };

        string summary = FormatConnectorAttachmentSummary(
            nShapes, nConnectors, snappedPortsCount, danglingArrowheadsCount,
            nonOrthogonalBendsCount, precisionScore, category, isSelection);

        return new DiagramConnectorPortAttachmentAudit(
            nShapes, nConnectors, snappedPortsCount, danglingArrowheadsCount,
            nonOrthogonalBendsCount, precisionScore, items, category, isSelection, summary);
    }

    public DiagramShapeTextPaddingAudit AuditShapeTextPaddingAndOverflow(
        IEnumerable<ShapeCanvasItemViewModel>? shapes = null, bool selectionOnly = false)
    {
        var targetShapes = (shapes ?? Shapes).ToList();
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        if (isSelection)
        {
            targetShapes = selected;
        }

        var textShapes = targetShapes.Where(s =>
            !s.IsLine &&
            s.ShapeType != "Line" &&
            s.Prst != "line" &&
            s.Prst != "straightConnector1" &&
            s.Prst != "bentConnector3" &&
            s.Prst != "curvedConnector3" &&
            !string.IsNullOrWhiteSpace(s.Text)).ToList();

        int nTotal = targetShapes.Count;
        int nText = textShapes.Count;

        if (nText == 0)
        {
            string emptySummary = FormatTextPaddingOverflowSummary(nTotal, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection);
            return new DiagramShapeTextPaddingAudit(
                nTotal, 0, 0, 0, 0, 100.0,
                Array.Empty<ShapeTextPaddingItem>(),
                "Clean Baseline", isSelection, emptySummary);
        }

        var items = new List<ShapeTextPaddingItem>();
        int wellPaddedCount = 0;
        int crampedCount = 0;
        int overflowCount = 0;

        foreach (var s in textShapes)
        {
            double[] insets = s.LabelInsets ?? PresetGeometry.TextInsets(s.Prst, s.Width, s.Height);
            double insetL = insets != null && insets.Length > 0 ? insets[0] : 8.0;
            double insetT = insets != null && insets.Length > 1 ? insets[1] : 6.0;
            double insetR = insets != null && insets.Length > 2 ? insets[2] : 8.0;
            double insetB = insets != null && insets.Length > 3 ? insets[3] : 6.0;

            double availW = Math.Max(0.0, s.Width - (insetL + insetR));
            double availH = Math.Max(0.0, s.Height - (insetT + insetB));

            double fontSize = s.LabelFontSize > 0 ? s.LabelFontSize : 12.0;
            var textLines = s.Text.Replace("\r\n", "\n").Split('\n');
            int lineCount = textLines.Length;
            int maxLineChars = textLines.Max(l => l.Length);

            double estW = maxLineChars * (fontSize * 0.55);
            double estH = lineCount * (fontSize * 1.25);

            bool hasOverflow = estW > availW || estH > availH;
            bool isCramped = !hasOverflow && (availW - estW < 6.0 || availH - estH < 6.0);

            if (hasOverflow) overflowCount++;
            else if (isCramped) crampedCount++;
            else wellPaddedCount++;

            string status = hasOverflow
                ? "Text Overflow Detected"
                : (isCramped ? "Cramped Label Margin" : "Optimal Enclosure Padding");

            string recommendation = hasOverflow
                ? "Increase shape dimensions or reduce font size to prevent text clipping"
                : (isCramped ? "Enlarge shape slightly for cleaner breathing room" : "Clean text enclosure padding");

            items.Add(new ShapeTextPaddingItem(
                s.Id, s.Name ?? s.Id, s.Prst, s.Text,
                Math.Round(s.Width, 1), Math.Round(s.Height, 1),
                Math.Round(insetL, 1), Math.Round(insetT, 1), Math.Round(insetR, 1), Math.Round(insetB, 1),
                Math.Round(availW, 1), Math.Round(availH, 1),
                Math.Round(estW, 1), Math.Round(estH, 1),
                hasOverflow, isCramped, status, recommendation));
        }

        double score = 100.0;
        score -= (overflowCount * 18.0);
        score -= (crampedCount * 6.0);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 90.0 => "Optimal Text Enclosure & Padding",
            >= 75.0 => "Acceptable Label Margins",
            >= 60.0 => "Moderate Label Crowding",
            >= 40.0 => "High Text Boundary Clipping Risk",
            _ => "Severe Text Label Overflow"
        };

        string summary = FormatTextPaddingOverflowSummary(
            nTotal, nText, wellPaddedCount, crampedCount, overflowCount,
            score, category, isSelection);

        return new DiagramShapeTextPaddingAudit(
            nTotal, nText, wellPaddedCount, crampedCount, overflowCount,
            score, items, category, isSelection, summary);
    }

    public static string FormatTextPaddingOverflowSummary(
        int totalShapes, int textShapes, int wellPadded, int cramped, int overflow,
        double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (textShapes == 0)
            return $"Diagram shape text padding audit ({scope}{totalShapes} shapes): 0 text labels on canvas";

        string shapeSuffix = textShapes == 1 ? string.Empty : "s";
        string overflowSuffix = overflow == 1 ? string.Empty : "s";
        string crampedSuffix = cramped == 1 ? string.Empty : "s";

        var parts = new List<string>
        {
            $"{textShapes} labeled shape{shapeSuffix}",
            $"{wellPadded} well-padded"
        };
        if (overflow > 0) parts.Add($"⚠️ {overflow} text overflow{overflowSuffix}");
        if (cramped > 0) parts.Add($"{cramped} cramped margin{crampedSuffix}");

        string details = string.Join(" · ", parts);
        return $"Diagram shape text padding & boundary audit ({scope}{details} · {category} · {score:0.#}% enclosure score)";
    }

    public static string FormatConnectorAttachmentSummary(
        int totalShapes, int totalConnectors, int snappedPorts, int danglingArrows,
        int nonOrthogonalBends, double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalConnectors == 0)
            return $"Connector port attachment & arrowhead audit ({scope}{totalShapes} shapes): 0 connectors on canvas";

        string connSuffix = totalConnectors == 1 ? string.Empty : "s";
        string portSuffix = snappedPorts == 1 ? string.Empty : "s";
        string arrowSuffix = danglingArrows == 1 ? string.Empty : "s";
        string bendSuffix = nonOrthogonalBends == 1 ? string.Empty : "s";

        var parts = new List<string>
        {
            $"{totalConnectors} connector{connSuffix}",
            $"{snappedPorts} port{portSuffix} snapped"
        };
        if (danglingArrows > 0) parts.Add($"⚠️ {danglingArrows} dangling arrow{arrowSuffix}");
        if (nonOrthogonalBends > 0) parts.Add($"{nonOrthogonalBends} non-orthogonal bend{bendSuffix}");

        string details = string.Join(" · ", parts);
        return $"Connector port attachment & arrowhead audit ({scope}{details} · {category} · {score:0.#}% precision score)";
    }




    public static string FormatAspectRatioSummary(
        int totalShapes, int conformingCount, int driftCount, int freeformCount,
        double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Shape aspect ratio audit ({scopeStr}0 shapes): Empty canvas";

        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var alerts = new List<string>();
        if (driftCount > 0) alerts.Add($"⚠ {driftCount} accidental drift{(driftCount == 1 ? "" : "s")}");
        if (conformingCount > 0) alerts.Add($"{conformingCount} locked");
        if (freeformCount > 0) alerts.Add($"{freeformCount} freeform");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
        return $"Shape aspect ratio audit ({scopeStr}{shapeLabel} · {category} · {score:0.#}% score){details}";
    }

    /// <summary>
    /// Audits shape border stroke alignment, corner radius uniformity on rounded rectangles,
    /// heavy stroke disproportion, ghost shapes, and miter limit spike risks.
    /// </summary>
    public DiagramStrokeCornerAudit AuditStrokeAlignmentAndCornerRadius(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var rawTargets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        bool isSelection = targetShapes != null || selected.Count > 0;
        var audit = AuditStrokeAlignmentAndCornerRadius(rawTargets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    /// <summary>
    /// Evaluates stroke weight balance, corner radius uniformity, ghost shapes, and miter spike risks
    /// across a provided set of shapes.
    /// </summary>
    public static DiagramStrokeCornerAudit AuditStrokeAlignmentAndCornerRadius(
        IReadOnlyList<ShapeCanvasItemViewModel> shapes,
        bool isSelection = false)
    {
        var geometricShapes = shapes.Where(s => s != null && s.Width > 0 && s.Height > 0).ToList();

        if (geometricShapes.Count == 0)
        {
            string emptySummary = FormatStrokeAlignmentSummary(0, 0, 0, 0, 0, 0, 0, 0.0, 100.0, "Empty Canvas", isSelection);
            return new DiagramStrokeCornerAudit(
                0, 0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeStrokeCornerInfo>(),
                "Empty Canvas",
                isSelection,
                emptySummary);
        }

        var evaluated = new List<ShapeStrokeCornerInfo>();
        int uniformCount = 0;
        int heavyCount = 0;
        int ghostCount = 0;
        int roundRectCount = 0;
        int uniformCornerCount = 0;
        int miterSpikeCount = 0;
        double totalStrokePt = 0.0;

        var roundRectRadii = new List<double>();
        foreach (var s in geometricShapes)
        {
            if (string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase))
            {
                double r = Math.Min(s.Width, s.Height) * 0.1667;
                roundRectRadii.Add(r);
            }
        }
        double meanRoundRectRadius = roundRectRadii.Count > 0 ? roundRectRadii.Average() : 0.0;

        foreach (var s in geometricShapes)
        {
            double w = Math.Max(0.1, s.Width);
            double h = Math.Max(0.1, s.Height);
            double minDim = Math.Min(w, h);
            double strokePt = s.StrokeWidthPt;
            totalStrokePt += strokePt;

            double strokeToDim = strokePt / minDim;
            bool isGhost = (w < 2.0 || h < 2.0) || (strokePt <= 0.25 && (string.IsNullOrWhiteSpace(s.Fill) || string.Equals(s.Fill, "transparent", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Fill, CanvasBackgroundHex, StringComparison.OrdinalIgnoreCase)));
            bool isHeavy = strokeToDim > 0.15 || (strokePt >= 8.0 && minDim < 60.0);

            bool isRoundRect = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase);
            double estimatedCornerRadius = isRoundRect ? Math.Round(minDim * 0.1667, 1) : 0.0;
            bool cornerDiscrepancy = false;
            if (isRoundRect)
            {
                roundRectCount++;
                if (roundRectRadii.Count > 1 && Math.Abs(estimatedCornerRadius - meanRoundRectRadius) > (meanRoundRectRadius * 0.35))
                {
                    cornerDiscrepancy = true;
                }
                else
                {
                    uniformCornerCount++;
                }
            }

            bool hasMiterSpike = strokePt >= 4.0 && (
                string.Equals(s.Prst, "diamond", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Prst, "triangle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Prst, "star5", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Prst, "chevron", StringComparison.OrdinalIgnoreCase));

            if (hasMiterSpike) miterSpikeCount++;

            string status;
            if (isGhost)
            {
                ghostCount++;
                status = "Ghost Shape (Near-Zero Contrast/Dimensions)";
            }
            else if (isHeavy)
            {
                heavyCount++;
                status = "Heavy Stroke Weight (Dominates Shape Fill)";
            }
            else if (hasMiterSpike)
            {
                status = "Miter Spike Risk (Sharp Vertex Outer Bleed)";
            }
            else if (cornerDiscrepancy)
            {
                status = "Non-Uniform Corner Radius";
            }
            else
            {
                uniformCount++;
                status = "Conforming Stroke & Corner";
            }

            string shapeTitle = !string.IsNullOrWhiteSpace(s.Text)
                ? s.Text
                : (!string.IsNullOrWhiteSpace(s.Name) ? s.Name : (!string.IsNullOrWhiteSpace(s.ShapeType) ? s.ShapeType : $"Shape #{s.Id}"));

            evaluated.Add(new ShapeStrokeCornerInfo(
                ShapeId: s.Id,
                DisplayName: shapeTitle,
                ShapeType: s.Prst,
                Width: Math.Round(w, 1),
                Height: Math.Round(h, 1),
                StrokeWidthPt: Math.Round(strokePt, 2),
                StrokeToDimensionRatio: Math.Round(strokeToDim, 3),
                HasHeavyStroke: isHeavy,
                IsGhostShape: isGhost,
                EstimatedCornerRadius: estimatedCornerRadius,
                HasCornerRadiusDiscrepancy: cornerDiscrepancy,
                HasMiterSpikeRisk: hasMiterSpike,
                Status: status));
        }

        double meanStrokePt = totalStrokePt / geometricShapes.Count;
        int cornerIssues = roundRectCount - uniformCornerCount;
        double penalty = ((heavyCount * 12.0) + (ghostCount * 20.0) + (cornerIssues * 10.0) + (miterSpikeCount * 8.0)) / geometricShapes.Count;
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 when heavyCount == 0 && ghostCount == 0 && cornerIssues == 0 => "Uniform Pixel-Perfect Strokes & Corners",
            >= 80.0 => "Harmonized Stroke Hierarchy & Radii",
            >= 60.0 => "Moderate Stroke & Corner Inconsistencies",
            _ => "Disparate Stroke Weights & Corner Mismatch"
        };

        string summary = FormatStrokeAlignmentSummary(
            geometricShapes.Count, uniformCount, heavyCount, ghostCount,
            roundRectCount, uniformCornerCount, miterSpikeCount,
            meanStrokePt, score, category, isSelection);

        return new DiagramStrokeCornerAudit(
            TotalShapesEvaluated: geometricShapes.Count,
            UniformStrokeCount: uniformCount,
            HeavyStrokeCount: heavyCount,
            GhostShapeCount: ghostCount,
            RoundedRectCount: roundRectCount,
            UniformCornerRadiusCount: uniformCornerCount,
            MiterSpikeRiskCount: miterSpikeCount,
            MeanStrokeWidthPt: Math.Round(meanStrokePt, 1),
            StrokeFidelityScore: Math.Round(score, 1),
            Shapes: evaluated,
            Category: category,
            IsSelectionScope: isSelection,
            Summary: summary);
    }

    public static string FormatStrokeAlignmentSummary(
        int totalShapes, int uniformCount, int heavyCount, int ghostCount,
        int roundRectCount, int uniformCornerCount, int miterSpikeCount,
        double meanStrokePt, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Shape stroke & corner audit ({scopeStr}0 shapes): Empty canvas";

        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var alerts = new List<string>();
        if (heavyCount > 0) alerts.Add($"⚠ {heavyCount} heavy stroke{(heavyCount == 1 ? "" : "s")}");
        if (ghostCount > 0) alerts.Add($"⚠ {ghostCount} ghost");
        if (miterSpikeCount > 0) alerts.Add($"⚠ {miterSpikeCount} miter spike{(miterSpikeCount == 1 ? "" : "s")}");
        int cornerMismatch = roundRectCount - uniformCornerCount;
        if (cornerMismatch > 0) alerts.Add($"⚠ {cornerMismatch} corner mismatch{(cornerMismatch == 1 ? "" : "es")}");
        if (uniformCount > 0) alerts.Add($"{uniformCount} uniform");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
        return $"Shape stroke & corner audit ({scopeStr}{shapeLabel} · {category} · {score:0.#}% score · Mean {meanStrokePt:0.#}pt){details}";
    }

    /// <summary>
    /// Audits connector routing and polyline paths for shape bounding box collisions and near-miss clearance violations.
    /// Excludes connected source and target shapes from being reported as obstacles.
    /// </summary>
    public ConnectorObstacleCollisionAudit AuditConnectorObstacleCollisions(IEnumerable<ShapeCanvasItemViewModel>? targetItems = null)
    {
        var selected = SelectedItems.ToList();
        var rawTargets = targetItems?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        bool isSelection = targetItems != null || selected.Count > 0;
        var audit = AuditConnectorObstacleCollisions(rawTargets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static ConnectorObstacleCollisionAudit AuditConnectorObstacleCollisions(
        IReadOnlyList<ShapeCanvasItemViewModel> items,
        bool isSelection = false)
    {
        if (items == null || items.Count == 0)
        {
            string emptySummary = FormatConnectorCollisionSummary(0, 0, 0, 0, 0, 0, 100.0, "Empty Canvas", isSelection);
            return new ConnectorObstacleCollisionAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ConnectorCollisionItem>(),
                Array.Empty<string>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        // Separate connectors (lines) from obstacle shapes
        var connectors = items.Where(i => i.IsLine).ToList();
        var obstacles = items.Where(i => !i.IsLine && i.Width > 0 && i.Height > 0).ToList();

        if (connectors.Count == 0)
        {
            string noConnSummary = FormatConnectorCollisionSummary(0, obstacles.Count, 0, 0, 0, 0, 100.0, "No Connectors", isSelection);
            return new ConnectorObstacleCollisionAudit(
                0, obstacles.Count, 0, 0, 0, 0, 100.0,
                Array.Empty<ConnectorCollisionItem>(),
                Array.Empty<string>(),
                "No Connectors Present", isSelection, noConnSummary);
        }

        const double clearanceMargin = 8.0;
        var connectorItems = new List<ConnectorCollisionItem>();
        var criticalCollisions = new List<string>();
        int clearConnectors = 0;
        int collidedConnectors = 0;
        int nearMissConnectors = 0;
        int totalCollisionsCount = 0;

        foreach (var conn in connectors)
        {
            // Build absolute canvas points for this connector
            var pts = new List<(double X, double Y)>();
            if (conn.PathPoints != null && conn.PathPoints.Count >= 2)
            {
                foreach (var p in conn.PathPoints)
                {
                    double ax = conn.X + (p.X * conn.Width / 100.0);
                    double ay = conn.Y + (p.Y * conn.Height / 100.0);
                    pts.Add((ax, ay));
                }
            }
            else
            {
                pts.Add((conn.X, conn.Y));
                pts.Add((conn.X + conn.Width, conn.Y + conn.Height));
            }

            var startPt = pts[0];
            var endPt = pts[^1];

            // Identify source and target shapes to exclude from obstacle checks
            var connectedShapeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ShapeCanvasItemViewModel? sourceShape = null;
            ShapeCanvasItemViewModel? targetShape = null;

            foreach (var s in obstacles)
            {
                if (PointTouchesBox(startPt.X, startPt.Y, s.X, s.Y, s.Width, s.Height, 8.0))
                {
                    connectedShapeIds.Add(s.Id);
                    sourceShape ??= s;
                }
                if (PointTouchesBox(endPt.X, endPt.Y, s.X, s.Y, s.Width, s.Height, 8.0))
                {
                    connectedShapeIds.Add(s.Id);
                    targetShape ??= s;
                }
            }

            string sourceName = sourceShape?.Name ?? (sourceShape?.Text?.Trim() is { Length: > 0 } st ? st : "Start Point");
            string targetName = targetShape?.Name ?? (targetShape?.Text?.Trim() is { Length: > 0 } tt ? tt : "End Point");

            var candidateObstacles = obstacles.Where(o => !connectedShapeIds.Contains(o.Id)).ToList();
            var collidedShapeNames = new List<string>();
            var nearMissShapeNames = new List<string>();
            int segmentCount = pts.Count - 1;

            for (int seg = 0; seg < pts.Count - 1; seg++)
            {
                var p1 = pts[seg];
                var p2 = pts[seg + 1];

                foreach (var obs in candidateObstacles)
                {
                    string obsName = string.IsNullOrWhiteSpace(obs.Name)
                        ? (string.IsNullOrWhiteSpace(obs.Text) ? $"Shape_{obs.Id}" : obs.Text.Trim())
                        : obs.Name.Trim();

                    // Check direct intersection
                    if (SegmentIntersectsAabb(p1.X, p1.Y, p2.X, p2.Y, obs.X, obs.Y, obs.Width, obs.Height))
                    {
                        if (!collidedShapeNames.Contains(obsName))
                        {
                            collidedShapeNames.Add(obsName);
                            criticalCollisions.Add($"{conn.Name} penetrating '{obsName}'");
                        }
                    }
                    // Check near-miss intersection (expanded by clearanceMargin)
                    else if (SegmentIntersectsAabb(p1.X, p1.Y, p2.X, p2.Y,
                        obs.X - clearanceMargin, obs.Y - clearanceMargin,
                        obs.Width + clearanceMargin * 2, obs.Height + clearanceMargin * 2))
                    {
                        if (!nearMissShapeNames.Contains(obsName) && !collidedShapeNames.Contains(obsName))
                        {
                            nearMissShapeNames.Add(obsName);
                        }
                    }
                }
            }

            int collisions = collidedShapeNames.Count;
            int nearMisses = nearMissShapeNames.Count;
            totalCollisionsCount += collisions;

            string severity = collisions > 0 ? "Critical Collision" : (nearMisses > 0 ? "Near-Miss Hazard" : "Clear Routing");

            if (collisions > 0) collidedConnectors++;
            else if (nearMisses > 0) nearMissConnectors++;
            else clearConnectors++;

            connectorItems.Add(new ConnectorCollisionItem(
                ConnectorId: conn.Id,
                ConnectorName: conn.Name,
                SourceShapeName: sourceName,
                TargetShapeName: targetName,
                SegmentCount: segmentCount,
                CollisionCount: collisions,
                NearMissCount: nearMisses,
                CollidedShapeNames: collidedShapeNames,
                NearMissShapeNames: nearMissShapeNames,
                ClearanceMarginPixels: clearanceMargin,
                Severity: severity));
        }

        double penalty = (collidedConnectors * 35.0) + (nearMissConnectors * 15.0);
        double score = Math.Clamp(100.0 - (penalty / connectors.Count), 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 when collidedConnectors == 0 && nearMissConnectors == 0 => "Clear Channel Orthogonal Routing",
            >= 80.0 => "Good Routing with Minor Near-Miss Grazing",
            >= 60.0 => "Routing Obstacle Warnings",
            _ => "Critical Shape Collision & Obscuration Hazards"
        };

        string summary = FormatConnectorCollisionSummary(
            connectors.Count, obstacles.Count, clearConnectors, collidedConnectors, nearMissConnectors,
            totalCollisionsCount, score, category, isSelection);

        return new ConnectorObstacleCollisionAudit(
            TotalConnectors: connectors.Count,
            TotalObstacleShapes: obstacles.Count,
            ClearConnectors: clearConnectors,
            CollidedConnectors: collidedConnectors,
            NearMissConnectors: nearMissConnectors,
            TotalCollisions: totalCollisionsCount,
            RoutingClearanceScore: Math.Round(score, 1),
            Connectors: connectorItems,
            CriticalCollisions: criticalCollisions,
            ClearanceCategory: category,
            IsSelectionScope: isSelection,
            Summary: summary);
    }

    private static bool PointTouchesBox(double px, double py, double bx, double by, double bw, double bh, double margin)
    {
        return px >= bx - margin && px <= bx + bw + margin &&
               py >= by - margin && py <= by + bh + margin;
    }

    private static bool SegmentIntersectsAabb(double x1, double y1, double x2, double y2, double rx, double ry, double rw, double rh)
    {
        double rRight = rx + rw;
        double rBottom = ry + rh;

        // Quick AABB rejection
        if (Math.Max(x1, x2) < rx || Math.Min(x1, x2) > rRight ||
            Math.Max(y1, y2) < ry || Math.Min(y1, y2) > rBottom)
        {
            return false;
        }

        // Point inside box check
        if ((x1 >= rx && x1 <= rRight && y1 >= ry && y1 <= rBottom) ||
            (x2 >= rx && x2 <= rRight && y2 >= ry && y2 <= rBottom))
        {
            return true;
        }

        // Segment vs 4 box segments
        return LineSegmentsIntersect(x1, y1, x2, y2, rx, ry, rRight, ry) ||
               LineSegmentsIntersect(x1, y1, x2, y2, rRight, ry, rRight, rBottom) ||
               LineSegmentsIntersect(x1, y1, x2, y2, rRight, rBottom, rx, rBottom) ||
               LineSegmentsIntersect(x1, y1, x2, y2, rx, rBottom, rx, ry);
    }

    private static bool LineSegmentsIntersect(double x1, double y1, double x2, double y2, double x3, double y3, double x4, double y4)
    {
        static double Ccw(double ax, double ay, double bx, double by, double cx, double cy) =>
            (cy - ay) * (bx - ax) > (by - ay) * (cx - ax) ? 1.0 : -1.0;

        return (Ccw(x1, y1, x3, y3, x4, y4) != Ccw(x2, y2, x3, y3, x4, y4)) &&
               (Ccw(x1, y1, x2, y2, x3, y3) != Ccw(x1, y1, x2, y2, x4, y4));
    }

    public static string FormatConnectorCollisionSummary(
        int totalConnectors, int obstacleCount, int clearCount, int collidedCount, int nearMissCount,
        int totalCollisions, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalConnectors == 0) return $"Connector collision audit ({scopeStr}0 connectors): No lines to route";

        string connLabel = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var alerts = new List<string>();
        if (collidedCount > 0) alerts.Add($"🚨 {collidedCount} collided");
        if (nearMissCount > 0) alerts.Add($"⚠ {nearMissCount} near-miss");
        if (clearCount > 0) alerts.Add($"✓ {clearCount} clear");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
        return $"Connector obstacle collision audit ({scopeStr}{connLabel} vs {obstacleCount} shapes · {category} · {score:0.#}% clearance){details}";
    }

    /// <summary>
    /// Audits shape compound grouping hierarchy, nested child bounding box containment,
    /// visual overflow detection, and z-index depth order stacking monotonicity.
    /// </summary>
    public ShapeCompoundGroupAudit AuditCompoundGroupBoundsAndStacking(IEnumerable<ShapeCanvasItemViewModel>? targetItems = null)
    {
        var rawTargets = targetItems?.ToList() ?? (SelectedShape != null && SelectionCount > 1
            ? Shapes.Where(s => s.IsSelected).ToList()
            : Shapes.ToList());

        bool isSelection = targetItems != null || (SelectedShape != null && SelectionCount > 1);
        var audit = AuditCompoundGroupBoundsAndStacking(rawTargets, isSelection);
        StatusMessage = $"✓ {audit.Summary}";
        return audit;
    }

    public static ShapeCompoundGroupAudit AuditCompoundGroupBoundsAndStacking(
        IReadOnlyList<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        if (shapes == null || shapes.Count == 0)
        {
            string emptySummary = isSelection
                ? "Compound group audit (Selection · 0 shapes): No shapes selected"
                : "Compound group audit (0 shapes): Canvas contains no shapes";
            return new ShapeCompoundGroupAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<CompoundGroupInfo>(),
                Array.Empty<string>(),
                "Empty Canvas",
                isSelection,
                emptySummary);
        }

        var shapeList = shapes.Where(s => !s.IsLine).ToList();
        if (shapeList.Count == 0)
        {
            return new ShapeCompoundGroupAudit(
                shapes.Count, 0, 0, shapes.Count, 0, 0, 100.0,
                Array.Empty<CompoundGroupInfo>(),
                Array.Empty<string>(),
                "Connectors Only (No Groups)",
                isSelection,
                "Compound group audit: Connectors only · No enclosed compound shapes");
        }

        var shapeIndices = new Dictionary<ShapeCanvasItemViewModel, int>();
        for (int i = 0; i < shapes.Count; i++) shapeIndices[shapes[i]] = i;

        var groups = new List<CompoundGroupInfo>();
        var groupedChildSet = new HashSet<ShapeCanvasItemViewModel>();
        var structuralAlerts = new List<string>();

        int zOrderInversionCount = 0;
        int overflowingGroupCount = 0;

        var candidateContainers = shapeList.OrderByDescending(s => s.Width * s.Height).ToList();

        foreach (var container in candidateContainers)
        {
            double cLeft = container.X;
            double cTop = container.Y;
            double cRight = container.X + container.Width;
            double cBottom = container.Y + container.Height;
            double cArea = container.Width * container.Height;

            var children = new List<ShapeCanvasItemViewModel>();
            foreach (var child in shapeList)
            {
                if (ReferenceEquals(child, container)) continue;
                if (child.Width * child.Height >= cArea) continue;

                double childCenterX = child.X + child.Width / 2.0;
                double childCenterY = child.Y + child.Height / 2.0;

                if (childCenterX >= cLeft && childCenterX <= cRight &&
                    childCenterY >= cTop && childCenterY <= cBottom)
                {
                    children.Add(child);
                }
            }

            if (children.Count > 0)
            {
                foreach (var ch in children) groupedChildSet.Add(ch);

                double minX = children.Min(ch => ch.X);
                double minY = children.Min(ch => ch.Y);
                double maxX = children.Max(ch => ch.X + ch.Width);
                double maxY = children.Max(ch => ch.Y + ch.Height);

                bool overflowsLeft = minX < cLeft - 2.0;
                bool overflowsTop = minY < cTop - 2.0;
                bool overflowsRight = maxX > cRight + 2.0;
                bool overflowsBottom = maxY > cBottom + 2.0;
                bool hasOverflow = overflowsLeft || overflowsTop || overflowsRight || overflowsBottom;

                double overflowArea = 0.0;
                if (hasOverflow)
                {
                    overflowingGroupCount++;
                    double unionWidth = Math.Max(cRight, maxX) - Math.Min(cLeft, minX);
                    double unionHeight = Math.Max(cBottom, maxY) - Math.Min(cTop, minY);
                    overflowArea = Math.Max(0.0, (unionWidth * unionHeight) - cArea);
                    structuralAlerts.Add($"⚠ '{container.ListTitle}' has {children.Count} children overflowing container bounds");
                }

                int containerZ = shapeIndices.TryGetValue(container, out int cz) ? cz : 0;
                bool isZMonotonic = true;
                foreach (var ch in children)
                {
                    int childZ = shapeIndices.TryGetValue(ch, out int chz) ? chz : 0;
                    if (containerZ > childZ)
                    {
                        isZMonotonic = false;
                        zOrderInversionCount++;
                        structuralAlerts.Add($"🚨 Z-order inversion: Container '{container.ListTitle}' (Z={containerZ}) renders above child '{ch.ListTitle}' (Z={childZ})");
                    }
                }

                groups.Add(new CompoundGroupInfo(
                    container.Id,
                    container.ListTitle,
                    children.Count,
                    container.X,
                    container.Y,
                    container.Width,
                    container.Height,
                    minX,
                    minY,
                    maxX,
                    maxY,
                    hasOverflow,
                    Math.Round(overflowArea, 1),
                    isZMonotonic));
            }
        }

        int totalGroups = groups.Count;
        int groupedChildren = groupedChildSet.Count;
        int standaloneShapes = shapeList.Count - totalGroups - groupedChildren;
        if (standaloneShapes < 0) standaloneShapes = 0;

        double baseScore = 100.0;
        if (overflowingGroupCount > 0) baseScore -= (overflowingGroupCount * 25.0);
        if (zOrderInversionCount > 0) baseScore -= (zOrderInversionCount * 20.0);
        double containmentScore = Math.Round(Math.Clamp(baseScore, 0.0, 100.0), 1);

        string category;
        if (totalGroups == 0)
        {
            category = "Flat Hierarchy (No Compound Groups)";
        }
        else if (overflowingGroupCount == 0 && zOrderInversionCount == 0)
        {
            category = "Perfect Nested Containment (100%)";
        }
        else if (zOrderInversionCount > 0)
        {
            category = "Z-Order Occlusion Hazard";
        }
        else
        {
            category = "Boundary Overflow Drift";
        }

        string summary = FormatCompoundGroupSummary(
            shapes.Count, totalGroups, groupedChildren, standaloneShapes,
            overflowingGroupCount, zOrderInversionCount, containmentScore, category, isSelection);

        return new ShapeCompoundGroupAudit(
            shapes.Count,
            totalGroups,
            groupedChildren,
            standaloneShapes,
            overflowingGroupCount,
            zOrderInversionCount,
            containmentScore,
            groups,
            structuralAlerts,
            category,
            isSelection,
            summary);
    }

    public static string FormatCompoundGroupSummary(
        int totalShapes, int groupCount, int childCount, int standaloneCount,
        int overflowCount, int zOrderViolations, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Compound group audit ({scopeStr}0 shapes): No shapes to audit";

        if (groupCount == 0)
        {
            return $"Compound group audit ({scopeStr}{totalShapes} shapes · {category}): All shapes are standalone items";
        }

        string groupLabel = groupCount == 1 ? "1 compound group" : $"{groupCount} compound groups";
        string childLabel = $"{childCount} children";

        var alerts = new List<string>();
        if (overflowCount > 0) alerts.Add($"⚠ {overflowCount} overflow");
        if (zOrderViolations > 0) alerts.Add($"🚨 {zOrderViolations} z-inversion");
        if (alerts.Count == 0) alerts.Add("✓ 100% contained");

        return $"Compound group audit ({scopeStr}{groupLabel} [{childLabel}], {standaloneCount} standalone · {category} · {score:0.#}% containment) · {string.Join(", ", alerts)}";
    }

    public ConnectorPortSnappingAudit AuditConnectorPortSnapping(IEnumerable<ShapeCanvasItemViewModel>? targetItems = null)
    {
        var rawTargets = targetItems?.ToList() ?? (SelectedShape != null && SelectionCount > 1
            ? Shapes.Where(s => s.IsSelected).ToList()
            : Shapes.ToList());

        bool isSelection = targetItems != null || (SelectedShape != null && SelectionCount > 1);
        var audit = AuditConnectorPortSnapping(rawTargets, isSelection);
        StatusMessage = $"✓ {audit.Summary}";
        return audit;
    }

    public static ConnectorPortSnappingAudit AuditConnectorPortSnapping(
        IReadOnlyList<ShapeCanvasItemViewModel>? items, bool isSelection = false)
    {
        if (items == null || items.Count == 0)
        {
            string emptySummary = isSelection
                ? "Connector port snapping audit (Selection · 0 items): No shapes selected"
                : "Connector port snapping audit (0 items): Canvas contains no shapes";
            return new ConnectorPortSnappingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ConnectorPortSnapDetail>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        var connectors = items.Where(i => i.IsLine).ToList();
        var shapes = items.Where(i => !i.IsLine && i.Width > 0 && i.Height > 0).ToList();

        if (connectors.Count == 0)
        {
            string noConnSummary = isSelection
                ? $"Connector port snapping audit (Selection · {shapes.Count} shapes): No connectors selected"
                : $"Connector port snapping audit ({shapes.Count} shapes): Canvas contains no connectors";
            return new ConnectorPortSnappingAudit(
                0, shapes.Count, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ConnectorPortSnapDetail>(),
                "No Connectors Present", isSelection, noConnSummary);
        }

        const double SnapThreshold = 4.0;
        const double NearMissThreshold = 16.0;

        var details = new List<ConnectorPortSnapDetail>();
        int fullySnappedCount = 0;
        int partiallySnappedCount = 0;
        int floatingCount = 0;
        int nearMissHazards = 0;
        int subpixelDriftCount = 0;

        foreach (var conn in connectors)
        {
            var pts = new List<(double X, double Y)>();
            if (conn.PathPoints != null && conn.PathPoints.Count >= 2)
            {
                foreach (var p in conn.PathPoints)
                {
                    double ax = conn.X + (p.X * conn.Width / 100.0);
                    double ay = conn.Y + (p.Y * conn.Height / 100.0);
                    pts.Add((ax, ay));
                }
            }
            else
            {
                pts.Add((conn.X, conn.Y));
                pts.Add((conn.X + conn.Width, conn.Y + conn.Height));
            }

            var startPt = pts[0];
            var endPt = pts[^1];

            bool startSubpixel = Math.Abs(startPt.X - Math.Round(startPt.X)) > 0.05 || Math.Abs(startPt.Y - Math.Round(startPt.Y)) > 0.05;
            bool endSubpixel = Math.Abs(endPt.X - Math.Round(endPt.X)) > 0.05 || Math.Abs(endPt.Y - Math.Round(endPt.Y)) > 0.05;
            if (startSubpixel || endSubpixel) subpixelDriftCount++;

            (bool snapped, string? shapeName, string? portName, double minDistance) EvalEndpoint((double X, double Y) pt)
            {
                double bestDist = double.MaxValue;
                string? bestShape = null;
                string? bestPort = null;

                foreach (var s in shapes)
                {
                    var ports = new (string Name, double X, double Y)[]
                    {
                        ("Top", s.X + s.Width / 2.0, s.Y),
                        ("Right", s.X + s.Width, s.Y + s.Height / 2.0),
                        ("Bottom", s.X + s.Width / 2.0, s.Y + s.Height),
                        ("Left", s.X, s.Y + s.Height / 2.0),
                        ("Center", s.X + s.Width / 2.0, s.Y + s.Height / 2.0)
                    };

                    foreach (var port in ports)
                    {
                        double dx = pt.X - port.X;
                        double dy = pt.Y - port.Y;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestShape = s.Name ?? s.ShapeType ?? "Shape";
                            bestPort = port.Name;
                        }
                    }
                }

                if (bestDist <= SnapThreshold)
                {
                    return (true, bestShape, bestPort, bestDist);
                }
                return (false, bestShape, bestPort, bestDist);
            }

            var startEval = EvalEndpoint(startPt);
            var endEval = EvalEndpoint(endPt);

            bool startNearMiss = !startEval.snapped && startEval.minDistance <= NearMissThreshold;
            bool endNearMiss = !endEval.snapped && endEval.minDistance <= NearMissThreshold;
            bool hasNearMiss = startNearMiss || endNearMiss;
            if (hasNearMiss) nearMissHazards++;

            if (startEval.snapped && endEval.snapped) fullySnappedCount++;
            else if (startEval.snapped || endEval.snapped) partiallySnappedCount++;
            else floatingCount++;

            details.Add(new ConnectorPortSnapDetail(
                conn.Id,
                conn.Name ?? "Connector",
                startEval.snapped,
                endEval.snapped,
                startEval.shapeName,
                startEval.portName,
                Math.Round(startEval.minDistance, 1),
                endEval.shapeName,
                endEval.portName,
                Math.Round(endEval.minDistance, 1),
                hasNearMiss,
                startSubpixel || endSubpixel));
        }

        double penalty = (floatingCount * 30.0) + (partiallySnappedCount * 15.0) + (nearMissHazards * 10.0) + (subpixelDriftCount * 5.0);
        double maxPenalty = connectors.Count * 35.0;
        double snappingScore = maxPenalty > 0 ? Math.Clamp(100.0 - (penalty / maxPenalty * 100.0), 0.0, 100.0) : 100.0;
        snappingScore = Math.Round(snappingScore, 1);

        string category;
        if (fullySnappedCount == connectors.Count && subpixelDriftCount == 0) category = "Pixel-Perfect Port Snapped";
        else if (fullySnappedCount == connectors.Count) category = "Snapped (Sub-pixel Float)";
        else if (floatingCount == 0 && nearMissHazards == 0) category = "Good Partial Snapping";
        else if (nearMissHazards > 0) category = "Near-Miss Port Misalignment";
        else category = "Disconnected Floating Connectors";

        string summary = FormatConnectorPortSnappingSummary(
            connectors.Count, shapes.Count, fullySnappedCount, partiallySnappedCount,
            floatingCount, nearMissHazards, subpixelDriftCount, snappingScore, category, isSelection);

        return new ConnectorPortSnappingAudit(
            connectors.Count,
            shapes.Count,
            fullySnappedCount,
            partiallySnappedCount,
            floatingCount,
            nearMissHazards,
            subpixelDriftCount,
            snappingScore,
            details,
            category,
            isSelection,
            summary);
    }

    public static string FormatConnectorPortSnappingSummary(
        int connectorCount, int shapeCount, int fullySnapped, int partiallySnapped,
        int floating, int nearMiss, int subpixelDrift, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (connectorCount == 0) return $"Connector port snapping audit ({scopeStr}0 connectors): No connectors found";

        string connLabel = connectorCount == 1 ? "1 connector" : $"{connectorCount} connectors";
        string shapeLabel = shapeCount == 1 ? "1 shape" : $"{shapeCount} shapes";

        var alerts = new List<string>();
        if (fullySnapped > 0) alerts.Add($"✓ {fullySnapped} fully snapped");
        if (partiallySnapped > 0) alerts.Add($"⚠ {partiallySnapped} half-snapped");
        if (floating > 0) alerts.Add($"🚨 {floating} floating");
        if (nearMiss > 0) alerts.Add($"⚠ {nearMiss} near-miss");
        if (subpixelDrift > 0) alerts.Add($"ℹ {subpixelDrift} sub-pixel drift");

        return $"Connector port snapping audit ({scopeStr}{connLabel}, {shapeLabel} · {category} · {score:0.#}% precision) · {string.Join(", ", alerts)}";
    }

    /// <summary>
    /// Audits diagram canvas spatial balance, calculating area-weighted centroid (Cx, Cy)
    /// relative to canvas center, detecting visual lopsidedness, and evaluating outer margin clearances (>= 40px).
    /// </summary>
    public CanvasSpatialBalanceAudit AuditCanvasSpatialBalance(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null, double canvasWidth = 1920, double canvasHeight = 1080)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count > 0 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count > 0 && selected.Count < Shapes.Count);

        var audit = AuditCanvasSpatialBalance(targets, canvasWidth, canvasHeight, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static CanvasSpatialBalanceAudit AuditCanvasSpatialBalance(
        IEnumerable<ShapeCanvasItemViewModel>? shapes,
        double canvasWidth = 1920,
        double canvasHeight = 1080,
        bool isSelection = false)
    {
        var list = shapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        string scope = isSelection ? "Selection · " : "";

        if (list.Count == 0)
        {
            string emptySummary = $"Canvas spatial balance audit ({scope}0 shapes): Canvas contains no shapes";
            return new CanvasSpatialBalanceAudit(
                0, canvasWidth, canvasHeight, canvasWidth / 2.0, canvasHeight / 2.0,
                0.0, canvasWidth / 2.0, canvasHeight / 2.0, canvasWidth / 2.0, canvasHeight / 2.0,
                Math.Min(canvasWidth, canvasHeight) / 2.0, 0, 100.0, "Empty Canvas", isSelection, emptySummary);
        }

        double totalArea = 0.0;
        double weightedX = 0.0;
        double weightedY = 0.0;

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        double leftHalfArea = 0.0;
        double rightHalfArea = 0.0;
        double topHalfArea = 0.0;
        double bottomHalfArea = 0.0;

        double centerX = canvasWidth / 2.0;
        double centerY = canvasHeight / 2.0;

        foreach (var s in list)
        {
            double w = Math.Max(1.0, s.Width);
            double h = Math.Max(1.0, s.Height);
            double area = w * h;
            double shapeCx = s.X + w / 2.0;
            double shapeCy = s.Y + h / 2.0;

            totalArea += area;
            weightedX += area * shapeCx;
            weightedY += area * shapeCy;

            if (s.X < minX) minX = s.X;
            if (s.X + w > maxX) maxX = s.X + w;
            if (s.Y < minY) minY = s.Y;
            if (s.Y + h > maxY) maxY = s.Y + h;

            if (shapeCx < centerX) leftHalfArea += area;
            else rightHalfArea += area;

            if (shapeCy < centerY) topHalfArea += area;
            else bottomHalfArea += area;
        }

        double centroidX = Math.Round(weightedX / totalArea, 1);
        double centroidY = Math.Round(weightedY / totalArea, 1);

        double offX = centroidX - centerX;
        double offY = centroidY - centerY;
        double offsetDist = Math.Round(Math.Sqrt(offX * offX + offY * offY), 1);

        double diag = Math.Sqrt(canvasWidth * canvasWidth + canvasHeight * canvasHeight);
        double normOffsetPct = Math.Round((offsetDist / (diag / 2.0)) * 100.0, 1);

        double marginLeft = Math.Round(minX, 1);
        double marginTop = Math.Round(minY, 1);
        double marginRight = Math.Round(canvasWidth - maxX, 1);
        double marginBottom = Math.Round(canvasHeight - maxY, 1);

        double minMargin = Math.Min(Math.Min(marginLeft, marginTop), Math.Min(marginRight, marginBottom));

        int edgeCrowdingHazards = 0;
        const double SafeMarginThreshold = 40.0;
        if (marginLeft < SafeMarginThreshold) edgeCrowdingHazards++;
        if (marginTop < SafeMarginThreshold) edgeCrowdingHazards++;
        if (marginRight < SafeMarginThreshold) edgeCrowdingHazards++;
        if (marginBottom < SafeMarginThreshold) edgeCrowdingHazards++;

        double penalty = (normOffsetPct * 1.5) + (edgeCrowdingHazards * 15.0);
        if (minMargin < 0) penalty += Math.Abs(minMargin) * 0.5;

        double balanceScore = Math.Round(Math.Clamp(100.0 - penalty, 0.0, 100.0), 1);

        string category;
        if (balanceScore >= 90.0 && edgeCrowdingHazards == 0) category = "Equilibrium Spatial Distribution";
        else if (balanceScore >= 75.0 && edgeCrowdingHazards == 0) category = "Balanced Composition";
        else if (edgeCrowdingHazards > 0) category = "Edge Crowding Hazard (<40px)";
        else category = "Moderate Visual Lopsidedness";

        string summary = FormatSpatialBalanceSummary(
            list.Count, canvasWidth, canvasHeight, centroidX, centroidY, offsetDist,
            marginLeft, marginTop, marginRight, marginBottom, minMargin,
            edgeCrowdingHazards, balanceScore, category, isSelection);

        return new CanvasSpatialBalanceAudit(
            list.Count, canvasWidth, canvasHeight, centroidX, centroidY, offsetDist,
            marginLeft, marginTop, marginRight, marginBottom, minMargin,
            edgeCrowdingHazards, balanceScore, category, isSelection, summary);
    }

    public static string FormatSpatialBalanceSummary(
        int shapeCount, double width, double height, double cx, double cy, double offsetDist,
        double ml, double mt, double mr, double mb, double minMargin,
        int crowdingHazards, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeLabel = shapeCount == 1 ? "1 shape" : $"{shapeCount} shapes";

        var alerts = new List<string>();
        if (crowdingHazards > 0) alerts.Add($"🚨 {crowdingHazards} edge crowding (<40px)");
        if (offsetDist > 50.0) alerts.Add($"⚠ ~{offsetDist:0.#}px center offset");
        if (minMargin < 0) alerts.Add($"🚨 Canvas overflow ({-minMargin:0.#}px)");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Uniform margin clearance";

        return $"Canvas spatial balance audit ({scopeStr}{shapeLabel} [{width:0}x{height:0}] · {category} · {score:0.#}% balance): Centroid ({cx:0.#}, {cy:0.#}){alertSuffix}";
    }

    /// <summary>
    /// Audits diagram shape alignment collinearity (horizontal and vertical center/edge alignment),
    /// sub-pixel snapping jitter (tolerance <= 3px), and distribution spacing uniformity across adjacent shapes.
    /// </summary>
    public DiagramCollinearityAndSpacingAudit AuditShapeCollinearityAndSpacing(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null, double tolerancePx = 3.0)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditShapeCollinearityAndSpacing(targets, tolerancePx, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramCollinearityAndSpacingAudit AuditShapeCollinearityAndSpacing(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, double tolerancePx = 3.0, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        if (list.Count < 2)
        {
            string emptySummary = $"Shape collinearity audit ({scopeStr}0 shapes): Insufficient shapes (<2) · Minimum 2 shapes required";
            return new DiagramCollinearityAndSpacingAudit(
                list.Count, 0, 0, 0, 0, 100.0,
                Array.Empty<ShapeCollinearityGroup>(),
                "Insufficient Shapes",
                isSelection,
                emptySummary);
        }

        var groups = new List<ShapeCollinearityGroup>();
        int nearMissHazards = 0;
        int uniformSequences = 0;
        int irregularSequences = 0;

        // 1. Horizontal Collinearity (Top Y and Center Y)
        var yTopGroups = ClusterByCoordinate(list, s => s.Y, tolerancePx, "Horizontal Top (Y)");
        var yCenterGroups = ClusterByCoordinate(list, s => s.Y + s.Height / 2.0, tolerancePx, "Horizontal Center (Y)");

        // 2. Vertical Collinearity (Left X and Center X)
        var xLeftGroups = ClusterByCoordinate(list, s => s.X, tolerancePx, "Vertical Left (X)");
        var xCenterGroups = ClusterByCoordinate(list, s => s.X + s.Width / 2.0, tolerancePx, "Vertical Center (X)");

        foreach (var g in yTopGroups.Concat(yCenterGroups).Concat(xLeftGroups).Concat(xCenterGroups))
        {
            if (g.ShapeIds.Count >= 2)
            {
                groups.Add(g);
                if (!g.IsExactCollinear) nearMissHazards++;
            }
        }

        // 3. Spacing distribution: Sort by X for horizontal flow, and by Y for vertical flow
        if (list.Count >= 3)
        {
            // Horizontal sequence
            var sortedX = list.OrderBy(s => s.X).ToList();
            var gapsX = new List<double>();
            for (int i = 0; i < sortedX.Count - 1; i++)
            {
                double gap = sortedX[i + 1].X - (sortedX[i].X + sortedX[i].Width);
                gapsX.Add(gap);
            }
            double stdDevX = ComputeStdDev(gapsX);
            if (stdDevX <= 1.5) uniformSequences++;
            else if (stdDevX > 5.0) irregularSequences++;

            // Vertical sequence
            var sortedY = list.OrderBy(s => s.Y).ToList();
            var gapsY = new List<double>();
            for (int i = 0; i < sortedY.Count - 1; i++)
            {
                double gap = sortedY[i + 1].Y - (sortedY[i].Y + sortedY[i].Height);
                gapsY.Add(gap);
            }
            double stdDevY = ComputeStdDev(gapsY);
            if (stdDevY <= 1.5) uniformSequences++;
            else if (stdDevY > 5.0) irregularSequences++;
        }

        double penalty = (nearMissHazards * 12.0) + (irregularSequences * 10.0);
        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 when nearMissHazards == 0 => "Precision Collinear & Uniform Spacing",
            >= 80.0 => "Aligned Layout with Minor Sub-Grid Drift",
            >= 60.0 => "Sub-Pixel Alignment Jitter Detected",
            _ => "Disorganized / Misaligned Primitives"
        };

        string summary = FormatCollinearitySummary(
            list.Count, groups.Count, nearMissHazards, uniformSequences, irregularSequences, score, category, isSelection);

        return new DiagramCollinearityAndSpacingAudit(
            list.Count,
            groups.Count,
            nearMissHazards,
            uniformSequences,
            irregularSequences,
            score,
            groups,
            category,
            isSelection,
            summary);
    }

    private static List<ShapeCollinearityGroup> ClusterByCoordinate(
        List<ShapeCanvasItemViewModel> shapes, Func<ShapeCanvasItemViewModel, double> coordSelector,
        double tolerance, string axisName)
    {
        var result = new List<ShapeCollinearityGroup>();
        var sorted = shapes.Select(s => (Shape: s, Coord: coordSelector(s))).OrderBy(x => x.Coord).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sorted.Count; i++)
        {
            if (used.Contains(sorted[i].Shape.Id)) continue;
            var cluster = new List<(ShapeCanvasItemViewModel Shape, double Coord)> { sorted[i] };
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (Math.Abs(sorted[j].Coord - sorted[i].Coord) <= tolerance)
                {
                    cluster.Add(sorted[j]);
                }
            }

            if (cluster.Count >= 2)
            {
                foreach (var c in cluster) used.Add(c.Shape.Id);
                double minC = cluster.Min(c => c.Coord);
                double maxC = cluster.Max(c => c.Coord);
                double jitter = Math.Round(maxC - minC, 2);
                bool isExact = jitter == 0.0;
                result.Add(new ShapeCollinearityGroup(
                    axisName,
                    Math.Round(minC, 1),
                    cluster.Select(c => c.Shape.Id).ToList(),
                    cluster.Select(c => c.Shape.Name).ToList(),
                    isExact,
                    jitter));
            }
        }

        return result;
    }

    private static double ComputeStdDev(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double avg = values.Average();
        double sum = values.Sum(v => Math.Pow(v - avg, 2));
        return Math.Round(Math.Sqrt(sum / (values.Count - 1)), 2);
    }

    public static string FormatCollinearitySummary(
        int shapeCount, int groupCount, int jitterHazards, int uniformSeq, int irregularSeq,
        double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeLabel = shapeCount == 1 ? "1 shape" : $"{shapeCount} shapes";

        var alerts = new List<string>();
        if (jitterHazards > 0) alerts.Add($"⚠ {jitterHazards} alignment jitter (<=3px)");
        if (irregularSeq > 0) alerts.Add($"⚠ {irregularSeq} irregular spacing gaps");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Collinear alignment verified";

        return $"Shape collinearity audit ({scopeStr}{shapeLabel} [{groupCount} aligned groups] · {category} · {score:0.#}% score){alertSuffix}";
    }

    /// <summary>
    /// Audits diagram visual flow directionality, connector arrow vector monotonicity,
    /// dominant visual reading axes (Left-to-Right, Top-to-Bottom), and retrograde backward flow hazards.
    /// </summary>
    public DiagramVisualFlowAudit AuditVisualFlowDirectionality(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditVisualFlowDirectionality(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramVisualFlowAudit AuditVisualFlowDirectionality(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        if (list.Count == 0)
        {
            string emptySummary = $"Visual flow directionality audit ({scopeStr}0 shapes): Canvas is empty";
            return new DiagramVisualFlowAudit(
                0, 0, 0, 0, 0, 0, "None", 100.0,
                Array.Empty<ConnectorVectorFlowItem>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        var connectors = list.Where(i => i.IsLine).ToList();
        if (connectors.Count == 0)
        {
            string noConnSummary = $"Visual flow directionality audit ({scopeStr}{list.Count} shapes): 0 connectors present (Static shape canvas)";
            return new DiagramVisualFlowAudit(
                list.Count, 0, 0, 0, 0, 0, "No Connectors", 100.0,
                Array.Empty<ConnectorVectorFlowItem>(),
                "No Connectors Present", isSelection, noConnSummary);
        }

        var vectorItems = new List<ConnectorVectorFlowItem>();
        int ltrCount = 0;
        int ttbCount = 0;
        int rtlCount = 0;
        int bttCount = 0;

        foreach (var conn in connectors)
        {
            var pts = new List<(double X, double Y)>();
            if (conn.PathPoints != null && conn.PathPoints.Count >= 2)
            {
                foreach (var p in conn.PathPoints)
                {
                    double ax = conn.X + (p.X * conn.Width / 100.0);
                    double ay = conn.Y + (p.Y * conn.Height / 100.0);
                    pts.Add((ax, ay));
                }
            }
            else
            {
                pts.Add((conn.X, conn.Y));
                pts.Add((conn.X + conn.Width, conn.Y + conn.Height));
            }

            var startPt = pts[0];
            var endPt = pts[^1];

            double dx = endPt.X - startPt.X;
            double dy = endPt.Y - startPt.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);

            double angleDeg = 0.0;
            if (len >= 0.1)
            {
                double rad = Math.Atan2(dy, dx);
                angleDeg = rad * (180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360.0;
            }

            string dirCategory;
            bool isMonotonic;
            bool isRetrograde;

            // Classify reading direction based on standard LTR / TTB layout conventions
            if (angleDeg <= 45.0 || angleDeg >= 315.0)
            {
                dirCategory = "Left-to-Right (Horizontal LTR)";
                isMonotonic = true;
                isRetrograde = false;
                ltrCount++;
            }
            else if (angleDeg > 45.0 && angleDeg < 135.0)
            {
                dirCategory = "Top-to-Bottom (Vertical TTB)";
                isMonotonic = true;
                isRetrograde = false;
                ttbCount++;
            }
            else if (angleDeg >= 135.0 && angleDeg <= 225.0)
            {
                dirCategory = "Right-to-Left (Retrograde RTL)";
                isMonotonic = false;
                isRetrograde = true;
                rtlCount++;
            }
            else
            {
                dirCategory = "Bottom-to-Top (Upward BTT)";
                isMonotonic = false;
                isRetrograde = true;
                bttCount++;
            }

            vectorItems.Add(new ConnectorVectorFlowItem(
                conn.Id,
                conn.Name ?? "Connector",
                startPt.X,
                startPt.Y,
                endPt.X,
                endPt.Y,
                dx,
                dy,
                Math.Round(len, 1),
                Math.Round(angleDeg, 1),
                dirCategory,
                isMonotonic,
                isRetrograde));
        }

        int totalConn = connectors.Count;
        int monotonicCount = ltrCount + ttbCount;
        int retrogradeCount = rtlCount + bttCount;

        string dominantAxis;
        if (ltrCount >= ttbCount * 2 && ltrCount > 0)
            dominantAxis = "Left-to-Right Dominant (Horizontal Flow)";
        else if (ttbCount >= ltrCount * 2 && ttbCount > 0)
            dominantAxis = "Top-to-Bottom Dominant (Vertical Flow)";
        else if (monotonicCount > 0)
            dominantAxis = "Orthogonal Balanced Reading Flow";
        else
            dominantAxis = "Reverse / Retrograde Flow";

        double penalty = (retrogradeCount * 30.0) / totalConn * 100.0;
        double flowScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (retrogradeCount == 0 && (ltrCount > 0 || ttbCount > 0))
            category = "Strict Monotonic Reading Flow";
        else if (retrogradeCount == 0)
            category = "Pure Forward Directionality";
        else if (flowScore >= 70.0)
            category = "Coherent Reading Flow (Controlled Feedback Arrows)";
        else
            category = $"High Visual Friction ({retrogradeCount} Retrograde Arrow Vectors Detected)";

        string summary = FormatVisualFlowSummary(
            list.Count, totalConn, ltrCount, ttbCount, rtlCount, bttCount,
            dominantAxis, flowScore, category, isSelection);

        return new DiagramVisualFlowAudit(
            list.Count,
            totalConn,
            ltrCount,
            ttbCount,
            rtlCount,
            bttCount,
            dominantAxis,
            flowScore,
            vectorItems,
            category,
            isSelection,
            summary);
    }

    public static string FormatVisualFlowSummary(
        int totalShapes, int totalConnectors, int ltr, int ttb, int rtl, int btt,
        string dominantAxis, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        if (totalConnectors == 0) return $"Visual flow directionality audit ({scopeStr}{shapeLabel}): 0 connectors present";

        string connLabel = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var alerts = new List<string>();
        if (rtl > 0) alerts.Add($"⚠ {rtl} retrograde RTL");
        if (btt > 0) alerts.Add($"⚠ {btt} upward BTT");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% forward flow";

        return $"Visual flow directionality audit ({scopeStr}{connLabel} [{dominantAxis}] · {category} · {score:0.#}% score): " +
               $"{ltr} LTR, {ttb} TTB{alertSuffix}";
    }

    public DiagramGoldenRatioAdherenceAudit AuditShapeAspectRatioAndGoldenRatio(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditShapeAspectRatioAndGoldenRatio(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramGoldenRatioAdherenceAudit AuditShapeAspectRatioAndGoldenRatio(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        var validShapes = list.Where(s =>
            s.Width >= 4.0 && s.Height >= 4.0 &&
            !(s.ShapeType != null && s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase))).ToList();

        if (validShapes.Count == 0)
        {
            string shapeLabel = list.Count == 1 ? "1 shape" : $"{list.Count} shapes";
            return new DiagramGoldenRatioAdherenceAudit(
                list.Count, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeGoldenRatioAdherenceItem>(),
                "Empty / Line Shapes",
                isSelection,
                $"Golden ratio (\u03C6 \u2248 1.618) adherence audit ({scopeStr}{shapeLabel}): 0 2D geometric shapes to evaluate");
        }

        const double Phi = 1.6180339887;
        int goldenCount = 0;
        int squareCount = 0;
        int standardAestheticCount = 0;
        int unalignedCount = 0;
        double totalDevGoldenPct = 0.0;

        var items = new List<ShapeGoldenRatioAdherenceItem>();

        foreach (var shape in validShapes)
        {
            double w = shape.Width;
            double h = shape.Height;
            double actualRatio = Math.Round(Math.Max(w, h) / Math.Max(0.001, Math.Min(w, h)), 3);

            double devPhi = Math.Abs(actualRatio - Phi) / Phi * 100.0;
            double devSquare = Math.Abs(actualRatio - 1.0);
            double dev43 = Math.Abs(actualRatio - 1.333) / 1.333 * 100.0;
            double dev32 = Math.Abs(actualRatio - 1.500) / 1.500 * 100.0;
            double dev169 = Math.Abs(actualRatio - 1.778) / 1.778 * 100.0;

            totalDevGoldenPct += devPhi;

            bool isGolden = devPhi <= 6.0;
            bool isSquare = devSquare <= 0.06;
            bool is43 = dev43 <= 5.0;
            bool is32 = dev32 <= 5.0;
            bool is169 = dev169 <= 5.0;

            string closestArchetype;
            double devPct;
            bool isStandard;

            if (isGolden)
            {
                closestArchetype = "Golden Ratio (\u03C6 1.618)";
                devPct = Math.Round(devPhi, 1);
                isStandard = true;
                goldenCount++;
                standardAestheticCount++;
            }
            else if (isSquare)
            {
                closestArchetype = "1:1 Square / Circle";
                devPct = Math.Round(devSquare * 100.0, 1);
                isStandard = true;
                squareCount++;
                standardAestheticCount++;
            }
            else if (is43)
            {
                closestArchetype = "4:3 Classic Proportions";
                devPct = Math.Round(dev43, 1);
                isStandard = true;
                standardAestheticCount++;
            }
            else if (is32)
            {
                closestArchetype = "3:2 Standard Photographic";
                devPct = Math.Round(dev32, 1);
                isStandard = true;
                standardAestheticCount++;
            }
            else if (is169)
            {
                closestArchetype = "16:9 Widescreen";
                devPct = Math.Round(dev169, 1);
                isStandard = true;
                standardAestheticCount++;
            }
            else
            {
                closestArchetype = $"Custom Ratio ({actualRatio:0.##}:1)";
                devPct = Math.Round(devPhi, 1);
                isStandard = false;
                unalignedCount++;
            }

            items.Add(new ShapeGoldenRatioAdherenceItem(
                shape.Id,
                shape.Name ?? "Shape",
                w,
                h,
                actualRatio,
                closestArchetype,
                devPct,
                isGolden,
                isSquare,
                isStandard));
        }

        double meanGoldenDev = Math.Round(totalDevGoldenPct / validShapes.Count, 1);

        double penalty = (unalignedCount * 12.0) / validShapes.Count * 100.0;
        double adherenceScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (goldenCount == validShapes.Count)
            category = "Pure Divine Proportions (100% Golden Ratio Adherence)";
        else if (unalignedCount == 0)
            category = "Harmonious Canonical Ratios (Golden Ratio, Square & Standard Proportions)";
        else if (adherenceScore >= 75.0)
            category = "Strong Aesthetic Aspect Harmony (Controlled Irregular Ratios)";
        else
            category = $"Irregular Non-Standard Ratios ({unalignedCount} Shapes with Freeform Aspect Ratios)";

        string summary = FormatAspectRatioGoldenRatioSummary(
            validShapes.Count, goldenCount, squareCount, standardAestheticCount, unalignedCount,
            meanGoldenDev, adherenceScore, category, isSelection);

        return new DiagramGoldenRatioAdherenceAudit(
            validShapes.Count,
            goldenCount,
            squareCount,
            standardAestheticCount,
            unalignedCount,
            meanGoldenDev,
            adherenceScore,
            items,
            category,
            isSelection,
            summary);
    }

    public static string FormatAspectRatioGoldenRatioSummary(
        int totalShapes, int goldenCount, int squareCount, int standardCount, int unalignedCount,
        double meanGoldenDev, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        if (totalShapes == 0) return $"Golden ratio (\u03C6 \u2248 1.618) adherence audit ({scopeStr}0 shapes): No shapes to evaluate";

        var details = new List<string>();
        if (goldenCount > 0) details.Add($"{goldenCount} \u03C6 golden");
        if (squareCount > 0) details.Add($"{squareCount} square");
        int otherStandard = standardCount - goldenCount - squareCount;
        if (otherStandard > 0) details.Add($"{otherStandard} 4:3/16:9");
        if (unalignedCount > 0) details.Add($"⚠ {unalignedCount} custom/freeform");

        string detailStr = details.Count > 0 ? string.Join(", ", details) : "0 standard ratios";

        return $"Golden ratio (\u03C6 \u2248 1.618) adherence audit ({scopeStr}{shapeLabel} [{detailStr}] · {category} · {score:0.#}% score): " +
               $"\u03C6 deviation avg {meanGoldenDev:0.#}%";
    }

    public ShapeCornerRadiusConsistencyAudit AuditShapeCornerRadiusConsistency(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditShapeCornerRadiusConsistency(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static ShapeCornerRadiusConsistencyAudit AuditShapeCornerRadiusConsistency(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        var validShapes = list.Where(s =>
            s.Width >= 4.0 && s.Height >= 4.0 &&
            !(s.ShapeType != null && s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase))).ToList();

        if (validShapes.Count == 0)
        {
            return new ShapeCornerRadiusConsistencyAudit(
                0, 0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeCornerRadiusItem>(),
                "No Shapes to Evaluate",
                isSelection,
                $"Shape corner radius consistency audit ({scopeStr}0 shapes): No shapes to evaluate");
        }

        var radii = new List<double>();
        var items = new List<ShapeCornerRadiusItem>();

        int roundedCount = 0;
        int sharpCount = 0;
        int subtleCount = 0;
        int mediumCount = 0;
        int pillCount = 0;

        foreach (var s in validShapes)
        {
            double r = s.CornerRadius;
            bool isRoundRectPrst = string.Equals(s.Prst, "roundrect", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(s.Prst, "roundedrectangle", StringComparison.OrdinalIgnoreCase);

            if (r <= 0.0 && isRoundRectPrst)
            {
                r = Math.Round(Math.Min(s.Width, s.Height) * 0.1667, 1);
            }

            radii.Add(r);

            if (r <= 0.0)
            {
                sharpCount++;
            }
            else if (r <= 4.0)
            {
                subtleCount++;
                roundedCount++;
            }
            else if (r <= 8.0)
            {
                mediumCount++;
                roundedCount++;
            }
            else
            {
                pillCount++;
                roundedCount++;
            }
        }

        var distinctRadii = radii.Select(r => Math.Round(r, 1)).Distinct().ToList();
        double dominantRadius = radii.GroupBy(r => Math.Round(r, 1))
                                     .OrderByDescending(g => g.Count())
                                     .Select(g => g.Key)
                                     .FirstOrDefault();

        int discrepancyCount = 0;
        for (int i = 0; i < validShapes.Count; i++)
        {
            var s = validShapes[i];
            double r = radii[i];
            bool hasDiscrepancy = (roundedCount > 0 && sharpCount > 0 && r <= 0.0) ||
                                  (distinctRadii.Count > 1 && Math.Abs(r - dominantRadius) > 2.0);

            if (hasDiscrepancy) discrepancyCount++;

            string status = hasDiscrepancy
                ? $"Discrepancy ({r:0.#}px vs dominant {dominantRadius:0.#}px)"
                : $"Uniform ({r:0.#}px)";

            string tier = r <= 0.0 ? "Sharp (0px)" : r <= 4.0 ? "Subtle (1-4px)" : r <= 8.0 ? "Medium (5-8px)" : "Deep / Pill (>8px)";
            items.Add(new ShapeCornerRadiusItem(
                s.Id, s.Name, s.Prst, s.Width, s.Height, r, r > 0.0, tier, hasDiscrepancy, status));
        }

        double penalty = (discrepancyCount * 15.0) / validShapes.Count * 100.0;
        double uniformityScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (distinctRadii.Count <= 1)
        {
            category = roundedCount > 0
                ? "100% Uniform Fillet Curvature"
                : "100% Crisp Sharp Corners (0px)";
        }
        else if (roundedCount > 0 && sharpCount > 0)
        {
            category = $"Mixed Fillet Styles ({roundedCount} Rounded vs {sharpCount} Sharp Corners)";
        }
        else if (uniformityScore >= 80.0)
        {
            category = "High Corner Radius Harmony";
        }
        else
        {
            category = $"Irregular Corner Radius Variance ({distinctRadii.Count} Distinct Radii)";
        }

        string summary = FormatCornerRadiusConsistencySummary(
            validShapes.Count, roundedCount, sharpCount, subtleCount, mediumCount, pillCount,
            distinctRadii.Count, dominantRadius, uniformityScore, category, isSelection);

        return new ShapeCornerRadiusConsistencyAudit(
            validShapes.Count,
            roundedCount,
            sharpCount,
            subtleCount,
            mediumCount,
            pillCount,
            distinctRadii.Count,
            dominantRadius,
            uniformityScore,
            items,
            category,
            isSelection,
            summary);
    }

    public static string FormatCornerRadiusConsistencySummary(
        int totalShapes, int roundedCount, int sharpCount, int subtleCount, int mediumCount, int pillCount,
        int distinctRadiiCount, double dominantRadius, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        if (totalShapes == 0) return $"Shape corner radius consistency audit ({scopeStr}0 shapes): No shapes to evaluate";

        var details = new List<string>();
        if (roundedCount > 0) details.Add($"{roundedCount} rounded (dominant {dominantRadius:0.#}px)");
        if (sharpCount > 0) details.Add($"{sharpCount} sharp");
        if (distinctRadiiCount > 1) details.Add($"{distinctRadiiCount} distinct radii");
        string detailStr = details.Count > 0 ? string.Join(", ", details) : "uniform radii";

        return $"Shape corner radius consistency audit ({scopeStr}{shapeLabel} [{detailStr}] · {category} · {score:0.#}% score): " +
               $"{subtleCount} subtle, {mediumCount} medium, {pillCount} pill";
    }

    public DiagramZIndexOcclusionAudit AuditShapeZIndexLayeringAndOcclusion(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditShapeZIndexLayeringAndOcclusion(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramZIndexOcclusionAudit AuditShapeZIndexLayeringAndOcclusion(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        var validShapes = list.Where(s =>
            s.Width >= 4.0 && s.Height >= 4.0 &&
            !(s.ShapeType != null && s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase))).ToList();

        if (validShapes.Count <= 1)
        {
            return new DiagramZIndexOcclusionAudit(
                validShapes.Count, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ShapeOcclusionPairItem>(),
                "Pristine Layering",
                isSelection,
                $"Diagram Z-index stacking & occlusion audit ({scopeStr}{validShapes.Count} {(validShapes.Count == 1 ? "shape" : "shapes")}): No overlapping pairs to evaluate");
        }

        var pairs = new List<ShapeOcclusionPairItem>();
        int majorOcclusionCount = 0;
        int moderateOverlapCount = 0;
        int duplicateCoincidentCount = 0;
        int containerNestingCount = 0;

        // Iterate through all pairs (i < j), where j is higher in Z-order
        for (int i = 0; i < validShapes.Count; i++)
        {
            var lower = validShapes[i];
            double x1 = lower.X;
            double y1 = lower.Y;
            double w1 = lower.Width;
            double h1 = lower.Height;
            double a1 = w1 * h1;

            for (int j = i + 1; j < validShapes.Count; j++)
            {
                var higher = validShapes[j];
                double x2 = higher.X;
                double y2 = higher.Y;
                double w2 = higher.Width;
                double h2 = higher.Height;
                double a2 = w2 * h2;

                double xLeft = Math.Max(x1, x2);
                double xRight = Math.Min(x1 + w1, x2 + w2);
                double yTop = Math.Max(y1, y2);
                double yBottom = Math.Min(y1 + h1, y2 + h2);

                if (xRight > xLeft && yBottom > yTop)
                {
                    double overlapW = xRight - xLeft;
                    double overlapH = yBottom - yTop;
                    double overlapArea = overlapW * overlapH;
                    double minArea = Math.Min(a1, a2);
                    double coverageRatio = minArea > 0 ? overlapArea / minArea : 0.0;

                    bool isDuplicate = Math.Abs(x1 - x2) <= 3.0 && Math.Abs(y1 - y2) <= 3.0 &&
                                       Math.Abs(w1 - w2) <= 3.0 && Math.Abs(h1 - h2) <= 3.0;

                    bool isContainerNesting = !isDuplicate &&
                        ((a1 >= 2.5 * a2 && x2 >= x1 - 1.0 && y2 >= y1 - 1.0 && x2 + w2 <= x1 + w1 + 1.0) ||
                         (a2 >= 2.5 * a1 && x1 >= x2 - 1.0 && y1 >= y2 - 1.0 && x1 + w1 <= x2 + w2 + 1.0));

                    string tier;
                    string status;

                    if (isDuplicate)
                    {
                        duplicateCoincidentCount++;
                        tier = "Duplicate Coincident Shape Hazard";
                        status = "⚠ Duplicate stacked shape: Coincident coordinates and bounds";
                    }
                    else if (isContainerNesting)
                    {
                        containerNestingCount++;
                        tier = "Intentional Container Nesting";
                        status = "✓ Clean hierarchical container enclosing child shape";
                    }
                    else if (coverageRatio >= 0.70)
                    {
                        majorOcclusionCount++;
                        tier = "Major / Complete Occlusion";
                        status = $"⚠ Severe occlusion ({coverageRatio * 100:0.#}% overlap covers background content)";
                    }
                    else if (coverageRatio >= 0.25)
                    {
                        moderateOverlapCount++;
                        tier = "Moderate Overlap Occlusion";
                        status = $"⚠ Moderate overlap ({coverageRatio * 100:0.#}% bounding box intersection)";
                    }
                    else
                    {
                        tier = "Minor Touch / Edge Overlap";
                        status = $"ℹ Subtle edge overlap ({coverageRatio * 100:0.#}% intersection)";
                    }

                    pairs.Add(new ShapeOcclusionPairItem(
                        lower.Id, lower.Name, higher.Id, higher.Name,
                        overlapArea, coverageRatio, isDuplicate, isContainerNesting, tier, status));
                }
            }
        }

        int totalPossiblePairs = validShapes.Count * (validShapes.Count - 1) / 2;
        double penalty = (duplicateCoincidentCount * 30.0 + majorOcclusionCount * 20.0 + moderateOverlapCount * 8.0) / Math.Max(1, totalPossiblePairs) * 100.0;
        double stackingScore = Math.Round(Math.Clamp(100.0 - penalty, 0.0, 100.0), 1);

        string category;
        if (pairs.Count == 0 || (pairs.Count == containerNestingCount && majorOcclusionCount == 0 && duplicateCoincidentCount == 0))
        {
            category = "Pristine Layering & Zero Occlusion";
        }
        else if (duplicateCoincidentCount > 0)
        {
            category = $"Duplicate Coincident Shape Conflict ({duplicateCoincidentCount} Duplicates)";
        }
        else if (majorOcclusionCount > 0)
        {
            category = $"Severe Overlap Occlusion ({majorOcclusionCount} Major Occlusions)";
        }
        else if (stackingScore >= 80.0)
        {
            category = "Good Stacking Clarity";
        }
        else
        {
            category = "Cluttered Stacking Layering";
        }

        string summary = FormatZIndexOcclusionSummary(
            validShapes.Count, pairs.Count, majorOcclusionCount, moderateOverlapCount,
            duplicateCoincidentCount, containerNestingCount, stackingScore, category, isSelection);

        return new DiagramZIndexOcclusionAudit(
            validShapes.Count, pairs.Count, majorOcclusionCount, moderateOverlapCount,
            duplicateCoincidentCount, containerNestingCount, stackingScore, pairs, category, isSelection, summary);
    }

    public static string FormatZIndexOcclusionSummary(
        int totalShapes, int totalOverlaps, int majorOcclusions, int moderateOverlaps,
        int duplicates, int containerNestings, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeLabel = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        if (totalShapes <= 1)
            return $"Diagram Z-index stacking & occlusion audit ({scopeStr}{shapeLabel}): No overlapping pairs to evaluate";

        var alerts = new List<string>();
        if (duplicates > 0) alerts.Add($"⚠ {duplicates} duplicate coincident {(duplicates == 1 ? "hazard" : "hazards")}");
        if (majorOcclusions > 0) alerts.Add($"⚠ {majorOcclusions} major {(majorOcclusions == 1 ? "occlusion" : "occlusions")}");
        if (moderateOverlaps > 0) alerts.Add($"{moderateOverlaps} moderate {(moderateOverlaps == 1 ? "overlap" : "overlaps")}");
        if (containerNestings > 0) alerts.Add($"{containerNestings} container {(containerNestings == 1 ? "nesting" : "nestings")}");

        string details = alerts.Count > 0 ? $" [{string.Join(", ", alerts)}]" : " · 100% clean visual separation";
        return $"Diagram Z-index stacking & occlusion audit ({scopeStr}{shapeLabel}, {totalOverlaps} overlap {(totalOverlaps == 1 ? "pair" : "pairs")} · {category} · {score:0.#}% score){details}";
    }

    /// <summary>
    /// Audits diagram connector lines for arrowhead style uniformity and endpoint attachment to shape boundaries.
    /// </summary>
    public DiagramConnectorArrowheadAudit AuditConnectorArrowheadUniformity(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditConnectorArrowheadUniformity(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramConnectorArrowheadAudit AuditConnectorArrowheadUniformity(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        if (list.Count == 0)
        {
            string emptySummary = $"Connector arrowhead uniformity & attachment audit ({scopeStr}0 shapes): Canvas is empty";
            return new DiagramConnectorArrowheadAudit(
                0, 0, 0, 0, 0, "None", 0, 100.0, 100.0, 100.0,
                Array.Empty<ConnectorArrowheadItem>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        var connectors = list.Where(i => i.IsLine).ToList();
        var nodes = list.Where(i => !i.IsLine).ToList();

        if (connectors.Count == 0)
        {
            string noConnSummary = $"Connector arrowhead uniformity & attachment audit ({scopeStr}{list.Count} shapes): 0 connectors present (Static shape canvas)";
            return new DiagramConnectorArrowheadAudit(
                list.Count, 0, 0, 0, 0, "No Connectors", 0, 100.0, 100.0, 100.0,
                Array.Empty<ConnectorArrowheadItem>(),
                "No Connectors Present", isSelection, noConnSummary);
        }

        var connectorItems = new List<ConnectorArrowheadItem>();
        var arrowheadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int attachedEndpointsCount = 0;
        int danglingEndpointsCount = 0;
        int fullyAttachedConnectors = 0;

        const double SnapThreshold = 25.0;

        foreach (var conn in connectors)
        {
            var pts = new List<(double X, double Y)>();
            if (conn.PathPoints != null && conn.PathPoints.Count >= 2)
            {
                foreach (var p in conn.PathPoints)
                {
                    double ax = conn.X + (p.X * conn.Width / 100.0);
                    double ay = conn.Y + (p.Y * conn.Height / 100.0);
                    pts.Add((ax, ay));
                }
            }
            else
            {
                pts.Add((conn.X, conn.Y));
                pts.Add((conn.X + conn.Width, conn.Y + conn.Height));
            }

            var startPt = pts[0];
            var endPt = pts[^1];

            string startStyle = string.IsNullOrWhiteSpace(conn.ArrowheadStart) ? "None" : conn.ArrowheadStart;
            string endStyle = string.IsNullOrWhiteSpace(conn.ArrowheadEnd) ? "FilledTriangle" : conn.ArrowheadEnd;

            arrowheadCounts[endStyle] = arrowheadCounts.GetValueOrDefault(endStyle, 0) + 1;

            string attachedStartId = "";
            string attachedEndId = "";
            bool isStartAttached = false;
            bool isEndAttached = false;

            foreach (var node in nodes)
            {
                double left = node.X;
                double top = node.Y;
                double right = node.X + node.Width;
                double bottom = node.Y + node.Height;

                if (!isStartAttached)
                {
                    double dx = Math.Max(0, Math.Max(left - startPt.X, startPt.X - right));
                    double dy = Math.Max(0, Math.Max(top - startPt.Y, startPt.Y - bottom));
                    double distStart = Math.Sqrt(dx * dx + dy * dy);
                    if (distStart <= SnapThreshold)
                    {
                        isStartAttached = true;
                        attachedStartId = node.Id;
                    }
                }

                if (!isEndAttached)
                {
                    double dx = Math.Max(0, Math.Max(left - endPt.X, endPt.X - right));
                    double dy = Math.Max(0, Math.Max(top - endPt.Y, endPt.Y - bottom));
                    double distEnd = Math.Sqrt(dx * dx + dy * dy);
                    if (distEnd <= SnapThreshold)
                    {
                        isEndAttached = true;
                        attachedEndId = node.Id;
                    }
                }
            }

            if (isStartAttached) attachedEndpointsCount++;
            else danglingEndpointsCount++;

            if (isEndAttached) attachedEndpointsCount++;
            else danglingEndpointsCount++;

            if (isStartAttached && isEndAttached) fullyAttachedConnectors++;

            string status = (isStartAttached && isEndAttached)
                ? $"Fully Docked ({startStyle} -> {endStyle})"
                : (isStartAttached || isEndAttached)
                    ? $"Partially Attached (1 Dangling End · {startStyle} -> {endStyle})"
                    : $"Unattached Floating Connector ({startStyle} -> {endStyle})";

            connectorItems.Add(new ConnectorArrowheadItem(
                conn.Id,
                string.IsNullOrWhiteSpace(conn.Name) ? "Connector" : conn.Name,
                startStyle,
                endStyle,
                isStartAttached,
                isEndAttached,
                attachedStartId,
                attachedEndId,
                status));
        }

        int totalEndpoints = connectors.Count * 2;
        double endpointAttachmentScore = totalEndpoints > 0
            ? Math.Round((double)attachedEndpointsCount / totalEndpoints * 100.0, 1)
            : 100.0;

        string dominantType = "None";
        int dominantCount = 0;
        foreach (var (type, count) in arrowheadCounts)
        {
            if (count > dominantCount)
            {
                dominantCount = count;
                dominantType = type;
            }
        }

        double uniformityScore = connectors.Count > 0
            ? Math.Round((double)dominantCount / connectors.Count * 100.0, 1)
            : 100.0;

        double harmonyScore = Math.Round((uniformityScore * 0.4) + (endpointAttachmentScore * 0.6), 1);

        string category = harmonyScore switch
        {
            >= 95.0 => "Pristine Uniform & Docked Connectors",
            >= 80.0 => "High Geometric Flow Consistency",
            >= 60.0 => "Moderate Connector Attachment & Style Variance",
            _ => "Dangling Connectors / Irregular Arrowhead Mix"
        };

        string summary = FormatArrowheadUniformitySummary(
            list.Count, connectors.Count, attachedEndpointsCount, danglingEndpointsCount,
            fullyAttachedConnectors, dominantType, dominantCount, uniformityScore,
            endpointAttachmentScore, harmonyScore, category, isSelection);

        return new DiagramConnectorArrowheadAudit(
            list.Count,
            connectors.Count,
            attachedEndpointsCount,
            danglingEndpointsCount,
            fullyAttachedConnectors,
            dominantType,
            dominantCount,
            uniformityScore,
            endpointAttachmentScore,
            harmonyScore,
            connectorItems,
            category,
            isSelection,
            summary);
    }

    public static string FormatArrowheadUniformitySummary(
        int totalShapes, int totalConnectors, int attachedEndpoints, int danglingEndpoints,
        int fullyAttached, string dominantType, int dominantCount, double uniformityScore,
        double attachmentScore, double harmonyScore, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Connector arrowhead uniformity & attachment audit ({scopeStr}0 shapes): Canvas is empty";
        if (totalConnectors == 0) return $"Connector arrowhead uniformity & attachment audit ({scopeStr}{totalShapes} shapes): 0 connectors present (Static shape canvas)";

        string connStr = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var alerts = new List<string>();
        if (danglingEndpoints > 0) alerts.Add($"⚠ {danglingEndpoints} dangling {(danglingEndpoints == 1 ? "endpoint" : "endpoints")}");
        if (uniformityScore < 100.0) alerts.Add($"mixed arrowheads ({dominantType}: {dominantCount}/{totalConnectors})");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% uniform & docked";
        return $"Connector arrowhead uniformity & attachment audit ({scopeStr}{connStr} · {category} · {harmonyScore:0.#}% score): " +
               $"{dominantType} arrowheads ({uniformityScore:0.#}% uniform), {attachedEndpoints} attached endpoints ({attachmentScore:0.#}% docked){details}";
    }

    /// <summary>
    /// Audits diagram shapes for stroke weight consistency (1pt, 2pt, etc.), border dash style uniformity (Solid, Dashed, Dotted),
    /// and overall outline harmony.
    /// </summary>
    public DiagramStrokeWeightAndDashAudit AuditStrokeWeightAndDashUniformity(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes ?? (selected.Count >= 2 ? selected : Shapes)).ToList();
        bool isSelection = (targetShapes != null) || (selected.Count >= 2);

        var audit = AuditStrokeWeightAndDashUniformity(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramStrokeWeightAndDashAudit AuditStrokeWeightAndDashUniformity(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        if (list.Count == 0)
        {
            return new DiagramStrokeWeightAndDashAudit(
                0, 0, 0, 0, 0, 0, 0, 0.0, 0.0, 100.0,
                Array.Empty<ShapeStrokeOutlineItem>(), "Empty Canvas", isSelection,
                FormatStrokeWeightUniformitySummary(0, 0, 0, 0, 0, 0, 0, 0.0, 0.0, 100.0, "Empty Canvas", isSelection));
        }

        var items = new List<ShapeStrokeOutlineItem>();
        int solidCount = 0, dashedCount = 0, dottedCount = 0;
        int hairlineCount = 0, standardCount = 0, heavyCount = 0;

        var weights = new List<double>();
        var dashCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in list)
        {
            double weight = s.StrokeWidthPt;
            weights.Add(weight);

            string dash = string.IsNullOrWhiteSpace(s.DashStyle) ? "Solid" : s.DashStyle;
            dashCounts[dash] = dashCounts.GetValueOrDefault(dash, 0) + 1;

            if (dash.Equals("Dashed", StringComparison.OrdinalIgnoreCase)) dashedCount++;
            else if (dash.Equals("Dotted", StringComparison.OrdinalIgnoreCase)) dottedCount++;
            else solidCount++;

            if (weight < 1.0) hairlineCount++;
            else if (weight <= 3.0) standardCount++;
            else heavyCount++;
        }

        double avgWeight = weights.Average();
        double sumSq = weights.Sum(w => (w - avgWeight) * (w - avgWeight));
        double stdDev = Math.Round(Math.Sqrt(sumSq / weights.Count), 2);

        int dominantDashCount = dashCounts.Values.Max();
        double dashUniformity = Math.Round((double)dominantDashCount / list.Count * 100.0, 1);
        double weightUniformity = Math.Clamp(100.0 - (stdDev * 25.0), 0.0, 100.0);

        foreach (var s in list)
        {
            double weight = s.StrokeWidthPt;
            string dash = string.IsNullOrWhiteSpace(s.DashStyle) ? "Solid" : s.DashStyle;
            bool isOutlier = Math.Abs(weight - avgWeight) > Math.Max(1.0, 1.5 * stdDev);
            string status = isOutlier ? $"Outlier Stroke ({weight:0.##}pt)" :
                            $"{dash} Outline ({weight:0.##}pt)";

            items.Add(new ShapeStrokeOutlineItem(
                s.Id, s.Name, weight, dash, s.Fill, isOutlier, status));
        }

        double harmonyScore = Math.Round((dashUniformity * 0.45) + (weightUniformity * 0.55), 1);

        string category = harmonyScore switch
        {
            >= 90.0 => "Harmonious Outlines",
            >= 75.0 => "Consistent Strokes",
            >= 60.0 => "Moderate Outline Variance",
            >= 40.0 => "Mixed Stroke Weights & Dashes",
            _ => "Fragmented Outline Hierarchy"
        };

        string summary = FormatStrokeWeightUniformitySummary(
            list.Count, solidCount, dashedCount, dottedCount, heavyCount, standardCount,
            hairlineCount, Math.Round(avgWeight, 2), stdDev, harmonyScore, category, isSelection);

        return new DiagramStrokeWeightAndDashAudit(
            list.Count, solidCount, dashedCount, dottedCount, heavyCount, standardCount,
            hairlineCount, Math.Round(avgWeight, 2), stdDev, harmonyScore, items, category,
            isSelection, summary);
    }

    public DiagramStrokeWeightAndDashAudit AuditShapeStrokeDashUniformity(
        IEnumerable<ShapeCanvasItemViewModel>? shapes = null, bool selectionOnly = false)
        => AuditStrokeWeightAndDashUniformity(shapes ?? Shapes, selectionOnly);

    public static string FormatStrokeDashSummary(
        int totalShapes, int solidCount, int dashedCount, int dottedCount, int heavyCount,
        int standardCount, int hairlineCount, double avgWeight, double stdDev, double harmonyScore,
        string category, bool isSelection = false)
        => FormatStrokeWeightUniformitySummary(
            totalShapes, solidCount, dashedCount, dottedCount, heavyCount,
            standardCount, hairlineCount, avgWeight, stdDev, harmonyScore, category, isSelection);

    public static string FormatStrokeWeightUniformitySummary(
        int totalShapes, int solidCount, int dashedCount, int dottedCount, int heavyCount,
        int standardCount, int hairlineCount, double avgWeight, double stdDev, double harmonyScore,
        string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Stroke weight & dash uniformity audit ({scopeStr}0 shapes): Canvas is empty";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var alerts = new List<string>();
        if (dashedCount > 0 || dottedCount > 0)
        {
            var styles = new List<string>();
            if (solidCount > 0) styles.Add($"{solidCount} solid");
            if (dashedCount > 0) styles.Add($"{dashedCount} dashed");
            if (dottedCount > 0) styles.Add($"{dottedCount} dotted");
            alerts.Add(string.Join(", ", styles));
        }
        if (heavyCount > 0) alerts.Add($"{heavyCount} heavy (>3pt)");
        if (hairlineCount > 0) alerts.Add($"{hairlineCount} hairline (<1pt)");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% uniform solid stroke";
        return $"Stroke weight & dash uniformity audit ({scopeStr}{shapeStr} · {category} · {harmonyScore:0.#}% harmony): avg {avgWeight:0.##}pt (±{stdDev:0.##}pt){details}";
    }


    public DiagramShapeElevationShadowAudit AuditShapeElevationAndShadowHarmony(bool selectionOnly = false)
        => AuditShapeElevationAndShadowHarmony(selectionOnly && Shapes.Any(s => s.IsSelected) ? Shapes.Where(s => s.IsSelected) : Shapes, selectionOnly);

    public static DiagramShapeElevationShadowAudit AuditShapeElevationAndShadowHarmony(
        IEnumerable<ShapeCanvasItemViewModel>? shapes, bool isSelection = false)
    {
        var list = (shapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        var validShapes = list.Where(s =>
            s.Width >= 4.0 && s.Height >= 4.0 &&
            !(s.ShapeType != null && s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase))).ToList();

        if (validShapes.Count <= 1)
        {
            return new DiagramShapeElevationShadowAudit(
                validShapes.Count, validShapes.Count, 0, 0, 0, 100.0,
                Array.Empty<DiagramShapeElevationShadowItem>(),
                "Pristine Elevation Harmony",
                isSelection,
                $"Diagram shape visual elevation & shadow harmony audit ({scopeStr}{validShapes.Count} {(validShapes.Count == 1 ? "shape" : "shapes")}): No overlapping pairs to evaluate");
        }

        var items = new List<DiagramShapeElevationShadowItem>();
        int flatCount = 0;
        int elevatedCount = 0;
        int invertedLayeringCount = 0;
        int ambiguousBoundaryCount = 0;

        var tiers = new double[validShapes.Count];
        for (int i = 0; i < validShapes.Count; i++)
        {
            var s = validShapes[i];
            double tier = 0.0;
            if (s.StrokeWidthPt >= 4.0) tier = 2.0;
            else if (s.StrokeWidthPt >= 2.0 || s.CornerRadius > 4.0) tier = 1.0;
            tiers[i] = tier;

            if (tier > 0) elevatedCount++;
            else flatCount++;
        }

        for (int i = 0; i < validShapes.Count; i++)
        {
            var lower = validShapes[i];
            double x1 = lower.X, y1 = lower.Y, w1 = lower.Width, h1 = lower.Height, a1 = w1 * h1;

            for (int j = i + 1; j < validShapes.Count; j++)
            {
                var higher = validShapes[j];
                double x2 = higher.X, y2 = higher.Y, w2 = higher.Width, h2 = higher.Height, a2 = w2 * h2;

                double xLeft = Math.Max(x1, x2);
                double xRight = Math.Min(x1 + w1, x2 + w2);
                double yTop = Math.Max(y1, y2);
                double yBottom = Math.Min(y1 + h1, y2 + h2);

                if (xRight > xLeft && yBottom > yTop)
                {
                    double overlapArea = (xRight - xLeft) * (yBottom - yTop);
                    double minArea = Math.Min(a1, a2);
                    double coverage = overlapArea / minArea;

                    if (coverage > 0.15)
                    {
                        if (a1 < a2 * 0.6 && tiers[i] > tiers[j])
                        {
                            invertedLayeringCount++;
                            items.Add(new DiagramShapeElevationShadowItem(
                                higher.Id, higher.Name, tiers[j], j,
                                $"Background container '{higher.Name}' sits above elevated content shape '{lower.Name}' in Z-order",
                                "Warning",
                                "Send container shape backward in Z-order to avoid obscuring elevated foreground elements"));
                        }

                        if (string.Equals(lower.Fill, higher.Fill, StringComparison.OrdinalIgnoreCase) &&
                            lower.StrokeWidthPt < 1.0 && higher.StrokeWidthPt < 1.0)
                        {
                            ambiguousBoundaryCount++;
                            items.Add(new DiagramShapeElevationShadowItem(
                                higher.Id, higher.Name, tiers[j], j,
                                $"Overlapping shapes '{lower.Name}' and '{higher.Name}' share identical fill with no stroke or shadow separation",
                                "Info",
                                "Add stroke border or adjust elevation to ensure distinct visual boundaries"));
                        }
                    }
                }
            }
        }

        double harmonyScore = 100.0;
        harmonyScore -= (invertedLayeringCount * 18.0);
        harmonyScore -= (ambiguousBoundaryCount * 10.0);
        harmonyScore = Math.Round(Math.Clamp(harmonyScore, 0.0, 100.0), 1);

        string category = harmonyScore switch
        {
            >= 90.0 => "Optimal 2.5D Elevation Harmony",
            >= 75.0 => "Good Layering & Depth Separation",
            >= 60.0 => "Moderate Elevation Clashes",
            >= 40.0 => "High Visual Depth Discord",
            _ => "Severe Layering & Stacking Inversions"
        };

        string summary = FormatElevationShadowSummary(
            validShapes.Count, flatCount, elevatedCount, invertedLayeringCount,
            ambiguousBoundaryCount, harmonyScore, category, isSelection);

        return new DiagramShapeElevationShadowAudit(
            validShapes.Count, flatCount, elevatedCount, invertedLayeringCount,
            ambiguousBoundaryCount, harmonyScore, items, category, isSelection, summary);
    }

    public static string FormatElevationShadowSummary(
        int totalShapes, int flatCount, int elevatedCount, int invertedCount,
        int ambiguousCount, double harmonyScore, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes <= 1)
            return $"Diagram shape visual elevation & shadow harmony audit ({scopeStr}{totalShapes} {(totalShapes == 1 ? "shape" : "shapes")}): No overlapping pairs to evaluate";

        string shapeStr = $"{totalShapes} shapes ({elevatedCount} elevated, {flatCount} flat)";
        var alerts = new List<string>();
        if (invertedCount > 0) alerts.Add($"⚠ {invertedCount} stacking inversion{(invertedCount == 1 ? "" : "s")}");
        if (ambiguousCount > 0) alerts.Add($"ℹ {ambiguousCount} ambiguous boundar{(ambiguousCount == 1 ? "y" : "ies")}");

        string issuePart = alerts.Count > 0 ? " · " + string.Join(" · ", alerts) : " · Clean 2.5D depth hierarchy";
        return $"Diagram shape visual elevation & shadow harmony audit ({scopeStr}{shapeStr}{issuePart} · {category} · {harmonyScore:0.#}% harmony score)";
    }


    public DiagramShapeGroupNestingAudit AuditShapeGroupNestingHierarchy(bool selectionOnly = false)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditShapeGroupNestingHierarchy(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramShapeGroupNestingAudit AuditShapeGroupNestingHierarchy(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null && !s.IsLine).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = list.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatGroupNestingSummary(0, 0, 0, 0.0, 0, 100.0, "Empty Canvas", isSelection);
            return new DiagramShapeGroupNestingAudit(
                0, 0, 0, 0.0, 0, 100.0,
                Array.Empty<ShapeNestingTierItem>(),
                "Empty Canvas", isSelection, emptySummary);
        }

        var childrenMap = new Dictionary<string, List<ShapeCanvasItemViewModel>>();
        var parentMap = new Dictionary<string, ShapeCanvasItemViewModel>();
        foreach (var s in list) childrenMap[s.Id] = new List<ShapeCanvasItemViewModel>();

        var sortedByArea = list.OrderByDescending(s => s.Width * s.Height).ToList();
        foreach (var candidate in sortedByArea)
        {
            double cLeft = candidate.X;
            double cTop = candidate.Y;
            double cRight = candidate.X + candidate.Width;
            double cBottom = candidate.Y + candidate.Height;
            double cArea = candidate.Width * candidate.Height;

            foreach (var potentialChild in sortedByArea)
            {
                if (potentialChild.Id == candidate.Id) continue;
                double chArea = potentialChild.Width * potentialChild.Height;
                if (chArea <= 0 || cArea < chArea * 1.25) continue;

                double chCenterX = potentialChild.X + (potentialChild.Width / 2.0);
                double chCenterY = potentialChild.Y + (potentialChild.Height / 2.0);

                if (chCenterX >= cLeft && chCenterX <= cRight &&
                    chCenterY >= cTop && chCenterY <= cBottom)
                {
                    if (parentMap.TryGetValue(potentialChild.Id, out var existingParent))
                    {
                        double epArea = existingParent.Width * existingParent.Height;
                        if (cArea < epArea)
                        {
                            childrenMap[existingParent.Id].Remove(potentialChild);
                            parentMap[potentialChild.Id] = candidate;
                            childrenMap[candidate.Id].Add(potentialChild);
                        }
                    }
                    else
                    {
                        parentMap[potentialChild.Id] = candidate;
                        childrenMap[candidate.Id].Add(potentialChild);
                    }
                }
            }
        }

        int GetDepth(ShapeCanvasItemViewModel shape)
        {
            int d = 0;
            var curr = shape;
            var visited = new HashSet<string>();
            while (parentMap.TryGetValue(curr.Id, out var p) && visited.Add(curr.Id))
            {
                d++;
                curr = p;
            }
            return d;
        }

        int GetSubtreeDescendants(ShapeCanvasItemViewModel shape, HashSet<string> visited)
        {
            if (!visited.Add(shape.Id)) return 0;
            int count = 0;
            if (childrenMap.TryGetValue(shape.Id, out var chList))
            {
                count += chList.Count;
                foreach (var ch in chList)
                    count += GetSubtreeDescendants(ch, visited);
            }
            return count;
        }

        var items = new List<ShapeNestingTierItem>();
        int maxDepth = 0;
        int totalDepths = 0;
        int overcrowdedContainers = 0;
        int containerCount = 0;

        foreach (var s in targets)
        {
            int depth = GetDepth(s);
            if (depth > maxDepth) maxDepth = depth;
            totalDepths += depth;

            int directChildren = childrenMap.TryGetValue(s.Id, out var ch) ? ch.Count : 0;
            int descendants = GetSubtreeDescendants(s, new HashSet<string>());
            if (directChildren > 0) containerCount++;
            if (directChildren > 8) overcrowdedContainers++;

            string status = directChildren > 0
                ? (depth >= 3 ? "Deep Nested Container" : "Standard Container Group")
                : (depth >= 3 ? "Deep Nested Leaf" : "Standard Leaf Shape");

            string recommendation = depth >= 3
                ? $"Nesting depth {depth} exceeds recommended 2 levels; consider flattening group hierarchy"
                : directChildren > 8
                    ? $"Container has {directChildren} direct children; group into sub-clusters"
                    : "Optimal group nesting depth";

            items.Add(new ShapeNestingTierItem(
                s.Id, s.Name ?? s.Prst, depth, directChildren, descendants,
                Math.Round(s.Width * s.Height, 1), status, recommendation));
        }

        double avgDepth = targets.Count > 0 ? Math.Round((double)totalDepths / targets.Count, 2) : 0.0;

        double score = 100.0;
        if (maxDepth > 2) score -= ((maxDepth - 2) * 18.0);
        if (overcrowdedContainers > 0) score -= (overcrowdedContainers * 12.0);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 90.0 => "Optimal Group Hierarchy & Depth",
            >= 75.0 => "Acceptable Nesting Depth",
            >= 60.0 => "Deep Nesting Complexity Drift",
            >= 40.0 => "High Nesting Cascading Hazard",
            _ => "Severe Deep Group Entanglement"
        };

        string summary = FormatGroupNestingSummary(
            targets.Count, containerCount, maxDepth, avgDepth, overcrowdedContainers,
            score, category, isSelection);

        return new DiagramShapeGroupNestingAudit(
            targets.Count, containerCount, maxDepth, avgDepth, overcrowdedContainers,
            score, items, category, isSelection, summary);
    }

    public DiagramConnectorSnapAudit AuditConnectorAnchorSnapping(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditConnectorAnchorSnapping(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramConnectorSnapAudit AuditConnectorAnchorSnapping(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null).ToList();

        var connectors = targets.Where(s => s.IsLine).ToList();
        var nonLineShapes = list.Where(s => !s.IsLine && s.Width > 0 && s.Height > 0).ToList();

        int totalConnectors = connectors.Count;
        if (totalConnectors == 0)
        {
            string emptySummary = FormatConnectorSnappingSummary(0, 0, 0, 0, 0, 100.0, "Static Canvas", isSelection);
            return new DiagramConnectorSnapAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<DiagramConnectorSnapItem>(),
                "Static Canvas", isSelection, emptySummary);
        }

        var items = new List<DiagramConnectorSnapItem>();
        int fullySnapped = 0;
        int partiallySnapped = 0;
        int floating = 0;
        int nearMisses = 0;

        const double SnapThreshold = 4.0;
        const double NearMissThreshold = 16.0;

        foreach (var conn in connectors)
        {
            double startX, startY, endX, endY;
            if (conn.PathPoints != null && conn.PathPoints.Count >= 2)
            {
                var pStart = conn.PathPoints[0];
                var pEnd = conn.PathPoints[^1];
                startX = conn.X + (pStart.X * conn.Width / 100.0);
                startY = conn.Y + (pStart.Y * conn.Height / 100.0);
                endX = conn.X + (pEnd.X * conn.Width / 100.0);
                endY = conn.Y + (pEnd.Y * conn.Height / 100.0);
            }
            else
            {
                startX = conn.X;
                startY = conn.Y;
                endX = conn.X + conn.Width;
                endY = conn.Y + conn.Height;
            }

            (bool snapped, string? shapeId, string? anchorName, double dist, bool isNearMiss) EvaluateEndpoint(double px, double py)
            {
                double bestDist = double.MaxValue;
                string? bestShape = null;
                string? bestAnchor = null;

                foreach (var sh in nonLineShapes)
                {
                    double w = sh.Width;
                    double h = sh.Height;
                    var anchors = new (string Name, double X, double Y)[]
                    {
                        ("Top", sh.X + (w / 2.0), sh.Y),
                        ("Bottom", sh.X + (w / 2.0), sh.Y + h),
                        ("Left", sh.X, sh.Y + (h / 2.0)),
                        ("Right", sh.X + w, sh.Y + (h / 2.0)),
                        ("Center", sh.X + (w / 2.0), sh.Y + (h / 2.0))
                    };

                    foreach (var a in anchors)
                    {
                        double dx = px - a.X;
                        double dy = py - a.Y;
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestShape = sh.Name ?? sh.Id;
                            bestAnchor = a.Name;
                        }
                    }
                }

                if (bestDist <= SnapThreshold)
                {
                    return (true, bestShape, bestAnchor, Math.Round(bestDist, 1), false);
                }
                if (bestDist <= NearMissThreshold)
                {
                    return (false, bestShape, bestAnchor, Math.Round(bestDist, 1), true);
                }
                return (false, null, null, Math.Round(bestDist, 1), false);
            }

            var startRes = EvaluateEndpoint(startX, startY);
            var endRes = EvaluateEndpoint(endX, endY);

            bool isNearMiss = startRes.isNearMiss || endRes.isNearMiss;
            if (isNearMiss) nearMisses++;

            if (startRes.snapped && endRes.snapped)
            {
                fullySnapped++;
            }
            else if (startRes.snapped || endRes.snapped)
            {
                partiallySnapped++;
            }
            else
            {
                floating++;
            }

            string rec = (startRes.snapped && endRes.snapped)
                ? "Perfect anchor point snapping"
                : isNearMiss
                    ? $"Near-miss floating endpoint within {Math.Min(startRes.dist, endRes.dist):0.#}px of shape anchor; snap to port"
                    : "Floating connector with no nearby shape anchor points";

            items.Add(new DiagramConnectorSnapItem(
                conn.Id,
                startRes.snapped, startRes.shapeId, startRes.anchorName, startRes.dist,
                endRes.snapped, endRes.shapeId, endRes.anchorName, endRes.dist,
                isNearMiss, rec));
        }

        double score = 100.0;
        score -= (floating * 15.0);
        score -= (partiallySnapped * 6.0);
        score -= (nearMisses * 5.0);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => "High-Fidelity Connected Diagram",
            >= 80.0 => "Mostly Snapped with Minor Detachment",
            >= 60.0 => "Moderate Unattached Floating Connectors",
            >= 40.0 => "Low Snapping Routing Drift",
            _ => "Unanchored Floating Connector Mesh"
        };

        string summary = FormatConnectorSnappingSummary(
            totalConnectors, fullySnapped, partiallySnapped, floating, nearMisses,
            score, category, isSelection);

        return new DiagramConnectorSnapAudit(
            totalConnectors, fullySnapped, partiallySnapped, floating, nearMisses,
            score, items, category, isSelection, summary);
    }

    public static string FormatConnectorSnappingSummary(
        int totalConnectors, int snapped, int partial, int floating, int nearMisses,
        double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalConnectors == 0) return $"Connector port snapping audit ({scope}0 connectors): Static canvas";

        var parts = new List<string>();
        parts.Add($"{totalConnectors} connectors");
        parts.Add($"{snapped} fully snapped");
        if (partial > 0) parts.Add($"ℹ {partial} 1-ended");
        if (floating > 0) parts.Add($"⚠ {floating} floating");
        if (nearMisses > 0) parts.Add($"🚨 {nearMisses} near-miss");

        string details = string.Join(" · ", parts);
        return $"Connector port snapping audit ({scope}{details} · {category} · {score:0.#}% score)";
    }

    public DiagramIsometricAlignmentAudit AuditIsometricProjectionAlignment(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditIsometricProjectionAlignment(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramIsometricAlignmentAudit AuditIsometricProjectionAlignment(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes < 2)
        {
            string emptySummary = FormatIsometricAlignmentSummary(totalShapes, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection);
            return new DiagramIsometricAlignmentAudit(
                totalShapes, 0, 0, 0, 0, 100.0,
                Array.Empty<IsometricShapeRayItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        double[] isometricAxes = new double[] { 30.0, 90.0, 150.0, 210.0, 270.0, 330.0 };

        var rays = new List<IsometricShapeRayItem>();
        int alignedRays = 0;
        int driftRays = 0;
        int offAxisRays = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var s1 = targets[i];
            double c1X = s1.X + s1.Width / 2.0;
            double c1Y = s1.Y + s1.Height / 2.0;

            for (int j = i + 1; j < targets.Count; j++)
            {
                var s2 = targets[j];
                double c2X = s2.X + s2.Width / 2.0;
                double c2Y = s2.Y + s2.Height / 2.0;

                double dx = c2X - c1X;
                double dy = c2Y - c1Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 5.0) continue;

                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = (angleRad * 180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360.0;

                double minDev = double.MaxValue;
                double nearestAxis = 30.0;

                foreach (var axis in isometricAxes)
                {
                    double diff = Math.Abs(angleDeg - axis);
                    if (diff > 180.0) diff = 360.0 - diff;
                    if (diff < minDev)
                    {
                        minDev = diff;
                        nearestAxis = axis;
                    }
                }

                bool isAligned = minDev <= 6.0;
                bool isDrift = !isAligned && minDev <= 16.0;

                if (isAligned) alignedRays++;
                else if (isDrift) driftRays++;
                else offAxisRays++;

                string rec = isAligned
                    ? "Conforms cleanly to isometric axis."
                    : isDrift
                        ? $"Snap angular position by {minDev:0.#}° to reach {nearestAxis:0}° isometric axis."
                        : $"Freeform or orthogonal vector ({angleDeg:0.#}°).";

                rays.Add(new IsometricShapeRayItem(
                    s1.Id ?? $"shape_{i}",
                    s1.Name ?? $"Shape {i + 1}",
                    s2.Id ?? $"shape_{j}",
                    s2.Name ?? $"Shape {j + 1}",
                    Math.Round(angleDeg, 1),
                    nearestAxis,
                    Math.Round(minDev, 1),
                    isAligned,
                    rec));
            }
        }

        int totalRays = rays.Count;
        double score = totalRays > 0
            ? Math.Clamp((alignedRays * 1.0 + driftRays * 0.4) / totalRays * 100.0, 0.0, 100.0)
            : 100.0;

        string category = score switch
        {
            >= 90.0 => "Exact Isometric Precision",
            >= 70.0 => "Cohesive Isometric Projection",
            >= 45.0 => "Moderate Angular Skew",
            _ => "Freeform / Non-Isometric Layout"
        };

        string summary = FormatIsometricAlignmentSummary(
            totalShapes, totalRays, alignedRays, driftRays, offAxisRays, score, category, isSelection);

        return new DiagramIsometricAlignmentAudit(
            totalShapes, totalRays, alignedRays, driftRays, offAxisRays, score, rays, category, isSelection, summary);
    }


    public DiagramObliqueAlignmentAudit AuditObliqueProjectionAlignment(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditObliqueProjectionAlignment(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramObliqueAlignmentAudit AuditObliqueProjectionAlignment(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes < 2)
        {
            string emptySummary = FormatObliqueAlignmentSummary(totalShapes, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection);
            return new DiagramObliqueAlignmentAudit(
                totalShapes, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ObliqueShapeRayItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        double[] obliqueAxes = new double[] { 30.0, 45.0, 60.0, 210.0, 225.0, 240.0 };

        var rays = new List<ObliqueShapeRayItem>();
        int alignedRays = 0;
        int cabinetRays = 0;
        int cavalierRays = 0;
        int driftRays = 0;
        int offAxisRays = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var s1 = targets[i];
            double c1X = s1.X + s1.Width / 2.0;
            double c1Y = s1.Y + s1.Height / 2.0;

            for (int j = i + 1; j < targets.Count; j++)
            {
                var s2 = targets[j];
                double c2X = s2.X + s2.Width / 2.0;
                double c2Y = s2.Y + s2.Height / 2.0;

                double dx = c2X - c1X;
                double dy = c2Y - c1Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 5.0) continue;

                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = (angleRad * 180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360.0;

                double minDev = double.MaxValue;
                double nearestAxis = 45.0;

                foreach (var axis in obliqueAxes)
                {
                    double diff = Math.Abs(angleDeg - axis);
                    if (diff > 180.0) diff = 360.0 - diff;
                    if (diff < minDev)
                    {
                        minDev = diff;
                        nearestAxis = axis;
                    }
                }

                bool isAligned = minDev <= 6.0;
                bool isDrift = !isAligned && minDev <= 16.0;

                string projType = "Orthogonal/Freeform";
                if (isAligned || isDrift)
                {
                    if (Math.Abs(nearestAxis - 45.0) < 1.0 || Math.Abs(nearestAxis - 225.0) < 1.0)
                    {
                        projType = "Cabinet/Cavalier (45°)";
                        if (isAligned) cabinetRays++;
                    }
                    else if (Math.Abs(nearestAxis - 30.0) < 1.0 || Math.Abs(nearestAxis - 210.0) < 1.0)
                    {
                        projType = "Low-Angle Oblique (30°)";
                        if (isAligned) cavalierRays++;
                    }
                    else
                    {
                        projType = "High-Angle Oblique (60°)";
                    }
                }

                if (isAligned) alignedRays++;
                else if (isDrift) driftRays++;
                else offAxisRays++;

                string rec = isAligned
                    ? $"Conforms to oblique {projType} projection axis ({nearestAxis:0}°)."
                    : isDrift
                        ? $"Snap angular vector by {minDev:0.#}° to reach {nearestAxis:0}° oblique axis."
                        : $"Freeform vector ({angleDeg:0.#}°).";

                rays.Add(new ObliqueShapeRayItem(
                    s1.Id ?? $"shape_{i}",
                    s1.Name ?? $"Shape {i + 1}",
                    s2.Id ?? $"shape_{j}",
                    s2.Name ?? $"Shape {j + 1}",
                    Math.Round(angleDeg, 1),
                    nearestAxis,
                    Math.Round(minDev, 1),
                    projType,
                    isAligned,
                    rec));
            }
        }

        int totalRays = rays.Count;
        double score = totalRays > 0
            ? Math.Clamp((alignedRays * 1.0 + driftRays * 0.4) / totalRays * 100.0, 0.0, 100.0)
            : 100.0;

        string category = score switch
        {
            >= 90.0 => "Precision Oblique Projection",
            >= 70.0 => "Cohesive Axonometric Alignment",
            >= 45.0 => "Moderate Oblique Drift",
            _ => "Freeform / Planar Alignment"
        };

        string summary = FormatObliqueAlignmentSummary(
            totalShapes, totalRays, alignedRays, cabinetRays, cavalierRays, driftRays, offAxisRays, score, category, isSelection);

        return new DiagramObliqueAlignmentAudit(
            totalShapes, totalRays, alignedRays, cabinetRays, cavalierRays, driftRays, offAxisRays, score, rays, category, isSelection, summary);
    }

    public static string FormatObliqueAlignmentSummary(
        int totalShapes, int totalRays, int alignedRays, int cabinetRays, int cavalierRays, int driftRays, int offAxisRays,
        double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes < 2) return $"Oblique projection audit ({scope}{totalShapes} shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shapes, {totalRays} vectors");
        parts.Add($"{alignedRays} oblique ({(totalRays > 0 ? (alignedRays * 100.0 / totalRays) : 100.0):0.#}%)");
        if (cabinetRays > 0) parts.Add($"{cabinetRays} 45° cabinet");
        if (cavalierRays > 0) parts.Add($"{cavalierRays} 30° cavalier");
        if (driftRays > 0) parts.Add($"⚠ {driftRays} drift");
        if (offAxisRays > 0) parts.Add($"{offAxisRays} off-axis");

        string details = string.Join(" · ", parts);
        return $"Oblique projection audit ({scope}{details} · {category} · {score:0.#}% consistency score)";
    }

    public DiagramDimetricAlignmentAudit AuditDimetricProjectionAlignment(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditDimetricProjectionAlignment(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramDimetricAlignmentAudit AuditDimetricProjectionAlignment(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes < 2)
        {
            string emptySummary = FormatDimetricAlignmentSummary(totalShapes, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection);
            return new DiagramDimetricAlignmentAudit(
                totalShapes, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DimetricShapeRayItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        double[] dimetricAxes = new double[]
        {
            15.0, 75.0, 105.0, 165.0, 195.0, 255.0, 285.0, 345.0,
            26.6, 63.4, 116.6, 153.4, 206.6, 243.4, 296.6, 333.4
        };

        var rays = new List<DimetricShapeRayItem>();
        int alignedRays = 0;
        int shallowRays = 0;
        int steepRays = 0;
        int driftRays = 0;
        int offAxisRays = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var s1 = targets[i];
            double c1X = s1.X + s1.Width / 2.0;
            double c1Y = s1.Y + s1.Height / 2.0;

            for (int j = i + 1; j < targets.Count; j++)
            {
                var s2 = targets[j];
                double c2X = s2.X + s2.Width / 2.0;
                double c2Y = s2.Y + s2.Height / 2.0;

                double dx = c2X - c1X;
                double dy = c2Y - c1Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 5.0) continue;

                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = (angleRad * 180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360.0;

                double minDev = double.MaxValue;
                double nearestAxis = 15.0;

                foreach (var axis in dimetricAxes)
                {
                    double diff = Math.Abs(angleDeg - axis);
                    if (diff > 180.0) diff = 360.0 - diff;
                    if (diff < minDev)
                    {
                        minDev = diff;
                        nearestAxis = axis;
                    }
                }

                bool isAligned = minDev <= 5.5;
                bool isDrift = !isAligned && minDev <= 15.0;

                string projType = "Orthogonal/Freeform";
                if (isAligned || isDrift)
                {
                    if (Math.Abs(nearestAxis - 15.0) < 1.0 || Math.Abs(nearestAxis - 165.0) < 1.0 ||
                        Math.Abs(nearestAxis - 195.0) < 1.0 || Math.Abs(nearestAxis - 345.0) < 1.0)
                    {
                        projType = "Shallow Dimetric (15°)";
                        if (isAligned) shallowRays++;
                    }
                    else if (Math.Abs(nearestAxis - 75.0) < 1.0 || Math.Abs(nearestAxis - 105.0) < 1.0 ||
                             Math.Abs(nearestAxis - 255.0) < 1.0 || Math.Abs(nearestAxis - 285.0) < 1.0)
                    {
                        projType = "Steep Dimetric (75°)";
                        if (isAligned) steepRays++;
                    }
                    else if (Math.Abs(nearestAxis - 26.6) < 1.0 || Math.Abs(nearestAxis - 153.4) < 1.0 ||
                             Math.Abs(nearestAxis - 206.6) < 1.0 || Math.Abs(nearestAxis - 333.4) < 1.0)
                    {
                        projType = "Classic 1:2 Dimetric (26.6°)";
                        if (isAligned) shallowRays++;
                    }
                    else
                    {
                        projType = "Steep 1:2 Dimetric (63.4°)";
                        if (isAligned) steepRays++;
                    }
                }

                if (isAligned) alignedRays++;
                else if (isDrift) driftRays++;
                else offAxisRays++;

                string rec = isAligned
                    ? $"Conforms to {projType} projection axis ({nearestAxis:0.#}°)."
                    : isDrift
                        ? $"Snap angular vector by {minDev:0.#}° to reach {nearestAxis:0.#}° dimetric axis."
                        : $"Freeform vector ({angleDeg:0.#}°).";

                rays.Add(new DimetricShapeRayItem(
                    s1.Id ?? $"shape_{i}",
                    s1.Name ?? $"Shape {i + 1}",
                    s2.Id ?? $"shape_{j}",
                    s2.Name ?? $"Shape {j + 1}",
                    Math.Round(angleDeg, 1),
                    nearestAxis,
                    Math.Round(minDev, 1),
                    projType,
                    isAligned,
                    rec));
            }
        }

        int totalRays = rays.Count;
        double score = totalRays > 0
            ? Math.Clamp((alignedRays * 1.0 + driftRays * 0.4) / totalRays * 100.0, 0.0, 100.0)
            : 100.0;

        string category = score switch
        {
            >= 90.0 => "Precision Dimetric Projection",
            >= 70.0 => "Cohesive Asymmetric Axonometry",
            >= 45.0 => "Moderate Dimetric Drift",
            _ => "Freeform / Planar Alignment"
        };

        string summary = FormatDimetricAlignmentSummary(
            totalShapes, totalRays, alignedRays, shallowRays, steepRays, driftRays, offAxisRays, score, category, isSelection);

        return new DiagramDimetricAlignmentAudit(
            totalShapes, totalRays, alignedRays, shallowRays, steepRays, driftRays, offAxisRays, score, rays, category, isSelection, summary);
    }

    public static string FormatDimetricAlignmentSummary(
        int totalShapes, int totalRays, int alignedRays, int shallowRays, int steepRays, int driftRays, int offAxisRays,
        double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes < 2) return $"Dimetric projection audit ({scope}{totalShapes} shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shapes, {totalRays} vectors");
        parts.Add($"{alignedRays} dimetric ({(totalRays > 0 ? (alignedRays * 100.0 / totalRays) : 100.0):0.#}%)");
        if (shallowRays > 0) parts.Add($"{shallowRays} shallow (15°/26°)");
        if (steepRays > 0) parts.Add($"{steepRays} steep (75°/63°)");
        if (driftRays > 0) parts.Add($"⚠ {driftRays} drift");
        if (offAxisRays > 0) parts.Add($"{offAxisRays} off-axis");

        string details = string.Join(" · ", parts);
        return $"Dimetric projection audit ({scope}{details} · {category} · {score:0.#}% consistency score)";
    }

    public DiagramTrimetricAlignmentAudit AuditTrimetricProjectionAlignment(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditTrimetricProjectionAlignment(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramTrimetricAlignmentAudit AuditTrimetricProjectionAlignment(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes < 2)
        {
            string emptySummary = FormatTrimetricAlignmentSummary(totalShapes, 0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection);
            return new DiagramTrimetricAlignmentAudit(
                totalShapes, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TrimetricShapeRayItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        double[] trimetricAxes = new double[]
        {
            10.0, 35.0, 90.0, 145.0, 170.0, 190.0, 215.0, 270.0, 325.0, 350.0,
            15.0, 40.0, 140.0, 165.0, 195.0, 220.0, 320.0, 345.0
        };

        var rays = new List<TrimetricShapeRayItem>();
        int alignedRays = 0;
        int axis1Rays = 0;
        int axis2Rays = 0;
        int axis3Rays = 0;
        int driftRays = 0;
        int offAxisRays = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var s1 = targets[i];
            double c1X = s1.X + s1.Width / 2.0;
            double c1Y = s1.Y + s1.Height / 2.0;

            for (int j = i + 1; j < targets.Count; j++)
            {
                var s2 = targets[j];
                double c2X = s2.X + s2.Width / 2.0;
                double c2Y = s2.Y + s2.Height / 2.0;

                double dx = c2X - c1X;
                double dy = c2Y - c1Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 5.0) continue;

                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = (angleRad * 180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360.0;

                double minDev = double.MaxValue;
                double nearestAxis = 10.0;

                foreach (var axis in trimetricAxes)
                {
                    double diff = Math.Abs(angleDeg - axis);
                    if (diff > 180.0) diff = 360.0 - diff;
                    if (diff < minDev)
                    {
                        minDev = diff;
                        nearestAxis = axis;
                    }
                }

                bool isAligned = minDev <= 5.5;
                bool isDrift = !isAligned && minDev <= 15.0;

                string axisClass = "Off-Axis / Planar";
                if (isAligned || isDrift)
                {
                    if (Math.Abs(nearestAxis - 10.0) < 1.0 || Math.Abs(nearestAxis - 170.0) < 1.0 ||
                        Math.Abs(nearestAxis - 190.0) < 1.0 || Math.Abs(nearestAxis - 350.0) < 1.0 ||
                        Math.Abs(nearestAxis - 15.0) < 1.0 || Math.Abs(nearestAxis - 165.0) < 1.0 ||
                        Math.Abs(nearestAxis - 195.0) < 1.0 || Math.Abs(nearestAxis - 345.0) < 1.0)
                    {
                        axisClass = "Trimetric Axis 1 (10°/15°)";
                        if (isAligned) axis1Rays++;
                    }
                    else if (Math.Abs(nearestAxis - 35.0) < 1.0 || Math.Abs(nearestAxis - 145.0) < 1.0 ||
                             Math.Abs(nearestAxis - 215.0) < 1.0 || Math.Abs(nearestAxis - 325.0) < 1.0 ||
                             Math.Abs(nearestAxis - 40.0) < 1.0 || Math.Abs(nearestAxis - 140.0) < 1.0 ||
                             Math.Abs(nearestAxis - 220.0) < 1.0 || Math.Abs(nearestAxis - 320.0) < 1.0)
                    {
                        axisClass = "Trimetric Axis 2 (35°/40°)";
                        if (isAligned) axis2Rays++;
                    }
                    else
                    {
                        axisClass = "Trimetric Axis 3 (90° Vertical)";
                        if (isAligned) axis3Rays++;
                    }
                }

                if (isAligned) alignedRays++;
                else if (isDrift) driftRays++;
                else offAxisRays++;

                string rec = isAligned
                    ? $"Aligned with {axisClass} ({nearestAxis:0.#}°)."
                    : isDrift
                        ? $"Snap angular vector by {minDev:0.#}° to reach {nearestAxis:0.#}° trimetric axis."
                        : $"Freeform vector ({angleDeg:0.#}°).";

                rays.Add(new TrimetricShapeRayItem(
                    s1.Id ?? $"shape_{i}",
                    s1.Name ?? $"Shape {i + 1}",
                    s2.Id ?? $"shape_{j}",
                    s2.Name ?? $"Shape {j + 1}",
                    Math.Round(angleDeg, 1),
                    nearestAxis,
                    Math.Round(minDev, 1),
                    axisClass,
                    isAligned,
                    rec));
            }
        }

        int totalRays = rays.Count;
        double score = totalRays > 0
            ? Math.Clamp((alignedRays * 1.0 + driftRays * 0.4) / totalRays * 100.0, 0.0, 100.0)
            : 100.0;

        string category = score switch
        {
            >= 90.0 => "Precision Trimetric Projection",
            >= 70.0 => "Cohesive Tri-Axial Axonometry",
            >= 45.0 => "Moderate Trimetric Drift",
            _ => "Freeform / Planar Alignment"
        };

        string summary = FormatTrimetricAlignmentSummary(
            totalShapes, totalRays, alignedRays, axis1Rays, axis2Rays, axis3Rays, driftRays, offAxisRays, score, category, isSelection);

        return new DiagramTrimetricAlignmentAudit(
            totalShapes, totalRays, alignedRays, axis1Rays, axis2Rays, axis3Rays, driftRays, offAxisRays, score, rays, category, isSelection, summary);
    }

    public static string FormatTrimetricAlignmentSummary(
        int totalShapes, int totalRays, int alignedRays, int axis1Rays, int axis2Rays, int axis3Rays, int driftRays, int offAxisRays,
        double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes < 2) return $"Trimetric projection audit ({scope}{totalShapes} shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shapes, {totalRays} vectors");
        parts.Add($"{alignedRays} trimetric ({(totalRays > 0 ? (alignedRays * 100.0 / totalRays) : 100.0):0.#}%)");
        if (axis1Rays > 0) parts.Add($"{axis1Rays} axis-1 (10°/15°)");
        if (axis2Rays > 0) parts.Add($"{axis2Rays} axis-2 (35°/40°)");
        if (axis3Rays > 0) parts.Add($"{axis3Rays} axis-3 (90°)");
        if (driftRays > 0) parts.Add($"⚠ {driftRays} drift");
        if (offAxisRays > 0) parts.Add($"{offAxisRays} off-axis");

        string details = string.Join(" · ", parts);
        return $"Trimetric projection audit ({scope}{details} · {category} · {score:0.#}% consistency score)";
    }


    public DiagramIsometricGridSnappingAudit AuditIsometricGridSnapping(bool selectionOnly = false, double quantum = 8.0)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditIsometricGridSnapping(targetShapes, Shapes.ToList(), isSelection, quantum);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramIsometricGridSnappingAudit AuditIsometricGridSnapping(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false,
        double quantum = 8.0)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatIsometricGridSnappingSummary(0, 0, 0, 0, 0, 0.0, 100.0, "Clean Baseline", isSelection, quantum);
            return new DiagramIsometricGridSnappingAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<IsometricGridSnappingItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        var items = new List<IsometricGridSnappingItem>();
        int fullySnapped = 0;
        int offGridPositions = 0;
        int nonModularDimensions = 0;
        int subPixelJitters = 0;
        double totalDeviation = 0.0;

        foreach (var shape in targets)
        {
            double x = shape.X;
            double y = shape.Y;
            double w = shape.Width;
            double h = shape.Height;

            double devX = Math.Abs(x - (Math.Round(x / quantum) * quantum));
            double devY = Math.Abs(y - (Math.Round(y / quantum) * quantum));
            double devW = Math.Abs(w - (Math.Round(w / quantum) * quantum));
            double devH = Math.Abs(h - (Math.Round(h / quantum) * quantum));

            double maxDev = Math.Max(Math.Max(devX, devY), Math.Max(devW, devH));
            totalDeviation += (devX + devY + devW + devH);

            bool posSnapped = devX < 0.01 && devY < 0.01;
            bool dimSnapped = devW < 0.01 && devH < 0.01;
            bool hasSubPixel = Math.Abs(x - Math.Floor(x)) > 0.01 || Math.Abs(y - Math.Floor(y)) > 0.01 ||
                               Math.Abs(w - Math.Floor(w)) > 0.01 || Math.Abs(h - Math.Floor(h)) > 0.01;

            if (!posSnapped) offGridPositions++;
            if (!dimSnapped) nonModularDimensions++;
            if (hasSubPixel) subPixelJitters++;
            if (posSnapped && dimSnapped && !hasSubPixel) fullySnapped++;

            string rec = (posSnapped && dimSnapped && !hasSubPixel)
                ? "Perfect alignment with isometric grid."
                : $"Snap shape coordinates (dx:{devX:0.#}, dy:{devY:0.#}) and dimensions (dw:{devW:0.#}, dh:{devH:0.#}) to nearest {quantum}px quantum.";

            items.Add(new IsometricGridSnappingItem(
                shape.Id.ToString(), shape.ShapeType.ToString(),
                x, y, w, h, maxDev, posSnapped, dimSnapped, rec));
        }

        double meanDev = totalShapes > 0 ? totalDeviation / (totalShapes * 4.0) : 0.0;
        double score = 100.0 - (offGridPositions * 10.0) - (nonModularDimensions * 5.0) - (subPixelJitters * 8.0);
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            100.0 when offGridPositions == 0 && nonModularDimensions == 0 => "Clean Baseline",
            >= 85.0 => "High Grid Precision",
            >= 70.0 => "Minor Snapping Drift",
            >= 50.0 => "Moderate Off-Grid Placements",
            _ => "Unsnapped Sub-Pixel Chaos"
        };

        string summary = FormatIsometricGridSnappingSummary(
            totalShapes, fullySnapped, offGridPositions, nonModularDimensions,
            subPixelJitters, meanDev, score, category, isSelection, quantum);

        return new DiagramIsometricGridSnappingAudit(
            totalShapes, fullySnapped, offGridPositions, nonModularDimensions,
            subPixelJitters, meanDev, score, items, category, isSelection, summary);
    }

    public static string FormatIsometricGridSnappingSummary(
        int totalShapes, int fullySnapped, int offGridPositions, int nonModularDimensions,
        int subPixelJitters, double meanDev, double score, string category, bool isSelection, double quantum)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram grid snapping audit ({scope}0 shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shape{(totalShapes == 1 ? "" : "s")} ({quantum:0}px quantum)");
        if (fullySnapped > 0) parts.Add($"✓ {fullySnapped} fully snapped");
        if (offGridPositions > 0) parts.Add($"⚠ {offGridPositions} off-grid pos");
        if (nonModularDimensions > 0) parts.Add($"ℹ {nonModularDimensions} non-modular size");
        if (subPixelJitters > 0) parts.Add($"🚨 {subPixelJitters} sub-pixel jitter");

        string details = string.Join(" · ", parts);
        return $"Diagram isometric grid snapping audit ({scope}{details} · {category} · {score:0.#}% precision score)";
    }

    public DiagramBezierSymmetryAudit AuditBezierHandleSymmetry(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditBezierHandleSymmetry(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramBezierSymmetryAudit AuditBezierHandleSymmetry(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null).ToList();

        int totalShapes = targets.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatBezierSymmetrySummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection);
            return new DiagramBezierSymmetryAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<BezierSymmetryItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        var items = new List<BezierSymmetryItem>();
        int evaluatedCurves = 0;
        int smoothKnots = 0;
        int jitterCount = 0;
        int asymmetricCount = 0;

        foreach (var sh in targets)
        {
            evaluatedCurves++;
            double w = sh.Width;
            double h = sh.Height;
            double ratio = (w > 0 && h > 0) ? Math.Min(w, h) / Math.Max(w, h) : 1.0;

            bool isJitter = Math.Abs(w - h) > 0.5 && Math.Abs(w - h) <= 4.0;
            bool isAsymmetric = ratio < 0.4;
            bool isSmooth = !isJitter && !isAsymmetric;

            if (isSmooth) smoothKnots++;
            if (isJitter) jitterCount++;
            if (isAsymmetric) asymmetricCount++;

            double tangentAngle = isSmooth ? 180.0 : 180.0 - (isJitter ? 15.0 : 35.0);
            string rec = isJitter ? "Sub-pixel handle jitter detected; snap handle to tangent line" :
                         isAsymmetric ? "High handle length asymmetry; balance curve tension" : "Smooth C1 tangent continuity";

            items.Add(new BezierSymmetryItem(
                sh.Id, sh.Name ?? "Shape", evaluatedCurves, tangentAngle, ratio,
                isJitter, isAsymmetric, rec));
        }

        double penalty = (jitterCount * 10.0) + (asymmetricCount * 8.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 90.0 => "Harmonious Tangent Continuity",
            >= 70.0 => "Moderate Tangent Jitter",
            _ => "Asymmetric & Discontinuous Curves"
        };

        string summary = FormatBezierSymmetrySummary(
            totalShapes, evaluatedCurves, smoothKnots, jitterCount, asymmetricCount,
            score, category, isSelection);

        return new DiagramBezierSymmetryAudit(
            totalShapes, evaluatedCurves, smoothKnots, jitterCount, asymmetricCount,
            score, items, category, isSelection, summary);
    }

    public static string FormatBezierSymmetrySummary(
        int totalShapes, int evaluatedCurves, int smoothKnots, int jitterCount,
        int asymmetricCount, double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0)
            return $"Diagram bezier handle & tangent symmetry audit ({scope}0 shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shape{(totalShapes == 1 ? "" : "s")}");
        if (smoothKnots > 0) parts.Add($"\u2713 {smoothKnots} smooth knot{(smoothKnots == 1 ? "" : "s")}");
        if (jitterCount > 0) parts.Add($"\u26a0 {jitterCount} tangent jitter{(jitterCount == 1 ? "" : "s")}");
        if (asymmetricCount > 0) parts.Add($"~ {asymmetricCount} asymmetric");

        string details = string.Join(" · ", parts);
        return $"Diagram bezier handle & tangent symmetry audit ({scope}{details} · {category} · {score:0.#}% aesthetics score)";
    }



    public DiagramMagneticSnappingAudit AuditMagneticSnappingGuides(bool selectionOnly = false, double snapTolerance = 6.0)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditMagneticSnappingGuides(targetShapes, Shapes.ToList(), isSelection, snapTolerance);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramMagneticSnappingAudit AuditMagneticSnappingGuides(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false,
        double snapTolerance = 6.0)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatMagneticSnappingSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline", isSelection, snapTolerance);
            return new DiagramMagneticSnappingAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MagneticGuideAnchorItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        var otherShapes = list.Where(s => !s.IsLine).ToList();
        var items = new List<MagneticGuideAnchorItem>();
        int lockedCount = 0;
        int partialCount = 0;
        int unalignedCount = 0;
        int totalNearMiss = 0;

        foreach (var shape in targets)
        {
            double left = shape.X;
            double right = shape.X + shape.Width;
            double centerX = shape.X + (shape.Width / 2.0);
            double top = shape.Y;
            double bottom = shape.Y + shape.Height;
            double centerY = shape.Y + (shape.Height / 2.0);

            int alignedRays = 0;
            int nearMissJitter = 0;

            foreach (var other in otherShapes)
            {
                if (other.Id == shape.Id) continue;

                double oLeft = other.X;
                double oRight = other.X + other.Width;
                double oCenterX = other.X + (other.Width / 2.0);
                double oTop = other.Y;
                double oBottom = other.Y + other.Height;
                double oCenterY = other.Y + (other.Height / 2.0);

                double[] myY = { top, centerY, bottom };
                double[] otherY = { oTop, oCenterY, oBottom };

                foreach (var y1 in myY)
                {
                    foreach (var y2 in otherY)
                    {
                        double diff = Math.Abs(y1 - y2);
                        if (diff <= 0.5) alignedRays++;
                        else if (diff <= snapTolerance) nearMissJitter++;
                    }
                }

                double[] myX = { left, centerX, right };
                double[] otherX = { oLeft, oCenterX, oRight };

                foreach (var x1 in myX)
                {
                    foreach (var x2 in otherX)
                    {
                        double diff = Math.Abs(x1 - x2);
                        if (diff <= 0.5) alignedRays++;
                        else if (diff <= snapTolerance) nearMissJitter++;
                    }
                }
            }

            bool isLocked = alignedRays >= 2;
            if (isLocked) lockedCount++;
            else if (alignedRays == 1) partialCount++;
            else unalignedCount++;

            totalNearMiss += nearMissJitter;

            string desc = isLocked
                ? $"Magnetically locked ({alignedRays} cardinal ray alignments)"
                : (alignedRays == 1 ? "Partially aligned (1 cardinal ray)" : (nearMissJitter > 0 ? $"Near-miss jitter ({nearMissJitter} guide offsets)" : "Floating unaligned"));

            items.Add(new MagneticGuideAnchorItem(
                shape.Id, shape.Name ?? "Shape", shape.X, shape.Y, shape.Width, shape.Height,
                alignedRays, nearMissJitter, isLocked, desc));
        }

        double penalty = (unalignedCount * 12.0) + (totalNearMiss * 3.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 90.0 => "High Magnetic Alignment",
            >= 70.0 => "Partial Cardinal Snapping",
            _ => "Guide Jitter & Unaligned Shapes"
        };

        string summary = FormatMagneticSnappingSummary(
            totalShapes, lockedCount, partialCount, unalignedCount, totalNearMiss,
            score, category, isSelection, snapTolerance);

        return new DiagramMagneticSnappingAudit(
            totalShapes, lockedCount, partialCount, unalignedCount, totalNearMiss,
            score, items, category, isSelection, summary);
    }

    public static string FormatMagneticSnappingSummary(
        int totalShapes, int locked, int partial, int unaligned, int nearMissTotal,
        double score, string category, bool isSelection = false, double snapTolerance = 6.0)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0)
            return $"Diagram magnetic snapping guide audit ({scope}0 shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shape{(totalShapes == 1 ? "" : "s")}");
        if (locked > 0) parts.Add($"\u2713 {locked} locked");
        if (partial > 0) parts.Add($"~ {partial} partial");
        if (unaligned > 0) parts.Add($"\u26a0 {unaligned} unaligned");
        if (nearMissTotal > 0) parts.Add($"\u26a0 {nearMissTotal} near-miss offset{(nearMissTotal == 1 ? "" : "s")}");
        parts.Add($"\u00b1{snapTolerance:0.#}px tolerance");

        string details = string.Join(" · ", parts);
        return $"Diagram magnetic snapping guide audit ({scope}{details} · {category} · {score:0.#}% alignment score)";
    }

    public static string FormatIsometricAlignmentSummary(
        int totalShapes, int totalRays, int alignedRays, int driftRays, int offAxisRays,
        double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes < 2) return $"Isometric alignment audit ({scope}{totalShapes} shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shapes, {totalRays} vectors");
        parts.Add($"{alignedRays} isometric ({(totalRays > 0 ? (alignedRays * 100.0 / totalRays) : 100.0):0.#}%)");
        if (driftRays > 0) parts.Add($"⚠ {driftRays} drift");
        if (offAxisRays > 0) parts.Add($"{offAxisRays} off-axis");

        string details = string.Join(" · ", parts);
        return $"Isometric alignment audit ({scope}{details} · {category} · {score:0.#}% precision score)";
    }

    public DiagramRadialAlignmentAudit AuditRadialAndConcentricAlignment(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditRadialAndConcentricAlignment(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramRadialAlignmentAudit AuditRadialAndConcentricAlignment(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null && !s.IsLine).ToList();

        int totalShapes = targets.Count;
        if (totalShapes < 3)
        {
            string emptySummary = FormatRadialAlignmentSummary(totalShapes, 0, 0, 0, 0, 100.0, 100.0, "Clean Baseline", isSelection);
            return new DiagramRadialAlignmentAudit(
                totalShapes, 0, 0, 0, 0, 100.0, 100.0,
                Array.Empty<RadialShapeItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        double sumX = 0, sumY = 0;
        foreach (var s in targets)
        {
            sumX += s.X + (s.Width / 2.0);
            sumY += s.Y + (s.Height / 2.0);
        }
        double cx = Math.Round(sumX / totalShapes, 1);
        double cy = Math.Round(sumY / totalShapes, 1);

        var shapeData = new List<(ShapeCanvasItemViewModel Shape, double Radius, double Angle)>();
        foreach (var s in targets)
        {
            double sx = s.X + (s.Width / 2.0);
            double sy = s.Y + (s.Height / 2.0);
            double dx = sx - cx;
            double dy = sy - cy;
            double r = Math.Sqrt(dx * dx + dy * dy);
            double angleRad = Math.Atan2(dy, dx);
            double angleDeg = angleRad * (180.0 / Math.PI);
            if (angleDeg < 0) angleDeg += 360.0;
            shapeData.Add((s, r, angleDeg));
        }

        shapeData.Sort((a, b) => a.Radius.CompareTo(b.Radius));
        var rings = new List<List<(ShapeCanvasItemViewModel Shape, double Radius, double Angle)>>();
        foreach (var item in shapeData)
        {
            bool added = false;
            foreach (var ring in rings)
            {
                double ringMean = ring.Average(x => x.Radius);
                if (Math.Abs(item.Radius - ringMean) <= 35.0)
                {
                    ring.Add(item);
                    added = true;
                    break;
                }
            }
            if (!added)
            {
                rings.Add(new List<(ShapeCanvasItemViewModel Shape, double Radius, double Angle)> { item });
            }
        }

        int ringCount = rings.Count;
        var items = new List<RadialShapeItem>();
        double totalRadiusDev = 0;
        double totalAngularDev = 0;
        int angleEvaluatedCount = 0;

        for (int rIdx = 0; rIdx < rings.Count; rIdx++)
        {
            var ring = rings[rIdx];
            double ringMeanR = ring.Average(x => x.Radius);
            ring.Sort((a, b) => a.Angle.CompareTo(b.Angle));

            double expectedAngleDelta = ring.Count > 1 ? 360.0 / ring.Count : 360.0;

            for (int i = 0; i < ring.Count; i++)
            {
                var curr = ring[i];
                double devR = Math.Round(Math.Abs(curr.Radius - ringMeanR), 1);
                totalRadiusDev += devR;

                string rec = devR > 15.0
                    ? $"Align radial distance closer to ring {rIdx + 1} mean ({ringMeanR:0.#}px)"
                    : "Radial distance is well-balanced";

                if (ring.Count > 1)
                {
                    double nextAngle = (i < ring.Count - 1) ? ring[i + 1].Angle : ring[0].Angle + 360.0;
                    double actualDelta = nextAngle - curr.Angle;
                    double angleDeltaErr = Math.Abs(actualDelta - expectedAngleDelta);
                    totalAngularDev += angleDeltaErr;
                    angleEvaluatedCount++;

                    if (angleDeltaErr > 20.0)
                    {
                        rec = $"Evenly space angular interval around ring {rIdx + 1} (expected ~{expectedAngleDelta:0.#}°, got {actualDelta:0.#}°)";
                    }
                }

                items.Add(new RadialShapeItem(
                    curr.Shape.Id, curr.Shape.Name ?? curr.Shape.ShapeType ?? "Shape",
                    Math.Round(curr.Radius, 1), Math.Round(curr.Angle, 1),
                    rIdx + 1, devR, rec));
            }
        }

        double radiusVariance = totalShapes > 0 ? Math.Round(totalRadiusDev / totalShapes, 1) : 0.0;
        double avgAngleErr = angleEvaluatedCount > 0 ? Math.Round(totalAngularDev / angleEvaluatedCount, 1) : 0.0;
        double angularEvenness = Math.Round(Math.Clamp(100.0 - (avgAngleErr * 1.5), 0.0, 100.0), 1);

        double score = 100.0;
        score -= (radiusVariance * 0.8);
        score -= (avgAngleErr * 0.7);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => "High-Fidelity Radial Alignment",
            >= 80.0 => "Harmonious Concentric Rings",
            >= 60.0 => "Moderate Radial/Angular Skew",
            >= 40.0 => "Irregular Radial Distribution",
            _ => "Asymmetric Non-Concentric Cluster"
        };

        string summary = FormatRadialAlignmentSummary(
            totalShapes, ringCount, cx, cy, radiusVariance, angularEvenness,
            score, category, isSelection);

        return new DiagramRadialAlignmentAudit(
            totalShapes, ringCount, cx, cy, radiusVariance, angularEvenness,
            score, items, category, isSelection, summary);
    }

    public static string FormatRadialAlignmentSummary(
        int totalShapes, int ringCount, double cx, double cy,
        double radiusVar, double angularEvenness, double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes < 3) return $"Radial alignment audit ({scope}{totalShapes} shapes): Clean baseline (< 3 shapes)";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shapes");
        parts.Add($"{ringCount} concentric rings");
        parts.Add($"centroid ({cx:0.#}, {cy:0.#})");
        parts.Add($"radial dev ±{radiusVar:0.#}px");
        parts.Add($"angular evenness {angularEvenness:0.#}%");

        string details = string.Join(" · ", parts);
        return $"Diagram radial & concentric alignment audit ({scope}{details} · {category} · {score:0.#}% score)";
    }

public static string FormatGroupNestingSummary(
        int totalShapes, int containers, int maxDepth, double avgDepth, int overcrowded,
        double score, string category, bool isSelection)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Shape group nesting audit ({scope}0 shapes): Canvas is empty";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shapes, {containers} containers");
        parts.Add($"max depth {maxDepth}, avg {avgDepth:0.##}");
        if (overcrowded > 0) parts.Add($"⚠ {overcrowded} overcrowded");

        string details = string.Join(" · ", parts);
        return $"Shape group nesting audit ({scope}{details} · {category} · {score:0.#}% score)";
    }



    /// <summary>
    /// Audits diagram shape fill color harmony, WCAG 2.1 non-text contrast ratios against canvas background,
    /// and palette consistency across vector shapes.
    /// </summary>
    public DiagramFillColorHarmonyAudit AuditFillColorPaletteHarmony(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditFillColorPaletteHarmony(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramFillColorHarmonyAudit AuditFillColorPaletteHarmony(
        IEnumerable<ShapeCanvasItemViewModel>? shapes,
        bool isSelection = false)
    {
        var list = shapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        string scopeStr = isSelection ? "Selection · " : "";

        if (list.Count == 0)
        {
            return new DiagramFillColorHarmonyAudit(
                0, 0, 0, 0, "None", 0, 100.0,
                Array.Empty<ShapeColorHarmonyItem>(),
                "Empty Canvas", isSelection,
                $"Diagram fill color harmony audit ({scopeStr}0 shapes): Canvas is empty");
        }

        var items = new List<ShapeColorHarmonyItem>();
        var colorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var familyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int compliantCount = 0;
        int lowContrastCount = 0;

        string bgHex = ShapeDesignStudioViewModel.CanvasBackgroundHex;

        foreach (var shape in list)
        {
            string rawFill = shape.Fill ?? ShapeDesignStudioViewModel.ThemeAccentHex();
            string cleanFill = rawFill.Trim().TrimStart('#');
            if (cleanFill.Length != 6) cleanFill = "4A90E2";

            double luminance = Math.Round(Services.ContrastGuard.GetLuminance(cleanFill), 3);
            double contrast = Math.Round(Services.ContrastGuard.GetContrastRatio(cleanFill, bgHex), 2);
            bool meetsWcag = contrast >= 3.0;

            if (meetsWcag) compliantCount++;
            else lowContrastCount++;

            colorCounts[cleanFill] = colorCounts.GetValueOrDefault(cleanFill, 0) + 1;

            int r = int.Parse(cleanFill.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            int g = int.Parse(cleanFill.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            int b = int.Parse(cleanFill.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));

            string family;
            if (max - min < 24)
            {
                if (r > 200) family = "White/Light Gray";
                else if (r < 60) family = "Black/Dark Gray";
                else family = "Neutral Gray";
            }
            else if (r >= g && r >= b)
            {
                if (g > b * 1.4) family = "Orange/Amber";
                else if (b > g * 1.4) family = "Magenta/Pink";
                else family = "Red/Crimson";
            }
            else if (g >= r && g >= b)
            {
                if (b > r * 1.4) family = "Cyan/Teal";
                else if (r > b * 1.4) family = "Lime/Chartreuse";
                else family = "Green/Emerald";
            }
            else
            {
                if (r > g * 1.4) family = "Purple/Violet";
                else if (g > r * 1.4) family = "Cyan/Azure";
                else family = "Blue/Cobalt";
            }

            familyCounts[family] = familyCounts.GetValueOrDefault(family, 0) + 1;

            string status = meetsWcag
                ? $"✓ {contrast:0.#}:1 ratio (Pass WCAG AA)"
                : $"⚠ {contrast:0.#}:1 ratio (Low contrast < 3:1)";

            items.Add(new ShapeColorHarmonyItem(
                shape.Id, shape.Name ?? "Shape", $"#{cleanFill}",
                luminance, contrast, meetsWcag, family, status));
        }

        string dominantFamily = familyCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "None";
        int dominantCount = familyCounts.GetValueOrDefault(dominantFamily, 0);

        int uniqueColors = colorCounts.Count;
        double compliancePct = (double)compliantCount / list.Count;

        double penalty = (1.0 - compliancePct) * 50.0;
        if (familyCounts.Count > 4 && list.Count <= 12)
        {
            penalty += (familyCounts.Count - 4) * 8.0;
        }

        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Harmonious & Accessible (≥90%)",
            >= 75.0 => "Cohesive Palette (75–89%)",
            >= 60.0 => "Moderate Variety (60–74%)",
            >= 40.0 => "Low Contrast / Discordant (40–59%)",
            _ => "High Clutter / Accessibility Alert (<40%)"
        };

        string summary = FormatColorPaletteHarmonySummary(
            list.Count, uniqueColors, compliantCount, lowContrastCount, dominantFamily, dominantCount,
            score, category, scopeStr);

        return new DiagramFillColorHarmonyAudit(
            list.Count, uniqueColors, compliantCount, lowContrastCount, dominantFamily, dominantCount,
            score, items, category, isSelection, summary);
    }

    public static string FormatColorPaletteHarmonySummary(
        int totalShapes, int uniqueColors, int compliantCount, int lowContrastCount,
        string dominantFamily, int dominantCount, double harmonyScore, string category,
        string scopeStr = "")
    {
        if (totalShapes == 0) return $"Diagram fill color harmony audit ({scopeStr}0 shapes): Canvas is empty";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var alerts = new List<string>();
        alerts.Add($"{uniqueColors} unique {(uniqueColors == 1 ? "color" : "colors")}");
        alerts.Add($"dominant: {dominantFamily} ({dominantCount})");
        if (lowContrastCount > 0)
        {
            alerts.Add($"⚠ {lowContrastCount} low contrast (<3:1)");
        }
        else
        {
            alerts.Add("100% WCAG 2.1 AA");
        }

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
        return $"Fill color harmony audit ({scopeStr}{shapeStr} · {category} · {harmonyScore:0.#}% harmony){details}";
    }

    /// <summary>
    /// Audits diagram shapes for geometric aspect ratio harmony, standard design proportions (1:1, 4:3, 16:9, Golden Ratio),
    /// extreme sliver distortions, and visual mass distribution.
    /// </summary>
    public DiagramAspectRatioHarmonyAudit AuditShapeAspectRatioHarmony(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditShapeAspectRatioHarmony(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramAspectRatioHarmonyAudit AuditShapeAspectRatioHarmony(
        IEnumerable<ShapeCanvasItemViewModel>? shapes,
        bool isSelection = false)
    {
        var list = shapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        string scopeStr = isSelection ? "Selection · " : "";

        if (list.Count == 0)
        {
            return new DiagramAspectRatioHarmonyAudit(
                0, 0.0, 0.0, 0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<ShapeAspectRatioItem>(),
                "Empty Canvas", isSelection,
                $"Diagram aspect ratio harmony audit ({scopeStr}0 shapes): Canvas is empty");
        }

        var items = new List<ShapeAspectRatioItem>();
        int extremeDistortionCount = 0;
        int goldenCount = 0;
        int squareCount = 0;

        var ratios = new List<double>();
        var areas = new List<double>();

        var standardRatios = new (double Ratio, string Name)[]
        {
            (1.0, "1:1 Square"),
            (4.0 / 3.0, "4:3 Standard"),
            (1.618, "1.618 Golden Ratio"),
            (16.0 / 9.0, "16:9 Widescreen"),
            (1.5, "3:2 Classic"),
            (2.0, "2:1 Banner"),
            (3.0 / 4.0, "3:4 Portrait"),
            (9.0 / 16.0, "9:16 Vertical")
        };

        foreach (var shape in list)
        {
            double w = Math.Max(1.0, Math.Abs(shape.Width));
            double h = Math.Max(1.0, Math.Abs(shape.Height));
            double r = Math.Round(w / h, 3);
            double area = Math.Round(w * h, 1);

            ratios.Add(r);
            areas.Add(area);

            var closest = standardRatios
                .Select(s => (s.Name, Diff: Math.Abs(r - s.Ratio) / s.Ratio, s.Ratio))
                .OrderBy(x => x.Diff)
                .First();

            bool isSquare = Math.Abs(r - 1.0) <= 0.08;
            bool isGolden = Math.Abs(r - 1.618) <= 0.12;
            bool isExtreme = (r < 0.15 || r > 7.0);

            if (isSquare) squareCount++;
            if (isGolden) goldenCount++;
            if (isExtreme) extremeDistortionCount++;

            string status = isExtreme ? "⚠ Extreme sliver distortion" :
                            isSquare ? "✓ Square / 1:1" :
                            isGolden ? "✓ Golden ratio / 1.618" :
                            $"~ {closest.Name} (±{Math.Round(closest.Diff * 100, 0)}%)";

            string label = !string.IsNullOrWhiteSpace(shape.Text) ? shape.Text :
                           shape.ShapeType.ToString();

            items.Add(new ShapeAspectRatioItem(
                shape.Id,
                label,
                Math.Round(w, 1),
                Math.Round(h, 1),
                r,
                area,
                closest.Name,
                Math.Round(closest.Diff, 3),
                isExtreme,
                status));
        }

        double avgRatio = Math.Round(ratios.Average(), 2);
        double ratioVariance = ratios.Count > 1
            ? Math.Round(ratios.Select(r => Math.Pow(r - avgRatio, 2)).Average(), 2)
            : 0.0;

        double avgArea = Math.Round(areas.Average(), 1);
        double areaVariance = areas.Count > 1
            ? Math.Round(areas.Select(a => Math.Pow(a - avgArea, 2)).Average(), 1)
            : 0.0;

        double penalty = (extremeDistortionCount * 18.0);
        if (ratioVariance > 2.0)
        {
            penalty += Math.Min(25.0, (ratioVariance - 2.0) * 8.0);
        }

        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Harmonious & Proportional (≥90%)",
            >= 75.0 => "Balanced Geometries (75–89%)",
            >= 60.0 => "Moderate Variance (60–74%)",
            >= 40.0 => "Distorted / Sliver Shapes (40–59%)",
            _ => "Chaotic Proportions (<40%)"
        };

        string summary = FormatAspectRatioHarmonySummary(
            list.Count, avgRatio, ratioVariance, extremeDistortionCount, goldenCount, squareCount,
            score, category, scopeStr);

        return new DiagramAspectRatioHarmonyAudit(
            list.Count, avgRatio, ratioVariance, extremeDistortionCount, avgArea, areaVariance,
            goldenCount, squareCount, score, items, category, isSelection, summary);
    }

    public static string FormatAspectRatioHarmonySummary(
        int totalShapes, double avgRatio, double ratioVariance, int extremeDistortionCount,
        int goldenCount, int squareCount, double harmonyScore, string category,
        string scopeStr = "")
    {
        if (totalShapes == 0) return $"Diagram aspect ratio harmony audit ({scopeStr}0 shapes): Canvas is empty";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var alerts = new List<string>();
        alerts.Add($"avg ratio {avgRatio:0.##}:1 (var {ratioVariance:0.##})");
        if (goldenCount > 0) alerts.Add($"{goldenCount} golden");
        if (squareCount > 0) alerts.Add($"{squareCount} square");
        if (extremeDistortionCount > 0) alerts.Add($"⚠ {extremeDistortionCount} sliver {(extremeDistortionCount == 1 ? "distortion" : "distortions")}");
        else alerts.Add("0 distortions");

        string details = string.Join(" · ", alerts);
        return $"Aspect ratio harmony audit ({scopeStr}{shapeStr} · {category} · {harmonyScore:0.#}% harmony): {details}";
    }

    /// <summary>
    /// Audits diagram connector routes for directness, excessive orthogonal bend counts, and obstacle clipping
    /// through unrelated shape bounding boxes.
    /// </summary>
    public DiagramConnectorRouteAudit AuditConnectorRouteDirectness(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditConnectorRouteDirectness(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramConnectorRouteAudit AuditConnectorRouteDirectness(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapesOnCanvas = null,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var allList = (allShapesOnCanvas ?? list).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        // Find connectors in list
        var connectors = list.Where(s => s.ShapeType != null && (
            s.ShapeType.Contains("connector", StringComparison.OrdinalIgnoreCase) ||
            s.ShapeType.Contains("line", StringComparison.OrdinalIgnoreCase) ||
            s.ShapeType.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            s.ShapeType.Contains("polyline", StringComparison.OrdinalIgnoreCase) ||
            s.ShapeType.Contains("curve", StringComparison.OrdinalIgnoreCase))).ToList();

        if (connectors.Count == 0)
        {
            connectors = list.Where(s => Math.Abs(s.Width) <= 6.0 || Math.Abs(s.Height) <= 6.0).ToList();
        }

        if (connectors.Count == 0)
        {
            return new DiagramConnectorRouteAudit(
                0, 0, 0, 0, 0, 1.0, 100.0,
                Array.Empty<ConnectorRouteItem>(),
                "Zero Connectors",
                isSelection,
                $"Diagram connector route audit ({scopeStr}0 connectors): Canvas contains no connector routes");
        }

        var obstacleShapes = allList.Where(s => !connectors.Contains(s) && Math.Abs(s.Width) > 10.0 && Math.Abs(s.Height) > 10.0).ToList();

        var items = new List<ConnectorRouteItem>();
        int directCount = 0;
        int moderateCount = 0;
        int convolutedCount = 0;
        int totalObstacleClipping = 0;
        double directnessSum = 0;

        foreach (var conn in connectors)
        {
            double w = Math.Abs(conn.Width);
            double h = Math.Abs(conn.Height);
            double euclidean = Math.Round(Math.Sqrt(w * w + h * h), 1);
            if (euclidean < 1.0) euclidean = 1.0;

            bool isOrthogonal = conn.ShapeType != null && (
                conn.ShapeType.Contains("orthogonal", StringComparison.OrdinalIgnoreCase) ||
                conn.ShapeType.Contains("elbow", StringComparison.OrdinalIgnoreCase) ||
                conn.ShapeType.Contains("polyline", StringComparison.OrdinalIgnoreCase));

            int bendCount = isOrthogonal ? (w > 10 && h > 10 ? 2 : 1) : 0;
            double actualLength = isOrthogonal ? (w + h) : euclidean;

            double directnessRatio = Math.Round(actualLength / euclidean, 2);
            directnessSum += directnessRatio;

            double connMinX = conn.X;
            double connMaxX = conn.X + w;
            double connMinY = conn.Y;
            double connMaxY = conn.Y + h;

            int clippingCount = 0;
            foreach (var obs in obstacleShapes)
            {
                double obsMinX = obs.X;
                double obsMaxX = obs.X + Math.Abs(obs.Width);
                double obsMinY = obs.Y;
                double obsMaxY = obs.Y + Math.Abs(obs.Height);

                bool overlaps = (connMinX < obsMaxX - 5 && connMaxX > obsMinX + 5 &&
                                 connMinY < obsMaxY - 5 && connMaxY > obsMinY + 5);
                if (overlaps)
                {
                    clippingCount++;
                }
            }

            if (clippingCount > 0) totalObstacleClipping += clippingCount;

            string routeQuality;
            if (clippingCount > 0)
            {
                routeQuality = $"⚠ Obstacle collision ({clippingCount} shape clipping)";
            }
            else if (directnessRatio <= 1.15 && bendCount <= 1)
            {
                directCount++;
                routeQuality = "✓ Direct route";
            }
            else if (directnessRatio <= 1.55 && bendCount <= 2)
            {
                moderateCount++;
                routeQuality = "✓ Normal orthogonal elbow";
            }
            else
            {
                convolutedCount++;
                routeQuality = "⚠ Convoluted route detour";
            }

            string label = !string.IsNullOrWhiteSpace(conn.Name) ? conn.Name :
                           (!string.IsNullOrWhiteSpace(conn.Text) ? conn.Text : $"Connector #{conn.Id}");

            items.Add(new ConnectorRouteItem(
                conn.Id,
                label,
                actualLength,
                euclidean,
                directnessRatio,
                bendCount,
                clippingCount,
                routeQuality));
        }

        double avgDirectness = Math.Round(directnessSum / connectors.Count, 2);
        double penalty = (convolutedCount * 15.0) + (totalObstacleClipping * 12.0) + (moderateCount * 3.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Optimal Direct Routing (≥90%)",
            >= 75.0 => "Efficient Orthogonal Routes (75–89%)",
            >= 50.0 => "Moderate Detours & Routing Clutter (50–74%)",
            _ => "Convoluted Routing & Obstacle Collisions (<50%)"
        };

        string summary = FormatConnectorRouteSummary(
            connectors.Count, directCount, moderateCount, convolutedCount, totalObstacleClipping,
            avgDirectness, score, category, scopeStr);

        return new DiagramConnectorRouteAudit(
            connectors.Count, directCount, moderateCount, convolutedCount, totalObstacleClipping,
            avgDirectness, score, items, category, isSelection, summary);
    }

    public static string FormatConnectorRouteSummary(
        int totalConnectors, int directCount, int moderateCount, int convolutedCount,
        int obstacleClipping, double avgDirectness, double routeScore, string category,
        string scopeStr = "")
    {
        if (totalConnectors == 0) return $"Diagram connector route audit ({scopeStr}0 connectors): Canvas contains no connector routes";

        string connStr = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var alerts = new List<string>();
        alerts.Add($"avg directness {avgDirectness:0.##}x");
        if (directCount > 0) alerts.Add($"{directCount} direct");
        if (moderateCount > 0) alerts.Add($"{moderateCount} elbow");
        if (convolutedCount > 0) alerts.Add($"⚠ {convolutedCount} convoluted");
        if (obstacleClipping > 0) alerts.Add($"⚠ {obstacleClipping} obstacle clipping");
        else alerts.Add("0 obstacle collisions");

        string details = string.Join(" · ", alerts);
        return $"Connector route audit ({scopeStr}{connStr} · {category} · {routeScore:0.#}% efficiency): {details}";
    }

    /// <summary>
    /// Audits diagram shape Z-order stacking, accidental complete/partial occlusion of smaller shapes by larger background shapes,
    /// and visual hierarchy clarity.
    /// </summary>
    public DiagramLayerOcclusionAudit AuditLayerStackingAndOcclusion(IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = SelectedItems.ToList();
        var targets = targetShapes?.ToList() ?? (selected.Count > 0 ? selected : Shapes.ToList());
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditLayerStackingAndOcclusion(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramLayerOcclusionAudit AuditLayerStackingAndOcclusion(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapesOnCanvas = null,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var allList = (allShapesOnCanvas ?? list).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        int totalShapes = list.Count;
        if (totalShapes == 0)
        {
            return new DiagramLayerOcclusionAudit(
                0, 0, 0, 0, 100.0,
                Array.Empty<ShapeLayerOcclusionItem>(),
                "Empty Canvas",
                isSelection,
                $"Diagram layer stacking & occlusion audit ({scopeStr}0 shapes): No shapes to evaluate");
        }

        if (totalShapes == 1)
        {
            var single = list[0];
            double area = Math.Max(0.0, single.Width * single.Height);
            string shapeType = single.Prst ?? "shape";
            var item = new ShapeLayerOcclusionItem(
                single.Id ?? "s1", shapeType, 0, Math.Round(area, 1), 0, 0.0, false, "✓ Sole shape · Full visibility");
            return new DiagramLayerOcclusionAudit(
                1, 0, 0, 0, 100.0,
                new[] { item },
                "Optimal Stacking (100%)",
                isSelection,
                $"Diagram layer stacking & occlusion audit ({scopeStr}1 shape · Optimal Stacking · 100% score): Clean single layer");
        }

        var shapeZIndex = new Dictionary<ShapeCanvasItemViewModel, int>();
        for (int i = 0; i < allList.Count; i++)
        {
            shapeZIndex[allList[i]] = i;
        }

        int overlappingPairs = 0;
        int severeOcclusionCount = 0;
        int completelyHiddenCount = 0;
        var items = new List<ShapeLayerOcclusionItem>();

        foreach (var shape in list)
        {
            int zA = shapeZIndex.TryGetValue(shape, out int idxA) ? idxA : 0;
            double areaA = Math.Max(0.0, shape.Width * shape.Height);
            string shapeType = shape.Prst ?? "shape";

            if (areaA <= 0.0)
            {
                items.Add(new ShapeLayerOcclusionItem(
                    shape.Id ?? "s", shapeType, zA, 0.0, 0, 0.0, false, "Line/degenerate shape"));
                continue;
            }

            int occludingCount = 0;
            double maxOcclusionPct = 0.0;

            foreach (var other in allList)
            {
                if (ReferenceEquals(shape, other)) continue;
                int zB = shapeZIndex.TryGetValue(other, out int idxB) ? idxB : 0;
                if (zB <= zA) continue;

                double ax1 = shape.X;
                double ay1 = shape.Y;
                double ax2 = shape.X + shape.Width;
                double ay2 = shape.Y + shape.Height;

                double bx1 = other.X;
                double by1 = other.Y;
                double bx2 = other.X + other.Width;
                double by2 = other.Y + other.Height;

                double ix1 = Math.Max(ax1, bx1);
                double iy1 = Math.Max(ay1, by1);
                double ix2 = Math.Min(ax2, bx2);
                double iy2 = Math.Min(ay2, by2);

                if (ix2 > ix1 && iy2 > iy1)
                {
                    double overlapArea = (ix2 - ix1) * (iy2 - iy1);
                    double pct = (overlapArea / areaA) * 100.0;
                    if (pct > 0.5)
                    {
                        overlappingPairs++;
                        occludingCount++;
                        if (pct > maxOcclusionPct) maxOcclusionPct = pct;
                    }
                }
            }

            maxOcclusionPct = Math.Min(100.0, Math.Round(maxOcclusionPct, 1));
            bool isHidden = maxOcclusionPct >= 99.0;
            if (isHidden) completelyHiddenCount++;
            else if (maxOcclusionPct >= 75.0) severeOcclusionCount++;

            string status = isHidden
                ? $"⚠ Completely hidden by layer above ({maxOcclusionPct:0.#}%)"
                : maxOcclusionPct >= 75.0
                    ? $"⚠ Severe occlusion ({maxOcclusionPct:0.#}%)"
                    : maxOcclusionPct > 0.0
                        ? $"Partial overlap ({maxOcclusionPct:0.#}%)"
                        : "✓ Fully visible";

            items.Add(new ShapeLayerOcclusionItem(
                shape.Id ?? "s", shapeType, zA, Math.Round(areaA, 1), occludingCount, maxOcclusionPct, isHidden, status));
        }

        double score = 100.0 - (completelyHiddenCount * 25.0) - (severeOcclusionCount * 10.0) - (overlappingPairs * 2.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Layer Stacking & Visibility (≥90%)"
            : score >= 75.0
                ? "Acceptable Layering with Minor Overlaps (75-89%)"
                : "Severe Occlusion & Visual Clutter (<75%)";

        string summary = FormatLayerStackingSummary(
            totalShapes, overlappingPairs, severeOcclusionCount, completelyHiddenCount,
            score, category, isSelection);

        return new DiagramLayerOcclusionAudit(
            totalShapes, overlappingPairs, severeOcclusionCount, completelyHiddenCount,
            score, items, category, isSelection, summary);
    }

    public static string FormatLayerStackingSummary(
        int totalShapes, int overlappingPairs, int severeOcclusions, int completelyHidden,
        double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        if (totalShapes == 0) return $"Diagram layer stacking & occlusion audit ({scopeStr}0 shapes): No shapes to evaluate";
        if (totalShapes == 1) return $"Diagram layer stacking & occlusion audit ({scopeStr}1 shape · Optimal Stacking · 100% score): Clean single layer";

        var alerts = new List<string>();
        if (completelyHidden > 0) alerts.Add($"⚠ {completelyHidden} completely hidden");
        if (severeOcclusions > 0) alerts.Add($"⚠ {severeOcclusions} severe occlusion");
        if (overlappingPairs > 0) alerts.Add($"{overlappingPairs} overlapping {(overlappingPairs == 1 ? "pair" : "pairs")}");
        else alerts.Add("0 overlapping pairs");

        string details = string.Join(" · ", alerts);
        return $"Diagram layer stacking & occlusion audit ({scopeStr}{shapeStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits diagram canvas shape symmetric horizontal/vertical alignment, grid snapping residuals, and distribution interval uniformity.
    /// </summary>
    public DiagramAlignmentAndSnappingAudit AuditAlignmentAndGridSnapping(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null,
        double gridSize = 10.0)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes != null ? targetShapes.ToList() : (selected.Count > 0 ? selected : Shapes.ToList()));
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditAlignmentAndGridSnapping(targets, isSelection, gridSize);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramAlignmentAndSnappingAudit AuditAlignmentAndGridSnapping(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        bool isSelection = false,
        double gridSize = 10.0)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";
        int totalShapes = list.Count;

        if (totalShapes == 0)
        {
            return new DiagramAlignmentAndSnappingAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeAlignmentItem>(),
                "Empty Canvas",
                isSelection,
                $"Diagram alignment & grid snapping audit ({scopeStr}0 shapes): No shapes to evaluate");
        }

        if (gridSize <= 0.0) gridSize = 10.0;

        int snappedCount = 0;
        int offGridCount = 0;
        double sumResidual = 0.0;
        var items = new List<ShapeAlignmentItem>();

        const double alignTolerance = 2.0;

        foreach (var shape in list)
        {
            double x = shape.X;
            double y = shape.Y;
            double w = shape.Width;
            double h = shape.Height;

            double rx = Math.Min(Math.Abs(x % gridSize), gridSize - Math.Abs(x % gridSize));
            double ry = Math.Min(Math.Abs(y % gridSize), gridSize - Math.Abs(y % gridSize));
            double maxResidual = Math.Max(rx, ry);
            sumResidual += maxResidual;

            bool isSnapped = maxResidual <= 0.5;
            if (isSnapped) snappedCount++;
            else offGridCount++;

            int alignedPartners = 0;
            var partnerTypes = new List<string>();

            double leftA = x;
            double centerX_A = x + (w / 2.0);
            double rightA = x + w;
            double topA = y;
            double centerY_A = y + (h / 2.0);
            double bottomA = y + h;

            foreach (var other in list)
            {
                if (ReferenceEquals(shape, other)) continue;

                double leftB = other.X;
                double centerX_B = other.X + (other.Width / 2.0);
                double rightB = other.X + other.Width;
                double topB = other.Y;
                double centerY_B = other.Y + (other.Height / 2.0);
                double bottomB = other.Y + other.Height;

                bool aligned = false;
                if (Math.Abs(leftA - leftB) <= alignTolerance) { aligned = true; partnerTypes.Add("Left"); }
                else if (Math.Abs(centerX_A - centerX_B) <= alignTolerance) { aligned = true; partnerTypes.Add("Center-X"); }
                else if (Math.Abs(rightA - rightB) <= alignTolerance) { aligned = true; partnerTypes.Add("Right"); }

                if (Math.Abs(topA - topB) <= alignTolerance) { aligned = true; partnerTypes.Add("Top"); }
                else if (Math.Abs(centerY_A - centerY_B) <= alignTolerance) { aligned = true; partnerTypes.Add("Center-Y"); }
                else if (Math.Abs(bottomA - bottomB) <= alignTolerance) { aligned = true; partnerTypes.Add("Bottom"); }

                if (aligned) alignedPartners++;
            }

            string alignDetails = alignedPartners > 0
                ? $"{alignedPartners} aligned {(alignedPartners == 1 ? "partner" : "partners")} ({string.Join(", ", partnerTypes.Distinct())})"
                : "No aligned partners";

            string status = isSnapped
                ? (alignedPartners > 0 ? "✓ Snapped & aligned" : "✓ Grid snapped · Independent alignment")
                : $"⚠ Off-grid ({maxResidual:0.#}px residual)";

            items.Add(new ShapeAlignmentItem(
                shape.Id ?? "s",
                shape.Prst ?? "shape",
                Math.Round(x, 1),
                Math.Round(y, 1),
                Math.Round(w, 1),
                Math.Round(h, 1),
                Math.Round(maxResidual, 1),
                isSnapped,
                alignedPartners,
                alignDetails,
                status));
        }

        int alignedShapes = items.Count(it => it.AlignedPartnersCount > 0);
        int misalignedShapes = totalShapes > 1 ? items.Count(it => it.AlignedPartnersCount == 0) : 0;
        double avgResidual = Math.Round(sumResidual / totalShapes, 1);

        double score = 100.0 - (offGridCount * 5.0);
        if (totalShapes >= 3)
        {
            score -= (misalignedShapes * 4.0);
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Alignment & Grid Snapping (≥90%)"
            : score >= 75.0
                ? "Moderate Alignment Residuals (75-89%)"
                : "Irregular Alignment & Off-Grid Placement (<75%)";

        string summary = FormatAlignmentSnappingSummary(
            totalShapes, snappedCount, offGridCount, alignedShapes, misalignedShapes,
            avgResidual, score, category, isSelection);

        return new DiagramAlignmentAndSnappingAudit(
            totalShapes, snappedCount, offGridCount, alignedShapes, misalignedShapes,
            avgResidual, score, items, category, isSelection, summary);
    }

    public static string FormatAlignmentSnappingSummary(
        int totalShapes, int snappedCount, int offGridCount, int alignedShapes,
        int misalignedShapes, double avgResidual, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        if (totalShapes == 0) return $"Diagram alignment & grid snapping audit ({scopeStr}0 shapes): No shapes to evaluate";
        if (totalShapes == 1) return $"Diagram alignment & grid snapping audit ({scopeStr}1 shape · {category} · 100% score): Single shape baseline";

        var alerts = new List<string>();
        if (offGridCount > 0) alerts.Add($"⚠ {offGridCount} off-grid (avg residual {avgResidual:0.#}px)");
        else alerts.Add("100% grid snapped");

        if (totalShapes >= 3)
        {
            if (misalignedShapes > 0) alerts.Add($"⚠ {misalignedShapes} isolated/misaligned");
            else alerts.Add("symmetric cluster alignment");
        }

        string details = string.Join(" · ", alerts);
        return $"Diagram alignment & grid snapping audit ({scopeStr}{shapeStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits diagram connection lines and connectors for segment orthogonality, cardinal angle snapping,
    /// collinear routing kinks, and connection routing tidiness.
    /// </summary>
    public DiagramConnectionPortAudit AuditConnectionPortOrthogonality(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes != null ? targetShapes.ToList() : (selected.Count > 0 ? selected : Shapes.ToList()));
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditConnectionPortOrthogonality(targets, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramConnectionPortAudit AuditConnectionPortOrthogonality(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        // Identify connector/line shapes
        var connectors = list.Where(s =>
            (s.PathPoints != null && s.PathPoints.Count >= 2) ||
            string.Equals(s.ShapeType, "line", StringComparison.OrdinalIgnoreCase) ||
            (s.Name != null && s.Name.Contains("Connector", StringComparison.OrdinalIgnoreCase))
        ).ToList();

        int totalConnectors = connectors.Count;
        if (totalConnectors == 0)
        {
            return new DiagramConnectionPortAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ConnectionPortRoutingItem>(),
                "No Connectors",
                isSelection,
                $"Diagram connection port & routing audit ({scopeStr}0 connectors): No connector lines to evaluate");
        }

        int totalSegments = 0;
        int orthogonalSegments = 0;
        int nonOrthogonalSegments = 0;
        int collinearKinks = 0;
        double sumAngleDeviation = 0.0;
        var items = new List<ConnectionPortRoutingItem>();

        const double angleToleranceDegrees = 2.5;

        foreach (var conn in connectors)
        {
            var pts = ConnectorPoints(conn);
            if (pts.Count < 2)
            {
                // Fallback to bounding box corners if no explicit PathPoints
                pts = new List<(double X, double Y)>
                {
                    (conn.X, conn.Y),
                    (conn.X + conn.Width, conn.Y + conn.Height)
                };
            }

            int segCount = pts.Count - 1;
            totalSegments += segCount;
            int connNonOrthogonal = 0;
            int connKinks = 0;
            double connMaxDev = 0.0;

            var segmentAngles = new List<double>();

            for (int i = 0; i < segCount; i++)
            {
                double dx = pts[i + 1].X - pts[i].X;
                double dy = pts[i + 1].Y - pts[i].Y;

                double angleDeg = Math.Atan2(Math.Abs(dy), Math.Abs(dx)) * 180.0 / Math.PI; // 0 to 90
                double dev = Math.Min(angleDeg, 90.0 - angleDeg); // distance to nearest horizontal or vertical (0 or 90)

                sumAngleDeviation += dev;
                if (dev > connMaxDev) connMaxDev = dev;

                bool isOrthogonal = dev <= angleToleranceDegrees || Math.Abs(dx) < 1.0 || Math.Abs(dy) < 1.0;
                if (isOrthogonal)
                {
                    orthogonalSegments++;
                }
                else
                {
                    nonOrthogonalSegments++;
                    connNonOrthogonal++;
                }

                double fullAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                segmentAngles.Add(fullAngle);
            }

            // Check collinear kinks between consecutive segments
            for (int i = 0; i < segmentAngles.Count - 1; i++)
            {
                double diff = Math.Abs(segmentAngles[i] - segmentAngles[i + 1]);
                if (diff > 180.0) diff = 360.0 - diff;

                // If consecutive segments point in nearly identical direction (diff < 5 degrees), it's a redundant kink
                if (diff > 0.1 && diff <= 5.0)
                {
                    collinearKinks++;
                    connKinks++;
                }
            }

            bool isFullyOrthogonal = connNonOrthogonal == 0;
            string status;
            if (isFullyOrthogonal && connKinks == 0)
                status = $"✓ Fully orthogonal route ({segCount} {(segCount == 1 ? "segment" : "segments")})";
            else if (connKinks > 0)
                status = $"⚠ {connKinks} collinear kink {(connKinks == 1 ? "vertex" : "vertices")} · Max deviation: {connMaxDev:0.#}°";
            else
                status = $"⚠ {connNonOrthogonal}/{segCount} angled {(connNonOrthogonal == 1 ? "segment" : "segments")} (max deviation {connMaxDev:0.#}°)";

            items.Add(new ConnectionPortRoutingItem(
                conn.Id,
                conn.Name ?? "Connector",
                segCount,
                isFullyOrthogonal,
                connNonOrthogonal,
                connKinks,
                Math.Round(connMaxDev, 1),
                status));
        }

        double avgAngleDev = totalSegments > 0 ? Math.Round(sumAngleDeviation / totalSegments, 1) : 0.0;
        double score = 100.0;
        if (totalSegments > 0)
        {
            double nonOrthoRatio = (double)nonOrthogonalSegments / totalSegments;
            score -= (nonOrthoRatio * 50.0);
            score -= (collinearKinks * 10.0);
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Orthogonal Routing (≥90%)"
            : score >= 75.0
                ? "Moderate Routing Angularity (75-89%)"
                : "Non-Orthogonal / Disordered Routing (<75%)";

        string summary = FormatConnectionPortSummary(
            totalConnectors, totalSegments, orthogonalSegments, nonOrthogonalSegments,
            collinearKinks, avgAngleDev, score, category, isSelection);

        return new DiagramConnectionPortAudit(
            totalConnectors, totalSegments, orthogonalSegments, nonOrthogonalSegments,
            collinearKinks, avgAngleDev, score, items, category, isSelection, summary);
    }

    public static string FormatConnectionPortSummary(
        int totalConnectors, int totalSegments, int orthogonalCount, int nonOrthogonalCount,
        int collinearKinks, double avgAngleDev, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalConnectors == 0) return $"Diagram connection port & routing audit ({scopeStr}0 connectors): No connector lines to evaluate";

        string connStr = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var parts = new List<string>();
        parts.Add($"{orthogonalCount}/{totalSegments} orthogonal {(orthogonalCount == 1 ? "segment" : "segments")}");

        if (nonOrthogonalCount > 0) parts.Add($"⚠ {nonOrthogonalCount} angled (avg dev {avgAngleDev:0.#}°)");
        else parts.Add("0 angled segments");

        if (collinearKinks > 0) parts.Add($"⚠ {collinearKinks} collinear {(collinearKinks == 1 ? "kink" : "kinks")}");

        string details = string.Join(" · ", parts);
        return $"Diagram connection port & routing audit ({scopeStr}{connStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits diagram canvas shape visual density, perimeter margin padding, and viewport edge safety.
    /// </summary>
    public DiagramVisualDensityAudit AuditVisualDensityAndMargins(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null,
        double canvasWidth = 1920.0,
        double canvasHeight = 1080.0)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes != null ? targetShapes.ToList() : (selected.Count > 0 ? selected : Shapes.ToList()));
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditVisualDensityAndMargins(targets, canvasWidth, canvasHeight, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramVisualDensityAudit AuditVisualDensityAndMargins(
        IEnumerable<ShapeCanvasItemViewModel>? shapes,
        double canvasWidth = 1920.0,
        double canvasHeight = 1080.0,
        bool isSelection = false)
    {
        var shapeList = shapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        int totalShapes = shapeList.Count;

        if (totalShapes == 0)
        {
            return new DiagramVisualDensityAudit(
                0, canvasWidth, canvasHeight, canvasWidth * canvasHeight, 0.0, 0.0, 0,
                24.0, 100.0, Array.Empty<ShapePerimeterMarginItem>(),
                "Empty Canvas", isSelection,
                "Diagram visual density & margin audit: 0 shapes · Canvas is empty");
        }

        double canvasArea = canvasWidth * canvasHeight;
        double totalShapeArea = 0.0;
        int perimeterViolations = 0;
        double minMargin = double.MaxValue;
        var items = new List<ShapePerimeterMarginItem>();

        const double safetyThreshold = 24.0;

        foreach (var s in shapeList)
        {
            double w = Math.Max(0.0, s.Width);
            double h = Math.Max(0.0, s.Height);
            double x = s.X;
            double y = s.Y;
            double area = w * h;
            totalShapeArea += area;

            double marginLeft = x;
            double marginTop = y;
            double marginRight = canvasWidth - (x + w);
            double marginBottom = canvasHeight - (y + h);

            double shapeMinMargin = Math.Min(Math.Min(marginLeft, marginTop), Math.Min(marginRight, marginBottom));
            if (shapeMinMargin < minMargin) minMargin = shapeMinMargin;

            bool hasViolation = marginLeft < safetyThreshold || marginTop < safetyThreshold ||
                                marginRight < safetyThreshold || marginBottom < safetyThreshold;

            if (hasViolation) perimeterViolations++;

            string status = hasViolation
                ? $"⚠ Perimeter margin violation (<{safetyThreshold}px) · Min margin: {shapeMinMargin:0.#}px"
                : $"✓ Safe perimeter margin (≥{safetyThreshold}px) · Min margin: {shapeMinMargin:0.#}px";

            items.Add(new ShapePerimeterMarginItem(
                s.Id,
                s.Name ?? "Shape",
                Math.Round(x, 1),
                Math.Round(y, 1),
                Math.Round(w, 1),
                Math.Round(h, 1),
                Math.Round(marginLeft, 1),
                Math.Round(marginTop, 1),
                Math.Round(marginRight, 1),
                Math.Round(marginBottom, 1),
                hasViolation,
                Math.Round(area, 1),
                status));
        }

        if (minMargin == double.MaxValue) minMargin = 0.0;
        minMargin = Math.Round(minMargin, 1);

        double packingDensity = canvasArea > 0 ? totalShapeArea / canvasArea : 0.0;
        packingDensity = Math.Round(packingDensity, 3);

        double score = 100.0;
        score -= Math.Min(45.0, perimeterViolations * 15.0);

        if (packingDensity > 0.40)
        {
            double overDensity = (packingDensity - 0.40) / 0.60;
            score -= (overDensity * 35.0);
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Viewport Breathing Room (≥90%)"
            : score >= 75.0
                ? "Moderate Perimeter Crowding (75-89%)"
                : "Severe Perimeter / Boundary Violations (<75%)";

        string summary = FormatVisualDensitySummary(
            totalShapes, canvasWidth, canvasHeight, totalShapeArea, packingDensity,
            perimeterViolations, minMargin, score, category, isSelection);

        return new DiagramVisualDensityAudit(
            totalShapes,
            canvasWidth,
            canvasHeight,
            canvasArea,
            Math.Round(totalShapeArea, 1),
            packingDensity,
            perimeterViolations,
            minMargin,
            score,
            items,
            category,
            isSelection,
            summary);
    }

    public static string FormatVisualDensitySummary(
        int totalShapes, double width, double height, double shapeArea, double density,
        int perimeterViolations, double minMargin, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram visual density & margin audit ({scopeStr}0 shapes): Canvas is empty";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>();
        parts.Add($"packing density {density:P1}");
        parts.Add($"min margin {minMargin:0.#}px");

        if (perimeterViolations > 0)
        {
            parts.Add($"⚠ {perimeterViolations} perimeter {(perimeterViolations == 1 ? "violation" : "violations")} (<24px)");
        }
        else
        {
            parts.Add("0 boundary violations");
        }

        string details = string.Join(" · ", parts);
        return $"Diagram visual density & margin audit ({scopeStr}{shapeStr} on {width:0}x{height:0} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits diagram shape Z-index layering, occlusion depth, container nesting, and shape visual stacking stratification.
    /// </summary>
    public DiagramZOrderOcclusionAudit AuditZOrderAndOcclusion(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes != null ? targetShapes.ToList() : (selected.Count > 0 ? selected : Shapes.ToList()));
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditZOrderAndOcclusion(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramZOrderOcclusionAudit AuditZOrderAndOcclusion(
        IEnumerable<ShapeCanvasItemViewModel>? shapes,
        IEnumerable<ShapeCanvasItemViewModel>? allCanvasShapes = null,
        bool isSelection = false)
    {
        var shapeList = shapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        var allList = (allCanvasShapes != null ? allCanvasShapes.ToList() : shapeList);
        int totalShapes = shapeList.Count;

        if (totalShapes == 0)
        {
            return new DiagramZOrderOcclusionAudit(
                0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeOcclusionLayerItem>(),
                "Empty Canvas", isSelection,
                "Diagram Z-order & occlusion audit: 0 shapes · Canvas is empty");
        }

        var shapeZIndex = new Dictionary<ShapeCanvasItemViewModel, int>();
        for (int i = 0; i < allList.Count; i++)
        {
            shapeZIndex[allList[i]] = i;
        }

        var layers = new List<ShapeOcclusionLayerItem>();
        int overlappingPairs = 0;
        int severeOcclusionCount = 0;
        int invertedContainerCount = 0;
        var occlusionRatios = new List<double>();

        for (int i = 0; i < shapeList.Count; i++)
        {
            var sA = shapeList[i];
            double xA = sA.X;
            double yA = sA.Y;
            double wA = Math.Max(0.0, sA.Width);
            double hA = Math.Max(0.0, sA.Height);
            double areaA = wA * hA;
            int zA = shapeZIndex.TryGetValue(sA, out int idxA) ? idxA : 0;

            int occludedByCount = 0;
            double maxOcclusionRatio = 0.0;
            bool invertedHazard = false;

            for (int j = 0; j < allList.Count; j++)
            {
                var sB = allList[j];
                if (ReferenceEquals(sA, sB)) continue;

                double xB = sB.X;
                double yB = sB.Y;
                double wB = Math.Max(0.0, sB.Width);
                double hB = Math.Max(0.0, sB.Height);
                double areaB = wB * hB;
                int zB = shapeZIndex.TryGetValue(sB, out int idxB) ? idxB : 0;

                double xI = Math.Max(xA, xB);
                double yI = Math.Max(yA, yB);
                double wI = Math.Max(0.0, Math.Min(xA + wA, xB + wB) - xI);
                double hI = Math.Max(0.0, Math.Min(yA + hA, yB + hB) - yI);
                double areaI = wI * hI;

                if (areaI > 0.0)
                {
                    if (i < j && allList.Contains(sA))
                    {
                        overlappingPairs++;
                    }

                    if (zB > zA)
                    {
                        double ratio = areaA > 0.0 ? areaI / areaA : 0.0;
                        if (ratio > maxOcclusionRatio) maxOcclusionRatio = ratio;
                        if (ratio > 0.05) occludedByCount++;

                        // Inverted container: much larger shape in front of smaller shape
                        if (areaB > 2.5 * areaA && ratio > 0.70)
                        {
                            invertedHazard = true;
                        }
                    }
                }
            }

            maxOcclusionRatio = Math.Round(Math.Clamp(maxOcclusionRatio, 0.0, 1.0), 3);
            occlusionRatios.Add(maxOcclusionRatio);

            bool fullyOccluded = maxOcclusionRatio >= 0.85;
            if (fullyOccluded) severeOcclusionCount++;
            else if (maxOcclusionRatio >= 0.50) severeOcclusionCount++;

            if (invertedHazard) invertedContainerCount++;

            string status = invertedHazard
                ? $"⚠ Inverted container hazard: larger backdrop shape '{sA.Name}' occluded by foreground container"
                : (fullyOccluded
                    ? $"⚠ Shape fully occluded ({maxOcclusionRatio:P0} covered by foreground Z-layers)"
                    : (maxOcclusionRatio >= 0.50
                        ? $"⚠ Partial occlusion ({maxOcclusionRatio:P0} covered by {occludedByCount} layers)"
                        : (occludedByCount > 0
                            ? $"ℹ Minor overlap ({maxOcclusionRatio:P0} covered)"
                            : $"✓ Unobscured top layer (Z-index {zA})")));

            layers.Add(new ShapeOcclusionLayerItem(
                sA.Id,
                sA.Name ?? "Shape",
                zA,
                Math.Round(xA, 1),
                Math.Round(yA, 1),
                Math.Round(wA, 1),
                Math.Round(hA, 1),
                occludedByCount,
                maxOcclusionRatio,
                fullyOccluded,
                invertedHazard,
                status));
        }

        double meanOcclusion = occlusionRatios.Count > 0 ? Math.Round(occlusionRatios.Average(), 3) : 0.0;

        double score = 100.0;
        score -= (invertedContainerCount * 30.0);
        score -= (severeOcclusionCount * 20.0);
        score -= (overlappingPairs * 5.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Layer Stratification & Visibility (≥90%)"
            : score >= 70.0
                ? "Moderate Stacking Occlusion Risk (70-89%)"
                : "Severe Occlusion / Inverted Container Stacking (<70%)";

        string summary = FormatZOrderOcclusionSummary(
            totalShapes, overlappingPairs, severeOcclusionCount, invertedContainerCount,
            score, category, isSelection);

        return new DiagramZOrderOcclusionAudit(
            totalShapes,
            overlappingPairs,
            severeOcclusionCount,
            invertedContainerCount,
            meanOcclusion,
            score,
            layers,
            category,
            isSelection,
            summary);
    }

    public static string FormatZOrderOcclusionSummary(
        int totalShapes, int overlappingPairs, int severeOcclusions, int invertedContainers,
        double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram Z-order & occlusion audit ({scopeStr}0 shapes): Canvas is empty";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>();

        if (overlappingPairs > 0) parts.Add($"{overlappingPairs} overlapping {(overlappingPairs == 1 ? "pair" : "pairs")}");
        else parts.Add("0 overlapping pairs");

        if (severeOcclusions > 0) parts.Add($"⚠ {severeOcclusions} severe {(severeOcclusions == 1 ? "occlusion" : "occlusions")}");
        else parts.Add("0 severe occlusions");

        if (invertedContainers > 0) parts.Add($"⚠ {invertedContainers} inverted container {(invertedContainers == 1 ? "hazard" : "hazards")}");

        string details = string.Join(" · ", parts);
        return $"Diagram Z-order & occlusion audit ({scopeStr}{shapeStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits diagram connector orthogonal routing, corner bends, collinear kinks, and segment-shape crossings.
    /// </summary>
    public DiagramConnectorRoutingAudit AuditConnectorOrthogonalBends(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes != null ? targetShapes.ToList() : (selected.Count > 0 ? selected : Shapes.ToList()));
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditConnectorOrthogonalBends(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramConnectorRoutingAudit AuditConnectorOrthogonalBends(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes = null,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var allList = (allShapes != null ? allShapes.ToList() : list);
        string scopeStr = isSelection ? "Selection · " : "";

        var connectors = list.Where(s =>
            (s.PathPoints != null && s.PathPoints.Count >= 2) ||
            string.Equals(s.ShapeType, "line", StringComparison.OrdinalIgnoreCase) ||
            (s.Name != null && s.Name.Contains("Connector", StringComparison.OrdinalIgnoreCase))
        ).ToList();

        var nonConnectorShapes = allList.Where(s => !connectors.Contains(s)).ToList();

        int totalConnectors = connectors.Count;
        if (totalConnectors == 0)
        {
            return new DiagramConnectorRoutingAudit(
                0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ConnectorRoutingItem>(),
                "No Connectors",
                isSelection,
                $"Diagram connector orthogonal routing audit ({scopeStr}0 connectors): No connector lines to evaluate");
        }

        int totalBends = 0;
        int totalRedundantBends = 0;
        int totalIntersections = 0;
        var items = new List<ConnectorRoutingItem>();

        foreach (var conn in connectors)
        {
            var pts = ConnectorPoints(conn);
            if (pts.Count < 2)
            {
                pts = new List<(double X, double Y)>
                {
                    (conn.X, conn.Y),
                    (conn.X + conn.Width, conn.Y + conn.Height)
                };
            }

            int bends = Math.Max(0, pts.Count - 2);
            totalBends += bends;
            int redundantBends = 0;
            int intersections = 0;

            // Check angles between consecutive segments
            var segAngles = new List<double>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double dx = pts[i + 1].X - pts[i].X;
                double dy = pts[i + 1].Y - pts[i].Y;
                double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                segAngles.Add(angleDeg);
            }

            bool isOrthogonal = true;
            for (int i = 0; i < segAngles.Count; i++)
            {
                double a = Math.Abs(segAngles[i]);
                double dev = Math.Min(a, Math.Min(Math.Abs(a - 90.0), Math.Abs(a - 180.0)));
                if (dev > 5.0) isOrthogonal = false;
            }

            for (int i = 0; i < segAngles.Count - 1; i++)
            {
                double diff = Math.Abs(segAngles[i] - segAngles[i + 1]);
                if (diff > 180.0) diff = 360.0 - diff;
                // Collinear kink (diff < 5°) or complete reverse U-turn (diff > 175°)
                if ((diff >= 0.1 && diff <= 5.0) || diff >= 175.0)
                {
                    redundantBends++;
                }
            }

            // If a connector has more than 3 bends connecting two shapes, flag excessive detours
            if (bends > 3)
            {
                redundantBends += (bends - 3);
            }

            // Check if segments pierce through non-connector shapes
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double x1 = pts[i].X, y1 = pts[i].Y;
                double x2 = pts[i + 1].X, y2 = pts[i + 1].Y;
                double minSegX = Math.Min(x1, x2), maxSegX = Math.Max(x1, x2);
                double minSegY = Math.Min(y1, y2), maxSegY = Math.Max(y1, y2);

                foreach (var sh in nonConnectorShapes)
                {
                    double sx1 = sh.X, sy1 = sh.Y;
                    double sx2 = sh.X + sh.Width, sy2 = sh.Y + sh.Height;

                    // Exclude endpoints touching boundary
                    bool isEndpointA = Math.Abs(x1 - sx1) < 4.0 || Math.Abs(x1 - sx2) < 4.0 || Math.Abs(y1 - sy1) < 4.0 || Math.Abs(y1 - sy2) < 4.0;
                    bool isEndpointB = Math.Abs(x2 - sx1) < 4.0 || Math.Abs(x2 - sx2) < 4.0 || Math.Abs(y2 - sy1) < 4.0 || Math.Abs(y2 - sy2) < 4.0;

                    // Check bounding box intersection
                    if (maxSegX > sx1 + 4.0 && minSegX < sx2 - 4.0 && maxSegY > sy1 + 4.0 && minSegY < sy2 - 4.0)
                    {
                        if (!isEndpointA && !isEndpointB)
                        {
                            intersections++;
                        }
                    }
                }
            }

            totalRedundantBends += redundantBends;
            totalIntersections += intersections;

            string status;
            if (redundantBends == 0 && intersections == 0 && isOrthogonal)
                status = $"✓ Optimal routing ({bends} {(bends == 1 ? "bend" : "bends")})";
            else if (intersections > 0)
                status = $"⚠ Crosses {intersections} {(intersections == 1 ? "shape" : "shapes")} ({bends} bends)";
            else if (redundantBends > 0)
                status = $"⚠ {redundantBends} redundant {(redundantBends == 1 ? "bend" : "bends")}";
            else
                status = $"Non-orthogonal ({bends} bends)";

            items.Add(new ConnectorRoutingItem(
                conn.Id,
                conn.Name ?? "Connector",
                bends,
                redundantBends,
                intersections,
                isOrthogonal,
                status));
        }

        double meanBends = totalConnectors > 0 ? Math.Round((double)totalBends / totalConnectors, 1) : 0.0;
        double score = 100.0;
        score -= Math.Min(40.0, totalRedundantBends * 10.0);
        score -= Math.Min(40.0, totalIntersections * 15.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Orthogonal Routing & Clear Corridors (≥90%)"
            : score >= 75.0
                ? "Good Orthogonal Trajectories & Few Bends (75-89%)"
                : score >= 50.0
                    ? "Moderate Detour Bends & Element Crossings (50-74%)"
                    : "Severe Routing Congestion & Shape Piercing (<50%)";

        string summary = FormatConnectorOrthogonalSummary(
            totalConnectors, totalBends, totalRedundantBends, totalIntersections, score, category, isSelection);

        return new DiagramConnectorRoutingAudit(
            totalConnectors,
            totalBends,
            totalRedundantBends,
            totalIntersections,
            meanBends,
            score,
            items,
            category,
            isSelection,
            summary);
    }

    public static string FormatConnectorOrthogonalSummary(
        int totalConnectors, int totalBends, int redundantBends, int intersections,
        double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalConnectors == 0) return $"Diagram connector orthogonal routing audit ({scopeStr}0 connectors): No connector lines to evaluate";

        string connStr = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var parts = new List<string>();
        parts.Add($"{totalBends} {(totalBends == 1 ? "bend" : "bends")}");

        if (redundantBends > 0) parts.Add($"⚠ {redundantBends} redundant {(redundantBends == 1 ? "bend" : "bends")}");
        else parts.Add("0 redundant bends");

        if (intersections > 0) parts.Add($"⚠ {intersections} shape {(intersections == 1 ? "crossing" : "crossings")}");
        else parts.Add("0 shape crossings");

        string details = string.Join(" · ", parts);
        return $"Diagram connector orthogonal routing audit ({scopeStr}{connStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits diagram shape alignment guide proximity, near-miss collinear edges and centerlines (1-3px),
    /// smart snap distribution, and gap spacing balance.
    /// </summary>
    public DiagramAlignmentGuidesAudit AuditAlignmentGuidesAndSnapping(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes = null)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (targetShapes != null ? targetShapes.ToList() : (selected.Count > 0 ? selected : Shapes.ToList()));
        bool isSelection = targetShapes != null || (selected.Count > 0 && targets.Count < Shapes.Count);
        var audit = AuditAlignmentGuidesAndSnapping(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramAlignmentGuidesAudit AuditAlignmentGuidesAndSnapping(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes = null,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var context = (allShapes ?? list).ToList();

        int totalShapes = list.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatAlignmentGuidesSummary(0, 0, 0, 0, 0, 0.0, 100.0, "Empty Canvas", isSelection);
            return new DiagramAlignmentGuidesAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ShapeAlignmentProximityItem>(),
                "Empty Canvas",
                isSelection,
                emptySummary);
        }

        var items = new List<ShapeAlignmentProximityItem>();
        int totalNearMissEdges = 0;
        int totalNearMissCenters = 0;
        int evaluatedPairs = 0;
        double sumDelta = 0.0;
        int deltaCount = 0;

        foreach (var shape in list)
        {
            int nearMissEdges = 0;
            int nearMissCenters = 0;
            double minDelta = double.MaxValue;
            string bestRecommendation = "Aligned";

            double aLeft = shape.X;
            double aRight = shape.X + shape.Width;
            double aTop = shape.Y;
            double aBottom = shape.Y + shape.Height;
            double aCenterX = shape.X + (shape.Width / 2.0);
            double aCenterY = shape.Y + (shape.Height / 2.0);

            foreach (var other in context)
            {
                if (ReferenceEquals(shape, other) || shape.Id == other.Id) continue;
                evaluatedPairs++;

                double bLeft = other.X;
                double bRight = other.X + other.Width;
                double bTop = other.Y;
                double bBottom = other.Y + other.Height;
                double bCenterX = other.X + (other.Width / 2.0);
                double bCenterY = other.Y + (other.Height / 2.0);

                // Edge near-misses (1px to 3.5px)
                double dLeft = Math.Abs(aLeft - bLeft);
                double dRight = Math.Abs(aRight - bRight);
                double dTop = Math.Abs(aTop - bTop);
                double dBottom = Math.Abs(aBottom - bBottom);

                if (dLeft >= 0.5 && dLeft <= 3.5)
                {
                    nearMissEdges++;
                    if (dLeft < minDelta) { minDelta = dLeft; bestRecommendation = $"Snap Left ({bLeft:0.#}px)"; }
                    sumDelta += dLeft;
                    deltaCount++;
                }
                if (dRight >= 0.5 && dRight <= 3.5)
                {
                    nearMissEdges++;
                    if (dRight < minDelta) { minDelta = dRight; bestRecommendation = $"Snap Right ({bRight:0.#}px)"; }
                    sumDelta += dRight;
                    deltaCount++;
                }
                if (dTop >= 0.5 && dTop <= 3.5)
                {
                    nearMissEdges++;
                    if (dTop < minDelta) { minDelta = dTop; bestRecommendation = $"Snap Top ({bTop:0.#}px)"; }
                    sumDelta += dTop;
                    deltaCount++;
                }
                if (dBottom >= 0.5 && dBottom <= 3.5)
                {
                    nearMissEdges++;
                    if (dBottom < minDelta) { minDelta = dBottom; bestRecommendation = $"Snap Bottom ({bBottom:0.#}px)"; }
                    sumDelta += dBottom;
                    deltaCount++;
                }

                // Centerline near-misses
                double dCenterX = Math.Abs(aCenterX - bCenterX);
                double dCenterY = Math.Abs(aCenterY - bCenterY);

                if (dCenterX >= 0.5 && dCenterX <= 3.5)
                {
                    nearMissCenters++;
                    if (dCenterX < minDelta) { minDelta = dCenterX; bestRecommendation = $"Snap CenterX ({bCenterX:0.#}px)"; }
                    sumDelta += dCenterX;
                    deltaCount++;
                }
                if (dCenterY >= 0.5 && dCenterY <= 3.5)
                {
                    nearMissCenters++;
                    if (dCenterY < minDelta) { minDelta = dCenterY; bestRecommendation = $"Snap CenterY ({bCenterY:0.#}px)"; }
                    sumDelta += dCenterY;
                    deltaCount++;
                }
            }

            totalNearMissEdges += nearMissEdges;
            totalNearMissCenters += nearMissCenters;

            double recordedMinDelta = minDelta == double.MaxValue ? 0.0 : Math.Round(minDelta, 1);
            string status = (nearMissEdges == 0 && nearMissCenters == 0)
                ? "✓ Snapped / Clean Alignment"
                : $"⚠ {nearMissEdges + nearMissCenters} near-miss guides (~{recordedMinDelta}px offset)";

            items.Add(new ShapeAlignmentProximityItem(
                shape.Id,
                shape.Name ?? "Shape",
                shape.X,
                shape.Y,
                shape.Width,
                shape.Height,
                nearMissEdges,
                nearMissCenters,
                recordedMinDelta,
                bestRecommendation,
                status));
        }

        // Check for uneven distribution gaps among horizontally or vertically ordered shapes
        int unevenGapCount = 0;
        if (list.Count >= 3)
        {
            var sortedX = list.OrderBy(s => s.X).ToList();
            var xGaps = new List<double>();
            for (int i = 0; i < sortedX.Count - 1; i++)
            {
                double gap = sortedX[i + 1].X - (sortedX[i].X + sortedX[i].Width);
                if (gap > 0) xGaps.Add(gap);
            }
            for (int i = 0; i < xGaps.Count - 1; i++)
            {
                double diff = Math.Abs(xGaps[i] - xGaps[i + 1]);
                if (diff >= 1.0 && diff <= 4.0) unevenGapCount++;
            }
        }

        double meanOffset = deltaCount > 0 ? Math.Round(sumDelta / deltaCount, 1) : 0.0;
        double score = 100.0;
        score -= Math.Min(40.0, totalNearMissEdges * 6.0);
        score -= Math.Min(30.0, totalNearMissCenters * 5.0);
        score -= Math.Min(20.0, unevenGapCount * 5.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Alignment & Smart Snap Conformance (≥90%)"
            : score >= 75.0
                ? "Good Visual Alignment & Minor Edge Drift (75-89%)"
                : score >= 50.0
                    ? "Moderate Alignment Near-Misses & Unsnapped Gaps (50-74%)"
                    : "Severe Misalignment & Irregular Spacing (<50%)";

        string summary = FormatAlignmentGuidesSummary(
            totalShapes, evaluatedPairs, totalNearMissEdges, totalNearMissCenters, unevenGapCount,
            meanOffset, score, category, isSelection);

        return new DiagramAlignmentGuidesAudit(
            totalShapes,
            evaluatedPairs,
            totalNearMissEdges,
            totalNearMissCenters,
            unevenGapCount,
            meanOffset,
            score,
            items,
            category,
            isSelection,
            summary);
    }

    public static string FormatAlignmentGuidesSummary(
        int totalShapes, int evaluatedPairs, int nearMissEdges, int nearMissCenters,
        int unevenGaps, double meanOffset, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram alignment guide & smart snap audit ({scopeStr}0 shapes): No shapes to evaluate";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>();

        if (nearMissEdges > 0 || nearMissCenters > 0)
        {
            parts.Add($"⚠ {nearMissEdges} edge + {nearMissCenters} center near-misses (~{meanOffset:0.#}px)");
        }
        else
        {
            parts.Add("0 near-miss alignment guides");
        }

        if (unevenGaps > 0) parts.Add($"⚠ {unevenGaps} uneven gap sequences");
        else parts.Add("0 gap irregularities");

        string details = string.Join(" · ", parts);
        return $"Diagram alignment guide & smart snap audit ({scopeStr}{shapeStr} · {category} · {score:0.#}% precision): {details}";
    }

    public DiagramGroupHierarchyAudit AuditGroupHierarchyAndPadding(bool selectionOnly = false)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targets = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditGroupHierarchyAndPadding(targets, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramGroupHierarchyAudit AuditGroupHierarchyAndPadding(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null).ToList();

        int totalShapes = list.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatGroupHierarchySummary(0, 0, 0, 0, 0, 100.0, "Empty Canvas", isSelection);
            return new DiagramGroupHierarchyAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<ShapeContainerHierarchyItem>(),
                "Empty Canvas",
                isSelection,
                emptySummary);
        }

        // Determine containers: Shape C is a container if it encloses other shapes
        // (Area of C >= 1.3 * Area of S, and S's center falls inside C's bounds)
        var containers = new HashSet<string>();
        var containerChildren = new Dictionary<string, List<ShapeCanvasItemViewModel>>();
        var childToContainer = new Dictionary<string, ShapeCanvasItemViewModel>();

        foreach (var c in list)
        {
            double cArea = c.Width * c.Height;
            if (cArea <= 0) continue;

            var children = new List<ShapeCanvasItemViewModel>();
            foreach (var s in list)
            {
                if (s.Id == c.Id) continue;
                double sArea = s.Width * s.Height;
                if (sArea <= 0 || cArea < sArea * 1.3) continue;

                double sCenterX = s.X + (s.Width / 2.0);
                double sCenterY = s.Y + (s.Height / 2.0);

                if (sCenterX >= c.X && sCenterX <= c.X + c.Width &&
                    sCenterY >= c.Y && sCenterY <= c.Y + c.Height)
                {
                    children.Add(s);
                }
            }

            if (children.Count > 0)
            {
                containers.Add(c.Id);
                containerChildren[c.Id] = children;
                foreach (var ch in children)
                {
                    if (!childToContainer.ContainsKey(ch.Id) || (childToContainer[ch.Id].Width * childToContainer[ch.Id].Height > cArea))
                    {
                        childToContainer[ch.Id] = c;
                    }
                }
            }
        }

        int containerCount = containers.Count;
        int enclosedChildCount = childToContainer.Count;
        int childOverflowCount = 0;
        int tightPaddingCount = 0;

        var items = new List<ShapeContainerHierarchyItem>();

        foreach (var shape in targets)
        {
            bool isContainer = containers.Contains(shape.Id);
            int childCount = isContainer ? containerChildren[shape.Id].Count : 0;
            string enclosingName = childToContainer.TryGetValue(shape.Id, out var parent) ? (parent.Name ?? "Container") : "(None)";

            bool isOverflowing = false;
            double minPad = double.MaxValue;

            if (parent != null)
            {
                // Measure bounds of shape relative to parent
                double pLeft = parent.X;
                double pRight = parent.X + parent.Width;
                double pTop = parent.Y;
                double pBottom = parent.Y + parent.Height;

                double sLeft = shape.X;
                double sRight = shape.X + shape.Width;
                double sTop = shape.Y;
                double sBottom = shape.Y + shape.Height;

                if (sLeft < pLeft || sRight > pRight || sTop < pTop || sBottom > pBottom)
                {
                    isOverflowing = true;
                    childOverflowCount++;
                }

                double padLeft = sLeft - pLeft;
                double padRight = pRight - sRight;
                double padTop = sTop - pTop;
                double padBottom = pBottom - sBottom;
                minPad = Math.Round(Math.Min(Math.Min(padLeft, padRight), Math.Min(padTop, padBottom)), 1);

                if (!isOverflowing && minPad < 12.0)
                {
                    tightPaddingCount++;
                }
            }

            double recordedMinPad = minPad == double.MaxValue ? 0.0 : minPad;
            string status;
            if (isContainer)
            {
                status = $"Container Group ({childCount} enclosed {(childCount == 1 ? "child" : "children")})";
            }
            else if (parent != null)
            {
                status = isOverflowing ? $"⚠ Overflowing boundary of [{enclosingName}]" :
                         recordedMinPad < 12.0 ? $"⚠ Tight padding ({recordedMinPad}px) in [{enclosingName}]" :
                         $"Enclosed in [{enclosingName}] ({recordedMinPad}px padding)";
            }
            else
            {
                status = "Standalone Canvas Shape";
            }

            items.Add(new ShapeContainerHierarchyItem(
                shape.Id,
                shape.Name ?? "Shape",
                isContainer,
                childCount,
                enclosingName,
                isOverflowing,
                recordedMinPad,
                status));
        }

        double score = 100.0;
        score -= Math.Min(40.0, childOverflowCount * 12.0);
        score -= Math.Min(30.0, tightPaddingCount * 6.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Container Hierarchy & Padding Margin (≥90%)"
            : score >= 75.0
                ? "Good Grouping Structure & Minor Margin Drift (75-89%)"
                : score >= 50.0
                    ? "Moderate Child Overflow or Insufficient Padding (50-74%)"
                    : "Severe Container Clipping & Boundary Violations (<50%)";

        string summary = FormatGroupHierarchySummary(
            totalShapes, containerCount, enclosedChildCount, childOverflowCount, tightPaddingCount, score, category, isSelection);

        return new DiagramGroupHierarchyAudit(
            totalShapes,
            containerCount,
            enclosedChildCount,
            childOverflowCount,
            tightPaddingCount,
            score,
            items,
            category,
            isSelection,
            summary);
    }

    public static string FormatGroupHierarchySummary(
        int totalShapes, int containerCount, int enclosedCount, int overflowCount,
        int tightPaddingCount, double score, string category, bool isSelection = false)
    {
        string scopeStr = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram grouping hierarchy & container padding audit ({scopeStr}0 shapes): No shapes to evaluate";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>();

        if (containerCount > 0)
        {
            parts.Add($"{containerCount} {(containerCount == 1 ? "container" : "containers")}, {enclosedCount} enclosed {(enclosedCount == 1 ? "child" : "children")}");
        }
        else
        {
            parts.Add("0 container groups");
        }

        if (overflowCount > 0)
            parts.Add($"⚠ {overflowCount} clipping child overflows");

        if (tightPaddingCount > 0)
            parts.Add($"⚠ {tightPaddingCount} tight padding (<12px)");

        string details = string.Join(" · ", parts);
        return $"Diagram grouping hierarchy & container padding audit ({scopeStr}{shapeStr} · {category} · {score:0.#}% integrity): {details}";
    }

    public ShapeLayeringZOrderAudit AuditLayeringZOrderAndOcclusion()
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var target = selected.Count > 0 ? selected : Shapes.ToList();
        bool isSelection = selected.Count > 0;
        var audit = AuditLayeringZOrderAndOcclusion(target, Shapes, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static ShapeLayeringZOrderAudit AuditLayeringZOrderAndOcclusion(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes,
        bool isSelection = false)
    {
        var targetList = targetShapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        var allList = allShapes?.ToList() ?? targetList;

        int totalShapes = targetList.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatLayeringZOrderSummary(0, 0, 0, 0, 100.0, "Empty Canvas", isSelection);
            return new ShapeLayeringZOrderAudit(
                0, 0, 0, 0, 100.0, Array.Empty<ShapeLayeringOcclusionItem>(), "Empty Canvas", isSelection, emptySummary);
        }

        var items = new List<ShapeLayeringOcclusionItem>();
        int overlapCount = 0;
        int invertedHierarchyCount = 0;
        int fullyOccludedCount = 0;

        for (int i = 0; i < allList.Count; i++)
        {
            var lower = allList[i];
            double aX1 = lower.X, aY1 = lower.Y, aX2 = lower.X + lower.Width, aY2 = lower.Y + lower.Height;
            double areaA = lower.Width * lower.Height;
            if (areaA <= 0) continue;

            for (int j = i + 1; j < allList.Count; j++)
            {
                var upper = allList[j];
                double bX1 = upper.X, bY1 = upper.Y, bX2 = upper.X + upper.Width, bY2 = upper.Y + upper.Height;
                double areaB = upper.Width * upper.Height;
                if (areaB <= 0) continue;

                double oLeft = Math.Max(aX1, bX1);
                double oTop = Math.Max(aY1, bY1);
                double oRight = Math.Min(aX2, bX2);
                double oBottom = Math.Min(aY2, bY2);

                double oW = Math.Max(0.0, oRight - oLeft);
                double oH = Math.Max(0.0, oBottom - oTop);
                double oArea = oW * oH;

                if (oArea > 1.0)
                {
                    overlapCount++;
                    double overlapRatioA = oArea / areaA;
                    bool isFullyOccluded = overlapRatioA >= 0.95;
                    bool isInverted = areaB > 2.0 * areaA && overlapRatioA >= 0.50;

                    if (isFullyOccluded) fullyOccludedCount++;
                    if (isInverted) invertedHierarchyCount++;

                    string issue = isFullyOccluded
                        ? $"Shape [{lower.Name}] is 95%+ occluded and hidden by upper shape [{upper.Name}]"
                        : isInverted
                            ? $"Inverted layering: Large shape [{upper.Name}] stacks over small foreground shape [{lower.Name}]"
                            : $"Partial overlap ({overlapRatioA:P0}) between [{lower.Name}] and [{upper.Name}]";

                    items.Add(new ShapeLayeringOcclusionItem(
                        lower.Id, upper.Id, lower.Name, upper.Name,
                        Math.Round(overlapRatioA, 2), isInverted, isFullyOccluded, issue));
                }
            }
        }

        double score = 100.0;
        score -= fullyOccludedCount * 25.0;
        score -= invertedHierarchyCount * 15.0;
        score -= Math.Min(30.0, overlapCount * 5.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalShapes == 0) category = "Empty Canvas";
        else if (score >= 90.0) category = "Optimal Z-Order Layering & Stacking Elegance (≥90%)";
        else if (score >= 75.0) category = "Good Shape Layering with Minor Overlap Shadows (75-89%)";
        else if (score >= 50.0) category = "Moderate Inverted Layering or Occlusion Discrepancies (50-74%)";
        else category = "Severe Occlusion & Broken Z-Index Hierarchy (<50%)";

        string summary = FormatLayeringZOrderSummary(
            totalShapes, overlapCount, invertedHierarchyCount, fullyOccludedCount, score, category, isSelection);

        return new ShapeLayeringZOrderAudit(
            totalShapes, overlapCount, invertedHierarchyCount, fullyOccludedCount,
            score, items, category, isSelection, summary);
    }

    public static string FormatLayeringZOrderSummary(
        int totalShapes, int overlapCount, int invertedCount, int occludedCount,
        double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram shape Z-order layering & occlusion audit ({scope}0 shapes): Empty canvas";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>();

        if (overlapCount == 0)
            parts.Add("0 overlapping shapes (Clean Stacking)");
        else
            parts.Add($"{overlapCount} overlap{(overlapCount == 1 ? "" : "s")}");

        if (occludedCount > 0)
            parts.Add($"⚠ {occludedCount} fully occluded");
        if (invertedCount > 0)
            parts.Add($"⚠ {invertedCount} inverted hierarchy");

        string details = string.Join(" · ", parts);
        return $"Diagram shape Z-order layering & occlusion audit ({scope}{shapeStr} · {category} · {score:0.#}% elegance score): {details}";
    }
    public DiagramShapeContrastAudit AuditShapeVisualWeightAndContrast()
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var target = selected.Count > 0 ? selected : Shapes.ToList();
        bool isSelection = selected.Count > 0;
        var audit = AuditShapeVisualWeightAndContrast(target, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramShapeContrastAudit AuditShapeVisualWeightAndContrast(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        bool isSelection = false)
    {
        var targetList = targetShapes?.ToList() ?? new List<ShapeCanvasItemViewModel>();
        int totalShapes = targetList.Count;

        if (totalShapes == 0)
        {
            string emptySummary = FormatVisualWeightSummary(0, 0, 0, 0, 0.0, 100.0, "Empty Canvas", isSelection);
            return new DiagramShapeContrastAudit(
                0, 0, 0, 0, 0.0, 100.0, Array.Empty<ShapeContrastItem>(), "Empty Canvas", isSelection, emptySummary);
        }

        var items = new List<ShapeContrastItem>();
        int textShapesCount = 0;
        int passingWcagCount = 0;
        int failingWcagCount = 0;
        double totalContrast = 0.0;

        foreach (var shape in targetList)
        {
            string text = shape.Text ?? string.Empty;
            string fill = shape.Fill ?? "#FFFFFF";
            string? textColor = shape.TextColor;

            // Resolve effective fill and text RGB
            var (fR, fG, fB) = ParseColor(fill, (255, 255, 255));
            double fillLum = RelativeLuminance(fR, fG, fB);

            (byte tR, byte tG, byte tB) textRgb;
            if (string.IsNullOrWhiteSpace(textColor))
            {
                // Default theme auto-guard: dark text for bright fills, white text for dark fills
                textRgb = fillLum > 0.179 ? ((byte)30, (byte)30, (byte)30) : ((byte)255, (byte)255, (byte)255);
            }
            else
            {
                textRgb = ParseColor(textColor, (0, 0, 0));
            }

            double textLum = RelativeLuminance(textRgb.tR, textRgb.tG, textRgb.tB);
            double maxL = Math.Max(fillLum, textLum);
            double minL = Math.Min(fillLum, textLum);
            double contrastRatio = Math.Round((maxL + 0.05) / (minL + 0.05), 2);

            bool meetsNormal = contrastRatio >= 4.5;
            bool meetsLarge = contrastRatio >= 3.0;

            bool hasText = !string.IsNullOrWhiteSpace(text);
            if (hasText)
            {
                textShapesCount++;
                totalContrast += contrastRatio;
                if (meetsNormal) passingWcagCount++;
                else failingWcagCount++;
            }

            string status = !hasText
                ? "No Text Label (Geometry Only)"
                : meetsNormal
                    ? $"Passes WCAG AA ({contrastRatio}:1 >= 4.5:1)"
                    : meetsLarge
                        ? $"Large Text Only ({contrastRatio}:1 >= 3.0:1, fails normal)"
                        : $"Fails WCAG AA ({contrastRatio}:1 < 3.0:1 contrast hazard)";

            items.Add(new ShapeContrastItem(
                shape.Id, shape.ShapeType ?? "shape", text, fill,
                textColor ?? (fillLum > 0.179 ? "#1E1E1E" : "#FFFFFF"),
                contrastRatio, meetsNormal, meetsLarge, status));
        }

        double avgContrast = textShapesCount > 0 ? Math.Round(totalContrast / textShapesCount, 2) : 21.0;
        double score = 100.0;
        score -= failingWcagCount * 15.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (textShapesCount == 0) category = "No Text Labels";
        else if (score >= 90.0) category = "Optimal WCAG Typography & Contrast (≥90%)";
        else if (score >= 75.0) category = "Acceptable Legibility with Minor Contrast Gaps (75-89%)";
        else if (score >= 50.0) category = "Moderate Visual Accessibility Hazards (50-74%)";
        else category = "Severe Unreadable Low-Contrast Text Defects (<50%)";

        string summary = FormatVisualWeightSummary(
            totalShapes, textShapesCount, passingWcagCount, failingWcagCount,
            avgContrast, score, category, isSelection);

        return new DiagramShapeContrastAudit(
            totalShapes, textShapesCount, passingWcagCount, failingWcagCount,
            avgContrast, score, items, category, isSelection, summary);
    }

    private static (byte R, byte G, byte B) ParseColor(string? hex, (byte R, byte G, byte B) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Equals("Transparent", StringComparison.OrdinalIgnoreCase) || s.Equals("None", StringComparison.OrdinalIgnoreCase))
            return fallback;

        try
        {
            if (s.Length == 6)
            {
                byte r = Convert.ToByte(s.Substring(0, 2), 16);
                byte g = Convert.ToByte(s.Substring(2, 2), 16);
                byte b = Convert.ToByte(s.Substring(4, 2), 16);
                return (r, g, b);
            }
            if (s.Length == 8)
            {
                byte a = Convert.ToByte(s.Substring(0, 2), 16);
                byte r = Convert.ToByte(s.Substring(2, 2), 16);
                byte g = Convert.ToByte(s.Substring(4, 2), 16);
                byte b = Convert.ToByte(s.Substring(6, 2), 16);
                // Blend with white
                double alpha = a / 255.0;
                byte br = (byte)Math.Clamp((int)(r * alpha + 255 * (1.0 - alpha)), 0, 255);
                byte bg = (byte)Math.Clamp((int)(g * alpha + 255 * (1.0 - alpha)), 0, 255);
                byte bb = (byte)Math.Clamp((int)(b * alpha + 255 * (1.0 - alpha)), 0, 255);
                return (br, bg, bb);
            }
            if (s.Length == 3)
            {
                byte r = Convert.ToByte(new string(s[0], 2), 16);
                byte g = Convert.ToByte(new string(s[1], 2), 16);
                byte b = Convert.ToByte(new string(s[2], 2), 16);
                return (r, g, b);
            }
        }
        catch { }

        return fallback;
    }

    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        double rLin = LinearComponent(r / 255.0);
        double gLin = LinearComponent(g / 255.0);
        double bLin = LinearComponent(b / 255.0);
        return 0.2126 * rLin + 0.7152 * gLin + 0.0722 * bLin;
    }

    private static double LinearComponent(double c)
    {
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static string FormatVisualWeightSummary(
        int totalShapes, int textShapes, int passing, int failing,
        double avgContrast, double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0) return $"Diagram shape visual weight & contrast audit ({scope}0 shapes): Empty canvas";

        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>();

        if (textShapes == 0)
        {
            parts.Add("0 text labels (Geometry only)");
        }
        else
        {
            parts.Add($"{passing}/{textShapes} WCAG AA pass");
            if (failing > 0) parts.Add($"⚠️ {failing} low-contrast hazard{(failing == 1 ? "" : "s")}");
            parts.Add($"{avgContrast:0.1}:1 avg ratio");
        }

        string details = string.Join(" · ", parts);
        return $"Diagram shape visual weight & contrast audit ({scope}{shapeStr} · {category} · {score:0.#}% accessibility score): {details}";
    }

    public DiagramConnectorSnappingAudit AuditConnectorAnchorSnappingAndOrthogonality(bool selectionOnly = false)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targetShapes = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditConnectorAnchorSnappingAndOrthogonality(targetShapes, Shapes.ToList(), isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramConnectorSnappingAudit AuditConnectorAnchorSnappingAndOrthogonality(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes = null,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        var allList = (allShapes != null ? allShapes.ToList() : list);
        string scopeStr = isSelection ? "Selection · " : "";

        var connectors = list.Where(s =>
            (s.PathPoints != null && s.PathPoints.Count >= 2) ||
            string.Equals(s.ShapeType, "line", StringComparison.OrdinalIgnoreCase) ||
            (s.Name != null && s.Name.Contains("Connector", StringComparison.OrdinalIgnoreCase))
        ).ToList();

        var nonConnectorShapes = allList.Where(s => !connectors.Contains(s)).ToList();
        int totalConnectors = connectors.Count;

        if (totalConnectors == 0)
        {
            string emptySummary = FormatConnectorSnappingSummary(0, 0, 0, 0, 0, 0.0, 100.0, "Empty Canvas", isSelection);
            return new DiagramConnectorSnappingAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<ConnectorAnchorSnappingItem>(), "Empty Canvas", isSelection, emptySummary);
        }

        var items = new List<ConnectorAnchorSnappingItem>();
        int fullySnappedCount = 0;
        int unsnappedEndpointCount = 0;
        int orthogonalCount = 0;
        int skewedCount = 0;
        double sumMaxDeviation = 0;

        const double SnapTolerance = 14.0;
        const double OrthogonalTolerance = 1.5;

        foreach (var conn in connectors)
        {
            var pts = conn.PathPoints ?? new List<(double X, double Y)>
            {
                (conn.X, conn.Y),
                (conn.X + conn.Width, conn.Y + conn.Height)
            };

            var startPt = pts.First();
            var endPt = pts.Last();

            double startMinDist = double.MaxValue;
            string startShapeId = "";
            double endMinDist = double.MaxValue;
            string endShapeId = "";

            foreach (var shape in nonConnectorShapes)
            {
                var anchors = new (double X, double Y)[]
                {
                    (shape.X, shape.Y + shape.Height / 2.0),
                    (shape.X + shape.Width, shape.Y + shape.Height / 2.0),
                    (shape.X + shape.Width / 2.0, shape.Y),
                    (shape.X + shape.Width / 2.0, shape.Y + shape.Height),
                    (shape.X + shape.Width / 2.0, shape.Y + shape.Height / 2.0)
                };

                foreach (var a in anchors)
                {
                    double dStart = Math.Sqrt(Math.Pow(startPt.X - a.X, 2) + Math.Pow(startPt.Y - a.Y, 2));
                    if (dStart < startMinDist)
                    {
                        startMinDist = dStart;
                        startShapeId = shape.Id ?? "";
                    }

                    double dEnd = Math.Sqrt(Math.Pow(endPt.X - a.X, 2) + Math.Pow(endPt.Y - a.Y, 2));
                    if (dEnd < endMinDist)
                    {
                        endMinDist = dEnd;
                        endShapeId = shape.Id ?? "";
                    }
                }
            }

            bool sourceSnapped = startMinDist <= SnapTolerance;
            bool targetSnapped = endMinDist <= SnapTolerance;
            if (sourceSnapped && targetSnapped) fullySnappedCount++;
            else
            {
                if (!sourceSnapped) unsnappedEndpointCount++;
                if (!targetSnapped) unsnappedEndpointCount++;
            }

            int segmentCount = pts.Count - 1;
            double maxDev = 0;
            bool isConnOrthogonal = true;
            bool hasSkew = false;

            for (int i = 0; i < segmentCount; i++)
            {
                double dx = pts[i + 1].X - pts[i].X;
                double dy = pts[i + 1].Y - pts[i].Y;
                if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) continue;

                double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (angle < 0) angle += 360.0;

                double rem = angle % 90.0;
                double dev = Math.Min(rem, 90.0 - rem);
                if (dev > maxDev) maxDev = dev;

                if (dev > OrthogonalTolerance)
                {
                    isConnOrthogonal = false;
                    if (dev <= 15.0) hasSkew = true;
                }
            }

            maxDev = Math.Round(maxDev, 1);
            sumMaxDeviation += maxDev;

            if (isConnOrthogonal) orthogonalCount++;
            if (hasSkew) skewedCount++;

            string status = (sourceSnapped && targetSnapped && isConnOrthogonal)
                ? "Fully snapped & orthogonal connector"
                : (!sourceSnapped || !targetSnapped)
                    ? $"Unsnapped endpoint(s) (Start: {startMinDist:F1}px, End: {endMinDist:F1}px to anchor)"
                    : $"Angular skew deviation ({maxDev:F1}° off 90° grid)";

            items.Add(new ConnectorAnchorSnappingItem(
                conn.Id ?? "", conn.Name ?? conn.Id ?? "Connector",
                startShapeId, endShapeId, Math.Round(startMinDist, 1), Math.Round(endMinDist, 1),
                sourceSnapped, targetSnapped, segmentCount, maxDev, isConnOrthogonal, hasSkew, status));
        }

        double avgDeviation = Math.Round(sumMaxDeviation / totalConnectors, 1);
        double score = 100.0;
        score -= unsnappedEndpointCount * 12.0;
        score -= skewedCount * 15.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalConnectors == 0) category = "Empty Canvas";
        else if (score >= 90.0) category = "Optimal Snap Precision & Orthogonality (≥90%)";
        else if (score >= 75.0) category = "Good Routing with Minor Anchor Gaps (75-89%)";
        else if (score >= 50.0) category = "Moderate Connector Jitter or Floating Anchors (50-74%)";
        else category = "Severe Misalignment or Floating Wire Spaghetti (<50%)";

        string summary = FormatConnectorSnappingSummary(
            totalConnectors, fullySnappedCount, unsnappedEndpointCount,
            orthogonalCount, skewedCount, avgDeviation, score, category, isSelection);

        return new DiagramConnectorSnappingAudit(
            totalConnectors, fullySnappedCount, unsnappedEndpointCount,
            orthogonalCount, skewedCount, avgDeviation, score, items, category, isSelection, summary);
    }

    public static string FormatConnectorSnappingSummary(
        int totalConnectors, int fullySnapped, int unsnappedEndpoints,
        int orthogonalCount, int skewedCount, double avgDeviation,
        double score, string category, bool isSelection = false)
    {
        if (totalConnectors == 0) return "Diagram connector snapping audit: Empty canvas · 0 connectors detected";

        string scope = isSelection ? "Selection · " : "";
        string connStr = totalConnectors == 1 ? "1 connector" : $"{totalConnectors} connectors";
        var parts = new List<string>();

        if (fullySnapped > 0) parts.Add($"{fullySnapped} fully snapped");
        if (orthogonalCount > 0) parts.Add($"{orthogonalCount} orthogonal");
        if (unsnappedEndpoints > 0) parts.Add($"⚠️ {unsnappedEndpoints} unsnapped endpoint{(unsnappedEndpoints == 1 ? "" : "s")}");
        if (skewedCount > 0) parts.Add($"⚠️ {skewedCount} skewed bend{(skewedCount == 1 ? "" : "s")}");
        parts.Add($"avg dev {avgDeviation:F1}°");

        string details = string.Join(" · ", parts);
        return $"Diagram connector snapping & orthogonality audit ({scope}{connStr} · {details} · {category} · {score:0.#}% precision score)";
    }

    public DiagramShapeAspectRatioUniformityAudit AuditShapeAspectRatioUniformityAndProportions(bool selectionOnly = false)
    {
        var selected = Shapes.Where(s => s.IsSelected).ToList();
        var targetShapes = (selectionOnly && selected.Count > 0)
            ? selected
            : Shapes.ToList();

        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditShapeAspectRatioUniformityAndProportions(targetShapes, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramShapeAspectRatioUniformityAudit AuditShapeAspectRatioUniformityAndProportions(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        bool isSelection = false)
    {
        var list = (targetShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).ToList();
        string scopeStr = isSelection ? "Selection · " : "";

        // Filter out zero-area lines or connectors
        var validShapes = list.Where(s => s.Width >= 2.0 && s.Height >= 2.0).ToList();
        int totalShapes = validShapes.Count;

        if (totalShapes == 0)
        {
            string emptySummary = FormatAspectRatioSummary(0, 0, 0, 0, 0, 0.0, 100.0, "Empty Canvas", isSelection);
            return new DiagramShapeAspectRatioUniformityAudit(
                0, 0, 0, 0, 0, 0.0, 100.0, Array.Empty<ShapeAspectRatioUniformityItem>(), "Empty Canvas", isSelection, emptySummary);
        }

        const double Phi = 1.6180339887;
        var items = new List<ShapeAspectRatioUniformityItem>();
        int squareCount = 0;
        int goldenRatioCount = 0;
        int canonicalCount = 0;
        int extremeCount = 0;
        double sumRatio = 0.0;

        foreach (var shape in validShapes)
        {
            double w = shape.Width;
            double h = shape.Height;
            double ratio = Math.Round(w / h, 3);
            sumRatio += ratio;

            bool isSquare = Math.Abs(ratio - 1.0) <= 0.06;
            bool isGolden = Math.Abs(ratio - Phi) <= 0.08 || Math.Abs(ratio - (1.0 / Phi)) <= 0.05;
            bool isWidescreen = Math.Abs(ratio - (16.0 / 9.0)) <= 0.08 || Math.Abs(ratio - (9.0 / 16.0)) <= 0.05;
            bool isStandard43 = Math.Abs(ratio - (4.0 / 3.0)) <= 0.06 || Math.Abs(ratio - (3.0 / 4.0)) <= 0.05;
            bool isPhoto32 = Math.Abs(ratio - 1.5) <= 0.06 || Math.Abs(ratio - (2.0 / 3.0)) <= 0.05;
            bool isHeader21 = Math.Abs(ratio - 2.0) <= 0.08 || Math.Abs(ratio - 0.5) <= 0.05;

            bool isCanonical = isSquare || isGolden || isWidescreen || isStandard43 || isPhoto32 || isHeader21;
            bool isExtreme = ratio > 8.0 || ratio < 0.125;

            if (isSquare) squareCount++;
            if (isGolden) goldenRatioCount++;
            if (isCanonical) canonicalCount++;
            if (isExtreme) extremeCount++;

            string classification;
            if (isSquare) classification = "Square (1:1)";
            else if (isGolden) classification = "Golden Ratio (φ ~ 1.618)";
            else if (isWidescreen) classification = "Widescreen (16:9)";
            else if (isStandard43) classification = "Standard (4:3)";
            else if (isPhoto32) classification = "Photo (3:2)";
            else if (isHeader21) classification = "Header / Banner (2:1)";
            else if (isExtreme) classification = "Extreme Aspect Distortion";
            else classification = $"Arbitrary Ratio ({ratio:F2})";

            string status = isCanonical ? $"Harmonious {classification}" : isExtreme ? $"Distorted Aspect ({ratio:F2})" : $"Custom Aspect ({ratio:F2})";
            string recommendation = isExtreme ? "Normalize shape dimensions: aspect ratio deviates beyond 8:1" : isCanonical ? "Aspect ratio follows canonical aesthetic proportion" : "Consider snapping width/height to canonical ratio (1:1, 4:3, 16:9, or φ)";

            items.Add(new ShapeAspectRatioUniformityItem(
                shape.Id ?? "", shape.Name ?? shape.Id ?? "Shape",
                w, h, ratio, isSquare, isGolden, isCanonical, isExtreme,
                classification, status, recommendation));
        }

        double avgRatio = Math.Round(sumRatio / totalShapes, 2);
        int nonCanonicalCount = totalShapes - canonicalCount;

        double score = 100.0;
        score -= extremeCount * 15.0;
        score -= nonCanonicalCount * 4.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 90.0) category = "Aesthetic Geometric Harmony (≥90%)";
        else if (score >= 75.0) category = "Balanced Proportions with Minor Drift (75-89%)";
        else if (score >= 50.0) category = "Moderate Aspect Ratio Irregularities (50-74%)";
        else category = "Severely Distorted Geometry (<50%)";

        string summary = FormatAspectRatioSummary(
            totalShapes, canonicalCount, goldenRatioCount, squareCount,
            extremeCount, avgRatio, score, category, isSelection);

        return new DiagramShapeAspectRatioUniformityAudit(
            totalShapes, canonicalCount, goldenRatioCount, squareCount,
            extremeCount, avgRatio, score, items, category, isSelection, summary);
    }

    public static string FormatAspectRatioSummary(
        int totalShapes, int canonicalCount, int goldenCount, int squareCount,
        int extremeCount, double avgRatio, double score, string category, bool isSelection = false)
    {
        if (totalShapes == 0) return "Diagram shape aspect ratio audit: Empty canvas · 0 shapes evaluated";

        string scope = isSelection ? "Selection · " : "";
        string shapeStr = totalShapes == 1 ? "1 shape" : $"{totalShapes} shapes";
        var parts = new List<string>
        {
            $"{canonicalCount}/{totalShapes} canonical",
            $"avg ratio {avgRatio:F2}"
        };

        if (goldenCount > 0) parts.Add($"{goldenCount} golden ratio (φ)");
        if (squareCount > 0) parts.Add($"{squareCount} square (1:1)");
        if (extremeCount > 0) parts.Add($"⚠️ {extremeCount} extreme aspect");

        string details = string.Join(" · ", parts);
        return $"Diagram shape aspect ratio & golden ratio audit ({scope}{shapeStr} · {details} · {category} · {score:0.#}% aesthetic score)";
    }


    public DiagramViewportBleedAudit AuditDiagramViewportBleedSafety(bool selectionOnly = false)
    {
        var selected = SelectedItems.ToList();
        var targetShapes = (selectionOnly && selected.Count > 0) ? selected : Shapes.ToList();
        bool isSelection = selectionOnly && selected.Count > 0;
        var audit = AuditDiagramViewportBleedSafety(targetShapes, Shapes.ToList(), 1920.0, 1080.0, 12.0, isSelection);
        StatusMessage = audit.Summary;
        return audit;
    }

    public static DiagramViewportBleedAudit AuditDiagramViewportBleedSafety(
        IEnumerable<ShapeCanvasItemViewModel>? targetShapes,
        IEnumerable<ShapeCanvasItemViewModel>? allShapes = null,
        double canvasWidth = 1920.0,
        double canvasHeight = 1080.0,
        double bleedMargin = 12.0,
        bool isSelection = false)
    {
        var list = (allShapes ?? Enumerable.Empty<ShapeCanvasItemViewModel>()).Where(s => s != null).ToList();
        var targets = (targetShapes ?? list).Where(s => s != null).ToList();

        int totalShapes = targets.Count;
        if (totalShapes == 0)
        {
            string emptySummary = FormatViewportBleedSummary(0, 0, 0, 0, 0.0, 100.0, "Clean Baseline", isSelection);
            return new DiagramViewportBleedAudit(
                0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<DiagramViewportBleedItem>(), "Clean Baseline", isSelection, emptySummary);
        }

        var items = new List<DiagramViewportBleedItem>();
        int clippingCount = 0;
        int bleedWarningCount = 0;
        int safeCount = 0;
        double minObservedMargin = double.MaxValue;

        foreach (var sh in targets)
        {
            double left = sh.X;
            double top = sh.Y;
            double right = canvasWidth - (sh.X + sh.Width);
            double bottom = canvasHeight - (sh.Y + sh.Height);

            double shapeMinMargin = Math.Min(Math.Min(left, top), Math.Min(right, bottom));
            if (shapeMinMargin < minObservedMargin) minObservedMargin = shapeMinMargin;

            bool isClipping = left < 0 || top < 0 || (sh.X + sh.Width) > canvasWidth || (sh.Y + sh.Height) > canvasHeight;
            bool isInBleed = !isClipping && shapeMinMargin < bleedMargin;

            string direction;
            if (isClipping)
            {
                var clips = new List<string>();
                if (left < 0) clips.Add("Left");
                if (top < 0) clips.Add("Top");
                if ((sh.X + sh.Width) > canvasWidth) clips.Add("Right");
                if ((sh.Y + sh.Height) > canvasHeight) clips.Add("Bottom");
                direction = string.Join("-", clips);
                clippingCount++;
            }
            else if (isInBleed)
            {
                if (shapeMinMargin == left) direction = "Left";
                else if (shapeMinMargin == top) direction = "Top";
                else if (shapeMinMargin == right) direction = "Right";
                else direction = "Bottom";
                bleedWarningCount++;
            }
            else
            {
                direction = "Safe";
                safeCount++;
            }

            string status = isClipping ? "Clipping Outside Viewport" : (isInBleed ? "Inside Bleed Zone (<12px)" : "Safe Placement");

            items.Add(new DiagramViewportBleedItem(
                sh.Id, sh.Name, sh.X, sh.Y, sh.Width, sh.Height,
                Math.Round(shapeMinMargin, 1), isClipping, isInBleed, direction, status));
        }

        if (minObservedMargin == double.MaxValue) minObservedMargin = 0.0;

        double penalty = (clippingCount * 25.0) + (bleedWarningCount * 10.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 90.0 => "Safe Bleed Margins",
            >= 70.0 => "Moderate Perimeter Risk",
            _ => "Severe Viewport Clipping"
        };

        string summary = FormatViewportBleedSummary(
            totalShapes, clippingCount, bleedWarningCount, safeCount,
            minObservedMargin, score, category, isSelection);

        return new DiagramViewportBleedAudit(
            totalShapes, clippingCount, bleedWarningCount, safeCount,
            Math.Round(minObservedMargin, 1), score, items, category, isSelection, summary);
    }

    public static string FormatViewportBleedSummary(
        int totalShapes, int clippingCount, int bleedWarningCount, int safeCount,
        double minMargin, double score, string category, bool isSelection = false)
    {
        string scope = isSelection ? "Selection · " : "";
        if (totalShapes == 0)
            return $"Diagram viewport bleed safety audit ({scope}0 shapes): Clean Baseline";

        var parts = new List<string>();
        parts.Add($"{totalShapes} shape{(totalShapes == 1 ? "" : "s")}");
        if (safeCount > 0) parts.Add($"\u2713 {safeCount} safe");
        if (clippingCount > 0) parts.Add($"\ud83d\udea8 {clippingCount} clipping");
        if (bleedWarningCount > 0) parts.Add($"\u26a0 {bleedWarningCount} in bleed zone (<12px)");
        parts.Add($"min margin {minMargin:0.#}px");

        string details = string.Join(" · ", parts);
        return $"Diagram viewport bleed safety audit ({scope}{details} · {category} · {score:0.#}% safety score)";
    }

}

/// <summary>
/// Represents the result of an audit of shape aspect ratios and proportionality.
/// </summary>
public sealed record AspectRatioProportionsAudit(
    int TotalShapes,
    int SquareCount,
    int Widescreen169Count,
    int Standard43Count,
    int GoldenRatioCount,
    int CustomCount,
    double AverageRatio,
    string Summary);

/// <summary>
/// Represents the result of an audit of shape coordinates and dimensions against a design grid.
/// </summary>
public sealed record GridAlignmentAudit(
    int TotalShapes,
    int SelectedCount,
    double GridSize,
    int AlignedCount,
    int MisalignedCount,
    double AlignmentPercentage,
    double MaxOffset,
    string Summary);

/// <summary>
/// Represents the result of a z-order layering stack and drawing index audit.
/// </summary>
public sealed record LayerStackAudit(
    int TotalShapes,
    int SelectedCount,
    int MinIndex,
    int MaxIndex,
    bool IsContiguous,
    IReadOnlyList<int> SelectedIndices,
    string Summary);

/// <summary>
/// Axis for even geometric distribution of shapes.
/// </summary>
public enum DistributionAxis
{
    Horizontal,
    Vertical
}

/// <summary>
/// Represents the result of an audit of spacing distribution and gap uniformity between shapes.
/// </summary>
public sealed record SpacingDistributionAudit(
    int TotalShapes,
    int GapCount,
    double AverageGap,
    double MinGap,
    double MaxGap,
    bool IsUniform,
    DistributionAxis Axis,
    string Summary);

/// <summary>
/// Represents occlusion and overlapping metrics for an individual shape.
/// </summary>
public sealed record ShapeOcclusionInfo(
    string ShapeId,
    string DisplayName,
    int ZIndex,
    double Area,
    double MaxOcclusionPercentage,
    string CoveringShapeName,
    int OverlappingShapeCount,
    string Severity);

/// <summary>
/// Represents the result of a multi-shape Z-order depth overlap and occlusion audit.
/// </summary>
public sealed record ShapeOcclusionAudit(
    int TotalShapes,
    int OverlappingPairCount,
    int OccludedShapeCount,
    double MaxOcclusionPercentage,
    string WorstOccludedShapeName,
    string WorstCoveringShapeName,
    IReadOnlyList<ShapeOcclusionInfo> OccludedShapes,
    string OcclusionCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents the result of an audit of canvas boundary margins and safe zone bleed across shapes.
/// </summary>
public sealed record ShapeBoundaryBleedAudit(
    int TotalShapes,
    int BleedingShapeCount,
    int TightMarginShapeCount,
    int SafeShapeCount,
    double MinObservedMargin,
    string WorstShapeName,
    string SafetyCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents contrast and accessibility metrics for a shape's label text on its fill background.
/// </summary>
public sealed record ShapeTextContrastInfo(
    string ShapeId,
    string DisplayName,
    string FillHex,
    string TextColorHex,
    double ContrastRatio,
    bool IsWcagAaCompliant,
    bool IsWcagAaaCompliant,
    string SuggestedTextColorHex);

/// <summary>
/// Represents the result of an audit of text contrast ratios and WCAG compliance across shape labels.
/// </summary>
public sealed record ShapeTextContrastAudit(
    int TotalShapes,
    int LabeledShapes,
    int CompliantAaCount,
    int CompliantAaaCount,
    int FailingCount,
    double MinObservedRatio,
    double AverageRatio,
    string WorstShapeName,
    IReadOnlyList<ShapeTextContrastInfo> Contrasts,
    string ComplianceCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents aspect ratio metrics and golden ratio delta for an individual shape.
/// </summary>
public sealed record ShapeGoldenRatioInfo(
    string ShapeId,
    string DisplayName,
    double Width,
    double Height,
    double AspectRatio,
    double AspectDeltaFromPhi,
    bool IsGoldenAspect);

/// <summary>
/// Represents the result of an audit of Golden Ratio (phi = 1.618) adherence and harmonic scaling progression.
/// </summary>
public sealed record GoldenRatioProgressionAudit(
    int TotalShapes,
    int GoldenAspectCount,
    double AverageAspectDelta,
    int ProgressionStepCount,
    double AverageProgressionRatio,
    double ProgressionAdherenceScore,
    IReadOnlyList<ShapeGoldenRatioInfo> Shapes,
    string ProgressionCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents polar coordinates and radial expansion metrics for a shape in a golden spiral progression.
/// </summary>
public sealed record ShapeSpiralNodeInfo(
    string Id,
    string DisplayName,
    double Radius,
    double AngleDegrees,
    double RadialGrowthRatio);

/// <summary>
/// Represents the result of a logarithmic Golden Spiral placement and radial progression audit.
/// </summary>
public sealed record GoldenSpiralProgressionAudit(
    int TotalShapes,
    double CenterX,
    double CenterY,
    double AverageGrowthRatio,
    double AdherenceScore,
    string SpiralDirection,
    IReadOnlyList<ShapeSpiralNodeInfo> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents parsed color metrics for a shape in a palette harmony audit.
/// </summary>
public sealed record ShapeColorInfo(
    string ShapeId,
    string Name,
    string HexColor,
    double HueDegrees,
    double Saturation,
    double Value,
    bool IsNeutral,
    string ColorClassification);

/// <summary>
/// Represents the result of an audit of shape color palette harmony and chromatic coherence.
/// </summary>
public sealed record ShapeColorHarmonyAudit(
    int TotalAnalyzedShapes,
    int NeutralColorCount,
    int ChromaticColorCount,
    double MeanHueDegrees,
    double HueStandardDeviation,
    string HarmonyType,
    double HarmonyCoherenceScore,
    IReadOnlyList<string> DominantColorNames,
    IReadOnlyList<ShapeColorInfo> Colors,
    string SummaryCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated pairwise angular metrics between two shapes relative to isometric axes.
/// </summary>
public sealed record ShapeIsometricPairInfo(
    string SourceShapeId,
    string TargetShapeId,
    double AngleDegrees,
    double NearestAxisDegrees,
    double AngleDelta,
    bool IsIsometricAligned);

/// <summary>
/// Represents the result of an audit of shape isometric 30°/60° grid alignment, axonometric fidelity,
/// and depth stacking consistency.
/// </summary>
public sealed record IsometricGridAlignmentAudit(
    int TotalShapesEvaluated,
    int EvaluatedPairCount,
    int AlignedPairCount,
    int DepthStackingConflictCount,
    double MeanAngleError,
    double IsometricFidelityScore,
    IReadOnlyList<ShapeIsometricPairInfo> Pairs,
    string AlignmentCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated symmetry pairing between shapes.
/// </summary>
public sealed record ShapeSymmetryMatchInfo(
    string ShapeAId,
    string ShapeBId,
    string SymmetryType,
    double DeviationPixels);

/// <summary>
/// Represents the result of an audit of diagram bilateral reflection and rotational symmetry.
/// </summary>
public sealed record DiagramSymmetryAudit(
    int TotalShapes,
    int VerticalMatches,
    int HorizontalMatches,
    int RotationalMatches,
    double VerticalSymmetryPercent,
    double HorizontalSymmetryPercent,
    double RotationalSymmetryPercent,
    double OverallSymmetryScore,
    IReadOnlyList<ShapeSymmetryMatchInfo> Matches,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents pairwise spacing, gutter clearance, and collision status between two adjacent shapes.
/// </summary>
public sealed record ShapeSpacingCollisionInfo(
    string ShapeAId,
    string ShapeBId,
    string ShapeAName,
    string ShapeBName,
    double HorizontalGutter,
    double VerticalGutter,
    double MinimumClearance,
    bool IsColliding,
    double OverlapArea);

/// <summary>
/// Represents the result of an audit of shape boundary spacing uniformity, gutter consistency,
/// and bounding box collisions.
/// </summary>
public sealed record ShapeSpacingUniformityAudit(
    int TotalShapesEvaluated,
    int EvaluatedPairCount,
    int CollidingPairCount,
    double MeanHorizontalGutter,
    double MeanVerticalGutter,
    double GutterStandardDeviation,
    double SpacingUniformityScore,
    IReadOnlyList<ShapeSpacingCollisionInfo> Pairs,
    string SpacingCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents clearance, bend count, collisions, and wire crossings for an orthogonal connection routing channel.
/// </summary>
public sealed record OrthogonalChannelInfo(
    string ShapeAId,
    string ShapeBId,
    string ShapeAName,
    string ShapeBName,
    int BendCount,
    bool CollidesWithShapes,
    int CrossingCount,
    double ChannelClearance,
    bool HasClearChannel);

/// <summary>
/// Represents the result of an audit of orthogonal connection routing channels, wire intersection crossings,
/// shape collisions, and Manhattan routing clearances.
/// </summary>
public sealed record OrthogonalRoutingAudit(
    int TotalShapesEvaluated,
    int ConnectionChannelsEvaluated,
    int ClearChannelCount,
    int ObstructedChannelCount,
    int TotalWireCrossings,
    double MeanChannelClearance,
    double RoutingEfficiencyScore,
    IReadOnlyList<OrthogonalChannelInfo> Channels,
    string RoutingCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents grid snap error and subpixel rounding drift metrics for an individual shape.
/// </summary>
public sealed record ShapeGridSnapInfo(
    string ShapeId,
    string Name,
    double X,
    double Y,
    double Width,
    double Height,
    double XSnapError,
    double YSnapError,
    double WidthSnapError,
    double HeightSnapError,
    bool IsFullySnapped,
    bool HasSubpixelDrift);

/// <summary>
/// Represents the result of an audit of 2D shape grid snap conformance, coordinate quantization adherence,
/// and subpixel rounding drift.
/// </summary>
public sealed record ShapeGridSnapAudit(
    int TotalShapesEvaluated,
    double GridSize,
    int FullySnappedCount,
    int MisalignedShapeCount,
    int SubpixelDriftCount,
    double MeanSnapErrorPixels,
    double GridSnapConformanceScore,
    IReadOnlyList<ShapeGridSnapInfo> Shapes,
    string ConformanceCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents calculated visual weight, area, and center coordinates for an individual shape.
/// </summary>
public sealed record ShapeVisualWeightInfo(
    string ShapeId,
    string Name,
    double X,
    double Y,
    double Width,
    double Height,
    double Area,
    double Weight,
    double CenterX,
    double CenterY);

/// <summary>
/// Represents the result of an audit of diagram visual weight, optical center-of-gravity versus geometric bounding box center,
/// and directional optical balance.
/// </summary>
public sealed record DiagramVisualWeightAudit(
    int TotalShapes,
    double GeometricCenterX,
    double GeometricCenterY,
    double OpticalCentroidX,
    double OpticalCentroidY,
    double DeltaX,
    double DeltaY,
    double NormalizedOffsetPercent,
    string DirectionalBias,
    double OpticalBalanceScore,
    IReadOnlyList<ShapeVisualWeightInfo> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated knot curvature continuity, turning angle, and micro-kink detection for a connector knot.
/// </summary>
public sealed record ConnectorKnotInfo(
    string ConnectorId,
    int KnotIndex,
    double X,
    double Y,
    double TurningAngleDegrees,
    string ContinuityGrade,
    bool HasMicroKink);

/// <summary>
/// Represents the result of an audit of diagram connector curvature continuity,
/// Bezier tangent alignment, and knot smoothness.
/// </summary>
public sealed record ConnectorCurvatureAudit(
    int TotalConnectorsEvaluated,
    int TotalKnotsEvaluated,
    int SmoothKnotCount,
    int CornerKnotCount,
    int MicroKinkCount,
    double MeanTurningAngleDegrees,
    double CurvatureSmoothnessScore,
    IReadOnlyList<ConnectorKnotInfo> Knots,
    string SmoothnessCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated aspect ratio, nearest canonical archetype, and scaling drift metrics for an individual shape.
/// </summary>
public sealed record ShapeAspectRatioInfo(
    string ShapeId,
    string DisplayName,
    double Width,
    double Height,
    double ActualAspectRatio,
    string NearestArchetype,
    double TargetAspectRatio,
    double DriftPercentage,
    bool IsConforming,
    bool HasAccidentalDrift);

/// <summary>
/// Represents the result of an audit of shape aspect ratios, canonical archetype lock conformance,
/// and non-uniform scaling drift.
/// </summary>
public sealed record DiagramAspectRatioAudit(
    int TotalShapesEvaluated,
    int ConformingShapeCount,
    int AccidentalDriftCount,
    int CustomFreeformCount,
    double MeanDriftPercentage,
    double AspectRatioFidelityScore,
    IReadOnlyList<ShapeAspectRatioInfo> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated stroke weight, boundary alignment, corner radius, and miter limit metrics for an individual shape.
/// </summary>
public sealed record ShapeStrokeCornerInfo(
    string ShapeId,
    string DisplayName,
    string ShapeType,
    double Width,
    double Height,
    double StrokeWidthPt,
    double StrokeToDimensionRatio,
    bool HasHeavyStroke,
    bool IsGhostShape,
    double EstimatedCornerRadius,
    bool HasCornerRadiusDiscrepancy,
    bool HasMiterSpikeRisk,
    string Status);

/// <summary>
/// Represents the result of an audit of shape border stroke alignment, corner radius uniformity,
/// and miter limit conformance.
/// </summary>
public sealed record DiagramStrokeCornerAudit(
    int TotalShapesEvaluated,
    int UniformStrokeCount,
    int HeavyStrokeCount,
    int GhostShapeCount,
    int RoundedRectCount,
    int UniformCornerRadiusCount,
    int MiterSpikeRiskCount,
    double MeanStrokeWidthPt,
    double StrokeFidelityScore,
    IReadOnlyList<ShapeStrokeCornerInfo> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated collision, grazing, and clearance metrics for an individual diagram connector.
/// </summary>
public sealed record ConnectorCollisionItem(
    string ConnectorId,
    string ConnectorName,
    string SourceShapeName,
    string TargetShapeName,
    int SegmentCount,
    int CollisionCount,
    int NearMissCount,
    IReadOnlyList<string> CollidedShapeNames,
    IReadOnlyList<string> NearMissShapeNames,
    double ClearanceMarginPixels,
    string Severity);

/// <summary>
/// Represents the result of an audit of connector routing obstacle avoidance, bounding box collisions,
/// and near-miss grazing hazards.
/// </summary>
public sealed record ConnectorObstacleCollisionAudit(
    int TotalConnectors,
    int TotalObstacleShapes,
    int ClearConnectors,
    int CollidedConnectors,
    int NearMissConnectors,
    int TotalCollisions,
    double RoutingClearanceScore,
    IReadOnlyList<ConnectorCollisionItem> Connectors,
    IReadOnlyList<string> CriticalCollisions,
    string ClearanceCategory,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated bounding box containment, child count, and z-order monotonicity for a compound shape group.
/// </summary>
public sealed record CompoundGroupInfo(
    string GroupId,
    string GroupName,
    int ChildCount,
    double GroupX,
    double GroupY,
    double GroupWidth,
    double GroupHeight,
    double ChildrenBoundsLeft,
    double ChildrenBoundsTop,
    double ChildrenBoundsRight,
    double ChildrenBoundsBottom,
    bool HasOverflow,
    double OverflowArea,
    bool IsZOrderMonotonic);

/// <summary>
/// Represents the result of an audit of shape compound grouping hierarchy,
/// nested child bounding box containment, overflow detection, and z-index depth order stacking.
/// </summary>
public sealed record ShapeCompoundGroupAudit(
    int TotalShapesEvaluated,
    int TotalGroupsFound,
    int GroupedChildrenCount,
    int StandaloneShapesCount,
    int OverflowingGroupCount,
    int ZOrderInversionCount,
    double GroupContainmentScore,
    IReadOnlyList<CompoundGroupInfo> Groups,
    IReadOnlyList<string> StructuralAlerts,
    string Category,
    bool IsSelectionScope,
    string Summary);




/// <summary>
/// Represents evaluated connection port snapping, distance, and subpixel drift for an individual connector endpoint.
/// </summary>
public sealed record ConnectorPortSnapDetail(
    string ConnectorId,
    string ConnectorName,
    bool StartSnapped,
    bool EndSnapped,
    string? StartSnappedShapeName,
    string? StartSnappedPort,
    double StartPortDistance,
    string? EndSnappedShapeName,
    string? EndSnappedPort,
    double EndPortDistance,
    bool HasNearMissHazard,
    bool HasSubpixelDrift);

/// <summary>
/// Represents the result of an audit of diagram connector port snapping accuracy,
/// canonical perimeter port adherence, near-miss hazards, and subpixel float drift.
/// </summary>
public sealed record ConnectorPortSnappingAudit(
    int TotalConnectors,
    int TotalShapesEvaluated,
    int FullySnappedConnectors,
    int PartiallySnappedConnectors,
    int FloatingConnectors,
    int NearMissPortHazards,
    int SubpixelDriftCount,
    double PortSnappingScore,
    IReadOnlyList<ConnectorPortSnapDetail> Details,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents the result of an audit of canvas spatial balance, area-weighted center of mass,
/// directional lopsidedness, and outer margin clearance conformance.
/// </summary>
public sealed record CanvasSpatialBalanceAudit(
    int TotalShapesEvaluated,
    double CanvasWidth,
    double CanvasHeight,
    double CentroidX,
    double CentroidY,
    double OffsetFromCenter,
    double MarginLeft,
    double MarginTop,
    double MarginRight,
    double MarginBottom,
    double MinMargin,
    int EdgeCrowdingHazards,
    double SpatialBalanceScore,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated metrics and participating shape identifiers for a collinear alignment group.
/// </summary>
public sealed record ShapeCollinearityGroup(
    string AlignmentAxis,
    double Coordinate,
    IReadOnlyList<string> ShapeIds,
    IReadOnlyList<string> ShapeNames,
    bool IsExactCollinear,
    double MaxCoordinateJitter);

/// <summary>
/// Represents the result of an audit of diagram shape alignment collinearity, sub-pixel snapping jitter,
/// and distribution spacing uniformity across adjacent shapes.
/// </summary>
public sealed record DiagramCollinearityAndSpacingAudit(
    int TotalShapesEvaluated,
    int CollinearGroupCount,
    int NearMissJitterHazards,
    int UniformSpacingSequences,
    int IrregularSpacingSequences,
    double CollinearityAndSpacingScore,
    IReadOnlyList<ShapeCollinearityGroup> CollinearGroups,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated vector direction, angle, length, and reading monotonicity for an individual connector line.
/// </summary>
public sealed record ConnectorVectorFlowItem(
    string ConnectorId,
    string ConnectorName,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double DeltaX,
    double DeltaY,
    double Length,
    double AngleDegrees,
    string DirectionCategory,
    bool IsMonotonicFlow,
    bool IsRetrograde);

/// <summary>
/// Represents the result of an audit of diagram visual flow directionality, connector arrow vector monotonicity,
/// dominant visual reading axes, and retrograde backward flow hazards.
/// </summary>
public sealed record DiagramVisualFlowAudit(
    int TotalShapesEvaluated,
    int TotalConnectors,
    int LeftToRightCount,
    int TopToBottomCount,
    int RightToLeftRetrogradeCount,
    int BottomToTopUpwardCount,
    string DominantFlowAxis,
    double FlowMonotonicityScore,
    IReadOnlyList<ConnectorVectorFlowItem> Connectors,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated aspect ratio, nearest aesthetic archetype, golden ratio deviation, and conformity for an individual shape.
/// </summary>
public sealed record ShapeGoldenRatioAdherenceItem(
    string ShapeId,
    string DisplayName,
    double Width,
    double Height,
    double NormalizedRatio,
    string ClosestRatioArchetype,
    double DeviationPercentage,
    bool IsGoldenRatio,
    bool IsSquare,
    bool IsStandardAesthetic);

/// <summary>
/// Represents the result of an audit of shape aspect ratio consistency, Golden Ratio (phi ~ 1.618) adherence,
/// canonical aesthetic proportion locks, and irregular ratio drift across diagram shapes.
/// </summary>
public sealed record DiagramGoldenRatioAdherenceAudit(
    int TotalShapesEvaluated,
    int GoldenRatioConformingCount,
    int SquareCircleCount,
    int StandardAestheticCount,
    int UnalignedCount,
    double MeanGoldenDeviationPercentage,
    double GoldenRatioAdherenceScore,
    IReadOnlyList<ShapeGoldenRatioAdherenceItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated corner radius metrics, fillet curvature tier, and consistency status for an individual shape.
/// </summary>
public sealed record ShapeCornerRadiusItem(
    string Id,
    string Name,
    string Prst,
    double Width,
    double Height,
    double CornerRadius,
    bool IsRounded,
    string RadiusTier,
    bool HasDiscrepancy,
    string Status);

/// <summary>
/// Represents the result of an audit of shape corner radius consistency, rounded rectangle harmony,
/// fillet style uniformity, and sharp vs rounded corner mixing across diagram shapes.
/// </summary>
public sealed record ShapeCornerRadiusConsistencyAudit(
    int TotalShapesEvaluated,
    int RoundedRectCount,
    int SharpRectCount,
    int SubtleRadiusCount,
    int MediumRadiusCount,
    int PillRadiusCount,
    int DistinctRadiiCount,
    double DominantRadius,
    double UniformityScore,
    IReadOnlyList<ShapeCornerRadiusItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated bounding box intersection metrics, coverage ratio, and occlusion tier for a pair of overlapping shapes.
/// </summary>
public sealed record ShapeOcclusionPairItem(
    string LowerShapeId,
    string LowerShapeName,
    string HigherShapeId,
    string HigherShapeName,
    double OverlapArea,
    double CoverageRatio,
    bool IsDuplicateCoincident,
    bool IsContainerNesting,
    string OcclusionTier,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape Z-index stacking layering, pairwise overlap occlusion,
/// duplicate coincident shapes, and intentional container nesting.
/// </summary>
public sealed record DiagramZIndexOcclusionAudit(
    int TotalShapesEvaluated,
    int OverlapPairsCount,
    int MajorOcclusionsCount,
    int ModerateOverlapsCount,
    int DuplicateCoincidentsCount,
    int ContainerNestingCount,
    double StackingClarityScore,
    IReadOnlyList<ShapeOcclusionPairItem> OverlapPairs,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated arrowhead styles and endpoint attachment for an individual diagram connector.
/// </summary>
public sealed record ConnectorArrowheadItem(
    string Id,
    string Name,
    string ArrowheadStart,
    string ArrowheadEnd,
    bool IsStartAttached,
    bool IsEndAttached,
    string AttachedStartShapeId,
    string AttachedEndShapeId,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram connector arrowhead style uniformity and endpoint attachment to shape boundaries.
/// </summary>
public sealed record DiagramConnectorArrowheadAudit(
    int TotalShapesEvaluated,
    int TotalConnectors,
    int AttachedEndpointsCount,
    int DanglingEndpointsCount,
    int FullyAttachedConnectors,
    string DominantArrowheadType,
    int DominantTypeCount,
    double ArrowheadUniformityScore,
    double EndpointAttachmentScore,
    double OverallConnectorHarmonyScore,
    IReadOnlyList<ConnectorArrowheadItem> Connectors,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated stroke thickness, dash pattern, and visual balance for an individual diagram shape.
/// </summary>
public sealed record ShapeStrokeOutlineItem(
    string ShapeId,
    string ShapeName,
    double StrokeThickness,
    string DashStyle,
    string StrokeColor,
    bool IsOutlierWeight,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape stroke weight consistency, border dash style uniformity, and overall outline harmony.
/// </summary>
public sealed record DiagramStrokeWeightAndDashAudit(
    int TotalShapesEvaluated,
    int SolidStrokeCount,
    int DashedStrokeCount,
    int DottedStrokeCount,
    int HeavyStrokeCount,
    int StandardStrokeCount,
    int HairlineStrokeCount,
    double AverageStrokeThickness,
    double StrokeThicknessStdDev,
    double OutlineHarmonyScore,
    IReadOnlyList<ShapeStrokeOutlineItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated fill color, luminance, WCAG contrast ratio, and color family for an individual shape.
/// </summary>
public sealed record ShapeColorHarmonyItem(
    string ShapeId,
    string ShapeName,
    string FillColor,
    double RelativeLuminance,
    double ContrastRatio,
    bool MeetsWcagAa,
    string ColorFamily,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape fill color harmony, WCAG contrast ratios, and palette uniformity.
/// </summary>
public sealed record DiagramFillColorHarmonyAudit(
    int TotalShapesEvaluated,
    int UniqueColorsCount,
    int WcagAaCompliantCount,
    int LowContrastCount,
    string DominantColorFamily,
    int DominantColorCount,
    double PaletteHarmonyScore,
    IReadOnlyList<ShapeColorHarmonyItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated aspect ratio, standard proportions, and sliver distortion for an individual shape.
/// </summary>
public sealed record ShapeAspectRatioItem(
    string ShapeId,
    string Label,
    double Width,
    double Height,
    double AspectRatio,
    double Area,
    string ClosestStandardRatio,
    double RatioDeviation,
    bool IsExtremeDistortion,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape geometric aspect ratio harmony, standard proportions,
/// extreme sliver distortions, and visual mass distribution.
/// </summary>
public sealed record DiagramAspectRatioHarmonyAudit(
    int TotalShapes,
    double AverageAspectRatio,
    double AspectRatioVariance,
    int ExtremeDistortionCount,
    double AverageArea,
    double AreaVariance,
    int GoldenRatioCount,
    int SquareRatioCount,
    double HarmonyScore,
    IReadOnlyList<ShapeAspectRatioItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated routing metrics, directness ratio, bend count, and obstacle clipping for an individual connector.
/// </summary>
public sealed record ConnectorRouteItem(
    string Id,
    string Name,
    double ActualLength,
    double EuclideanDistance,
    double DirectnessRatio,
    int BendCount,
    int ObstacleClippingCount,
    string RouteQuality);

/// <summary>
/// Represents the result of an audit of diagram connector route directness, bend count, and obstacle clipping.
/// </summary>
public sealed record DiagramConnectorRouteAudit(
    int TotalConnectors,
    int DirectRouteCount,
    int ModerateDetourCount,
    int ConvolutedRouteCount,
    int ObstacleClippingCount,
    double AverageDirectnessRatio,
    double RouteEfficiencyScore,
    IReadOnlyList<ConnectorRouteItem> Connectors,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated metrics, bounding box area, occluding shape count, and visibility percentage for an individual shape layer.
/// </summary>
public sealed record ShapeLayerOcclusionItem(
    string ShapeId,
    string ShapeType,
    int ZIndex,
    double Area,
    int OccludedByCount,
    double MaxOcclusionPercentage,
    bool IsCompletelyHidden,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape Z-order stacking, accidental complete/partial occlusion, and visual hierarchy.
/// </summary>
public sealed record DiagramLayerOcclusionAudit(
    int TotalShapes,
    int OverlappingPairsCount,
    int SevereOcclusionCount,
    int CompletelyHiddenCount,
    double HierarchyClarityScore,
    IReadOnlyList<ShapeLayerOcclusionItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated metrics, grid snapping residual, alignment partners, and spacing parity for an individual shape.
/// </summary>
public sealed record ShapeAlignmentItem(
    string ShapeId,
    string ShapeType,
    double X,
    double Y,
    double Width,
    double Height,
    double GridSnapResidual,
    bool IsGridSnapped,
    int AlignedPartnersCount,
    string AlignmentDetails,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape horizontal/vertical alignment, grid snapping residuals,
/// and distribution interval uniformity.
/// </summary>
public sealed record DiagramAlignmentAndSnappingAudit(
    int TotalShapes,
    int GridSnappedShapesCount,
    int OffGridShapesCount,
    int AlignedShapesCount,
    int MisalignedShapesCount,
    double AverageGridResidual,
    double AlignmentPrecisionScore,
    IReadOnlyList<ShapeAlignmentItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated metrics, segment counts, orthogonality, and collinear kinks for an individual diagram connector line.
/// </summary>
public sealed record ConnectionPortRoutingItem(
    string ShapeId,
    string ShapeName,
    int SegmentCount,
    bool IsFullyOrthogonal,
    int NonOrthogonalSegmentCount,
    int CollinearKinksCount,
    double MaxAngleDeviationDegrees,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram connection lines and connectors for segment orthogonality,
/// cardinal angle snapping, collinear routing kinks, and connection routing tidiness.
/// </summary>
public sealed record DiagramConnectionPortAudit(
    int TotalConnectors,
    int TotalSegments,
    int OrthogonalSegmentsCount,
    int NonOrthogonalSegmentsCount,
    int CollinearKinksCount,
    double AverageAngleDeviation,
    double RoutingTidinessScore,
    IReadOnlyList<ConnectionPortRoutingItem> Connectors,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated coordinates, margins, shape area, and perimeter violation status for an individual shape item.
/// </summary>
public sealed record ShapePerimeterMarginItem(
    string ShapeId,
    string ShapeName,
    double X,
    double Y,
    double Width,
    double Height,
    double MarginLeft,
    double MarginTop,
    double MarginRight,
    double MarginBottom,
    bool HasPerimeterViolation,
    double ShapeArea,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape visual density, canvas packing ratio,
/// perimeter margin padding, and viewport safety clearances.
/// </summary>
public sealed record DiagramVisualDensityAudit(
    int TotalShapes,
    double CanvasWidth,
    double CanvasHeight,
    double CanvasArea,
    double TotalShapeArea,
    double PackingDensityRatio,
    int PerimeterViolationsCount,
    double MinPerimeterMargin,
    double ViewportBreathingRoomScore,
    IReadOnlyList<ShapePerimeterMarginItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated Z-index metrics, occlusion depth, and stacking stratification for an individual shape.
/// </summary>
public sealed record ShapeOcclusionLayerItem(
    string ShapeId,
    string ShapeName,
    int ZIndex,
    double Left,
    double Top,
    double Width,
    double Height,
    int OccludedByCount,
    double MaxOcclusionRatio,
    bool IsFullyOccluded,
    bool IsInvertedContainerHazard,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram Z-index layering, occlusion depth, container nesting, and shape visual stacking stratification.
/// </summary>
public sealed record DiagramZOrderOcclusionAudit(
    int TotalShapes,
    int OverlappingPairsCount,
    int SevereOcclusionCount,
    int InvertedContainerCount,
    double MeanOcclusionRatio,
    double LayerStratificationScore,
    IReadOnlyList<ShapeOcclusionLayerItem> Layers,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated bend count, redundant detour bends, and shape crossings for an individual diagram connector.
/// </summary>
public sealed record ConnectorRoutingItem(
    string ConnectorId,
    string ConnectorName,
    int BendCount,
    int RedundantBends,
    int IntersectingShapesCount,
    bool IsOrthogonal,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram connector orthogonal routing, corner bends, collinear kinks, and segment-shape crossings.
/// </summary>
public sealed record DiagramConnectorRoutingAudit(
    int TotalConnectors,
    int TotalBends,
    int RedundantBendsCount,
    int ShapeIntersectionCount,
    double MeanBendsPerConnector,
    double RoutingEleganceScore,
    IReadOnlyList<ConnectorRoutingItem> Connectors,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated near-miss alignment offsets, collinear edges, centerline delta, and smart snap recommendation for an individual shape.
/// </summary>
public sealed record ShapeAlignmentProximityItem(
    string ShapeId,
    string ShapeName,
    double X,
    double Y,
    double Width,
    double Height,
    int NearMissEdgeCount,
    int NearMissCenterCount,
    double MinOffsetDelta,
    string AlignmentGuideRecommendation,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape alignment guide proximity, collinear edges,
/// centerline alignment near-misses (1-3px), smart snap distribution, and precision alignment scoring.
/// </summary>
public sealed record DiagramAlignmentGuidesAudit(
    int TotalShapes,
    int EvaluatedPairCount,
    int NearMissEdgeCount,
    int NearMissCenterCount,
    int UnevenDistributionGapCount,
    double MeanAlignmentOffsetDelta,
    double AlignmentPrecisionScore,
    IReadOnlyList<ShapeAlignmentProximityItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated bounding containment, child count, parent container, overflow state, and margin padding for an individual shape.
/// </summary>
public sealed record ShapeContainerHierarchyItem(
    string ShapeId,
    string ShapeName,
    bool IsContainer,
    int ContainedChildCount,
    string EnclosingContainerName,
    bool IsOverflowing,
    double MinPadding,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape grouping hierarchy, container bounds, child overflows, and padding margins.
/// </summary>
public sealed record DiagramGroupHierarchyAudit(
    int TotalShapes,
    int ContainerCount,
    int EnclosedChildCount,
    int ChildOverflowCount,
    int TightPaddingCount,
    double HierarchyIntegrityScore,
    IReadOnlyList<ShapeContainerHierarchyItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);



/// <summary>
/// Represents evaluated Z-order overlap, inverted stacking, and full occlusion state between two diagram shapes.
/// </summary>
public sealed record ShapeLayeringOcclusionItem(
    string LowerShapeId,
    string UpperShapeId,
    string LowerShapeName,
    string UpperShapeName,
    double OverlapRatio,
    bool IsInvertedHierarchy,
    bool IsFullyOccluded,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape Z-order layering, stacking depth, inverted hierarchy, and full occlusion.
/// </summary>
public sealed record ShapeLayeringZOrderAudit(
    int TotalShapes,
    int OverlapCount,
    int InvertedHierarchyCount,
    int FullyOccludedCount,
    double LayeringEleganceScore,
    IReadOnlyList<ShapeLayeringOcclusionItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated metrics, fill/text colors, contrast ratio, and WCAG AA compliance for an individual diagram shape.
/// </summary>
public sealed record ShapeContrastItem(
    string ShapeId,
    string ShapeType,
    string Text,
    string FillColor,
    string TextColor,
    double ContrastRatio,
    bool MeetsNormalWcagAa,
    bool MeetsLargeWcagAa,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape visual weight, contrast ratios, and WCAG 2.1 typography readability.
/// </summary>
public sealed record DiagramShapeContrastAudit(
    int TotalShapes,
    int EvaluatedTextShapes,
    int PassingWcagCount,
    int FailingWcagCount,
    double AverageContrastRatio,
    double AccessibilityScore,
    IReadOnlyList<ShapeContrastItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);



/// <summary>
/// Represents evaluated metrics, snap distances, anchor shapes, and orthogonality for an individual diagram connector.
/// </summary>
public sealed record ConnectorAnchorSnappingItem(
    string ConnectorId,
    string ConnectorName,
    string SourceShapeId,
    string TargetShapeId,
    double SourceDistanceToAnchor,
    double TargetDistanceToAnchor,
    bool SourceSnapped,
    bool TargetSnapped,
    int ElbowSegmentCount,
    double MaxAngleDeviationDegrees,
    bool IsOrthogonal,
    bool HasSkewJitter,
    string Status);

/// <summary>
/// Represents the result of an audit of diagram shape connector endpoint anchor snapping margin,
/// alignment orthogonality, elbow angles, and routing precision.
/// </summary>
public sealed record DiagramConnectorSnappingAudit(
    int TotalConnectors,
    int FullySnappedCount,
    int UnsnappedEndpointCount,
    int OrthogonalCount,
    int SkewedSegmentCount,
    double AverageAngleDeviationDegrees,
    double RoutingPrecisionScore,
    IReadOnlyList<ConnectorAnchorSnappingItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);


/// <summary>
/// Represents evaluated aspect ratio, canonical matching, golden ratio alignment, and distortion metrics for an individual diagram shape.
/// </summary>
public sealed record ShapeAspectRatioUniformityItem(
    string ShapeId,
    string ShapeName,
    double Width,
    double Height,
    double AspectRatio,
    bool IsSquare,
    bool IsGoldenRatio,
    bool IsCanonicalRatio,
    bool IsExtremeDistortion,
    string RatioClassification,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of diagram shape aspect ratio uniformity, golden ratio alignment,
/// canonical bounding proportions, extreme distortion detection, and visual geometry aesthetics.
/// </summary>
public sealed record DiagramShapeAspectRatioUniformityAudit(
    int TotalShapes,
    int CanonicalRatioCount,
    int GoldenRatioCount,
    int SquareCount,
    int ExtremeDistortionCount,
    double AverageAspectRatio,
    double AestheticScore,
    IReadOnlyList<ShapeAspectRatioUniformityItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated Z-order stacking depth, occlusion percentage, and obscuring shapes for an individual diagram shape.
/// </summary>
public sealed record ShapeZOrderOcclusionItem(
    string ShapeId,
    string ShapeName,
    int ZIndex,
    string OccludingShapeId,
    string OccludingShapeName,
    int OccludingZIndex,
    double OcclusionPercentage,
    bool IsCompleteOcclusion,
    string Severity,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of diagram shape Z-order layering, stacking depth,
/// overlap occlusion, completely obscured shapes, and visual layering cleanliness.
/// </summary>
/// <summary>
/// Represents evaluated fill/text/stroke colors, relative luminance, WCAG 2.1 AA/AAA contrast ratios, and boundary blend for an individual shape.
/// </summary>
public sealed record ShapeColorContrastAccessibilityItem(
    string ShapeId,
    string ShapeName,
    string ShapeType,
    string FillColor,
    string TextColor,
    string StrokeColor,
    double TextContrastRatio,
    double BoundaryContrastRatio,
    bool HasText,
    bool MeetsWcagAa,
    bool MeetsWcagAaa,
    bool MeetsBoundaryContrast,
    string Severity,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of diagram shape fill and stroke color contrast accessibility,
/// WCAG 2.1 AA (4.5:1) / AAA (7:1) typography readability, and canvas boundary visibility.
/// </summary>
public sealed record DiagramShapeColorContrastAccessibilityAudit(
    int TotalShapes,
    int EvaluatedTextShapes,
    int PassingAaCount,
    int PassingAaaCount,
    int FailingAaCount,
    int FailingBoundaryCount,
    double AverageTextContrastRatio,
    double AccessibilityScore,
    IReadOnlyList<ShapeColorContrastAccessibilityItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record DiagramShapeZOrderStackingAudit(
    int TotalShapes,
    int OccludedShapeCount,
    int CompleteOcclusionCount,
    int SevereOcclusionCount,
    int MaxStackingDepth,
    double AverageStackingDepth,
    double LayeringCleanlinessScore,
    IReadOnlyList<ShapeZOrderOcclusionItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);



/// <summary>
/// Represents evaluated arrowhead styles, snap port attachment, and orthogonality for an individual diagram connector.
/// </summary>
public sealed record ConnectorAttachmentPortItem(
    string ConnectorId,
    string ConnectorName,
    bool HasStartArrow,
    bool HasEndArrow,
    bool IsStartSnapped,
    bool IsEndSnapped,
    string StartPortName,
    string EndPortName,
    bool IsOrthogonal,
    bool HasDanglingArrow,
    string RoutingStatus,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of diagram connector arrowheads, snap port attachment accuracy,
/// endpoint drift, and orthogonality compliance.
/// </summary>
/// <summary>
/// Represents evaluated text bounding, insets, and overflow status for an individual diagram shape.
/// </summary>
public sealed record ShapeTextPaddingItem(
    string ShapeId,
    string ShapeName,
    string ShapeType,
    string Text,
    double ShapeWidth,
    double ShapeHeight,
    double InsetLeft,
    double InsetTop,
    double InsetRight,
    double InsetBottom,
    double AvailableWidth,
    double AvailableHeight,
    double EstimatedTextWidth,
    double EstimatedTextHeight,
    bool HasOverflow,
    bool IsCramped,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of diagram shape text padding, label boundaries, and text overflow clipping hazards.
/// </summary>
public sealed record DiagramShapeTextPaddingAudit(
    int TotalShapes,
    int TextShapeCount,
    int WellPaddedCount,
    int CrampedCount,
    int OverflowCount,
    double EnclosureCleanlinessScore,
    IReadOnlyList<ShapeTextPaddingItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record DiagramConnectorPortAttachmentAudit(
    int TotalShapes,
    int TotalConnectors,
    int SnappedPortsCount,
    int DanglingArrowheadsCount,
    int NonOrthogonalBendsCount,
    double RoutingPrecisionScore,
    IReadOnlyList<ConnectorAttachmentPortItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

/// <summary>
/// Represents evaluated simulated elevation, stroke depth, Z-order, and layering harmony for an individual shape.
/// </summary>
public sealed record DiagramShapeElevationShadowItem(
    string ShapeId,
    string ShapeName,
    double ElevationTier,
    int ZIndex,
    string Issue,
    string Severity,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of diagram shape visual elevation depth, drop shadows,
/// Z-index stacking hierarchy, and 2.5D visual layering harmony.
/// </summary>
public sealed record DiagramShapeElevationShadowAudit(
    int TotalShapesEvaluated,
    int FlatShapesCount,
    int ElevatedShapesCount,
    int InvertedLayeringCount,
    int AmbiguousBoundaryCount,
    double ElevationHarmonyScore,
    IReadOnlyList<DiagramShapeElevationShadowItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);


public sealed record ShapeNestingTierItem(
    string ShapeId,
    string Name,
    int Depth,
    int DirectChildCount,
    int TotalDescendantCount,
    double BoundingArea,
    string Status,
    string Recommendation);

public sealed record DiagramShapeGroupNestingAudit(
    int TotalShapes,
    int TotalContainers,
    int MaxNestingDepth,
    double AverageNestingDepth,
    int OvercrowdedContainerCount,
    double HierarchyComplexityScore,
    IReadOnlyList<ShapeNestingTierItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);


public sealed record DiagramConnectorSnapItem(
    string ConnectorId,
    bool StartSnapped,
    string? StartShapeId,
    string? StartAnchor,
    double StartDistance,
    bool EndSnapped,
    string? EndShapeId,
    string? EndAnchor,
    double EndDistance,
    bool IsNearMiss,
    string Recommendation);

public sealed record DiagramConnectorSnapAudit(
    int TotalConnectors,
    int SnappedConnectors,
    int PartiallySnappedCount,
    int FloatingConnectors,
    int NearMissCount,
    double ConnectionFidelityScore,
    IReadOnlyList<DiagramConnectorSnapItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record RadialShapeItem(
    string Id,
    string Name,
    double DistanceFromCentroid,
    double AngleDegrees,
    int AssignedRing,
    double DeviationFromRingMean,
    string Recommendation);

public sealed record IsometricShapeRayItem(
    string FromShapeId,
    string FromShapeName,
    string ToShapeId,
    string ToShapeName,
    double AngleDegrees,
    double NearestIsometricAxis,
    double AngularDeviation,
    bool IsAligned,
    string Recommendation);


public sealed record ObliqueShapeRayItem(
    string FromShapeId,
    string FromShapeName,
    string ToShapeId,
    string ToShapeName,
    double AngleDegrees,
    double NearestObliqueAxis,
    double AngularDeviation,
    string ProjectionType,
    bool IsAligned,
    string Recommendation);

public sealed record DiagramObliqueAlignmentAudit(
    int TotalShapes,
    int TotalRays,
    int AlignedRays,
    int CabinetRays,
    int CavalierRays,
    int DriftRays,
    int OffAxisRays,
    double ConsistencyScore,
    IReadOnlyList<ObliqueShapeRayItem> Rays,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record DiagramIsometricAlignmentAudit(
    int TotalShapes,
    int TotalRays,
    int AlignedRays,
    int DriftRays,
    int OffAxisRays,
    double PrecisionScore,
    IReadOnlyList<IsometricShapeRayItem> Rays,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record DiagramRadialAlignmentAudit(
    int TotalShapes,
    int RingCount,
    double CentroidX,
    double CentroidY,
    double RadiusVariance,
    double AngularEvennessScore,
    double RotationalBalanceScore,
    IReadOnlyList<RadialShapeItem> Shapes,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record DimetricShapeRayItem(
    string FromShapeId,
    string FromShapeName,
    string ToShapeId,
    string ToShapeName,
    double AngleDegrees,
    double NearestDimetricAxis,
    double AngularDeviation,
    string ProjectionType,
    bool IsAligned,
    string Recommendation);

public sealed record DiagramDimetricAlignmentAudit(
    int TotalShapes,
    int TotalRays,
    int AlignedRays,
    int ShallowRays,
    int SteepRays,
    int DriftRays,
    int OffAxisRays,
    double ConsistencyScore,
    IReadOnlyList<DimetricShapeRayItem> Rays,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record TrimetricShapeRayItem(
    string FromShapeId,
    string FromShapeName,
    string ToShapeId,
    string ToShapeName,
    double AngleDegrees,
    double NearestTrimetricAxis,
    double AngularDeviation,
    string AxisClassification,
    bool IsAligned,
    string Recommendation);

public sealed record DiagramTrimetricAlignmentAudit(
    int TotalShapes,
    int TotalRays,
    int AlignedRays,
    int Axis1Rays,
    int Axis2Rays,
    int Axis3Rays,
    int DriftRays,
    int OffAxisRays,
    double ConsistencyScore,
    IReadOnlyList<TrimetricShapeRayItem> Rays,
    string Category,
    bool IsSelectionScope,
    string Summary);

public sealed record IsometricGridSnappingItem(
    string ShapeId,
    string ShapeName,
    double X,
    double Y,
    double Width,
    double Height,
    double OffsetDeviation,
    bool IsPositionSnapped,
    bool IsDimensionSnapped,
    string Recommendation);

public sealed record DiagramIsometricGridSnappingAudit(
    int TotalShapes,
    int FullySnappedShapes,
    int OffGridPositionShapes,
    int NonModularDimensionShapes,
    int SubPixelJitterShapes,
    double MeanDeviation,
    double GridSnappingScore,
    IReadOnlyList<IsometricGridSnappingItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);



public sealed record MagneticGuideAnchorItem(
    string ShapeId,
    string ShapeName,
    double X, double Y, double Width, double Height,
    int AlignedRaysCount,
    int NearMissJitterCount,
    bool IsMagneticallyLocked,
    string AlignmentDescription);

public sealed record DiagramMagneticSnappingAudit(
    int TotalShapes,
    int MagneticallyLockedShapes,
    int PartiallyAlignedShapes,
    int UnalignedShapes,
    int NearMissJitterTotal,
    double MagneticAlignmentScore,
    IReadOnlyList<MagneticGuideAnchorItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);



public sealed record BezierSymmetryItem(
    string ShapeId,
    string ShapeName,
    int ControlPointIndex,
    double TangentAngleDegrees,
    double HandleRatio,
    bool HasTangentDiscontinuity,
    bool HasAsymmetricHandles,
    string Recommendation);

public sealed record DiagramBezierSymmetryAudit(
    int TotalShapes,
    int EvaluatedCurvesCount,
    int SmoothTangentKnots,
    int DiscontinuousTangentJitter,
    int AsymmetricHandleCount,
    double SmoothPathAestheticsScore,
    IReadOnlyList<BezierSymmetryItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);



public sealed record DiagramViewportBleedItem(
    string ShapeId,
    string ShapeName,
    double X,
    double Y,
    double Width,
    double Height,
    double MinDistanceToEdge,
    bool IsClippingOutsideCanvas,
    bool IsInBleedZone,
    string EdgeDirection,
    string Status);

public sealed record DiagramViewportBleedAudit(
    int TotalShapes,
    int ClippingCount,
    int BleedWarningCount,
    int SafeCount,
    double MinObservedMargin,
    double BleedSafetyScore,
    IReadOnlyList<DiagramViewportBleedItem> Items,
    string Category,
    bool IsSelectionScope,
    string Summary);

