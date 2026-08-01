namespace MdToPdf.Core.Tests.Mermaid;

using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using Xunit;

public class GanttRoundtripTests
{
    [Fact]
    public void Gantt_TasksAndSections_ParsesCorrectly()
    {
        string code = @"gantt
    title Software Roadmap
    dateFormat YYYY-MM-DD
    axisFormat %Y-%m-%d
    section Planning
        Task 1 :active, des1, 2024-01-01, 30d
        Task 2 :done, des2, after des1, 20d";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<GanttChartAst>(result.Ast);

        Assert.Equal("Software Roadmap", ast.Title);
        Assert.Single(ast.Sections);
        Assert.Equal("Planning", ast.Sections[0].Name);
        Assert.Equal(2, ast.Sections[0].Tasks.Count);
        Assert.True(ast.Sections[0].Tasks[0].Status.HasFlag(GanttTaskStatus.Active));
    }

    [Theory]
    [InlineData("gantt\n    title Sprint Plan\n    dateFormat YYYY-MM-DD\n    axisFormat %Y-%m-%d\n    section Core\n        Dev Task :active, t1, 2024-01-01, 10d\n")]
    public void Gantt_ParseAndGenerate_IsIdempotent(string inputCode)
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
