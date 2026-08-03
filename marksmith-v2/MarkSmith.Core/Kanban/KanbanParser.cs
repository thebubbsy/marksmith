using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Core.AdvancedFeatures;

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
        if (rawText.TrimStart().StartsWith(":::kanban", StringComparison.OrdinalIgnoreCase))
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

            lines = DetectorHelpers.GetInnerLines(rawText);
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
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == ":::")
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
