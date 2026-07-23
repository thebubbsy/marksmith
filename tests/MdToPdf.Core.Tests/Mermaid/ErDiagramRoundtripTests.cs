namespace MdToPdf.Core.Tests.Mermaid;

using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using Xunit;

public class ErDiagramRoundtripTests
{
    [Fact]
    public void ErDiagram_EntitiesAndRelationships_ParsesCorrectly()
    {
        string code = @"erDiagram
    CUSTOMER {
        string name PK ""Customer Name""
        string email FK
    }
    ORDER {
        int orderId PK
    }
    CUSTOMER ||--o{ ORDER : places";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<ErDiagramAst>(result.Ast);

        Assert.Equal(2, ast.Entities.Count);
        Assert.True(ast.Entities.ContainsKey("CUSTOMER"));
        Assert.Equal(2, ast.Entities["CUSTOMER"].Attributes.Count);
        Assert.True(ast.Entities["CUSTOMER"].Attributes[0].IsPrimaryKey);
        Assert.Equal("Customer Name", ast.Entities["CUSTOMER"].Attributes[0].Comment);
        Assert.Single(ast.Relationships);
        Assert.Equal(ErCardinality.ExactlyOne, ast.Relationships[0].Cardinality1);
        Assert.Equal(ErCardinality.ZeroOrMore, ast.Relationships[0].Cardinality2);
    }

    [Theory]
    [InlineData("erDiagram\n    USER {\n        int id PK\n    }\n    POST {\n        int userId FK\n    }\n    USER ||--o{ POST : writes\n")]
    public void ErDiagram_ParseAndGenerate_IsIdempotent(string inputCode)
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
