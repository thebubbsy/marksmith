namespace MdToPdf.Core.Tests.Mermaid;

using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Parser;
using MdToPdf.Mermaid.Sync;
using Xunit;

public class MermaidMarkdownSyncStressTests
{
    private const string ComplexMultiDiagramDocument = @"# Architectural Specifications & System Overview

Welcome to the comprehensive system documentation. This document contains multiple architecture diagrams intermingled with specifications, code samples, tables, and notes.

## 1. System High-Level Overview

The following diagram illustrates the core components of the system:

```mermaid
flowchart TD
    Client[Web Client] --> Gateway[API Gateway]
    Gateway --> ServiceA[Auth Service]
    Gateway --> ServiceB[Data Processing Service]
    ServiceB --> DB[(Database)]
```

Here is a quick overview table of service endpoints:

| Service Name | Port | Protocol | Description |
| ------------ | ---- | -------- | ----------- |
| Gateway      | 8080 | HTTP/2   | Ingress routing |
| Auth         | 8081 | gRPC     | JWT token auth |
| Data         | 8082 | gRPC     | Async ETL job processing |

```csharp
// C# Code snippet for gateway initialization
public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine(""Starting Gateway..."");
    }
}
```

## 2. Authentication Protocol Flow

Below is the sequence diagram describing the OAuth2 token exchange protocol:

```MERMAID
sequenceDiagram
    autonumber
    Client->>Gateway: POST /api/v1/auth/login
    Gateway->>AuthService: Validate Credentials
    AuthService-->>Gateway: Return JWT Token & Refresh Token
    Gateway-->>Client: 200 OK (JWT)
```

> **Note on Security**:
> All JWT tokens must be signed with RSA-256 and have an expiration of no longer than 15 minutes.

## 3. Core Domain Model

The class diagram below defines the internal domain models and entity relationships:

```language-mermaid
classDiagram
    class User {
        +Guid Id
        +string Email
        +bool IsActive
        +Login()
    }
    class Order {
        +Guid OrderId
        +decimal TotalAmount
        +OrderStatus Status
    }
    User ""1"" --> ""*"" Order : places
```

Some bullet points summarizing business rules:
- Users cannot place orders if `IsActive` is false.
- Orders must contain at least one line item.

```json
{
  ""systemConfig"": {
    ""maxOrderItems"": 100,
    ""enableNotifications"": true
  }
}
```

## 4. Payment State Machine

State transitions for an incoming customer order:

```Mermaid
stateDiagram-v2
    [*] --> Pending
    Pending --> Authorized : Payment Approved
    Pending --> Failed : Payment Declined
    Authorized --> Captured : Settle
    Captured --> [*]
    Failed --> [*]
```

## 5. System Roadmap & Delivery Schedule

Project timeline represented as a Gantt chart:

```mermaid
gantt
    dateFormat YYYY-MM-DD
    title Q3 Software Delivery Roadmap
    section Core Infra
    API Gateway Setup :done, task1, 2026-07-01, 2026-07-10
    Auth Service Impl :active, task2, 2026-07-10, 2026-07-20
    section Features
    Order Engine :task3, 2026-07-20, 2026-08-05
```

---

*End of document specifications.*
";

    [Fact]
    public void ExtractMermaidBlocks_ComplexDocument_ExtractsAllDiagramsWithCorrectOrderAndCaseInsensitivity()
    {
        // Act
        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(ComplexMultiDiagramDocument);

        // Assert
        Assert.NotNull(blocks);
        Assert.Equal(5, blocks.Count);

        // Block 0: Flowchart
        Assert.Equal(0, blocks[0].BlockIndex);
        Assert.Contains("flowchart TD", blocks[0].Code);
        Assert.Contains("Client[Web Client] --> Gateway[API Gateway]", blocks[0].Code);

        // Block 1: Sequence Diagram (uppercase info tag: ```MERMAID)
        Assert.Equal(1, blocks[1].BlockIndex);
        Assert.Contains("sequenceDiagram", blocks[1].Code);
        Assert.Contains("Client->>Gateway: POST /api/v1/auth/login", blocks[1].Code);

        // Block 2: Class Diagram (prefixed info tag: ```language-mermaid)
        Assert.Equal(2, blocks[2].BlockIndex);
        Assert.Contains("classDiagram", blocks[2].Code);
        Assert.Contains("class User", blocks[2].Code);

        // Block 3: State Diagram (mixed case info tag: ```Mermaid)
        Assert.Equal(3, blocks[3].BlockIndex);
        Assert.Contains("stateDiagram-v2", blocks[3].Code);
        Assert.Contains("Pending --> Authorized", blocks[3].Code);

        // Block 4: Gantt Chart
        Assert.Equal(4, blocks[4].BlockIndex);
        Assert.Contains("gantt", blocks[4].Code);
        Assert.Contains("title Q3 Software Delivery Roadmap", blocks[4].Code);
    }

    [Fact]
    public void ExtractMermaidBlocks_IgnoresNonMermaidCodeFences()
    {
        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(ComplexMultiDiagramDocument);

        // Ensure non-mermaid code blocks (csharp, json) are NOT in extracted list
        foreach (var block in blocks)
        {
            Assert.DoesNotContain("Console.WriteLine", block.Code);
            Assert.DoesNotContain("maxOrderItems", block.Code);
        }
    }

    [Fact]
    public void ReplaceMermaidBlock_AtomicReplacement_PreservesSurroundingProseAndOtherBlocks()
    {
        string newSequenceCode = @"sequenceDiagram
    autonumber
    Client->>Gateway: POST /api/v2/auth/token
    Gateway->>OAuth2Provider: Exchange Auth Code
    OAuth2Provider-->>Gateway: Access Token Response
    Gateway-->>Client: 200 OK (v2 Access Token)";

        // Act - Replace block 1 (Sequence Diagram)
        string updatedDoc = MermaidMarkdownSyncService.ReplaceMermaidBlock(ComplexMultiDiagramDocument, 1, newSequenceCode);

        // Assert - Extracted blocks from updated document
        var newBlocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updatedDoc);
        Assert.Equal(5, newBlocks.Count);

        // Block 1 should be updated
        Assert.Contains("POST /api/v2/auth/token", newBlocks[1].Code);
        Assert.Contains("OAuth2Provider-->>Gateway", newBlocks[1].Code);

        // Block 0, 2, 3, 4 MUST be unchanged
        Assert.Contains("flowchart TD", newBlocks[0].Code);
        Assert.Contains("classDiagram", newBlocks[2].Code);
        Assert.Contains("stateDiagram-v2", newBlocks[3].Code);
        Assert.Contains("gantt", newBlocks[4].Code);

        // Surrounding prose & non-mermaid code must be preserved intact
        Assert.Contains("# Architectural Specifications & System Overview", updatedDoc);
        Assert.Contains("| Service Name | Port | Protocol | Description |", updatedDoc);
        Assert.Contains("Console.WriteLine(\"Starting Gateway...\");", updatedDoc);
        Assert.Contains("\"maxOrderItems\": 100", updatedDoc);
        Assert.Contains("*End of document specifications.*", updatedDoc);
    }

    [Fact]
    public void ReplaceMermaidBlock_SequentialMultiDiagramUpdates_AppliesAllChangesCleanly()
    {
        string currentDoc = ComplexMultiDiagramDocument;

        string newFlowchart = "flowchart LR\n    MicroServiceA --> MicroServiceB";
        string newGantt = "gantt\n    title Phase 2 Roadmap\n    section Dev\n    Task A :2026-08-01, 10d";

        // Step 1: Replace block 4 (Gantt) first
        currentDoc = MermaidMarkdownSyncService.ReplaceMermaidBlock(currentDoc, 4, newGantt);

        // Step 2: Replace block 0 (Flowchart) next
        currentDoc = MermaidMarkdownSyncService.ReplaceMermaidBlock(currentDoc, 0, newFlowchart);

        // Re-extract blocks
        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(currentDoc);
        Assert.Equal(5, blocks.Count);

        Assert.Contains("MicroServiceA --> MicroServiceB", blocks[0].Code);
        Assert.Contains("title Phase 2 Roadmap", blocks[4].Code);
        Assert.Contains("sequenceDiagram", blocks[1].Code);
        Assert.Contains("classDiagram", blocks[2].Code);
        Assert.Contains("stateDiagram-v2", blocks[3].Code);

        // Check surrounding prose
        Assert.Contains("## 1. System High-Level Overview", currentDoc);
        Assert.Contains("## 5. System Roadmap & Delivery Schedule", currentDoc);
    }

    [Fact]
    public void SyncAstToMarkdown_GeneratesAndSyncsAstInMultiDiagramDoc()
    {
        var newFlowchartAst = new FlowchartDiagramAst
        {
            Direction = FlowDirection.LR
        };
        newFlowchartAst.Nodes["N1"] = new FlowNode { Id = "N1", Text = "Frontend Single Page App" };
        newFlowchartAst.Nodes["N2"] = new FlowNode { Id = "N2", Text = "Backend Microservice" };
        newFlowchartAst.Edges.Add(new FlowEdge { FromId = "N1", ToId = "N2" });

        // Act
        string updatedDoc = MermaidMarkdownSyncService.SyncAstToMarkdown(ComplexMultiDiagramDocument, 0, newFlowchartAst);

        // Verify AST roundtrip on block 0
        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(updatedDoc);
        var parseResult = MermaidParser.Parse(blocks[0].Code);

        Assert.True(parseResult.IsSuccess);
        Assert.IsType<FlowchartDiagramAst>(parseResult.Ast);
        var parsedAst = (FlowchartDiagramAst)parseResult.Ast!;
        Assert.Equal(FlowDirection.LR, parsedAst.Direction);
        Assert.True(parsedAst.Nodes.ContainsKey("N1"));
        Assert.True(parsedAst.Nodes.ContainsKey("N2"));

        // Verify remaining document intact
        Assert.Contains("sequenceDiagram", blocks[1].Code);
        Assert.Contains("## 2. Authentication Protocol Flow", updatedDoc);
    }

    [Fact]
    public void ReplaceMermaidBlock_InvalidIndex_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MermaidMarkdownSyncService.ReplaceMermaidBlock(ComplexMultiDiagramDocument, -1, "flowchart TD\n A-->B"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MermaidMarkdownSyncService.ReplaceMermaidBlock(ComplexMultiDiagramDocument, 5, "flowchart TD\n A-->B"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MermaidMarkdownSyncService.ReplaceMermaidBlock("# Plain Document\nNo mermaid here.", 0, "flowchart TD\n A-->B"));
    }

    [Fact]
    public void ExtractMermaidBlocks_DiagramInsideListOrBlockquote_FindsNestedFences()
    {
        string markdownWithNested = @"
# Nested Diagram Test

1. Step One: Read specs.
2. Step Two: Observe flowchart below:
   ```mermaid
   flowchart TD
       Start --> Finish
   ```
3. Step Three: Execute.

> Quote Section:
> ```mermaid
> sequenceDiagram
>     User->>System: Action
> ```
";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdownWithNested);
        Assert.Equal(2, blocks.Count);
        Assert.Contains("Start --> Finish", blocks[0].Code);
        Assert.Contains("User->>System: Action", blocks[1].Code);
    }

    [Fact]
    public void ReplaceMermaidBlock_DiagramAtStartOrEnd_ReplacesCorrectlyWithoutCorruptingBoundaries()
    {
        string docStarts = @"```mermaid
flowchart TD
    A --> B
```
Prose content following diagram.";

        string updatedStarts = MermaidMarkdownSyncService.ReplaceMermaidBlock(docStarts, 0, "flowchart LR\n    X --> Y");
        Assert.StartsWith("```mermaid\nflowchart LR\n    X --> Y\n```", updatedStarts);
        Assert.Contains("Prose content following diagram.", updatedStarts);

        string docEnds = @"Prose content preceding diagram.
```mermaid
flowchart TD
    A --> B
```";

        string updatedEnds = MermaidMarkdownSyncService.ReplaceMermaidBlock(docEnds, 0, "flowchart LR\n    X --> Y");
        Assert.EndsWith("```mermaid\nflowchart LR\n    X --> Y\n```", updatedEnds);
        Assert.Contains("Prose content preceding diagram.", updatedEnds);
    }

    [Fact]
    public void ExtractMermaidBlocks_EmptyOrWhitespaceDocument_ReturnsEmptyList()
    {
        Assert.Empty(MermaidMarkdownSyncService.ExtractMermaidBlocks(""));
        Assert.Empty(MermaidMarkdownSyncService.ExtractMermaidBlocks("   \n\n\t  "));
    }
}
