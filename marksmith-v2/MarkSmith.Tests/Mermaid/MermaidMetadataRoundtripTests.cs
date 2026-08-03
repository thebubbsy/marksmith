namespace MarkSmith.Core.Tests.Mermaid;

using System.Collections.Generic;
using System.Linq;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using Xunit;

public class MermaidMetadataRoundtripTests
{
    [Fact]
    public void Test_MermaidMetadata_Parse_Extract_Modify_Serialize_Roundtrip()
    {
        // a) Parsing a Markdown block containing standard Mermaid syntax and position metadata comments
        string inputMermaid = "%% {\"id\":\"A\", \"x\":100, \"y\":200}\n" +
                               "%% {\"id\":\"B\", \"x\":300, \"y\":200}\n" +
                               "flowchart TD\n" +
                               "    A --> B";

        var parseResult = MermaidParser.Parse(inputMermaid);
        Assert.NotNull(parseResult.Ast);
        var ast = parseResult.Ast;

        // b) Extracting metadata comments into node coordinates
        var positionMap = MermaidMetadataService.ExtractPositions(ast.Comments);
        Assert.Equal(2, positionMap.Count);
        Assert.True(positionMap.ContainsKey("A"));
        Assert.True(positionMap.ContainsKey("B"));

        Assert.Equal(100, positionMap["A"].X);
        Assert.Equal(200, positionMap["A"].Y);
        Assert.Equal(300, positionMap["B"].X);
        Assert.Equal(200, positionMap["B"].Y);

        // c) Modifying node coordinates to (X=350, Y=450) and serializing back into %% {"id":"A", "x":350, "y":450} comments
        positionMap["A"].X = 350;
        positionMap["A"].Y = 450;

        MermaidMetadataService.InjectPositions(ast, positionMap.Values);

        string outputMermaid = MermaidCodeGenerator.Generate(ast);
        Assert.Contains("%% {\"id\":\"A\", \"x\":350, \"y\":450}", outputMermaid);

        // Verify re-parsing output metadata
        var reParsedResult = MermaidParser.Parse(outputMermaid);
        Assert.NotNull(reParsedResult.Ast);
        var reExtractedMap = MermaidMetadataService.ExtractPositions(reParsedResult.Ast.Comments);

        Assert.True(reExtractedMap.ContainsKey("A"));
        Assert.Equal(350, reExtractedMap["A"].X);
        Assert.Equal(450, reExtractedMap["A"].Y);

        Assert.True(reExtractedMap.ContainsKey("B"));
        Assert.Equal(300, reExtractedMap["B"].X);
        Assert.Equal(200, reExtractedMap["B"].Y);

        // d) Confirming standard Mermaid syntax (flowchart TD, A --> B) remains 100% uncorrupted and valid
        Assert.Contains("flowchart TD", outputMermaid);
        Assert.Contains("A --> B", outputMermaid);
    }

    [Fact]
    public void Test_MermaidMetadataService_PreservesNonMetadataComments()
    {
        var ast = new FlowchartDiagramAst();
        ast.Comments.Add("Human readable comment");
        ast.Comments.Add("{\"id\":\"X\", \"x\":10, \"y\":20}");

        var positions = new[]
        {
            new NodePositionMetadata { Id = "X", X = 150, Y = 250, Width = 100, Height = 50 }
        };

        MermaidMetadataService.InjectPositions(ast, positions);

        Assert.Contains("Human readable comment", ast.Comments);
        Assert.DoesNotContain(ast.Comments, c => c.Contains("\"x\":10"));
        Assert.Contains(ast.Comments, c => c.Contains("\"x\":150"));

        string code = MermaidCodeGenerator.Generate(ast);
        Assert.Contains("%% Human readable comment", code);
        Assert.Contains("%% {\"id\":\"X\", \"x\":150, \"y\":250, \"width\":100, \"height\":50}", code);
        Assert.Contains("flowchart TD", code);
    }
}
