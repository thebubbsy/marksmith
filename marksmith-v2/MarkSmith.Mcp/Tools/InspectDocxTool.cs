using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class InspectDocxTool : IMcpTool
{
    public string Name => "inspect_docx";

    public string Description =>
        "Inspects an existing .docx document and returns a structured structural inventory, including metadata, section properties, paragraph blocks with w14:paraId identifiers, tables, track changes revisions, reviewer comments, styles used, and media parts.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            docx_path = new { type = "string", description = "Path to the .docx file to inspect." },
            include_text = new { type = "boolean", @default = true, description = "Include text snippets for paragraphs and table cells." },
            include_xml = new { type = "boolean", @default = false, description = "Include raw OpenXML element tags for deep inspection." },
            max_paragraphs = new { type = "integer", @default = 500, description = "Maximum number of paragraph blocks to return in detail." },
            filter_revisions = new { type = "boolean", @default = false, description = "If true, filters results to only blocks containing track changes." },
            filter_comments = new { type = "boolean", @default = false, description = "If true, filters results to only blocks containing reviewer comments." }
        },
        required = new[] { "docx_path" },
        additionalProperties = false
    };

    public Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            if (!arguments.TryGetProperty("docx_path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(McpToolResult.Error("Required parameter 'docx_path' is missing."));
            }

            string docxPath = pathProp.GetString() ?? "";
            if (!File.Exists(docxPath))
            {
                return Task.FromResult(McpToolResult.Error($"File not found: '{docxPath}'"));
            }

            bool includeText = true;
            if (arguments.TryGetProperty("include_text", out var itProp) && (itProp.ValueKind == JsonValueKind.True || itProp.ValueKind == JsonValueKind.False))
                includeText = itProp.GetBoolean();

            bool includeXml = false;
            if (arguments.TryGetProperty("include_xml", out var ixProp) && (ixProp.ValueKind == JsonValueKind.True || ixProp.ValueKind == JsonValueKind.False))
                includeXml = ixProp.GetBoolean();

            int maxParagraphs = 500;
            if (arguments.TryGetProperty("max_paragraphs", out var mpProp) && mpProp.TryGetInt32(out int mp))
                maxParagraphs = mp;

            bool filterRevisions = false;
            if (arguments.TryGetProperty("filter_revisions", out var frProp) && (frProp.ValueKind == JsonValueKind.True || frProp.ValueKind == JsonValueKind.False))
                filterRevisions = frProp.GetBoolean();

            bool filterComments = false;
            if (arguments.TryGetProperty("filter_comments", out var fcProp) && (fcProp.ValueKind == JsonValueKind.True || fcProp.ValueKind == JsonValueKind.False))
                filterComments = fcProp.GetBoolean();

            var options = new DocxInspectionOptions
            {
                IncludeText = includeText,
                IncludeXml = includeXml,
                MaxParagraphs = maxParagraphs,
                FilterRevisions = filterRevisions,
                FilterComments = filterComments
            };

            var inspector = new DocxInspector();
            var report = inspector.Inspect(docxPath, options);

            return Task.FromResult(McpToolResult.SuccessJson(new
            {
                status = "success",
                docx_path = Path.GetFullPath(docxPath),
                report
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpToolResult.Error($"Failed to inspect DOCX: {ex.Message}"));
        }
    }
}
