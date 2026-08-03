namespace MarkSmith.Core.Tests.Mermaid;

using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Sync;
using Xunit;

public class MarkdownSyncTests
{
    [Fact]
    public void ExtractMermaidBlocks_FindsFencedCodeBlocks()
    {
        string markdown = @"# Document Header

Here is diagram 1:
```mermaid
flowchart TD
    A --> B
```

Some text in between.

```mermaid
sequenceDiagram
    Alice->>Bob: Hi
```
";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        Assert.Equal(2, blocks.Count);
        Assert.Contains("flowchart TD", blocks[0].Code);
        Assert.Contains("sequenceDiagram", blocks[1].Code);
    }

    [Fact]
    public void ReplaceMermaidBlock_UpdatesTargetBlockInMarkdown()
    {
        string markdown = @"# Doc

```mermaid
flowchart TD
    A --> B
```

Trailing prose.";

        string newCode = "flowchart LR\n    A[Start] --> B[End]";
        string updatedDoc = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 0, newCode);

        Assert.Contains("flowchart LR", updatedDoc);
        Assert.Contains("A[Start] --> B[End]", updatedDoc);
        Assert.Contains("# Doc", updatedDoc);
        Assert.Contains("Trailing prose.", updatedDoc);
    }

    [Fact]
    public void SyncAstToMarkdown_ParsesAndSyncsAst()
    {
        string markdown = @"# Doc

```mermaid
flowchart TD
    A --> B
```";

        var ast = new FlowchartDiagramAst
        {
            Direction = FlowDirection.LR
        };
        ast.Nodes["A"] = new FlowNode { Id = "A", Text = "Alpha" };
        ast.Nodes["B"] = new FlowNode { Id = "B", Text = "Beta" };
        ast.Edges.Add(new FlowEdge { FromId = "A", ToId = "B" });

        string updatedDoc = MermaidMarkdownSyncService.SyncAstToMarkdown(markdown, 0, ast);

        Assert.Contains("flowchart LR", updatedDoc);
        Assert.Contains("A[Alpha]", updatedDoc);
        Assert.Contains("B[Beta]", updatedDoc);
    }
}
