using System;
using System.Collections.Generic;

namespace MdToPdf.Core.Kanban;

/// <summary>
/// Represents a Kanban Card (Level 2 Node) positioned beneath a Kanban Column header.
/// </summary>
public sealed class KanbanCard
{
    /// <summary>
    /// Parsed text content of the card.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Original raw line or block text for this card.
    /// </summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>
    /// 0-based order index within the parent column.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Indicates whether the task card has a task list checkbox and its checked status (e.g. - [x] or - [ ]).
    /// </summary>
    public bool? IsCompleted { get; set; }

    /// <summary>
    /// Extracted tags (e.g., #bug, #feature) present in the card text.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Key-value metadata attributes associated with the card.
    /// </summary>
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a Kanban Column (Level 1 Node) parsed from an H1 header (# Column Title).
/// </summary>
public sealed class KanbanColumn
{
    /// <summary>
    /// Column title parsed from the header line (e.g., "To Do", "In Progress").
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 0-based column order index.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// List of Kanban Cards (Level 2 Nodes) belonging to this column.
    /// </summary>
    public List<KanbanCard> Cards { get; set; } = new();

    /// <summary>
    /// Key-value metadata attributes for the column.
    /// </summary>
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a Kanban Board AST parsed from a :::kanban fenced block.
/// </summary>
public class KanbanBoard
{
    /// <summary>
    /// Title of the Kanban board, parsed from title attribute on the block opener if present (e.g., :::kanban title="Board Title").
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Columns (Level 1 Nodes) contained in this Kanban block.
    /// </summary>
    public List<KanbanColumn> Columns { get; set; } = new();

    /// <summary>
    /// The full raw text of the Kanban block.
    /// </summary>
    public string RawText { get; set; } = string.Empty;

    /// <summary>
    /// Attributes from the opening :::kanban header line.
    /// </summary>
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a Kanban Block AST parsed from a :::kanban fenced block.
/// </summary>
public sealed class KanbanBlock : KanbanBoard
{
}

