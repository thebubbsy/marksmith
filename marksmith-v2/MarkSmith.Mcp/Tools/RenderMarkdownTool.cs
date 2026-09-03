using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class RenderMarkdownTool : IMcpTool
{
    public string Name => "render_markdown_to_docx";

    public string Description =>
        "Compiles Markdown content or an input .md file into a styled Microsoft Word (.docx) document with optional template matching, theme selection, and layout configurations.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            markdown = new { type = "string", description = "Raw Markdown source text to render." },
            input_path = new { type = "string", description = "Path to input .md file (if markdown text not directly provided)." },
            output_path = new { type = "string", description = "Destination path for generated .docx file. If omitted, writes to a temporary file." },
            template_path = new { type = "string", description = "Path to reference .dotx or .docx template to inherit styles, margins, and headers/footers from." },
            theme = new { type = "string", description = "Theme name (e.g., 'GitHub Light', 'Dracula', 'Nord', 'Academic', 'Corporate')." },
            a4_fixed_width = new { type = "boolean", description = "Use A4 page geometry instead of US Letter." },
            include_toc = new { type = "boolean", description = "Insert auto-updating Word Table of Contents field." },
            track_changes = new { type = "boolean", description = "Enable Track Changes mode in document settings." },
            author_name = new { type = "string", description = "Author name for document properties and revision tracking." },
            stream_mode = new { type = "boolean", description = "Enable high-throughput multi-threaded SAX streaming pipeline for large token streams." },
            return_base64 = new { type = "boolean", description = "If true, returns docx binary encoded as base64 string in JSON output." }
        },
        additionalProperties = false
    };

    public async Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            string markdown = "";
            if (arguments.TryGetProperty("markdown", out var mdProp) && mdProp.ValueKind == JsonValueKind.String)
            {
                markdown = mdProp.GetString() ?? "";
            }
            else if (arguments.TryGetProperty("input_path", out var inPathProp) && inPathProp.ValueKind == JsonValueKind.String)
            {
                string inPath = inPathProp.GetString() ?? "";
                if (!File.Exists(inPath))
                {
                    return McpToolResult.Error($"Input file not found: '{inPath}'");
                }
                markdown = await File.ReadAllTextAsync(inPath, ct);
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                return McpToolResult.Error("Either 'markdown' content or valid 'input_path' must be provided.");
            }

            string outputPath = "";
            if (arguments.TryGetProperty("output_path", out var outPathProp) && outPathProp.ValueKind == JsonValueKind.String)
            {
                outputPath = outPathProp.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.Combine(Path.GetTempPath(), $"marksmith_render_{Guid.NewGuid():N}.docx");
            }

            outputPath = Path.GetFullPath(outputPath);
            string outputDir = Path.GetDirectoryName(outputPath) ?? ".";
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            var settings = new AppSettings();

            if (arguments.TryGetProperty("theme", out var themeProp) && themeProp.ValueKind == JsonValueKind.String)
            {
                settings.Theme = themeProp.GetString() ?? "GitHub Light";
            }
            if (arguments.TryGetProperty("template_path", out var tmplProp) && tmplProp.ValueKind == JsonValueKind.String)
            {
                settings.BrandTemplatePath = tmplProp.GetString() ?? "";
            }
            if (arguments.TryGetProperty("a4_fixed_width", out var a4Prop) && (a4Prop.ValueKind == JsonValueKind.True || a4Prop.ValueKind == JsonValueKind.False))
            {
                settings.A4FixedWidth = a4Prop.GetBoolean();
            }
            if (arguments.TryGetProperty("include_toc", out var tocProp) && (tocProp.ValueKind == JsonValueKind.True || tocProp.ValueKind == JsonValueKind.False))
            {
                settings.IncludeToc = tocProp.GetBoolean();
            }
            if (arguments.TryGetProperty("track_changes", out var tcProp) && (tcProp.ValueKind == JsonValueKind.True || tcProp.ValueKind == JsonValueKind.False))
            {
                settings.TrackChanges = tcProp.GetBoolean();
            }
            if (arguments.TryGetProperty("author_name", out var authorProp) && authorProp.ValueKind == JsonValueKind.String)
            {
                settings.AuthorName = authorProp.GetString() ?? "";
            }

            bool streamMode = arguments.TryGetProperty("stream_mode", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

            var exportService = new DocxExportService();
            if (streamMode)
            {
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown));
                await exportService.ExportStreamAsync(ms, outputPath, settings, ct);
            }
            else
            {
                await exportService.ExportAsync(markdown, outputPath, settings);
            }

            var fileInfo = new FileInfo(outputPath);
            bool returnBase64 = arguments.TryGetProperty("return_base64", out var b64Prop) && b64Prop.GetBoolean();
            string? base64Data = null;
            if (returnBase64 && fileInfo.Exists)
            {
                byte[] bytes = await File.ReadAllBytesAsync(outputPath, ct);
                base64Data = Convert.ToBase64String(bytes);
            }

            var response = new
            {
                status = "success",
                output_path = outputPath,
                bytes_written = fileInfo.Length,
                theme = settings.Theme,
                created_at = DateTime.UtcNow.ToString("o"),
                base64 = base64Data
            };

            return McpToolResult.SuccessJson(response);
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"Failed to render Markdown to DOCX: {ex.Message}");
        }
    }
}
