namespace MarkSmith.WebApp.Server.Ot;

/// <summary>
/// One-way operational transformation used by the server-sequenced model.
///
/// The server is the single sequencer: it owns the operation log. When a batch arrives with a
/// base sequence number, the server transforms each incoming operation against every operation
/// that was sequenced after that base (i.e. concurrent operations from other clients), then
/// appends the adjusted operation to the log. Clients never transform against the server; they
/// only rebase their *pending* local operations against remote operations that arrive first
/// (mirrored in TypeScript on the client).
///
/// <see cref="Transform"/> returns a new <see cref="Operation"/> adjusted as if <paramref name="prior"/>
/// had already been applied, or <c>null</c> when the operation becomes a harmless no-op
/// (e.g. deleting a paragraph another user already deleted, or formatting a range that was
/// already deleted). No-ops are still sequenced and acknowledged -- the intent is satisfied --
/// but they mutate nothing. Genuinely malformed operations (out-of-range indices, missing
/// payload) are NOT handled here; they are rejected by validation in the session.
///
/// Model: the document is a list of blocks (paragraphs and tables). Text-level operations
/// address (block, offset, length) where offset/length are character offsets inside the block's
/// text. Block-level operations address a block index. ID-based operations (images, comments,
/// track changes) are position-independent and only collide with their own kind.
/// </summary>
public static class OtTransform
{
    /// <summary>
    /// Transform <paramref name="op"/> against <paramref name="prior"/> (which was applied
    /// first). Returns the adjusted operation, or null when the op is a satisfied no-op.
    /// </summary>
    public static Operation? Transform(Operation op, Operation prior)
    {
        // Undo/redo never travel through transforms (server resolves them into real ops first).
        if (op.Type is OpType.Undo or OpType.Redo) return op;
        if (prior.Type is OpType.Undo or OpType.Redo) return op;

        return prior.Type switch
        {
            OpType.InsertText => TransformAgainstInsertText(op, prior),
            OpType.DeleteText => TransformAgainstDeleteText(op, prior),
            OpType.InsertParagraph or OpType.InsertTable => TransformAgainstBlockInsert(op, prior),
            OpType.DeleteParagraph or OpType.DeleteTable => TransformAgainstBlockDelete(op, prior),
            OpType.InsertTableRow or OpType.DeleteTableRow => TransformAgainstTableRow(op, prior),
            OpType.DeleteImage => TransformAgainstDeleteImage(op, prior),
            OpType.ResolveComment => TransformAgainstResolveComment(op, prior),
            OpType.AcceptTrackChange or OpType.RejectTrackChange => TransformAgainstTrackDecision(op, prior),
            _ => op, // ApplyFormatting/InsertImage/AddComment/ApplyTrackChange/InsertHyperlink: no position impact
        };
    }

    /// <summary>Transform <paramref name="op"/> against a whole list of prior ops, in order.</summary>
    public static Operation? TransformAgainst(Operation op, IEnumerable<Operation> priors)
    {
        var current = op;
        foreach (var prior in priors)
        {
            current = Transform(current, prior);
            if (current is null) return null;
        }
        return current;
    }

    // ------------------------------------------------------------------ inserts

    private static Operation? TransformAgainstInsertText(Operation op, Operation prior)
    {
        // An insert at prior.offset..prior.offset+len pushes text to the right.
        if (IsTextOp(op) && op.Block == prior.Block)
        {
            var insLen = prior.Text?.Length ?? 0;
            if (prior.Offset <= op.Offset)
            {
                return With(op, offset: (op.Offset ?? 0) + insLen);
            }
            return op;
        }

        if (IsBlockIndexedOp(op) && op.Block == prior.Block)
        {
            // Block-indexed op targeting the same block: an insert inside the block does not
            // change the block index, so nothing shifts.
            return op;
        }

        return op;
    }

    // ------------------------------------------------------------------ deletes

    private static Operation? TransformAgainstDeleteText(Operation op, Operation prior)
    {
        var dStart = prior.Offset ?? 0;
        var dEnd = dStart + (prior.Length ?? 0);

        if (IsTextOp(op) && op.Block == prior.Block)
        {
            var tStart = op.Offset ?? 0;
            var tLen = op.Length ?? (op.Type == OpType.InsertText ? op.Text?.Length ?? 0 : 0);

            return op.Type switch
            {
                // Insert into/after the deleted region snaps to the deletion start;
                // insert before the region is unaffected.
                OpType.InsertText or OpType.InsertImage or OpType.InsertHyperlink =>
                    tStart >= dEnd ? With(op, offset: tStart - (dEnd - dStart))
                    : tStart >= dStart ? With(op, offset: dStart)
                    : op,

                // Range ops (delete/format): shift + shrink across the deleted span.
                OpType.DeleteText or OpType.ApplyFormatting or OpType.DeleteHyperlink or OpType.ApplyTrackChange =>
                    TransformRangeAgainstDelete(op, dStart, dEnd, tStart, tLen),

                _ => op,
            };
        }

        return op;
    }

    private static Operation? TransformRangeAgainstDelete(Operation op, int dStart, int dEnd, int tStart, int tLen)
    {
        var tEnd = tStart + tLen;

        // Deleted region entirely before our range: shift left by the deleted length.
        if (dEnd <= tStart) return With(op, offset: tStart - (dEnd - dStart));

        // Deleted region entirely after our range: unaffected.
        if (dStart >= tEnd) return op;

        // Deleted region entirely covers our range: satisfied no-op.
        if (dStart <= tStart && dEnd >= tEnd) return null;

        // Partial overlap: the surviving portion of our range is the original range minus the
        // chars the prior delete removed. Everything after the deletion shifts left.
        var deletedBeforeStart = Math.Max(0, Math.Min(dEnd, tStart) - dStart);
        var overlapLen = Math.Min(tEnd, dEnd) - Math.Max(tStart, dStart);
        var newStart = tStart - deletedBeforeStart;
        var newLen = tLen - overlapLen;
        return With(op, offset: newStart, length: newLen);
    }

    // ------------------------------------------------------------------ block inserts

    private static Operation? TransformAgainstBlockInsert(Operation op, Operation prior)
    {
        // prior inserted a block at index b (meaning "before the block currently at b").
        // Every block index >= b shifts up by one.
        if (op.Block is { } b && prior.Block is { } pb && b >= pb)
        {
            return With(op, block: b + 1);
        }
        return op;
    }

    // ------------------------------------------------------------------ block deletes

    private static Operation? TransformAgainstBlockDelete(Operation op, Operation prior)
    {
        var pb = prior.Block ?? 0;

        if (op.Type is OpType.InsertParagraph or OpType.InsertTable)
        {
            // Inserting "before the block now at b" keeps meaning index b after the delete.
            if (op.Block is { } b && b >= pb + 1) return With(op, block: b - 1);
            return op;
        }

        if (op.Type is OpType.InsertTableRow)
        {
            // A row insert targets the table at block b; if that table was deleted, no-op.
            if (op.Block == pb) return null;
            if (op.Block is { } b && b >= pb + 1) return With(op, block: b - 1);
            return op;
        }

        if (op.Block is { } ob)
        {
            if (ob == pb)
            {
                // Text ops / row ops / block ops targeting the deleted block are satisfied no-ops.
                return op.Type is OpType.DeleteText or OpType.ApplyFormatting or OpType.DeleteHyperlink or OpType.ApplyTrackChange
                    or OpType.InsertText or OpType.InsertImage or OpType.InsertHyperlink
                    or OpType.DeleteParagraph or OpType.DeleteTable
                        ? null
                        : op;
            }
            if (ob > pb) return With(op, block: ob - 1);
        }
        return op;
    }

    // ------------------------------------------------------------------ table rows

    private static Operation? TransformAgainstTableRow(Operation op, Operation prior)
    {
        var isInsert = prior.Type == OpType.InsertTableRow;
        var pRow = prior.Row ?? 0;

        if (op.Type is OpType.InsertTableRow or OpType.DeleteTableRow && op.Block == prior.Block)
        {
            var r = op.Row ?? 0;
            if (isInsert)
            {
                if (r >= pRow) return With(op, row: r + 1);
            }
            else
            {
                if (r == pRow) return null;          // row already deleted
                if (r > pRow) return With(op, row: r - 1);
            }
        }
        return op;
    }

    // ------------------------------------------------------------------ id collisions

    private static Operation? TransformAgainstDeleteImage(Operation op, Operation prior)
    {
        if (op.Type == OpType.DeleteImage && op.ImageId == prior.ImageId) return null;
        return op;
    }

    private static Operation? TransformAgainstResolveComment(Operation op, Operation prior)
    {
        if (op.Type == OpType.ResolveComment && op.CommentId == prior.CommentId) return null;
        return op;
    }

    private static Operation? TransformAgainstTrackDecision(Operation op, Operation prior)
    {
        // Accept/reject on a change that was already accepted or rejected is a satisfied no-op,
        // including a conflicting decision (one of them wins -- v1 policy: first sequenced wins).
        if (op is { Type: OpType.AcceptTrackChange or OpType.RejectTrackChange } && op.ChangeId == prior.ChangeId) return null;
        return op;
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsTextOp(Operation op) => op.Type is
        OpType.InsertText or OpType.DeleteText or OpType.ApplyFormatting or
        OpType.InsertImage or OpType.InsertHyperlink or OpType.DeleteHyperlink or OpType.ApplyTrackChange;

    private static bool IsBlockIndexedOp(Operation op) => op.Type is
        OpType.InsertParagraph or OpType.DeleteParagraph or OpType.InsertTable or OpType.DeleteTable or
        OpType.InsertTableRow or OpType.DeleteTableRow;

    private static Operation With(Operation op, int? block = null, int? offset = null, int? length = null, int? row = null)
    {
        // The wire contract is immutable; rebuild via the same builder the JSON path uses.
        var b = new OperationBuilder(op.Id, op.ClientId, op.Type)
        {
            Block = block ?? op.Block,
            Offset = offset ?? op.Offset,
            Length = length ?? op.Length,
            Text = op.Text,
            Style = op.Style,
            Rows = op.Rows,
            Cols = op.Cols,
            Row = row ?? op.Row,
            Format = op.Format,
            Alt = op.Alt,
            DataUri = op.DataUri,
            Url = op.Url,
            Width = op.Width,
            Height = op.Height,
            ImageId = op.ImageId,
            Href = op.Href,
            CommentId = op.CommentId,
            Author = op.Author,
            ChangeId = op.ChangeId,
            Kind = op.Kind,
            UptoSeq = op.UptoSeq,
            FromSeq = op.FromSeq,
        };
        return b.Build();
    }
}
