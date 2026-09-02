using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class ConvertDocxTool : IMcpTool
{
    public string Name => "convert_docx_to_markdown";

    public string Description =>
        "Reverses a .docx document back into Markdown, reconstructing CriticMarkup revisions ({++addition++}, {--deletion--}), reviewer comment threads, and LaTeX math equations.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            docx_path = new { type = "string", description = "Path to input .docx file." },
            output_path = new { type = "string", description = "Path to write output .md file. If omitted, returns markdown in response payload." },
            media_dir = new { type = "string", description = "Folder path to extract embedded images into." },
            extract_critic_markup = new { type = "boolean", @default = true, description = "Transpile native Word track changes and comments to CriticMarkup." },
            include_metadata = new { type = "boolean", @default = true, description = "Emit document properties as YAML frontmatter." }
        },
        required = new[] { "docx_path" },
        additionalProperties = false
    };

    public async Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            if (!arguments.TryGetProperty("docx_path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            {
                return McpToolResult.Error("Required parameter 'docx_path' is missing.");
            }

            string docxPath = pathProp.GetString() ?? "";
            if (!File.Exists(docxPath))
            {
                return McpToolResult.Error($"File not found: '{docxPath}'");
            }

            string? mediaDir = null;
            if (arguments.TryGetProperty("media_dir", out var mdProp) && mdProp.ValueKind == JsonValueKind.String)
            {
                mediaDir = mdProp.GetString();
            }

            var service = new ReverseImportService();
            var result = await service.ImportFromDocxAsync(docxPath, mediaDir);

            string markdown = result.Markdown;

            if (arguments.TryGetProperty("output_path", out var outProp) && outProp.ValueKind == JsonValueKind.String)
            {
                string outputPath = Path.GetFullPath(outProp.GetString() ?? "");
                string outDir = Path.GetDirectoryName(outputPath) ?? ".";
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                await File.WriteAllTextAsync(outputPath, markdown, ct);

                return McpToolResult.SuccessJson(new
                {
                    status = "success",
                    output_path = outputPath,
                    tier = result.Tier.ToString(),
                    is_stale = result.IsStale,
                    extracted_media = result.ExtractedMedia,
                    warning = result.Warning
                });
            }

            return McpToolResult.SuccessJson(new
            {
                status = "success",
                markdown = markdown,
                tier = result.Tier.ToString(),
                is_stale = result.IsStale,
                extracted_media = result.ExtractedMedia,
                warning = result.Warning
            });
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"Failed to convert DOCX to Markdown: {ex.Message}");
        }
    }
}
