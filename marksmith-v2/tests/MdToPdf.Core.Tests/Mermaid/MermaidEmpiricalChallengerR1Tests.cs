namespace MdToPdf.Core.Tests.Mermaid;

using System.Collections.Generic;
using System.Linq;
using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using MdToPdf.Mermaid.Sync;
using MdToPdf.ViewModels.Mermaid;
using Xunit;

public class MermaidEmpiricalChallengerR1Tests
{
    [Fact]
    public void VerifyR1_MetadataCommentParsing_HandlesVariedFormatsAndFloatingPoints()
    {
        var comments = new List<string>
        {
            "%% {\"id\":\"Node_1\", \"x\":500.75, \"y\":200.25, \"width\":150.5, \"height\":60.25}",
            "%%   {\"Id\":\"Node_2\", \"X\":\"350.5\", \"Y\":\"450.0\", \"W\":\"180\", \"H\":\"75\"}  ",
            "%% This is a standard non-JSON Mermaid comment line",
            "%% {\"id\":\"Node_3\", \"x\":0, \"y\":0}"
        };

        var positions = MermaidMetadataService.ExtractPositions(comments);

        Assert.Equal(3, positions.Count);
        
        Assert.True(positions.ContainsKey("Node_1"));
        Assert.Equal(500.75, positions["Node_1"].X);
        Assert.Equal(200.25, positions["Node_1"].Y);
        Assert.Equal(150.5, positions["Node_1"].Width);
        Assert.Equal(60.25, positions["Node_1"].Height);

        Assert.True(positions.ContainsKey("Node_2"));
        Assert.Equal(350.5, positions["Node_2"].X);
        Assert.Equal(450.0, positions["Node_2"].Y);
        Assert.Equal(180.0, positions["Node_2"].Width);
        Assert.Equal(75.0, positions["Node_2"].Height);

        Assert.True(positions.ContainsKey("Node_3"));
        Assert.Equal(0, positions["Node_3"].X);
        Assert.Equal(0, positions["Node_3"].Y);
    }

    [Fact]
    public void VerifyR1_CanvasCoordinatesLoadAndModifyRoundTripCleanly_StandardTD()
    {
        string markdown = "# System Diagram\n\n" +
                          "```mermaid\n" +
                          "%% {\"id\":\"A\", \"x\":100, \"y\":150, \"width\":120, \"height\":50}\n" +
                          "%% {\"id\":\"B\", \"x\":300, \"y\":150, \"width\":120, \"height\":50}\n" +
                          "%% Architecture review note\n" +
                          "flowchart TD\n" +
                          "    A[Client App] --> B[API Gateway]\n" +
                          "```\n\n" +
                          "End of document.";

        // 1. Extract block and load into VM
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown(markdown, 0);

        Assert.Equal(2, vm.Nodes.Count);
        var nodeA = vm.Nodes.First(n => n.Id == "A");
        var nodeB = vm.Nodes.First(n => n.Id == "B");

        Assert.Equal(100, nodeA.X);
        Assert.Equal(150, nodeA.Y);
        Assert.Equal(120, nodeA.Width);
        Assert.Equal(50, nodeA.Height);

        Assert.Equal(300, nodeB.X);
        Assert.Equal(150, nodeB.Y);

        // 2. Modify coordinates on Canvas
        nodeA.X = 550;
        nodeA.Y = 250;
        nodeB.X = 850;
        nodeB.Y = 250;

        // 3. Sync modified coordinates back to Markdown
        string updatedMarkdown = vm.SyncToMarkdown(markdown);

        // 4. Verify updated %% metadata comments in synced Markdown
        Assert.Contains("%% {\"id\":\"A\", \"x\":550, \"y\":250, \"width\":120, \"height\":50}", updatedMarkdown);
        Assert.Contains("%% {\"id\":\"B\", \"x\":850, \"y\":250, \"width\":120, \"height\":50}", updatedMarkdown);
        Assert.Contains("%% Architecture review note", updatedMarkdown);
        Assert.Contains("flowchart TD", updatedMarkdown);
        Assert.Contains("A[\"Client App\"]", updatedMarkdown);
        Assert.Contains("B[\"API Gateway\"]", updatedMarkdown);
        Assert.Contains("A --> B", updatedMarkdown);
        Assert.Contains("End of document.", updatedMarkdown);

        // 5. Re-parse updated markdown block to ensure clean roundtrip
        var reBlocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updatedMarkdown);
        Assert.Single(reBlocks);

        var reParse = MermaidParser.Parse(reBlocks[0].Code);
        Assert.True(reParse.IsSuccess);
        var rePositions = MermaidMetadataService.ExtractPositions(reParse.Ast!.Comments);

        Assert.Equal(550, rePositions["A"].X);
        Assert.Equal(250, rePositions["A"].Y);
        Assert.Equal(850, rePositions["B"].X);
        Assert.Equal(250, rePositions["B"].Y);

        var flowchart = (FlowchartDiagramAst)reParse.Ast!;
        Assert.Equal(2, flowchart.Nodes.Count);
        Assert.Equal("Client App", flowchart.Nodes["A"].Text);
        Assert.Equal("API Gateway", flowchart.Nodes["B"].Text);
    }

    [Fact]
    public void VerifyR1_ExternalMermaidRendererSyntaxCompliance()
    {
        string diagramWithMetadata = "%% {\"id\":\"A\", \"x\":100, \"y\":200}\n" +
                                     "%% {\"id\":\"B\", \"x\":400, \"y\":200}\n" +
                                     "%% {\"id\":\"C\", \"x\":250, \"y\":400}\n" +
                                     "flowchart TD\n" +
                                     "    A[Input Data] ==> B{Valid?}\n" +
                                     "    B -- Yes --> C[Process]\n" +
                                     "    B -. No .-> A";

        var parseResult = MermaidParser.Parse(diagramWithMetadata);
        Assert.True(parseResult.IsSuccess);

        // Strip %% comment lines to simulate external renderer ignoring %% lines
        string cleanMermaid = string.Join("\n", diagramWithMetadata
            .Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.TrimStart().StartsWith("%%")));

        // Verify standard Mermaid code parses cleanly without %% lines
        var cleanParseResult = MermaidParser.Parse(cleanMermaid);
        Assert.True(cleanParseResult.IsSuccess);
        Assert.NotNull(cleanParseResult.Ast);

        var flowchart = (FlowchartDiagramAst)cleanParseResult.Ast!;
        Assert.Equal(3, flowchart.Nodes.Count);
        Assert.Equal(3, flowchart.Edges.Count);
        Assert.Equal(FlowDirection.TD, flowchart.Direction);
    }
}
