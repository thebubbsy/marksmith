namespace MdToPdf.Core.Tests.Mermaid;

using System;
using System.Collections.Generic;
using System.Text.Json;
using MdToPdf.Mermaid.Sync;
using Xunit;

public class MermaidM3EmpiricalStressTests
{
    // ==========================================
    // FOCUS AREA 1: Diagram Index Mapping
    // ==========================================

    [Fact]
    public void IndexMapping_MultipleBlocks_ExtractedWithCorrectIndices()
    {
        string markdown = @"# Multiple Diagrams

First diagram:
```mermaid
flowchart TD
    A --> B
```

Second diagram:
```mermaid
sequenceDiagram
    Alice->>Bob: Hello
```

Third diagram:
```mermaid
gantt
    title A Gantt Diagram
    section Section
    A task :a1, 2024-01-01, 30d
```";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(0, blocks[0].BlockIndex);
        Assert.Contains("flowchart TD", blocks[0].Code);
        Assert.Equal(1, blocks[1].BlockIndex);
        Assert.Contains("sequenceDiagram", blocks[1].Code);
        Assert.Equal(2, blocks[2].BlockIndex);
        Assert.Contains("gantt", blocks[2].Code);
    }

    [Fact]
    public void IndexMapping_IdenticalBlocks_DifferentiatedByIndexInSyncService()
    {
        string markdown = @"# Identical Diagrams Test

Block 0:
```mermaid
flowchart TD
    A --> B
```

Block 1:
```mermaid
flowchart TD
    A --> B
```

Block 2:
```mermaid
flowchart TD
    A --> B
```";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        Assert.Equal(3, blocks.Count);

        // Target replacement of Block 1 specifically using MermaidMarkdownSyncService
        string newCodeForBlock1 = "flowchart LR\n    B1 --> B2";
        string updatedMd = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 1, newCodeForBlock1);

        var updatedBlocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updatedMd);
        Assert.Equal(3, updatedBlocks.Count);
        Assert.Contains("flowchart TD", updatedBlocks[0].Code);
        Assert.Contains("flowchart LR", updatedBlocks[1].Code);
        Assert.Contains("flowchart TD", updatedBlocks[2].Code);
    }

    [Fact]
    public void IndexMapping_NaiveStringReplaceBug_DemonstratesDuplicateBlockCorruption()
    {
        // Demonstration of naive string.Replace vs index-based AST replacement
        string currentMd = "# Doc\n\n```mermaid\nflowchart TD\n    A --> B\n```\n\n```mermaid\nflowchart TD\n    A --> B\n```";

        // Naive logic used in MainWindow.xaml.cs: currentMd.Replace(originalBlock, newBlock)
        var matches = System.Text.RegularExpressions.Regex.Matches(currentMd, @"```mermaid[\s\S]*?```");
        string originalBlock = matches[1].Value; // target index 1
        string newBlock = "```mermaid\nflowchart LR\n    A --> C\n```";

        string naiveResult = currentMd.Replace(originalBlock, newBlock);
        // Naive replace replaces BOTH block 0 and block 1 because they are identical text!
        var naiveBlocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(naiveResult);
        Assert.Contains("flowchart LR", naiveBlocks[0].Code); // Block 0 accidentally corrupted!

        // Correct AST Index-based replacement via MermaidMarkdownSyncService:
        string astResult = MermaidMarkdownSyncService.ReplaceMermaidBlock(currentMd, 1, "flowchart LR\n    A --> C");
        var astBlocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(astResult);
        Assert.Contains("flowchart TD", astBlocks[0].Code); // Block 0 preserved!
        Assert.Contains("flowchart LR", astBlocks[1].Code); // Block 1 updated!
    }

    [Fact]
    public void IndexMapping_EmptyAndWhitespaceDocument_ReturnsEmptyList()
    {
        Assert.Empty(MermaidMarkdownSyncService.ExtractMermaidBlocks(""));
        Assert.Empty(MermaidMarkdownSyncService.ExtractMermaidBlocks("   \n\t  \n"));
    }

    [Fact]
    public void IndexMapping_OutOfBoundsIndex_ThrowsArgumentOutOfRangeException()
    {
        string markdown = @"```mermaid
flowchart TD
    A --> B
```";

        var exNegative = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, -1, "flowchart LR"));
        Assert.Contains("out of range", exNegative.Message);

        var exTooHigh = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 5, "flowchart LR"));
        Assert.Contains("out of range", exTooHigh.Message);

        var exEmptyDoc = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MermaidMarkdownSyncService.ReplaceMermaidBlock("", 0, "flowchart LR"));
        Assert.Contains("out of range", exEmptyDoc.Message);
    }

    [Fact]
    public void IndexMapping_NestedBlocksInListsAndBlockquotes()
    {
        string markdown = @"> Quote before
> ```mermaid
> flowchart TD
>     Q1 --> Q2
> ```

- List item
  ```mermaid
  flowchart LR
      L1 --> L2
  ```";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        Assert.True(blocks.Count >= 2, $"Expected at least 2 blocks, found {blocks.Count}");
    }


    // ==========================================
    // FOCUS AREA 2: Code Block Replacement Accuracy
    // ==========================================

    [Fact]
    public void ReplacementAccuracy_ReplaceBlock0_PreservesSurroundingText()
    {
        string markdown = @"# Header Title

Intro paragraph with **bold** text.

```mermaid
flowchart TD
    A --> B
```

Middle paragraph.

```mermaid
sequenceDiagram
    X->>Y: Ping
```

Footer text.";

        string newCode0 = "flowchart LR\n    A[Start] --> B[Finish]";
        string result = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 0, newCode0);

        Assert.StartsWith("# Header Title", result);
        Assert.Contains("Intro paragraph with **bold** text.", result);
        Assert.Contains("flowchart LR", result);
        Assert.Contains("Middle paragraph.", result);
        Assert.Contains("sequenceDiagram", result);
        Assert.EndsWith("Footer text.", result);
    }

    [Fact]
    public void ReplacementAccuracy_ReplaceLastBlock_PreservesPrecedingBlocks()
    {
        string markdown = @"```mermaid
flowchart TD
    A --> B
```

```mermaid
flowchart TD
    C --> D
```";

        string newCode1 = "flowchart TD\n    C[New] --> D[New]";
        string result = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 1, newCode1);

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(result);
        Assert.Equal(2, blocks.Count);
        Assert.Contains("A --> B", blocks[0].Code);
        Assert.Contains("C[New] --> D[New]", blocks[1].Code);
    }

    [Fact]
    public void ReplacementAccuracy_CRLFLineEndings_HandledWithoutCorruptingNewlines()
    {
        string markdown = "# Header\r\n\r\n```mermaid\r\nflowchart TD\r\n    A --> B\r\n```\r\n\r\nFooter\r\n";
        string newCode = "flowchart LR\r\n    X --> Y";

        string result = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 0, newCode);

        Assert.Contains("# Header", result);
        Assert.Contains("Footer", result);
        Assert.Contains("flowchart LR", result);
    }


    // ==========================================
    // FOCUS AREA 3: Special Characters, Quotes, Unicode, JSON
    // ==========================================

    [Fact]
    public void SpecialChars_QuotesAndBackslashes_RoundtripThroughJsonAndDeserialization()
    {
        string complexMermaid = "flowchart TD\n    A[\"Node with \\\"quotes\\\" and \\\\ backslash\"] --> B[\"<script>alert('xss')</script> & extra|pipe\"]";

        var payload = new
        {
            type = "launch-mermaid-studio",
            index = 2,
            code = complexMermaid,
            gesture = "long-press-800ms",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        string jsonString = JsonSerializer.Serialize(payload);

        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;

        Assert.Equal("launch-mermaid-studio", root.GetProperty("type").GetString());
        Assert.Equal(2, root.GetProperty("index").GetInt32());
        string deserializedCode = root.GetProperty("code").GetString()!;

        Assert.Equal(complexMermaid, deserializedCode);
    }

    [Fact]
    public void Unicode_CJKAndEmojis_ExtractedAndReplacedAccurately()
    {
        string markdown = "# Unicode Document \uD83D\uDE80\n\n```mermaid\nflowchart TD\n    \u7B80\u4F53A[\"\uD83D\uDE80 \u542F\u52A8\u670D\u52A1\"] --> \u7B80\u4F53B[\"\uD83C\uDFAF \u76EE\u6807\u5B8C\u6710\"]\n    \u7B80\u4F53B --> \u7B80\u4F53C[\"Caf\u00E9 & Na\u00EFve\"]\n```\n\nTrailing text \uD83C\uDF89";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        Assert.Single(blocks);
        Assert.Contains("\u542F\u52A8\u670D\u52A1", blocks[0].Code);

        string newUnicodeCode = "flowchart LR\n    NodeX[\"\u26A1 \u5FEB\u901F\"] --> NodeY[\"\uD83D\uDD25 \u706B\u701B\"]";
        string updatedMd = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 0, newUnicodeCode);

        Assert.Contains("# Unicode Document \uD83D\uDE80", updatedMd);
        Assert.Contains("\u5FEB\u901F", updatedMd);
        Assert.Contains("\u706B\u701B", updatedMd);
        Assert.Contains("Trailing text \uD83C\uDF89", updatedMd);
    }

    [Fact]
    public void EmojiSurrogatePairsBeforeBlock_DoesNotDistortSpanOffsets()
    {
        string markdown = "# \uD83D\uDE80\uD83C\uDF89\uD83C\uDF1F Prefix with surrogate pairs \uD83C\uDF3A Unicode \uD83C\uDF08\n\n```mermaid\nflowchart TD\n    A --> B\n```\n\nFooter \uD83C\uDFAF";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        Assert.Single(blocks);

        string updatedMd = MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, 0, "flowchart LR\n    A1 --> B1");

        Assert.Contains("# \uD83D\uDE80\uD83C\uDF89\uD83C\uDF1F Prefix with surrogate pairs \uD83C\uDF3A Unicode \uD83C\uDF08", updatedMd);
        Assert.Contains("flowchart LR", updatedMd);
        Assert.Contains("Footer \uD83C\uDFAF", updatedMd);

        var newBlocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updatedMd);
        Assert.Single(newBlocks);
        Assert.Contains("A1 --> B1", newBlocks[0].Code);
    }
}
