using System.IO;
using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// The headline guarantee: a canonical Markdown document exported to DOCX and re-imported must come
// back BYTE-FOR-BYTE identical. This is a REAL reverse converter (ReverseImportService) — no copy of
// the source is embedded in the DOCX. The test dumps both sides so any divergence is inspectable.
public class DocxRoundTripTests
{
    private const string SampleSource = """
        # Quarterly Review — Sample Document

        This is a **sample** so you can try MarkSmith without hunting for a Markdown file. Restyle it on the right, then hit **Generate PDF** below.

        > [!TIP]
        > Everything here survives export: the table, the math, and the diagrams.

        ---

        ## Data Tables

        | Region | Revenue | Change |
        | --- | --- | --- |
        | APAC | $4.2M | +12% |
        | EU | $3.1M | +5% |
        | US | $5.5M | +9% |

        ---

        ## Math

        Reserves follow $R=\sum_{i=1}^np_i\cdotL_i$ — and in Word export this becomes a real, editable equation, not a picture.

        Block equations work too:

        $$
        \begin{bmatrix}1 & 2 & 3 \\ 4 & 5 & 6 \\ 7 & 8 & 9\end{bmatrix}
        $$

        ---

        ## Formatting & Code

        *Italic*, **Bold**, ***Bold Italic***, ~~Strikethrough~~, ==Highlight==, and `Inline code`. Subscript: H~2~O | Superscript: X^2^

        ```python
        def hello_world():
            print("Syntax highlighting works!")
        ```

        ---

        ## Task Lists

        - [x] Completed task
        - [ ] Incomplete task

        1. First ordered
        2. Second ordered

        - Bullet one
        - Bullet two

        A [link to tables](#data-tables) and some ***final*** text.
        """;

    // The reverse importer emits the CANONICAL text form: LF line endings and a single trailing
    // newline. The raw literal above inherits CRLF from the Windows source file and has no trailing
    // newline, so normalize it once here. This is the documented round-trip contract — equivalent
    // line-ending spellings converge to LF — and it makes the byte-for-byte assertion well-defined.
    private static readonly string Sample = SampleSource.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    [Fact]
    public async Task Sample_round_trips_byte_for_byte()
    {
        var outDir = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test_outputs"));
        Directory.CreateDirectory(outDir);
        var docxPath = Path.Combine(outDir, "rt_roundtrip.docx");

        await new DocxExportService().ExportAsync(Sample, docxPath, new AppSettings());
        var reimported = new ReverseImportService().ImportFromDocx(docxPath);

        File.WriteAllText(Path.Combine(outDir, "rt_original.md"), Sample);
        File.WriteAllText(Path.Combine(outDir, "rt_reimported.md"), reimported);

        Assert.Equal(Sample, reimported);
    }

    // A Mermaid flowchart exported as native Word shapes must come back as Mermaid source — the
    // DOCX -> MD leg rebuilds a FlowchartDiagramAst from the shape identity tags and regenerates
    // canonical text. The input here IS the generator's canonical form, so the recovered fence must
    // match it byte-for-byte (the documented idempotent-from-canonical contract).
    [Fact]
    public async Task Mermaid_flowchart_round_trips()
    {
        var ast = new FlowchartDiagramAst { Direction = FlowDirection.TD };
        ast.Nodes["A"] = new FlowNode { Id = "A", Text = "Start Process", Shape = FlowNodeShape.Rectangle };
        ast.Nodes["B"] = new FlowNode { Id = "B", Text = "Decision Point", Shape = FlowNodeShape.RoundedRectangle };
        ast.Nodes["C"] = new FlowNode { Id = "C", Text = "Circle Node", Shape = FlowNodeShape.Circle };
        ast.Nodes["D"] = new FlowNode { Id = "D", Text = "Hex Node", Shape = FlowNodeShape.Hexagon };
        ast.Nodes["E"] = new FlowNode { Id = "E", Text = "Para Node", Shape = FlowNodeShape.Parallelogram };
        ast.Nodes["F"] = new FlowNode { Id = "F", Text = "DB Node", Shape = FlowNodeShape.CylindricalDatabase };
        ast.Edges.Add(new FlowEdge { FromId = "A", ToId = "B" });
        ast.Edges.Add(new FlowEdge { FromId = "B", ToId = "C", LineStyle = FlowLineStyle.Dashed });
        ast.Edges.Add(new FlowEdge { FromId = "C", ToId = "D", LineStyle = FlowLineStyle.Thick });
        ast.Edges.Add(new FlowEdge { FromId = "D", ToId = "E", StartHead = FlowArrowHead.Normal, EndHead = FlowArrowHead.Normal });
        ast.Edges.Add(new FlowEdge { FromId = "E", ToId = "F", Label = "Yes" });

        var canonical = MermaidCodeGenerator.Generate(ast).Replace("\r\n", "\n"); // canonical = LF (importer's form)
        var md = "# Diagram Round Trip\n\n```mermaid\n" + canonical + "\n```\n\nAfter the diagram.\n";

        var outDir = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test_outputs"));
        Directory.CreateDirectory(outDir);
        var docxPath = Path.Combine(outDir, "rt_mermaid.docx");

        await new DocxExportService().ExportAsync(md, docxPath, new AppSettings());
        var reimported = new ReverseImportService().ImportFromDocx(docxPath);

        File.WriteAllText(Path.Combine(outDir, "rt_mermaid_original.md"), md);
        File.WriteAllText(Path.Combine(outDir, "rt_mermaid_reimported.md"), reimported);

        var m = Regex.Match(reimported, "```mermaid\n(?<body>.*?)\n```", RegexOptions.Singleline);
        Assert.True(m.Success, "reimported markdown has no mermaid fence:\n" + reimported);
        Assert.Equal(canonical, m.Groups["body"].Value);
    }
}
