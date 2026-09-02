using MarkSmith.WebApp.Server.Ot;
using Xunit;

namespace MarkSmith.WebApp.Tests;

/// <summary>
/// Transform-function correctness: the 10-users-same-paragraph convergence criterion is
/// exercised here at the pure-function level. Every concurrent pair must converge to the same
/// state regardless of arrival order.
/// </summary>
public class TransformTests
{
    private static Operation Op(string id, OpType type, int? block = null, int? offset = null,
        int? length = null, string? text = null, string? style = null,
        string? imageId = null, string? commentId = null, string? changeId = null)
        => new()
        {
            Id = id, ClientId = "u-" + id[^1], Type = type,
            Block = block, Offset = offset, Length = length, Text = text, Style = style,
            ImageId = imageId, CommentId = commentId, ChangeId = changeId,
        };

    // ------------------------------------------------------------ insert vs insert

    [Fact]
    public void InsertText_BeforeExistingInsert_ShiftsOffset()
    {
        // A inserts "X" at 2; B inserts "Y" at 0. After A applies, B's offset stays 0 (before X).
        var a = Op("a", OpType.InsertText, block: 0, offset: 2, text: "X");
        var b = Op("b", OpType.InsertText, block: 0, offset: 0, text: "Y");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(0, bPrime!.Offset);
    }

    [Fact]
    public void InsertText_AfterExistingInsert_ShiftsOffsetByInsertedLength()
    {
        var a = Op("a", OpType.InsertText, block: 0, offset: 2, text: "XY");
        var b = Op("b", OpType.InsertText, block: 0, offset: 3, text: "Z");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(5, bPrime!.Offset); // 3 + len("XY")
    }

    [Fact]
    public void InsertText_AtSameOffsetAsExistingInsert_AppendsAfter()
    {
        var a = Op("a", OpType.InsertText, block: 0, offset: 2, text: "X");
        var b = Op("b", OpType.InsertText, block: 0, offset: 2, text: "Y");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(3, bPrime!.Offset); // same offset => after the first insert
    }

    // ------------------------------------------------------------ insert vs delete

    [Fact]
    public void InsertText_AtDeletedOffset_SnapsToDeletionStart()
    {
        // A deletes [2,4); B inserts at 3 -> insert must land at 2 (deletion start).
        var del = Op("a", OpType.DeleteText, block: 0, offset: 2, length: 2);
        var ins = Op("b", OpType.InsertText, block: 0, offset: 3, text: "Q");

        var insPrime = OtTransform.Transform(ins, del);
        Assert.Equal(2, insPrime!.Offset);
    }

    [Fact]
    public void InsertText_AfterDeletedRange_ShiftsLeftByDeletedLength()
    {
        var del = Op("a", OpType.DeleteText, block: 0, offset: 2, length: 3);
        var ins = Op("b", OpType.InsertText, block: 0, offset: 10, text: "Q");

        var insPrime = OtTransform.Transform(ins, del);
        Assert.Equal(7, insPrime!.Offset);
    }

    [Fact]
    public void InsertText_BeforeDeletedRange_Unaffected()
    {
        var del = Op("a", OpType.DeleteText, block: 0, offset: 5, length: 3);
        var ins = Op("b", OpType.InsertText, block: 0, offset: 2, text: "Q");

        var insPrime = OtTransform.Transform(ins, del);
        Assert.Equal(2, insPrime!.Offset);
    }

    // ------------------------------------------------------------ delete vs delete

    [Fact]
    public void DeleteText_AfterPriorDelete_ShiftsLeft()
    {
        // A deletes [1,2); B deletes [3,4). After A, B's range is [2,3).
        var a = Op("a", OpType.DeleteText, block: 0, offset: 1, length: 1);
        var b = Op("b", OpType.DeleteText, block: 0, offset: 3, length: 1);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(2, bPrime!.Offset);
        Assert.Equal(1, bPrime.Length);
    }

    [Fact]
    public void DeleteText_BeforePriorDelete_Unaffected()
    {
        var a = Op("a", OpType.DeleteText, block: 0, offset: 5, length: 1);
        var b = Op("b", OpType.DeleteText, block: 0, offset: 1, length: 1);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(1, bPrime!.Offset);
    }

    [Fact]
    public void DeleteText_OverlappingPriorDelete_ShrinksToSurvivingPortion()
    {
        // A deletes [2,4); B deletes [3,5). The overlap [3,4) is already gone; the surviving
        // portion of B's range ([4,5)) shifts left to position 2 -> delete [2,1).
        var a = Op("a", OpType.DeleteText, block: 0, offset: 2, length: 2);
        var b = Op("b", OpType.DeleteText, block: 0, offset: 3, length: 2);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(2, bPrime!.Offset);
        Assert.Equal(1, bPrime.Length);
    }

    [Fact]
    public void DeleteText_FullyCoveredByPriorDelete_IsNoOp()
    {
        var a = Op("a", OpType.DeleteText, block: 0, offset: 1, length: 5);
        var b = Op("b", OpType.DeleteText, block: 0, offset: 2, length: 2);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Null(bPrime);
    }

    // ------------------------------------------------------------ formatting

    [Fact]
    public void ApplyFormatting_ShiftsAndShrinksAcrossPriorDelete()
    {
        // A deletes [2,4); B formats [1,5). Surviving format targets: position 1 and the shifted
        // position 4 -> [1,2) after the deletion.
        var a = Op("a", OpType.DeleteText, block: 0, offset: 2, length: 2);
        var b = Op("b", OpType.ApplyFormatting, block: 0, offset: 1, length: 4);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(1, bPrime!.Offset);
        Assert.Equal(2, bPrime.Length);
    }

    [Fact]
    public void ApplyFormatting_OnRangeAfterPriorDelete_ShiftsLeft()
    {
        var a = Op("a", OpType.DeleteText, block: 0, offset: 0, length: 3);
        var b = Op("b", OpType.ApplyFormatting, block: 0, offset: 5, length: 2);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(2, bPrime!.Offset);
        Assert.Equal(2, bPrime.Length);
    }

    // ------------------------------------------------------------ block ops

    [Fact]
    public void InsertParagraph_AfterPriorBlockInsert_ShiftsIndex()
    {
        var a = Op("a", OpType.InsertParagraph, block: 2, style: "Normal");
        var b = Op("b", OpType.InsertParagraph, block: 3, style: "Heading1");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(4, bPrime!.Block);
    }

    [Fact]
    public void InsertParagraph_BeforePriorBlockInsert_Unaffected()
    {
        var a = Op("a", OpType.InsertParagraph, block: 5, style: "Normal");
        var b = Op("b", OpType.InsertParagraph, block: 2, style: "Normal");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(2, bPrime!.Block);
    }

    [Fact]
    public void DeleteParagraph_TargetingDeletedBlock_IsNoOp()
    {
        var a = Op("a", OpType.DeleteParagraph, block: 2);
        var b = Op("b", OpType.DeleteParagraph, block: 2);

        var bPrime = OtTransform.Transform(b, a);
        Assert.Null(bPrime);
    }

    [Fact]
    public void TextOp_IntoDeletedBlock_IsNoOp()
    {
        var a = Op("a", OpType.DeleteParagraph, block: 1);
        var b = Op("b", OpType.InsertText, block: 1, offset: 0, text: "zombie");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Null(bPrime);
    }

    [Fact]
    public void TextOp_IntoBlockAfterDeletedOne_ShiftsIndex()
    {
        var a = Op("a", OpType.DeleteParagraph, block: 1);
        var b = Op("b", OpType.InsertText, block: 4, offset: 0, text: "x");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Equal(3, bPrime!.Block);
    }

    // ------------------------------------------------------------ id-based collisions

    [Fact]
    public void DeleteImage_Twice_SecondIsNoOp()
    {
        var a = Op("a", OpType.DeleteImage, imageId: "img-1");
        var b = Op("b", OpType.DeleteImage, imageId: "img-1");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Null(bPrime);
    }

    [Fact]
    public void ResolveComment_Twice_SecondIsNoOp()
    {
        var a = Op("a", OpType.ResolveComment, commentId: "c-1");
        var b = Op("b", OpType.ResolveComment, commentId: "c-1");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Null(bPrime);
    }

    [Fact]
    public void AcceptTrackChange_ThenReject_RejectIsNoOp()
    {
        var a = Op("a", OpType.AcceptTrackChange, changeId: "ch-1");
        var b = Op("b", OpType.RejectTrackChange, changeId: "ch-1");

        var bPrime = OtTransform.Transform(b, a);
        Assert.Null(bPrime);
    }

    // ------------------------------------------------------------ sequence transform

    [Fact]
    public void TransformAgainst_AppliesPriorsInOrder()
    {
        var ins1 = Op("a", OpType.InsertText, block: 0, offset: 0, text: "AA");
        var ins2 = Op("b", OpType.InsertText, block: 0, offset: 1, text: "B");
        var ins3 = Op("c", OpType.InsertText, block: 0, offset: 2, text: "C");

        // Concurrent window: ins1 then ins2 (both applied). ins3 must land after both:
        // against ins1 (0 <= 2): offset += 2  -> 4
        // against ins2 (1 <= 4): offset += 1  -> 5
        var result = OtTransform.TransformAgainst(ins3, new[] { ins1, ins2 });
        Assert.NotNull(result);
        Assert.Equal(5, result!.Offset);
    }

    [Fact]
    public void TransformAgainst_StopsAtFirstNoOp()
    {
        var del = Op("a", OpType.DeleteText, block: 0, offset: 0, length: 10);
        var del2 = Op("b", OpType.DeleteText, block: 0, offset: 5, length: 2);
        var fmt = Op("c", OpType.ApplyFormatting, block: 0, offset: 1, length: 3);

        var result = OtTransform.TransformAgainst(fmt, new[] { del, del2 });
        Assert.Null(result);
    }
}
