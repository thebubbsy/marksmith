using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class ValidateMarkdownTool : IMcpTool
{
    private readonly MarkdownValidationService _validator = new();

    public string Name => "validate_markdown";

    public string Description =>
        "Validates and lints Markdown against the MarkSmith syntax governance specification (MD_ENGINE_GOVERNANCE.md) with line-level diagnostics.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            markdown = new
            {
                type = "string",
                description = "Raw markdown string to validate (optional if input_path is provided)."
            },
            input_path = new
            {
                type = "string",
                description = "Path to the markdown file on disk to validate."
            }
        }
    };

    public async Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            string markdown = "";
            if (arguments.TryGetProperty("markdown", out var mProp) && mProp.ValueKind == JsonValueKind.String)
            {
                markdown = mProp.GetString() ?? "";
            }
            else if (arguments.TryGetProperty("input_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
            {
                string path = pProp.GetString() ?? "";
                if (!File.Exists(path))
                {
                    return McpToolResult.Error($"File not found: '{path}'");
                }
                markdown = await File.ReadAllTextAsync(path, ct);
            }

            if (string.IsNullOrEmpty(markdown))
            {
                return McpToolResult.Error("No markdown content or valid input_path provided.");
            }

            var report = _validator.Validate(markdown);

            return McpToolResult.SuccessJson(new
            {
                isValid = report.IsValid,
                totalLines = report.TotalLines,
                totalBlocks = report.TotalBlocks,
                errorsCount = report.ErrorsCount,
                warningsCount = report.WarningsCount,
                infoCount = report.InfoCount,
                issues = report.Issues
            });
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"validate_markdown error: {ex.Message}");
        }
    }
}
