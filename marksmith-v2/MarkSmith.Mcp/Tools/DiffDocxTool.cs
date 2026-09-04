using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Mcp.Tools;

public sealed class DiffDocxTool : IMcpTool
{
    private readonly DocxInspector _inspector = new();

    public string Name => "diff_docx";

    public string Description =>
        "Compares two DOCX files structurally, identifying modified paragraphs, headings, tables, track changes, and comments.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            old_docx_path = new { type = "string", description = "Path to original DOCX document." },
            new_docx_path = new { type = "string", description = "Path to modified DOCX document." }
        },
        required = new[] { "old_docx_path", "new_docx_path" }
    };

    public Task<McpToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            string oldPath = arguments.TryGetProperty("old_docx_path", out var op) ? op.GetString() ?? "" : "";
            string newPath = arguments.TryGetProperty("new_docx_path", out var np) ? np.GetString() ?? "" : "";

            if (!File.Exists(oldPath)) return Task.FromResult(McpToolResult.Error($"Original DOCX not found: '{oldPath}'"));
            if (!File.Exists(newPath)) return Task.FromResult(McpToolResult.Error($"Modified DOCX not found: '{newPath}'"));

            var options = new DocxInspectionOptions { IncludeText = true, IncludeXml = false };
            var oldRep = _inspector.Inspect(oldPath, options);
            var newRep = _inspector.Inspect(newPath, options);

            var oldParaMap = oldRep.Blocks.Where(b => !string.IsNullOrEmpty(b.ParaId)).ToDictionary(b => b.ParaId!, b => b);
            var newParaMap = newRep.Blocks.Where(b => !string.IsNullOrEmpty(b.ParaId)).ToDictionary(b => b.ParaId!, b => b);

            var addedBlocks = new List<object>();
            var removedBlocks = new List<object>();
            var modifiedBlocks = new List<object>();

            foreach (var kvp in newParaMap)
            {
                if (!oldParaMap.TryGetValue(kvp.Key, out var oldBlock))
                {
                    addedBlocks.Add(new { paraId = kvp.Key, text = kvp.Value.Text, headingLevel = kvp.Value.HeadingLevel });
                }
                else if (oldBlock.Text != kvp.Value.Text)
                {
                    modifiedBlocks.Add(new
                    {
                        paraId = kvp.Key,
                        oldText = oldBlock.Text,
                        newText = kvp.Value.Text,
                        headingPath = kvp.Value.HeadingPath
                    });
                }
            }

            foreach (var kvp in oldParaMap)
            {
                if (!newParaMap.ContainsKey(kvp.Key))
                {
                    removedBlocks.Add(new { paraId = kvp.Key, text = kvp.Value.Text });
                }
            }

            return Task.FromResult(McpToolResult.SuccessJson(new
            {
                paragraphCountChange = newRep.TotalParagraphs - oldRep.TotalParagraphs,
                tableCountChange = newRep.TotalTables - oldRep.TotalTables,
                revisionsCountChange = newRep.TotalRevisions - oldRep.TotalRevisions,
                commentsCountChange = newRep.TotalComments - oldRep.TotalComments,
                addedBlocksCount = addedBlocks.Count,
                removedBlocksCount = removedBlocks.Count,
                modifiedBlocksCount = modifiedBlocks.Count,
                modifiedBlocks,
                addedBlocks,
                removedBlocks
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpToolResult.Error($"diff_docx error: {ex.Message}"));
        }
    }
}
