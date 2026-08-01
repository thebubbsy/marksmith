namespace MdToPdf.ViewModels.Mermaid;

public readonly struct Rect
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public Rect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public bool Contains(Point point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public bool IntersectsWith(Rect rect) =>
        !(rect.Left > Right || rect.Right < Left || rect.Top > Bottom || rect.Bottom < Top);

    public static implicit operator MdToPdf.Core.Mermaid.Routing.Rect(Rect r) =>
        new MdToPdf.Core.Mermaid.Routing.Rect(r.X, r.Y, r.Width, r.Height);

    public static implicit operator Rect(MdToPdf.Core.Mermaid.Routing.Rect r) =>
        new Rect(r.X, r.Y, r.Width, r.Height);
}
