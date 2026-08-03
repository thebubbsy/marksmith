using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters
{
    /// <summary>Maps a DrawingML preset name to a 100×100 geometry for canvas preview.</summary>
    public class ShapeGeometryConverter : IValueConverter
    {
        private static Geometry Parse(string d)
        {
            // WinUI has no Geometry.Parse; round-trip through XamlReader.
            var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
                $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{d}'/>");
            return path.Data;
        }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string prst = (value as string ?? "rect").ToLowerInvariant();
            switch (prst)
            {
                case "ellipse":
                    return Parse("M50,0 A50,50 0 1,1 49.9,0 Z");
                case "roundrect":
                    return Parse("M20,0 H80 A20,20 0 0,1 100,20 V80 A20,20 0 0,1 80,100 H20 A20,20 0 0,1 0,80 V20 A20,20 0 0,1 20,0 Z");
                case "rect":
                    return Parse("M0,0 H100 V100 H0 Z");
                case "chevron":
                    return Parse("M0,0 L65,0 L100,50 L65,100 L0,100 L35,50 Z");
                case "diamond":
                    return Parse("M50,0 L100,50 L50,100 L0,50 Z");
                case "hexagon":
                    return Parse("M25,0 L75,0 L100,50 L75,100 L25,100 L0,50 Z");
                case "triangle":
                    return Parse("M50,0 L100,100 L0,100 Z");
                case "parallelogram":
                    return Parse("M25,0 L100,0 L75,100 L0,100 Z");
                case "line":
                    return Parse("M0,48 L100,48 L100,52 L0,52 Z");
                case "arc":
                    return Parse("M10,90 A40,40 0 1,1 90,90 L50,90 Z");
                case "heart":
                    return Parse("M50,88 C20,60 0,42 0,25 C0,8 14,0 25,8 C35,15 45,25 50,38 C55,25 65,15 75,8 C86,0 100,8 100,25 C100,42 80,60 50,88 Z");
                case "moon":
                    return Parse("M70,5 A45,45 0 1,0 70,95 A35,35 0 1,1 70,5 Z");
                case "cloud":
                    return Parse("M15,80 A25,25 0 0,1 35,35 A30,30 0 0,1 85,40 A20,20 0 0,1 80,80 Z");
                case "smileyface":
                    var g = new GeometryGroup();
                    g.Children.Add(Parse("M50,5 A45,45 0 1,1 49.9,5 Z"));
                    g.Children.Add(Parse("M33,38 A5,5 0 1,1 33,37.9 Z"));
                    g.Children.Add(Parse("M67,38 A5,5 0 1,1 67,37.9 Z"));
                    g.Children.Add(Parse("M25,60 Q50,85 75,60"));
                    return g;
                case "circulararrow":
                    var ga = new GeometryGroup();
                    ga.Children.Add(Parse("M50,10 A40,40 0 1,1 90,50 L90,30 L97,55 L70,52 L84,38"));
                    return ga;
                default:
                    return Parse("M0,0 H100 V100 H0 Z");
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
