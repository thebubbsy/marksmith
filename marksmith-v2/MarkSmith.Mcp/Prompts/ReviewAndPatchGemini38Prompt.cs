using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Prompts;

public sealed class ReviewAndPatchGemini38Prompt : IMcpPrompt
{
    public string Name => "review_and_patch_gemini_3_8";

    public string Description =>
        "Systematic review, redline, and surgical patching workflow for Markdown and OpenXML DOCX documents.";

    public IReadOnlyList<McpPromptArgument> Arguments => new List<McpPromptArgument>
    {
        new() { Name = "document_type", Description = "Type of document (Report, TechnicalSpec, LegalContract, Article)", Required = false },
        new() { Name = "review_criteria", Description = "Specific review focus (Accuracy, Clarity, Grammar, Layout, Compliance)", Required = false }
    };

    public Task<McpPromptResult> GetAsync(JsonElement arguments, CancellationToken ct = default)
    {
        string docType = arguments.TryGetProperty("document_type", out var dProp) ? dProp.GetString() ?? "Document" : "Document";
        string criteria = arguments.TryGetProperty("review_criteria", out var cProp) ? cProp.GetString() ?? "Comprehensive" : "Comprehensive";

        var sb = new StringBuilder();
        sb.AppendLine($"# Document Review & Surgical Patching Workflow: {docType}");
        sb.AppendLine();
        sb.AppendLine($"Focus Area: **{criteria}**");
        sb.AppendLine();
        sb.AppendLine("## Step-by-Step Procedure:");
        sb.AppendLine("1. **Inspection**:");
        sb.AppendLine("   - For DOCX: Call `inspect_docx` to examine paragraphs, heading paths, `paraId` tags, comments, and revisions.");
        sb.AppendLine("   - For Markdown: Read the document structure and check for AST blocks.");
        sb.AppendLine("2. **Syntax Validation**:");
        sb.AppendLine("   - Call `validate_markdown` to detect unclosed containers (`:::`), unclosed math (`$$`), or table syntax errors.");
        sb.AppendLine("3. **Surgical Patching**:");
        sb.AppendLine("   - For Markdown: Call `patch_markdown` using exact `search_replace`, AST `block_replace`, or CriticMarkup (`{++addition++}`, `{--deletion--}`).");
        sb.AppendLine("   - For DOCX: Call `patch_docx` using selector precedence: `para_id` > `bookmark` > `heading_path` > `index`.");
        sb.AppendLine("4. **Verification**:");
        sb.AppendLine("   - Call `diff_markdown` or `diff_docx` to verify that only intended sections were modified without collateral drift.");

        return Task.FromResult(McpPromptResult.SingleMessage(sb.ToString(), "user", $"Review and patch workflow for '{docType}'"));
    }
}
