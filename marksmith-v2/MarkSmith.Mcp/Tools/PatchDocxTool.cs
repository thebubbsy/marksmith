using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class PatchDocxTool : IMcpTool
{
    public string Name => "patch_docx";

    public string Description =>
        "Applies surgical, localized block-level modifications (replace, insert, delete, comment, track changes) to an existing .docx file without full round-tripping, preserving all untouched styling, headers/footers, embedded objects, and relationships.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            docx_path = new { type = "string", description = "Path to the source .docx file." },
            output_path = new { type = "string", description = "Path for the patched file. If omitted, modifies in-place atomically." },
            operations = new
            {
                type = "array",
                description = "List of patch operations to apply in order.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        op = new
                        {
                            type = "string",
                            @enum = new[] { "replace", "insert_before", "insert_after", "delete", "append", "prepend", "add_comment", "accept_revision", "reject_revision" },
                            description = "The mutation operation to perform."
                        },
                        target = new
                        {
                            type = "object",
                            description = "Selector specifying the target block.",
                            properties = new
                            {
                                para_id = new { type = "string", description = "Target paragraph by 8-character hex w14:paraId." },
                                index = new { type = "integer", description = "Target block by 0-based body element index." },
                                heading_path = new { type = "string", description = "Target block by heading text or breadcrumb path." },
                                bookmark = new { type = "string", description = "Target block by named bookmark." },
                                comment_id = new { type = "string", description = "Target comment ID." },
                                table_cell = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        table_index = new { type = "integer" },
                                        table_para_id = new { type = "string" },
                                        row = new { type = "integer" },
                                        col = new { type = "integer" }
                                    },
                                    required = new[] { "row", "col" }
                                }
                            }
                        },
                        content = new { type = "string", description = "Markdown content to render and inject into the target slot." },
                        track_changes = new { type = "boolean", description = "If true, records modification as native Word Track Changes." },
                        author = new { type = "string", description = "Author name for Track Changes or Comments (default: 'Marksmith AI')." },
                        comment = new { type = "string", description = "Reviewer comment text to attach to target block." },
                        preserve_formatting = new { type = "boolean", @default = true, description = "Retain target paragraph style and formatting when replacing." }
                    },
                    required = new[] { "op" }
                }
            },
            // Direct single-operation properties for simplicity
            op = new { type = "string", description = "Single mutation operation (if operations array not provided)." },
            target = new { type = "object", description = "Single target selector (if operations array not provided)." },
            content = new { type = "string", description = "Markdown content for single operation." },
            comment = new { type = "string", description = "Comment text for single operation." }
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

            string? outputPath = null;
            if (arguments.TryGetProperty("output_path", out var outProp) && outProp.ValueKind == JsonValueKind.String)
            {
                outputPath = outProp.GetString();
            }

            var opList = new List<DocxPatchOperationItem>();

            if (arguments.TryGetProperty("operations", out var opsProp) && opsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in opsProp.EnumerateArray())
                {
                    opList.Add(ParseOperationItem(item));
                }
            }
            else if (arguments.TryGetProperty("op", out var singleOpProp) || arguments.TryGetProperty("target", out _))
            {
                opList.Add(ParseOperationItem(arguments));
            }

            if (opList.Count == 0)
            {
                return Task.FromResult(McpToolResult.Error("No patch operations specified."));
            }

            var patchRequest = new DocxPatchRequest
            {
                DocxPath = docxPath,
                OutputPath = outputPath,
                Operations = opList
            };

            var patcher = new InPlaceDocxPatcher();
            var result = patcher.ApplyPatch(docxPath, patchRequest);

            if (!result.Success)
            {
                return Task.FromResult(McpToolResult.Error(result.ErrorMessage ?? "Patch failed."));
            }

            return Task.FromResult(McpToolResult.SuccessJson(new
            {
                status = "success",
                output_path = result.OutputPath ?? docxPath,
                operations_applied = result.OperationsApplied,
                modified_blocks = result.ModifiedBlocks,
                modified_parts = result.ModifiedParts,
                details = result.OperationDetails,
                validation_errors = result.ValidationErrors
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpToolResult.Error($"Failed to patch DOCX: {ex.Message}"));
        }
    }

    private static DocxPatchOperationItem ParseOperationItem(JsonElement elem)
    {
        var op = PatchOperation.Replace;
        if (elem.TryGetProperty("op", out var opProp) && opProp.ValueKind == JsonValueKind.String)
        {
            var opStr = opProp.GetString()?.Replace("_", "").ToLowerInvariant();
            if (Enum.TryParse<PatchOperation>(opStr, true, out var parsedOp))
            {
                op = parsedOp;
            }
        }

        var selector = new BlockSelector();
        if (elem.TryGetProperty("target", out var targetProp) && targetProp.ValueKind == JsonValueKind.Object)
        {
            string? paraId = targetProp.TryGetProperty("para_id", out var pId) ? pId.GetString() : null;
            if (string.IsNullOrEmpty(paraId) && targetProp.TryGetProperty("paraId", out var pId2)) paraId = pId2.GetString();

            int? index = null;
            if (targetProp.TryGetProperty("index", out var idx) && idx.TryGetInt32(out int iVal)) index = iVal;
            if (!index.HasValue && targetProp.TryGetProperty("body_index", out var bIdx) && bIdx.TryGetInt32(out int bVal)) index = bVal;
            if (!index.HasValue && targetProp.TryGetProperty("BodyIndex", out var bIdx2) && bIdx2.TryGetInt32(out int bVal2)) index = bVal2;
            if (!index.HasValue && targetProp.TryGetProperty("bodyIndex", out var bIdx3) && bIdx3.TryGetInt32(out int bVal3)) index = bVal3;

            string? headingPath = targetProp.TryGetProperty("heading_path", out var hp) ? hp.GetString() : null;
            if (string.IsNullOrEmpty(headingPath) && targetProp.TryGetProperty("headingPath", out var hp2)) headingPath = hp2.GetString();

            string? bookmark = targetProp.TryGetProperty("bookmark", out var bm) ? bm.GetString() : null;
            if (string.IsNullOrEmpty(bookmark) && targetProp.TryGetProperty("bookmark_name", out var bm2)) bookmark = bm2.GetString();

            string? commentId = targetProp.TryGetProperty("comment_id", out var cid) ? cid.GetString() : null;

            TableCellSelector? tableCell = null;
            if (targetProp.TryGetProperty("table_cell", out var tc) && tc.ValueKind == JsonValueKind.Object)
            {
                int? tblIdx = tc.TryGetProperty("table_index", out var tIdx) && tIdx.TryGetInt32(out int ti) ? ti : null;
                string? tblParaId = tc.TryGetProperty("table_para_id", out var tPId) ? tPId.GetString() : null;
                int row = tc.TryGetProperty("row", out var rProp) && rProp.TryGetInt32(out int r) ? r : 0;
                int col = tc.TryGetProperty("col", out var cProp) && cProp.TryGetInt32(out int c) ? c : 0;
                tableCell = new TableCellSelector { TableIndex = tblIdx, TableParaId = tblParaId, Row = row, Col = col };
            }

            selector = new BlockSelector
            {
                ParaId = paraId,
                BodyIndex = index,
                HeadingPath = headingPath,
                BookmarkName = bookmark,
                CommentId = commentId,
                TableCell = tableCell
            };
        }

        string? content = elem.TryGetProperty("content", out var cProp2) ? cProp2.GetString() : null;
        if (string.IsNullOrEmpty(content) && elem.TryGetProperty("markdown", out var mdProp)) content = mdProp.GetString();

        bool trackChanges = elem.TryGetProperty("track_changes", out var tcProp2) && tcProp2.GetBoolean();
        string author = elem.TryGetProperty("author", out var aProp) ? (aProp.GetString() ?? "Marksmith AI") : "Marksmith AI";
        string? comment = elem.TryGetProperty("comment", out var cmProp) ? cmProp.GetString() : null;
        bool preserveFormatting = !elem.TryGetProperty("preserve_formatting", out var pfProp) || pfProp.GetBoolean();

        return new DocxPatchOperationItem
        {
            Op = op,
            Target = selector,
            Content = content,
            TrackChanges = trackChanges,
            Author = author,
            Comment = comment,
            PreserveFormatting = preserveFormatting
        };
    }
}
