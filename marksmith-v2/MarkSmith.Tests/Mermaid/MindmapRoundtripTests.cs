namespace MdToPdf.Core.Tests.Mermaid;

using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using Xunit;

public class MindmapRoundtripTests
{
    [Fact]
    public void Mindmap_IndentedTree_ParsesCorrectly()
    {
        string code = @"mindmap
    Root Node
        Branch 1
            ((Circle Node))
        Branch 2";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<MindmapAst>(result.Ast);

        Assert.NotNull(ast.Root);
        Assert.Equal("Root Node", ast.Root.Text);
        Assert.Equal(2, ast.Root.Children.Count);
        Assert.Equal("Branch 1", ast.Root.Children[0].Text);
        Assert.Single(ast.Root.Children[0].Children);
        Assert.Equal(MindmapNodeShape.Circle, ast.Root.Children[0].Children[0].Shape);
        Assert.Equal("Circle Node", ast.Root.Children[0].Children[0].Text);
    }

    [Theory]
    [InlineData("mindmap\n    Root\n        Topic A\n            Subtopic 1\n        Topic B\n")]
    public void Mindmap_ParseAndGenerate_IsIdempotent(string inputCode)
    {
        var result1 = MermaidParser.Parse(inputCode);
        Assert.True(result1.IsSuccess);

        string gen1 = MermaidCodeGenerator.Generate(result1.Ast!);
        var result2 = MermaidParser.Parse(gen1);
        Assert.True(result2.IsSuccess);

        string gen2 = MermaidCodeGenerator.Generate(result2.Ast!);
        Assert.Equal(gen1, gen2);
    }
}
