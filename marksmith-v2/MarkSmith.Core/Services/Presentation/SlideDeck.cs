using System.Collections.Generic;

namespace MarkSmith.Services.Presentation;

// The deck PptxExportService draws: slides made of measured blocks, already paginated, so the
// writer only has to place shapes. Built by SlideDeckBuilder from the Markdown AST.

public enum SlideKind { Title, Section, Content }

public sealed class PptxDeck
{
    public string Title { get; set; } = "";
    public List<DeckSlide> Slides { get; } = new();
}

public sealed class DeckSlide
{
    public SlideKind Kind { get; init; }
    public string Title { get; set; } = "";
    /// <summary>Title slide: the line under the title. Section slide: unused.</summary>
    public string? Subtitle { get; set; }
    /// <summary>Title slide: the author line.</summary>
    public string? Byline { get; set; }
    public List<SlideBlock> Blocks { get; } = new();
    /// <summary>True for the second and later slides a long section was split across.</summary>
    public bool IsContinuation { get; init; }
}

/// <summary>A styled piece of inline text. <see cref="Url"/> is an external link.</summary>
public sealed record TextRun(string Text)
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Strike { get; init; }
    public bool Underline { get; init; }
    public bool Code { get; init; }
    public bool Math { get; init; }
    /// <summary>Positive = superscript, negative = subscript (percent, as DrawingML wants).</summary>
    public int Baseline { get; init; }
    public string? Url { get; init; }
    /// <summary>Hex colour override (no '#'), e.g. a syntax token or an alert label.</summary>
    public string? Color { get; init; }
    public bool LineBreak { get; init; }
}

public enum ParaStyle { Body, Bullet, Numbered, Task, Continuation, Subheading, Caption, Math }

public sealed class SlideParagraph
{
    public ParaStyle Style { get; init; }
    public int Level { get; init; }
    public int Number { get; init; }        // Numbered: this item's number
    public bool Checked { get; init; }      // Task
    public int HeadingLevel { get; init; }  // Subheading: 3..6
    public List<TextRun> Runs { get; } = new();
    public bool KeepWithNext => Style == ParaStyle.Subheading;
    public string PlainText => string.Concat(Runs.ConvertAll(r => r.LineBreak ? "\n" : r.Text));
}

public abstract class SlideBlock
{
    /// <summary>Height on the slide in points, set by the paginator; the writer stacks blocks by it.</summary>
    public double HeightPt { get; set; }
}

public enum PanelKind { None, Quote, Alert }

/// <summary>Paragraphs of body text. A quote or an alert is the same thing drawn on a panel.</summary>
public sealed class TextBlock : SlideBlock
{
    public List<SlideParagraph> Paragraphs { get; } = new();
    public PanelKind Panel { get; init; }
    /// <summary>Alert accent colour (hex, no '#').</summary>
    public string? PanelColor { get; init; }
    /// <summary>Shrink factor applied when one paragraph alone is taller than a slide (1 = none).</summary>
    public double FontScale { get; set; } = 1;
}

public sealed class CodeSlideBlock : SlideBlock
{
    public string Language { get; init; } = "";
    public List<string> Lines { get; } = new();
    /// <summary>Shown above the code, e.g. "Flowchart 1 (diagram source)".</summary>
    public string? Caption { get; init; }
    public bool ContinuedFromPrevious { get; set; }
}

public enum CellAlign { Left, Center, Right }

public sealed class TableSlideBlock : SlideBlock
{
    public List<List<SlideParagraph>> Header { get; } = new();
    public List<List<List<SlideParagraph>>> Rows { get; } = new();
    public List<CellAlign> Align { get; } = new();
    /// <summary>Column widths as fractions of the table width.</summary>
    public List<double> Widths { get; } = new();
    public int Columns => Align.Count;
}

public sealed class PictureSlideBlock : SlideBlock
{
    public byte[] Data { get; init; } = System.Array.Empty<byte>();
    public string ContentType { get; init; } = "image/png";
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    /// <summary>Size at 100%, in points (a 2x diagram PNG reports half its pixels).</summary>
    public double NaturalWidthPt { get; init; }
    public double NaturalHeightPt { get; init; }
    /// <summary>Diagrams may grow up to 1.5x to fill a slide; photos never upscale.</summary>
    public bool IsDiagram { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Line shown under the picture (a diagram's label, an image's title).</summary>
    public string? Caption { get; init; }
}

public enum ChartKind { Bar, Line, Pie }

/// <summary>A <c>:::chart</c> block, drawn as a native PowerPoint chart (editable, with its data).</summary>
public sealed class ChartSlideBlock : SlideBlock
{
    public ChartKind Kind { get; init; }
    public List<string> Labels { get; } = new();
    public List<double> Values { get; } = new();
}

/// <summary>A <c>:::metrics</c> / <c>:::kpi</c> block: a row (or rows) of KPI cards.</summary>
public sealed class MetricsSlideBlock : SlideBlock
{
    public List<(string Value, string Label)> Items { get; } = new();
}

/// <summary>A <c>:::smartart</c>, <c>:::workflow</c> or <c>:::timeline</c> block, drawn as native
/// SmartArt from the same layout packages the Word export uses.</summary>
public sealed class SmartArtSlideBlock : SlideBlock
{
    /// <summary>The layout asked for (or suggested from the content), e.g. "process".</summary>
    public string Layout { get; init; } = "list";
    /// <summary>The bulleted body ("- step" lines; indentation is hierarchy).</summary>
    public string Body { get; init; } = "";
    /// <summary>Top-level entries, for the height estimate and the text fallback.</summary>
    public int TopLevelCount { get; init; }
    public int Depth { get; init; } = 1;
}

/// <summary>An image that couldn't be loaded: drawn as a labelled placeholder, never dropped.</summary>
public sealed class MissingPictureBlock : SlideBlock
{
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
}
