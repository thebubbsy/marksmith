using CommunityToolkit.Mvvm.ComponentModel;

namespace MdToPdf.ViewModels.Mermaid;

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
                break;

            case ConnectorRoutingMode.Bezier:
                double ctrlDistance = Math.Max(40, Math.Abs(TargetY - SourceY) * 0.5);
                double c1X = SourceX;
                double c1Y = SourceAnchor == "Top" ? SourceY - ctrlDistance : (SourceAnchor == "Bottom" ? SourceY + ctrlDistance : SourceY);
                double c2X = TargetX;
                double c2Y = TargetAnchor == "Bottom" ? TargetY + ctrlDistance : (TargetAnchor == "Top" ? TargetY - ctrlDistance : TargetY);
                PathData = System.FormattableString.Invariant($"M {SourceX:F1},{SourceY:F1} C {c1X:F1},{c1Y:F1} {c2X:F1},{c2Y:F1} {TargetX:F1},{TargetY:F1}");
                break;

            case ConnectorRoutingMode.Orthogonal:
            default:
                if (sourceNodeBounds.HasValue && targetNodeBounds.HasValue)
                {
                    var srcR = (MdToPdf.Core.Mermaid.Routing.Rect)sourceNodeBounds.Value;
                    var tgtR = (MdToPdf.Core.Mermaid.Routing.Rect)targetNodeBounds.Value;
                    var obsR = obstacleNodeBounds?.Select(r => (MdToPdf.Core.Mermaid.Routing.Rect)r) ?? Array.Empty<MdToPdf.Core.Mermaid.Routing.Rect>();
                    var srcP = new MdToPdf.Core.Mermaid.Routing.Point(SourceX, SourceY);
                    var tgtP = new MdToPdf.Core.Mermaid.Routing.Point(TargetX, TargetY);

                    var routePoints = MdToPdf.Core.Mermaid.Routing.OrthogonalRouter.Route(srcR, tgtR, obsR, srcP, tgtP);

                    if (routePoints.Count >= 2)
                    {
                        PathData = MdToPdf.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(routePoints, 8.0);

                        int midIndex = routePoints.Count / 2;
                        MidpointX = (routePoints[midIndex - 1].X + routePoints[midIndex].X) / 2;
                        MidpointY = (routePoints[midIndex - 1].Y + routePoints[midIndex].Y) / 2;
                        break;
                    }
                }

                if (Math.Abs(SourceX - TargetX) < 5 || Math.Abs(SourceY - TargetY) < 5)
                {
                    PathData = System.FormattableString.Invariant($"M {SourceX:F1},{SourceY:F1} L {TargetX:F1},{TargetY:F1}");
                }
                else if (SourceAnchor is "Left" or "Right" && TargetAnchor is "Left" or "Right")
                {
                    double midX = (SourceX + TargetX) / 2;
                    var pts = new List<MdToPdf.Core.Mermaid.Routing.Point>
                    {
                        new(SourceX, SourceY),
                        new(midX, SourceY),
                        new(midX, TargetY),
                        new(TargetX, TargetY)
                    };
                    PathData = MdToPdf.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(pts, 8.0);
                }
                else
                {
                    double midY = (SourceY + TargetY) / 2;
                    var pts = new List<MdToPdf.Core.Mermaid.Routing.Point>
                    {
                        new(SourceX, SourceY),
                        new(SourceX, midY),
                        new(TargetX, midY),
                        new(TargetX, TargetY)
                    };
                    PathData = MdToPdf.Core.Mermaid.Routing.OrthogonalRouter.GenerateRoundedPathData(pts, 8.0);
                }
                break;
        }
    }

    public void TranslateGeometry(double deltaX, double deltaY)
    {
        SourceX += deltaX;
        SourceY += deltaY;
        TargetX += deltaX;
        TargetY += deltaY;
        MidpointX += deltaX;
        MidpointY += deltaY;

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
