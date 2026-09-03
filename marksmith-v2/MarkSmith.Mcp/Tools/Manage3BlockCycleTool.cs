using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Services.AiWorkflow;

namespace MarkSmith.Mcp.Tools;

public sealed class Manage3BlockCycleTool : IMcpTool
{
    private readonly AiCycleManager _cycleManager = new();

    public string Name => "manage_3block_cycle";

    public string Description =>
        "Manages the AI-Executable 3-Block Cycle state machine per GEMINI.md Section 7 (Block 1 -> Block 2 -> Block 3 -> Block 4 Execution Phase, tracking refinement passes and carry-forward).";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                description = "Cycle action to perform.",
                @enum = new[]
                {
                    "start",
                    "submit_ideas",
                    "refine_idea",
                    "advance_stage",
                    "execute_all",
                    "get_summary",
                    "export_state"
                }
            },
            cycle_id = new
            {
                type = "string",
                description = "Optional custom identifier for the cycle."
            },
            state_json = new
            {
                type = "string",
                description = "Serialized cycle state JSON to resume/advance an existing cycle."
            },
            block_number = new
            {
                type = "integer",
                description = "Block number (1, 2, or 3) when submitting new ideas."
            },
            ideas = new
            {
                type = "array",
                description = "Array of 2 new ideas to submit (with 'title' and 'description').",
                items = new
                {
                    type = "object",
                    required = new[] { "title", "description" },
                    properties = new
                    {
                        title = new { type = "string" },
                        description = new { type = "string" }
                    }
                }
            },
            idea_id = new
            {
                type = "string",
                description = "Idea ID (e.g. 'B1-IDEA-1') to refine."
            },
            refinement_notes = new
            {
                type = "string",
                description = "Notes and design details added during this refinement pass."
            }
        },
        required = new[] { "action" }
    };

    public Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            string action = arguments.TryGetProperty("action", out var actProp) ? actProp.GetString()?.ToLowerInvariant() ?? "" : "";
            string stateJson = arguments.TryGetProperty("state_json", out var sjProp) ? sjProp.GetString() ?? "" : "";

            CycleState state;
            if (!string.IsNullOrEmpty(stateJson))
            {
                state = _cycleManager.LoadStateJson(stateJson);
            }
            else
            {
                string? cycleId = arguments.TryGetProperty("cycle_id", out var cIdProp) ? cIdProp.GetString() : null;
                state = _cycleManager.StartNewCycle(cycleId);
            }

            switch (action)
            {
                case "start":
                    {
                        return Task.FromResult(McpToolResult.SuccessJson(new
                        {
                            success = true,
                            message = $"Cycle {state.CycleId} initialized at {state.CurrentStage}.",
                            state,
                            stateJson = _cycleManager.ExportStateJson(state)
                        }));
                    }

                case "submit_ideas":
                    {
                        int blockNum = arguments.TryGetProperty("block_number", out var bnProp) ? bnProp.GetInt32() : (int)state.CurrentStage;
                        var newIdeas = new List<(string Title, string Description)>();
                        if (arguments.TryGetProperty("ideas", out var ideasProp) && ideasProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in ideasProp.EnumerateArray())
                            {
                                string t = elem.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                                string d = elem.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "";
                                newIdeas.Add((t, d));
                            }
                        }

                        var res = _cycleManager.SubmitIdeas(state, blockNum, newIdeas);
                        return Task.FromResult(McpToolResult.SuccessJson(new
                        {
                            success = res.Success,
                            message = res.Message,
                            diagnostics = res.Diagnostics,
                            state = res.State,
                            stateJson = _cycleManager.ExportStateJson(res.State)
                        }));
                    }

                case "refine_idea":
                    {
                        string ideaId = arguments.TryGetProperty("idea_id", out var idProp) ? idProp.GetString() ?? "" : "";
                        string notes = arguments.TryGetProperty("refinement_notes", out var rnProp) ? rnProp.GetString() ?? "" : "";

                        var res = _cycleManager.RefineIdea(state, ideaId, notes);
                        return Task.FromResult(McpToolResult.SuccessJson(new
                        {
                            success = res.Success,
                            message = res.Message,
                            diagnostics = res.Diagnostics,
                            state = res.State,
                            stateJson = _cycleManager.ExportStateJson(res.State)
                        }));
                    }

                case "advance_stage":
                    {
                        var res = _cycleManager.AdvanceStage(state);
                        return Task.FromResult(McpToolResult.SuccessJson(new
                        {
                            success = res.Success,
                            message = res.Message,
                            diagnostics = res.Diagnostics,
                            state = res.State,
                            stateJson = _cycleManager.ExportStateJson(res.State)
                        }));
                    }

                case "execute_all":
                    {
                        var res = _cycleManager.ExecuteAll(state);
                        return Task.FromResult(McpToolResult.SuccessJson(new
                        {
                            success = res.Success,
                            message = res.Message,
                            diagnostics = res.Diagnostics,
                            state = res.State,
                            carriedForwardCount = res.State.CarriedForwardIdeas.Count,
                            executedCount = res.State.ExecutedIdeas.Count,
                            stateJson = _cycleManager.ExportStateJson(res.State)
                        }));
                    }

                case "get_summary":
                    {
                        string summary = _cycleManager.GenerateSummaryMarkdown(state);
                        return Task.FromResult(McpToolResult.Success(summary));
                    }

                case "export_state":
                    {
                        return Task.FromResult(McpToolResult.Success(_cycleManager.ExportStateJson(state)));
                    }

                default:
                    return Task.FromResult(McpToolResult.Error($"Unknown action: '{action}'"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpToolResult.Error($"manage_3block_cycle error: {ex.Message}"));
        }
    }
}
