using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Mcp.Resources;

public sealed class PatchSpecResource : IMcpResource
{
    public string Uri => "marksmith://schemas/patch-spec";
    public string Name => "MarkSmith Patch Specifications";
    public string Description => "JSON Schema specifications for patch_markdown, patch_docx, validate_markdown, and manage_3block_cycle.";
    public string MimeType => "application/json";

    public Task<McpResourceResult> ReadAsync(CancellationToken ct = default)
    {
        var spec = new
        {
            patchMarkdown = new
            {
                type = "object",
                properties = new
                {
                    content = new { type = "string", description = "Raw markdown string to patch (mutually exclusive with input_path)" },
                    input_path = new { type = "string", description = "Path to source markdown file" },
                    output_path = new { type = "string", description = "Path to write patched output" },
                    operations = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            required = new[] { "op" },
                            properties = new
                            {
                                op = new
                                {
                                    type = "string",
                                    @enum = new[]
                                    {
                                        "search_replace", "block_replace", "block_insert_before",
                                        "block_insert_after", "block_delete", "prepend", "append",
                                        "accept_critic_markup", "reject_critic_markup", "inject_critic_markup"
                                    }
                                },
                                target_content = new { type = "string", description = "Exact target text to find and replace" },
                                replacement_content = new { type = "string", description = "Replacement text or block content" },
                                allow_multiple = new { type = "boolean", description = "Allow replacing multiple matches without error" },
                                start_line = new { type = "integer", description = "1-indexed starting line limit" },
                                end_line = new { type = "integer", description = "1-indexed ending line limit" },
                                heading_path = new { type = "string", description = "AST heading path selector" },
                                block_index = new { type = "integer", description = "0-indexed AST block selector" }
                            }
                        }
                    }
                }
            },
            patchDocx = new
            {
                type = "object",
                properties = new
                {
                    docx_path = new { type = "string", description = "Target DOCX path" },
                    output_path = new { type = "string", description = "Output DOCX path" },
                    operations = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            required = new[] { "op" },
                            properties = new
                            {
                                op = new
                                {
                                    type = "string",
                                    @enum = new[]
                                    {
                                        "replace", "insert_before", "insert_after", "delete",
                                        "append", "prepend", "add_comment", "accept_revision", "reject_revision"
                                    }
                                },
                                target = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        para_id = new { type = "string", description = "8-hex w14:paraId" },
                                        index = new { type = "integer", description = "0-indexed body block" },
                                        heading_path = new { type = "string", description = "Breadcrumb heading path" },
                                        bookmark = new { type = "string", description = "Bookmark name" }
                                    }
                                },
                                content = new { type = "string", description = "Markdown text to transpile into DOCX elements" },
                                track_changes = new { type = "boolean", description = "Wrap mutation in OpenXML revision markup" },
                                comment = new { type = "string", description = "Reviewer comment text" },
                                author = new { type = "string", description = "Author name for revisions and comments" }
                            }
                        }
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(McpResourceResult.FromText(Uri, json, MimeType));
    }
}
