using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Prompts;

public sealed class ThreeBlockCycleGemini38Prompt : IMcpPrompt
{
    public string Name => "three_block_cycle_gemini_3_8";

    public string Description =>
        "Workflow prompt enforcing the AI-Executable 3-Block Cycle + Execution Phase Protocol per GEMINI.md Section 7.";

    public IReadOnlyList<McpPromptArgument> Arguments => new List<McpPromptArgument>
    {
        new() { Name = "goal", Description = "The objective or feature to build across the cycle", Required = true },
        new() { Name = "current_block", Description = "Current cycle block (Block1, Block2, Block3, Block4Execution)", Required = false },
        new() { Name = "carried_ideas", Description = "Ideas carried forward from the previous cycle", Required = false }
    };

    public Task<McpPromptResult> GetAsync(JsonElement arguments, CancellationToken ct = default)
    {
        string goal = arguments.TryGetProperty("goal", out var gProp) ? gProp.GetString() ?? "Goal" : "Goal";
        string currentBlock = arguments.TryGetProperty("current_block", out var bProp) ? bProp.GetString() ?? "Block1" : "Block1";
        string carried = arguments.TryGetProperty("carried_ideas", out var cProp) ? cProp.GetString() ?? "" : "";

        var sb = new StringBuilder();
        sb.AppendLine($"# AI-Executable 3-Block Cycle Protocol: {goal}");
        sb.AppendLine();
        sb.AppendLine("## System Directive:");
        sb.AppendLine("Run a 3-block cycle. Each block generates 2 new ideas and refines the ideas from previous blocks. After Block 3, refine its 2 ideas again and then execute all 6 refined ideas into code. The execution phase is the 4th stage but not a generation block.");
        sb.AppendLine();
        sb.AppendLine("## 4-Stage Cadence & Rules:");
        sb.AppendLine("1. **Block 1 (0–15 min)**:");
        sb.AppendLine("   - Generate exactly 2 new ideas (Idea 1, Idea 2).");
        sb.AppendLine("   - Store them for refinement in Block 2.");
        sb.AppendLine("2. **Block 2 (15–30 min)**:");
        sb.AppendLine("   - Refine Block 1's 2 ideas (Pass 1).");
        sb.AppendLine("   - Generate exactly 2 new ideas (Idea 3, Idea 4).");
        sb.AppendLine("   - Store new ideas for refinement in Block 3.");
        sb.AppendLine("3. **Block 3 (30–45 min)**:");
        sb.AppendLine("   - Refine Block 2's 2 ideas (Pass 1).");
        sb.AppendLine("   - Refine Block 1's 2 ideas (Pass 2).");
        sb.AppendLine("   - Generate exactly 2 new ideas (Idea 5, Idea 6).");
        sb.AppendLine("   - Store new ideas for refinement in Execution Phase.");
        sb.AppendLine("4. **⚡ Block 4 — Execution Phase (45–60 min)**:");
        sb.AppendLine("   - *Non-generation block*: Final refinement + production execution.");
        sb.AppendLine("   - Refine Block 3's 2 ideas (Pass 1).");
        sb.AppendLine("   - Collect all 6 fully refined ideas:");
        sb.AppendLine("     * Block 1 -> 2 ideas (fully refined, 2 passes)");
        sb.AppendLine("     * Block 2 -> 2 ideas (fully refined, 1 pass)");
        sb.AppendLine("     * Block 3 -> 2 ideas (just refined, 1 pass)");
        sb.AppendLine("   - Execute all 6 refined ideas into code: 100% full implementation, no placeholders, production-ready output, verified with test suite.");
        sb.AppendLine("   - Carry Block 3's 2 ideas forward into the next cycle as the first refinement targets.");
        sb.AppendLine();
        sb.AppendLine($"Current Stage: **{currentBlock}**");
        if (!string.IsNullOrEmpty(carried))
        {
            sb.AppendLine($"Carried-forward ideas from previous cycle: {carried}");
        }
        sb.AppendLine();
        sb.AppendLine("Proceed with the current block's duties using `manage_3block_cycle` MCP tool.");

        return Task.FromResult(McpPromptResult.SingleMessage(sb.ToString(), "user", $"3-Block Cycle instructions for '{goal}'"));
    }
}
