namespace MdToPdf.Core.Tests.Mermaid;

using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using Xunit;

public class StateDiagramRoundtripTests
{
    [Fact]
    public void StateDiagram_TransitionsAndChoice_ParsesCorrectly()
    {
        string code = @"stateDiagram-v2
    [*] --> Idle
    state check_state <<choice>>
    Idle --> check_state : CheckCondition
    check_state --> Active : Valid
    check_state --> Error : Invalid";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<StateDiagramAst>(result.Ast);

        Assert.True(ast.IsV2);
        Assert.True(ast.States.ContainsKey("check_state"));
        Assert.Equal(StateNodeType.Choice, ast.States["check_state"].Type);
        Assert.Equal(4, ast.Transitions.Count);
    }

    [Theory]
    [InlineData("stateDiagram-v2\n    [*] --> Off\n    Off --> On : Toggle\n    On --> Off : Toggle\n")]
    public void StateDiagram_ParseAndGenerate_IsIdempotent(string inputCode)
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
