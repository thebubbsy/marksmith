using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Core.Services.AiWorkflow;
using MarkSmith.Mcp.Server;
using MarkSmith.Mcp.Tools;
using Xunit;

namespace MarkSmith.Tests;

public class Ai3BlockCycleWorkflowTests
{
    private readonly AiCycleManager _cycleManager = new();

    [Fact]
    public void AiCycleManager_Full4StageLifecycleRoundTrip()
    {
        // =====================================================================
        // Block 1 (0–15 min): Generate 2 new ideas
        // =====================================================================
        var state = _cycleManager.StartNewCycle("CYCLE-TEST-001");
        Assert.Equal(CycleStage.Block1_Generation, state.CurrentStage);
        Assert.Empty(state.ActiveIdeas);

        // Attempt to advance without ideas -> should fail
        var failAdvanceB1 = _cycleManager.AdvanceStage(state);
        Assert.False(failAdvanceB1.Success);

        // Submit 2 ideas for Block 1
        var submitB1 = _cycleManager.SubmitIdeas(state, 1, new[]
        {
            ("Streaming SAX Architecture", "O(1) memory streaming export pipeline."),
            ("SmartArt Native Rendering", "Direct OOXML dgm layout generation.")
        });
        Assert.True(submitB1.Success);
        Assert.Equal(2, state.ActiveIdeas.Count);

        // Advance to Block 2
        var advB1 = _cycleManager.AdvanceStage(state);
        Assert.True(advB1.Success);
        Assert.Equal(CycleStage.Block2_RefinementAndGeneration, state.CurrentStage);

        // =====================================================================
        // Block 2 (15–30 min): Refine Block 1's ideas (Pass 1) & Generate 2 new ideas
        // =====================================================================
        // Attempt to advance before refining Block 1 -> should fail
        var failAdvanceB2 = _cycleManager.AdvanceStage(state);
        Assert.False(failAdvanceB2.Success);

        // Refine Block 1 ideas (Pass 1)
        var refB1_1 = _cycleManager.RefineIdea(state, "B1-IDEA-1", "Pass 1: Added System.Threading.Channels.");
        var refB1_2 = _cycleManager.RefineIdea(state, "B1-IDEA-2", "Pass 1: Added Glox converter mapping.");
        Assert.True(refB1_1.Success);
        Assert.True(refB1_2.Success);

        // Generate 2 new ideas for Block 2
        var submitB2 = _cycleManager.SubmitIdeas(state, 2, new[]
        {
            ("Multi-Column Continuous Sections", "Support :::columns with continuous section breaks."),
            ("Collapsible Headings", "Emit <w15:collapsed> on headings.")
        });
        Assert.True(submitB2.Success);
        Assert.Equal(4, state.ActiveIdeas.Count);

        // Advance to Block 3
        var advB2 = _cycleManager.AdvanceStage(state);
        Assert.True(advB2.Success);
        Assert.Equal(CycleStage.Block3_RefinementAndGeneration, state.CurrentStage);

        // =====================================================================
        // Block 3 (30–45 min): Refine Block 2 (Pass 1), Refine Block 1 (Pass 2), Generate 2 new ideas
        // =====================================================================
        // Refine Block 2 ideas (Pass 1)
        var refB2_1 = _cycleManager.RefineIdea(state, "B2-IDEA-1", "Pass 1: Implemented LiftColumnsBlocks in HTML preview.");
        var refB2_2 = _cycleManager.RefineIdea(state, "B2-IDEA-2", "Pass 1: Added outline level 8 to <details>.");
        Assert.True(refB2_1.Success);
        Assert.True(refB2_2.Success);

        // Refine Block 1 ideas (Pass 2)
        var refB1_1_p2 = _cycleManager.RefineIdea(state, "B1-IDEA-1", "Pass 2: Bound memory buffer pool via ArrayPool<byte>.");
        var refB1_2_p2 = _cycleManager.RefineIdea(state, "B1-IDEA-2", "Pass 2: Hardened relationship ID allocator.");
        Assert.True(refB1_1_p2.Success);
        Assert.True(refB1_2_p2.Success);

        // Generate 2 new ideas for Block 3
        var submitB3 = _cycleManager.SubmitIdeas(state, 3, new[]
        {
            ("CriticMarkup AST Ingestion", "Support inline redline CriticMarkup in docx import."),
            ("Nested HTML Table Grid Parser", "Translate colspan and rowspan to OpenXML gridSpan and vMerge.")
        });
        Assert.True(submitB3.Success);
        Assert.Equal(6, state.ActiveIdeas.Count);

        // Advance to Block 4 (Execution Phase)
        var advB3 = _cycleManager.AdvanceStage(state);
        Assert.True(advB3.Success);
        Assert.Equal(CycleStage.Block4_ExecutionPhase, state.CurrentStage);

        // =====================================================================
        // ⚡ Block 4 — Execution Phase (45–60 min): Refine Block 3 (Pass 1) & Execute All 6
        // =====================================================================
        // Attempting to generate ideas in Block 4 must fail
        var failGenB4 = _cycleManager.SubmitIdeas(state, 4, new[] { ("Disallowed Idea", "Should fail") });
        Assert.False(failGenB4.Success);

        // Refine Block 3 ideas (Pass 1)
        var refB3_1 = _cycleManager.RefineIdea(state, "B3-IDEA-1", "Pass 1: Added reverse-import regex pattern.");
        var refB3_2 = _cycleManager.RefineIdea(state, "B3-IDEA-2", "Pass 1: Added recursive cell matrix solver.");
        Assert.True(refB3_1.Success);
        Assert.True(refB3_2.Success);

        // Execute all 6 ideas into code
        int executedCount = 0;
        var execResult = _cycleManager.ExecuteAll(state, idea =>
        {
            executedCount++;
            idea.ImplementationDetails = "100% production code verified.";
        });

        Assert.True(execResult.Success);
        Assert.Equal(6, executedCount);
        Assert.Equal(6, state.ExecutedIdeas.Count);
        Assert.Equal(2, state.CarriedForwardIdeas.Count); // Block 3's 2 ideas carried forward
        Assert.Equal(CycleStage.Completed, state.CurrentStage);

        // Verify summary export
        string summary = _cycleManager.GenerateSummaryMarkdown(state);
        Assert.Contains("AI 3-Block Cycle Summary", summary);
        Assert.Contains("Executed Ideas (Production Verified)", summary);
        Assert.Contains("Carried Forward Ideas", summary);
    }

    [Fact]
    public async Task Manage3BlockCycleTool_McpEndpointExecution()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();

        // 1. Start cycle
        string startReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 401,
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new
                {
                    action = "start",
                    cycle_id = "MCP-CYCLE-TEST"
                }
            }
        });

        string? startResp = await dispatcher.DispatchAsync(startReq);
        Assert.NotNull(startResp);
        using var startDoc = JsonDocument.Parse(startResp);
        var content = startDoc.RootElement.GetProperty("result").GetProperty("content");
        string stateJson = content[0].GetProperty("text").GetString()!;

        // 2. Submit ideas
        string submitReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 402,
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new
                {
                    action = "submit_ideas",
                    state_json = stateJson,
                    block_number = 1,
                    ideas = new[]
                    {
                        new { title = "Idea Alpha", description = "Alpha description" },
                        new { title = "Idea Beta", description = "Beta description" }
                    }
                }
            }
        });

        string? submitResp = await dispatcher.DispatchAsync(submitReq);
        Assert.NotNull(submitResp);
        Assert.Contains("Idea Alpha", submitResp);
    }
}
