using MarkSmith.WebApp.Server.Ot;
using Xunit;

namespace MarkSmith.WebApp.Tests;

public class OperationLogTests
{
    private static Operation Op(string id, OpType type) => new()
    {
        Id = id, ClientId = "u-" + id[^1], Type = type,
        Block = 0, Offset = 0, Text = "x",
    };

    [Fact]
    public void Append_AssignsStrictlyIncreasingSeqs()
    {
        var log = new OperationLog();
        var e1 = log.Append("u1", Op("a", OpType.InsertText), false);
        var e2 = log.Append("u2", Op("b", OpType.InsertText), false);

        Assert.Equal(1, e1.Seq);
        Assert.Equal(2, e2.Seq);
        Assert.Equal(2, log.LastSeq);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void ResumeSeq_ContinuesNumberingFromSnapshot()
    {
        var log = new OperationLog(resumeSeq: 41);
        var e = log.Append("u1", Op("a", OpType.InsertText), false);
        Assert.Equal(42, e.Seq);
    }

    [Fact]
    public void After_ReturnsOnlyEntriesPastBase()
    {
        var log = new OperationLog();
        log.Append("u1", Op("a", OpType.InsertText), false);
        var e2 = log.Append("u2", Op("b", OpType.InsertText), false);
        log.Append("u3", Op("c", OpType.InsertText), false);

        var after = log.After(1);
        Assert.Equal(new[] { 2L, 3L }, after.Select(x => x.Seq));
    }

    [Fact]
    public void RecentByClient_NewestFirst_AndRespectsUptoSeq()
    {
        var log = new OperationLog();
        log.Append("u1", Op("a", OpType.InsertText), false);  // seq 1
        log.Append("u2", Op("b", OpType.InsertText), false);  // seq 2
        log.Append("u1", Op("c", OpType.InsertText), false);  // seq 3
        log.Append("u1", Op("d", OpType.InsertText), false);  // seq 4

        var recent = log.RecentByClient("u1", uptoSeq: 4);
        Assert.Equal(new[] { 4L, 3L, 1L }, recent.Select(x => x.Seq));
    }

    [Fact]
    public void CompactTo_TrimsEntriesUpToSnapshotSeq()
    {
        var log = new OperationLog();
        for (int i = 0; i < 5; i++) log.Append("u1", Op($"op-{i}", OpType.InsertText), false);

        log.CompactTo(3);
        Assert.Equal(2, log.Count);
        Assert.Equal(new[] { 4L, 5L }, log.Entries().Select(x => x.Seq));
        // Seq numbering is unaffected by compaction.
        Assert.Equal(5, log.LastSeq);
    }
}

public class InverseTests
{
    [Fact]
    public void InsertText_Inverse_IsDeleteText_OfSameRange()
    {
        var op = new Operation { Id = "a", ClientId = "u1", Type = OpType.InsertText, Block = 2, Offset = 5, Text = "hello" };
        var inv = Inverse.For(op);
        Assert.NotNull(inv);
        Assert.Equal(OpType.DeleteText, inv!.Type);
        Assert.Equal(2, inv.Block);
        Assert.Equal(5, inv.Offset);
        Assert.Equal(5, inv.Length);
    }

    [Fact]
    public void DeleteText_Inverse_RestoresText()
    {
        var op = new Operation { Id = "a", ClientId = "u1", Type = OpType.DeleteText, Block = 1, Offset = 3, Length = 2, Text = "ab" };
        var inv = Inverse.For(op);
        Assert.Equal(OpType.InsertText, inv!.Type);
        Assert.Equal("ab", inv.Text);
        Assert.Equal(3, inv.Offset);
    }

    [Fact]
    public void DeleteImage_HasNoInverse_InV1()
    {
        var op = new Operation { Id = "a", ClientId = "u1", Type = OpType.DeleteImage, ImageId = "img-1" };
        Assert.Null(Inverse.For(op));
    }
}
