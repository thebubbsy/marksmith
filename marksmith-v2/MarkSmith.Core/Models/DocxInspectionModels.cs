using System;
using System.Collections.Generic;

namespace MarkSmith.Models;

public sealed record DocxStructureReport
{
    public string Title { get; init; } = "";
    public string Creator { get; init; } = "";
    public string LastModifiedBy { get; init; } = "";
    public string Revision { get; init; } = "";
    public DateTime? CreatedDate { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public int TotalParagraphs { get; init; }
    public int TotalTables { get; init; }
    public int TotalSections { get; init; }
    public int TotalComments { get; init; }
    public int TotalRevisions { get; init; }
    public int TotalMedia { get; init; }
    public bool HasEmbeddedSource { get; init; }
    public IReadOnlyList<SectionSummary> Sections { get; init; } = Array.Empty<SectionSummary>();
    public IReadOnlyList<BlockSummary> Blocks { get; init; } = Array.Empty<BlockSummary>();
    public IReadOnlyList<RevisionSummary> Revisions { get; init; } = Array.Empty<RevisionSummary>();
    public IReadOnlyList<CommentSummary> Comments { get; init; } = Array.Empty<CommentSummary>();
    public IReadOnlyList<string> StylesUsed { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MediaSummary> Media { get; init; } = Array.Empty<MediaSummary>();
}

public sealed record BlockSummary
{
    public int Index { get; init; }
    public string? ParaId { get; init; }
    public string? TextId { get; init; }
    public string? StyleId { get; init; }
    public int? HeadingLevel { get; init; }
    public string? HeadingPath { get; init; }
    public string Text { get; init; } = "";
    public string TextPreview => Text;
    public string? Xml { get; init; }
    public bool HasRevisions { get; init; }
    public bool HasComments { get; init; }
    public IReadOnlyList<string> CommentIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Bookmarks { get; init; } = Array.Empty<string>();
    public TableSummary? TableInfo { get; init; }
}


public sealed record TableSummary
{
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public IReadOnlyList<TableCellSummary> Cells { get; init; } = Array.Empty<TableCellSummary>();
}

public sealed record TableCellSummary
{
    public int Row { get; init; }
    public int Column { get; init; }
    public string Text { get; init; } = "";
}

public sealed record RevisionSummary
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = ""; // "Insert" or "Delete"
    public string Author { get; init; } = "";
    public DateTime? Date { get; init; }
    public string Text { get; init; } = "";
    public string? ParaId { get; init; }
}

public sealed record CommentSummary
{
    public string Id { get; init; } = "";
    public string Author { get; init; } = "";
    public string? Initials { get; init; }
    public DateTime? Date { get; init; }
    public string? AnchorText { get; init; }
    public string CommentText { get; init; } = "";
}

public sealed record SectionSummary
{
    public int Index { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    public string Orientation { get; init; } = "Portrait";
    public double MarginTop { get; init; }
    public double MarginBottom { get; init; }
    public double MarginLeft { get; init; }
    public double MarginRight { get; init; }
}

public sealed record MediaSummary
{
    public string RelId { get; init; } = "";
    public string PartName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long SizeBytes { get; init; }
}

public sealed record DocxInspectionOptions
{
    public bool IncludeText { get; init; } = true;
    public bool IncludeXml { get; init; } = false;
    public int MaxParagraphs { get; init; } = 500;
    public bool FilterRevisions { get; init; } = false;
    public bool FilterComments { get; init; } = false;
}
