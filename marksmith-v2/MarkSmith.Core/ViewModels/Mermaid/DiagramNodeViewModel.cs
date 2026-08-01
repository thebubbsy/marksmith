using CommunityToolkit.Mvvm.ComponentModel;

namespace MdToPdf.ViewModels.Mermaid;

public partial class DiagramNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _labelText = "New Node";

    [ObservableProperty]
    private string _category = "Flowchart";

    [ObservableProperty]
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
    private double _height = 60;

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
    private string _fillColor = "#2B2D42";

    [ObservableProperty]
    private string _strokeColor = "#8D99AE";

    [ObservableProperty]
    private double _strokeWidth = 2.0;

    [ObservableProperty]
    private object? _extraData;

    [ObservableProperty]
    private bool _hasCustomPosition;

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

    public void RecalculateBoundsForText()
    {
        if (string.IsNullOrWhiteSpace(LabelText)) return;
        // Approximate width & height based on text length and lines
        var lines = LabelText.Split('\n');
        int maxLineLen = lines.Max(l => l.Length);
        double estWidth = Math.Max(120, maxLineLen * 10 + 30);
        double estHeight = Math.Max(50, lines.Length * 22 + 24);

        Width = estWidth;
        Height = estHeight;
    }
}
