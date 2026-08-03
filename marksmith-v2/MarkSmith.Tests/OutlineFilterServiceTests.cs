using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class OutlineFilterServiceTests
{
    private const string Doc = """
        # Alpha
        ## Beta
        ### Gamma
        ## Delta
        # Epsilon
        #### Deep
        """;

    [Fact]
    public void FilterFlat_KeepsHeadingsAtOrAboveMaxDepth()
    {
        var flat = OutlineFilterService.FilterFlat(Doc, maxDepth: 2);
        Assert.Equal(new[] { "Alpha", "Beta", "Delta", "Epsilon" }, flat.Select(e => e.Text).ToArray());
        Assert.All(flat, e => Assert.True(e.Level <= 2));
    }

    [Fact]
    public void FilterFlat_MaxDepthThreeIncludesH3ButNotH4()
    {
        var flat = OutlineFilterService.FilterFlat(Doc, maxDepth: 3);
        Assert.Contains(flat, e => e.Text == "Gamma");
        Assert.DoesNotContain(flat, e => e.Text == "Deep");
    }

    [Fact]
    public void BuildOutline_NestsChildrenUnderShallowerHeadings()
    {
        var outline = OutlineFilterService.BuildOutline(Doc, maxDepth: 3);

        Assert.Equal(2, outline.Count);                 // Alpha, Epsilon
        var alpha = outline[0];
        Assert.Equal("Alpha", alpha.Text);
        Assert.Equal(new[] { "Beta", "Delta" }, alpha.Children.Select(c => c.Text).ToArray());
        Assert.Equal("Gamma", alpha.Children[0].Children[0].Text); // Gamma nests under Beta
    }

    [Fact]
    public void BuildOutline_PrunesEmptySubTrees()
    {
        var entries = new[]
        {
            new TocEntry(1, "Root", "root"),
            new TocEntry(2, "", "empty-leaf"),   // no text, no children -> pruned
            new TocEntry(2, "Keep", "keep"),
        };
        var outline = OutlineFilterService.BuildOutline(entries, maxDepth: 6);

        var root = Assert.Single(outline);
        var child = Assert.Single(root.Children);
        Assert.Equal("Keep", child.Text);
    }

    [Fact]
    public void BuildOutline_ClampsDepthToValidRange()
    {
        // maxDepth of 0 clamps up to H1 only.
        var outline = OutlineFilterService.BuildOutline(Doc, maxDepth: 0);
        Assert.All(outline, n => Assert.Equal(1, n.Level));
        Assert.Equal(2, outline.Count); // Alpha, Epsilon
    }
}
