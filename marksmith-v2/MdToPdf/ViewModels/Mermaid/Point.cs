namespace MdToPdf.ViewModels.Mermaid;

public readonly struct Point
{
    public double X { get; }
    public double Y { get; }

    public Point(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static implicit operator MdToPdf.Core.Mermaid.Routing.Point(Point p) =>
        new MdToPdf.Core.Mermaid.Routing.Point(p.X, p.Y);

    public static implicit operator Point(MdToPdf.Core.Mermaid.Routing.Point p) =>
        new Point(p.X, p.Y);
}
