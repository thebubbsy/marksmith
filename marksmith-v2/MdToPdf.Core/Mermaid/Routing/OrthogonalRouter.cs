using System;
using System.Collections.Generic;
using System.Linq;

namespace MdToPdf.Core.Mermaid.Routing;

public record struct Point(double X, double Y);

public record struct Rect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(Point p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool IntersectsWith(Rect other) =>
        Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

    public Rect Inflate(double padding) =>
        new Rect(X - padding, Y - padding, Width + 2 * padding, Height + 2 * padding);
}

public static class OrthogonalRouter
{
    /// <summary>
    /// Computes a 90-degree orthogonal line path (sequence of Points) connecting sourceAnchor to targetAnchor,
    /// avoiding passing through intermediate obstacle node bounding boxes.
    /// </summary>
    public static List<Point> Route(
        Rect sourceBounds,
        Rect targetBounds,
        IEnumerable<Rect> obstacleNodes,
        Point sourceAnchor,
        Point targetAnchor,
        double padding = 10.0)
    {
        // 1. Filter out source and target nodes from obstacles
        var intermediateObstacles = obstacleNodes
            .Where(o => !IsSameRect(o, sourceBounds) && !IsSameRect(o, targetBounds))
            .Select(o => o.Inflate(padding))
            .ToList();

        // 2. Enforce outward anchor stub vectors (stubLength = 20.0) matching anchor orientation
        Point sourceStub = GetStubPoint(sourceAnchor, sourceBounds, 20.0);
        Point targetStub = GetStubPoint(targetAnchor, targetBounds, 20.0);

        var rawCandidatePaths = GenerateCandidatePaths(sourceStub, targetStub, intermediateObstacles);

        var candidatePaths = new List<List<Point>>();
        foreach (var rawPath in rawCandidatePaths)
        {
            var fullPath = new List<Point>();
            if (Math.Abs(sourceAnchor.X - sourceStub.X) > 0.1 || Math.Abs(sourceAnchor.Y - sourceStub.Y) > 0.1)
            {
                fullPath.Add(sourceAnchor);
            }
            fullPath.AddRange(rawPath);
            if (Math.Abs(targetAnchor.X - targetStub.X) > 0.1 || Math.Abs(targetAnchor.Y - targetStub.Y) > 0.1)
            {
                fullPath.Add(targetAnchor);
            }
            candidatePaths.Add(fullPath);
        }

        var validPath = candidatePaths
            .Where(path => IsPathValid(path, intermediateObstacles))
            .OrderBy(GetPathCost)
            .FirstOrDefault();

        if (validPath != null && validPath.Count >= 2)
        {
            return SimplifyPath(validPath);
        }

        // Grid-based A* search fallback if simple candidate paths hit obstacles
        var rawGridPath = GridSearch(sourceStub, targetStub, intermediateObstacles);
        var fullGridPath = new List<Point>();
        if (Math.Abs(sourceAnchor.X - sourceStub.X) > 0.1 || Math.Abs(sourceAnchor.Y - sourceStub.Y) > 0.1)
        {
            fullGridPath.Add(sourceAnchor);
        }
        fullGridPath.AddRange(rawGridPath);
        if (Math.Abs(targetAnchor.X - targetStub.X) > 0.1 || Math.Abs(targetAnchor.Y - targetStub.Y) > 0.1)
        {
            fullGridPath.Add(targetAnchor);
        }

        return SimplifyPath(fullGridPath);
    }

    public static Point GetStubPoint(Point anchor, Rect bounds, double stubLength = 20.0)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return anchor;

        double distTop = Math.Abs(anchor.Y - bounds.Top);
        double distBottom = Math.Abs(anchor.Y - bounds.Bottom);
        double distLeft = Math.Abs(anchor.X - bounds.Left);
        double distRight = Math.Abs(anchor.X - bounds.Right);

        double minDist = Math.Min(Math.Min(distTop, distBottom), Math.Min(distLeft, distRight));

        if (Math.Abs(minDist - distTop) < 2.0)
            return new Point(anchor.X, anchor.Y - stubLength);
        if (Math.Abs(minDist - distBottom) < 2.0)
            return new Point(anchor.X, anchor.Y + stubLength);
        if (Math.Abs(minDist - distLeft) < 2.0)
            return new Point(anchor.X - stubLength, anchor.Y);
        if (Math.Abs(minDist - distRight) < 2.0)
            return new Point(anchor.X + stubLength, anchor.Y);

        return anchor;
    }

    /// <summary>
    /// Generates SVG path data with quadratic Bezier (Q) curves filleting 90-degree polyline corners.
    /// </summary>
    public static string GenerateRoundedPathData(IReadOnlyList<Point> points, double cornerRadius = 8.0)
    {
        if (points == null || points.Count == 0) return string.Empty;
        if (points.Count == 1) return System.FormattableString.Invariant($"M {points[0].X:F1},{points[0].Y:F1}");
        if (points.Count == 2) return System.FormattableString.Invariant($"M {points[0].X:F1},{points[0].Y:F1} L {points[1].X:F1},{points[1].Y:F1}");

        var sb = new System.Text.StringBuilder();
        sb.Append(System.FormattableString.Invariant($"M {points[0].X:F1},{points[0].Y:F1}"));

        for (int i = 1; i < points.Count - 1; i++)
        {
            var prev = points[i - 1];
            var curr = points[i];
            var next = points[i + 1];

            double dx1 = curr.X - prev.X;
            double dy1 = curr.Y - prev.Y;
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);

            double dx2 = next.X - curr.X;
            double dy2 = next.Y - curr.Y;
            double len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);

            if (len1 < 0.1 || len2 < 0.1)
            {
                sb.Append($" L {curr.X:F1},{curr.Y:F1}");
                continue;
            }

            double r = Math.Min(cornerRadius, Math.Min(len1 / 2.0, len2 / 2.0));

            double startX = curr.X - (dx1 / len1) * r;
            double startY = curr.Y - (dy1 / len1) * r;

            double endX = curr.X + (dx2 / len2) * r;
            double endY = curr.Y + (dy2 / len2) * r;

            sb.Append(System.FormattableString.Invariant($" L {startX:F1},{startY:F1}"));
            sb.Append(System.FormattableString.Invariant($" Q {curr.X:F1},{curr.Y:F1} {endX:F1},{endY:F1}"));
        }

        sb.Append(System.FormattableString.Invariant($" L {points[^1].X:F1},{points[^1].Y:F1}"));
        return sb.ToString();
    }

    private static bool IsSameRect(Rect r1, Rect r2)
    {
        return Math.Abs(r1.X - r2.X) < 0.1 &&
               Math.Abs(r1.Y - r2.Y) < 0.1 &&
               Math.Abs(r1.Width - r2.Width) < 0.1 &&
               Math.Abs(r1.Height - r2.Height) < 0.1;
    }

    private static List<List<Point>> GenerateCandidatePaths(Point src, Point tgt, List<Rect> obstacles)
    {
        var paths = new List<List<Point>>();

        // Direct straight line (0 bends)
        if (Math.Abs(src.X - tgt.X) < 0.1 || Math.Abs(src.Y - tgt.Y) < 0.1)
        {
            paths.Add(new List<Point> { src, tgt });
        }

        // L-shape 1: (src.X, src.Y) -> (tgt.X, src.Y) -> (tgt.X, tgt.Y)
        paths.Add(new List<Point> { src, new Point(tgt.X, src.Y), tgt });

        // L-shape 2: (src.X, src.Y) -> (src.X, tgt.Y) -> (tgt.X, tgt.Y)
        paths.Add(new List<Point> { src, new Point(src.X, tgt.Y), tgt });

        // Z/U-shape Y midpoints
        var midYs = new List<double> { (src.Y + tgt.Y) / 2 };
        foreach (var obs in obstacles)
        {
            midYs.Add(obs.Top - 15);
            midYs.Add(obs.Bottom + 15);
        }
        foreach (var midY in midYs.Distinct())
        {
            paths.Add(new List<Point>
            {
                src,
                new Point(src.X, midY),
                new Point(tgt.X, midY),
                tgt
            });
        }

        // Z/U-shape X midpoints
        var midXs = new List<double> { (src.X + tgt.X) / 2 };
        foreach (var obs in obstacles)
        {
            midXs.Add(obs.Left - 15);
            midXs.Add(obs.Right + 15);
        }
        foreach (var midX in midXs.Distinct())
        {
            paths.Add(new List<Point>
            {
                src,
                new Point(midX, src.Y),
                new Point(midX, tgt.Y),
                tgt
            });
        }

        return paths;
    }

    private static bool IsPathValid(List<Point> path, List<Rect> obstacles)
    {
        for (int i = 0; i < path.Count - 1; i++)
        {
            var p1 = path[i];
            var p2 = path[i + 1];

            foreach (var obs in obstacles)
            {
                if (SegmentIntersectsRect(p1, p2, obs))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static bool SegmentIntersectsRect(Point p1, Point p2, Rect r)
    {
        double minX = Math.Min(p1.X, p2.X);
        double maxX = Math.Max(p1.X, p2.X);
        double minY = Math.Min(p1.Y, p2.Y);
        double maxY = Math.Max(p1.Y, p2.Y);

        if (Math.Abs(p1.Y - p2.Y) < 0.1) // Horizontal segment
        {
            double y = p1.Y;
            if (y > r.Top && y < r.Bottom && maxX > r.Left && minX < r.Right)
            {
                return true;
            }
        }
        else if (Math.Abs(p1.X - p2.X) < 0.1) // Vertical segment
        {
            double x = p1.X;
            if (x > r.Left && x < r.Right && maxY > r.Top && minY < r.Bottom)
            {
                return true;
            }
        }
        else // Diagonal (should not happen in 90-deg routing, but safety check)
        {
            if (maxX > r.Left && minX < r.Right && maxY > r.Top && minY < r.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    private static double GetPathCost(List<Point> path)
    {
        double length = 0;
        int bends = 0;

        for (int i = 0; i < path.Count - 1; i++)
        {
            var p1 = path[i];
            var p2 = path[i + 1];
            length += Math.Abs(p2.X - p1.X) + Math.Abs(p2.Y - p1.Y);

            if (i > 0)
            {
                var p0 = path[i - 1];
                bool isPrevHoriz = Math.Abs(p1.Y - p0.Y) < 0.1;
                bool isCurrHoriz = Math.Abs(p2.Y - p1.Y) < 0.1;
                if (isPrevHoriz != isCurrHoriz)
                {
                    bends++;
                }
            }
        }

        return length + (bends * 50); // 50px penalty per 90-deg turn
    }

    private static List<Point> GridSearch(Point src, Point tgt, List<Rect> obstacles)
    {
        var xCoords = new List<double> { src.X, tgt.X, (src.X + tgt.X) / 2 };
        var yCoords = new List<double> { src.Y, tgt.Y, (src.Y + tgt.Y) / 2 };

        foreach (var obs in obstacles)
        {
            xCoords.Add(obs.Left - 10);
            xCoords.Add(obs.Right + 10);
            yCoords.Add(obs.Top - 10);
            yCoords.Add(obs.Bottom + 10);
        }

        xCoords = xCoords.Distinct().OrderBy(x => x).ToList();
        yCoords = yCoords.Distinct().OrderBy(y => y).ToList();

        // Standard Dijkstra / A* over 2D grid of candidate coordinates
        var nodes = new List<Point>();
        foreach (var x in xCoords)
        {
            foreach (var y in yCoords)
            {
                var pt = new Point(x, y);
                if (!obstacles.Any(o => o.Contains(pt)))
                {
                    nodes.Add(pt);
                }
            }
        }

        if (!nodes.Any(p => Math.Abs(p.X - src.X) < 0.1 && Math.Abs(p.Y - src.Y) < 0.1)) nodes.Add(src);
        if (!nodes.Any(p => Math.Abs(p.X - tgt.X) < 0.1 && Math.Abs(p.Y - tgt.Y) < 0.1)) nodes.Add(tgt);

        var startNode = nodes.First(p => Math.Abs(p.X - src.X) < 0.1 && Math.Abs(p.Y - src.Y) < 0.1);
        var targetNode = nodes.First(p => Math.Abs(p.X - tgt.X) < 0.1 && Math.Abs(p.Y - tgt.Y) < 0.1);

        var distances = new Dictionary<Point, double>();
        var previous = new Dictionary<Point, Point>();
        var unvisited = new HashSet<Point>(nodes);

        foreach (var n in nodes) distances[n] = double.MaxValue;
        distances[startNode] = 0;

        while (unvisited.Count > 0)
        {
            var current = unvisited.OrderBy(n => distances[n]).First();
            if (distances[current] == double.MaxValue) break;
            if (current == targetNode) break;

            unvisited.Remove(current);

            // Find neighbors (same X or same Y)
            var neighbors = unvisited.Where(n =>
                (Math.Abs(n.X - current.X) < 0.1 && Math.Abs(n.Y - current.Y) > 0) ||
                (Math.Abs(n.Y - current.Y) < 0.1 && Math.Abs(n.X - current.X) > 0)).ToList();

            foreach (var neighbor in neighbors)
            {
                if (IsPathValid(new List<Point> { current, neighbor }, obstacles))
                {
                    double dist = distances[current] + Math.Abs(neighbor.X - current.X) + Math.Abs(neighbor.Y - current.Y);
                    if (dist < distances[neighbor])
                    {
                        distances[neighbor] = dist;
                        previous[neighbor] = current;
                    }
                }
            }
        }

        var path = new List<Point>();
        var curr = targetNode;
        while (previous.ContainsKey(curr))
        {
            path.Add(curr);
            curr = previous[curr];
        }
        path.Add(startNode);
        path.Reverse();

        return path;
    }

    public static List<Point> SimplifyPath(List<Point> path)
    {
        if (path == null || path.Count <= 2) return path ?? new List<Point>();

        var result = new List<Point> { path[0] };
        for (int i = 1; i < path.Count - 1; i++)
        {
            var prev = result.Last();
            var curr = path[i];
            var next = path[i + 1];

            // Collinear check (horizontal or vertical line)
            bool isHorizCollinear = Math.Abs(prev.Y - curr.Y) < 0.1 && Math.Abs(curr.Y - next.Y) < 0.1;
            bool isVertCollinear = Math.Abs(prev.X - curr.X) < 0.1 && Math.Abs(curr.X - next.X) < 0.1;

            if (!isHorizCollinear && !isVertCollinear)
            {
                result.Add(curr);
            }
        }
        result.Add(path.Last());
        return result;
    }
}
