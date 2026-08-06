using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class LineDiffTests
{
    private static string[] Texts(params string[] lines) => lines;

    [Fact]
    public void IdenticalTexts_AreAllSame()
    {
        var lines = LineDiff.Diff("a\nb\nc", "a\nb\nc");
        Assert.All(lines, l => Assert.Equal(LineDiff.Kind.Same, l.Kind));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void AddedLines_CarryNewNumbers()
    {
        var lines = LineDiff.Diff("a\nc", "a\nb\nc");
        var added = lines.Single(l => l.Kind == LineDiff.Kind.Added);
        Assert.Equal("b", added.Text);
        Assert.Equal(2, added.NewNumber);
        Assert.Null(added.OldNumber);
    }

    [Fact]
    public void RemovedLines_CarryOldNumbers()
    {
        var lines = LineDiff.Diff("a\nb\nc", "a\nc");
        var removed = lines.Single(l => l.Kind == LineDiff.Kind.Removed);
        Assert.Equal("b", removed.Text);
        Assert.Equal(2, removed.OldNumber);
        Assert.Null(removed.NewNumber);
    }

    [Fact]
    public void ModifiedLine_IsRemovedPlusAdded()
    {
        var lines = LineDiff.Diff("old line", "new line");
        Assert.Equal(LineDiff.Kind.Removed, lines[0].Kind);
        Assert.Equal(LineDiff.Kind.Added, lines[1].Kind);
        Assert.Equal("old line", lines[0].Text);
        Assert.Equal("new line", lines[1].Text);
    }

    [Fact]
    public void MultipleHunks_KeepCommonLinesSame()
    {
        var before = "keep1\noldA\nkeep2\noldB\nkeep3";
        var after = "keep1\nnewA\nkeep2\nnewB\nkeep3";
        var lines = LineDiff.Diff(before, after);
        Assert.Equal(1, lines.Count(l => l.Kind == LineDiff.Kind.Same && l.Text == "keep2"));
        Assert.Equal(2, lines.Count(l => l.Kind == LineDiff.Kind.Removed));
        Assert.Equal(2, lines.Count(l => l.Kind == LineDiff.Kind.Added));
    }

    [Fact]
    public void Crlf_AndLf_AreEquivalent()
    {
        var lines = LineDiff.Diff("a\r\nb", "a\nb");
        Assert.All(lines, l => Assert.Equal(LineDiff.Kind.Same, l.Kind));
    }

    [Fact]
    public void EmptyVersusContent_IsAllAdded()
    {
        var lines = LineDiff.Diff("", "one\ntwo");
        Assert.Equal(2, lines.Count(l => l.Kind == LineDiff.Kind.Added));
    }

    [Fact]
    public void SideBySide_ReplacementPairsOneToOne()
    {
        var rows = LineDiff.BuildSideBySide(LineDiff.Diff("a\nx\nb", "a\ny\nb"));
        var changed = rows.Where(r => r.Left is not null && r.Right is not null &&
                                      r.Left.Kind == LineDiff.Kind.Removed && r.Right.Kind == LineDiff.Kind.Added).ToList();
        Assert.Single(changed);
        Assert.Equal("x", changed[0].Left!.Text);
        Assert.Equal("y", changed[0].Right!.Text);
    }

    [Fact]
    public void SideBySide_UnbalancedRun_LeavesBlankCell()
    {
        // 2 removed -> 1 added: one paired row + one removed-only row.
        var rows = LineDiff.BuildSideBySide(LineDiff.Diff("p\nq\nr", "p\nr"));
        Assert.Equal(3, rows.Count); // p (same), q (removed-only), r (same)
        Assert.NotNull(rows[1].Left);
        Assert.Null(rows[1].Right);
        Assert.Equal(LineDiff.Kind.Removed, rows[1].Left!.Kind);
    }
}
