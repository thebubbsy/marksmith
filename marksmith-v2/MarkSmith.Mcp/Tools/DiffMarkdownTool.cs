using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class DiffMarkdownTool : IMcpTool
{
    private readonly MarkdownDiffService _diffService = new();

    public string Name => "diff_markdown";

    public string Description =>
        "Calculates line-level differences between two Markdown documents or files with change classification.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            old_content = new { type = "string", description = "Original markdown content (mutually exclusive with old_path)." },
            new_content = new { type = "string", description = "Modified markdown content (mutually exclusive with new_path)." },
            old_path = new { type = "string", description = "Path to the original markdown file." },
            new_path = new { type = "string", description = "Path to the modified markdown file." }
        }
    };

    public async Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            string oldText = "";
            string newText = "";

            if (arguments.TryGetProperty("old_content", out var ocProp) && ocProp.ValueKind == JsonValueKind.String)
                oldText = ocProp.GetString() ?? "";
            else if (arguments.TryGetProperty("old_path", out var opProp) && opProp.ValueKind == JsonValueKind.String)
            {
                string path = opProp.GetString() ?? "";
                if (File.Exists(path)) oldText = await File.ReadAllTextAsync(path, ct);
            }

            if (arguments.TryGetProperty("new_content", out var ncProp) && ncProp.ValueKind == JsonValueKind.String)
                newText = ncProp.GetString() ?? "";
            else if (arguments.TryGetProperty("new_path", out var npProp) && npProp.ValueKind == JsonValueKind.String)
            {
                string path = npProp.GetString() ?? "";
                if (File.Exists(path)) newText = await File.ReadAllTextAsync(path, ct);
            }

            var diff = _diffService.Compare(oldText, newText);

            return McpToolResult.SuccessJson(new
            {
                hasChanges = diff.HasChanges,
                insertedCount = diff.InsertedCount,
                deletedCount = diff.DeletedCount,
                unchangedCount = diff.UnchangedCount,
                lines = diff.Lines
            });
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"diff_markdown error: {ex.Message}");
        }
    }
}
