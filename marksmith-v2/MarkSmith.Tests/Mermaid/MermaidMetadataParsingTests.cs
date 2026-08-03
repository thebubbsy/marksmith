namespace MarkSmith.Core.Tests.Mermaid;

using System.Collections.Generic;
using System.Linq;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

public class MermaidMetadataParsingTests
{
    [Fact]
    public void ExtractPositions_ParsesValidJsonComments_ReturnsPositionsDictionary()
    {
        var comments = new List<string>
        {
            "%% {\"id\":\"A\", \"x\":100, \"y\":200}",
            "{\"id\":\"B\", \"x\":300.5, \"y\":400.25, \"width\":140, \"height\":60}",
            "%% Regular non-JSON comment"
        };

        var positions = MermaidMetadataService.ExtractPositions(comments);

        Assert.Equal(2, positions.Count);
        Assert.True(positions.ContainsKey("A"));
        Assert.Equal(100, positions["A"].X);
        Assert.Equal(200, positions["A"].Y);

        Assert.True(positions.ContainsKey("B"));
        Assert.Equal(300.5, positions["B"].X);
        Assert.Equal(400.25, positions["B"].Y);
        Assert.Equal(140, positions["B"].Width);
        Assert.Equal(60, positions["B"].Height);
    }

    [Fact]
    public void InjectPositions_ReplacesPositionComments_PreservesRegularComments()
    {
        var ast = new FlowchartDiagramAst();
        ast.Comments.Add("%% Regular human comment");
        ast.Comments.Add("{\"id\":\"A\", \"x\":10, \"y\":20}");

        var newPositions = new List<NodePositionMetadata>
        {
            new NodePositionMetadata { Id = "A", X = 500, Y = 600 }
        };

        MermaidMetadataService.InjectPositions(ast, newPositions);

        Assert.Contains(ast.Comments, c => c.Contains("\"id\":\"A\"") && c.Contains("\"x\":500") && c.Contains("\"y\":600"));
        Assert.Contains(ast.Comments, c => c.Contains("Regular human comment"));

        string generatedCode = MermaidCodeGenerator.Generate(ast);
        Assert.Contains("%% {\"id\":\"A\", \"x\":500, \"y\":600}", generatedCode);
        Assert.Contains("%% Regular human comment", generatedCode);
    }

    [Fact]
    public void FullRoundtrip_MermaidCodeWithMetadata_PreservesPositionsAndSyntax()
    {
        string inputCode = "%% {\"id\":\"A\", \"x\":100, \"y\":200}\n" +
                           "%% {\"id\":\"B\", \"x\":350, \"y\":450}\n" +
                           "flowchart TD\n" +
                           "    A[Start Process] --> B[End Process]";

        var parseResult = MermaidParser.Parse(inputCode);
        Assert.True(parseResult.IsSuccess);
        Assert.NotNull(parseResult.Ast);

        var ast = parseResult.Ast!;
        var extractedPositions = MermaidMetadataService.ExtractPositions(ast.Comments);

        Assert.Equal(2, extractedPositions.Count);
        Assert.Equal(100, extractedPositions["A"].X);
        Assert.Equal(200, extractedPositions["A"].Y);
        Assert.Equal(350, extractedPositions["B"].X);
        Assert.Equal(450, extractedPositions["B"].Y);

        string generatedCode = MermaidCodeGenerator.Generate(ast);
        Assert.Contains("%% {\"id\":\"A\", \"x\":100, \"y\":200}", generatedCode);
        Assert.Contains("%% {\"id\":\"B\", \"x\":350, \"y\":450}", generatedCode);
        Assert.Contains("flowchart TD", generatedCode);
        Assert.Contains("A[", generatedCode);
        Assert.Contains("B[", generatedCode);
        Assert.Contains("-->", generatedCode);
    }

    [Fact]
    public void MermaidStudioViewModel_AstToCanvas_PopulatesNodePositionsAndPreservesInLayout()
    {
        string code = "%% {\"id\":\"A\", \"x\":150, \"y\":250}\n" +
                      "%% {\"id\":\"B\", \"x\":400, \"y\":500}\n" +
                      "flowchart TD\n" +
                      "    A[Start] --> B[End]";

        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(code);

        Assert.Equal(2, vm.Nodes.Count);
        var nodeA = vm.Nodes.First(n => n.Id == "A");
        var nodeB = vm.Nodes.First(n => n.Id == "B");

        Assert.Equal(150, nodeA.X);
        Assert.Equal(250, nodeA.Y);
        Assert.True(nodeA.HasCustomPosition);

        Assert.Equal(400, nodeB.X);
        Assert.Equal(500, nodeB.Y);
        Assert.True(nodeB.HasCustomPosition);

        // Auto-layout should preserve existing custom positions
        vm.ApplyAutoLayout();

        Assert.Equal(150, nodeA.X);
        Assert.Equal(250, nodeA.Y);
        Assert.Equal(400, nodeB.X);
        Assert.Equal(500, nodeB.Y);
    }

    [Fact]
    public void MermaidStudioViewModel_CanvasToAst_ModifyingCoordinatesSavesCleanlyToComments()
    {
        string initialCode = "flowchart TD\n" +
                             "    A[Start] --> B[End]";

        var vm = new MermaidStudioViewModel();
        vm.LoadFromMermaidCode(initialCode);

        // Modify coordinates via VM
        var nodeA = vm.Nodes.First(n => n.Id == "A");
        var nodeB = vm.Nodes.First(n => n.Id == "B");

        nodeA.X = 123;
        nodeA.Y = 456;
        nodeB.X = 789;
        nodeB.Y = 987;

        string updatedCode = vm.GenerateMermaidCode();

        Assert.Contains("%% {\"id\":\"A\", \"x\":123, \"y\":456", updatedCode);
        Assert.Contains("%% {\"id\":\"B\", \"x\":789, \"y\":987", updatedCode);

        // Re-parse updated code to verify roundtrip integrity
        var parseResult = MermaidParser.Parse(updatedCode);
        Assert.True(parseResult.IsSuccess);
        Assert.NotNull(parseResult.Ast);

        var extracted = MermaidMetadataService.ExtractPositions(parseResult.Ast!.Comments);
        Assert.Equal(123, extracted["A"].X);
        Assert.Equal(456, extracted["A"].Y);
        Assert.Equal(789, extracted["B"].X);
        Assert.Equal(987, extracted["B"].Y);
    }

    [Fact]
    public void ExtractPositions_EdgeCases_HandlesMalformedJsonAndStringNumbers()
    {
        var comments = new List<string>
        {
            "%% {\"id\":\"C\", \"x\":\"123.5\", \"y\":\"456.7\", \"w\":\"200\", \"h\":\"100\"}",
            "%% {\"invalid_json\": true}",
            "%% ",
            "",
            "%% {\"id\":\"\"}"
        };

        var positions = MermaidMetadataService.ExtractPositions(comments);

        Assert.Single(positions);
        Assert.True(positions.ContainsKey("C"));
        Assert.Equal(123.5, positions["C"].X);
        Assert.Equal(456.7, positions["C"].Y);
        Assert.Equal(200, positions["C"].Width);
        Assert.Equal(100, positions["C"].Height);
    }
}
