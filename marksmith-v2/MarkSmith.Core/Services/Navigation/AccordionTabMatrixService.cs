using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Navigation;

public record MatrixTabCategory(string Title, List<List<string>> Rows);

public class AccordionMatrixModel
{
    public string Title { get; set; } = "Matrix";
    public List<string> Headers { get; } = new();
    public List<MatrixTabCategory> Categories { get; } = new();
}

/// <summary>
/// Service that transforms complex grouped table matrices into responsive hybrid accordion-tab components.
/// </summary>
public static class AccordionTabMatrixService
{
    private static readonly Regex MatrixFenceRegex = new(
        @":::matrix-tabs(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a matrix-tabs block and converts it into interactive HTML tabs with collapsible accordion fallback.
    /// </summary>
    public static string TransformMatrixTabs(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return MatrixFenceRegex.Replace(markdown, match =>
        {
            string title = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "Feature Matrix";
            string body = match.Groups[2].Value.Trim();

            var model = ParseMatrix(body, title);
            return RenderMatrixHtml(model);
        });
    }

    public static AccordionMatrixModel ParseMatrix(string body, string title)
    {
        var model = new AccordionMatrixModel { Title = title };
        var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        MatrixTabCategory? curCategory = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("###", StringComparison.OrdinalIgnoreCase))
            {
                string catName = line.TrimStart('#').Trim();
                curCategory = new MatrixTabCategory(catName, new List<List<string>>());
                model.Categories.Add(curCategory);
            }
            else if (line.StartsWith("|") && line.EndsWith("|"))
            {
                var trimmedLine = line.Trim('|');
                var cells = trimmedLine.Split('|').Select(c => c.Trim()).ToList();
                if (cells.Count > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch == '-' || ch == ':' || ch == ' ')))
                {
                    continue; // skip separator row
                }

                if (model.Headers.Count == 0)
                {
                    model.Headers.AddRange(cells);
                }
                else if (curCategory != null)
                {
                    curCategory.Rows.Add(cells);
                }
            }
        }

        return model;
    }

    private static string RenderMatrixHtml(AccordionMatrixModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<div class=\"ms-matrix-container\">");
        sb.AppendLine($"  <h4 class=\"ms-matrix-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</h4>");

        sb.AppendLine("  <div class=\"ms-matrix-accordion-group\">");
        for (int i = 0; i < model.Categories.Count; i++)
        {
            var cat = model.Categories[i];
            string openAttr = i == 0 ? " open" : "";

            sb.AppendLine($"    <details class=\"ms-matrix-category\"{openAttr}>");
            sb.AppendLine($"      <summary class=\"ms-matrix-cat-header\">{System.Net.WebUtility.HtmlEncode(cat.Title)} ({cat.Rows.Count} items)</summary>");
            sb.AppendLine("      <div class=\"ms-matrix-table-wrap\">");
            sb.AppendLine("        <table class=\"ms-matrix-table\">");

            if (model.Headers.Count > 0)
            {
                sb.AppendLine("          <thead><tr>" + string.Join("", model.Headers.Select(h => $"<th>{System.Net.WebUtility.HtmlEncode(h)}</th>")) + "</tr></thead>");
            }

            sb.AppendLine("          <tbody>");
            foreach (var r in cat.Rows)
            {
                sb.AppendLine("            <tr>" + string.Join("", r.Select(c => $"<td>{System.Net.WebUtility.HtmlEncode(c)}</td>")) + "</tr>");
            }
            sb.AppendLine("          </tbody>");
            sb.AppendLine("        </table>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </details>");
        }
        sb.AppendLine("  </div>");

        sb.AppendLine("</div>");
        return sb.ToString();
    }
}
