using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class SmartArtPotentialDetectorTests
{
    // ── hierarchy ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NestedBulletTree_SuggestsHierarchy()
    {
        var md = """
            - Executive Board
              - CEO
                - Engineering Team
                - Product Team
              - CFO
              - CMO
            """;

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.Hierarchy, s.Kind);
        Assert.True(s.IsOffered);
        Assert.Equal("hierarchy", s.LayoutAlias);
        Assert.False(string.IsNullOrWhiteSpace(s.Reason));
    }

    [Fact]
    public void NestedHeadings_SuggestHierarchy()
    {
        var md = """
            ## Departments
            ### Engineering
            #### Platform
            #### Applications
            ### Sales
            #### North
            #### South
            """;

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.Hierarchy, s.Kind);
    }

    [Fact]
    public void FlatList_IsNotHierarchy()
    {
        var md = "- apples\n- bananas\n- cherries\n- dates";

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.None, s.Kind);
    }

    [Fact]
    public void TinyThreeNodeTree_IsNotOffered()
    {
        // Two levels but only 3 nodes total — not chart-worthy.
        var md = "- A\n  - B\n  - C";

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.None, s.Kind);
    }

    // ── process ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FourOrderedSteps_SuggestProcess()
    {
        var md = "1. Draft the brief\n2. Review with stakeholders\n3. Publish\n4. Measure";

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.Process, s.Kind);
        Assert.Equal("process", s.LayoutAlias);
    }

    [Fact]
    public void ThreeOrderedSteps_AreNotOffered()
    {
        var md = "1. One\n2. Two\n3. Three";

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.None, s.Kind);
    }

    [Fact]
    public void RestartingNumberedList_DoesNotSuggestProcess()
    {
        // Two separate 3-step lists never form a 4-run.
        var md = "1. A\n2. B\n3. C\n\n1. X\n2. Y\n3. Z";

        var s = SmartArtPotentialDetector.Detect(md);

        Assert.Equal(SmartArtKind.None, s.Kind);
    }

    // ── negatives ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlainProse_IsNeverOffered()
    {
        var md = "This is a paragraph about the quarterly results. Nothing here has any structure " +
                 "worth turning into a diagram, it is just a few sentences of normal prose.";

        Assert.Equal(SmartArtKind.None, SmartArtPotentialDetector.Detect(md).Kind);
    }

    [Fact]
    public void BareTable_IsNotOffered()
    {
        var md = """
            | Name | Q1 | Q2 |
            |------|----|----|
            | A    | 10 | 12 |
            | B    | 8  | 9  |
            """;

        Assert.Equal(SmartArtKind.None, SmartArtPotentialDetector.Detect(md).Kind);
    }

    [Fact]
    public void StructureInsideCodeBlock_IsIgnored()
    {
        var md = """
            Here is the code:

            ```
            - Executive
              - CEO
                - Engineering
                - Product
              - CFO
            ```

            And that is all.
            """;

        Assert.Equal(SmartArtKind.None, SmartArtPotentialDetector.Detect(md).Kind);
    }

    [Fact]
    public void DeeplyNestedPaste_NeverCrashes()
    {
        // A pathological paste: thousands of progressively deeper bullet levels. Detection must
        // survive it (iterative walk with a depth cap), not overflow the UI-thread stack.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++)
            sb.Append(' ', i).Append("- item ").Append(i).Append('\n');

        var s = SmartArtPotentialDetector.Detect(sb.ToString());

        Assert.NotNull(s); // no exception, and the capped walk returns a sane suggestion
        Assert.False(s.IsOffered); // 5000-deep single chain is a pathological input, not a chart
    }

    [Fact]
    public void EmptyOrNull_IsNeverOffered()
    {
        Assert.Equal(SmartArtKind.None, SmartArtPotentialDetector.Detect("").Kind);
        Assert.Equal(SmartArtKind.None, SmartArtPotentialDetector.Detect("   \n  ").Kind);
    }
}

public class SmartArtOfferGateTests
{
    [Fact]
    public void FirstOffer_IsAllowed()
    {
        var gate = new SmartArtOfferGate();
        var suggestion = new SmartArtSuggestion(SmartArtKind.Hierarchy, 10, "reason");

        Assert.True(gate.ShouldOffer("same content", suggestion));
    }

    [Fact]
    public void SameContent_IsNotOfferedTwice()
    {
        var gate = new SmartArtOfferGate();
        var suggestion = new SmartArtSuggestion(SmartArtKind.Process, 10, "reason");

        Assert.True(gate.ShouldOffer("identical", suggestion));
        Assert.False(gate.ShouldOffer("identical", suggestion));
    }

    [Fact]
    public void ChangedContent_OffersAgain()
    {
        var gate = new SmartArtOfferGate();
        var suggestion = new SmartArtSuggestion(SmartArtKind.Hierarchy, 10, "reason");

        Assert.True(gate.ShouldOffer("version one", suggestion));
        Assert.True(gate.ShouldOffer("version two", suggestion));
    }

    [Fact]
    public void NonSuggestion_IsNeverOffered()
    {
        var gate = new SmartArtOfferGate();
        var none = new SmartArtSuggestion(SmartArtKind.None, 0, "");

        Assert.False(gate.ShouldOffer("anything", none));
    }
}
