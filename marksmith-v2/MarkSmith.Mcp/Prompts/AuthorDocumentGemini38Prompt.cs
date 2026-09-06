using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Prompts;

public sealed class AuthorDocumentGemini38Prompt : IMcpPrompt
{
    public string Name => "author_document_gemini_3_8";

    public string Description =>
        "System prompt and guidelines instructing Gemini 3.8 to author canonical MarkSmith Markdown conforming to MD_ENGINE_GOVERNANCE.md.";

    public IReadOnlyList<McpPromptArgument> Arguments => new List<McpPromptArgument>
    {
        new() { Name = "topic", Description = "The subject or document title to author", Required = true },
        new() { Name = "target_audience", Description = "Target audience (e.g., Executive, Developer, Academic, General)", Required = false },
        new() { Name = "include_visuals", Description = "Whether to include SmartArt, Chart, or Diagram containers (true/false)", Required = false },
        new() { Name = "tone", Description = "Writing tone (Formal, Technical, Executive, Direct)", Required = false }
    };

    public Task<McpPromptResult> GetAsync(JsonElement arguments, CancellationToken ct = default)
    {
        string topic = arguments.TryGetProperty("topic", out var tProp) ? tProp.GetString() ?? "Document" : "Document";
        string audience = arguments.TryGetProperty("target_audience", out var aProp) ? aProp.GetString() ?? "Professional" : "Professional";
        bool includeVisuals = arguments.TryGetProperty("include_visuals", out var vProp) && (vProp.ValueKind == JsonValueKind.True || vProp.GetString() == "true");
        string tone = arguments.TryGetProperty("tone", out var toProp) ? toProp.GetString() ?? "Formal" : "Formal";

        var sb = new StringBuilder();
        sb.AppendLine($"# Authoring Mission: {topic}");
        sb.AppendLine();
        sb.AppendLine($"You are authoring a production-ready document on '{topic}' for an audience of '{audience}' in a '{tone}' tone.");
        sb.AppendLine("You MUST adhere strictly to the MarkSmith Markdown Engine Governance contract (`docs/MD_ENGINE_GOVERNANCE.md`).");
        sb.AppendLine();
        sb.AppendLine("## Formatting & DSL Rules:");
        sb.AppendLine("1. **Headings**: Use standard `#`, `##`, `###` headings. DO NOT use pseudo-bold headings like `**Section:**`.");
        sb.AppendLine("2. **Mathematics**: Use `$...$` for inline math and `$$...$$` for display math blocks. Never use raw LaTeX without delimiters.");
        sb.AppendLine("3. **Callouts / Alerts**: Use GitHub alert syntax: `> [!NOTE]`, `> [!TIP]`, `> [!WARNING]`, `> [!IMPORTANT]`, `> [!CAUTION]`.");
        sb.AppendLine("4. **Tables**: Use standard GFM tables with consistent column delimiters `|---|---|`.");

        sb.AppendLine("5. **Document Layout & Typography Directives** (use native MarkSmith containers):");
        sb.AppendLine("   - Executive Cover Page: `:::cover-page theme=\"modern\"` followed by metadata lines (`title: ...`, `subtitle: ...`, `author: ...`, `date: ...`) and closed with `:::`.");
        sb.AppendLine("   - Document Watermark: `:::watermark \"CONFIDENTIAL\"` or `:::watermark \"DRAFT\" opacity=\"0.2\"`.");
        sb.AppendLine("   - Editorial Drop Cap: `:::dropcap` followed by paragraph text and closed with `:::`.");
        sb.AppendLine("   - Legal / Academic Line Numbering: `:::line-numbers` to enable continuous line numbering.");
        sb.AppendLine("   - Concordance / Subject Index: Place `:::index count=2` for the back-of-book index, and tag inline terms using `^[index: \"Keyword\"]`.");
        sb.AppendLine("   - Synchronized Parallel / Bilingual Columns: `:::parallel \"Col 1\" | \"Col 2\"` separated by `===` and closed with `:::`.");

        if (includeVisuals)
        {
            sb.AppendLine("6. **Rich Visual & Analytical Containers**: Use native MarkSmith container blocks with closing `:::`:");
            sb.AppendLine("   - Executive KPIs & Metrics: `:::metrics` with list items like `- **99.9%** Availability`.");
            sb.AppendLine("   - Process / Hierarchy: `:::smartart` or `:::workflow`");
            sb.AppendLine("   - Charts / Plots: `:::chart type=\"bar\"` with `Label,Value` rows");
            sb.AppendLine("   - Multi-tab Views: `:::tabs` with `=== \"Tab Title\"`");
            sb.AppendLine("   - Multi-column Layouts: `:::columns` with `===` separators");
            sb.AppendLine("   - Milestones: `:::timeline` with `YYYY: Event` lines");
        }

        sb.AppendLine();
        sb.AppendLine("Begin by providing a structured outline, followed by the complete, unabridged document markdown.");

        return Task.FromResult(McpPromptResult.SingleMessage(sb.ToString(), "user", $"Authoring instructions for '{topic}'"));
    }
}
