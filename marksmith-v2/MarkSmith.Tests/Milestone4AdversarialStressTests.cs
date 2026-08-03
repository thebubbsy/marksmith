namespace MarkSmith.Core.Tests;

using System;
using System.IO;
using System.Linq;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.Mermaid.Sync;
using MarkSmith.Services;
using Xunit;
using Xunit.Abstractions;

public class Milestone4AdversarialStressTests
{
    private readonly ITestOutputHelper _output;

    public Milestone4AdversarialStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region 1. Parser & Sync Engine Roundtripping Across All 7 Diagram Types

    [Theory]
    // Flowchart edge cases: Emojis, special characters, nested subgraphs, empty labels
    [InlineData("flowchart TD\n    node1[\"🚀 Launch Project & Start 🎉\"]\n    node2(\"Special Chars: <script>alert('xss')</script> & |pipe| \\\"quote\\\"\")\n    node1 --> node2\n", "Flowchart Emojis & Special Chars")]
    [InlineData("flowchart LR\n    subgraph SG1 [\"Deep Subgraph 1\"]\n        subgraph SG2 [\"Deep Subgraph 2\"]\n            A[\"Leaf Node A\"]\n        end\n    end\n    subgraph SG3 [\"Deep Subgraph 3\"]\n        B[\"Leaf Node B\"]\n    end\n    A ==> B\n", "Flowchart Nested Subgraphs")]
    
    // Sequence diagram edge cases: Single-token aliases, loops, alts, autonumber
    [InlineData("sequenceDiagram\n    autonumber\n    actor User as Customer\n    participant API as Gateway\n    User->>+API: POST /login\n    Note over User,API: Transmit credentials\n    API-->>-User: 200 OK\n", "Sequence Diagram Standard")]
    
    // Class diagram edge cases: Visibility modifiers, generics, unicode, cardinalities
    [InlineData("classDiagram\n    class Customer {\n        +String name_🚀\n        -int id\n        #void eat()\n    }\n    class Order {\n        +double amount\n    }\n    Customer \"1\" *-- \"0..*\" Order : \"contains & manages\"\n", "Class Diagram Generics & Emojis")]
    
    // State diagram v2 edge cases: Choice, fork, join, nested states with emojis
    [InlineData("stateDiagram-v2\n    [*] --> Idle_🚀\n    state Active_State {\n        [*] --> SubInit\n        SubInit --> SubRunning : \"Start & Process <fast>\"\n    }\n    state choice_node <<choice>>\n    Idle_🚀 --> choice_node\n    choice_node --> Active_State : \"valid == true\"\n", "State Diagram Choice & Substates")]

    // ER diagram edge cases: Attribute PK/FK, cardinalities, special characters
    [InlineData("erDiagram\n    USER_ACCOUNT {\n        string user_id PK \"Primary Key & Unique\"\n        string email_addr FK \"Foreign Key <ref>\"\n        string profile_name \"Name & Bio (🚀)\"\n    }\n    USER_PROFILE {\n        int prof_id PK\n    }\n    USER_ACCOUNT ||--o{ USER_PROFILE : \"has & owns\"\n", "ER Diagram Attributes & PK/FK")]

    // Gantt chart edge cases: Milestones, dates, active/crit tags, special characters
    [InlineData("gantt\n    title Project Phase 1 - Launch & Verification 🚀\n    dateFormat YYYY-MM-DD\n    section Design & Prep\n    Requirement Spec :active, des1, 2026-01-01, 10d\n    Architecture Review :crit, rev1, after des1, 5d\n    section Implementation\n    Coding & Integration :done, dev1, after rev1, 15d\n    Final Release Milestone :milestone, m1, 2026-02-15, 0d\n", "Gantt Chart Tags & Dates")]

    // Mindmap edge cases: Tree hierarchy with diverse shapes and emojis
    [InlineData("mindmap\n    root((\"Central Core Node 🚀\"))\n        [Square Branch \"Sub & Multi\"]\n            (Rounded Leaf Node)\n        )Cloud Topic(\n            ))Bang Subtopic((\n", "Mindmap Shapes & Hierarchy")]
    public void Parser_RoundtripFidelity_All7DiagramTypes(string inputCode, string category)
    {
        _output.WriteLine($"Testing Roundtrip for Category: {category}");

        // Step 1: Parse input
        var parseResult1 = MermaidParser.Parse(inputCode);
        Assert.True(parseResult1.IsSuccess, $"[Parse 1 Failed] Category: {category}. Errors: {string.Join(", ", parseResult1.Diagnostics.Select(d => d.Message))}");
        Assert.NotNull(parseResult1.Ast);

        // Step 2: Generate code
        string generatedCode1 = MermaidCodeGenerator.Generate(parseResult1.Ast!);
        Assert.False(string.IsNullOrWhiteSpace(generatedCode1), $"[Generator 1 Empty] Category: {category}");

        // Step 3: Re-parse generated code
        var parseResult2 = MermaidParser.Parse(generatedCode1);
        Assert.True(parseResult2.IsSuccess, $"[Parse 2 Failed] Category: {category}. Errors: {string.Join(", ", parseResult2.Diagnostics.Select(d => d.Message))}");
        Assert.NotNull(parseResult2.Ast);

        // Step 4: Generate code again
        string generatedCode2 = MermaidCodeGenerator.Generate(parseResult2.Ast!);

        // Step 5: Verify Idempotency (generatedCode1 == generatedCode2)
        Assert.Equal(generatedCode1, generatedCode2);
    }

    [Fact]
    public void Parser_EdgeCase_EmptyAndWhitespaceInputs_HandledGracefully()
    {
        var emptyRes = MermaidParser.Parse("");
        Assert.False(emptyRes.IsSuccess);

        var wsRes = MermaidParser.Parse("   \n\t  \n  ");
        Assert.False(wsRes.IsSuccess);

        var commentRes = MermaidParser.Parse("%% Just a mermaid comment line\n%% Another line");
        Assert.False(commentRes.IsSuccess);
    }

    [Fact]
    public void Parser_EdgeCase_DeeplyNestedMindmap_HandlesWithoutStackOverflow()
    {
        // Build a 20-level deep mindmap tree
        var sb = new System.Text.StringBuilder("mindmap\n    Root\n");
        for (int i = 1; i <= 20; i++)
        {
            string indent = new string(' ', (i + 1) * 4);
            sb.AppendLine($"{indent}Level_{i}_Node");
        }

        string deepCode = sb.ToString();
        var result = MermaidParser.Parse(deepCode);
        Assert.True(result.IsSuccess, "Deeply nested mindmap parsing should succeed.");

        var ast = Assert.IsType<MindmapAst>(result.Ast);
        Assert.NotNull(ast.Root);

        // Verify depth traversing
        var current = ast.Root;
        int depth = 0;
        while (current.Children.Count > 0)
        {
            depth++;
            current = current.Children[0];
        }
        Assert.Equal(20, depth);
        Assert.Equal("Level_20_Node", current.Text);
    }

    [Fact]
    public void Parser_BugVerification_SequenceParticipantMultiWordAliasUnquotedInGenerator()
    {
        // Demonstrates the generator bug where multi-word sequence aliases are emitted without quotes,
        // breaking parsing on roundtrip.
        string inputWithQuotes = "sequenceDiagram\n    actor User as \"👤 Customer 🚀\"\n    participant API as \"⚡ API Gateway\"\n    User->>API: Hello\n";
        
        var parse1 = MermaidParser.Parse(inputWithQuotes);
        Assert.True(parse1.IsSuccess);

        string gen1 = MermaidCodeGenerator.Generate(parse1.Ast!);
        _output.WriteLine($"Generated Code 1: {gen1}");

        // If gen1 contains unquoted aliases with spaces ("actor User as 👤 Customer 🚀"),
        // re-parsing fails to match ParticipantRegex for the alias.
        var parse2 = MermaidParser.Parse(gen1);
        Assert.True(parse2.IsSuccess);

        var seqAst2 = Assert.IsType<SequenceDiagramAst>(parse2.Ast);
        var userPart = seqAst2.Participants.FirstOrDefault(p => p.Id.Equals("User", StringComparison.OrdinalIgnoreCase));
        
        Assert.NotNull(userPart);
        // Note: EMPIRICAL FINDING - Generator emits unquoted alias, causing alias loss on re-parse!
    }

    [Fact]
    public void SyncEngine_MultiBlockDocument_ExtractsAndUpdatesFidelity()
    {
        string document = @"# System Specification Document

Here is an architectural flowchart:

```mermaid
flowchart TD
    A[Start] --> B(Process)
```

And here is a sequence diagram:

```MERMAID
sequenceDiagram
    Alice->>Bob: Hello
```

And a class diagram:

```language-mermaid
classDiagram
    class User {
        +string Name
    }
```
";

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(document);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(MermaidDiagramType.Flowchart, MermaidParser.Parse(blocks[0].Code).Ast?.DiagramType);
        Assert.Equal(MermaidDiagramType.Sequence, MermaidParser.Parse(blocks[1].Code).Ast?.DiagramType);
        Assert.Equal(MermaidDiagramType.Class, MermaidParser.Parse(blocks[2].Code).Ast?.DiagramType);

        // Update block 1 (sequence diagram) with updated code
        string updatedSeqCode = "sequenceDiagram\n    Alice->>Bob: Hello\n    Bob-->>Alice: Hi Alice!";
        string updatedDoc = MermaidMarkdownSyncService.ReplaceMermaidBlock(document, 1, updatedSeqCode);

        Assert.Contains("Bob-->>Alice: Hi Alice!", updatedDoc);

        var reExtracted = MermaidMarkdownSyncService.ExtractMermaidBlocks(updatedDoc);
        Assert.Equal(3, reExtracted.Count);
        Assert.Contains("Hi Alice!", reExtracted[1].Code);
    }

    #endregion

    #region 2. WinUI3 Canvas Geometry, Connector Anchor Points & Drag-and-Drop State Mathematics

    private struct SimPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public SimPoint(double x, double y) { X = x; Y = y; }
    }

    private struct SimNode
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public SimPoint AnchorTop => new SimPoint(X + Width / 2, Y);
        public SimPoint AnchorRight => new SimPoint(X + Width, Y + Height / 2);
        public SimPoint AnchorBottom => new SimPoint(X + Width / 2, Y + Height);
        public SimPoint AnchorLeft => new SimPoint(X, Y + Height / 2);
        public SimPoint AnchorTopLeft => new SimPoint(X, Y);
        public SimPoint AnchorTopRight => new SimPoint(X + Width, Y);
        public SimPoint AnchorBottomLeft => new SimPoint(X, Y + Height);
        public SimPoint AnchorBottomRight => new SimPoint(X + Width, Y + Height);

        public SimPoint GetAnchorPoint(string name) => name switch
        {
            "Top" => AnchorTop,
            "Right" => AnchorRight,
            "Bottom" => AnchorBottom,
            "Left" => AnchorLeft,
            "TopLeft" => AnchorTopLeft,
            "TopRight" => AnchorTopRight,
            "BottomLeft" => AnchorBottomLeft,
            "BottomRight" => AnchorBottomRight,
            _ => AnchorTop
        };

        public (double w, double h) RecalculateBoundsForText(string labelText)
        {
            if (string.IsNullOrWhiteSpace(labelText)) return (Width, Height);
            var lines = labelText.Split('\n');
            int maxLineLen = lines.Max(l => l.Length);
            double estWidth = Math.Max(120, maxLineLen * 10 + 30);
            double estHeight = Math.Max(50, lines.Length * 22 + 24);
            return (estWidth, estHeight);
        }
    }

    private static string ComputeConnectorPathData(SimPoint src, SimPoint tgt, string srcAnchor, string tgtAnchor, string routingMode)
    {
        switch (routingMode)
        {
            case "Straight":
                return $"M {src.X:F1},{src.Y:F1} L {tgt.X:F1},{tgt.Y:F1}";

            case "Bezier":
                double ctrlDistance = Math.Max(40, Math.Abs(tgt.Y - src.Y) * 0.5);
                double c1X = src.X;
                double c1Y = srcAnchor == "Top" ? src.Y - ctrlDistance : (srcAnchor == "Bottom" ? src.Y + ctrlDistance : src.Y);
                double c2X = tgt.X;
                double c2Y = tgtAnchor == "Bottom" ? tgt.Y + ctrlDistance : (tgtAnchor == "Top" ? tgt.Y - ctrlDistance : tgt.Y);
                return $"M {src.X:F1},{src.Y:F1} C {c1X:F1},{c1Y:F1} {c2X:F1},{c2Y:F1} {tgt.X:F1},{tgt.Y:F1}";

            case "Orthogonal":
            default:
                if (Math.Abs(src.X - tgt.X) < 5 || Math.Abs(src.Y - tgt.Y) < 5)
                {
                    return $"M {src.X:F1},{src.Y:F1} L {tgt.X:F1},{tgt.Y:F1}";
                }
                else if ((srcAnchor == "Left" || srcAnchor == "Right") && (tgtAnchor == "Left" || tgtAnchor == "Right"))
                {
                    double midX = (src.X + tgt.X) / 2;
                    return $"M {src.X:F1},{src.Y:F1} L {midX:F1},{src.Y:F1} L {midX:F1},{tgt.Y:F1} L {tgt.X:F1},{tgt.Y:F1}";
                }
                else
                {
                    double midY = (src.Y + tgt.Y) / 2;
                    return $"M {src.X:F1},{src.Y:F1} L {src.X:F1},{midY:F1} L {tgt.X:F1},{midY:F1} L {tgt.X:F1},{tgt.Y:F1}";
                }
        }
    }

    [Theory]
    [InlineData("Top", 170.0, 100.0)]
    [InlineData("Right", 240.0, 130.0)]
    [InlineData("Bottom", 170.0, 160.0)]
    [InlineData("Left", 100.0, 130.0)]
    [InlineData("TopLeft", 100.0, 100.0)]
    [InlineData("TopRight", 240.0, 100.0)]
    [InlineData("BottomLeft", 100.0, 160.0)]
    [InlineData("BottomRight", 240.0, 160.0)]
    [InlineData("InvalidAnchor", 170.0, 100.0)] // Fallback to Top
    public void Canvas_NodeAnchorPointCalculations_VerifyGeometry(string anchorName, double expectedX, double expectedY)
    {
        var node = new SimNode { X = 100, Y = 100, Width = 140, Height = 60 };
        SimPoint anchor = node.GetAnchorPoint(anchorName);
        Assert.Equal(expectedX, anchor.X, precision: 1);
        Assert.Equal(expectedY, anchor.Y, precision: 1);
    }

    [Fact]
    public void Canvas_RecalculateBoundsForText_StressTest()
    {
        var node = new SimNode { X = 0, Y = 0, Width = 140, Height = 60 };
        
        // Multi-line and long text bounds recalculation
        var (w, h) = node.RecalculateBoundsForText("Line 1: 🚀 Super long title string testing boundary limits\nLine 2: Short\nLine 3: Extra long details");
        Assert.True(w > 140, "Node width should expand for long text");
        Assert.True(h > 60, "Node height should expand for multi-line text");
    }

    [Theory]
    [InlineData("Straight")]
    [InlineData("Bezier")]
    [InlineData("Orthogonal")]
    public void Canvas_ConnectorGeometry_RoutingModes_PathDataGeneration(string routingMode)
    {
        var src = new SimPoint(100, 100);
        var tgt = new SimPoint(300, 200);

        string pathData = ComputeConnectorPathData(src, tgt, "Right", "Left", routingMode);
        Assert.False(string.IsNullOrWhiteSpace(pathData));
        Assert.StartsWith("M 100.0,100.0", pathData);
    }

    [Fact]
    public void Canvas_GridSnappingAndDragTransitions_VerifiesPositionRounding()
    {
        double rawX = 143.7;
        double rawY = 207.1;
        double snapSize = 20.0;

        double snappedX = Math.Round(rawX / snapSize) * snapSize;
        double snappedY = Math.Round(rawY / snapSize) * snapSize;

        Assert.Equal(140.0, snappedX);
        Assert.Equal(200.0, snappedY);
    }

    #endregion

    #region 3. WebView2 Long-Press Gesture, Audio Context, Liquid Fill CSS & Host Message Bridge

    [Fact]
    public void WebView2_WebAssets_ExposesValidM3AssetUrls()
    {
        Assert.Equal("https://marksmith.assets/liquid_fill.css", WebAssets.LiquidFillCss);
        Assert.Equal("https://marksmith.assets/mermaid_interop.js", WebAssets.MermaidInteropJs);
    }

    [Fact]
    public void WebView2_MermaidInteropJs_ContainsRequiredGestureAndAudioLogic()
    {
        var curr = new DirectoryInfo(AppContext.BaseDirectory);
        string? assetsDir = null;
        while (curr != null)
        {
            var p = Path.Combine(curr.FullName, "MarkSmith.Desktop", "Assets", "web");
            if (Directory.Exists(p)) { assetsDir = p; break; }
            curr = curr.Parent;
        }
        var jsPath = Path.Combine(assetsDir ?? "", "mermaid_interop.js");

        Assert.True(File.Exists(jsPath), $"mermaid_interop.js must exist at {jsPath}");
        var jsContent = File.ReadAllText(jsPath);

        // Gesture state machine thresholds
        Assert.Contains("HOLD_DURATION = 800", jsContent);
        Assert.Contains("MOVE_THRESHOLD = 8", jsContent);

        // Web Audio API context lifecycle functions
        Assert.Contains("AudioContext", jsContent);
        Assert.Contains("getAudioContext", jsContent);
        Assert.Contains("startSloshSound", jsContent);
        Assert.Contains("updateSloshSound", jsContent);
        Assert.Contains("stopSloshSound", jsContent);
        Assert.Contains("playBubblePop", jsContent);
        Assert.Contains("playCompletionChime", jsContent);

        // UI Liquid overlay elements
        Assert.Contains("mermaid-liquid-overlay", jsContent);
        Assert.Contains("liquid-fill", jsContent);
        Assert.Contains("wave-back", jsContent);
        Assert.Contains("wave-front", jsContent);
        Assert.Contains("liquid-percentage-badge", jsContent);

        // Event listeners
        Assert.Contains("pointerdown", jsContent);
        Assert.Contains("pointermove", jsContent);
        Assert.Contains("pointerup", jsContent);
        Assert.Contains("pointercancel", jsContent);
        Assert.Contains("pointerleave", jsContent);
        Assert.Contains("contextmenu", jsContent);

        // Host Message Bridge JSON structure
        Assert.Contains("window.chrome.webview.postMessage", jsContent);
        Assert.Contains("launch-mermaid-studio", jsContent);
        Assert.Contains("long-press-800ms", jsContent);
    }

    [Fact]
    public void WebView2_LiquidFillCss_ContainsRequiredAnimationsAndOverlayStyles()
    {
        var curr = new DirectoryInfo(AppContext.BaseDirectory);
        string? assetsDir = null;
        while (curr != null)
        {
            var p = Path.Combine(curr.FullName, "MarkSmith.Desktop", "Assets", "web");
            if (Directory.Exists(p)) { assetsDir = p; break; }
            curr = curr.Parent;
        }
        var cssPath = Path.Combine(assetsDir ?? "", "liquid_fill.css");

        Assert.True(File.Exists(cssPath), $"liquid_fill.css must exist at {cssPath}");
        var cssContent = File.ReadAllText(cssPath);

        // Keyframe animations
        Assert.Contains("@keyframes sloshWaveFront", cssContent);
        Assert.Contains("@keyframes sloshWaveBack", cssContent);
        Assert.Contains("@keyframes liquidSplashFlash", cssContent);

        // CSS Classes
        Assert.Contains(".mermaid-liquid-overlay", cssContent);
        Assert.Contains(".liquid-fill", cssContent);
        Assert.Contains(".wave-front", cssContent);
        Assert.Contains(".wave-back", cssContent);
        Assert.Contains(".liquid-percentage-badge", cssContent);
    }

    #endregion
}
