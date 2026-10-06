using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters
{
    /// <summary>Shared 100×100 geometry builder for DrawingML preset names. Geometries are
    /// built once per preset via XamlReader and then CACHED — the lines list converts one
    /// geometry per row and the canvas one per shape, so the same 15 presets were being
    /// re-parsed from XAML on every single conversion.</summary>
    public static class ShapeGeometries
    {
        public static Geometry For(string prst)
        {
            string key = (prst ?? "rect").ToLowerInvariant();
            return BuildGeometry(key);
        }

        private static Geometry BuildGeometry(string key)
        {
            var geo = new PathGeometry();
            var fig = new PathFigure { IsClosed = true };

            switch (key)
            {
                case "ellipse" or "circle":
                    return new EllipseGeometry { Center = new Windows.Foundation.Point(50, 50), RadiusX = 50, RadiusY = 50 };

                case "roundrect":
                    fig.StartPoint = new Windows.Foundation.Point(20, 0);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(80, 0) });
                    fig.Segments.Add(new ArcSegment { Point = new Windows.Foundation.Point(100, 20), Size = new Windows.Foundation.Size(20, 20), SweepDirection = SweepDirection.Clockwise });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 80) });
                    fig.Segments.Add(new ArcSegment { Point = new Windows.Foundation.Point(80, 100), Size = new Windows.Foundation.Size(20, 20), SweepDirection = SweepDirection.Clockwise });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(20, 100) });
                    fig.Segments.Add(new ArcSegment { Point = new Windows.Foundation.Point(0, 80), Size = new Windows.Foundation.Size(20, 20), SweepDirection = SweepDirection.Clockwise });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 20) });
                    fig.Segments.Add(new ArcSegment { Point = new Windows.Foundation.Point(20, 0), Size = new Windows.Foundation.Size(20, 20), SweepDirection = SweepDirection.Clockwise });
                    geo.Figures.Add(fig);
                    return geo;

                case "rect":
                    return new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 100, 100) };

                case "triangle":
                    fig.StartPoint = new Windows.Foundation.Point(50, 0);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 100) });
                    geo.Figures.Add(fig);
                    return geo;

                case "trapezoid":
                    fig.StartPoint = new Windows.Foundation.Point(20, 0);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(80, 0) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 100) });
                    geo.Figures.Add(fig);
                    return geo;

                case "chevron":
                    fig.StartPoint = new Windows.Foundation.Point(0, 0);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(65, 0) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 50) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(65, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(35, 50) });
                    geo.Figures.Add(fig);
                    return geo;

                case "diamond":
                    fig.StartPoint = new Windows.Foundation.Point(50, 0);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 50) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(50, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 50) });
                    geo.Figures.Add(fig);
                    return geo;

                case "hexagon":
                    fig.StartPoint = new Windows.Foundation.Point(25, 0);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(75, 0) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 50) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(75, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(25, 100) });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 50) });
                    geo.Figures.Add(fig);
                    return geo;

                case "cylinder" or "can":
                    fig.StartPoint = new Windows.Foundation.Point(0, 15);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 85) });
                    fig.Segments.Add(new ArcSegment { Point = new Windows.Foundation.Point(100, 85), Size = new Windows.Foundation.Size(50, 15), SweepDirection = SweepDirection.Clockwise });
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 15) });
                    fig.Segments.Add(new ArcSegment { Point = new Windows.Foundation.Point(0, 15), Size = new Windows.Foundation.Size(50, 15), SweepDirection = SweepDirection.Counterclockwise });
                    geo.Figures.Add(fig);
                    return geo;

                case "line":
                    fig.IsClosed = false;
                    fig.StartPoint = new Windows.Foundation.Point(0, 50);
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(100, 50) });
                    geo.Figures.Add(fig);
                    return geo;

                case "heart":
                    fig.StartPoint = new Windows.Foundation.Point(50, 88);
                    fig.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(20, 60), Point2 = new Windows.Foundation.Point(0, 42), Point3 = new Windows.Foundation.Point(0, 25) });
                    fig.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(0, 8), Point2 = new Windows.Foundation.Point(14, 0), Point3 = new Windows.Foundation.Point(25, 8) });
                    fig.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(35, 15), Point2 = new Windows.Foundation.Point(45, 25), Point3 = new Windows.Foundation.Point(50, 38) });
                    fig.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(55, 25), Point2 = new Windows.Foundation.Point(65, 15), Point3 = new Windows.Foundation.Point(75, 8) });
                    fig.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(86, 0), Point2 = new Windows.Foundation.Point(100, 8), Point3 = new Windows.Foundation.Point(100, 25) });
                    fig.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(100, 42), Point2 = new Windows.Foundation.Point(80, 60), Point3 = new Windows.Foundation.Point(50, 88) });
                    geo.Figures.Add(fig);
                    return geo;

                // The rest of the primitives palette used to fall through to a plain square — the
                // Cycle preset's four circular arrows drew as four boxes, and picking "Cloud" or
                // "Moon" in the inspector visibly did nothing.
                case "parallelogram":
                    fig.StartPoint = P(25, 0);
                    fig.Segments.Add(new LineSegment { Point = P(100, 0) });
                    fig.Segments.Add(new LineSegment { Point = P(75, 100) });
                    fig.Segments.Add(new LineSegment { Point = P(0, 100) });
                    geo.Figures.Add(fig);
                    return geo;

                case "arc":
                    // A quarter-thick band along the top-right quadrant, like Word's arc preset.
                    fig.StartPoint = P(50, 0);
                    fig.Segments.Add(new ArcSegment { Point = P(100, 50), Size = new Windows.Foundation.Size(50, 50), SweepDirection = SweepDirection.Clockwise });
                    fig.Segments.Add(new LineSegment { Point = P(88, 50) });
                    fig.Segments.Add(new ArcSegment { Point = P(50, 12), Size = new Windows.Foundation.Size(38, 38), SweepDirection = SweepDirection.Counterclockwise });
                    geo.Figures.Add(fig);
                    return geo;

                case "moon":
                    fig.StartPoint = P(75, 0);
                    fig.Segments.Add(new ArcSegment { Point = P(75, 100), Size = new Windows.Foundation.Size(50, 50), IsLargeArc = false, SweepDirection = SweepDirection.Counterclockwise });
                    fig.Segments.Add(new ArcSegment { Point = P(75, 0), Size = new Windows.Foundation.Size(36, 50), SweepDirection = SweepDirection.Clockwise });
                    geo.Figures.Add(fig);
                    return geo;

                case "cloud":
                {
                    var cloud = new GeometryGroup { FillRule = FillRule.Nonzero };
                    cloud.Children.Add(Circle(28, 58, 22, 20));
                    cloud.Children.Add(Circle(48, 38, 26, 26));
                    cloud.Children.Add(Circle(72, 50, 24, 22));
                    cloud.Children.Add(Circle(52, 70, 30, 18));
                    return cloud;
                }

                case "smileyface":
                {
                    var face = new GeometryGroup { FillRule = FillRule.EvenOdd };
                    face.Children.Add(Circle(50, 50, 50, 50));
                    face.Children.Add(Circle(34, 36, 7, 8));
                    face.Children.Add(Circle(66, 36, 7, 8));
                    var mouth = new PathGeometry();
                    var m = new PathFigure { StartPoint = P(28, 62), IsClosed = true };
                    m.Segments.Add(new ArcSegment { Point = P(72, 62), Size = new Windows.Foundation.Size(24, 20), SweepDirection = SweepDirection.Counterclockwise });
                    m.Segments.Add(new ArcSegment { Point = P(28, 62), Size = new Windows.Foundation.Size(30, 14), SweepDirection = SweepDirection.Clockwise });
                    mouth.Figures.Add(m);
                    face.Children.Add(mouth);
                    return face;
                }

                case "circulararrow":
                    // A 270° ring (outer r 44, inner r 26) ending in an arrowhead at the bottom,
                    // pointing in the clockwise direction of travel.
                    fig.StartPoint = P(6, 50);
                    fig.Segments.Add(new ArcSegment { Point = P(50, 94), Size = new Windows.Foundation.Size(44, 44), IsLargeArc = true, SweepDirection = SweepDirection.Clockwise });
                    fig.Segments.Add(new LineSegment { Point = P(50, 100) });
                    fig.Segments.Add(new LineSegment { Point = P(28, 85) });
                    fig.Segments.Add(new LineSegment { Point = P(50, 68) });
                    fig.Segments.Add(new LineSegment { Point = P(50, 76) });
                    fig.Segments.Add(new ArcSegment { Point = P(24, 50), Size = new Windows.Foundation.Size(26, 26), IsLargeArc = true, SweepDirection = SweepDirection.Counterclockwise });
                    geo.Figures.Add(fig);
                    return geo;

                default:
                    return new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 100, 100) };
            }
        }

        private static Windows.Foundation.Point P(double x, double y) => new(x, y);

        private static EllipseGeometry Circle(double cx, double cy, double rx, double ry) =>
            new() { Center = P(cx, cy), RadiusX = rx, RadiusY = ry };
    }

    /// <summary>Maps a DrawingML preset name to a 100×100 geometry for canvas preview.</summary>
    public class ShapeGeometryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => ShapeGeometries.For(value as string ?? "rect");

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
