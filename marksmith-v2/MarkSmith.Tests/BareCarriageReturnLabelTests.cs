using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Core.Composer;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// A WinUI TextBox stores a typed line break as a bare '\r'. The Diagram Studio and Shape Studio
// label boxes are TextBoxes, so every label can arrive with '\r' line breaks — which used to
// split Mermaid statements (corrupting the diagram) and run Shape labels together into one word.
public class BareCarriageReturnLabelTests
{
    private static MermaidStudioViewModel FlowchartVm()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown("```mermaid\nflowchart TD\n    A[Start] -->|go| B[End]\n```", 0);
        return vm;
    }

    [Theory]
    [InlineData("Line one\rLine two")]
    [InlineData("Line one\nLine two")]
    [InlineData("Line one\r\nLine two")]
    public void Flowchart_label_typed_with_any_line_break_stays_one_statement(string typed)
    {
        var vm = FlowchartVm();
        vm.Nodes.First(n => n.Id == "A").LabelText = typed;

        string code = vm.GenerateMermaidCode();

        Assert.Contains("A[\"Line one<br/>Line two\"]", code);
        Assert.DoesNotContain("\r", code.Replace("\r\n", "\n"));
        var parsed = MermaidParser.Parse(code);
        Assert.NotNull(parsed.Ast);
    }

    [Fact]
    public void Flowchart_multi_line_label_round_trips_to_a_real_line_break_on_the_canvas()
    {
        var vm = FlowchartVm();
        vm.Nodes.First(n => n.Id == "A").LabelText = "Line one\rLine two";
        string code = vm.GenerateMermaidCode();

        var again = new MermaidStudioViewModel();
        Assert.True(again.SyncCanvasFromCode(code));
        Assert.Equal("Line one\nLine two", again.Nodes.First(n => n.Id == "A").LabelText);
        Assert.Equal(2, again.Nodes.Count);
    }

    [Fact]
    public void Edge_label_with_bare_cr_is_written_as_a_break_tag()
    {
        var vm = FlowchartVm();
        vm.Connectors.First().Label = "yes\rplease";

        string code = vm.GenerateMermaidCode();

        Assert.Contains("\"yes<br/>please\"", code);
        Assert.NotNull(MermaidParser.Parse(code).Ast);
    }

    [Fact]
    public void Sequence_message_and_alias_keep_line_breaks_as_break_tags()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown("```mermaid\nsequenceDiagram\n    participant A as Alice\n    participant B as Bob\n    A->>B: Hello\n```", 0);
        vm.Nodes.First(n => n.Id == "A").LabelText = "Alice\rSmith";
        vm.Connectors.First().Label = "Hello\rthere";

        string code = vm.GenerateMermaidCode();

        Assert.Contains("participant A as Alice<br/>Smith", code);
        Assert.Contains("A->>B: Hello<br/>there", code);

        var again = new MermaidStudioViewModel();
        Assert.True(again.SyncCanvasFromCode(code));
        Assert.Equal("Alice\nSmith", again.Nodes.First(n => n.Id == "A").LabelText);
        Assert.Equal("Hello\nthere", again.Connectors.First().Label);
    }

    [Fact]
    public void Mindmap_and_gantt_text_is_joined_onto_one_line()
    {
        Assert.Equal("Phase one kickoff", MermaidCodeGenerator.OneLine("Phase one\r kickoff"));
        Assert.Equal("a b c", MermaidCodeGenerator.OneLine("a\r\nb\nc\r"));
        Assert.Equal(new[] { "a", "b", "c" }, MermaidCodeGenerator.Lines("a\rb\r\nc"));
    }

    [Fact]
    public void Node_bounds_count_bare_cr_lines()
    {
        var single = new DiagramNodeViewModel { LabelText = "Line one Line two" };
        var multi = new DiagramNodeViewModel { LabelText = "Line one\rLine two" };
        single.RecalculateBoundsForText();
        multi.RecalculateBoundsForText();

        Assert.True(multi.Height > single.Height);
        Assert.True(multi.Width < single.Width);
    }

    [Fact]
    public void Shape_label_with_bare_cr_keeps_both_lines_through_the_codec()
    {
        var shape = new ComposedShape { Prst = "rect", X = 0, Y = 0, W = 2, H = 1, Fill = "FFFFFF", Text = "Line one\rLine two" };

        string line = ShapeMarkdownCodec.Format(shape);
        Assert.Contains("text=\"Line one&#10;Line two\"", line);

        var back = ShapeMarkdownCodec.Parse(line).Single();
        Assert.Equal("Line one\nLine two", back.Text);
    }

    [Fact]
    public void Shape_label_literal_entity_text_survives_the_codec()
    {
        var shape = new ComposedShape { Prst = "rect", X = 0, Y = 0, W = 2, H = 1, Fill = "FFFFFF", Text = "use &#10; here" };
        var back = ShapeMarkdownCodec.Parse(ShapeMarkdownCodec.Format(shape)).Single();
        Assert.Equal("use &#10; here", back.Text);
    }

    [Fact]
    public void Shape_preview_draws_a_bare_cr_label_on_two_lines()
    {
        var shape = new ComposedShape { Prst = "rect", X = 0, Y = 0, W = 3, H = 2, Fill = "FFFFFF", Text = "Alpha\rOmega" };
        string svg = ImageShapeComposer.RenderSvg(new[] { shape }.ToList(), 3, 2);

        Assert.Contains(">Alpha</tspan>", svg);
        Assert.Contains(">Omega</tspan>", svg);
    }

    [Fact]
    public void Shape_docx_label_breaks_lines_with_w_br()
    {
        string docx = Path.Combine(Path.GetTempPath(), $"crlabel-{Guid.NewGuid():N}.docx");
        try
        {
            var shape = new ComposedShape { Prst = "rect", X = 0, Y = 0, W = 3, H = 2, Fill = "FFFFFF", Text = "Alpha\rOmega" };
            ShapeComposerDocxWriter.WriteDocx(docx, new[] { shape }.ToList(), 3.0, 2.0, null);

            using var doc = WordprocessingDocument.Open(docx, false);
            using var r = new StreamReader(doc.MainDocumentPart!.GetStream());
            string xml = r.ReadToEnd();
            Assert.Contains(">Alpha</w:t><w:br/><w:t xml:space=\"preserve\">Omega</w:t>", xml);
        }
        finally
        {
            if (File.Exists(docx)) File.Delete(docx);
        }
    }
}
