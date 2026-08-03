namespace MarkSmith.Core.Tests.Mermaid;

using System;
using System.Linq;

using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.Mermaid.Sync;
using Xunit;
using Xunit.Abstractions;

public class MermaidEmpiricalChallengerTests
{
    private readonly ITestOutputHelper _output;

    public MermaidEmpiricalChallengerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Test_PaletteShape_Rhombus_Vs_Enum_RhombusDiamond_Mismatch()
    {
        // Palette item specifies ShapeType = "Rhombus"
        string paletteShapeType = "Rhombus";

        // CanvasToAst uses Enum.TryParse<FlowNodeShape>(n.Shape, true, out var shape)
        bool parsed = Enum.TryParse<FlowNodeShape>(paletteShapeType, true, out var shape);

        // Verification: "Rhombus" fails to parse into FlowNodeShape (which has RhombusDiamond)
        _output.WriteLine($"TryParse('Rhombus') result: {parsed}, parsed shape: {shape}");
        Assert.False(parsed); // Demonstrates the bug: Enum.TryParse fails and defaults to Rectangle (0)
        Assert.Equal(FlowNodeShape.Rectangle, shape);
    }

    [Fact]
    public void Test_Sync_ClassDiagram_Attributes_Methods_Loss()
    {
        string classMarkdown = @"```mermaid
classDiagram
    class Customer {
        +String name
        +int age
        +placeOrder()
    }
```";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(classMarkdown);
        Assert.Single(blocks);

        var parseResult = MermaidParser.Parse(blocks[0].Code);
        Assert.True(parseResult.IsSuccess);
        var ast = (ClassDiagramAst)parseResult.Ast!;

        // Class has 2 attributes and 1 method
        Assert.Equal(2, ast.Classes["Customer"].Attributes.Count);
        Assert.Equal(1, ast.Classes["Customer"].Methods.Count);

        // Simulate CanvasToAst: creating ClassNode with Name = "Customer" without populating Attributes/Methods
        var reconstructedAst = new ClassDiagramAst();
        reconstructedAst.Classes["Customer"] = new ClassNode { Name = "Customer" };

        string syncResult = MermaidMarkdownSyncService.SyncAstToMarkdown(classMarkdown, 0, reconstructedAst);
        _output.WriteLine($"Synced Markdown Result:\n{syncResult}");

        // Attributes and methods are lost from the generated output
        Assert.DoesNotContain("+String name", syncResult);
        Assert.DoesNotContain("+placeOrder()", syncResult);
    }

    [Fact]
    public void Test_Sync_StateDiagram_ChoiceNode_Loss()
    {
        string stateMarkdown = @"```mermaid
stateDiagram-v2
    [*] --> ChoiceState
    state ChoiceState <<choice>>
```";

        var parseResult = MermaidParser.Parse(MermaidMarkdownSyncService.ExtractMermaidBlocks(stateMarkdown)[0].Code);
        Assert.True(parseResult.IsSuccess);

        // If StateNode.Type is lost/defaulted to Normal in CanvasToAst
        var reconstructedAst = new StateDiagramAst { IsV2 = true };
        reconstructedAst.States["ChoiceState"] = new StateNode { Id = "ChoiceState", Label = "ChoiceState", Type = StateNodeType.Normal };

        string syncedCode = MermaidCodeGenerator.Generate(reconstructedAst);
        _output.WriteLine($"Synced State Code:\n{syncedCode}");

        Assert.DoesNotContain("<<choice>>", syncedCode);
    }

    [Fact]
    public void Test_Sync_Gantt_Milestone_Loss()
    {
        var ganttAst = new GanttChartAst();
        var sec = new GanttSection { Name = "Sprint" };
        sec.Tasks.Add(new GanttTask { Id = "m1", Name = "Release 1.0", IsMilestone = true, StartDate = "2026-07-01", DurationOrEndDate = "0d" });
        ganttAst.Sections.Add(sec);

        string originalCode = MermaidCodeGenerator.Generate(ganttAst);
        Assert.Contains(":milestone", originalCode);

        // Simulated CanvasToAst where IsMilestone is omitted
        var lossAst = new GanttChartAst();
        var sec2 = new GanttSection { Name = "Sprint" };
        sec2.Tasks.Add(new GanttTask { Id = "m1", Name = "Release 1.0", IsMilestone = false, DurationOrEndDate = "5d" });
        lossAst.Sections.Add(sec2);

        string lossCode = MermaidCodeGenerator.Generate(lossAst);
        Assert.DoesNotContain(":milestone", lossCode);
    }

    [Fact]
    public void Test_ReplaceMermaidBlock_MultipleBlocks_PreservesIndicesAndContent()
    {
        string multiDoc = @"# Header
```mermaid
flowchart TD
    A --> B
```
Middle section prose.
```mermaid
sequenceDiagram
    User->>System: Ping
```
End prose.";

        string updated = MermaidMarkdownSyncService.ReplaceMermaidBlock(multiDoc, 1, "sequenceDiagram\n    User->>System: Pong");

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updated);
        Assert.Equal(2, blocks.Count);
        Assert.Contains("flowchart TD", blocks[0].Code);
        Assert.Contains("User->>System: Pong", blocks[1].Code);
        Assert.Contains("Middle section prose.", updated);
        Assert.Contains("End prose.", updated);
    }

    [Fact]
    public void Test_ReplaceMermaidBlock_CRLF_LineEndings()
    {
        string crlfDoc = "# Header\r\n\r\n```mermaid\r\nflowchart TD\r\n    A --> B\r\n```\r\n\r\nFooter\r\n";
        string updated = MermaidMarkdownSyncService.ReplaceMermaidBlock(crlfDoc, 0, "flowchart LR\r\n    A --> B");

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updated);
        Assert.Single(blocks);
        Assert.Contains("flowchart LR", blocks[0].Code);
    }
}
