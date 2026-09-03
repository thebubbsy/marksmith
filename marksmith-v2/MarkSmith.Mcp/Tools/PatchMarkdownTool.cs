using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class PatchMarkdownTool : IMcpTool
{
    private readonly MarkdownPatchService _patchService = new();

    public string Name => "patch_markdown";

    public string Description =>
        "Performs lossless, in-place search/replace, AST block patching, or CriticMarkup revision processing on Markdown files/text with structured diagnostics.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            content = new
            {
                type = "string",
                description = "Direct markdown content to patch (optional if input_path is provided)."
            },
            input_path = new
            {
                type = "string",
                description = "Path to the markdown file to read and mutate."
            },
            output_path = new
            {
                type = "string",
                description = "Path to write mutated markdown. If omitted and input_path is provided, mutates in-place."
            },
            operations = new
            {
                type = "array",
                description = "List of patch operations to apply sequentially.",
                items = new
                {
                    type = "object",
                    required = new[] { "op" },
                    properties = new
                    {
                        op = new
                        {
                            type = "string",
                            description = "Patch operation type.",
                            @enum = new[]
                            {
                                "search_replace",
                                "block_replace",
                                "block_insert_before",
                                "block_insert_after",
                                "block_delete",
                                "prepend",
                                "append",
                                "accept_critic_markup",
                                "reject_critic_markup",
                                "inject_critic_markup"
                            }
                        },
                        target_content = new
                        {
                            type = "string",
                            description = "Exact character sequence to find (whitespace-sensitive). For search_replace and inject_critic_markup."
                        },
                        replacement_content = new
                        {
                            type = "string",
                            description = "Replacement markdown content to drop in."
                        },
                        allow_multiple = new
                        {
                            type = "boolean",
                            description = "If true, replaces all occurrences. If false, errors if target appears multiple times."
                        },
                        start_line = new
                        {
                            type = "integer",
                            description = "1-indexed starting line number to constrain search scope."
                        },
                        end_line = new
                        {
                            type = "integer",
                            description = "1-indexed ending line number to constrain search scope."
                        },
                        heading_path = new
                        {
                            type = "string",
                            description = "AST heading path selector (e.g. 'Overview / Architecture')."
                        },
                        block_index = new
                        {
                            type = "integer",
                            description = "0-indexed AST block selector."
                        },
                        comment = new
                        {
                            type = "string",
                            description = "Optional reviewer comment for CriticMarkup injection."
                        },
                        author = new
                        {
                            type = "string",
                            description = "Author name for revisions."
                        }
                    }
                }
            }
        },
        required = new[] { "operations" }
    };

    public Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            var req = new MarkdownPatchRequest();

            if (arguments.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                req.Content = cProp.GetString();

            if (arguments.TryGetProperty("input_path", out var inProp) && inProp.ValueKind == JsonValueKind.String)
                req.InputPath = inProp.GetString();

            if (arguments.TryGetProperty("output_path", out var outProp) && outProp.ValueKind == JsonValueKind.String)
                req.OutputPath = outProp.GetString();
            else if (!string.IsNullOrEmpty(req.InputPath))
                req.OutputPath = req.InputPath;

            if (arguments.TryGetProperty("operations", out var opsProp) && opsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var opElem in opsProp.EnumerateArray())
                {
                    var op = new MarkdownPatchOperation();
                    if (opElem.TryGetProperty("op", out var opTypeProp))
                    {
                        string opStr = opTypeProp.GetString()?.ToLowerInvariant() ?? "search_replace";
                        op.Op = opStr switch
                        {
                            "search_replace" => MarkdownPatchOp.SearchReplace,
                            "block_replace" => MarkdownPatchOp.BlockReplace,
                            "block_insert_before" => MarkdownPatchOp.BlockInsertBefore,
                            "block_insert_after" => MarkdownPatchOp.BlockInsertAfter,
                            "block_delete" => MarkdownPatchOp.BlockDelete,
                            "prepend" => MarkdownPatchOp.Prepend,
                            "append" => MarkdownPatchOp.Append,
                            "accept_critic_markup" => MarkdownPatchOp.AcceptCriticMarkup,
                            "reject_critic_markup" => MarkdownPatchOp.RejectCriticMarkup,
                            "inject_critic_markup" => MarkdownPatchOp.InjectCriticMarkup,
                            _ => MarkdownPatchOp.SearchReplace
                        };
                    }

                    if (opElem.TryGetProperty("target_content", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                        op.TargetContent = tProp.GetString();

                    if (opElem.TryGetProperty("replacement_content", out var rProp) && rProp.ValueKind == JsonValueKind.String)
                        op.ReplacementContent = rProp.GetString();

                    if (opElem.TryGetProperty("allow_multiple", out var mProp))
                        op.AllowMultiple = mProp.ValueKind == JsonValueKind.True;

                    if (opElem.TryGetProperty("start_line", out var slProp) && slProp.TryGetInt32(out int sl))
                        op.StartLine = sl;

                    if (opElem.TryGetProperty("end_line", out var elProp) && elProp.TryGetInt32(out int el))
                        op.EndLine = el;

                    if (opElem.TryGetProperty("heading_path", out var hpProp) && hpProp.ValueKind == JsonValueKind.String)
                        op.HeadingPath = hpProp.GetString();

                    if (opElem.TryGetProperty("block_index", out var biProp) && biProp.TryGetInt32(out int bi))
                        op.BlockIndex = bi;

                    if (opElem.TryGetProperty("comment", out var cmProp) && cmProp.ValueKind == JsonValueKind.String)
                        op.Comment = cmProp.GetString();

                    if (opElem.TryGetProperty("author", out var auProp) && auProp.ValueKind == JsonValueKind.String)
                        op.Author = auProp.GetString();

                    req.Operations.Add(op);
                }
            }

            var result = _patchService.ApplyPatch(req);
            if (!result.Success)
            {
                return Task.FromResult(McpToolResult.SuccessJson(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    diagnostics = result.Diagnostics
                }));
            }

            return Task.FromResult(McpToolResult.SuccessJson(new
            {
                success = true,
                modifiedBlocks = result.ModifiedBlocks,
                appliedOperations = result.AppliedOperations,
                outputPath = req.OutputPath,
                contentLength = result.NewMarkdown?.Length ?? 0
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpToolResult.Error($"patch_markdown error: {ex.Message}"));
        }
    }
}
