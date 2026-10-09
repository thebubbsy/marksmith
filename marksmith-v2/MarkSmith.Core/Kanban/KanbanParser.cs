using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Kanban;

/// <summary>
/// Parser for :::kanban Markdown container blocks into Kanban AST data structures.
/// </summary>
public static class KanbanParser
{
    private static readonly Regex HeaderRegex = new(@"^\s*#{1,6}\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s*(?:[-*+]\s+|\d+[\.\)]\s+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex CheckboxRegex = new(@"^\[([ xX])\]\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"(?<=^|\s)#([a-zA-Z0-9_\-]+)\b", RegexOptions.Compiled);

    // Matches KanbanNormalizer's own opener/closer regexes, which (like other ::: containers)
    // allow 3-OR-MORE colons so a kanban block can be fenced with extra colons when nested inside
    // another ::: container. Kept in sync with that flexibility here; a plain "StartsWith(3
    // colons)" check would silently fail to strip a "::::kanban" / "::::" pair, leaking the raw
    // closing marker into the last card's text and skipping the opener's attributes entirely.
    private static readonly Regex OpenerRegex = new(@"^\s*:::+\s*kanban\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CloserRegex = new(@"^:::+$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a raw :::kanban block string or block inner content into a KanbanBlock AST.
    /// </summary>
    public static KanbanBlock Parse(string rawText, string? innerContent = null, Dictionary<string, string>? attributes = null)
    {
        var block = new KanbanBlock
        {
            RawText = rawText
        };

        if (attributes != null)
        {
            foreach (var kv in attributes)
            {
                block.Attributes[kv.Key] = kv.Value;
            }
        }

        List<string> lines;
        if (OpenerRegex.IsMatch(rawText.TrimStart()))
        {
            var allLines = rawText.Split('\n');
            var firstLine = allLines[0].TrimEnd('\r');

            // Parse attributes from the opener line if not already supplied
            if (attributes == null || attributes.Count == 0)
            {
                var attrMatches = Regex.Matches(firstLine, @"(\w+)=(?:""([^""]*)""|(\S+))");
                foreach (Match m in attrMatches)
                {
                    var key = m.Groups[1].Value;
                    var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                    block.Attributes[key] = val;
                }
            }

            lines = GetInnerLines(rawText);
        }
        else
        {
            lines = (innerContent ?? rawText).Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .ToList();
        }

        if (block.Attributes.TryGetValue("title", out var titleAttr))
        {
            block.Title = titleAttr;
        }

        KanbanColumn? currentColumn = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Skip empty lines or closing ::: marker
            if (string.IsNullOrWhiteSpace(trimmed) || CloserRegex.IsMatch(trimmed))
                continue;

            // Level 1 Node: Column header (# Title or ## Title)
            var headerMatch = HeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                var colTitle = headerMatch.Groups[1].Value.Trim();
                currentColumn = new KanbanColumn
                {
                    Title = colTitle,
                    Index = block.Columns.Count
                };
                block.Columns.Add(currentColumn);
                continue;
            }

            // Level 2 Node: Bullet point card (- Task 1 or * Task 2 or + Task 3)
            var bulletMatch = BulletRegex.Match(line);
            if (bulletMatch.Success)
            {
                var cardBody = bulletMatch.Groups[1].Value.Trim();

                // If no column header has been encountered yet, create a default backlog column
                if (currentColumn == null)
                {
                    currentColumn = new KanbanColumn
                    {
                        Title = "Backlog",
                        Index = block.Columns.Count
                    };
                    block.Columns.Add(currentColumn);
                }

                var card = ParseCard(cardBody, line, currentColumn.Cards.Count);
                currentColumn.Cards.Add(card);
                continue;
            }

            // Multiline card extension or non-bullet text
            if (currentColumn != null && currentColumn.Cards.Count > 0)
            {
                var lastCard = currentColumn.Cards[^1];
                lastCard.Text += "\n" + trimmed;
                lastCard.Raw += "\n" + line;
            }
            else if (currentColumn != null)
            {
                // Standalone text line inside a column treated as a card
                var card = ParseCard(trimmed, line, currentColumn.Cards.Count);
                currentColumn.Cards.Add(card);
            }
        }

        return block;
    }

    // Local equivalent of DetectorHelpers.GetInnerLines that recognizes a 3-OR-MORE colon closer
    // (":::+" — matching OpenerRegex above) instead of only the exact 3-colon "::: ". Everything
    // else mirrors that helper's behavior: the opener line (index 0) is always skipped, and a
    // closer on the last line, or on the second-to-last line followed by a single trailing blank
    // line, is dropped rather than treated as card content.
    private static List<string> GetInnerLines(string rawText)
    {
        var lines = rawText.TrimStart('\r', '\n').Split('\n');
        var result = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (i == lines.Length - 1 && CloserRegex.IsMatch(trimmed.Trim())) break;
            if (i == lines.Length - 2 && CloserRegex.IsMatch(trimmed.Trim()) && string.IsNullOrWhiteSpace(lines[^1])) break;
            result.Add(trimmed);
        }
        return result;
    }

    private static KanbanCard ParseCard(string cardBody, string rawLine, int index)
    {
        bool? isCompleted = null;
        var cleanText = cardBody;

        var checkMatch = CheckboxRegex.Match(cardBody);
        if (checkMatch.Success)
        {
            var mark = checkMatch.Groups[1].Value;
            isCompleted = mark.Equals("x", StringComparison.OrdinalIgnoreCase);
            cleanText = checkMatch.Groups[2].Value.Trim();
        }

        var tags = new List<string>();
        foreach (Match tagMatch in TagRegex.Matches(cleanText))
        {
            tags.Add(tagMatch.Groups[1].Value);
        }

        return new KanbanCard
        {
            Text = cleanText,
            Raw = rawLine,
            Index = index,
            IsCompleted = isCompleted,
            Tags = tags
        };
    }
}
