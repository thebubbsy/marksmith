using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public enum SemanticBlockType
{
    Heading,
    Paragraph,
    CodeBlock,
    Table,
    List,
    Blockquote
}

public enum SemanticChangeType
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public record SemanticBlock(
    string Id,
    SemanticBlockType BlockType,
    string Content,
    string ContentHash,
    int StartLine);

public record SemanticDiffItem(
    SemanticChangeType ChangeType,
    SemanticBlock? OldBlock,
    SemanticBlock? NewBlock,
    string Summary);

public class SemanticDiffReport
{
    public List<SemanticDiffItem> Items { get; } = new();
    public int AdditionsCount => Items.Count(i => i.ChangeType == SemanticChangeType.Added);
    public int DeletionsCount => Items.Count(i => i.ChangeType == SemanticChangeType.Removed);
    public int ModificationsCount => Items.Count(i => i.ChangeType == SemanticChangeType.Modified);
}

/// <summary>
/// Service that performs AST-aware structural diffing between two versions of a Markdown document.
/// </summary>
public static class DocumentSemanticDiffService
{
    private static readonly Regex BlockSplitRegex = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex ListBlockRegex = new(@"^(?:[-*+]|\d+\.)\s+", RegexOptions.Compiled);

    /// <summary>
    /// Computes the semantic block diff between an original and updated Markdown document.
    /// </summary>
    public static SemanticDiffReport Compare(string oldMarkdown, string newMarkdown)
    {
        var report = new SemanticDiffReport();
        var oldBlocks = ParseBlocks(oldMarkdown);
        var newBlocks = ParseBlocks(newMarkdown);

        int oldIdx = 0, newIdx = 0;

        while (oldIdx < oldBlocks.Count || newIdx < newBlocks.Count)
        {
            if (oldIdx >= oldBlocks.Count)
            {
                // Remaining new blocks are Added
                var nb = newBlocks[newIdx++];
                report.Items.Add(new SemanticDiffItem(SemanticChangeType.Added, null, nb, $"Added {nb.BlockType}"));
                continue;
            }

            if (newIdx >= newBlocks.Count)
            {
                // Remaining old blocks are Removed
                var ob = oldBlocks[oldIdx++];
                report.Items.Add(new SemanticDiffItem(SemanticChangeType.Removed, ob, null, $"Removed {ob.BlockType}"));
                continue;
            }

            var oldB = oldBlocks[oldIdx];
            var newB = newBlocks[newIdx];

            if (oldB.ContentHash == newB.ContentHash)
            {
                report.Items.Add(new SemanticDiffItem(SemanticChangeType.Unchanged, oldB, newB, "Unchanged"));
                oldIdx++;
                newIdx++;
            }
            else if (oldB.BlockType == newB.BlockType)
            {
                report.Items.Add(new SemanticDiffItem(SemanticChangeType.Modified, oldB, newB, $"Modified {newB.BlockType}"));
                oldIdx++;
                newIdx++;
            }
            else
            {
                // One removed, one added
                report.Items.Add(new SemanticDiffItem(SemanticChangeType.Removed, oldB, null, $"Removed {oldB.BlockType}"));
                report.Items.Add(new SemanticDiffItem(SemanticChangeType.Added, null, newB, $"Added {newB.BlockType}"));
                oldIdx++;
                newIdx++;
            }
        }

        return report;
    }

    private static List<SemanticBlock> ParseBlocks(string markdown)
    {
        var list = new List<SemanticBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
            return list;

        var rawBlocks = BlockSplitRegex.Split(markdown.Trim());
        int currentLine = 1;

        for (int i = 0; i < rawBlocks.Length; i++)
        {
            string content = rawBlocks[i].Trim();
            if (string.IsNullOrEmpty(content)) continue;

            var type = DetectBlockType(content);
            string hash = ComputeHash(content);
            list.Add(new SemanticBlock($"blk_{i + 1}", type, content, hash, currentLine));

            currentLine += content.Split('\n').Length + 1;
        }

        return list;
    }

    private static SemanticBlockType DetectBlockType(string text)
    {
        if (text.StartsWith("#")) return SemanticBlockType.Heading;
        if (text.StartsWith("```")) return SemanticBlockType.CodeBlock;
        if (text.StartsWith("|")) return SemanticBlockType.Table;
        if (text.StartsWith(">")) return SemanticBlockType.Blockquote;
        if (ListBlockRegex.IsMatch(text)) return SemanticBlockType.List;
        return SemanticBlockType.Paragraph;
    }

    private static string ComputeHash(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).Substring(0, 16);
    }
}
