using CommunityToolkit.Mvvm.ComponentModel;

namespace MarkSmith.ViewModels.Mermaid;

public enum ConnectorRoutingMode
{
    Orthogonal,
    Bezier,
    Straight
}

public partial class DiagramConnectorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _sourceNodeId = string.Empty;

    [ObservableProperty]
    private string _sourceAnchor = "Bottom";

    [ObservableProperty]
    private string _targetNodeId = string.Empty;

    [ObservableProperty]
    private string _targetAnchor = "Top";

    [ObservableProperty]
    private string _lineStyle = "Solid"; // Solid, Dashed, Thick

    [ObservableProperty]
    private string _startHead = "None"; // None, Normal, Cross, Circle, Diamond

    [ObservableProperty]
    private string _endHead = "Normal"; // Normal, Cross, Circle, None, Inheritance, Aggregation, Composition, CrowsFoot

    [ObservableProperty]
    private string? _label;

    [ObservableProperty]
    private double _sourceX;

    [ObservableProperty]
    private double _sourceY;

    [ObservableProperty]
    private double _targetX;

    [ObservableProperty]
    private double _targetY;

    [ObservableProperty]
    private string _pathData = string.Empty;

    [ObservableProperty]
    private double _midpointX;

    [ObservableProperty]
    private double _midpointY;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ConnectorRoutingMode _routingMode = ConnectorRoutingMode.Orthogonal;

    [ObservableProperty]
    private string _strokeColor = "#8D99AE";

    [ObservableProperty]
    private double _strokeWidth = 2.0;

    /// <summary>Pointer is over the connector (UI state only; never saved or synced).</summary>
    [ObservableProperty]
    private bool _isHovered;

    // ---- What the canvas draws (derived; see ConnectorAppearance) ----

    /// <summary>Selection colour shared with node selection chrome.</summary>
    public const string SelectionColor = "#4CC9F0";

    /// <summary>The canvas background, used to fill hollow markers so the line doesn't show through.</summary>
    public const string CanvasColor = "#1E1E2E";

    [ObservableProperty]
    private string _startMarkerData = string.Empty;

    [ObservableProperty]
    private string _endMarkerData = string.Empty;

    [ObservableProperty]
    private string _startMarkerFill = "Transparent";

    [ObservableProperty]
    private string _endMarkerFill = "Transparent";

    [ObservableProperty]
    private bool _isDashed;

    [ObservableProperty]
    private double _displayStrokeWidth = 2.0;

    [ObservableProperty]
    private string _displayStroke = "#8D99AE";

    /// <summary>Opacity of the soft halo behind the line: shown on hover, stronger when selected.</summary>
    [ObservableProperty]
    private double _haloOpacity;

    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    // Where each end is and which way the line is travelling there (start: leaving the source,
    // end: arriving at the target). Kept so markers can be rebuilt when only the style changes.
    private MarkSmith.Core.Mermaid.Routing.Point _startTip, _endTip;
    private double _startDirX, _startDirY = 1, _endDirX, _endDirY = 1;

    partial void OnLabelChanged(string? value) => OnPropertyChanged(nameof(HasLabel));
    partial void OnLineStyleChanged(string value) => RebuildMarkers();
    partial void OnStartHeadChanged(string value) => RebuildMarkers();
    partial void OnEndHeadChanged(string value) => RebuildMarkers();
    partial void OnStrokeWidthChanged(double value) => RebuildMarkers();
    partial void OnStrokeColorChanged(string value) => RebuildMarkers();
    partial void OnIsSelectedChanged(bool value) => RebuildMarkers();
    partial void OnIsHoveredChanged(bool value) => RebuildMarkers();

    private void RebuildMarkers()
    {
        var look = MarkSmith.Core.Mermaid.Routing.ConnectorAppearance.Resolve(LineStyle, StartHead, EndHead);
        IsDashed = look.Dashed;
        DisplayStrokeWidth = StrokeWidth * look.WidthScale;
        DisplayStroke = IsSelected ? SelectionColor : StrokeColor;
        HaloOpacity = IsSelected ? 0.4 : IsHovered ? 0.22 : 0;

        (StartMarkerData, StartMarkerFill) = Marker(look.Start, _startTip, -_startDirX, -_startDirY);
        (EndMarkerData, EndMarkerFill) = Marker(look.End, _endTip, _endDirX, _endDirY);
    }

    private (string Data, string Fill) Marker(MarkSmith.Core.Mermaid.Routing.ConnectorMarker kind,
        MarkSmith.Core.Mermaid.Routing.Point tip, double dirX, double dirY)
    {
        var shape = MarkSmith.Core.Mermaid.Routing.ConnectorAppearance.Shape(kind, tip, dirX, dirY, DisplayStrokeWidth);
        if (shape is not { } s) return (string.Empty, "Transparent");
        return (s.PathData, s.StrokeOnly ? "Transparent" : s.FilledWithStroke ? DisplayStroke : CanvasColor);
    }

    private void SetEnds(double sx, double sy, double sDirX, double sDirY, double tx, double ty, double tDirX, double tDirY)
    {
        _startTip = new(sx, sy);
        _endTip = new(tx, ty);
        if (Math.Abs(sDirX) + Math.Abs(sDirY) > 1e-6) (_startDirX, _startDirY) = (sDirX, sDirY);
        if (Math.Abs(tDirX) + Math.Abs(tDirY) > 1e-6) (_endDirX, _endDirY) = (tDirX, tDirY);
        RebuildMarkers();
    }

    // A polyline's direction at each end: away from the first point, into the last one.
    private void SetEnds(IReadOnlyList<MarkSmith.Core.Mermaid.Routing.Point> pts)
    {
        var a = pts[0];
        var b = pts.Skip(1).FirstOrDefault(p => p != a);
        var z = pts[^1];
        var y = pts.Take(pts.Count - 1).LastOrDefault(p => p != z);
        SetEnds(a.X, a.Y, b.X - a.X, b.Y - a.Y, z.X, z.Y, z.X - y.X, z.Y - y.Y);
    }

    public void UpdateGeometry(
        Point sourcePoint,
        Point targetPoint,
        Rect? sourceNodeBounds = null,
        Rect? targetNodeBounds = null,
        IEnumerable<Rect>? obstacleNodeBounds = null)
    {
        SourceX = sourcePoint.X;
        SourceY = sourcePoint.Y;
        TargetX = targetPoint.X;
        TargetY = targetPoint.Y;

        MidpointX = (SourceX + TargetX) / 2;
        MidpointY = (SourceY + TargetY) / 2;

        switch (RoutingMode)
        {
            case ConnectorRoutingMode.Straight:
                PathData = System.FormattableString.Invariant($"M {SourceX:F1},{SourceY:F1} L {TargetX:F1},{TargetY:F1}");
                SetEnds(SourceX, SourceY, TargetX - SourceX, TargetY - SourceY, TargetX, TargetY, TargetX - SourceX, TargetY - SourceY);
                break;

            case ConnectorRoutingMode.Bezier:
                double ctrlDistance = Math.Max(40, Math.Abs(TargetY - SourceY) * 0.5);
                double c1X = SourceX;
                double c1Y = SourceAnchor == "Top" ? SourceY - ctrlDistance : (SourceAnchor == "Bottom" ? SourceY + ctrlDistance : SourceY);
                double c2X = TargetX;
                double c2Y = TargetAnchor == "Bottom" ? TargetY + ctrlDistance : (TargetAnchor == "Top" ? TargetY - ctrlDistance : TargetY);
                PathData = System.FormattableString.Invariant($"M {SourceX:F1},{SourceY:F1} C {c1X:F1},{c1Y:F1} {c2X:F1},{c2Y:F1} {TargetX:F1},{TargetY:F1}");
                // Side anchors put the control point on the end itself; fall back to the chord.
                bool flatStart = Math.Abs(c1Y - SourceY) < 1e-6, flatEnd = Math.Abs(c2Y - TargetY) < 1e-6;
                SetEnds(SourceX, SourceY, flatStart ? TargetX - SourceX : 0, flatStart ? TargetY - SourceY : c1Y - SourceY,
                        TargetX, TargetY, flatEnd ? TargetX - SourceX : 0, flatEnd ? TargetY - SourceY : TargetY - c2Y);
                break;

            case ConnectorRoutingMode.Orthogonal:
            default:
                if (sourceNodeBounds.HasValue && targetNodeBounds.HasValue)
                {
                    var srcR = (MarkSmith.Core.Mermaid.Routing.Rect)sourceNodeBounds.Value;
                    var tgtR = (MarkSmith.Core.Mermaid.Routing.Rect)targetNodeBounds.Value;
                    var obsR = obstacleNodeBounds?.Select(r => (MarkSmith.Core.Mermaid.Routing.Rect)r) ?? Array.Empty<MarkSmith.Core.Mermaid.Routing.Rect>();
                    var srcP = new MarkSmith.Core.Mermaid.Routing.Point(SourceX, SourceY);
                    var tgtP = new MarkSmith.Core.Mermaid.Routing.Point(TargetX, TargetY);

                    var routePoints = MarkSmith.Core.Mermaid.Routing.OrthogonalRouter.Route(srcR, tgtR, obsR, srcP, tgtP);

                    if (routePoints.Count >= 2)
                    {
                        PathData = MarkSmith.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(routePoints, 8.0);

                        int midIndex = routePoints.Count / 2;
                        MidpointX = (routePoints[midIndex - 1].X + routePoints[midIndex].X) / 2;
                        MidpointY = (routePoints[midIndex - 1].Y + routePoints[midIndex].Y) / 2;
                        SetEnds(routePoints);
                        break;
                    }
                }

                if (Math.Abs(SourceX - TargetX) < 5 || Math.Abs(SourceY - TargetY) < 5)
                {
                    PathData = System.FormattableString.Invariant($"M {SourceX:F1},{SourceY:F1} L {TargetX:F1},{TargetY:F1}");
                    SetEnds(SourceX, SourceY, TargetX - SourceX, TargetY - SourceY, TargetX, TargetY, TargetX - SourceX, TargetY - SourceY);
                }
                else if (SourceAnchor is "Left" or "Right" && TargetAnchor is "Left" or "Right")
                {
                    double midX = (SourceX + TargetX) / 2;
                    var pts = new List<MarkSmith.Core.Mermaid.Routing.Point>
                    {
                        new(SourceX, SourceY),
                        new(midX, SourceY),
                        new(midX, TargetY),
                        new(TargetX, TargetY)
                    };
                    PathData = MarkSmith.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(pts, 8.0);
                    SetEnds(pts);
                }
                else
                {
                    double midY = (SourceY + TargetY) / 2;
                    var pts = new List<MarkSmith.Core.Mermaid.Routing.Point>
                    {
                        new(SourceX, SourceY),
                        new(SourceX, midY),
                        new(TargetX, midY),
                        new(TargetX, TargetY)
                    };
                    PathData = MarkSmith.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(pts, 8.0);
                    SetEnds(pts);
                }
                break;
        }
    }

    /// <summary>
    /// Draws the connector along a route someone else worked out (a sequence message runs
    /// lifeline to lifeline on its own row; see <see cref="MarkSmith.Core.Mermaid.Routing.SequenceLayout"/>),
    /// with its label centred on (<paramref name="labelX"/>, <paramref name="labelY"/>).
    /// </summary>
    public void SetRoute(IReadOnlyList<MarkSmith.Core.Mermaid.Routing.Point> points, double labelX, double labelY)
    {
        if (points.Count < 2) return;
        SourceX = points[0].X;
        SourceY = points[0].Y;
        TargetX = points[^1].X;
        TargetY = points[^1].Y;
        MidpointX = labelX;
        MidpointY = labelY;
        PathData = points.Count == 2
            ? System.FormattableString.Invariant($"M {SourceX:F1},{SourceY:F1} L {TargetX:F1},{TargetY:F1}")
            : MarkSmith.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(points.ToList(), 6.0);
        SetEnds(points);
    }

    public void TranslateGeometry(double deltaX, double deltaY)
    {
        SourceX += deltaX;
        SourceY += deltaY;
        TargetX += deltaX;
        TargetY += deltaY;
        MidpointX += deltaX;
        MidpointY += deltaY;
        _startTip = new(_startTip.X + deltaX, _startTip.Y + deltaY);
        _endTip = new(_endTip.X + deltaX, _endTip.Y + deltaY);
        RebuildMarkers();

        if (!string.IsNullOrEmpty(PathData))
        {
            var sb = new System.Text.StringBuilder();
            var parts = PathData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Contains(','))
                {
                    var coords = part.Split(',');
                    if (coords.Length == 2 &&
                        double.TryParse(coords[0], System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(coords[1], System.Globalization.CultureInfo.InvariantCulture, out double y))
                    {
                        sb.Append(System.FormattableString.Invariant($"{x + deltaX:F1},{y + deltaY:F1} "));
                        continue;
                    }
                }
                sb.Append(part).Append(" ");
            }
            PathData = sb.ToString().TrimEnd();
        }
    }
}
