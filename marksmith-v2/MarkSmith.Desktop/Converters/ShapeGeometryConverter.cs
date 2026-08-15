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

                default:
                    return new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 100, 100) };
            }
        }
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
