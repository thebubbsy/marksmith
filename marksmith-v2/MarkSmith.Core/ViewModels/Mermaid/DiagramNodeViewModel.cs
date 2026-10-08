using CommunityToolkit.Mvvm.ComponentModel;
using MarkSmith.Services;

namespace MarkSmith.ViewModels.Mermaid;

public partial class DiagramNodeViewModel : ObservableObject
{
    // Theme-aware default fill: a brand-new node sits on the studio's dark canvas (#1E1E2E), so the
    // current theme's accent is routed through ContrastGuard.EnsureVisibleFill — a theme-derived
    // fill can never blend into the canvas (the "black node on dark canvas" review claim).
    public DiagramNodeViewModel()
    {
        var theme = AppServices.Themes.GetOrDefault(AppServices.Settings.Current.Theme);
        _fillColor = "#" + ContrastGuard.EnsureVisibleFill(theme.Heading, "1E1E2E");
    }

    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _labelText = "New Node";

    [ObservableProperty]
    private string _category = "Flowchart";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxCornerRadius))]
    private string _shape = "Rectangle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorTop))]
    [NotifyPropertyChangedFor(nameof(AnchorBottom))]
    [NotifyPropertyChangedFor(nameof(AnchorLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorRight))]
    [NotifyPropertyChangedFor(nameof(AnchorTopLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorTopRight))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomRight))]
    private double _x = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorTop))]
    [NotifyPropertyChangedFor(nameof(AnchorBottom))]
    [NotifyPropertyChangedFor(nameof(AnchorLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorRight))]
    [NotifyPropertyChangedFor(nameof(AnchorTopLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorTopRight))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomRight))]
    private double _y = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorTop))]
    [NotifyPropertyChangedFor(nameof(AnchorBottom))]
    [NotifyPropertyChangedFor(nameof(AnchorLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorRight))]
    [NotifyPropertyChangedFor(nameof(AnchorTopLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorTopRight))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomRight))]
    private double _width = 140;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorTop))]
    [NotifyPropertyChangedFor(nameof(AnchorBottom))]
    [NotifyPropertyChangedFor(nameof(AnchorLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorRight))]
    [NotifyPropertyChangedFor(nameof(AnchorTopLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorTopRight))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomLeft))]
    [NotifyPropertyChangedFor(nameof(AnchorBottomRight))]
    [NotifyPropertyChangedFor(nameof(BoxCornerRadius))]
    private double _height = 60;

    /// <summary>
    /// Corner radius of the canvas box for box-shaped nodes, so each reads as what it will render
    /// as: square Rectangle/Subroutine, rounded Rounded Rectangle, a true pill for Stadium (half the
    /// height — a fixed large radius clamps on both axes and turns the box into an ellipse).
    /// </summary>
    public double BoxCornerRadius => Shape switch
    {
        "RoundedRectangle" or "NormalState" or "BranchNode" => 10,
        "Stadium" or "TaskBar" => Math.Max(0, Height / 2),
        _ => 2,
    };

    [ObservableProperty]
    private int _zIndex = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnchors))]
    private bool _isSelected;

    /// <summary>True while the pointer hovers the node — drives the hover glow and reveals
    /// the connector anchor dots (world-class editors only show anchors on hover/selection).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnchors))]
    private bool _isHovered;

    /// <summary>True while a connector is being drawn and this node is the prospective drop
    /// target — drives the bright "connect here" highlight ring.</summary>
    [ObservableProperty]
    private bool _isConnectionTarget;

    /// <summary>Anchor dots are revealed when the node is hovered OR selected.</summary>
    public bool ShowAnchors => IsSelected || IsHovered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelColor))]
    private string _fillColor = "#2B2D42";

    /// <summary>The label colour that reads on <see cref="FillColor"/>: the studio's light text on
    /// dark fills, near-black on light ones. A fixed light label vanished on light fills (the theme's
    /// heading colour on light themes, the Monochrome Print preset, a pale custom fill).</summary>
    public string LabelColor => ReadableLabelOn(FillColor);

    public static string ReadableLabelOn(string fillHex)
    {
        const string light = "#EDF2F4", dark = "#111827";
        double lightRatio = ContrastGuard.GetContrastRatio(light, fillHex), darkRatio = ContrastGuard.GetContrastRatio(dark, fillHex);
        string best = lightRatio >= darkRatio ? light : dark;
        if (Math.Max(lightRatio, darkRatio) >= 4.5) return best;
        // Saturated mid-tone fills (Cyberpunk's #FF003C) miss AA with both studio tones by a
        // hair; pure black or white is the most a label can get.
        return best == light ? "#FFFFFF" : "#000000";
    }

    [ObservableProperty]
    private string _strokeColor = "#8D99AE";

    [ObservableProperty]
    private double _strokeWidth = 2.0;

    [ObservableProperty]
    private object? _extraData;

    [ObservableProperty]
    private bool _hasCustomPosition;

    /// <summary>How far a sequence participant's dashed lifeline runs below its header box (0 = none).
    /// Set by the studio's sequence layout; UI only, never saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLifeline))]
    [NotifyPropertyChangedFor(nameof(LifelineBottom))]
    private double _lifelineLength;

    public bool HasLifeline => LifelineLength > 0;
    public double LifelineX => X + Width / 2;
    public double LifelineTop => Y + Height;
    public double LifelineBottom => Y + Height + LifelineLength;

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(LifelineX));
    partial void OnWidthChanged(double value) => OnPropertyChanged(nameof(LifelineX));
    partial void OnYChanged(double value) { OnPropertyChanged(nameof(LifelineTop)); OnPropertyChanged(nameof(LifelineBottom)); }
    partial void OnHeightChanged(double value) { OnPropertyChanged(nameof(LifelineTop)); OnPropertyChanged(nameof(LifelineBottom)); }

    // Computed Anchor Points
    public Point AnchorTop => new(X + Width / 2, Y);
    public Point AnchorRight => new(X + Width, Y + Height / 2);
    public Point AnchorBottom => new(X + Width / 2, Y + Height);
    public Point AnchorLeft => new(X, Y + Height / 2);

    public Point AnchorTopLeft => new(X, Y);
    public Point AnchorTopRight => new(X + Width, Y);
    public Point AnchorBottomLeft => new(X, Y + Height);
    public Point AnchorBottomRight => new(X + Width, Y + Height);

    public Point GetAnchorPoint(string anchorName) => anchorName switch
    {
        "Top" => AnchorTop,
        "Right" => AnchorRight,
        "Bottom" => AnchorBottom,
        "Left" => AnchorLeft,
        "TopLeft" => AnchorTopLeft,
        "TopRight" => AnchorTopRight,
        "BottomLeft" => AnchorBottomLeft,
        "BottomRight" => AnchorBottomRight,
        _ => AnchorTop
    };

    /// <summary>A state diagram's [*] start/end point: drawn as a dot, never sized to its label.</summary>
    public bool IsPseudoState => Shape is "Start" or "End";

    /// <summary>
    /// Grows (never shrinks) the node so its label fits: a class box with five members used to
    /// load at the default 140x60 and clip everything after the second line.
    /// </summary>
    public void GrowToFitLabel()
    {
        if (string.IsNullOrWhiteSpace(LabelText) || IsPseudoState) return;
        var lines = MarkSmith.Mermaid.Generator.MermaidCodeGenerator.Lines(LabelText);
        int maxLineLen = lines.Max(l => l.Length);
        Width = Math.Max(Width, maxLineLen * 8.5 + 32);
        Height = Math.Max(Height, lines.Length * 19 + 24);
    }

    public void RecalculateBoundsForText()
    {
        if (string.IsNullOrWhiteSpace(LabelText)) return;
        // Approximate width & height based on text length and lines
        var lines = MarkSmith.Mermaid.Generator.MermaidCodeGenerator.Lines(LabelText);
        int maxLineLen = lines.Max(l => l.Length);
        double estWidth = Math.Max(120, maxLineLen * 10 + 30);
        double estHeight = Math.Max(50, lines.Length * 22 + 24);

        Width = estWidth;
        Height = estHeight;
    }
}
