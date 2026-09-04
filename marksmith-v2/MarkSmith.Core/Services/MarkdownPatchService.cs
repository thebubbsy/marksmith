using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace MarkSmith.Core.Services;

public enum MarkdownPatchOp
{
    SearchReplace,
    BlockReplace,
    BlockInsertBefore,
    BlockInsertAfter,
    BlockDelete,
    Prepend,
    Append,
    AcceptCriticMarkup,
    RejectCriticMarkup,
    InjectCriticMarkup
}

public class MarkdownPatchOperation
{
    public MarkdownPatchOp Op { get; set; } = MarkdownPatchOp.SearchReplace;
    public string? TargetContent { get; set; }
    public string? ReplacementContent { get; set; }
    public bool AllowMultiple { get; set; } = false;
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public string? HeadingPath { get; set; }
    public int? BlockIndex { get; set; }
    public string? Comment { get; set; }
    public string? Author { get; set; }
}

public class MarkdownPatchRequest
{
    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }
    public string? Content { get; set; }
    public List<MarkdownPatchOperation> Operations { get; set; } = new();
}

public class MarkdownPatchDiagnostic
{
    public string ErrorCode { get; set; } = "";
    public string Message { get; set; } = "";
    public int? LineNumber { get; set; }
    public List<int> CandidateLines { get; set; } = new();
    public string? Suggestion { get; set; }
}

public class MarkdownPatchResult
{
    public bool Success { get; set; }
    public string? NewMarkdown { get; set; }
    public string? ErrorMessage { get; set; }
    public int ModifiedBlocks { get; set; }
    public List<string> AppliedOperations { get; set; } = new();
    public List<MarkdownPatchDiagnostic> Diagnostics { get; set; } = new();

    public static MarkdownPatchResult Ok(string newMarkdown, int modifiedBlocks, List<string> operations) => new()
    {
        Success = true,
        NewMarkdown = newMarkdown,
        ModifiedBlocks = modifiedBlocks,
        AppliedOperations = operations
    };

    public static MarkdownPatchResult Fail(string message, string errorCode = "PATCH_FAILED", int? line = null, List<int>? candidates = null, string? suggestion = null)
    {
        var result = new MarkdownPatchResult
        {
            Success = false,
            ErrorMessage = message
        };
        result.Diagnostics.Add(new MarkdownPatchDiagnostic
        {
            ErrorCode = errorCode,
            Message = message,
            LineNumber = line,
            CandidateLines = candidates ?? new List<int>(),
            Suggestion = suggestion
        });
        return result;
    }
}

public class MarkdownPatchService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .Build();

    private static readonly Regex AdditionRegex = new(@"\{\+\+(.+?)\+\+\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DeletionRegex = new(@"\{--(.+?)--\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SubstitutionRegex = new(@"\{~~(.+?)~>(.+?)~~\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HighlightCommentRegex = new(@"\{==(.+?)==\}\{>>(.+?)<<\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HighlightOnlyRegex = new(@"\{==(.+?)==\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CommentOnlyRegex = new(@"\{>>(.+?)<<\}", RegexOptions.Singleline | RegexOptions.Compiled);

    public MarkdownPatchResult ApplyPatch(MarkdownPatchRequest request)
    {
        string markdown = request.Content ?? "";
        if (!string.IsNullOrEmpty(request.InputPath) && File.Exists(request.InputPath))
        {
            markdown = File.ReadAllText(request.InputPath, Encoding.UTF8);
        }

        if (string.IsNullOrEmpty(markdown) && request.Operations.Count == 0)
        {
            return MarkdownPatchResult.Fail("No content or operations provided.", "EMPTY_REQUEST");
        }

        string current = markdown;
        int totalModified = 0;
        var applied = new List<string>();

        foreach (var op in request.Operations)
        {
            var opResult = ApplySingleOperation(current, op);
            if (!opResult.Success)
            {
                return opResult;
            }
            current = opResult.NewMarkdown ?? current;
            totalModified += opResult.ModifiedBlocks;
            applied.AddRange(opResult.AppliedOperations);
        }

        if (!string.IsNullOrEmpty(request.OutputPath))
        {
            string outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath)) ?? ".";
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(request.OutputPath, current, Encoding.UTF8);
        }

        return MarkdownPatchResult.Ok(current, totalModified, applied);
    }

    public MarkdownPatchResult ApplySingleOperation(string markdown, MarkdownPatchOperation op)
    {
        switch (op.Op)
        {
            case MarkdownPatchOp.SearchReplace:
                return ExecuteSearchReplace(markdown, op);

            case MarkdownPatchOp.BlockReplace:
            case MarkdownPatchOp.BlockInsertBefore:
            case MarkdownPatchOp.BlockInsertAfter:
            case MarkdownPatchOp.BlockDelete:
                return ExecuteAstBlockOperation(markdown, op);

            case MarkdownPatchOp.Prepend:
                {
                    string toPrepend = (op.ReplacementContent ?? "").TrimEnd() + "\n\n";
                    string result = toPrepend + markdown.TrimStart();
                    return MarkdownPatchResult.Ok(result, 1, new List<string> { "Prepended content to document." });
                }

            case MarkdownPatchOp.Append:
                {
                    string toAppend = "\n\n" + (op.ReplacementContent ?? "").TrimStart();
                    string result = markdown.TrimEnd() + toAppend;
                    return MarkdownPatchResult.Ok(result, 1, new List<string> { "Appended content to document." });
                }

            case MarkdownPatchOp.AcceptCriticMarkup:
                {
                    string accepted = AcceptCriticMarkup(markdown);
                    return MarkdownPatchResult.Ok(accepted, 1, new List<string> { "Accepted all CriticMarkup revisions." });
                }

            case MarkdownPatchOp.RejectCriticMarkup:
                {
                    string rejected = RejectCriticMarkup(markdown);
                    return MarkdownPatchResult.Ok(rejected, 1, new List<string> { "Rejected all CriticMarkup revisions." });
                }

            case MarkdownPatchOp.InjectCriticMarkup:
                return ExecuteInjectCriticMarkup(markdown, op);

            default:
                return MarkdownPatchResult.Fail($"Unsupported patch operation: {op.Op}", "UNSUPPORTED_OP");
        }
    }

    private static MarkdownPatchResult ExecuteSearchReplace(string markdown, MarkdownPatchOperation op)
    {
        if (string.IsNullOrEmpty(op.TargetContent))
        {
            return MarkdownPatchResult.Fail("TargetContent must not be empty for SearchReplace.", "EMPTY_TARGET");
        }

        string target = NormalizeLineEndings(op.TargetContent);
        string replacement = op.ReplacementContent != null ? NormalizeLineEndings(op.ReplacementContent) : "";
        string normalized = NormalizeLineEndings(markdown);

        int searchStart = 0;
        int searchEnd = normalized.Length;

        // Line range slicing if specified
        if (op.StartLine.HasValue || op.EndLine.HasValue)
        {
            var lines = normalized.Split('\n');
            int startLine = Math.Max(1, op.StartLine ?? 1);
            int endLine = Math.Min(lines.Length, op.EndLine ?? lines.Length);

            if (startLine > lines.Length || startLine > endLine)
            {
                return MarkdownPatchResult.Fail($"Invalid line range: [{startLine}, {endLine}]. Document has {lines.Length} lines.", "INVALID_LINE_RANGE", startLine);
            }

            // Calculate char offsets for line range
            searchStart = 0;
            for (int i = 0; i < startLine - 1; i++)
            {
                searchStart += lines[i].Length + 1; // +1 for \n
            }

            searchEnd = 0;
            for (int i = 0; i < endLine; i++)
            {
                searchEnd += lines[i].Length + 1;
            }
            searchEnd = Math.Min(normalized.Length, searchEnd);
        }

        string searchScope = normalized.Substring(searchStart, searchEnd - searchStart);

        // Find all occurrences
        var matches = new List<int>();
        int idx = 0;
        while ((idx = searchScope.IndexOf(target, idx, StringComparison.Ordinal)) >= 0)
        {
            matches.Add(searchStart + idx);
            idx += target.Length;
        }

        if (matches.Count == 0)
        {
            // Scan whole document to find if target exists elsewhere for diagnostic suggestions
            var globalMatches = new List<int>();
            int gIdx = 0;
            while ((gIdx = normalized.IndexOf(target, gIdx, StringComparison.Ordinal)) >= 0)
            {
                int line = GetLineNumber(normalized, gIdx);
                globalMatches.Add(line);
                gIdx += target.Length;
            }

            if (globalMatches.Count > 0)
            {
                return MarkdownPatchResult.Fail(
                    $"TargetContent not found in specified range [{op.StartLine}, {op.EndLine}], but found at line(s): {string.Join(", ", globalMatches)}.",
                    "TARGET_OUT_OF_RANGE",
                    op.StartLine,
                    globalMatches,
                    $"Update StartLine/EndLine to include line {globalMatches[0]}."
                );
            }

            return MarkdownPatchResult.Fail("TargetContent not found in document.", "TARGET_NOT_FOUND");
        }

        if (matches.Count > 1 && !op.AllowMultiple)
        {
            var lineNumbers = matches.Select(m => GetLineNumber(normalized, m)).ToList();
            return MarkdownPatchResult.Fail(
                $"TargetContent is ambiguous: found {matches.Count} occurrences at lines: {string.Join(", ", lineNumbers)}. Set AllowMultiple=true or narrow StartLine/EndLine.",
                "AMBIGUOUS_TARGET",
                lineNumbers[0],
                lineNumbers,
                "Provide more surrounding context in TargetContent or specify exact StartLine/EndLine."
            );
        }

        var sb = new StringBuilder(normalized.Length + (replacement.Length - target.Length) * matches.Count);
        int lastPos = 0;
        foreach (int matchIndex in matches)
        {
            sb.Append(normalized.Substring(lastPos, matchIndex - lastPos));
            sb.Append(replacement);
            lastPos = matchIndex + target.Length;
        }
        sb.Append(normalized.Substring(lastPos));

        return MarkdownPatchResult.Ok(sb.ToString(), matches.Count, new List<string> { $"Replaced {matches.Count} occurrence(s) of target text." });
    }

    private static MarkdownPatchResult ExecuteAstBlockOperation(string markdown, MarkdownPatchOperation op)
    {
        string normalized = NormalizeLineEndings(markdown);
        var doc = Markdown.Parse(normalized, Pipeline);

        Block? targetBlock = null;
        int blockIndex = 0;

        if (op.BlockIndex.HasValue)
        {
            if (op.BlockIndex.Value >= 0 && op.BlockIndex.Value < doc.Count)
            {
                targetBlock = doc[op.BlockIndex.Value];
                blockIndex = op.BlockIndex.Value;
            }
            else
            {
                return MarkdownPatchResult.Fail($"BlockIndex {op.BlockIndex.Value} out of range [0, {doc.Count - 1}].", "BLOCK_INDEX_OUT_OF_RANGE");
            }
        }
        else if (!string.IsNullOrEmpty(op.HeadingPath))
        {
            var parts = op.HeadingPath.Split(new[] { '/', '>' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int matchIdx = 0;
            for (int i = 0; i < doc.Count; i++)
            {
                if (doc[i] is HeadingBlock hb)
                {
                    string headingText = normalized.Substring(hb.Span.Start, hb.Span.Length).TrimStart('#', ' ', '\t').Trim();
                    if (string.Equals(headingText, parts[matchIdx], StringComparison.OrdinalIgnoreCase))
                    {
                        matchIdx++;
                        if (matchIdx == parts.Length)
                        {
                            targetBlock = hb;
                            blockIndex = i;
                            break;
                        }
                    }
                }
            }

            if (targetBlock == null)
            {
                return MarkdownPatchResult.Fail($"Heading path '{op.HeadingPath}' not found.", "HEADING_NOT_FOUND");
            }
        }

        if (targetBlock == null)
        {
            return MarkdownPatchResult.Fail("No target selector specified (requires BlockIndex or HeadingPath).", "SELECTOR_MISSING");
        }

        int start = targetBlock.Span.Start;
        int end = targetBlock.Span.End + 1; // Markdig Span.End is inclusive

        string replacement = NormalizeLineEndings(op.ReplacementContent ?? "");

        var sb = new StringBuilder();
        switch (op.Op)
        {
            case MarkdownPatchOp.BlockReplace:
                sb.Append(normalized.Substring(0, start));
                sb.Append(replacement);
                if (end < normalized.Length) sb.Append(normalized.Substring(end));
                return MarkdownPatchResult.Ok(sb.ToString(), 1, new List<string> { $"Replaced block at index {blockIndex}." });

            case MarkdownPatchOp.BlockInsertBefore:
                sb.Append(normalized.Substring(0, start));
                sb.Append(replacement);
                sb.Append("\n\n");
                sb.Append(normalized.Substring(start));
                return MarkdownPatchResult.Ok(sb.ToString(), 1, new List<string> { $"Inserted content before block at index {blockIndex}." });

            case MarkdownPatchOp.BlockInsertAfter:
                sb.Append(normalized.Substring(0, end));
                sb.Append("\n\n");
                sb.Append(replacement);
                if (end < normalized.Length) sb.Append(normalized.Substring(end));
                return MarkdownPatchResult.Ok(sb.ToString(), 1, new List<string> { $"Inserted content after block at index {blockIndex}." });

            case MarkdownPatchOp.BlockDelete:
                sb.Append(normalized.Substring(0, start).TrimEnd('\n', '\r'));
                if (end < normalized.Length)
                {
                    string remainder = normalized.Substring(end).TrimStart('\n', '\r');
                    if (!string.IsNullOrEmpty(remainder))
                    {
                        sb.Append("\n\n").Append(remainder);
                    }
                }
                return MarkdownPatchResult.Ok(sb.ToString(), 1, new List<string> { $"Deleted block at index {blockIndex}." });

            default:
                return MarkdownPatchResult.Fail($"Unsupported block op: {op.Op}", "UNSUPPORTED_OP");
        }
    }

    private static MarkdownPatchResult ExecuteInjectCriticMarkup(string markdown, MarkdownPatchOperation op)
    {
        if (string.IsNullOrEmpty(op.TargetContent) && string.IsNullOrEmpty(op.ReplacementContent))
        {
            return MarkdownPatchResult.Fail("TargetContent or ReplacementContent must be specified for CriticMarkup injection.", "EMPTY_TARGET");
        }

        string target = op.TargetContent ?? "";
        string replacement = op.ReplacementContent ?? "";
        string comment = !string.IsNullOrEmpty(op.Comment) ? $"{{>>{op.Comment}<<}}" : "";

        string criticToken;
        if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(replacement))
        {
            criticToken = $"{{~~{target}~>{replacement}~~}}{comment}";
        }
        else if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(replacement))
        {
            criticToken = $"{{++{replacement}++}}{comment}";
        }
        else
        {
            criticToken = $"{{--{target}--}}{comment}";
        }

        if (!string.IsNullOrEmpty(target) && markdown.Contains(target))
        {
            var searchOp = new MarkdownPatchOperation
            {
                Op = MarkdownPatchOp.SearchReplace,
                TargetContent = target,
                ReplacementContent = criticToken,
                AllowMultiple = op.AllowMultiple,
                StartLine = op.StartLine,
                EndLine = op.EndLine
            };
            return ExecuteSearchReplace(markdown, searchOp);
        }

        // If target was empty (pure addition), append/prepend or replace at line
        string result = markdown.TrimEnd() + "\n\n" + criticToken;
        return MarkdownPatchResult.Ok(result, 1, new List<string> { "Injected CriticMarkup addition." });
    }

    public static string AcceptCriticMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Substitutions: {~~old~>new~~} -> new
        text = SubstitutionRegex.Replace(text, "$2");

        // Additions: {++added++} -> added
        text = AdditionRegex.Replace(text, "$1");

        // Deletions: {--deleted--} -> (removed)
        text = DeletionRegex.Replace(text, "");

        // Highlight + Comment: {==highlighted==}{>>comment<<} -> highlighted
        text = HighlightCommentRegex.Replace(text, "$1");

        // Highlight only: {==highlighted==} -> highlighted
        text = HighlightOnlyRegex.Replace(text, "$1");

        // Comments only: {>>comment<<} -> (removed)
        text = CommentOnlyRegex.Replace(text, "");

        return text;
    }

    public static string RejectCriticMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Substitutions: {~~old~>new~~} -> old
        text = SubstitutionRegex.Replace(text, "$1");

        // Additions: {++added++} -> (removed)
        text = AdditionRegex.Replace(text, "");

        // Deletions: {--deleted--} -> deleted
        text = DeletionRegex.Replace(text, "$1");

        // Highlight + Comment: {==highlighted==}{>>comment<<} -> highlighted
        text = HighlightCommentRegex.Replace(text, "$1");

        // Highlight only: {==highlighted==} -> highlighted
        text = HighlightOnlyRegex.Replace(text, "$1");

        // Comments only: {>>comment<<} -> (removed)
        text = CommentOnlyRegex.Replace(text, "");

        return text;
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static int GetLineNumber(string text, int charIndex)
    {
        int line = 1;
        for (int i = 0; i < charIndex && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }
}
