using System.IO.Compression;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests;

// erDiagram cardinality must be drawn the way mermaid draws it — as crow's-foot GRAPHICS (bars for
// "one", a punched-out circle for "zero", a fanned foot for "many") — never as the literal text
// "1" / "0..1" / "0..N" that the old renderer painted and that read as a broken diagram in Word.
public class ErCardinalityMarkerTests
{
    private static ThemeDefinition Light => new ThemeCatalog().GetOrDefault("GitHub Light");

    private static MDiagram Render(string relationship) =>
        new MermaidClassErRenderer().Render($"erDiagram\n    {relationship}\n", Light);

    private static double Length(MConnector c) =>
        Math.Sqrt((c.X2 - c.X1) * (c.X2 - c.X1) + (c.Y2 - c.Y1) * (c.Y2 - c.Y1));

    private static bool IsCardinalityText(MShape s) =>
        s.Text is "1" or "0..1" or "0..N" or "1..N" or "N";

    [Fact]
    public void Er_exactly_one_renders_two_bars_not_the_text_1()
    {
        var d = Render("A ||--|| B : \"has\"");

        // The reported bug: a "1" where the double line belongs. No cardinality text may remain.
        Assert.DoesNotContain(d.Shapes, IsCardinalityText);

        // Two perpendicular bars per "||" end → four short (12pt) headless bar connectors.
        var bars = d.Connectors.Where(c => Math.Abs(Length(c) - 12) < 0.5).ToList();
        Assert.Equal(4, bars.Count);
        Assert.All(bars, b =>
        {
            Assert.Equal(ArrowHead.None, b.StartHead);
            Assert.Equal(ArrowHead.None, b.EndHead);
        });
    }

    [Fact]
    public void Er_zero_or_more_renders_circle_and_foot_not_text()
    {
        var d = Render("A ||--o{ B : \"has\"");

        Assert.DoesNotContain(d.Shapes, IsCardinalityText);

        // The "o" becomes a real circle, filled with the diagram background so it punches the line
        // out — exactly how mermaid paints its zero marker.
        var circle = Assert.Single(d.Shapes, s => s.Kind == ShapeKind.Circle);
        Assert.Equal(Light.Background, circle.Fill, ignoreCase: true);

        // Connectors: main line + two "||" bars at A + two foot toes at B = 5, all headless
        // (crow's-foot notation has no arrowheads).
        Assert.Equal(5, d.Connectors.Count);
        Assert.All(d.Connectors, c =>
        {
            Assert.Equal(ArrowHead.None, c.StartHead);
            Assert.Equal(ArrowHead.None, c.EndHead);
        });
    }

    [Fact]
    public void Er_bars_are_perpendicular_to_the_relationship_line()
    {
        var d = Render("A ||--|| B : \"has\"");

        // The relationship line is the longest connector. The layered layout stacks A above B, so it
        // is vertical here — but whatever its orientation, every cardinality bar must meet it at 90°.
        var main = d.Connectors.OrderByDescending(Length).First();
        double mx = main.X2 - main.X1, my = main.Y2 - main.Y1;

        var bars = d.Connectors.Where(c => Math.Abs(Length(c) - 12) < 0.5).ToList();
        Assert.Equal(4, bars.Count);
        Assert.All(bars, b =>
        {
            double dot = mx * (b.X2 - b.X1) + my * (b.Y2 - b.Y1);
            Assert.True(Math.Abs(dot) < 1.0, "bar must be perpendicular to the relationship line");
        });
    }

    [Fact]
    public void Er_one_or_more_renders_bar_and_foot_not_text()
    {
        var d = Render("A ||--|{ B : \"has\"");

        Assert.DoesNotContain(d.Shapes, IsCardinalityText);

        // "|{" = a bar beyond the foot; no circle for this cardinality.
        Assert.DoesNotContain(d.Shapes, s => s.Kind == ShapeKind.Circle);
        // Connectors: main + two "||" bars at A + two toes + one companion bar at B = 6.
        Assert.Equal(6, d.Connectors.Count);
    }

    [Fact]
    public void Class_diagram_role_names_stay_textual()
    {
        // classDiagram multiplicities are free-form role labels, not crow's-foot cardinality — they
        // must keep rendering as text (guard against the ER fix leaking into the class path).
        var d = new MermaidClassErRenderer().Render(
            "classDiagram\n    Animal \"1\" --> \"*\" Duck : owns\n", Light);

        Assert.Contains(d.Shapes, s => s.Text == "1");
        Assert.Contains(d.Shapes, s => s.Text == "*");
    }

    [Fact]
    public void Er_relationship_chain_flows_top_to_bottom()
    {
        // The reported diagram is a chain (MERCHANT -> TRANSACTION -> LEDGER_ENTRY); mermaid lays it
        // out top to bottom, so the geometry must stack the entities vertically in that order — not
        // spread them into an L shape with one box off to the side.
        var d = Render("MERCHANT ||--o{ TRANSACTION : \"processes\"\nTRANSACTION ||--o{ LEDGER_ENTRY : \"contains\"");

        double Top(string name) => d.Shapes.Where(s => s.Kind == ShapeKind.Rect && s.Text == name).Min(s => s.Y);
        double merchant = Top("MERCHANT"), transaction = Top("TRANSACTION"), ledger = Top("LEDGER_ENTRY");

        Assert.True(merchant < transaction, "MERCHANT must sit above TRANSACTION");
        Assert.True(transaction < ledger, "TRANSACTION must sit above LEDGER_ENTRY");
    }

    // A tinted (non-white) theme makes the attribute-row fix visible: the header keeps the theme
    // background while the rows stay mermaid's fixed white / light grey.
    private static ThemeDefinition Tinted => new ThemeCatalog().GetOrDefault("Solarized Light");

    [Fact]
    public void Er_attribute_rows_are_white_not_the_theme_background()
    {
        var d = new MermaidClassErRenderer().Render(
            "erDiagram\n    MERCHANT {\n        uuid id PK\n        string name\n        string webhook_url\n        boolean is_active\n    }\n    MERCHANT ||--o{ TRANSACTION : \"processes\"\n",
            Tinted);

        // The header keeps the tinted theme background...
        var header = d.Shapes.Single(s => s.Kind == ShapeKind.Rect && s.Text == "MERCHANT");
        Assert.Equal(Tinted.Background, header.Fill, ignoreCase: true);

        // ...but the four attribute rows alternate white / light grey (mermaid's attributeBoxOdd /
        // attributeBoxEven) — never the tinted page background that read as "lighter green/yellow".
        var rows = d.Shapes.Where(s => s.Kind == ShapeKind.Rect && s.Text is null &&
            s.Fill is "#ffffff" or "#f2f2f2").ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("#ffffff", rows[0].Fill);
        Assert.Equal("#f2f2f2", rows[1].Fill);
        Assert.Equal("#ffffff", rows[2].Fill);
        Assert.Equal("#f2f2f2", rows[3].Fill);
    }

    // ---- end-to-end: the exact reported diagram exports as graphics, not "1" / "0..N" text -----

    private const string ReportedDiagram = @"erDiagram
    MERCHANT ||--o{ TRANSACTION : ""processes""
    TRANSACTION ||--o{ LEDGER_ENTRY : ""contains""
    MERCHANT {
        uuid id PK
        string name
        string webhook_url
        boolean is_active
    }
    TRANSACTION {
        uuid id PK
        string idempotency_key UK
        int amount
        string currency
        string status
        timestamp created_at
    }
    LEDGER_ENTRY {
        uuid id PK
        uuid transaction_id FK
        string previous_state
        string new_state
        timestamp recorded_at
    }";

    private static string ExportDocumentXml(string md)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-erd-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            return reader.ReadToEnd();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Reported_erDiagram_exports_cardinality_as_shapes_not_text()
    {
        var xml = ExportDocumentXml("```mermaid\n" + ReportedDiagram + "\n```");

        // The relationship labels survive...
        Assert.Contains("processes", xml);
        Assert.Contains("contains", xml);

        // ...but the old textual cardinality ("1" / "0..N") is gone — the distinctive multi-char
        // codes must not appear as runs, and the zero markers exist as real ellipse shapes.
        Assert.DoesNotContain("0..N", xml);
        Assert.DoesNotContain("0..1", xml);
        Assert.DoesNotContain("1..N", xml);
        Assert.Contains("prst=\"ellipse\"", xml);          // the punched-out "o" circles
        Assert.Contains("straightConnector1", xml);         // the bars / foot toes / relationship lines
    }

    [Fact]
    public void Diagram_text_uses_mermaids_trebuchet_font()
    {
        // Mermaid renders every diagram in its default font stack ("trebuchet ms", ...). Word would
        // otherwise fall back to the body font (Calibri), so the exported runs must pin Trebuchet MS.
        var xml = ExportDocumentXml("```mermaid\n" + ReportedDiagram + "\n```");

        Assert.Contains("w:rFonts", xml);
        Assert.Contains("Trebuchet MS", xml);
    }
}
