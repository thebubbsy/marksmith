using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Editor;

public enum EditorFoldType
{
    CodeBlock,
    FeatureBlock,
    HeadingSection,
    FunctionDefinition
}

public class EditorFoldRegion
{
    public int StartLine { get; set; } // 1-indexed
    public int EndLine { get; set; }   // 1-indexed
    public EditorFoldType Type { get; set; }
    public string Header { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public int LineCount => EndLine - StartLine + 1;
    public string Summary => $"{LineCount} lines folded";
}

/// <summary>
/// Service providing high-performance, snappy code, function, and section folding
/// directly on the editor side (left-hand editor pane) in MarkSmith.
/// </summary>
public static class EditorFoldingService
{
    private static readonly Regex CodeFenceOpenRegex = new(
        @"^```([a-zA-Z0-9_\-#+]*)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FeatureBlockOpenRegex = new(
        @"^:::([a-zA-Z0-9_\-]+)",
        RegexOptions.Compiled);

    private static readonly Regex HeadingRegex = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRegex = new(
        @"^(?:\s*)(?:public|private|protected|internal|async|static|\s)*\s*(?:def\s+[a-zA-Z0-9_]+|function\s+[a-zA-Z0-9_]+|fn\s+[a-zA-Z0-9_]+|func\s+[a-zA-Z0-9_]+|[a-zA-Z0-9_<>\[\]]+\s+[a-zA-Z0-9_]+\s*\([^)]*\)\s*\{?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FoldMarkerRegex = new(
        @"<!--\s*FOLDED:([a-zA-Z0-9_]+):([a-zA-Z0-9_\-#+]*):(\d+):([A-Za-z0-9+/=]+)\s*-->",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans markdown text and identifies all foldable code blocks, feature containers, and sections.
    /// </summary>
    public static List<EditorFoldRegion> DetectFoldableRegions(string markdownText)
    {
        var regions = new List<EditorFoldRegion>();
        if (string.IsNullOrWhiteSpace(markdownText))
            return regions;

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int totalLines = lines.Length;

        // 1. Detect Fenced Code Blocks (``` ... ```)
        int codeStart = -1;
        string codeLang = "";
        string codeHeader = "";

        for (int i = 0; i < totalLines; i++)
        {
            string line = lines[i].TrimEnd();

            if (codeStart == -1)
            {
                if (line.StartsWith("```"))
                {
                    var m = CodeFenceOpenRegex.Match(line.Trim());
                    codeLang = m.Success ? m.Groups[1].Value : "";
                    codeHeader = line.Trim();
                    codeStart = i + 1; // 1-indexed
                }
            }
            else
            {
                if (line.Trim() == "```")
                {
                    int codeEnd = i + 1; // 1-indexed
                    if (codeEnd > codeStart)
                    {
                        regions.Add(new EditorFoldRegion
                        {
                            StartLine = codeStart,
                            EndLine = codeEnd,
                            Type = EditorFoldType.CodeBlock,
                            Language = codeLang,
                            Header = codeHeader
                        });
                    }
                    codeStart = -1;
                }
            }
        }

        // 2. Detect Feature Blocks (::: ... :::)
        int featStart = -1;
        string featHeader = "";

        for (int i = 0; i < totalLines; i++)
        {
            string line = lines[i].TrimEnd();

            if (featStart == -1)
            {
                if (line.StartsWith(":::"))
                {
                    featHeader = line.Trim();
                    featStart = i + 1;
                }
            }
            else
            {
                if (line.Trim() == ":::")
                {
                    int featEnd = i + 1;
                    if (featEnd > featStart)
                    {
                        regions.Add(new EditorFoldRegion
                        {
                            StartLine = featStart,
                            EndLine = featEnd,
                            Type = EditorFoldType.FeatureBlock,
                            Header = featHeader
                        });
                    }
                    featStart = -1;
                }
            }
        }

        // 3. Detect Markdown Headings (# ... to next heading of equal or higher level)
        for (int i = 0; i < totalLines; i++)
        {
            string line = lines[i].TrimEnd();
            var hm = HeadingRegex.Match(line);
            if (hm.Success)
            {
                int level = hm.Groups[1].Value.Length;
                int start = i + 1;
                int end = totalLines;

                for (int j = i + 1; j < totalLines; j++)
                {
                    var nextHm = HeadingRegex.Match(lines[j].TrimEnd());
                    if (nextHm.Success && nextHm.Groups[1].Value.Length <= level)
                    {
                        end = j;
                        break;
                    }
                }

                if (end > start)
                {
                    regions.Add(new EditorFoldRegion
                    {
                        StartLine = start,
                        EndLine = end,
                        Type = EditorFoldType.HeadingSection,
                        Header = line
                    });
                }
            }
        }

        return regions.OrderBy(r => r.StartLine).ToList();
    }

    /// <summary>
    /// Folds the specified region in the editor text into a compact interactive folded chip.
    /// </summary>
    public static string FoldRegion(string markdownText, EditorFoldRegion region)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
            return markdownText;

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (region.StartLine < 1 || region.EndLine > lines.Length || region.StartLine >= region.EndLine)
            return markdownText;

        var foldedLines = lines.Skip(region.StartLine - 1).Take(region.EndLine - region.StartLine + 1).ToArray();
        string innerContent = string.Join("\n", foldedLines);
        string base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerContent));

        string typeStr = region.Type.ToString().ToLowerInvariant();
        string marker = $"<!-- FOLDED:{typeStr}:{region.Language}:{region.LineCount}:{base64Payload} -->";
        string placeholder = $"{region.Header} /* ▾ [{region.LineCount} lines folded] */ {marker}";

        var result = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            if (lineNum < region.StartLine || lineNum > region.EndLine)
            {
                result.Add(lines[i]);
            }
            else if (lineNum == region.StartLine)
            {
                result.Add(placeholder);
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Unfolds a folded region at the given line number.
    /// </summary>
    public static string UnfoldRegion(string markdownText, int lineIndex)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
            return markdownText;

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lineIndex < 1 || lineIndex > lines.Length)
            return markdownText;

        string targetLine = lines[lineIndex - 1];
        var match = FoldMarkerRegex.Match(targetLine);
        if (!match.Success)
            return markdownText;

        string base64Payload = match.Groups[4].Value;
        string restoredText = Encoding.UTF8.GetString(Convert.FromBase64String(base64Payload));
        var restoredLines = restoredText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var result = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lineIndex - 1)
            {
                result.AddRange(restoredLines);
            }
            else
            {
                result.Add(lines[i]);
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Toggles fold/unfold state at the line corresponding to the current cursor position.
    /// </summary>
    public static string ToggleFoldAtLine(string markdownText, int lineIndex)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
            return markdownText;

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lineIndex < 1 || lineIndex > lines.Length)
            return markdownText;

        string targetLine = lines[lineIndex - 1];
        if (FoldMarkerRegex.IsMatch(targetLine))
        {
            return UnfoldRegion(markdownText, lineIndex);
        }

        var regions = DetectFoldableRegions(markdownText);
        var region = regions.FirstOrDefault(r => r.StartLine == lineIndex)
                  ?? regions.FirstOrDefault(r => lineIndex >= r.StartLine && lineIndex <= r.EndLine);

        if (region != null)
        {
            return FoldRegion(markdownText, region);
        }

        return markdownText;
    }

    /// <summary>
    /// Folds all code blocks in the markdown document into snappy compact chips.
    /// </summary>
    public static string FoldAllCodeBlocks(string markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
            return markdownText;

        var regions = DetectFoldableRegions(markdownText)
            .Where(r => r.Type == EditorFoldType.CodeBlock || r.Type == EditorFoldType.FeatureBlock)
            .OrderByDescending(r => r.StartLine)
            .ToList();

        string current = markdownText;
        foreach (var r in regions)
        {
            current = FoldRegion(current, r);
        }

        return current;
    }

    /// <summary>
    /// Unfolds all folded regions in the markdown document back to full source code.
    /// </summary>
    public static string UnfoldAll(string markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
            return markdownText;

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var result = new List<string>();

        foreach (var line in lines)
        {
            var match = FoldMarkerRegex.Match(line);
            if (match.Success)
            {
                string base64Payload = match.Groups[4].Value;
                string restoredText = Encoding.UTF8.GetString(Convert.FromBase64String(base64Payload));
                result.AddRange(restoredText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None));
            }
            else
            {
                result.Add(line);
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Returns the number of currently folded regions in the document.
    /// </summary>
    public static int GetFoldedCount(string markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
            return 0;

        return FoldMarkerRegex.Matches(markdownText).Count;
    }
}
