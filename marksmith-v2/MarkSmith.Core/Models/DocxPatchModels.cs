using System;
using System.Collections.Generic;

namespace MarkSmith.Models;

public enum PatchOperation
{
    Replace,
    InsertBefore,
    InsertAfter,
    Delete,
    Append,
    Prepend,
    AddComment,
    AcceptRevision,
    RejectRevision
}

public sealed record TableCellSelector
{
    public int? TableIndex { get; init; }
    public string? TableParaId { get; init; }
    public int Row { get; init; }
    public int Col { get; init; }
}

public sealed record BlockSelector
{
    public string? ParaId { get; init; }
    public int? BodyIndex { get; init; }
    public string? HeadingPath { get; init; }
    public string? BookmarkName { get; init; }
    public string? CommentId { get; init; }
    public TableCellSelector? TableCell { get; init; }
}

public sealed record CommentPayload
{
    public string Author { get; init; } = "Marksmith AI";
    public string? Initials { get; init; }
    public DateTime? Date { get; init; }
    public string Text { get; init; } = "";
}

public sealed record DocxPatchOperationItem
{
    public PatchOperation Op { get; init; } = PatchOperation.Replace;
    public BlockSelector Target { get; init; } = new();
    public string? Content { get; init; }
    public bool TrackChanges { get; init; }
    public string Author { get; init; } = "Marksmith AI";
    public string? Comment { get; init; }
    public CommentPayload? CommentPayload { get; init; }
    public bool PreserveFormatting { get; init; } = true;
}

public sealed record DocxPatchRequest
{
    public string? DocxPath { get; init; }
    public string? OutputPath { get; init; }

    // Support single-operation contract from PROJECT.md
    public BlockSelector Target { get; init; } = new();
    public PatchOperation Operation { get; init; } = PatchOperation.Replace;
    public string? MarkdownContent { get; init; }
    public CommentPayload? Comment { get; init; }
    public bool TrackChanges { get; init; }
    public bool PreserveFormatting { get; init; } = true;

    // Support multi-operation batch requests
    public IReadOnlyList<DocxPatchOperationItem>? Operations { get; init; }

    /// <summary>
    /// Returns the normalized list of operations to execute.
    /// If Operations list is provided and non-empty, returns it; otherwise synthesizes one from single properties.
    /// </summary>
    public IReadOnlyList<DocxPatchOperationItem> GetNormalizedOperations()
    {
        if (Operations != null && Operations.Count > 0)
            return Operations;

        return new[]
        {
            new DocxPatchOperationItem
            {
                Op = Operation,
                Target = Target,
                Content = MarkdownContent,
                TrackChanges = TrackChanges,
                Comment = Comment?.Text,
                CommentPayload = Comment,
                PreserveFormatting = PreserveFormatting
            }
        };
    }
}

public sealed record OperationDetail
{
    public PatchOperation Op { get; init; }
    public bool Success { get; init; }
    public string? TargetParaId { get; init; }
    public string? Message { get; init; }
}

public sealed record PatchResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public int ModifiedBlocks { get; init; }
    public int OperationsApplied { get; init; }
    public IReadOnlyList<OperationDetail> OperationDetails { get; init; } = Array.Empty<OperationDetail>();
    public IReadOnlyList<string> ModifiedParts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }

    public static PatchResult Ok(string? outputPath, int modifiedBlocks, int opsApplied,
        IReadOnlyList<OperationDetail>? details = null, IReadOnlyList<string>? parts = null) =>
        new()
        {
            Success = true,
            OutputPath = outputPath,
            ModifiedBlocks = modifiedBlocks,
            OperationsApplied = opsApplied,
            OperationDetails = details ?? Array.Empty<OperationDetail>(),
            ModifiedParts = parts ?? new[] { "word/document.xml" }
        };

    public static PatchResult Fail(string error, IReadOnlyList<string>? validationErrors = null) =>
        new()
        {
            Success = false,
            ErrorMessage = error,
            ValidationErrors = validationErrors ?? Array.Empty<string>()
        };
}
