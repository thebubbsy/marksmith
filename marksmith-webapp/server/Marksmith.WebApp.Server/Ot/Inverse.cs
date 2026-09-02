namespace MarkSmith.WebApp.Server.Ot;

/// <summary>
/// Server-side inversion of operations for undo (v1).
/// Resolves an inverse operation from a previously sequenced and applied log entry.
/// </summary>
public static class Inverse
{
    /// <summary>
    /// Returns the inverse of <paramref name="op"/>, or <c>null</c> if no safe inverse exists in v1.
    /// </summary>
    public static Operation? For(Operation op)
    {
        var id = $"inv-{Guid.NewGuid():N}";
        return op.Type switch
        {
            OpType.InsertText => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.DeleteText,
                Block = op.Block,
                Offset = op.Offset,
                Length = op.Text?.Length ?? op.Length ?? 0,
            },
            OpType.DeleteText => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.InsertText,
                Block = op.Block,
                Offset = op.Offset,
                Text = op.Text ?? "",
            },
            OpType.InsertParagraph => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.DeleteParagraph,
                Block = op.Block,
            },
            OpType.DeleteParagraph => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.InsertParagraph,
                Block = op.Block,
                Style = op.Style ?? "Normal",
            },
            OpType.InsertTable => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.DeleteTable,
                Block = op.Block,
            },
            OpType.InsertTableRow => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.DeleteTableRow,
                Block = op.Block,
                Row = op.Row,
            },
            OpType.InsertImage when op.ImageId is not null => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.DeleteImage,
                ImageId = op.ImageId,
            },
            OpType.InsertHyperlink => new Operation
            {
                Id = id,
                ClientId = op.ClientId,
                Type = OpType.DeleteHyperlink,
                Block = op.Block,
                Offset = op.Offset,
                Length = op.Text?.Length ?? op.Length ?? 0,
            },
            _ => null,
        };
    }
}
