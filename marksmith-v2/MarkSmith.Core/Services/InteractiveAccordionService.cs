using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record AccordionSection(string Title, string Body, bool IsDefaultOpen, int StartLine);

/// <summary>
/// Service for parsing collapsible details blocks and transforming them into accessible interactive accordion components.
/// </summary>
public static class InteractiveAccordionService
{
    private static readonly Regex DetailsFenceRegex = new(
        @":::details(?:\s+(?:open\s+)?([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex HtmlDetailsRegex = new(
        @"<details(\s+open)?>\s*<summary>([^<]+)</summary>([\s\S]*?)</details>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts all accordion and collapsible details sections from Markdown.
    /// </summary>
    public static List<AccordionSection> ExtractAccordions(string markdown)
    {
        var list = new List<AccordionSection>();
        if (string.IsNullOrWhiteSpace(markdown))
            return list;

        // 1. :::details fence syntax
        foreach (Match m in DetailsFenceRegex.Matches(markdown))
        {
            string title = m.Groups[1].Success ? m.Groups[1].Value.Trim() : "Details";
            string body = m.Groups[2].Value.Trim();
            bool isOpen = m.Value.StartsWith(":::details open", StringComparison.OrdinalIgnoreCase);

            int lineNum = 1 + (m.Index > 0 ? markdown.Substring(0, m.Index).Split('\n').Length - 1 : 0);
            list.Add(new AccordionSection(title, body, isOpen, lineNum));
        }

        // 2. <details><summary> syntax
        foreach (Match m in HtmlDetailsRegex.Matches(markdown))
        {
            bool isOpen = m.Groups[1].Success;
            string title = m.Groups[2].Value.Trim();
            string body = m.Groups[3].Value.Trim();

            int lineNum = 1 + (m.Index > 0 ? markdown.Substring(0, m.Index).Split('\n').Length - 1 : 0);
            list.Add(new AccordionSection(title, body, isOpen, lineNum));
        }

        return list;
    }

    /// <summary>
    /// Transforms Markdown :::details blocks into interactive HTML accordion elements.
    /// </summary>
    public static string TransformToHtmlAccordions(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return DetailsFenceRegex.Replace(markdown, m =>
        {
            string title = m.Groups[1].Success ? m.Groups[1].Value.Trim() : "Details";
            string body = m.Groups[2].Value.Trim();
            bool isOpen = m.Value.StartsWith(":::details open", StringComparison.OrdinalIgnoreCase);
            string openAttr = isOpen ? " open" : "";

            return $"""
                <details class="ms-accordion"{openAttr}>
                  <summary class="ms-accordion-header">{System.Net.WebUtility.HtmlEncode(title)}</summary>
                  <div class="ms-accordion-body">{body}</div>
                </details>
                """;
        });
    }
}
