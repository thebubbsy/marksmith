namespace MarkSmith.Core.Tests.Mermaid;

using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using Xunit;

public class ClassDiagramRoundtripTests
{
    [Fact]
    public void ClassDiagram_MembersAndRelationships_ParsesCorrectly()
    {
        string code = @"classDiagram
    class Customer {
        <<interface>>
        +String name
        +getOrders(int limit) List
    }
    Customer ""1"" *-- ""many"" Order : contains";

        var result = MermaidParser.Parse(code);
        Assert.True(result.IsSuccess);
        var ast = Assert.IsType<ClassDiagramAst>(result.Ast);

        Assert.True(ast.Classes.ContainsKey("Customer"));
        var cls = ast.Classes["Customer"];
        Assert.Equal("<<interface>>", cls.Annotation);
        Assert.Single(cls.Attributes);
        Assert.Single(cls.Methods);
        Assert.Single(ast.Relationships);
        Assert.Equal(ClassRelationshipType.Composition, ast.Relationships[0].RelationshipType);
    }

    [Theory]
    [InlineData("classDiagram\n    class Animal {\n        +String species\n        +makeSound() void\n    }\n")]
    public void ClassDiagram_ParseAndGenerate_IsIdempotent(string inputCode)
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
