using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

// The shared contract between Mermaid renderers (pure text -> geometry, NO OOXML) and the
// DocxShapeEmitter (geometry -> native Word DrawingML shapes). Renderers lay out a diagram in
// POINTS (pt) with the origin at the top-left; the emitter converts to EMUs and produces a single
// grouped drawing whose child shapes are real, draggable, editable Word shapes.

public enum ShapeKind
{
    Rect,           // process box
    RoundRect,      // rounded node / participant
    Diamond,        // decision
    Ellipse,        // start/end, ER entity attr
    Circle,         // small connector dot (W == H)
    Parallelogram,  // input/output
    Trapezoid,      // manual operation
    Cylinder,       // database ("can")
    Hexagon,        // preparation
    Pie,            // pie wedge — uses AdjStartDeg/AdjEndDeg (degrees, 0 = 3 o'clock, clockwise)
    Frame,          // border-only rectangle (alt/loop frames, quadrant boxes) — no fill
    Text,           // borderless, fill-less text label
    Subgraph,       // nested container with subtle background
}

public enum ArrowHead { None, Triangle, Open, Diamond, Oval, Stealth }

public sealed class MShape
{
    public uint Id { get; set; }               // unique XML ID (wps:cNvPr) assigned during emission
    public ShapeKind Kind { get; set; } = ShapeKind.Rect;
    public double X { get; set; }              // pt, top-left
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string? Text { get; set; }           // plain text; '\n' allowed for line breaks
    public string? Fill { get; set; }           // "#RRGGBB" or null (theme default / none for Frame|Text)
    public string? Stroke { get; set; }         // "#RRGGBB" or null (theme default)
    public string? TextColor { get; set; }      // "#RRGGBB" or null (theme default)
    public double FontSize { get; set; } = 10;  // pt
    public bool Bold { get; set; }
    public bool Dashed { get; set; }
    public double StrokeWidth { get; set; } = 1.25; // pt
    public double AdjStartDeg { get; set; }     // Pie only
    public double AdjEndDeg { get; set; }       // Pie only
}

public sealed class MConnector
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public bool Elbow { get; set; }             // false = straight, true = bent (3-segment) connector
    public ArrowHead StartHead { get; set; } = ArrowHead.None;
    public ArrowHead EndHead { get; set; } = ArrowHead.Triangle;
    public bool Dashed { get; set; }
    public string? Stroke { get; set; }
    public double StrokeWidth { get; set; } = 1.25;
    public string? Label { get; set; }          // optional; emitter draws it as a small Text shape
    public double LabelX { get; set; }          // top-left of the label box (pt); used when Label != null
    public double LabelY { get; set; }
    public double LabelW { get; set; } = 80;
    public double LabelH { get; set; } = 14;

    // Smart Connectors (anchored edges)
    public uint? FromShapeId { get; set; }
    public int FromConnectionSite { get; set; }
    public uint? ToShapeId { get; set; }
    public int ToConnectionSite { get; set; }

    // When set (>= 2 points, absolute pt), the connector is drawn as a freeform curve following
    // exactly these points instead of a straight/bent line — used by the exact-layout path to
    // reproduce mermaid's own curved edge routing. The arrowhead lands on the final point.
    public IReadOnlyList<(double X, double Y)>? Points { get; set; }
}

public sealed class MDiagram
{
    public double Width { get; set; }           // canvas size in pt — must enclose all geometry
    public double Height { get; set; }
    public List<MShape> Shapes { get; } = new();
    public List<MConnector> Connectors { get; } = new();
}

// Thrown by renderers when the source can't be understood; the exporter falls back to a code block.
public sealed class MermaidParseException : Exception
{
    public MermaidParseException(string message) : base(message) { }
}

// One renderer per diagram family. `diagramType` is the lowercased first word of the mermaid source
// (e.g. "flowchart", "graph", "sequencediagram", "pie", "gantt", "mindmap", "classdiagram", ...).
public interface IMermaidRenderer
{
    bool CanRender(string diagramType);
    MDiagram Render(string source, ThemeDefinition theme);
}
