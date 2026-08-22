using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Analytics;

public enum DiffLineKind { Equal, Inserted, Deleted }
public record DiffLinePair(string? OldText, string? NewText, DiffLineKind Kind, int? OldLineNum, int? NewLineNum);

public class SideBySideDiffModel
{
    public string Title { get; set; } = "Document Comparison";
    public string OldLabel { get; set; } = "Original";
    public string NewLabel { get; set; } = "Modified";
    public List<DiffLinePair> Pairs { get; } = new();
}

/// <summary>
/// Service for parsing comparative document versions and rendering responsive side-by-side synchronized HTML diff tables.
/// </summary>
public static class DocumentSideBySideDiffService
{
    private static readonly Regex DiffFenceRegex = new(
        @":::diff-view(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    /// <summary>
    /// Transforms all :::diff-view blocks into responsive dual-column HTML comparative tables.
    /// </summary>
    public static string TransformDiffViews(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        if (!markdown.Contains(":::diff-view", StringComparison.OrdinalIgnoreCase))
            return markdown;

        return DiffFenceRegex.Replace(markdown, match =>
        {
            string title = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "Document Comparison";
            string body = match.Groups[2].Value;

            var model = ParseDiffView(body, title);
            return RenderDiffHtml(model);
        });
    }

    public static SideBySideDiffModel ParseDiffView(string body, string title)
    {
        var model = new SideBySideDiffModel { Title = title };
        var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var oldLines = new List<string>();
        var newLines = new List<string>();
        bool inOld = false, inNew = false;

        foreach (var l in lines)
        {
            if (l.StartsWith("<<<", StringComparison.OrdinalIgnoreCase))
            {
                inOld = true;
                inNew = false;
                string label = l.TrimStart('<').Trim();
                if (!string.IsNullOrEmpty(label)) model.OldLabel = label;
            }
            else if (l.StartsWith("===", StringComparison.OrdinalIgnoreCase))
            {
                inOld = false;
                inNew = true;
                string label = l.TrimStart('=').Trim();
                if (!string.IsNullOrEmpty(label)) model.NewLabel = label;
            }
            else if (l.StartsWith(">>>", StringComparison.OrdinalIgnoreCase))
            {
                inOld = false;
                inNew = false;
            }
            else if (inOld)
            {
                oldLines.Add(l);
            }
            else if (inNew)
            {
                newLines.Add(l);
            }
        }

        // Compute alignment
        int max = Math.Max(oldLines.Count, newLines.Count);
        for (int i = 0; i < max; i++)
        {
            string? oldText = i < oldLines.Count ? oldLines[i] : null;
            string? newText = i < newLines.Count ? newLines[i] : null;

            if (oldText == newText)
            {
                model.Pairs.Add(new DiffLinePair(oldText, newText, DiffLineKind.Equal, i + 1, i + 1));
            }
            else
            {
                DiffLineKind kind = (oldText != null && newText != null) ? DiffLineKind.Inserted
                    : (oldText != null ? DiffLineKind.Deleted : DiffLineKind.Inserted);
                model.Pairs.Add(new DiffLinePair(oldText, newText, kind, oldText != null ? i + 1 : null, newText != null ? i + 1 : null));
            }
        }

        return model;
    }

    private static string RenderDiffHtml(SideBySideDiffModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"ms-diff-container\">");
        sb.AppendLine($"  <div class=\"ms-diff-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</div>");
        sb.AppendLine("  <table class=\"ms-diff-table\">");
        sb.AppendLine($"    <thead><tr><th colspan=\"2\">{System.Net.WebUtility.HtmlEncode(model.OldLabel)}</th><th colspan=\"2\">{System.Net.WebUtility.HtmlEncode(model.NewLabel)}</th></tr></thead>");
        sb.AppendLine("    <tbody>");

        foreach (var p in model.Pairs)
        {
            string leftClass = p.Kind == DiffLineKind.Deleted ? "diff-del" : "";
            string rightClass = p.Kind == DiffLineKind.Inserted ? "diff-add" : "";

            sb.AppendLine("      <tr>");
            sb.AppendLine($"        <td class=\"diff-line-num\">{(p.OldLineNum?.ToString() ?? "")}</td>");
            sb.AppendLine($"        <td class=\"diff-text {leftClass}\"><code>{System.Net.WebUtility.HtmlEncode(p.OldText ?? "")}</code></td>");
            sb.AppendLine($"        <td class=\"diff-line-num\">{(p.NewLineNum?.ToString() ?? "")}</td>");
            sb.AppendLine($"        <td class=\"diff-text {rightClass}\"><code>{System.Net.WebUtility.HtmlEncode(p.NewText ?? "")}</code></td>");
            sb.AppendLine("      </tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private static class DiffLineKindExtensions
    {
        public static DiffLineKind ModifiedOrBoth(string a, string b) => DiffLineKind.Inserted;
    }
}
