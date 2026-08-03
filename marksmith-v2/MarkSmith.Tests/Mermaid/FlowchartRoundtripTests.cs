namespace MarkSmith.Core.Tests.Mermaid;

using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using Xunit;

public class FlowchartRoundtripTests
{
    [Fact]
    public void Flowchart_BasicNodesAndEdges_ParsesCorrectly()
    {
        string code = @"flowchart TD
    A[Start Process] --> B{Is Valid?}
    B -- Yes --> C[(Database)]
    B -- No --> D[Error Log]";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Ast);

        var ast = Assert.IsType<FlowchartDiagramAst>(result.Ast);
        Assert.Equal(FlowDirection.TD, ast.Direction);
        Assert.Equal(4, ast.Nodes.Count);
        Assert.Equal(FlowNodeShape.Rectangle, ast.Nodes["A"].Shape);
        Assert.Equal("Start Process", ast.Nodes["A"].Text);
        Assert.Equal(FlowNodeShape.RhombusDiamond, ast.Nodes["B"].Shape);
        Assert.Equal(FlowNodeShape.CylindricalDatabase, ast.Nodes["C"].Shape);
        Assert.Equal(3, ast.Edges.Count);
    }

    [Fact]
    public void Flowchart_Subgraphs_ParsesCorrectly()
    {
        string code = @"flowchart LR
    subgraph Storage [""Data Storage""]
        C[(DB)]
    end
    A[App] --> C";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<FlowchartDiagramAst>(result.Ast);

        Assert.Single(ast.Subgraphs);
        Assert.Equal("Storage", ast.Subgraphs[0].Id);
        Assert.Equal("Data Storage", ast.Subgraphs[0].Title);
        Assert.Contains("C", ast.Subgraphs[0].NodeIds);
    }

    [Theory]
    [InlineData("flowchart TD\n    A[Start] --> B{Choice}\n    B -- Yes --> C((End))\n")]
    [InlineData("flowchart LR\n    id1[(Database)] ==> id2[/Output/]\n")]
    public void Flowchart_ParseAndGenerate_IsIdempotent(string inputCode)
    {
        var parseResult1 = MermaidParser.Parse(inputCode);
        Assert.True(parseResult1.IsSuccess);
        Assert.NotNull(parseResult1.Ast);

        string generatedCode1 = MermaidCodeGenerator.Generate(parseResult1.Ast!);

        var parseResult2 = MermaidParser.Parse(generatedCode1);
        Assert.True(parseResult2.IsSuccess);

        string generatedCode2 = MermaidCodeGenerator.Generate(parseResult2.Ast!);
        Assert.Equal(generatedCode1, generatedCode2);
    }

    // Regression: the canonical mermaid edge-label form "A -->|label| B[Node]" used to be
    // misparsed — the plain "-->" alternative won, leaving "|label| B[Node]" to be swallowed as a
    // single garbage node. The label must land on the edge and the target node must keep its shape.
    [Fact]
    public void Flowchart_ArrowThenPipeLabel_ParsesEdgeLabelAndTargetNode()
    {
        string code = @"flowchart TD
    B -->|Yes| C[Do it]";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<FlowchartDiagramAst>(result.Ast);

        // The target node is a real rectangle labelled "Do it", not a "|Yes| C[Do it]" blob.
        Assert.True(ast.Nodes.ContainsKey("C"));
        Assert.Equal("Do it", ast.Nodes["C"].Text);
        Assert.Equal(FlowNodeShape.Rectangle, ast.Nodes["C"].Shape);
        Assert.False(ast.Nodes.ContainsKey("|Yes| C[Do it]"));

        var edge = Assert.Single(ast.Edges);
        Assert.Equal("B", edge.FromId);
        Assert.Equal("C", edge.ToId);
        Assert.Equal("Yes", edge.Label);
        // The arrowhead survives the trailing label ("-->|Yes|" ends in '|', not '>').
        Assert.Equal(FlowArrowHead.Normal, edge.EndHead);
    }

    [Theory]
    [InlineData("flowchart TD\n    A ==>|Fast| B[Done]", FlowLineStyle.Thick, FlowArrowHead.Normal, "Fast")]
    [InlineData("flowchart TD\n    A -.->|Maybe| B[Done]", FlowLineStyle.Dashed, FlowArrowHead.Normal, "Maybe")]
    [InlineData("flowchart TD\n    A ---|Link| B[Done]", FlowLineStyle.Solid, FlowArrowHead.None, "Link")]
    public void Flowchart_ArrowThenPipeLabel_KeepsLineStyle(string inputCode, FlowLineStyle expectedStyle, FlowArrowHead expectedEndHead, string expectedLabel)
    {
        var result = MermaidParser.Parse(inputCode);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<FlowchartDiagramAst>(result.Ast);

        var edge = Assert.Single(ast.Edges);
        Assert.Equal(expectedLabel, edge.Label);
        Assert.Equal(expectedStyle, edge.LineStyle);
        Assert.Equal(expectedEndHead, edge.EndHead);
        Assert.Equal("Done", ast.Nodes["B"].Text);
    }
}
