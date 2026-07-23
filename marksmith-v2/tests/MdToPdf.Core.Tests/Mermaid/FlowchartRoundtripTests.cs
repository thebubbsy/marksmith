namespace MdToPdf.Core.Tests.Mermaid;

using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
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
}
