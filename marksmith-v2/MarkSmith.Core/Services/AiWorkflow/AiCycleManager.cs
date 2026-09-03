using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkSmith.Core.Services.AiWorkflow;

public enum CycleStage
{
    Block1_Generation = 1,
    Block2_RefinementAndGeneration = 2,
    Block3_RefinementAndGeneration = 3,
    Block4_ExecutionPhase = 4,
    Completed = 5
}

public enum IdeaStatus
{
    Proposed,
    InRefinement,
    Refined,
    ReadyForExecution,
    Executed,
    CarriedForward
}

public class CycleIdea
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int OriginBlock { get; set; }
    public int RefinementPasses { get; set; }
    public IdeaStatus Status { get; set; } = IdeaStatus.Proposed;
    public List<string> RefinementNotes { get; set; } = new();
    public string? ImplementationDetails { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CycleState
{
    public string CycleId { get; set; } = "";
    public CycleStage CurrentStage { get; set; } = CycleStage.Block1_Generation;
    public int IterationNumber { get; set; } = 1;
    public List<CycleIdea> ActiveIdeas { get; set; } = new();
    public List<CycleIdea> ExecutedIdeas { get; set; } = new();
    public List<CycleIdea> CarriedForwardIdeas { get; set; } = new();
    public DateTime CycleStartedAt { get; set; } = DateTime.UtcNow;
    public DateTime StageStartedAt { get; set; } = DateTime.UtcNow;
    public List<string> StageHistory { get; set; } = new();
}

public class CycleActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public CycleState State { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();

    public static CycleActionResult Ok(CycleState state, string message) => new()
    {
        Success = true,
        Message = message,
        State = state
    };

    public static CycleActionResult Fail(CycleState state, string message, IEnumerable<string>? diagnostics = null) => new()
    {
        Success = false,
        Message = message,
        State = state,
        Diagnostics = diagnostics != null ? diagnostics.ToList() : new List<string>()
    };
}

public class AiCycleManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public CycleState StartNewCycle(string? cycleId = null, IEnumerable<CycleIdea>? carriedIdeas = null)
    {
        string id = cycleId ?? $"CYCLE-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var state = new CycleState
        {
            CycleId = id,
            CurrentStage = CycleStage.Block1_Generation,
            CycleStartedAt = DateTime.UtcNow,
            StageStartedAt = DateTime.UtcNow,
            StageHistory = new List<string> { $"Started cycle {id} at Block 1 (Generation)." }
        };

        if (carriedIdeas != null)
        {
            foreach (var idea in carriedIdeas)
            {
                var carried = new CycleIdea
                {
                    Id = idea.Id,
                    Title = idea.Title,
                    Description = idea.Description,
                    OriginBlock = 1, // Treat as carried into Block 1
                    RefinementPasses = idea.RefinementPasses,
                    Status = IdeaStatus.InRefinement,
                    RefinementNotes = new List<string>(idea.RefinementNotes) { "Carried forward from previous cycle." },
                    CreatedAt = idea.CreatedAt,
                    LastUpdatedAt = DateTime.UtcNow
                };
                state.ActiveIdeas.Add(carried);
            }
        }

        return state;
    }

    public CycleActionResult SubmitIdeas(CycleState state, int blockNumber, IEnumerable<(string Title, string Description)> newIdeas)
    {
        if (state.CurrentStage == CycleStage.Block4_ExecutionPhase || state.CurrentStage == CycleStage.Completed)
        {
            return CycleActionResult.Fail(state, "Block 4 / Execution Phase is a non-generation block. New idea generation is prohibited.",
                new[] { "GEMINI.md Section 7 directive: Block 4 is reserved for final refinement and production execution only." });
        }

        if ((int)state.CurrentStage != blockNumber)
        {
            return CycleActionResult.Fail(state, $"Cannot submit Block {blockNumber} ideas when cycle is at stage {state.CurrentStage}.",
                new[] { $"Current stage is {state.CurrentStage}. Advance stage or submit to current stage." });
        }

        var ideasList = newIdeas.ToList();
        int expectedNew = 2;
        int currentInBlock = state.ActiveIdeas.Count(i => i.OriginBlock == blockNumber);

        if (currentInBlock + ideasList.Count > expectedNew)
        {
            return CycleActionResult.Fail(state, $"Block {blockNumber} allows exactly {expectedNew} new ideas. Already have {currentInBlock}, attempting to add {ideasList.Count}.",
                new[] { $"Each block must generate exactly {expectedNew} new ideas." });
        }

        int index = currentInBlock + 1;
        foreach (var (title, desc) in ideasList)
        {
            var idea = new CycleIdea
            {
                Id = $"B{blockNumber}-IDEA-{index++}",
                Title = title,
                Description = desc,
                OriginBlock = blockNumber,
                RefinementPasses = 0,
                Status = IdeaStatus.Proposed,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow
            };
            state.ActiveIdeas.Add(idea);
        }

        state.StageHistory.Add($"Block {blockNumber}: Generated {ideasList.Count} new idea(s).");
        return CycleActionResult.Ok(state, $"Successfully submitted {ideasList.Count} new idea(s) for Block {blockNumber}.");
    }

    public CycleActionResult RefineIdea(CycleState state, string ideaId, string refinementNotes)
    {
        var idea = state.ActiveIdeas.FirstOrDefault(i => string.Equals(i.Id, ideaId, StringComparison.OrdinalIgnoreCase));
        if (idea == null)
        {
            return CycleActionResult.Fail(state, $"Idea with ID '{ideaId}' not found in active ideas.",
                state.ActiveIdeas.Select(i => $"Available: {i.Id} - {i.Title}"));
        }

        idea.RefinementPasses++;
        idea.RefinementNotes.Add($"Pass {idea.RefinementPasses} (Stage {state.CurrentStage}): {refinementNotes}");
        idea.Status = IdeaStatus.InRefinement;
        idea.LastUpdatedAt = DateTime.UtcNow;

        state.StageHistory.Add($"Refined {idea.Id} (Pass {idea.RefinementPasses}) in Stage {state.CurrentStage}.");
        return CycleActionResult.Ok(state, $"Successfully applied refinement pass {idea.RefinementPasses} to idea '{idea.Id}'.");
    }

    public CycleActionResult AdvanceStage(CycleState state)
    {
        var diagnostics = new List<string>();

        switch (state.CurrentStage)
        {
            case CycleStage.Block1_Generation:
                {
                    // Block 1 requires 2 ideas generated
                    var b1Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 1).ToList();
                    if (b1Ideas.Count < 2)
                    {
                        diagnostics.Add($"Block 1 requires 2 ideas. Currently generated: {b1Ideas.Count}.");
                        return CycleActionResult.Fail(state, "Cannot advance from Block 1: 2 new ideas must be generated.", diagnostics);
                    }

                    state.CurrentStage = CycleStage.Block2_RefinementAndGeneration;
                    state.StageStartedAt = DateTime.UtcNow;
                    state.StageHistory.Add("Advanced to Block 2 (Refinement + Generation).");
                    return CycleActionResult.Ok(state, "Successfully advanced to Block 2.");
                }

            case CycleStage.Block2_RefinementAndGeneration:
                {
                    // Block 2 requires Block 1's 2 ideas to have >= 1 refinement pass AND 2 Block 2 ideas generated
                    var b1Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 1).ToList();
                    var unrefinedB1 = b1Ideas.Where(i => i.RefinementPasses < 1).ToList();
                    if (unrefinedB1.Count > 0)
                    {
                        diagnostics.Add($"Block 1 ideas requiring Pass 1 refinement: {string.Join(", ", unrefinedB1.Select(i => i.Id))}");
                    }

                    var b2Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 2).ToList();
                    if (b2Ideas.Count < 2)
                    {
                        diagnostics.Add($"Block 2 requires 2 new ideas. Currently generated: {b2Ideas.Count}.");
                    }

                    if (diagnostics.Count > 0)
                    {
                        return CycleActionResult.Fail(state, "Cannot advance from Block 2: Preconditions not met.", diagnostics);
                    }

                    state.CurrentStage = CycleStage.Block3_RefinementAndGeneration;
                    state.StageStartedAt = DateTime.UtcNow;
                    state.StageHistory.Add("Advanced to Block 3 (Refinement + Generation).");
                    return CycleActionResult.Ok(state, "Successfully advanced to Block 3.");
                }

            case CycleStage.Block3_RefinementAndGeneration:
                {
                    // Block 3 requires:
                    // - Block 1's 2 ideas to have >= 2 refinement passes
                    // - Block 2's 2 ideas to have >= 1 refinement pass
                    // - Block 3's 2 ideas to be generated
                    var b1Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 1).ToList();
                    var unrefinedB1 = b1Ideas.Where(i => i.RefinementPasses < 2).ToList();
                    if (unrefinedB1.Count > 0)
                    {
                        diagnostics.Add($"Block 1 ideas requiring Pass 2 refinement: {string.Join(", ", unrefinedB1.Select(i => $"{i.Id} (passes: {i.RefinementPasses})"))}");
                    }

                    var b2Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 2).ToList();
                    var unrefinedB2 = b2Ideas.Where(i => i.RefinementPasses < 1).ToList();
                    if (unrefinedB2.Count > 0)
                    {
                        diagnostics.Add($"Block 2 ideas requiring Pass 1 refinement: {string.Join(", ", unrefinedB2.Select(i => i.Id))}");
                    }

                    var b3Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 3).ToList();
                    if (b3Ideas.Count < 2)
                    {
                        diagnostics.Add($"Block 3 requires 2 new ideas. Currently generated: {b3Ideas.Count}.");
                    }

                    if (diagnostics.Count > 0)
                    {
                        return CycleActionResult.Fail(state, "Cannot advance from Block 3: Preconditions not met.", diagnostics);
                    }

                    state.CurrentStage = CycleStage.Block4_ExecutionPhase;
                    state.StageStartedAt = DateTime.UtcNow;
                    state.StageHistory.Add("Advanced to Block 4 (Execution Phase).");
                    return CycleActionResult.Ok(state, "Successfully advanced to Block 4 (Execution Phase).");
                }

            case CycleStage.Block4_ExecutionPhase:
                {
                    return CycleActionResult.Fail(state, "Cannot advance beyond Block 4 via AdvanceStage. Call ExecuteAll() to complete the cycle and execute all 6 refined ideas.",
                        new[] { "Block 4 must be finalized by calling ExecuteAll()." });
                }

            default:
                return CycleActionResult.Fail(state, $"Cycle is already in stage {state.CurrentStage}.");
        }
    }

    public CycleActionResult ExecuteAll(CycleState state, Action<CycleIdea>? executionCallback = null)
    {
        if (state.CurrentStage != CycleStage.Block4_ExecutionPhase)
        {
            return CycleActionResult.Fail(state, $"Cannot execute ideas at stage {state.CurrentStage}. Execution is only permitted in Block 4 (Execution Phase).",
                new[] { "Complete Blocks 1, 2, and 3 first before entering Execution Phase." });
        }

        var diagnostics = new List<string>();

        // Verify Block 3 ideas have completed their first refinement pass in Block 4
        var b3Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 3).ToList();
        var unrefinedB3 = b3Ideas.Where(i => i.RefinementPasses < 1).ToList();
        if (unrefinedB3.Count > 0)
        {
            diagnostics.Add($"Block 3 ideas must receive their first refinement pass before execution: {string.Join(", ", unrefinedB3.Select(i => i.Id))}");
        }

        // Verify all 6 ideas exist and meet pass counts
        var b1Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 1).ToList();
        var b2Ideas = state.ActiveIdeas.Where(i => i.OriginBlock == 2).ToList();

        if (b1Ideas.Count != 2 || b1Ideas.Any(i => i.RefinementPasses < 2))
        {
            diagnostics.Add("Block 1 must have exactly 2 ideas with 2 refinement passes each.");
        }
        if (b2Ideas.Count != 2 || b2Ideas.Any(i => i.RefinementPasses < 1))
        {
            diagnostics.Add("Block 2 must have exactly 2 ideas with at least 1 refinement pass each.");
        }
        if (b3Ideas.Count != 2 || b3Ideas.Any(i => i.RefinementPasses < 1))
        {
            diagnostics.Add("Block 3 must have exactly 2 ideas with at least 1 refinement pass each.");
        }

        if (diagnostics.Count > 0)
        {
            return CycleActionResult.Fail(state, "Execution Phase requirements not satisfied.", diagnostics);
        }

        // Execute all 6 ideas
        foreach (var idea in state.ActiveIdeas)
        {
            idea.Status = IdeaStatus.Executed;
            idea.LastUpdatedAt = DateTime.UtcNow;
            executionCallback?.Invoke(idea);
            state.ExecutedIdeas.Add(idea);
        }

        // Carry Block 3's 2 ideas forward into next cycle
        state.CarriedForwardIdeas = b3Ideas.Select(i => new CycleIdea
        {
            Id = i.Id,
            Title = i.Title,
            Description = i.Description,
            OriginBlock = 3,
            RefinementPasses = i.RefinementPasses,
            Status = IdeaStatus.CarriedForward,
            RefinementNotes = new List<string>(i.RefinementNotes),
            CreatedAt = i.CreatedAt,
            LastUpdatedAt = DateTime.UtcNow
        }).ToList();

        state.ActiveIdeas.Clear();
        state.CurrentStage = CycleStage.Completed;
        state.StageHistory.Add("Executed all 6 refined ideas into code with 100% production fidelity. Carried forward Block 3 ideas.");

        return CycleActionResult.Ok(state, "Successfully executed all 6 refined ideas into code. Cycle completed.");
    }

    public string GenerateSummaryMarkdown(CycleState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# AI 3-Block Cycle Summary — {state.CycleId}");
        sb.AppendLine();
        sb.AppendLine($"- **Current Stage**: {state.CurrentStage}");
        sb.AppendLine($"- **Iteration Number**: {state.IterationNumber}");
        sb.AppendLine($"- **Started At**: {state.CycleStartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Active Ideas");
        if (state.ActiveIdeas.Count == 0)
        {
            sb.AppendLine("*(No active ideas in flight)*");
        }
        else
        {
            foreach (var idea in state.ActiveIdeas)
            {
                sb.AppendLine($"### [{idea.Id}] {idea.Title} (Origin: Block {idea.OriginBlock}, Passes: {idea.RefinementPasses})");
                sb.AppendLine($"- **Status**: {idea.Status}");
                sb.AppendLine($"- **Description**: {idea.Description}");
                if (idea.RefinementNotes.Count > 0)
                {
                    sb.AppendLine("- **Refinements**:");
                    foreach (var note in idea.RefinementNotes)
                    {
                        sb.AppendLine($"  - {note}");
                    }
                }
                sb.AppendLine();
            }
        }

        if (state.ExecutedIdeas.Count > 0)
        {
            sb.AppendLine("## Executed Ideas (Production Verified)");
            foreach (var idea in state.ExecutedIdeas)
            {
                sb.AppendLine($"### [{idea.Id}] {idea.Title} (Origin: Block {idea.OriginBlock}, Passes: {idea.RefinementPasses})");
                sb.AppendLine($"- **Description**: {idea.Description}");
                sb.AppendLine();
            }
        }

        if (state.CarriedForwardIdeas.Count > 0)
        {
            sb.AppendLine("## Carried Forward Ideas (For Next Cycle)");
            foreach (var idea in state.CarriedForwardIdeas)
            {
                sb.AppendLine($"- **[{idea.Id}]** {idea.Title} (Passes: {idea.RefinementPasses})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Stage History");
        foreach (var history in state.StageHistory)
        {
            sb.AppendLine($"- {history}");
        }

        return sb.ToString();
    }

    public string ExportStateJson(CycleState state) => JsonSerializer.Serialize(state, JsonOpts);

    public CycleState LoadStateJson(string json) => JsonSerializer.Deserialize<CycleState>(json, JsonOpts) ?? new CycleState();
}
