using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Core.Kanban;

namespace MarkSmith.Services;

/// <summary>
/// Normalizes :::kanban fenced container blocks into HTML structure for live preview rendering.
/// </summary>
public static class KanbanNormalizer
{
    private static readonly Regex Opener = new(@"^\s*:::+\s*kanban(?:\s+(?<attrs>.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Closer = new(@"^\s*:::+\s*$", RegexOptions.Compiled);

    public static string Apply(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains(":::kanban", StringComparison.OrdinalIgnoreCase))
            return markdown;

        var lines = markdown.Split('\n');
        var outLines = new List<string>(lines.Length + 8);
        bool inCode = false;
        string? fence = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Skip fenced code blocks
            if (!inCode && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                inCode = true;
                fence = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                outLines.Add(line);
                continue;
            }
            if (inCode)
            {
                if (fence is not null && trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    inCode = false;
                    fence = null;
                }
                outLines.Add(line);
                continue;
            }

            var m = Opener.Match(line);
            if (m.Success)
            {
                var blockLines = new List<string> { line };
                int j = i + 1;
                while (j < lines.Length)
                {
                    blockLines.Add(lines[j]);
                    if (Closer.IsMatch(lines[j]))
                    {
                        break;
                    }
                    j++;
                }
                i = j;

                var rawBlock = string.Join("\n", blockLines);
                var block = KanbanParser.Parse(rawBlock);
                var html = RenderHtmlBoard(block);

                if (outLines.Count > 0 && outLines[^1].Length > 0) outLines.Add("");
                outLines.Add(html);
                outLines.Add("");
                continue;
            }

            outLines.Add(line);
        }

        return string.Join("\n", outLines);
    }

    private static string RenderHtmlBoard(KanbanBlock block)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"kanban-board\">");

        if (!string.IsNullOrWhiteSpace(block.Title))
        {
            sb.AppendLine($"  <div class=\"kanban-board-title\">{System.Net.WebUtility.HtmlEncode(block.Title)}</div>");
        }

        sb.AppendLine("  <div class=\"kanban-columns\">");

        foreach (var col in block.Columns)
        {
            sb.AppendLine("    <div class=\"kanban-column\">");
            sb.AppendLine($"      <div class=\"kanban-column-title\">{System.Net.WebUtility.HtmlEncode(col.Title)}</div>");
            sb.AppendLine("      <div class=\"kanban-cards\">");

            foreach (var card in col.Cards)
            {
                var completedCls = card.IsCompleted == true ? " completed" : "";
                var checkIcon = card.IsCompleted == true ? "☑ " : card.IsCompleted == false ? "☐ " : "";

                sb.Append($"        <div class=\"kanban-card{completedCls}\">");
                sb.Append(checkIcon);
                sb.Append(System.Net.WebUtility.HtmlEncode(card.Text));

                if (card.Tags.Count > 0)
                {
                    foreach (var tag in card.Tags)
                    {
                        sb.Append($" <span class=\"kanban-tag\">#{System.Net.WebUtility.HtmlEncode(tag)}</span>");
                    }
                }

                sb.AppendLine("</div>");
            }

            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");

        return sb.ToString();
    }
}
