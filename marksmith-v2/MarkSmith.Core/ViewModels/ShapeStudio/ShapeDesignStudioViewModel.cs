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
    private string _fill = ShapeDesignStudioViewModel.ThemeAccentHex();

    /// <summary>Optional explicit label colour (#RRGGBB); null = auto-guarded against the fill.</summary>
    public string? TextColor { get; set; }

    [ObservableProperty]
    private string _text = "";

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

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(TextForegroundHex));

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

    [ObservableProperty]
    private string _activeTool = "roundrect";

    [ObservableProperty]
    private ObservableCollection<ShapeCanvasItemViewModel> _shapes = new();

    [ObservableProperty]
    private ShapeCanvasItemViewModel? _selectedShape;

    [ObservableProperty]
    private string _statusMessage = "Ready — pick a SmartArt template, click a shape to draw, or trace an image.";

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
            if (s.IsSelected != (s == value)) s.IsSelected = s == value;
        }
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
    }

    public string[] GetPaletteColors() =>
        ColorPalettes.TryGetValue(SelectedPaletteName ?? "Office Blue", out var colors)
            ? colors
            : ColorPalettes["Office Blue"];

    public ShapeDesignStudioViewModel()
    {
        RegisterPresets();
        UpdateFilteredPresets();
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
        StatusMessage = $"Placed {prst} at ({x:F0}, {y:F0})";
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

    [RelayCommand]
    public void DuplicateSelected()
    {
        if (SelectedShape == null) return;
        var s = SelectedShape;
        var clone = new ShapeCanvasItemViewModel
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
            IsSelected = true
        };
        Shapes.Add(clone);
        SelectedShape = clone;
        StatusMessage = $"Duplicated {clone.Prst}";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task RemoveSelectedAsync()
    {
        if (SelectedShape == null) return;
        Shapes.Remove(SelectedShape);
        SelectedShape = null;
        await RefreshCanvasModeAsync();
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

    [RelayCommand]
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
        StatusMessage = $"✓ Inserted {composed.Count} native DrawingML shapes into document.";
    }

    [RelayCommand]
    public void ApplyPaletteTheme()
    {
        if (Shapes.Count == 0) return;
        var colors = GetPaletteColors();
        int idx = 0;
        foreach (var s in Shapes)
        {
            if (s.PathPoints is not { Count: >= 2 })
            {
                s.Fill = colors[idx % colors.Length];
                idx++;
            }
        }
        StatusMessage = $"✓ Applied '{SelectedPaletteName}' theme across {Shapes.Count} shapes.";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
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

    [RelayCommand]
    public void GenerateCycleTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 270, 40, 180, 65, colors[0 % colors.Length], "1 · PLAN\nStrategy & Goals");
        AddShapeAt("roundrect", 480, 190, 180, 65, colors[1 % colors.Length], "2 · DO\nBuild & Execute");
        AddShapeAt("roundrect", 270, 340, 180, 65, colors[2 % colors.Length], "3 · CHECK\nTest & Validate");
        AddShapeAt("roundrect", 60, 190, 180, 65, colors[3 % colors.Length], "4 · ACT\nDeploy & Improve");

        AddShapeAt("circulararrow", 460, 100, 55, 55, colors[0 % colors.Length]);
        AddShapeAt("circulararrow", 460, 280, 55, 55, colors[1 % colors.Length]);
        AddShapeAt("circulararrow", 200, 280, 55, 55, colors[2 % colors.Length]);
        AddShapeAt("circulararrow", 200, 100, 55, 55, colors[3 % colors.Length]);
        StatusMessage = "✓ Generated PDCA Continuous Cycle";
    }

    [RelayCommand]
    public void GenerateBuildMeasureLearnTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 270, 50, 180, 65, colors[0 % colors.Length], "1 · BUILD\nCode & Features");
        AddShapeAt("roundrect", 450, 250, 180, 65, colors[1 % colors.Length], "2 · MEASURE\nMetrics & Signals");
        AddShapeAt("roundrect", 90, 250, 180, 65, colors[2 % colors.Length], "3 · LEARN\nInsights & Pivot");

        AddShapeAt("circulararrow", 440, 140, 60, 60, colors[0 % colors.Length]);
        AddShapeAt("circulararrow", 280, 310, 60, 60, colors[1 % colors.Length]);
        AddShapeAt("circulararrow", 150, 140, 60, 60, colors[2 % colors.Length]);
        StatusMessage = "✓ Generated Build-Measure-Learn Loop";
    }

    [RelayCommand]
    public void GenerateDesignThinkingTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("hexagon", 30, 140, 120, 110, colors[0 % colors.Length], "1 · EMPATHIZE\nUser Needs");
        AddShapeAt("hexagon", 160, 140, 120, 110, colors[1 % colors.Length], "2 · DEFINE\nProblem Frame");
        AddShapeAt("hexagon", 290, 140, 120, 110, colors[2 % colors.Length], "3 · IDEATE\nBrainstorm");
        AddShapeAt("hexagon", 420, 140, 120, 110, colors[3 % colors.Length], "4 · PROTOTYPE\nMockups & Code");
        AddShapeAt("hexagon", 550, 140, 120, 110, colors[4 % colors.Length], "5 · TEST\nUser Feedback");
        StatusMessage = "✓ Generated Design Thinking 5-Phase Cycle";
    }

    [RelayCommand]
    public void GenerateDevOpsLoopTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("roundrect", 80, 60, 140, 55, colors[0 % colors.Length], "1. PLAN");
        AddShapeAt("roundrect", 40, 160, 140, 55, colors[1 % colors.Length], "2. CODE");
        AddShapeAt("roundrect", 80, 260, 140, 55, colors[2 % colors.Length], "3. BUILD");
        AddShapeAt("roundrect", 270, 160, 160, 55, colors[3 % colors.Length], "4. TEST & STAGE");
        AddShapeAt("roundrect", 480, 60, 140, 55, colors[4 % colors.Length], "5. RELEASE");
        AddShapeAt("roundrect", 520, 160, 140, 55, colors[5 % colors.Length], "6. DEPLOY");
        AddShapeAt("roundrect", 480, 260, 140, 55, colors[0 % colors.Length], "7. MONITOR");
        StatusMessage = "✓ Generated DevOps Infinity Delivery Loop";
    }

    [RelayCommand]
    public void GenerateFeedbackSpiralTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("ellipse", 160, 60, 380, 280, colors[0 % colors.Length], "");
        AddShapeAt("ellipse", 210, 100, 280, 200, colors[1 % colors.Length], "");
        AddShapeAt("ellipse", 260, 140, 180, 120, colors[2 % colors.Length], "CORE INSIGHT\nImmediate Signal");
        AddShapeAt("chevron", 50, 175, 140, 50, colors[3 % colors.Length], "Outer Loop");
        AddShapeAt("chevron", 510, 175, 140, 50, colors[4 % colors.Length], "Fast Pivot");
        StatusMessage = "✓ Generated Continuous Feedback Spiral";
    }

    [RelayCommand]
    public void GenerateTimelineTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("chevron", 40, 150, 155, 85, colors[0 % colors.Length], "PHASE 1\nDiscovery");
        AddShapeAt("chevron", 205, 150, 155, 85, colors[1 % colors.Length], "PHASE 2\nPrototype");
        AddShapeAt("chevron", 370, 150, 155, 85, colors[2 % colors.Length], "PHASE 3\nBeta Launch");
        AddShapeAt("chevron", 535, 150, 155, 85, colors[4 % colors.Length], "PHASE 4\nScale Out");
        StatusMessage = "✓ Generated 4-Phase Roadmap Timeline";
    }

    [RelayCommand]
    public void GeneratePipeline5StepTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("chevron", 30, 150, 125, 85, colors[0 % colors.Length], "STAGE 1\nIntake");
        AddShapeAt("chevron", 160, 150, 125, 85, colors[1 % colors.Length], "STAGE 2\nTriage");
        AddShapeAt("chevron", 290, 150, 125, 85, colors[2 % colors.Length], "STAGE 3\nDesign");
        AddShapeAt("chevron", 420, 150, 125, 85, colors[3 % colors.Length], "STAGE 4\nVerify");
        AddShapeAt("chevron", 550, 150, 125, 85, colors[4 % colors.Length], "STAGE 5\nDeliver");
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
        AddShapeAt("roundrect", 40, 150, 140, 75, colors[0 % colors.Length], "Phase 1: Concept");
        AddShapeAt("diamond", 200, 145, 90, 85, colors[2 % colors.Length], "Gate 1");
        AddShapeAt("roundrect", 310, 150, 140, 75, colors[1 % colors.Length], "Phase 2: Build");
        AddShapeAt("diamond", 470, 145, 90, 85, colors[2 % colors.Length], "Gate 2");
        AddShapeAt("roundrect", 580, 150, 120, 75, colors[4 % colors.Length], "Phase 3: Launch");
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
        AddShapeAt("rect", 40, 40, 640, 80, colors[0 % colors.Length], "LANE 1 · PRODUCT MANAGEMENT");
        AddShapeAt("rect", 40, 135, 640, 80, colors[1 % colors.Length], "LANE 2 · ENGINEERING & QA");
        AddShapeAt("rect", 40, 230, 640, 80, colors[2 % colors.Length], "LANE 3 · OPERATIONS & SECURITY");

        AddShapeAt("roundrect", 60, 50, 140, 60, "FFFFFF", "User Stories");
        AddShapeAt("roundrect", 250, 145, 140, 60, "FFFFFF", "Build & Test");
        AddShapeAt("roundrect", 450, 240, 140, 60, "FFFFFF", "Release & Monitor");
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
        AddShapeAt("ellipse", 140, 70, 260, 260, colors[0 % colors.Length], "STRATEGY\n\nMarket Reach &\nPositioning");
        AddShapeAt("ellipse", 300, 70, 260, 260, colors[1 % colors.Length], "EXECUTION\n\nAgile Velocity &\nQuality");
        AddShapeAt("roundrect", 260, 160, 180, 80, colors[2 % colors.Length], "CORE OVERLAP\nCompetitive Moat");
        StatusMessage = "✓ Generated 2-Set Core Overlap Venn";
    }

    [RelayCommand]
    public void GenerateBullseyeTargetTemplate()
    {
        ClearAll();
        var colors = GetPaletteColors();
        AddShapeAt("ellipse", 160, 30, 380, 340, colors[0 % colors.Length], "OUTER RING · Long-term Frontier Vision");
        AddShapeAt("ellipse", 220, 80, 260, 240, colors[1 % colors.Length], "MIDDLE RING · Mid-term Growth Drivers");
        AddShapeAt("ellipse", 280, 130, 140, 140, colors[2 % colors.Length], "BULLSEYE\nCore Focus");
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
        var colors = GetPaletteColors();
        AddShapeAt("ellipse", 110, 20, 480, 360, colors[0 % colors.Length], "Layer 1 · Perimeter Firewalls & Cloud WAF");
        AddShapeAt("ellipse", 160, 60, 380, 280, colors[1 % colors.Length], "Layer 2 · Zero Trust Network Access");
        AddShapeAt("ellipse", 210, 100, 280, 200, colors[2 % colors.Length], "Layer 3 · Identity & Access Management");
        AddShapeAt("ellipse", 260, 140, 180, 120, colors[3 % colors.Length], "Layer 4 · DATA\nEncrypted at Rest");
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
        AddShapeAt("chevron", 40, 150, 120, 85, colors[0 % colors.Length], "1. EXTRACT\nRaw Sources");
        AddShapeAt("chevron", 165, 150, 120, 85, colors[1 % colors.Length], "2. INGEST\nKafka Queue");
        AddShapeAt("chevron", 290, 150, 120, 85, colors[2 % colors.Length], "3. TRANSFORM\nSpark Engine");
        AddShapeAt("chevron", 415, 150, 120, 85, colors[3 % colors.Length], "4. STORE\nIceberg Lake");
        AddShapeAt("chevron", 540, 150, 120, 85, colors[4 % colors.Length], "5. SERVE\nBI Dashboard");
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
        AddShapeAt("trapezoid", 100, 40, 500, 65, colors[0 % colors.Length], "ACQUISITION · Inbound Leads");
        AddShapeAt("trapezoid", 180, 115, 340, 65, colors[1 % colors.Length], "ACTIVATION · Onboarding Users");
        AddShapeAt("triangle", 250, 190, 200, 60, colors[2 % colors.Length], "RETENTION · Core Power Users");
        AddShapeAt("trapezoid", 140, 260, 420, 65, colors[3 % colors.Length], "EXPANSION & REFERRALS · Enterprise Champions");
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
        AddShapeAt("hexagon", 280, 140, 140, 120, colors[0 % colors.Length], "CORE\nENGINE");
        AddShapeAt("hexagon", 190, 60, 140, 120, colors[1 % colors.Length], "Security");
        AddShapeAt("hexagon", 370, 60, 140, 120, colors[2 % colors.Length], "Speed");
        AddShapeAt("hexagon", 100, 140, 140, 120, colors[3 % colors.Length], "InterOp");
        AddShapeAt("hexagon", 460, 140, 140, 120, colors[4 % colors.Length], "Vector");
        AddShapeAt("hexagon", 190, 220, 140, 120, colors[5 % colors.Length], "SmartArt");
        AddShapeAt("hexagon", 370, 220, 140, 120, colors[0 % colors.Length], "Export");
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

    [RelayCommand]
    public void AlignLeft()
    {
        if (Shapes.Count < 2) return;
        double minX = Shapes.Min(s => s.X);
        foreach (var s in Shapes) s.X = minX;
        StatusMessage = "Aligned shapes to Left";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void AlignCenter()
    {
        if (Shapes.Count < 2) return;
        double avgCenter = Shapes.Average(s => s.X + s.Width / 2.0);
        foreach (var s in Shapes) s.X = avgCenter - s.Width / 2.0;
        StatusMessage = "Aligned shapes to Center";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void AlignRight()
    {
        if (Shapes.Count < 2) return;
        double maxRight = Shapes.Max(s => s.X + s.Width);
        foreach (var s in Shapes) s.X = maxRight - s.Width;
        StatusMessage = "Aligned shapes to Right";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void AlignTop()
    {
        if (Shapes.Count < 2) return;
        double minY = Shapes.Min(s => s.Y);
        foreach (var s in Shapes) s.Y = minY;
        StatusMessage = "Aligned shapes to Top";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void AlignMiddle()
    {
        if (Shapes.Count < 2) return;
        double avgMiddle = Shapes.Average(s => s.Y + s.Height / 2.0);
        foreach (var s in Shapes) s.Y = avgMiddle - s.Height / 2.0;
        StatusMessage = "Aligned shapes to Middle";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void AlignBottom()
    {
        if (Shapes.Count < 2) return;
        double maxBottom = Shapes.Max(s => s.Y + s.Height);
        foreach (var s in Shapes) s.Y = maxBottom - s.Height;
        StatusMessage = "Aligned shapes to Bottom";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void DistributeHorizontal()
    {
        if (Shapes.Count < 3) return;
        var ordered = Shapes.OrderBy(s => s.X).ToList();
        double start = ordered[0].X;
        double end = ordered[^1].X;
        double step = (end - start) / (ordered.Count - 1);
        for (int i = 0; i < ordered.Count; i++) ordered[i].X = start + i * step;
        StatusMessage = "Distributed shapes horizontally";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void DistributeVertical()
    {
        if (Shapes.Count < 3) return;
        var ordered = Shapes.OrderBy(s => s.Y).ToList();
        double start = ordered[0].Y;
        double end = ordered[^1].Y;
        double step = (end - start) / (ordered.Count - 1);
        for (int i = 0; i < ordered.Count; i++) ordered[i].Y = start + i * step;
        StatusMessage = "Distributed shapes vertically";
        CanvasChanged?.Invoke(this, EventArgs.Empty);
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

            Shapes = new ObservableCollection<ShapeCanvasItemViewModel>(traced.Select(ToItem));
            SelectedShape = null;
            LineStats = $"{traced.Count:N0} lines";

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

            Shapes = new ObservableCollection<ShapeCanvasItemViewModel>(parsed.Select(ToItem));
            SelectedShape = null;
            LineStats = $"{parsed.Count:N0} shapes";
            StatusMessage = $"Loaded {parsed.Count} shapes from markdown.";
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

    [RelayCommand]
    public System.Threading.Tasks.Task ExportDocxAsync() => ExportToWordAsync(template: false);

    [RelayCommand]
    public System.Threading.Tasks.Task ExportDotxAsync() => ExportToWordAsync(template: true);

    private async System.Threading.Tasks.Task ExportToWordAsync(bool template)
    {
        try
        {
            if (Shapes.Count == 0) { StatusMessage = "Nothing to export."; return; }

            var composed = SnapshotComposed();
            double maxX = composed.Max(s => s.X + s.W);
            double maxY = composed.Max(s => s.Y + s.H);
            double w = Math.Max(2, maxX + 0.5);
            double h = Math.Max(2, maxY + 0.5);

            string ext = template ? ".dotx" : ".docx";
            // Full date stamp, not just HHmmss — an export today must not silently overwrite a
            // leftover file from a previous day that happened at the same clock time.
            string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
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
            StatusMessage = $"✓ Exported {composed.Count:N0} native DrawingML shapes → {outPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
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
