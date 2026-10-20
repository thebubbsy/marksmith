using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Core.AST;
using MarkSmith.Core.Preview;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #19 swept every SmartArt preview family through eight outlines: siblings mixed 15 pt with 9 pt,
// circles and pyramid tips cut words into syllables ("Dis-cov-er", "Internati-onalisati-on…"), Venn
// dropped sets past six, and the gallery showed the same icon for every row with ids for names.
public class SmartArtStudioPolishTests
{
    private const string Long = "- Customer onboarding and account verification\n- Self-actualisation\n- Internationalisation strategy for APAC markets\n- Quarterly business review";

    private static string Render(string md, string alias) =>
        HtmlPreviewRenderer.RenderHtml(MarkdownAstParser.Parse(md), alias, alias);

    /// <summary>Font sizes of the bold (title) text elements, in drawing order.</summary>
    private static List<double> TitleSizes(string html) =>
        Regex.Matches(html, "<text fill=\"[^\"]+\" font-weight=\"600\" font-size=\"([0-9.]+)\"")
             .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    private static string Words(string html) =>
        WebUtilityDecode(string.Concat(Regex.Matches(html, "<tspan[^>]*>([^<]*)</tspan>").Select(m => m.Groups[1].Value + " ")));

    private static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

    [Theory]
    [InlineData("process1")]
    [InlineData("chevron1")]
    [InlineData("hierarchy1")]
    [InlineData("equation1")]
    [InlineData("StepUpProcess")]
    public void Sibling_shapes_share_one_title_size(string alias)
    {
        var sizes = TitleSizes(Render("- Plan\n- Build the first release\n- Ship\n- Measure", alias));
        Assert.NotEmpty(sizes);
        Assert.Single(sizes.Distinct());
    }

    [Fact]
    public void A_crowded_shape_shrinks_alone_below_the_shared_floor()
    {
        // "Define" carries enough bullets to need less than 10 pt; the others keep a readable size.
        var md = "- Plan\n- Define\n  - one two three four five six\n  - seven eight nine ten eleven\n  - twelve thirteen fourteen\n  - fifteen sixteen\n  - seventeen\n  - eighteen\n- Ship";
        var sizes = TitleSizes(Render(md, "process1"));
        Assert.True(sizes.Count(s => s >= 10) >= 2, string.Join(",", sizes));
    }

    [Theory]
    [InlineData("cycle2")]
    [InlineData("radial1")]
    [InlineData("venn1")]
    public void Circles_grow_to_keep_long_words_whole(string alias)
    {
        var html = Render(Long, alias);
        var words = Words(html);
        Assert.Contains("Internationalisation", words);
        Assert.DoesNotContain("Internati-", words);
        Assert.DoesNotContain("…", words);
    }

    [Fact]
    public void A_tall_pyramid_tip_gets_a_callout_instead_of_syllables()
    {
        var md = string.Join("\n", "Discover Define Deliver Learn Retro Plan Build Ship Measure Review Adapt".Split(' ').Select(w => "- " + w));
        var words = Words(Render(md, "pyramid1"));
        Assert.Contains("Discover", words);
        Assert.DoesNotContain("Dis-", words);
    }

    [Fact]
    public void A_single_tier_pyramid_keeps_pyramid_proportions()
    {
        var html = Render("- Only item", "pyramid1");
        var vb = Regex.Match(html, "viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"");
        var pts = Regex.Match(html, "<polygon points=\"([^\"]+)\"").Groups[1].Value.Split(' ')
            .Select(p => double.Parse(p.Split(',')[0], CultureInfo.InvariantCulture)).ToList();
        double width = pts.Max() - pts.Min();
        Assert.True(width <= 300, $"single tier was {width} wide");
        Assert.True(vb.Success);
    }

    [Fact]
    public void Venn_draws_every_set_up_to_twelve()
    {
        var md = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"- Set {i}"));
        var html = Render(md, "venn1");
        Assert.Equal(9, Regex.Matches(html, "fill-opacity=\"0.55\"").Count);
        Assert.DoesNotContain("not shown", html);
    }

    [Fact]
    public void Wrapped_bullets_hang_under_their_first_word()
    {
        var html = Render("- Define\n  - Agree success metrics with the whole team today", "process1");
        var lines = Regex.Matches(html, "<tspan[^>]*>([^<]*)</tspan>").Select(m => WebUtilityDecode(m.Groups[1].Value)).ToList();
        int first = lines.FindIndex(l => l.StartsWith("•", StringComparison.Ordinal));
        Assert.True(first >= 0 && first + 1 < lines.Count, string.Join(" | ", lines));
        Assert.StartsWith("  ", lines[first + 1]);
    }

    [Fact]
    public void A_lone_timeline_item_leaves_no_empty_lower_row()
    {
        var one = Regex.Match(Render("- Only item", "hProcess11"), "viewBox=\"0 0 [0-9.]+ ([0-9.]+)\"");
        var two = Regex.Match(Render("- One\n- Two", "hProcess11"), "viewBox=\"0 0 [0-9.]+ ([0-9.]+)\"");
        Assert.True(double.Parse(one.Groups[1].Value, CultureInfo.InvariantCulture) < double.Parse(two.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Every_family_has_a_text_free_sized_thumbnail()
    {
        foreach (var family in Enum.GetValues<SmartArtPreviewFamily>())
        {
            var svg = HtmlPreviewRenderer.RenderThumbnailSvg(family);
            Assert.StartsWith("<svg", svg);
            Assert.DoesNotContain("<text", svg);
            Assert.DoesNotContain("<style", svg);
            Assert.DoesNotContain("width=\"100%\"", svg);
            Assert.Matches("width=\"[0-9.]+\" height=\"[0-9.]+\"", svg);
            Assert.Matches("<(rect|circle|polygon|path)", svg);
        }
    }

    [Fact]
    public void Thumbnails_differ_between_families()
    {
        var all = Enum.GetValues<SmartArtPreviewFamily>().Select(HtmlPreviewRenderer.RenderThumbnailSvg).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Theory]
    [InlineData("default", "Basic Block List")]
    [InlineData("cycle2", "Basic Cycle")]
    [InlineData("orgChart1", "Organization Chart")]
    [InlineData("hList7", "Horizontal List 7")]
    [InlineData("bProcess3", "Bending Process 3")]
    [InlineData("vProcess5", "Vertical Process 5")]
    [InlineData("pList1", "Picture Caption List")]
    [InlineData("PlusandMinus", "Plus and Minus")]
    [InlineData("MeetTheTeamOval", "Meet the Team Oval")]
    [InlineData("hList9", "Horizontal List 9")]
    [InlineData("AlternatingCircleProcess", "Alternating Circle Process")]
    [InlineData("radial2", "Radial 2")]
    public void Gallery_names_read_like_words(string alias, string expected)
    {
        Assert.Equal(expected, new StudioLayoutItem { Name = alias, Alias = alias }.DisplayName);
    }

    [Fact]
    public void Gallery_is_alphabetical_by_the_name_it_shows()
    {
        var vm = new SmartArtDesignStudioViewModel();
        var names = vm.Layouts.Select(l => l.DisplayName).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList(), names);
        Assert.DoesNotContain(names, n => Regex.IsMatch(n, "^[hvbpl] [A-Z]"));
    }

    [Fact]
    public void Gallery_rows_carry_their_preview_family()
    {
        var vm = new SmartArtDesignStudioViewModel();
        Assert.Equal(SmartArtPreviewFamily.Cycle, vm.Layouts.First(l => l.Alias == "cycle2").Family);
        Assert.Equal(SmartArtPreviewFamily.Venn, vm.Layouts.First(l => l.Alias == "venn1").Family);
    }

    [Fact]
    public void Preloading_new_content_starts_a_fresh_undo_history()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Select(vm.OutlineRows[0]);
        vm.AddChild();
        vm.CommitRename();
        Assert.True(vm.CanUndo);
        // Inserted first: an edit that never went anywhere stays one Ctrl+Z away instead
        // (StudioCloseGuardTests).
        vm.InsertIntoDocument();
        const string md = "- Plan\n- Build\n- Ship";
        vm.Preload(md, "process1");
        Assert.False(vm.CanUndo);
        vm.Undo();
        Assert.Equal(md, vm.MarkdownText);
    }

    [Fact]
    public void Insert_status_names_the_layout()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.SelectedLayout = vm.Layouts.First(l => l.Alias == "process1");
        vm.InsertIntoDocument();
        Assert.Contains("Basic Process", vm.StatusMessage);
        Assert.DoesNotContain("Added  to", vm.StatusMessage);
    }

    [Fact]
    public void Outline_operations_report_informative_status_messages()
    {
        var vm = new SmartArtDesignStudioViewModel();
        Assert.NotEmpty(vm.OutlineRows);
        var initialCount = vm.OutlineRows.Count;

        vm.Select(vm.OutlineRows[0]);
        vm.AddChild();
        Assert.Contains("Added child under", vm.StatusMessage);
        Assert.Contains("Ctrl+Z to undo", vm.StatusMessage);

        vm.AddSibling();
        Assert.Contains("Added sibling after", vm.StatusMessage);

        var added = vm.SelectedNode;
        Assert.NotNull(added);
        vm.DeleteSelected();
        Assert.Contains("Deleted 'New Node'", vm.StatusMessage);

        vm.Undo();
        Assert.Contains("Undid outline edit", vm.StatusMessage);

        vm.Redo();
        Assert.Contains("Redid outline edit", vm.StatusMessage);
    }

    [Fact]
    public void Empty_outline_Enter_or_Insert_creates_first_node()
    {
        var vm = new SmartArtDesignStudioViewModel();
        // Clear all nodes
        while (vm.RootNodes.Count > 0)
        {
            vm.DeleteSelected(vm.RootNodes[0]);
        }
        Assert.True(vm.IsOutlineEmpty);
        Assert.Empty(vm.OutlineRows);

        bool handled = vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.Enter);
        Assert.True(handled);
        Assert.False(vm.IsOutlineEmpty);
        Assert.Single(vm.OutlineRows);
        Assert.Contains("Added first item", vm.StatusMessage);
    }

    [Fact]
    public void HandleOutlineKey_Delete_and_F2_operate_on_selection()
    {
        var vm = new SmartArtDesignStudioViewModel();
        var leafNode = vm.OutlineRows.First(r => r.Children.Count == 0);
        vm.Select(leafNode);

        bool renameHandled = vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.F2);
        Assert.True(renameHandled);
        Assert.True(leafNode.IsEditing);
        vm.CancelRename();
        Assert.False(leafNode.IsEditing);

        int countBefore = vm.OutlineRows.Count;
        bool deleteHandled = vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.Delete);
        Assert.True(deleteHandled);
        Assert.Equal(countBefore - 1, vm.OutlineRows.Count);
    }

    [Fact]
    public void DuplicateSelected_And_TryBuildSmartArtBlock_RoundTrip_Subtree_And_Block()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Discovery\n  - User interviews\n- Delivery", "process1");

        var firstRoot = vm.RootNodes[0];
        vm.Select(firstRoot);
        Assert.False(vm.CanMoveUp);
        Assert.True(vm.CanMoveDown);
        Assert.False(vm.CanPromote);
        Assert.False(vm.CanDemote);

        vm.DuplicateSelected();
        Assert.Equal(3, vm.RootNodes.Count);
        Assert.Equal("Discovery", vm.RootNodes[1].Text);
        Assert.Single(vm.RootNodes[1].Children);
        Assert.Equal("User interviews", vm.RootNodes[1].Children[0].Text);

        Assert.True(vm.TryBuildSmartArtBlock(out string block, out string layoutName, out string error));
        Assert.Empty(error);
        Assert.Equal("Basic Process", layoutName);
        Assert.Contains(":::smartart type=\"process1\"", block);
        Assert.Contains("- Discovery", block);
    }

    [Fact]
    public void HandleOutlineKey_AltLeft_And_AltRight_Promote_And_Demote_And_Report_Boundary_Status()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Plan\n- Build\n- Ship", "process1");

        var first = vm.OutlineRows.First(r => r.Text == "Plan");
        vm.Select(first);

        // Boundary feedback at top level / first sibling
        vm.Promote();
        Assert.Contains("already at the top level", vm.StatusMessage);

        vm.Demote();
        Assert.Contains("no previous sibling to nest under", vm.StatusMessage);

        vm.MoveUp();
        Assert.Contains("already the first sibling", vm.StatusMessage);

        var last = vm.OutlineRows.First(r => r.Text == "Ship");
        vm.Select(last);
        vm.MoveDown();
        Assert.Contains("already the last sibling", vm.StatusMessage);

        // Alt+Right demotes "Build" under "Plan", Alt+Left promotes it back
        var second = vm.OutlineRows.First(r => r.Text == "Build");
        vm.Select(second);
        Assert.True(vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.Right, alt: true));
        Assert.Equal(1, vm.SelectedNode!.Depth);
        Assert.Contains("Demoted 'Build'", vm.StatusMessage);

        Assert.True(vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.Left, alt: true));
        Assert.Equal(0, vm.SelectedNode!.Depth);
        Assert.Contains("Promoted 'Build'", vm.StatusMessage);
    }

    [Fact]
    public void HandleOutlineKey_Navigation_Updates_Status_With_Node_And_Level_And_CommitRename_Reports_Status()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Executive\n  - Engineering\n- Operations", "orgChart1");

        Assert.Contains("3 items", vm.OutlineSummaryText);
        Assert.Contains("Organization Chart", vm.OutlineSummaryText);

        Assert.True(vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.Home));
        Assert.Equal("Executive · level 1", vm.StatusMessage);

        Assert.True(vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.Down));
        Assert.Equal("Engineering · level 2", vm.StatusMessage);

        Assert.True(vm.HandleOutlineKey(SmartArtDesignStudioViewModel.OutlineKey.End));
        Assert.Equal("Operations · level 1", vm.StatusMessage);

        vm.BeginRename(vm.SelectedNode);
        vm.SelectedNode!.Text = "Global Operations";
        vm.CommitRename();
        Assert.Contains("Renamed to 'Global Operations'", vm.StatusMessage);
    }

    [Fact]
    public void ComputeOutlineMetrics_CalculatesAccurateWordsCharactersAndNodes()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Preload("- Executive Leader\n  - Senior Engineering Director\n- Global Operations", "orgChart1");

        var (words, chars, nodes) = SmartArtDesignStudioViewModel.ComputeOutlineMetrics(vm.OutlineRows);
        Assert.Equal(3, nodes);
        Assert.Equal(7, words); // Executive (1) + Leader (1) + Senior (1) + Engineering (1) + Director (1) + Global (1) + Operations (1) = 7
        Assert.Equal("Executive Leader".Length + "Senior Engineering Director".Length + "Global Operations".Length, chars);
    }

    [Fact]
    public void Run131_ReverseOutline_ReversesLinesPreservingLineEndings()
    {
        var inputLf = "- Alpha\n- Beta\n- Gamma";
        var reversedLf = SmartArtDesignStudioViewModel.ReverseOutline(inputLf);
        Assert.Equal("- Gamma\n- Beta\n- Alpha", reversedLf);

        var inputCrlf = "- Alpha\r\n- Beta\r\n- Gamma";
        var reversedCrlf = SmartArtDesignStudioViewModel.ReverseOutline(inputCrlf);
        Assert.Equal("- Gamma\r\n- Beta\r\n- Alpha", reversedCrlf);

        Assert.Equal("", SmartArtDesignStudioViewModel.ReverseOutline(""));
    }

    [Fact]
    public void Run131_SortOutlineAlphabetically_SortsAscendingAndDescending()
    {
        var input = "- Zebra\n- Apple\n- Mango";
        var asc = SmartArtDesignStudioViewModel.SortOutlineAlphabetically(input, ascending: true);
        Assert.Equal("- Apple\n- Mango\n- Zebra", asc);

        var desc = SmartArtDesignStudioViewModel.SortOutlineAlphabetically(input, ascending: false);
        Assert.Equal("- Zebra\n- Mango\n- Apple", desc);

        // Leading bullets/spaces stripped during comparison
        var mixed = "* Zebra\n- Apple\n  Banana";
        var mixedAsc = SmartArtDesignStudioViewModel.SortOutlineAlphabetically(mixed, ascending: true);
        Assert.Equal("- Apple\n  Banana\n* Zebra", mixedAsc);
    }

    [Fact]
    public void Run132_TrimTrailingWhitespace_TrimsTrailingSpacesPreservingIndentationAndLineEndings()
    {
        var inputLf = "  - Leading indent   \n- Normal line\t  \n    - Deep indent  ";
        var expectedLf = "  - Leading indent\n- Normal line\n    - Deep indent";
        var trimmedLf = SmartArtDesignStudioViewModel.TrimTrailingWhitespace(inputLf);
        Assert.Equal(expectedLf, trimmedLf);

        var inputCrlf = "  - Leading indent   \r\n- Normal line\t  \r\n    - Deep indent  ";
        var expectedCrlf = "  - Leading indent\r\n- Normal line\r\n    - Deep indent";
        var trimmedCrlf = SmartArtDesignStudioViewModel.TrimTrailingWhitespace(inputCrlf);
        Assert.Equal(expectedCrlf, trimmedCrlf);

        Assert.Equal("", SmartArtDesignStudioViewModel.TrimTrailingWhitespace(""));
    }

    [Fact]
    public void Run133_StandardiseBullets_NormalizesMixedBulletsPreservingIndentAndLineEndings()
    {
        var inputLf = "* Item One\n  + Nested Two\n    - Deep Three\nPlain text line";
        var expectedLf = "- Item One\n  - Nested Two\n    - Deep Three\nPlain text line";
        var resultLf = SmartArtDesignStudioViewModel.StandardiseBullets(inputLf, '-');
        Assert.Equal(expectedLf, resultLf);

        var inputCrlf = "* Item One\r\n  + Nested Two\r\n    - Deep Three";
        var expectedCrlf = "- Item One\r\n  - Nested Two\r\n    - Deep Three";
        var resultCrlf = SmartArtDesignStudioViewModel.StandardiseBullets(inputCrlf, '-');
        Assert.Equal(expectedCrlf, resultCrlf);

        Assert.Equal("", SmartArtDesignStudioViewModel.StandardiseBullets(""));
    }

    [Fact]
    public void Run134_NormalizeIndentation_NormalizesSpacingPreservingLineEndings()
    {
        // Tabs and uneven spaces normalized to 2 spaces per depth level
        var inputLf = "* Root\n\t- Child One\n\t\t- Grandchild Two\n    - Four Space Child\n   - Three Space Child";
        var expectedLf = "* Root\n  - Child One\n    - Grandchild Two\n    - Four Space Child\n  - Three Space Child";
        var resultLf = SmartArtDesignStudioViewModel.NormalizeIndentation(inputLf, 2);
        Assert.Equal(expectedLf, resultLf);

        var inputCrlf = "* Root\r\n\t- Child One\r\n\t\t- Grandchild Two";
        var expectedCrlf = "* Root\r\n  - Child One\r\n    - Grandchild Two";
        var resultCrlf = SmartArtDesignStudioViewModel.NormalizeIndentation(inputCrlf, 2);
        Assert.Equal(expectedCrlf, resultCrlf);

        Assert.Equal("", SmartArtDesignStudioViewModel.NormalizeIndentation(""));
    }

    [Fact]
    public void Run135_AnalyzeHierarchyMetrics_CalculatesCorrectCountsAndDepth()
    {
        var markdown = "- Root 1\n  - Child 1\n    - Grandchild 1\n- Root 2\n  - Child 2";
        var (total, roots, leaves, maxDepth, summary) = SmartArtDesignStudioViewModel.AnalyzeHierarchyMetrics(markdown);
        Assert.Equal(5, total);
        Assert.Equal(2, roots);
        Assert.Equal(2, leaves); // Grandchild 1, Child 2
        Assert.Equal(3, maxDepth);
        Assert.Equal("Hierarchy: 5 nodes · 2 roots · 2 leaves · max depth 3", summary);

        var empty = SmartArtDesignStudioViewModel.AnalyzeHierarchyMetrics("");
        Assert.Equal((0, 0, 0, 0, "No nodes in outline"), empty);
    }

    [Fact]
    public void Run136_FormatOutlineAtMaxDepth_FiltersDepthLevelsAccurately()
    {
        var markdown = "- Root 1\n  - Child 1\n    - Grandchild 1\n- Root 2\n  - Child 2";
        var rootsOnly = SmartArtDesignStudioViewModel.FormatOutlineAtMaxDepth(markdown, 1);
        Assert.Equal("- Root 1\n- Root 2", rootsOnly);

        var depth2 = SmartArtDesignStudioViewModel.FormatOutlineAtMaxDepth(markdown, 2);
        Assert.Equal("- Root 1\n  - Child 1\n- Root 2\n  - Child 2", depth2);

        // CRLF preserved
        var crlf = "- Root 1\r\n  - Child 1\r\n- Root 2";
        var crlfRoots = SmartArtDesignStudioViewModel.FormatOutlineAtMaxDepth(crlf, 1);
        Assert.Equal("- Root 1\r\n- Root 2", crlfRoots);

        // Empty input
        Assert.Equal("", SmartArtDesignStudioViewModel.FormatOutlineAtMaxDepth(""));
    }

    [Fact]
    public void Run137_AuditOutlineLineLengths_CalculatesLengthsAccurately()
    {
        var markdown = "- Short\n- Normal length node\n- This is a very long line that clearly exceeds the standard threshold of forty characters";
        var audit = SmartArtDesignStudioViewModel.AuditOutlineLineLengths(markdown, 40);

        Assert.Equal(3, audit.TotalLines);
        Assert.Equal(1, audit.ExceedingCount);
        Assert.True(audit.LongestLineLength > 40);
        Assert.Equal(40, audit.Threshold);

        // Empty input
        var empty = SmartArtDesignStudioViewModel.AuditOutlineLineLengths("");
        Assert.Equal(0, empty.TotalLines);
        Assert.Equal(0, empty.ExceedingCount);
    }

    [Fact]
    public void Run137_FormatOutlineLineLengthAuditSummary_FormatsExpectedSummary()
    {
        var shortDoc = "- Alpha\n- Beta\n- Gamma";
        var summary = SmartArtDesignStudioViewModel.FormatOutlineLineLengthAuditSummary(shortDoc, 40);
        Assert.Contains("3 lines", summary);
        Assert.Contains("All lines within 40-char limit", summary);

        var emptySummary = SmartArtDesignStudioViewModel.FormatOutlineLineLengthAuditSummary(null, 40);
        Assert.Equal("Outline is empty (0 lines)", emptySummary);
    }

    [Fact]
    public void Run138_FormatOutlineHierarchyBreakdown_ComputesLevelBreakdown()
    {
        var outline = @"- Root 1
  - Child 1.1
  - Child 1.2
    - Grandchild 1.2.1
- Root 2
  - Child 2.1";

        var summary = SmartArtDesignStudioViewModel.FormatOutlineHierarchyBreakdown(outline);
        Assert.Contains("Outline: 6 items across 3 levels (L1: 2, L2: 3, L3: 1) · Max Depth: 3", summary);

        var emptySummary = SmartArtDesignStudioViewModel.FormatOutlineHierarchyBreakdown("");
        Assert.Equal("Outline is empty (0 items)", emptySummary);

        var nonStructured = "Just a paragraph without list markers.";
        var noListSummary = SmartArtDesignStudioViewModel.FormatOutlineHierarchyBreakdown(nonStructured);
        Assert.Equal("Outline has 0 structured items", noListSummary);
    }

    [Fact]
    public void Run139_AuditOutlineBranching_ComputesBranchingAndLeafStats()
    {
        var outline = @"- Root 1
  - Child 1.1
  - Child 1.2
- Root 2
  - Child 2.1";

        var (total, branches, leaves, avgBranch, maxBranch) = SmartArtDesignStudioViewModel.AuditOutlineBranching(outline);
        Assert.Equal(5, total);
        Assert.Equal(2, branches);
        Assert.Equal(3, leaves);
        Assert.Equal(1.5, avgBranch);
        Assert.Equal(2, maxBranch);

        var summary = SmartArtDesignStudioViewModel.FormatOutlineBranchingSummary(outline);
        Assert.Equal("Outline branching: 5 nodes (2 branches, 3 leaf/leaves) · Avg: 1.5 children/branch (Max: 2)", summary);

        var emptySummary = SmartArtDesignStudioViewModel.FormatOutlineBranchingSummary(null);
        Assert.Equal("Outline branching: Empty outline (0 nodes)", emptySummary);

        var flatOutline = "- Item 1\n- Item 2\n- Item 3";
        var flatSummary = SmartArtDesignStudioViewModel.FormatOutlineBranchingSummary(flatOutline);
        Assert.Equal("Outline branching: 3 nodes (All 3 are flat root items, 0 branches)", flatSummary);
    }

    [Fact]
    public void Run140_AuditOutlineBalance_ComputesSubtreeBalanceAndStdDev()
    {
        var outline = @"- Root 1
  - Child 1.1
  - Child 1.2
- Root 2
  - Child 2.1
  - Child 2.2";

        var (roots, sizes, stdDev, isBalanced) = SmartArtDesignStudioViewModel.AuditOutlineBalance(outline);
        Assert.Equal(2, roots);
        Assert.Equal(new[] { 3, 3 }, sizes);
        Assert.Equal(0.0, stdDev);
        Assert.True(isBalanced);

        var summary = SmartArtDesignStudioViewModel.FormatOutlineBalanceSummary(outline);
        Assert.Equal("Outline balance: 2 root trees (Balanced: subtree sizes [3, 3] · σ=0)", summary);

        var emptySummary = SmartArtDesignStudioViewModel.FormatOutlineBalanceSummary(null);
        Assert.Equal("Outline balance: Empty outline (0 nodes)", emptySummary);

        var singleRoot = "- Only Root\n  - Item 1\n  - Item 2";
        var singleSummary = SmartArtDesignStudioViewModel.FormatOutlineBalanceSummary(singleRoot);
        Assert.Equal("Outline balance: 1 root tree (3 total nodes)", singleSummary);
    }

    [Fact]
    public void Run141_AuditOutlineDepths_And_FormatOutlineDepthDistributionSummary()
    {
        var outline = @"- Root
  - Child 1
    - Grandchild 1
  - Child 2";

        var (total, maxDepth, avgDepth, levelCounts, isStepUniform) = SmartArtDesignStudioViewModel.AuditOutlineDepths(outline);
        Assert.Equal(4, total);
        Assert.Equal(3, maxDepth);
        Assert.Equal(2.0, avgDepth);
        Assert.True(isStepUniform);
        Assert.Equal(1, levelCounts[1]);
        Assert.Equal(2, levelCounts[2]);
        Assert.Equal(1, levelCounts[3]);

        var summary = SmartArtDesignStudioViewModel.FormatOutlineDepthDistributionSummary(outline);
        Assert.Equal("Outline depth: 4 nodes (Max: L3, Avg: 2) · [L1: 1, L2: 2, L3: 1] · Uniform step hierarchy", summary);

        var emptySummary = SmartArtDesignStudioViewModel.FormatOutlineDepthDistributionSummary(null);
        Assert.Equal("Outline depth: Empty outline (0 nodes)", emptySummary);

        var flatOutline = "- Flat 1\n- Flat 2";
        var flatSummary = SmartArtDesignStudioViewModel.FormatOutlineDepthDistributionSummary(flatOutline);
        Assert.Equal("Outline depth: 2 nodes (All flat at Level 1 · Uniform)", flatSummary);

        // Skipped level: Root directly to Grandchild (L1 -> L3)
        var skippedOutline = "- Root\n    - Grandchild without Child";
        var (_, _, _, _, isUniform) = SmartArtDesignStudioViewModel.AuditOutlineDepths(skippedOutline);
        Assert.False(isUniform);
        var skippedSummary = SmartArtDesignStudioViewModel.FormatOutlineDepthDistributionSummary(skippedOutline);
        Assert.Contains("Non-uniform indentation (contains skipped levels)", skippedSummary);
    }

    [Fact]
    public void Run142_ReverseOutlineHierarchically_And_FormatHierarchyReverseSummary()
    {
        Assert.Equal(0, SmartArtDesignStudioViewModel.ReverseOutlineHierarchically(null).BlockCount);
        Assert.Equal("Reversed outline: 0 blocks (Empty outline)", SmartArtDesignStudioViewModel.FormatHierarchyReverseSummary(0, 0));

        var outline = "- Strategy\n  - Research\n  - Interviews\n- Execution\n  - Build\n  - Test";
        var (reversed, blockCount, lineCount) = SmartArtDesignStudioViewModel.ReverseOutlineHierarchically(outline);

        Assert.Equal(2, blockCount);
        Assert.Equal(6, lineCount);
        var expected = "- Execution\n  - Build\n  - Test\n- Strategy\n  - Research\n  - Interviews";
        Assert.Equal(expected, reversed);

        var summary = SmartArtDesignStudioViewModel.FormatHierarchyReverseSummary(blockCount, lineCount);
        Assert.Equal("Reversed 2 top-level blocks (6 lines · Child subtrees preserved)", summary);
    }

    [Fact]
    public void Run143_DeduplicateOutlineNodes_And_FormatOutlineDeduplicationSummary()
    {
        Assert.Equal("Outline deduplication: Empty outline (0 nodes)", SmartArtDesignStudioViewModel.FormatOutlineDeduplicationSummary(0, 0));

        var outline = "- Phase 1\n  - Design\n  - design\n  - Review\n- Phase 2\n  - Design\n- phase 1\n  - Redundant child";
        var (deduped, removedCount, survivingCount) = SmartArtDesignStudioViewModel.DeduplicateOutlineNodes(outline, ignoreCase: true);

        // 'design' under Phase 1 (1), 'phase 1' at top-level + 'Redundant child' under it (2) = 3 removed
        Assert.Equal(3, removedCount);
        Assert.Equal(5, survivingCount);
        Assert.Equal("- Phase 1\n  - Design\n  - Review\n- Phase 2\n  - Design", deduped);

        var summary = SmartArtDesignStudioViewModel.FormatOutlineDeduplicationSummary(removedCount, survivingCount);
        Assert.Equal("Deduplicated outline: Removed 3 duplicate sibling nodes (5 unique nodes retained)", summary);

        var cleanSummary = SmartArtDesignStudioViewModel.FormatOutlineDeduplicationSummary(0, 5);
        Assert.Equal("Outline deduplication: 0 duplicates found (5 unique nodes verified)", cleanSummary);
    }

    [Fact]
    public void Run144_SplitLongOutlineItems_And_FormatLongItemSplitSummary()
    {
        Assert.Equal("Split long outline items (>45 chars): Empty outline (0 nodes)",
            SmartArtDesignStudioViewModel.FormatLongItemSplitSummary(0, 0, 45));

        var outline = "- Short node\n- Phase 1 Discovery: Conduct stakeholder interviews and baseline architecture review\n- Continual operational excellence across distributed cloud deployment regions";
        var (splitMd, splitCount, resultingNodes) = SmartArtDesignStudioViewModel.SplitLongOutlineItems(outline, maxChars: 45);

        Assert.Equal(2, splitCount);
        Assert.Equal(5, resultingNodes);
        Assert.Contains("- Short node\n", splitMd);
        Assert.Contains("- Phase 1 Discovery\n  - Conduct stakeholder interviews and baseline architecture review\n", splitMd);
        Assert.Contains("\n  - ", splitMd);

        var summary = SmartArtDesignStudioViewModel.FormatLongItemSplitSummary(splitCount, resultingNodes, 45);
        Assert.Equal("Split long outline items (>45 chars): 2 long bullets split into parent + sub-bullet (5 nodes total)", summary);
    }

    [Fact]
    public void Run145_ApplyHierarchicalNumbering_And_FormatOutlineNumberingSummary()
    {
        Assert.Equal("Outline numbering: Empty outline (0 nodes)",
            SmartArtDesignStudioViewModel.FormatOutlineNumberingSummary(0, 0));

        var raw = "- Discovery\n  - Stakeholder Interviews\n  - Architecture Audit\n- Delivery\n  - Rollout";
        var (numbered, count, maxDepth) = SmartArtDesignStudioViewModel.ApplyHierarchicalNumbering(raw);

        Assert.Equal(5, count);
        Assert.Equal(2, maxDepth);
        Assert.Equal("- 1. Discovery\n  - 1.1. Stakeholder Interviews\n  - 1.2. Architecture Audit\n- 2. Delivery\n  - 2.1. Rollout", numbered);

        var summary = SmartArtDesignStudioViewModel.FormatOutlineNumberingSummary(count, maxDepth);
        Assert.Equal("Outline numbering: Applied hierarchical numbering (1., 1.1.) to 5 nodes across 2 levels", summary);

        // Idempotent re-numbering strips old prefix before re-applying
        var (renumbered, _, _) = SmartArtDesignStudioViewModel.ApplyHierarchicalNumbering(numbered);
        Assert.Equal(numbered, renumbered);

        // Strip mode restores clean unnumbered bullets
        var (stripped, stripCount, stripDepth) = SmartArtDesignStudioViewModel.ApplyHierarchicalNumbering(numbered, stripExistingOnly: true);
        Assert.Equal(raw, stripped);
        Assert.Equal("Outline numbering: Stripped numeric prefixes from 5 nodes across 2 levels",
            SmartArtDesignStudioViewModel.FormatOutlineNumberingSummary(stripCount, stripDepth, stripped: true));
    }

    [Fact]
    public void Run146_RecommendLayoutFamiliesForOutline_And_FormatLayoutRecommendationSummary()
    {
        Assert.Equal("SmartArt layout recommendations: Empty outline (0 nodes)",
            SmartArtDesignStudioViewModel.FormatLayoutRecommendationSummary(null));

        // 1. Hierarchy outline (3 levels)
        var hierOutline = "- Enterprise Architecture\n  - Cloud Platforms\n    - Azure\n    - AWS\n  - Data Governance\n    - Lineage";
        var hierRecs = SmartArtDesignStudioViewModel.RecommendLayoutFamiliesForOutline(hierOutline);
        Assert.NotEmpty(hierRecs);
        Assert.Equal("Hierarchy", hierRecs[0].Family);
        Assert.True(hierRecs[0].MatchScorePercent >= 90);

        var hierSummary = SmartArtDesignStudioViewModel.FormatLayoutRecommendationSummary(hierOutline);
        Assert.Contains("SmartArt layout recommendation: #1 Hierarchy", hierSummary);
        Assert.Contains("Horizontal Hierarchy", hierSummary);

        // 2. Matrix outline (4 quadrant roots)
        var matrixOutline = "- Strengths\n  - Internal advantages\n- Weaknesses\n  - Resource gaps\n- Opportunities\n  - Market tailwinds\n- Threats\n  - Competitive risks";
        var matrixRecs = SmartArtDesignStudioViewModel.RecommendLayoutFamiliesForOutline(matrixOutline);
        var matrixTop = matrixRecs.FirstOrDefault(r => r.Family == "Matrix");
        Assert.NotNull(matrixTop);
        Assert.True(matrixTop!.MatchScorePercent >= 88);

        // 3. Cycle outline (with iteration/feedback keywords)
        var cycleOutline = "- Plan\n- Execute\n- Review Feedback\n- Repeat Cycle";
        var cycleRecs = SmartArtDesignStudioViewModel.RecommendLayoutFamiliesForOutline(cycleOutline);
        var cycleTop = cycleRecs.FirstOrDefault(r => r.Family == "Cycle");
        Assert.NotNull(cycleTop);
        Assert.True(cycleTop!.MatchScorePercent >= 90);

        // 4. Pyramid outline (tapering levels: 3 at L1, 2 at L2, 1 at L3)
        var pyramidOutline = "- Level 1 Base A\n- Level 1 Base B\n- Level 1 Base C\n  - Level 2 Mid A\n  - Level 2 Mid B\n    - Level 3 Apex";
        var pyramidRecs = SmartArtDesignStudioViewModel.RecommendLayoutFamiliesForOutline(pyramidOutline);
        var pyramidTop = pyramidRecs.FirstOrDefault(r => r.Family == "Pyramid");
        Assert.NotNull(pyramidTop);
        Assert.True(pyramidTop!.MatchScorePercent >= 80);
    }

    [Fact]
    public void Run147_StructureFlatOutlineByDelimiter_And_FormatDelimiterStructuringSummary()
    {
        Assert.Equal("Structure by delimiter: 0 nodes (Empty outline)",
            SmartArtDesignStudioViewModel.FormatDelimiterStructuringSummary(0, 0, 0));

        var flatOutline = """
            - Backend: REST API Services
            - Backend: Database Layer
            - Frontend — WinUI 3 Desktop
            - Frontend — React Dashboard
            - Deployment - Docker Compose
            """;

        var (structured, parents, items, summary) =
            SmartArtDesignStudioViewModel.StructureFlatOutlineByDelimiter(flatOutline);

        Assert.Equal(3, parents); // Backend, Frontend, Deployment
        Assert.Equal(5, items);
        Assert.Contains("Structure by delimiter (3 parent groups created): Converted 5 items across 5 nodes into hierarchical subtrees", summary);

        // Verify output hierarchy (normalized CRLF)
        var normalized = structured.Replace("\r", "");
        Assert.Contains("- Backend\n  - REST API Services\n  - Database Layer", normalized);
        Assert.Contains("- Frontend\n  - WinUI 3 Desktop\n  - React Dashboard", normalized);
        Assert.Contains("- Deployment\n  - Docker Compose", normalized);
    }

    [Fact]
    public void Run148_BalanceOutlineNestingDepth()
    {
        Assert.Equal("Balance outline depth: 0 bullets (Empty outline)",
            SmartArtDesignStudioViewModel.FormatDepthBalancingSummary(0, 0, 2));

        var shallow = "- Phase 1\n  - Task A\n- Phase 2\n  - Task B";
        var (_, adjZero, totalZero, sumZero) =
            SmartArtDesignStudioViewModel.BalanceOutlineNestingDepth(shallow, maxDepth: 2);
        Assert.Equal(0, adjZero);
        Assert.Equal(4, totalZero);
        Assert.Contains("All 4 bullets within max depth 2 (0 adjustments needed)", sumZero);

        var deep = """
            - Root Level
              - Child Level
                - Subchild Level 3
                  - Deep Child Level 4
            """;

        // Mode 1: Clamp indentation to maxDepth 2
        var (clamped, adjClamped, totalClamped, sumClamped) =
            SmartArtDesignStudioViewModel.BalanceOutlineNestingDepth(deep, maxDepth: 2, mergeIntoParent: false);

        Assert.Equal(2, adjClamped); // Level 3 and Level 4 adjusted
        Assert.Equal(4, totalClamped);
        Assert.Contains("Balance outline depth (Max depth 2): 2 bullets clamped to max depth", sumClamped);

        var clampedNorm = clamped.Replace("\r", "");
        Assert.Contains("- Root Level\n  - Child Level\n  - Subchild Level 3\n  - Deep Child Level 4", clampedNorm);

        // Mode 2: Merge into parent bullet
        var (merged, adjMerged, totalMerged, sumMerged) =
            SmartArtDesignStudioViewModel.BalanceOutlineNestingDepth(deep, maxDepth: 2, mergeIntoParent: true);

        Assert.Equal(2, adjMerged);
        var mergedNorm = merged.Replace("\r", "");
        Assert.Contains("- Root Level\n  - Child Level — Subchild Level 3 — Deep Child Level 4", mergedNorm);
    }

    [Fact]
    public void Run149_AuditOutlineTokenDensity_EvaluatesReadabilityAndDensity()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditOutlineTokenDensity("");
        Assert.Equal("Empty", empty.Rating);
        Assert.Contains("0 bullets", empty.Summary);

        // 2. Optimal concise outline
        var optimalMd = """
            - Plan sprint tasks
            - Build core models
            - Run unit verification
            - Deploy package release
            """;
        var optimal = SmartArtDesignStudioViewModel.AuditOutlineTokenDensity(optimalMd);
        Assert.Equal(4, optimal.TotalBullets);
        Assert.Equal(12, optimal.TotalWords);
        Assert.Equal(3.0, optimal.MeanWords);
        Assert.Equal(0, optimal.VerboseCount);
        Assert.Equal("Optimal", optimal.Rating);
        Assert.Contains("Ideal concise diagram phrasing", optimal.Summary);

        // 3. Verbose overflow risk outline
        var verboseMd = """
            - Short
            - This is an excessively long and verbose multi-sentence outline item describing numerous complex business rules that will almost certainly overflow standard layout containers and chevron boxes
            """;
        var verbose = SmartArtDesignStudioViewModel.AuditOutlineTokenDensity(verboseMd);
        Assert.Equal(2, verbose.TotalBullets);
        Assert.Equal(1, verbose.VerboseCount);
        Assert.Equal("Overflow Risk", verbose.Rating);
        Assert.Contains("Flagged: 1 verbose node", verbose.Summary);
    }

    [Fact]
    public void Run150_AuditLayoutAspectRatio_EvaluatesAspectRatiosAndFit()
    {
        // 1. Process category (Normal & Ultra-wide)
        var procNorm = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Process", 4);
        Assert.Equal("Process", procNorm.Category);
        Assert.Equal(4, procNorm.NodeCount);
        Assert.Equal("16:9 Widescreen", procNorm.RecommendedRatioName);
        Assert.Equal(960, procNorm.OptimalWidth);
        Assert.Equal(540, procNorm.OptimalHeight);
        Assert.Equal("Optimal Fit", procNorm.FitQuality);
        Assert.Contains("Target 16:9 Widescreen (960×540px", procNorm.Summary);

        var procWide = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Process", 9);
        Assert.Equal("2:1 Ultra-Wide", procWide.RecommendedRatioName);
        Assert.Equal(1000, procWide.OptimalWidth);
        Assert.Equal(500, procWide.OptimalHeight);
        Assert.Equal("Crowded (Consider splitting or clamping depth)", procWide.FitQuality);

        // 2. Cycle & Radial (1:1 Square)
        var cycle = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Cycle", 5);
        Assert.Equal("1:1 Square", cycle.RecommendedRatioName);
        Assert.Equal(1.0, cycle.EstimatedAspectRatio);
        Assert.Equal(600, cycle.OptimalWidth);
        Assert.Equal(600, cycle.OptimalHeight);
        Assert.Equal("Optimal Fit", cycle.FitQuality);

        // 3. Matrix (2x2 Quadrant check)
        var matrix4 = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Matrix", 4);
        Assert.Equal("Perfect 2×2 Quadrant", matrix4.FitQuality);

        var matrixOver = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Matrix", 5);
        Assert.Equal("Overloaded for 2×2 Matrix", matrixOver.FitQuality);

        // 4. Pyramid (>6 tiers warning)
        var pyramidCrowded = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Pyramid", 7);
        Assert.Equal("Crowded (>6 tiers)", pyramidCrowded.FitQuality);

        // 5. Hierarchy (>12 nodes zoom warning)
        var hierComplex = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Hierarchy", 14);
        Assert.Equal("Complex Hierarchy (Recommend zoom / pan)", hierComplex.FitQuality);

        // 6. List (Portrait A4 & >10 items scroll warning)
        var listScroll = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("List", 12);
        Assert.Equal("A4 Portrait (1:√2)", listScroll.RecommendedRatioName);
        Assert.Equal("Long Vertical List (Scroll required)", listScroll.FitQuality);

        // 7. Outline markdown counting fallback
        var mdFallback = SmartArtDesignStudioViewModel.AuditLayoutAspectRatio("Process", 0, "- Step 1\n- Step 2\n- Step 3");
        Assert.Equal(3, mdFallback.NodeCount);
    }

    [Fact]
    public void Run151_AuditNodeCharacterBudgets_CalculatesBudgetsAndTruncationRisk()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditNodeCharacterBudgets("Process", "");
        Assert.Equal(0, empty.TotalNodes);
        Assert.Contains("0 nodes (Empty outline)", empty.Summary);

        // 2. Concise Process outline (within 25 char budget)
        var concise = """
            - Ingest Data
              - Validate Schema
            - Transform
            - Export
            """;
        var auditConcise = SmartArtDesignStudioViewModel.AuditNodeCharacterBudgets("Process", concise);
        Assert.Equal(4, auditConcise.TotalNodes);
        Assert.Equal(0, auditConcise.OverBudgetCount);
        Assert.Equal("Minimal (Labels fit within bounds)", auditConcise.TruncationRiskLevel);
        Assert.Contains("0 over budget", auditConcise.Summary);

        // 3. Sprawling Process outline with truncated labels (>25 chars L1, >18 chars L2)
        var sprawling = """
            - Initialize Autonomous Cloud Infrastructure Telemetry Engine
              - Perform Extremely Thorough Recursive Database Schema Integrity Verification
            - Optimize Pipelines
            """;
        var auditSprawling = SmartArtDesignStudioViewModel.AuditNodeCharacterBudgets("Process", sprawling);
        Assert.Equal(3, auditSprawling.TotalNodes);
        Assert.Equal(2, auditSprawling.OverBudgetCount);
        Assert.True(auditSprawling.MaxCharacterCount > 50);
        Assert.Contains("2 nodes over budget", auditSprawling.Summary);
        Assert.Contains("Budget: L1≤25c, L2≤18c", auditSprawling.Summary);

        // 4. Matrix category budgets (L1: 28c, L2: 20c)
        var matrixOutline = """
            - Strengths
              - Rapid Iteration Loop
            - Weaknesses
            - Opportunities
            - Threats
            """;
        var auditMatrix = SmartArtDesignStudioViewModel.AuditNodeCharacterBudgets("Matrix", matrixOutline);
        Assert.Equal(5, auditMatrix.TotalNodes);
        Assert.Equal(28, auditMatrix.Level1Budget);
        Assert.Equal(20, auditMatrix.Level2Budget);
    }

    [Fact]
    public void Run152_RecommendIconsForOutline()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.RecommendIconsForOutline("");
        Assert.Equal(0, empty.TotalNodes);
        Assert.Equal(0, empty.MatchedNodes);
        Assert.Contains("0 nodes (Empty outline)", empty.Summary);

        // 2. Outline with strong semantic matches
        var outline = """
            - Cloud Architecture Pipeline
              - Database Schema Replication
              - Security Authentication Token
            - Real-time Analytics Reporting
            - User Profile Settings
            """;

        var audit = SmartArtDesignStudioViewModel.RecommendIconsForOutline(outline);
        Assert.Equal(5, audit.TotalNodes);
        Assert.Equal(5, audit.MatchedNodes);
        Assert.Equal(100.0, audit.CoveragePercentage);

        // Check matched keywords and glyphs
        Assert.Equal("Cloud", audit.Recommendations[0].IconName);
        Assert.Equal("\uE753", audit.Recommendations[0].IconGlyph);

        Assert.Equal("Database", audit.Recommendations[1].IconName);
        Assert.Equal("\uE81E", audit.Recommendations[1].IconGlyph);

        Assert.Equal("Lock", audit.Recommendations[2].IconName);
        Assert.Equal("\uE72E", audit.Recommendations[2].IconGlyph);

        Assert.Equal("Analytics", audit.Recommendations[3].IconName);
        Assert.Equal("\uE9D9", audit.Recommendations[3].IconGlyph);

        Assert.Equal("Contact", audit.Recommendations[4].IconName);
        Assert.Equal("\uE77B", audit.Recommendations[4].IconGlyph);

        Assert.Contains("5/5 matched (100%)", audit.Summary);

        // 3. Outline with fallback default icons
        var genericOutline = "- Item Alpha\n- Item Beta";
        var genericAudit = SmartArtDesignStudioViewModel.RecommendIconsForOutline(genericOutline);
        Assert.Equal(2, genericAudit.TotalNodes);
        Assert.Equal(0, genericAudit.MatchedNodes);
        Assert.Equal(0.0, genericAudit.CoveragePercentage);
        Assert.Equal("Page", genericAudit.Recommendations[0].IconName);
    }

    [Fact]
    public void Run153_AuditHierarchyBranchingFactor()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditHierarchyBranchingFactor("");
        Assert.Equal(0, empty.TotalItems);
        Assert.Equal(0, empty.MaxDepth);
        Assert.Contains("0 items (Empty outline)", empty.Summary);

        // 2. Balanced 3-level hierarchy: Root -> 3 Children, each child has 2 sub-children
        var balancedOutline = """
            - System Kernel
              - Storage Engine
                - Block Cache
                - WAL Log
              - Compute Scheduler
                - Thread Pool
                - Work Stealing
              - Network Gateway
                - HTTP Parser
                - TLS Handshake
            """;
        var audit = SmartArtDesignStudioViewModel.AuditHierarchyBranchingFactor(balancedOutline);
        Assert.Equal(10, audit.TotalItems);
        Assert.Equal(3, audit.MaxDepth);
        Assert.Equal(3, audit.MaxBranchingFactor);
        Assert.Equal(2.2, audit.AverageBranchingFactor);
        Assert.Equal(1, audit.LevelCounts[1]); // 1 root
        Assert.Equal(3, audit.LevelCounts[2]); // 3 L2
        Assert.Equal(6, audit.LevelCounts[3]); // 6 L3
        Assert.Empty(audit.OverBranchingNodes);
        Assert.Empty(audit.DeepBranches);
        Assert.Equal("Well Balanced", audit.TreeBalance);
        Assert.Equal("Hierarchy / Organization Chart", audit.RecommendedCategory);
        Assert.Contains("10 items across 3 levels", audit.Summary);
    }

    [Fact]
    public void Run154_AuditContainerDensityFit()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditContainerDensityFit(category: "Process / Steps", nodeCount: 0);
        Assert.Equal(0, empty.NodeCount);
        Assert.Equal("Empty", empty.DensityRating);
        Assert.Contains("Insufficient items", empty.Summary);

        // 2. Optimal 4-step process on standard 960px container
        // Available width = 960 - 80 - (3 * 16) = 832. 832 / 4 = 208px per item => Spacious
        var spacious = SmartArtDesignStudioViewModel.AuditContainerDensityFit(category: "Process / Steps", nodeCount: 4, containerWidth: 960.0);
        Assert.Equal(4, spacious.NodeCount);
        Assert.Equal(208.0, spacious.NodeWidth);
        Assert.Equal("Spacious Fit", spacious.DensityRating);
        Assert.Contains("208px/item", spacious.Summary);

        // 3. Overcrowded 12-step process on standard 960px container
        // Available width = 960 - 80 - (11 * 16) = 704. 704 / 12 = 58.7px per item => Overcrowded
        var crowded = SmartArtDesignStudioViewModel.AuditContainerDensityFit(category: "Process / Steps", nodeCount: 12, containerWidth: 960.0);
        Assert.Equal(12, crowded.NodeCount);
        Assert.True(crowded.NodeWidth < 75.0);
        Assert.Equal("Overcrowded (High Overflow Risk)", crowded.DensityRating);
        Assert.Contains("exceeds horizontal slide capacity", crowded.Recommendation);
        Assert.Contains("Overcrowded", crowded.Summary);
    }

    [Fact]
    public void Run155_AuditSiblingTextLengthVariance()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditSiblingTextLengthVariance("");
        Assert.Equal(0, empty.TotalNodes);
        Assert.Equal(0, empty.SiblingGroupCount);
        Assert.Contains("0 nodes (Empty outline)", empty.Summary);

        // 2. Harmonious symmetry outline (siblings have very similar lengths)
        var harmonious = """
            - Planning Stage
            - Building Stage
            - Testing Stage
            - Shipping Stage
            """;
        var auditHarmonious = SmartArtDesignStudioViewModel.AuditSiblingTextLengthVariance(harmonious);
        Assert.Equal(4, auditHarmonious.TotalNodes);
        Assert.Equal(1, auditHarmonious.SiblingGroupCount);
        Assert.True(auditHarmonious.AverageCoefficientOfVariation < 0.15);
        Assert.Equal("Harmonious Symmetry (<25% CV)", auditHarmonious.SymmetryCategory);
        Assert.Contains("Harmonious Symmetry", auditHarmonious.Summary);

        // 3. Asymmetric outline (one sibling has 5 chars, another has 85 chars)
        var asymmetric = """
            - Plan
            - Comprehensive Multithreaded High-Throughput Asynchronous Distributed Network Engine
            """;
        var auditAsym = SmartArtDesignStudioViewModel.AuditSiblingTextLengthVariance(asymmetric);
        Assert.Equal(2, auditAsym.TotalNodes);
        Assert.Equal(1, auditAsym.SiblingGroupCount);
        Assert.True(auditAsym.AverageCoefficientOfVariation > 0.65);
        Assert.Equal("Highly Asymmetric (>65% CV)", auditAsym.SymmetryCategory);
        Assert.Contains("Worst: 'Root'", auditAsym.Summary);
    }

    [Fact]
    public void Run156_AuditLinearFlowMonotonicity()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditLinearFlowMonotonicity("");
        Assert.Equal(0, empty.TotalSteps);
        Assert.Equal("Empty Outline", empty.FlowCategory);
        Assert.Contains("0 steps (Empty outline)", empty.Summary);

        // 2. Strict monotonic progression (Step 1, Step 2, Step 3, Step 4)
        var monotonicOutline = """
            - Step 1: Ingest Data
              - Validate payload
            - Step 2: Transform Model
            - Step 3: Verify Integrity
            - Step 4: Export Deliverable
            """;
        var auditMono = SmartArtDesignStudioViewModel.AuditLinearFlowMonotonicity(monotonicOutline);
        Assert.Equal(4, auditMono.TotalSteps);
        Assert.Equal(4, auditMono.ExplicitlyNumberedSteps);
        Assert.Equal(0, auditMono.InversionCount);
        Assert.Equal("Strict Monotonic Progression", auditMono.FlowCategory);
        Assert.Equal("Process / Steps", auditMono.RecommendedLayoutCategory);
        Assert.Contains("Strict Monotonic Progression", auditMono.Summary);

        // 3. Out-of-order inversions (Step 1, Step 3, Step 2)
        var invertedOutline = """
            - Step 1: Ingest Data
            - Step 3: Verify Integrity
            - Step 2: Transform Model
            """;
        var auditInv = SmartArtDesignStudioViewModel.AuditLinearFlowMonotonicity(invertedOutline);
        Assert.Equal(3, auditInv.TotalSteps);
        Assert.Equal(3, auditInv.ExplicitlyNumberedSteps);
        Assert.Equal(1, auditInv.InversionCount);
        Assert.Equal("Non-Monotonic (Out-of-Order Steps)", auditInv.FlowCategory);
        Assert.Contains("1 out-of-order inversions", auditInv.Summary);
    }

    [Fact]
    public void Run157_AuditCyclicFlowContinuity()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditCyclicFlowContinuity("");
        Assert.Equal(0, empty.TotalSteps);
        Assert.False(empty.HasLoopClosure);
        Assert.Equal("Empty Outline", empty.FlowContinuityCategory);
        Assert.Contains("0 steps", empty.Summary);

        // 2. Insufficient steps (<3)
        var shortOutline = """
            - Plan sprint
            - Execute sprint
            """;
        var shortAudit = SmartArtDesignStudioViewModel.AuditCyclicFlowContinuity(shortOutline);
        Assert.Equal(2, shortAudit.TotalSteps);
        Assert.False(shortAudit.HasLoopClosure);
        Assert.Equal("Insufficient Steps for Cycle (Minimum 3)", shortAudit.FlowContinuityCategory);
        Assert.Equal("Process / Steps", shortAudit.RecommendedLayoutCategory);

        // 3. Closed circular cycle with tail loopback keyword
        var cycleOutline = """
            - Plan Objectives
            - Implement Architecture
            - Validate Verification Gate
            - Iterate and Repeat Cycle
            """;
        var cycleAudit = SmartArtDesignStudioViewModel.AuditCyclicFlowContinuity(cycleOutline);
        Assert.Equal(4, cycleAudit.TotalSteps);
        Assert.True(cycleAudit.HasLoopClosure);
        Assert.Equal("Tail Keyword Loopback", cycleAudit.ClosureMechanism);
        Assert.Equal("Closed Circular Feedback Loop", cycleAudit.FlowContinuityCategory);
        Assert.Equal("Cycle / Circular", cycleAudit.RecommendedLayoutCategory);
        Assert.True(cycleAudit.ContinuityScore >= 0.70);
        Assert.Contains("Closed Loop (↺)", cycleAudit.Summary);
    }

    [Fact]
    public void Run158_AuditMatrixQuadrantBalance()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditMatrixQuadrantBalance("");
        Assert.Equal(0, empty.TotalQuadrants);
        Assert.Equal("Empty Outline", empty.BalanceCategory);
        Assert.Contains("0 quadrants", empty.Summary);

        // 2. Perfect 4-quadrant SWOT matrix (2 items per quadrant)
        var swotOutline = """
            - Strengths
              - High performance architecture
              - Native OpenXML pipeline
            - Weaknesses
              - WinUI 3 initial startup time
              - Memory footprint on huge docs
            - Opportunities
              - Enterprise document automation
              - AI-assisted layout styling
            - Threats
              - Legacy Word format regressions
              - Cross-platform web competitors
            """;

        var swotAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantBalance(swotOutline);
        Assert.Equal(4, swotAudit.TotalQuadrants);
        Assert.Equal(8, swotAudit.TotalItems);
        Assert.Equal(2.0, swotAudit.AverageItemsPerQuadrant);
        Assert.Equal(0.0, swotAudit.ItemsCoefficientOfVariation);
        Assert.Equal(100.0, swotAudit.BalanceScore);
        Assert.Equal("Perfect 2x2 Quad Symmetry", swotAudit.BalanceCategory);
        Assert.Contains("4 quadrants (2x2), 8 items · Perfect 2x2 Quad Symmetry", swotAudit.Summary);

        // 3. Incomplete matrix (<4 quadrants)
        var twoQuads = """
            - Pros
              - Fast
            - Cons
              - Expensive
            """;
        var twoAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantBalance(twoQuads);
        Assert.Equal(2, twoAudit.TotalQuadrants);
        Assert.Equal("Incomplete Matrix (2/4 Quadrants)", twoAudit.BalanceCategory);
    }

    [Fact]
    public void Run159_NormalizeMatrixQuadrantHeaders_CanonicalizesTitles()
    {
        // 1. Empty outline
        var emptyRes = SmartArtDesignStudioViewModel.NormalizeMatrixQuadrantHeaders("");
        Assert.Equal(0, emptyRes.NormalizedHeadersCount);
        Assert.Equal("None", emptyRes.DetectedArchetype);

        // 2. SWOT outline with informal titles (S, W, O, T)
        var rawSwot = """
            - S
              - Fast parsing
            - W
              - Memory limits
            - O
              - Cloud sync
            - T
              - Network loss
            """;
        var swotRes = SmartArtDesignStudioViewModel.NormalizeMatrixQuadrantHeaders(rawSwot);
        Assert.Equal(4, swotRes.NormalizedHeadersCount);
        Assert.Equal("SWOT Matrix", swotRes.DetectedArchetype);
        Assert.Contains("- Strengths", swotRes.NormalizedMarkdown);
        Assert.Contains("- Weaknesses", swotRes.NormalizedMarkdown);
        Assert.Contains("- Opportunities", swotRes.NormalizedMarkdown);
        Assert.Contains("- Threats", swotRes.NormalizedMarkdown);
        Assert.Contains("Fast parsing", swotRes.NormalizedMarkdown);

        // 3. Eisenhower matrix
        var rawEisenhower = """
            - Urgent & Important
              - Server down
            - Not Urgent & Important
              - Refactoring
            - Urgent & Not Important
              - Phone call
            - Not Urgent & Not Important
              - Spam
            """;
        var eRes = SmartArtDesignStudioViewModel.NormalizeMatrixQuadrantHeaders(rawEisenhower);
        Assert.Equal("Eisenhower Priority Matrix", eRes.DetectedArchetype);
        Assert.Contains("Do First", eRes.NormalizedMarkdown);
        Assert.Contains("Schedule", eRes.NormalizedMarkdown);
    }

    [Fact]
    public void AuditPyramidStageProportionality_EvaluatesTaperProgression()
    {
        // 1. Empty or whitespace
        var emptyAudit = SmartArtDesignStudioViewModel.AuditPyramidStageProportionality("");
        Assert.Equal(0, emptyAudit.StageCount);
        Assert.Equal("Empty Outline", emptyAudit.ProportionalityCategory);

        // 2. Monotonically tapered top-to-bottom funnel (Inverted Funnel)
        var funnelMd = """
            - Top-of-Funnel Brand Awareness & Public Marketing Outreach Campaigns
              - Social ads
              - Search engine marketing
              - Public blogs
            - Mid-Funnel Lead Qualification & Product Trial Registrations
              - Free tier signups
              - Webinar attendees
            - Bottom-Funnel Enterprise Purchase Contracts
              - Custom terms
            """;
        var funnelAudit = SmartArtDesignStudioViewModel.AuditPyramidStageProportionality(funnelMd);
        Assert.Equal(3, funnelAudit.StageCount);
        Assert.True(funnelAudit.IsMonotonicallyTapered);
        Assert.Contains("Top-to-Bottom", funnelAudit.ProportionalityCategory);
        Assert.True(funnelAudit.TaperProgressionRatio < 1.0);
        Assert.True(funnelAudit.ProportionalityScore >= 80.0);

        // 3. Upward pyramid (base largest at bottom)
        var pyramidMd = """
            - Apex Tier Executive Strategy
            - Management Tier Operational Coordination & Scheduling
            - Foundation Tier Infrastructure, Hardware, Core Network Routing, & Physical Security
            """;
        var pyramidAudit = SmartArtDesignStudioViewModel.AuditPyramidStageProportionality(pyramidMd);
        Assert.Equal(3, pyramidAudit.StageCount);
        Assert.True(pyramidAudit.IsMonotonicallyTapered);
        Assert.Contains("Bottom-to-Top", pyramidAudit.ProportionalityCategory);
        Assert.True(pyramidAudit.TaperProgressionRatio > 1.0);

        // 4. Uniform slices
        var uniformMd = """
            - Stage 1: Assessment
            - Stage 2: Assessment
            - Stage 3: Assessment
            """;
        var uniformAudit = SmartArtDesignStudioViewModel.AuditPyramidStageProportionality(uniformMd);
        Assert.Equal(3, uniformAudit.StageCount);
        Assert.Equal("Uniform Slices (Balanced Hierarchy)", uniformAudit.ProportionalityCategory);
    }

    [Fact]
    public void Run161_AuditVennDiagramCompleteness_Evaluates2And3SetVennOutlines()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditVennDiagramCompleteness("");
        Assert.Equal(0, emptyAudit.PrimarySetCount);
        Assert.Equal("Empty Outline", emptyAudit.CompletenessCategory);

        // 2. Complete 2-set Venn (3/3 partitions)
        var twoSetMd = """
            - Frontend Development
              - React
              - TypeScript
            - Backend Services
              - C# .NET
              - PostgreSQL
            - Fullstack Overlap (Both)
              - Node API
              - End-to-end integration
            """;
        var twoSetAudit = SmartArtDesignStudioViewModel.AuditVennDiagramCompleteness(twoSetMd);
        Assert.Equal(2, twoSetAudit.PrimarySetCount);
        Assert.Equal(3, twoSetAudit.ExpectedRegions);
        Assert.Equal(3, twoSetAudit.DefinedRegions);
        Assert.Equal(0, twoSetAudit.MissingRegionsCount);
        Assert.Equal(100.0, twoSetAudit.CompletenessPercentage);
        Assert.Equal("Complete 2-Set Venn (3/3 Partitions)", twoSetAudit.CompletenessCategory);
        Assert.Contains("Complete", twoSetAudit.Summary);

        // 3. Partial 2-set Venn (missing overlap)
        var partialTwoSetMd = """
            - Design Systems
            - Engineering Team
            """;
        var partialTwoAudit = SmartArtDesignStudioViewModel.AuditVennDiagramCompleteness(partialTwoSetMd);
        Assert.Equal(2, partialTwoAudit.PrimarySetCount);
        Assert.Equal(3, partialTwoAudit.ExpectedRegions);
        Assert.Equal(2, partialTwoAudit.DefinedRegions);
        Assert.Equal(1, partialTwoAudit.MissingRegionsCount);
        Assert.Equal("Partial 2-Set Venn (Missing Overlap)", partialTwoAudit.CompletenessCategory);
        Assert.NotEmpty(partialTwoAudit.MissingPartitions);

        // 4. Complete 3-set Venn (7/7 partitions)
        var threeSetMd = """
            - Set Alpha: Usability
            - Set Beta: Viability
            - Set Gamma: Feasibility
            - Overlap Alpha & Beta
            - Overlap Beta & Gamma
            - Overlap Alpha & Gamma
            - Core Intersection (All Three Sets)
            """;
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = threeSetMd;
        var threeAudit = vm.AuditVennDiagramCompleteness();
        Assert.Equal(3, threeAudit.PrimarySetCount);
        Assert.Equal(7, threeAudit.ExpectedRegions);
        Assert.Equal(7, threeAudit.DefinedRegions);
        Assert.Equal(0, threeAudit.MissingRegionsCount);
        Assert.Equal(100.0, threeAudit.CompletenessPercentage);
        Assert.Equal("Complete 3-Set Venn (7/7 Partitions)", threeAudit.CompletenessCategory);
    }

    [Fact]
    public void Run162_AuditTimelineMilestoneChronology_ValidatesMonotonicityAndDateFormats()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditTimelineMilestoneChronology("");
        Assert.Equal(0, emptyAudit.TotalMilestones);
        Assert.Equal("Empty Timeline Outline", emptyAudit.Category);
        Assert.Equal(100.0, emptyAudit.MonotonicityScore);

        // 2. Monotonic ascending chronology with uniform ISO dates
        var validIsoMd = """
            - 2026-01-15: Architecture Blueprint
            - 2026-04-01: Beta Release
            - 2026-09-30: General Availability
            """;
        var validAudit = SmartArtDesignStudioViewModel.AuditTimelineMilestoneChronology(validIsoMd);
        Assert.Equal(3, validAudit.TotalMilestones);
        Assert.Equal(3, validAudit.DatedMilestones);
        Assert.Equal(0, validAudit.ChronologicalInversions);
        Assert.Equal(100.0, validAudit.MonotonicityScore);
        Assert.True(validAudit.IsUniformDateFormat);
        Assert.Equal("ISO Date (YYYY-MM-DD)", validAudit.DominantDateFormat);
        Assert.Equal("Strict Chronological Progression (100% Monotonic)", validAudit.Category);
        Assert.Equal(validAudit.Summary, SmartArtDesignStudioViewModel.FormatTimelineChronologySummary(
            validAudit.TotalMilestones, validAudit.DatedMilestones, validAudit.ChronologicalInversions,
            validAudit.MonotonicityScore, validAudit.IsUniformDateFormat, validAudit.DominantDateFormat,
            validAudit.Category, validAudit.Inversions));

        // 3. Chronological inversion
        var invertedMd = """
            - 2026-01-15: Stage 1
            - 2026-09-30: Stage 3 (Out of order)
            - 2026-04-01: Stage 2
            """;
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = invertedMd;
        var invertedAudit = vm.AuditTimelineMilestoneChronology();
        Assert.Equal(3, invertedAudit.TotalMilestones);
        Assert.Equal(3, invertedAudit.DatedMilestones);
        Assert.Equal(1, invertedAudit.ChronologicalInversions);
        Assert.True(invertedAudit.MonotonicityScore < 100.0);
        Assert.Contains("Chronological Inversion Detected", invertedAudit.Category);
        Assert.NotEmpty(invertedAudit.Inversions);

        // 4. Undated milestones
        var undatedMd = """
            - Concept Phase
            - Planning Phase
            - Execution Phase
            """;
        var undatedAudit = SmartArtDesignStudioViewModel.AuditTimelineMilestoneChronology(undatedMd);
        Assert.Equal(3, undatedAudit.TotalMilestones);
        Assert.Equal(0, undatedAudit.DatedMilestones);
        Assert.Equal("Undated Sequential Steps", undatedAudit.Category);
    }

    [Fact]
    public void Run163_AuditCyclicProcessConvergence_ValidatesClosingFeedbackLoops()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditCyclicProcessConvergence("");
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal("Empty Outline", emptyAudit.ConvergenceCategory);
        Assert.Equal(100.0, emptyAudit.LoopConvergenceScore);
        Assert.False(emptyAudit.HasRecurrenceClosure);

        // 2. Insufficient stages
        var singleAudit = SmartArtDesignStudioViewModel.AuditCyclicProcessConvergence("- Single Phase");
        Assert.Equal(1, singleAudit.TotalStages);
        Assert.Equal("Incomplete Loop (1 Stage)", singleAudit.ConvergenceCategory);

        // 3. Closed cyclic process with return loop
        var closedCycleMd = """
            - Plan: Define requirements
            - Do: Implement changes
            - Check: Verify tests
            - Act: Return to Plan for iterative refinement
            """;
        var closedAudit = SmartArtDesignStudioViewModel.AuditCyclicProcessConvergence(closedCycleMd);
        Assert.Equal(4, closedAudit.TotalStages);
        Assert.True(closedAudit.HasRecurrenceClosure);
        Assert.Contains("Explicit Recurrence", closedAudit.ClosureMechanism);
        Assert.True(closedAudit.LoopConvergenceScore >= 80.0);
        Assert.Equal("Seamless Closed-Loop Convergence", closedAudit.ConvergenceCategory);
        Assert.Equal(closedAudit.Summary, SmartArtDesignStudioViewModel.FormatCyclicProcessSummary(
            closedAudit.TotalStages, closedAudit.HasRecurrenceClosure, closedAudit.TerminalSinkCount,
            closedAudit.StageWordCountVariance, closedAudit.LoopConvergenceScore,
            closedAudit.ClosureMechanism, closedAudit.ConvergenceCategory));

        // 4. Open process lacking return arc
        var openCycleMd = """
            - Inception
            - Construction
            - Transition
            """;
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = openCycleMd;
        var openAudit = vm.AuditCyclicProcessConvergence();
        Assert.Equal(3, openAudit.TotalStages);
        Assert.False(openAudit.HasRecurrenceClosure);
        Assert.True(openAudit.LoopConvergenceScore < 100.0);
        Assert.Equal("Open-Ended Linear Sequence (Missing Recurrence Closure)", openAudit.ConvergenceCategory);
    }

    [Fact]
    public void Run164_AuditHierarchySpanOfControl_ValidatesManagerReportsAndBranching()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditHierarchySpanOfControl("", isStatic: true);
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal("Empty Outline", emptyAudit.HierarchyCategory);
        Assert.Equal(100.0, emptyAudit.HierarchyBalanceScore);
        Assert.Contains("Outline is empty", emptyAudit.Summary);

        // 2. Flat non-hierarchical list (all level 0)
        var flatMd = """
            - Item 1
            - Item 2
            - Item 3
            """;
        var flatAudit = SmartArtDesignStudioViewModel.AuditHierarchySpanOfControl(flatMd, isStatic: true);
        Assert.Equal(3, flatAudit.TotalNodes);
        Assert.Equal(0, flatAudit.ManagerCount);
        Assert.Equal(3, flatAudit.IndividualContributorCount);
        Assert.Equal("Flat Non-Hierarchical List", flatAudit.HierarchyCategory);
        Assert.Equal(100.0, flatAudit.HierarchyBalanceScore);

        // 3. Balanced organizational hierarchy
        var orgMd = """
            - Chief Executive Officer
              - VP Engineering
                - Senior Backend Dev
                - Frontend Architect
                - QA Automation Lead
              - VP Product
                - Principal PM
                - UX Research Lead
                - Technical Writer
            """;
        var vm = new SmartArtDesignStudioViewModel();
        vm.MarkdownText = orgMd;
        var orgAudit = vm.AuditHierarchySpanOfControl();

        Assert.Equal(9, orgAudit.TotalNodes);
        Assert.Equal(3, orgAudit.ManagerCount); // CEO, VP Eng, VP Product
        Assert.Equal(6, orgAudit.IndividualContributorCount);
        Assert.Equal(2, orgAudit.MaxDepth);
        Assert.True(orgAudit.MeanSpanOfControl >= 2.5);
        Assert.Equal(0, orgAudit.OverloadedManagerCount);
        Assert.Equal(1, orgAudit.UnderutilizedManagerCount); // CEO has 2 reports (< 3)
        Assert.NotEmpty(orgAudit.NodeSpans);
        Assert.Equal(orgAudit.Summary, SmartArtDesignStudioViewModel.FormatHierarchySpanSummary(
            orgAudit.TotalNodes, orgAudit.ManagerCount, orgAudit.IndividualContributorCount,
            orgAudit.MaxDepth, orgAudit.MeanSpanOfControl, orgAudit.SpanStandardDeviation,
            orgAudit.ManagerToReportRatio, orgAudit.OverloadedManagerCount, orgAudit.UnderutilizedManagerCount,
            orgAudit.HierarchyBalanceScore, orgAudit.HierarchyCategory));
    }

    [Fact]
    public void Run165_AuditLayoutTypographyAndFontFit_EvaluatesNodeDensityAndOverflowRisk()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditLayoutTypographyAndFontFit("", isStatic: true);
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal("Empty Outline", emptyAudit.LegibilityCategory);
        Assert.Equal(100.0, emptyAudit.TypographicLegibilityScore);
        Assert.Contains("Outline is empty", emptyAudit.Summary);

        // 2. Concise clean outline
        var conciseMd = """
            - Initiate
            - Plan
            - Execute
            - Monitor
            - Close
            """;
        var conciseAudit = SmartArtDesignStudioViewModel.AuditLayoutTypographyAndFontFit(conciseMd, isStatic: true);
        Assert.Equal(5, conciseAudit.TotalNodes);
        Assert.Equal(0, conciseAudit.ClippingRiskCount);
        Assert.Equal(100.0, conciseAudit.TypographicLegibilityScore);
        Assert.Equal(conciseAudit.Summary, SmartArtDesignStudioViewModel.FormatTypographyFontFitSummary(
            5, conciseAudit.MinFontSizePt, conciseAudit.MaxFontSizePt, conciseAudit.MeanFontSizePt,
            0, 100.0, conciseAudit.LegibilityCategory));

        // 3. Verbose overflow risk node
        var verboseMd = """
            - Concise Node
            - This is an excessively long node label with multiple sentences designed to cause severe overflow and tight typography constraints inside SmartArt layout bounding boxes
            """;
        var verboseAudit = SmartArtDesignStudioViewModel.AuditLayoutTypographyAndFontFit(verboseMd, isStatic: true);
        Assert.Equal(2, verboseAudit.TotalNodes);
        Assert.True(verboseAudit.ClippingRiskCount >= 1);
        Assert.True(verboseAudit.TypographicLegibilityScore < 100.0);
        Assert.Contains(verboseAudit.LegibilityCategory, new[] { "Text Overflow Risk Detected", "Tight Text Fitting", "Minor Text Overflow Risks" });
    }

    [Fact]
    public void Run166_AuditColorHierarchyAndGradients_EvaluatesDepthLuminanceProgression()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditColorHierarchyAndGradients("", isStatic: true);
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal(100.0, emptyAudit.ChromaticHarmonyScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("Outline is empty", emptyAudit.Summary);

        // 2. Monotonic multi-tier outline
        var hierarchyMd = """
            - Root Initiative
              - Strategic Priority A
                - Tactical Task 1
                - Tactical Task 2
              - Strategic Priority B
            """;
        var hierarchyAudit = SmartArtDesignStudioViewModel.AuditColorHierarchyAndGradients(hierarchyMd, isStatic: true);
        Assert.Equal(5, hierarchyAudit.TotalNodes);
        Assert.Equal(2, hierarchyAudit.MaxDepth);
        Assert.Equal(3, hierarchyAudit.DepthLevels.Count);
        Assert.True(hierarchyAudit.IsMonotonicProgression);
        Assert.True(hierarchyAudit.ChromaticHarmonyScore >= 80.0);
        Assert.Equal(hierarchyAudit.Summary, SmartArtDesignStudioViewModel.FormatColorHierarchySummary(
            5, 2, 3, hierarchyAudit.MeanContrastRatio, hierarchyAudit.ContrastDeficitCount,
            hierarchyAudit.ChromaticHarmonyScore, hierarchyAudit.IsMonotonicProgression,
            hierarchyAudit.GradientDirection, hierarchyAudit.Category));

        // 3. Contrast deficit check with low contrast palette
        var lowContrastPalette = new[] { "#555555", "#565656", "#575757" };
        var vm = new SmartArtDesignStudioViewModel();
        var deficitAudit = vm.AuditColorHierarchyAndGradients(hierarchyMd, lowContrastPalette);
        Assert.True(deficitAudit.ContrastDeficitCount >= 1);
        Assert.True(deficitAudit.ChromaticHarmonyScore < 100.0);
    }

    [Fact]
    public void Run167_AuditLinearFlowChevronRhythm_EvaluatesStepPacingAndOutliers()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditLinearFlowChevronRhythm("", isStatic: true);
        Assert.Equal(0, emptyAudit.TotalSteps);
        Assert.Equal(0, emptyAudit.TotalWords);
        Assert.Equal(100.0, emptyAudit.ProgressionRhythmScore);
        Assert.Equal("Empty Outline", emptyAudit.RhythmCategory);
        Assert.Contains("0 steps", emptyAudit.Summary);

        // 2. Harmonic balanced 4-step linear flow (each ~3 words)
        var balancedMd = string.Join("\n",
            "- Initiate project scope",
            "- Develop core architecture",
            "- Execute test validation",
            "- Deploy release artifacts");

        var balancedAudit = SmartArtDesignStudioViewModel.AuditLinearFlowChevronRhythm(balancedMd, isStatic: true);
        Assert.Equal(4, balancedAudit.TotalSteps);
        Assert.Equal(12, balancedAudit.TotalWords);
        Assert.Equal(3.0, balancedAudit.MeanWordsPerStep);
        Assert.Equal(0.0, balancedAudit.WordCountStdDev);
        Assert.Equal(0.0, balancedAudit.PacingVariationCoefficient);
        Assert.Equal(0, balancedAudit.PacingOutlierCount);
        Assert.Equal(100.0, balancedAudit.ProgressionRhythmScore);
        Assert.Equal("Harmonic Step Pacing & Balanced Rhythm", balancedAudit.RhythmCategory);
        Assert.Equal(balancedAudit.Summary, SmartArtDesignStudioViewModel.FormatLinearFlowSummary(
            4, 12, 3.0, 0.0, 0, 100.0, balancedAudit.RhythmCategory));

        // 3. Outlier step with massive paragraph among concise steps
        var outlierMd = string.Join("\n",
            "- Step One",
            "- Step Two with a drastically long and verbose explanatory description that contains way too many unnecessary words for a clean chevron arrow block and severely unbalances the visual layout rhythm",
            "- Step Three");

        var outlierAudit = SmartArtDesignStudioViewModel.AuditLinearFlowChevronRhythm(outlierMd, isStatic: true);
        Assert.Equal(3, outlierAudit.TotalSteps);
        Assert.True(outlierAudit.PacingOutlierCount >= 1);
        Assert.True(outlierAudit.PacingVariationCoefficient > 0.5);
        Assert.True(outlierAudit.ProgressionRhythmScore < 85.0);
        Assert.Equal("Pacing Imbalance / Wordy Steps Detected", outlierAudit.RhythmCategory);
    }

    [Fact]
    public void Run168_AuditRadialCycleOrbitalBalance_DetectsSpokeBalance()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditRadialCycleOrbitalBalance("", isStatic: true);
        Assert.Equal(0, emptyAudit.TotalSpokes);
        Assert.Equal(100.0, emptyAudit.SpokeBalanceScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        // 2. Harmonic 4-spoke radial cycle with central hub
        var balancedMd = string.Join("\n",
            "# Core System Hub",
            "- Frontend Presentation Layer",
            "- Backend API Service",
            "- Database Storage Cluster",
            "- Realtime Messaging Bus");

        var balancedAudit = SmartArtDesignStudioViewModel.AuditRadialCycleOrbitalBalance(balancedMd, isStatic: true);
        Assert.Equal("Core System Hub", balancedAudit.HubTitle);
        Assert.Equal(4, balancedAudit.TotalSpokes);
        Assert.Equal(90.0, balancedAudit.AngularSectorStepDegrees); // 360 / 4 = 90
        Assert.Equal(0, balancedAudit.ImbalancedSpokeCount);
        Assert.True(balancedAudit.SpokeBalanceScore >= 90.0);
        Assert.Equal("Harmonic Radial Orbital Cycle", balancedAudit.Category);

        // 3. Degenerate cycle (<3 spokes)
        var degenerateMd = string.Join("\n",
            "# Two Spokes",
            "- Spoke A",
            "- Spoke B");

        var degenerateAudit = SmartArtDesignStudioViewModel.AuditRadialCycleOrbitalBalance(degenerateMd, isStatic: true);
        Assert.Equal(2, degenerateAudit.TotalSpokes);
        Assert.Equal(65.0, degenerateAudit.SpokeBalanceScore);
        Assert.Contains("< 3 Spokes", degenerateAudit.Category);
    }

    [Fact]
    public void Run169_AuditPyramidTaperingAndMassBalance_DetectsInvertedMassTraps()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditPyramidTaperingAndMassBalance("");
        Assert.Equal(0, emptyAudit.TotalTiers);
        Assert.Equal(100.0, emptyAudit.TaperSmoothnessScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        // 2. Balanced standard pyramid (Apex is brief, base is broad and descriptive)
        var balancedMd = string.Join("\n",
            "- Vision & Executive Strategy",
            "- Tactical Objectives & Milestone Plan",
            "- Detailed Operational Execution Workflows and Infrastructure Requirements Across Global Engineering Teams");

        var balancedAudit = SmartArtDesignStudioViewModel.AuditPyramidTaperingAndMassBalance(balancedMd, isInverted: false);
        Assert.Equal(3, balancedAudit.TotalTiers);
        Assert.False(balancedAudit.IsInverted);
        Assert.Equal(0, balancedAudit.InvertedMassTrapCount);
        Assert.True(balancedAudit.TaperSmoothnessScore >= 90.0);
        Assert.Equal("Harmonic Proportional Mass Taper", balancedAudit.Category);

        // 3. Inverted mass trap (Apex has massive word dump while base is tiny single word)
        var trapMd = string.Join("\n",
            "- Highly Verbose Apex Strategy Requiring Extensive Elaboration and Deep Strategic Architectural Justification Across Multiple Paradigms",
            "- Mid Layer",
            "- Base");

        var trapAudit = SmartArtDesignStudioViewModel.AuditPyramidTaperingAndMassBalance(trapMd, isInverted: false);
        Assert.Equal(3, trapAudit.TotalTiers);
        Assert.True(trapAudit.InvertedMassTrapCount >= 1);
        Assert.True(trapAudit.Tiers[0].HasInvertedMassTrap);
        Assert.True(trapAudit.TaperSmoothnessScore < 90.0);

        // 4. Inverted funnel mode
        var funnelAudit = SmartArtDesignStudioViewModel.AuditPyramidTaperingAndMassBalance(balancedMd, isInverted: true);
        Assert.True(funnelAudit.IsInverted);
        Assert.Equal(3, funnelAudit.TotalTiers);
    }

    [Fact]
    public void Run170_AuditMatrixQuadrantBalance_EvaluatesSymmetryAndDetectsTraps()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetry("");
        Assert.Equal(0, emptyAudit.TotalItems);
        Assert.Equal(100.0, emptyAudit.SymmetryBalanceScore);
        Assert.Equal("Empty Matrix", emptyAudit.SymmetryCategory);

        // 2. Harmonic balanced 4-quadrant outline (e.g. SWOT analysis)
        var balancedMd = string.Join("\n",
            "- Strengths",
            "  - Fast performance",
            "  - Robust type system",
            "- Weaknesses",
            "  - High memory usage",
            "  - Complex configuration",
            "- Opportunities",
            "  - Cloud native expansion",
            "  - Cross platform reach",
            "- Threats",
            "  - Legacy technical debt",
            "  - Emerging competition"
        );

        var balancedAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetry(balancedMd);
        Assert.Equal(4, balancedAudit.TotalQuadrants);
        Assert.Equal(8, balancedAudit.TotalItems);
        Assert.Equal(0, balancedAudit.Quadrants.Count(q => q.IsEmpty));
        Assert.Equal(0, balancedAudit.Quadrants.Count(q => q.IsOverloaded));
        Assert.True(balancedAudit.SymmetryBalanceScore >= 90.0);
        Assert.Equal("Harmonic 4-Quadrant Symmetry", balancedAudit.SymmetryCategory);
        Assert.Contains("4/4 quadrants populated", balancedAudit.Summary);

        // 3. Imbalanced matrix: 1 huge overloaded quadrant and 2 empty quadrants
        var imbalancedMd = string.Join("\n",
            "- Mega Quadrant With Many Items And Verbose Detailed Descriptions Explaining Everything In Huge Length And Text Mass",
            "  - Item one",
            "  - Item two",
            "  - Item three",
            "  - Item four",
            "  - Item five",
            "- Tiny Quadrant",
            "  - Brief item"
        );

        var imbalancedAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetry(imbalancedMd);
        Assert.Equal(4, imbalancedAudit.TotalQuadrants);
        Assert.Equal(2, imbalancedAudit.Quadrants.Count(q => q.IsEmpty));
        Assert.True(imbalancedAudit.Quadrants.Any(q => q.IsOverloaded));
        Assert.True(imbalancedAudit.SymmetryBalanceScore < 70.0);
        Assert.Contains("Critical Matrix Imbalance", imbalancedAudit.SymmetryCategory);

        // 4. Formatter test
        string formatted = SmartArtDesignStudioViewModel.FormatMatrixQuadrantSummary(
            imbalancedAudit.Quadrants.Count(q => !q.IsEmpty),
            imbalancedAudit.TotalItems,
            imbalancedAudit.TotalWords,
            imbalancedAudit.Quadrants.Count(q => q.IsEmpty),
            imbalancedAudit.Quadrants.Count(q => q.IsOverloaded),
            imbalancedAudit.SymmetryBalanceScore,
            imbalancedAudit.SymmetryCategory);
        Assert.Equal(imbalancedAudit.Summary, formatted);
    }

    [Fact]
    public void Run171_AuditVennDiagramOverlapAndSetBalance_ValidatesLensAndSetInclusion()
    {
        // 1. Empty outline
        var empty = SmartArtDesignStudioViewModel.AuditVennDiagramOverlapAndSetBalance("");
        Assert.Equal(0, empty.PrimarySetCount);
        Assert.Equal(0, empty.PopulatedRegions);
        Assert.Equal(100.0, empty.VennBalanceScore);
        Assert.Equal("Empty Outline", empty.Category);

        // 2. Balanced 2-set Venn with shared intersection
        string balancedMd = string.Join("\n",
            "- Set A: Product Engineering",
            "  - Architecture",
            "  - Implementation",
            "- Set B: User Research",
            "  - Usability Testing",
            "  - User Interviews",
            "- Shared Intersection (A & B)",
            "  - Interactive Prototyping"
        );

        var balanced = SmartArtDesignStudioViewModel.AuditVennDiagramOverlapAndSetBalance(balancedMd);
        Assert.Equal(2, balanced.PrimarySetCount);
        Assert.Equal(3, balanced.PopulatedRegions);
        Assert.Equal(0, balanced.EmptyTrapRegions);
        Assert.Equal(0.285, balanced.LensOverlapChordAreaFraction);
        Assert.Equal(100.0, balanced.VennBalanceScore);
        Assert.Equal("Balanced 2-Set Dual Overlap", balanced.Category);
        Assert.Empty(balanced.BalanceAlerts);

        // 3. Disjoint / Empty intersection trap
        string disjointMd = string.Join("\n",
            "- Set Alpha: Front-end Systems",
            "  - HTML/CSS",
            "- Set Beta: Embedded Microcontrollers",
            "  - Firmware Assembly"
        );

        var disjoint = SmartArtDesignStudioViewModel.AuditVennDiagramOverlapAndSetBalance(disjointMd);
        Assert.Equal(2, disjoint.PrimarySetCount);
        Assert.True(disjoint.EmptyTrapRegions >= 1);
        Assert.True(disjoint.VennBalanceScore < 80.0);
        Assert.Equal("Disjoint / Empty Intersection Traps", disjoint.Category);
        Assert.NotEmpty(disjoint.BalanceAlerts);

        // 4. Formatter test
        string formatted = SmartArtDesignStudioViewModel.FormatVennDiagramSummary(
            balanced.PrimarySetCount, balanced.Regions.Count, balanced.TotalItems,
            balanced.EmptyTrapRegions, balanced.LensOverlapChordAreaFraction,
            balanced.VennBalanceScore, balanced.Category);
        Assert.Equal(balanced.Summary, formatted);
    }

    [Fact]
    public void Run172_AuditProcessSequencePacing_ValidatesStepsAndAspectRatios()
    {
        var vm = new SmartArtDesignStudioViewModel();

        var empty = vm.AuditProcessSequencePacing("");
        Assert.Equal(0, empty.TotalSteps);
        Assert.Equal(100.0, empty.PacingBalanceScore);
        Assert.Equal("Empty Outline", empty.SequenceCategory);

        string optimalMd = string.Join("\n",
            "1. Discover: Research customer requirements and metrics.",
            "2. Design: Architect interactive wireframes and models.",
            "3. Implement: Build production code with tests.",
            "4. Deploy: Release automated package updates."
        );
        var optimalAudit = vm.AuditProcessSequencePacing(optimalMd);
        Assert.Equal(4, optimalAudit.TotalSteps);
        Assert.True(optimalAudit.TotalWords >= 15);
        Assert.Equal(0, optimalAudit.OverloadedStepCount);
        Assert.True(optimalAudit.PacingBalanceScore >= 90.0);
        Assert.Equal("Optimal Linear Flow", optimalAudit.SequenceCategory);
        Assert.True(optimalAudit.EstimatedChevronAspectRatio >= 1.0);

        string imbalancedMd = string.Join("\n",
            "- Concept",
            "- Ultra Massive Detailed Execution Phase: Comprehensive investigations regarding complex engineering specifications and extensive requirements with abundant verbose prose exceeding thirty words in length for testing",
            "- Ship"
        );
        var imbalancedAudit = vm.AuditProcessSequencePacing(imbalancedMd);
        Assert.Equal(3, imbalancedAudit.TotalSteps);
        Assert.True(imbalancedAudit.OverloadedStepCount >= 1);
        Assert.NotEmpty(imbalancedAudit.PacingAlerts);

        string formatted = SmartArtDesignStudioViewModel.FormatProcessSequenceSummary(
            optimalAudit.TotalSteps, optimalAudit.TotalWords, optimalAudit.MeanWordsPerStep,
            optimalAudit.WordStandardDeviation, optimalAudit.EstimatedChevronAspectRatio,
            optimalAudit.OverloadedStepCount, optimalAudit.TooBriefStepCount,
            optimalAudit.PacingBalanceScore, optimalAudit.SequenceCategory);
        Assert.Equal(optimalAudit.Summary, formatted);
    }

    [Fact]
    public void Run173_AuditHierarchyAspectAndBranching_EvaluatesDepthBreadthAndFans()
    {
        var vm = new SmartArtDesignStudioViewModel();
        var empty = vm.AuditHierarchyAspectAndBranching("");
        Assert.Equal(0, empty.TotalNodes);
        Assert.Equal(100.0, empty.HierarchyBalanceScore);
        Assert.Equal("Empty Hierarchy", empty.Category);

        string balancedTree = string.Join("\n",
            "- Executive Director",
            "  - Engineering Lead",
            "    - Frontend Architect",
            "    - Systems Engineer",
            "  - Product Manager",
            "    - User Researcher",
            "    - Design Specialist"
        );
        var balancedAudit = vm.AuditHierarchyAspectAndBranching(balancedTree);
        Assert.Equal(7, balancedAudit.TotalNodes);
        Assert.Equal(2, balancedAudit.MaxDepth);
        Assert.Equal(4, balancedAudit.MaxBreadth);
        Assert.Equal(0, balancedAudit.SingleChildChains);
        Assert.Equal(0, balancedAudit.WideFanNodes);
        Assert.True(balancedAudit.HierarchyBalanceScore >= 90.0);
        Assert.Equal("Balanced Organizational Hierarchy", balancedAudit.Category);

        string chainTree = string.Join("\n",
            "- Level 0 Root",
            "  - Level 1 Only Child",
            "    - Level 2 Only Child",
            "      - Level 3 Only Child",
            "        - Level 4 Only Child",
            "          - Level 5 Only Child"
        );
        var chainAudit = vm.AuditHierarchyAspectAndBranching(chainTree);
        Assert.Equal(6, chainAudit.TotalNodes);
        Assert.Equal(5, chainAudit.MaxDepth);
        Assert.True(chainAudit.SingleChildChains >= 4);
        Assert.True(chainAudit.HierarchyBalanceScore < 90.0);
    }

    [Fact]
    public void Run174_AuditTimelineMilestoneChronology_ValidatesMonotonicProgression()
    {
        var empty = SmartArtDesignStudioViewModel.AuditTimelineMilestoneChronologyAndSpacing("");
        Assert.Equal(0, empty.TotalMilestones);
        Assert.Equal(0, empty.DatedMilestones);
        Assert.Equal(100.0, empty.ChronologyMonotonicityScore);
        Assert.Equal("Empty Timeline", empty.Category);

        string orderedTimeline = string.Join("\n",
            "- 2026-01-15: Architecture Blueprint Kickoff",
            "- 2026-03-31: Alpha Engine Integration Complete",
            "- 2026-06-30: Beta Production Hardening",
            "- 2026-09-15: General Availability Launch"
        );
        var orderedAudit = SmartArtDesignStudioViewModel.AuditTimelineMilestoneChronologyAndSpacing(orderedTimeline);
        Assert.Equal(4, orderedAudit.TotalMilestones);
        Assert.Equal(4, orderedAudit.DatedMilestones);
        Assert.Equal(0, orderedAudit.ChronologyInversions);
        Assert.True(orderedAudit.MeanIntervalDays > 0.0);
        Assert.True(orderedAudit.ChronologyMonotonicityScore >= 95.0);
        Assert.Equal("Strict Monotonic Chronological Progression", orderedAudit.Category);

        string invertedTimeline = string.Join("\n",
            "- 2026-06-30: Beta Testing Phase",
            "- 2026-01-15: Architecture Design Kickoff"
        );
        var invertedAudit = SmartArtDesignStudioViewModel.AuditTimelineMilestoneChronologyAndSpacing(invertedTimeline);
        Assert.Equal(2, invertedAudit.TotalMilestones);
        Assert.Equal(2, invertedAudit.DatedMilestones);
        Assert.Equal(1, invertedAudit.ChronologyInversions);
        Assert.True(invertedAudit.ChronologyMonotonicityScore < 95.0);

        string formatted = SmartArtDesignStudioViewModel.FormatTimelineChronologyAndSpacingSummary(
            orderedAudit.TotalMilestones, orderedAudit.DatedMilestones, orderedAudit.ChronologyInversions,
            orderedAudit.MeanIntervalDays, orderedAudit.ChronologyMonotonicityScore, orderedAudit.Category);
        Assert.Equal(orderedAudit.Summary, formatted);
    }

    [Fact]
    public void Run175_AuditCycleIntervalAndRadialChords_EvaluatesProcessStepsAndChordBalance()
    {
        var empty = SmartArtDesignStudioViewModel.AuditCycleIntervalAndRadialChords("");
        Assert.Equal(0, empty.TotalSteps);
        Assert.Equal(100.0, empty.RadialHarmonyScore);
        Assert.Equal("Empty Cycle", empty.Category);

        string harmonicCycle = string.Join("\n",
            "- Concept Discovery & Requirements Gathering",
            "- Rapid MVP Prototype Validation Sprint",
            "- Production Hardening & CI Automation",
            "- Telemetry Analysis & Iteration Blueprint"
        );
        var harmonicAudit = SmartArtDesignStudioViewModel.AuditCycleIntervalAndRadialChords(harmonicCycle);
        Assert.Equal(4, harmonicAudit.TotalSteps);
        Assert.Equal(90.0, harmonicAudit.AngularChordDegrees);
        Assert.Equal(0, harmonicAudit.OvercrowdedStepsCount);
        Assert.True(harmonicAudit.MeanWordsPerStep > 0.0);
        Assert.True(harmonicAudit.RadialHarmonyScore >= 90.0);
        Assert.Equal("Harmonic Radial Cycle (Optimal 3-7 Steps & Uniform Chords)", harmonicAudit.Category);

        string degenerateCycle = string.Join("\n",
            "- Step One Kickoff",
            "- Step Two Completion"
        );
        var degenerateAudit = SmartArtDesignStudioViewModel.AuditCycleIntervalAndRadialChords(degenerateCycle);
        Assert.Equal(2, degenerateAudit.TotalSteps);
        Assert.Equal(180.0, degenerateAudit.AngularChordDegrees);
        Assert.True(degenerateAudit.RadialHarmonyScore < 90.0);
        Assert.Contains("Degenerate", degenerateAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatCycleIntervalSummary(
            harmonicAudit.TotalSteps, harmonicAudit.AngularChordDegrees, harmonicAudit.MeanWordsPerStep,
            harmonicAudit.WordCountStdDev, harmonicAudit.OvercrowdedStepsCount, harmonicAudit.RadialHarmonyScore,
            harmonicAudit.Category);
        Assert.Equal(harmonicAudit.Summary, formatted);
    }

    [Fact]
    public void Run176_AuditQuadrantGridBalance_Comprehensive()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditQuadrantGridBalance("");
        Assert.Equal(0, emptyAudit.TotalItems);
        Assert.Equal(0, emptyAudit.TotalWords);
        Assert.Equal(4, emptyAudit.EmptyQuadrants);
        Assert.Equal(100.0, emptyAudit.QuadrantBalanceScore);
        Assert.Equal("Empty Matrix", emptyAudit.Category);

        string balancedMatrix = string.Join("\n",
            "- Core high performance engine",
            "- Legacy codebase tech debt",
            "- Modern enterprise cloud scale",
            "- Emerging rapid market competitors"
        );
        var balancedAudit = SmartArtDesignStudioViewModel.AuditQuadrantGridBalance(balancedMatrix);
        Assert.Equal(4, balancedAudit.TotalItems);
        Assert.Equal(4, balancedAudit.PopulatedQuadrants);
        Assert.Equal(0, balancedAudit.EmptyQuadrants);
        Assert.True(balancedAudit.DiagonalSymmetryPercentage >= 80.0);
        Assert.True(balancedAudit.QuadrantBalanceScore >= 80.0);
        Assert.Contains("Equilateral 2x2 Matrix Grid", balancedAudit.Category);

        string sparseMatrix = string.Join("\n",
            "- Strength One",
            "- Weakness One"
        );
        var sparseAudit = SmartArtDesignStudioViewModel.AuditQuadrantGridBalance(sparseMatrix);
        Assert.Equal(2, sparseAudit.TotalItems);
        Assert.Equal(2, sparseAudit.PopulatedQuadrants);
        Assert.Equal(2, sparseAudit.EmptyQuadrants);
        Assert.True(sparseAudit.QuadrantBalanceScore < 70.0);
        Assert.Contains("Sparse Matrix", sparseAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatQuadrantBalanceSummary(
            balancedAudit.TotalItems, balancedAudit.TotalWords, 4, 4, 4, 4,
            balancedAudit.DiagonalSymmetryPercentage, balancedAudit.QuadrantBalanceScore, balancedAudit.Category);
        Assert.Contains("2x2 matrix quadrant grid audit", formatted);
    }

    [Fact]
    public void Run177_AuditFunnelPyramidProgression_And_FormatFunnelPyramidSummary()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidProgression("");
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal(0, emptyAudit.TotalWords);
        Assert.Equal(100.0, emptyAudit.TaperUniformityScore);
        Assert.Equal("Empty Funnel/Pyramid", emptyAudit.Category);
        Assert.Contains("0 stages", emptyAudit.Summary);

        string smoothFunnel = string.Join("\n",
            "- Top Funnel Lead Awareness Stage",
            "- Middle Evaluation And Interest Stage",
            "- Lower Qualified Decision Stage",
            "- Final Transactional Action Stage"
        );

        var smoothAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidProgression(smoothFunnel);
        Assert.Equal(4, smoothAudit.TotalStages);
        Assert.Equal(0, smoothAudit.InversionCount);
        Assert.True(smoothAudit.IsMonotonicProgression);
        Assert.Equal(100.0, smoothAudit.TaperUniformityScore);
        Assert.Equal("Smooth Monotonic Stage Taper", smoothAudit.Category);

        string jaggedFunnel = string.Join("\n",
            "- Stage One",
            "- Stage Two With A Massive Paragraph Describing Extremely Verbose And Elaborate Architectural Enterprise Details Far Exceeding Any Reasonable Proportions",
            "- Stage Three"
        );

        var jaggedAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidProgression(jaggedFunnel);
        Assert.Equal(3, jaggedAudit.TotalStages);
        Assert.True(jaggedAudit.InversionCount > 0);
        Assert.False(jaggedAudit.IsMonotonicProgression);
        Assert.Contains("Abrupt Volume Inversion", jaggedAudit.Category);
        Assert.True(jaggedAudit.TaperUniformityScore < 100.0);

        string formatted = SmartArtDesignStudioViewModel.FormatFunnelPyramidSummary(
            smoothAudit.TotalStages, smoothAudit.TotalWords, smoothAudit.InversionCount,
            smoothAudit.MeanStageWords, smoothAudit.WordCountStdDev, smoothAudit.TaperUniformityScore,
            smoothAudit.Category);
        Assert.Equal(smoothAudit.Summary, formatted);
    }

    [Fact]
    public void Run178_AuditVennRadialIntersectionBalance()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditVennRadialIntersectionBalance("");
        Assert.Equal(0, emptyAudit.TotalSets);
        Assert.Equal(100.0, emptyAudit.BalanceScore);
        Assert.Contains("0 sets", emptyAudit.Summary);

        // Balanced 2-set Venn diagram with intersection
        string balancedVenn = string.Join("\n",
            "* Business Strategy",
            "  * Market Analysis",
            "* Technology Innovation",
            "  * Platform Architecture",
            "* Shared Goals & Synergies Intersection"
        );

        var balancedAudit = SmartArtDesignStudioViewModel.AuditVennRadialIntersectionBalance(balancedVenn, "venn-2");
        Assert.Equal(2, balancedAudit.TotalSets);
        Assert.Equal(1, balancedAudit.TotalIntersections);
        Assert.Equal("Harmonious Venn/Radial Overlap Balance", balancedAudit.Category);
        Assert.True(balancedAudit.BalanceScore >= 80.0);

        // Skewed set volume
        string skewedVenn = string.Join("\n",
            "* Set Alpha",
            "* Set Beta With Extremely Disproportionate Verbose Details Containing Far Too Many Extraneous Descriptions That Unbalance The Diagram"
        );

        var skewedAudit = SmartArtDesignStudioViewModel.AuditVennRadialIntersectionBalance(skewedVenn, "venn-2");
        Assert.Equal(2, skewedAudit.TotalSets);
        Assert.Equal(0, skewedAudit.TotalIntersections);
        Assert.Contains("Skewed", skewedAudit.Category);
        Assert.True(skewedAudit.BalanceScore < 80.0);

        string formatted = SmartArtDesignStudioViewModel.FormatVennRadialBalanceSummary(
            balancedAudit.TotalSets, balancedAudit.TotalIntersections, balancedAudit.TotalWords,
            balancedAudit.MeanSetWords, balancedAudit.WordCountStdDev, balancedAudit.BalanceScore,
            balancedAudit.Category);
        Assert.Equal(balancedAudit.Summary, formatted);
    }

    [Fact]
    public void Run179_AuditListTextDensityBalance()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditListTextDensityBalance("");
        Assert.Equal(0, emptyAudit.TotalListItems);
        Assert.Equal(100.0, emptyAudit.DensityBalanceScore);
        Assert.Contains("0 list items", emptyAudit.Summary);

        // Balanced list items
        string balancedMarkdown = string.Join("\n",
            "* First requirement specification and analysis",
            "* Second architecture design and layout",
            "* Third system implementation and build",
            "* Fourth quality assurance and testing"
        );

        var balancedAudit = SmartArtDesignStudioViewModel.AuditListTextDensityBalance(balancedMarkdown, "horizontal-list");
        Assert.Equal(4, balancedAudit.TotalListItems);
        Assert.Equal(0, balancedAudit.OutlierCount);
        Assert.True(balancedAudit.DensityBalanceScore >= 80.0);
        Assert.Equal("Balanced Harmonious List Items", balancedAudit.Category);

        // Skewed list with severe word count outlier
        string skewedMarkdown = string.Join("\n",
            "* Short item",
            "* Item two",
            "* Item three",
            "* An excessively long item with a massive paragraph of text repeating multiple times to skew the standard deviation and trigger the outlier detection logic inside the audit"
        );

        var skewedAudit = SmartArtDesignStudioViewModel.AuditListTextDensityBalance(skewedMarkdown, "horizontal-list");
        Assert.Equal(4, skewedAudit.TotalListItems);
        Assert.True(skewedAudit.OutlierCount > 0);
        Assert.True(skewedAudit.DensityBalanceScore < 80.0);

        string formatted = SmartArtDesignStudioViewModel.FormatListTextDensitySummary(
            balancedAudit.TotalListItems, balancedAudit.TotalWords, balancedAudit.TotalCharacters,
            balancedAudit.MeanWordCount, balancedAudit.StdDevWordCount, balancedAudit.MeanLineCount,
            balancedAudit.OutlierCount, balancedAudit.DensityBalanceScore, balancedAudit.Category);
        Assert.Equal(balancedAudit.Summary, formatted);
    }

    [Fact]
    public void Run180_AuditProcessStepFlowAndTransitions()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditProcessStepFlowAndTransitions("");
        Assert.Equal(0, emptyAudit.TotalSteps);
        Assert.Equal(100.0, emptyAudit.FlowHarmonyScore);
        Assert.Contains("0 steps", emptyAudit.Summary);

        string sequentialMarkdown = string.Join("\n",
            "- Step 1: Initial Discovery Phase",
            "- Step 2: System Architecture Design",
            "- Step 3: Production Code Implementation",
            "- Step 4: Final Verification and Deployment"
        );

        var audit = SmartArtDesignStudioViewModel.AuditProcessStepFlowAndTransitions(sequentialMarkdown);
        Assert.Equal(4, audit.TotalSteps);
        Assert.Equal(4, audit.SequentiallyNumberedSteps);
        Assert.Equal(0, audit.OutlierStepCount);
        Assert.True(audit.FlowHarmonyScore >= 80.0);
        Assert.Contains("Initial / Entry", audit.Steps[0].Directionality);
        Assert.Contains("Terminal / Exit", audit.Steps[^1].Directionality);

        string formatted = SmartArtDesignStudioViewModel.FormatProcessStepFlowSummary(
            audit.TotalSteps, audit.StepsWithTransitionCues, audit.SequentiallyNumberedSteps,
            audit.OutlierStepCount, audit.AverageTitleWordCount, audit.TitleWordVariance,
            audit.FlowHarmonyScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);
    }

    [Fact]
    public void Run181_AuditHierarchyIndentationAndChildBalance_EvaluatesTreeSymmetry()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditHierarchyIndentationAndChildBalance("");
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal(100.0, emptyAudit.HierarchyBalanceScore);
        Assert.Contains("0 nodes", emptyAudit.Summary);

        string hierarchyMd = string.Join("\n",
            "- Executive Leadership",
            "  - Engineering Directorate",
            "    - Frontend Core Team",
            "    - Platform Services",
            "  - Product Management",
            "    - UX Design Group",
            "    - Market Research"
        );

        var audit = SmartArtDesignStudioViewModel.AuditHierarchyIndentationAndChildBalance(hierarchyMd);
        Assert.Equal(7, audit.TotalNodes);
        Assert.Equal(2, audit.MaxDepth);
        Assert.Equal(0, audit.LopsidedParentCount);
        Assert.Equal(0, audit.DeeplyNestedCount);
        Assert.True(audit.HierarchyBalanceScore >= 90.0);
        Assert.Equal("Balanced Hierarchy", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatHierarchyBalanceSummary(
            audit.TotalNodes, audit.MaxDepth, audit.AverageBranchingFactor,
            audit.LopsidedParentCount, audit.DeeplyNestedCount, audit.BranchingFactorVariance,
            audit.HierarchyBalanceScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);
    }

    [Fact]
    public void Run182_AuditCycleSequenceAndClosure_EvaluatesMonotonicityAndClosure()
    {
        // 1. Empty markdown
        var emptyAudit = SmartArtDesignStudioViewModel.AuditCycleSequenceAndClosure("", "Continuous Block Cycle");
        Assert.Equal(0, emptyAudit.TotalSteps);
        Assert.Equal(100.0, emptyAudit.BalanceScore);
        Assert.Contains("0 steps", emptyAudit.Summary);

        // 2. Closed 4-step cycle with balanced wording
        string cycleMd = string.Join("\n",
            "- Step 1: Discover",
            "- Step 2: Design",
            "- Step 3: Implement",
            "- Step 4: Verify"
        );

        var audit = SmartArtDesignStudioViewModel.AuditCycleSequenceAndClosure(cycleMd, "Radial Cycle");
        Assert.Equal(4, audit.TotalSteps);
        Assert.True(audit.MeetsMinimumCycleSize);
        Assert.True(audit.IsClosedLoop);
        Assert.True(audit.BalanceScore >= 90.0);
        Assert.Equal("Harmonious Cyclic Loop (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatCycleSequenceSummary(
            audit.TotalSteps, audit.MeetsMinimumCycleSize, audit.AverageStepWordCount,
            audit.WordCountVariance, audit.SubBulletTotal, audit.BalanceScore,
            audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Cycle with insufficient steps (< 3 steps)
        string shortCycleMd = string.Join("\n",
            "- Single Starting Step",
            "- Second Step Only"
        );

        var shortAudit = SmartArtDesignStudioViewModel.AuditCycleSequenceAndClosure(shortCycleMd, "Continuous Block Cycle");
        Assert.Equal(2, shortAudit.TotalSteps);
        Assert.False(shortAudit.MeetsMinimumCycleSize);
        Assert.False(shortAudit.IsClosedLoop);
        Assert.True(shortAudit.BalanceScore < audit.BalanceScore);
    }

    [Fact]
    public void Run183_AuditMatrixQuadrantBalance_EvaluatesSymmetryAndVariance()
    {
        // 1. Empty markdown
        var emptyAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantBalance("", "Quadrant Matrix");
        Assert.Equal(4, emptyAudit.TotalQuadrants);
        Assert.Equal(0, emptyAudit.PopulatedQuadrants);
        Assert.Equal(4, emptyAudit.EmptyQuadrants);
        Assert.Equal(100.0, emptyAudit.SymmetryScore);
        Assert.Contains("0 items", emptyAudit.Summary);

        // 2. Symmetric 4-quadrant layout with 1 item per quadrant
        string matrixMd = string.Join("\n",
            "- Strengths: Strong brand and patents",
            "- Weaknesses: Limited global supply chain",
            "- Opportunities: Expanding AI automation demand",
            "- Threats: Volatile macroeconomic climate"
        );

        var audit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantBalance(matrixMd, "2x2 SWOT Matrix");
        Assert.Equal(4, audit.TotalQuadrants);
        Assert.Equal(4, audit.PopulatedQuadrants);
        Assert.Equal(0, audit.EmptyQuadrants);
        Assert.Equal(4, audit.TotalItems);
        Assert.Equal(0.0, audit.ItemVariance);
        Assert.Equal(100.0, audit.SymmetryScore);
        Assert.Equal("Harmonious Quadrant Balance (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatMatrixQuadrantSummary(
            audit.TotalQuadrants, audit.PopulatedQuadrants, audit.EmptyQuadrants,
            audit.TotalItems, audit.TotalWords, audit.ItemVariance,
            audit.SymmetryScore, audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Skewed matrix with empty quadrants
        string skewedMd = string.Join("\n",
            "- Solo Quadrant Item",
            "  - Sub item 1",
            "  - Sub item 2"
        );

        var skewedAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantBalance(skewedMd, "Quadrant Matrix");
        Assert.Equal(4, skewedAudit.TotalQuadrants);
        Assert.Equal(1, skewedAudit.PopulatedQuadrants);
        Assert.Equal(3, skewedAudit.EmptyQuadrants);
        Assert.True(skewedAudit.SymmetryScore < audit.SymmetryScore);
    }

    [Fact]
    public void Run184_AuditFunnelPyramidVolumeProgression_EvaluatesStagingAndProgression()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeProgression("", "Funnel");
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal(100.0, emptyAudit.VolumeProgressionScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 stages", emptyAudit.Summary);

        // 2. Structured 4-stage funnel outline
        var funnelMd = string.Join("\n",
            "- Awareness Stage",
            "- Consideration & Evaluation",
            "- Decision & Purchase",
            "- Retention & Loyalty"
        );

        var audit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeProgression(funnelMd, "Funnel");
        Assert.Equal(4, audit.TotalStages);
        Assert.True(audit.IsFunnelLayout);
        Assert.True(audit.IsMonotonicallyOrdered);
        Assert.Equal(0, audit.WordLengthViolations);
        Assert.Equal(100.0, audit.VolumeProgressionScore);
        Assert.Equal("Optimal Stage Progression (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatFunnelPyramidSummary(
            audit.TotalStages, audit.IsFunnelLayout, audit.IsPyramidLayout,
            audit.IsMonotonicallyOrdered, audit.WordLengthViolations,
            audit.VolumeProgressionScore, audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Structured 3-stage pyramid outline
        var pyramidMd = string.Join("\n",
            "- Strategic Vision",
            "- Operational Programs",
            "- Tactical Execution Tasks"
        );

        var pyrAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeProgression(pyramidMd, "Pyramid");
        Assert.Equal(3, pyrAudit.TotalStages);
        Assert.True(pyrAudit.IsPyramidLayout);
        Assert.True(pyrAudit.IsMonotonicallyOrdered);
        Assert.Equal(100.0, pyrAudit.VolumeProgressionScore);

        // 4. Overly verbose stages
        var verboseMd = string.Join("\n",
            "- First Stage with way too many words in this description that exceeds twelve words in total length violation",
            "- Second Stage"
        );

        var verboseAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeProgression(verboseMd, "Funnel");
        Assert.Equal(2, verboseAudit.TotalStages);
        Assert.True(verboseAudit.WordLengthViolations >= 1);
        Assert.True(verboseAudit.VolumeProgressionScore < 100.0);
    }

    [Fact]
    public void Run185_AuditTimelineChronologyAndIntervals_EvaluatesMonotonicityAndBounds()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditTimelineChronologyAndIntervals("", "Timeline");
        Assert.Equal(0, emptyAudit.TotalMilestones);
        Assert.Equal(100.0, emptyAudit.ChronologyBalanceScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 milestones", emptyAudit.Summary);

        // 2. Structured chronological 4-step process
        var timelineMd = string.Join("\n",
            "- 2023: Project Inception",
            "- 2024: Alpha Release",
            "- 2025: Beta Testing & Scalability",
            "- 2026: General Availability"
        );

        var audit = SmartArtDesignStudioViewModel.AuditTimelineChronologyAndIntervals(timelineMd, "Timeline");
        Assert.Equal(4, audit.TotalMilestones);
        Assert.True(audit.IsMonotonicallyOrdered);
        Assert.Equal(0, audit.InvertedOrderCount);
        Assert.Equal(0, audit.WordLengthViolations);
        Assert.True(audit.IsWithinOptimalStepBounds);
        Assert.Equal(100.0, audit.ChronologyBalanceScore);
        Assert.Equal("Optimal Milestone Pacing & Chronology (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatTimelineChronologySummary(
            audit.TotalMilestones, audit.IsMonotonicallyOrdered, audit.InvertedOrderCount,
            audit.WordLengthViolations, audit.IsWithinOptimalStepBounds,
            audit.ChronologyBalanceScore, audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Chronological inversion (2025 before 2024)
        var invertedMd = string.Join("\n",
            "- 2025: Future Stage",
            "- 2024: Past Stage",
            "- 2026: Final Stage"
        );

        var invAudit = SmartArtDesignStudioViewModel.AuditTimelineChronologyAndIntervals(invertedMd, "Timeline");
        Assert.Equal(3, invAudit.TotalMilestones);
        Assert.False(invAudit.IsMonotonicallyOrdered);
        Assert.True(invAudit.InvertedOrderCount >= 1);
        Assert.True(invAudit.ChronologyBalanceScore < 100.0);
    }

    [Fact]
    public void Run186_AuditRadialHubSpokeSymmetry_EvaluatesAngularSeparationAndBalance()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditRadialHubSpokeSymmetry("", "Radial Cycle");
        Assert.Equal(0, emptyAudit.TotalSpokes);
        Assert.Equal(100.0, emptyAudit.RadialSymmetryScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 spokes", emptyAudit.Summary);

        // 2. Balanced 4-spoke radial layout
        var radialMd = string.Join("\n",
            "- Plan Phase",
            "- Execute Phase",
            "- Review Phase",
            "- Improve Phase"
        );

        var audit = SmartArtDesignStudioViewModel.AuditRadialHubSpokeSymmetry(radialMd, "Radial Cycle");
        Assert.Equal(4, audit.TotalSpokes);
        Assert.Equal(90.0, audit.AngularSeparationDegrees);
        Assert.True(audit.IsWithinOptimalSpokeBounds);
        Assert.Equal(0, audit.WordLengthViolations);
        Assert.Equal(100.0, audit.RadialSymmetryScore);
        Assert.Equal("Optimal Radial Symmetry & Balance (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatRadialHubSpokeSummary(
            audit.TotalSpokes, audit.HubLabel, audit.AngularSeparationDegrees,
            audit.IsWithinOptimalSpokeBounds, audit.WordLengthViolations,
            audit.TextLengthVariance, audit.RadialSymmetryScore, audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Hub and spoke layout with central hub
        var hubSpokeMd = string.Join("\n",
            "- Central Platform Hub",
            "- Analytics Module",
            "- Ingestion Engine",
            "- Visualization Client"
        );

        var hubAudit = SmartArtDesignStudioViewModel.AuditRadialHubSpokeSymmetry(hubSpokeMd, "Hub and Spoke");
        Assert.Equal(3, hubAudit.TotalSpokes);
        Assert.Equal("Central Platform Hub", hubAudit.HubLabel);
        Assert.Equal(120.0, hubAudit.AngularSeparationDegrees);
        Assert.True(hubAudit.IsWithinOptimalSpokeBounds);
        Assert.Equal(100.0, hubAudit.RadialSymmetryScore);

        // 4. Overly verbose spoke violating word limits
        var verboseMd = string.Join("\n",
            "- Concise Spoke One",
            "- Extremely verbose spoke label that includes way too many redundant words exceeding twelve words in total length violation",
            "- Concise Spoke Three"
        );

        var verboseAudit = SmartArtDesignStudioViewModel.AuditRadialHubSpokeSymmetry(verboseMd, "Radial Cycle");
        Assert.Equal(3, verboseAudit.TotalSpokes);
        Assert.True(verboseAudit.WordLengthViolations >= 1);
        Assert.True(verboseAudit.RadialSymmetryScore < 100.0);
    }

    [Fact]
    public void Run187_AuditChevronProcessFlowAndSpacing_EvaluatesProcessDirectionality()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditChevronProcessFlowAndSpacing("", "Chevron Process");
        Assert.Equal(0, emptyAudit.TotalPhases);
        Assert.Equal(100.0, emptyAudit.FlowDirectionalityScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 phases", emptyAudit.Summary);

        // 2. Balanced 4-phase chevron process
        var chevronMd = string.Join("\n",
            "- Discovery & Analysis",
            "  - Stakeholder interviews",
            "- Architecture & Design",
            "- Implementation & Verification",
            "- Deployment & Telemetry"
        );

        var audit = SmartArtDesignStudioViewModel.AuditChevronProcessFlowAndSpacing(chevronMd, "Chevron Process");
        Assert.Equal(4, audit.TotalPhases);
        Assert.Equal(1, audit.TotalSubSteps);
        Assert.True(audit.IsWithinOptimalPhaseBounds);
        Assert.Equal(0, audit.WordLengthViolations);
        Assert.Equal(100.0, audit.FlowDirectionalityScore);
        Assert.Equal("Optimal Chevron Process Flow (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatChevronProcessFlowSummary(
            audit.TotalPhases, audit.TotalSubSteps, audit.IsWithinOptimalPhaseBounds,
            audit.WordLengthViolations, audit.FlowDirectionalityScore, audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Process with too few phases (<3) and verbose title (>12 words)
        var probMd = string.Join("\n",
            "- Brief Phase One",
            "- An excessively verbose phase label that explains every single technical requirement and detail across far too many words violating the limit"
        );

        var probAudit = SmartArtDesignStudioViewModel.AuditChevronProcessFlowAndSpacing(probMd, "Closed Chevron");
        Assert.Equal(2, probAudit.TotalPhases);
        Assert.False(probAudit.IsWithinOptimalPhaseBounds);
        Assert.True(probAudit.WordLengthViolations >= 1);
        Assert.True(probAudit.FlowDirectionalityScore < 100.0);
    }

    [Fact]
    public void Run188_AuditFunnelPyramidProportions_ValidatesStageBoundsAndTextOverflow()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidProportions("", "Funnel");
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal(100.0, emptyAudit.ProportionFidelityScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 stages", emptyAudit.Summary);

        // 2. Balanced 4-stage funnel
        var funnelMd = string.Join("\n",
            "- Awareness",
            "  - Social & Search",
            "- Consideration",
            "- Intent",
            "- Purchase"
        );

        var audit = SmartArtDesignStudioViewModel.AuditFunnelPyramidProportions(funnelMd, "Funnel");
        Assert.Equal(4, audit.TotalStages);
        Assert.Equal(1, audit.TotalSubBullets);
        Assert.True(audit.IsWithinOptimalStageBounds);
        Assert.Equal(0, audit.TextOverflowRisksCount);
        Assert.Equal(100.0, audit.ProportionFidelityScore);
        Assert.Equal("Optimal Funnel/Pyramid Proportions (≥90%)", audit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatFunnelPyramidSummary(
            audit.TotalStages, audit.TotalSubBullets, audit.IsWithinOptimalStageBounds,
            audit.TextOverflowRisksCount, audit.ProportionFidelityScore, audit.Category, audit.LayoutName);
        Assert.Equal(audit.Summary, formatted);

        // 3. Pyramid with overflow on narrow stage and too few stages (< 3)
        var probMd = string.Join("\n",
            "- An excessively long label that will certainly overflow the tiny narrow apex of the pyramid trapezoid",
            "- Base Stage"
        );

        var probAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidProportions(probMd, "Pyramid");
        Assert.Equal(2, probAudit.TotalStages);
        Assert.False(probAudit.IsWithinOptimalStageBounds);
        Assert.True(probAudit.TextOverflowRisksCount >= 1);
        Assert.True(probAudit.ProportionFidelityScore < 75.0);
    }

    [Fact]
    public void Run189_AuditMatrixCellSymmetry_ValidatesQuadrantParityAndWordLimits()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditMatrixCellSymmetry("", "Grid Matrix");
        Assert.Equal(0, emptyAudit.TotalCells);
        Assert.Equal(100.0, emptyAudit.SymmetryScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 cells", emptyAudit.Summary);

        // 2. Clean balanced 2x2 matrix
        var cleanMd = string.Join("\n",
            "- Quadrant 1",
            "  - Strengths and core competencies",
            "- Quadrant 2",
            "  - Weaknesses and operational gaps",
            "- Quadrant 3",
            "  - Opportunities and growth paths",
            "- Quadrant 4",
            "  - Threats and competitive risks"
        );

        var cleanAudit = SmartArtDesignStudioViewModel.AuditMatrixCellSymmetry(cleanMd, "Grid Matrix");
        Assert.Equal(4, cleanAudit.TotalCells);
        Assert.True(cleanAudit.IsStandardGridSize);
        Assert.Equal(4, cleanAudit.TotalSubBullets);
        Assert.Equal(100.0, cleanAudit.SymmetryScore);
        Assert.Equal("Optimal Matrix Symmetry & Quadrant Balance (≥90%)", cleanAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatMatrixCellSymmetrySummary(
            cleanAudit.TotalCells, cleanAudit.TotalWords, cleanAudit.TotalSubBullets,
            cleanAudit.IsStandardGridSize, cleanAudit.MeanWordsPerCell, cleanAudit.SymmetryScore,
            cleanAudit.Category, cleanAudit.LayoutName);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Irregular 3-cell matrix with extreme word imbalance
        var probMd = string.Join("\n",
            "- Short",
            "- Giant Paragraph of Many Words That Overwhelms This Particular Quadrant Cell By Far Exceeding All Proportions Compared To Peers",
            "- Medium"
        );

        var probAudit = SmartArtDesignStudioViewModel.AuditMatrixCellSymmetry(probMd, "Grid Matrix");
        Assert.Equal(3, probAudit.TotalCells);
        Assert.False(probAudit.IsStandardGridSize);
        Assert.True(probAudit.SymmetryScore < 75.0);
    }

    [Fact]
    public void Run190_AuditHierarchyOrgChartBalance_ValidatesSpanAndDepth()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditHierarchyOrgChartBalance("", "Organization Chart");
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal(100.0, emptyAudit.BalanceScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 nodes", emptyAudit.Summary);

        // 2. Clean balanced 3-level organization chart (CEO -> 3 VPs -> 2 Leads each)
        var cleanMd = string.Join("\n",
            "- Chief Executive Officer",
            "  - VP Engineering",
            "    - Frontend Lead",
            "    - Backend Lead",
            "  - VP Product",
            "    - UX Design Lead",
            "    - Product Strategy Lead",
            "  - VP Operations",
            "    - Infrastructure Lead",
            "    - Support Lead"
        );

        var cleanAudit = SmartArtDesignStudioViewModel.AuditHierarchyOrgChartBalance(cleanMd, "Organization Chart");
        Assert.Equal(10, cleanAudit.TotalNodes);
        Assert.Equal(3, cleanAudit.MaxDepth);
        Assert.Equal(1, cleanAudit.RootNodeCount);
        Assert.Equal(4, cleanAudit.ParentNodeCount);
        Assert.Equal(6, cleanAudit.LeafNodeCount);
        Assert.True(cleanAudit.AverageSpanOfControl > 2.0);
        Assert.Equal(100.0, cleanAudit.BalanceScore);
        Assert.Equal("Optimal Org Chart Balance & Clean Span of Control (≥90%)", cleanAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatHierarchyOrgChartSummary(
            cleanAudit.TotalNodes, cleanAudit.MaxDepth, cleanAudit.RootNodeCount,
            cleanAudit.ParentNodeCount, cleanAudit.LeafNodeCount, cleanAudit.AverageSpanOfControl,
            cleanAudit.BalanceScore, cleanAudit.Category, cleanAudit.LayoutName);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Problematic org chart: multiple disconnected roots and overloaded manager (>7 direct reports)
        var probMd = string.Join("\n",
            "- First Root Co-CEO",
            "- Second Root Co-CEO",
            "  - Report 1",
            "  - Report 2",
            "  - Report 3",
            "  - Report 4",
            "  - Report 5",
            "  - Report 6",
            "  - Report 7",
            "  - Report 8"
        );

        var probAudit = SmartArtDesignStudioViewModel.AuditHierarchyOrgChartBalance(probMd, "Organization Chart");
        Assert.Equal(10, probAudit.TotalNodes);
        Assert.Equal(2, probAudit.RootNodeCount);
        Assert.True(probAudit.BalanceScore < 80.0);
    }

    [Fact]
    public void Run191_AuditAlternatingFlowAndRhythm_ValidatesStepParityAndWordRatio()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditAlternatingFlowAndRhythm("");
        Assert.Equal(0, emptyAudit.TotalSteps);
        Assert.Equal(100.0, emptyAudit.RhythmScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 steps", emptyAudit.Summary);

        // 2. Symmetrical alternating flow (4 steps: 2 Left vs 2 Right, similar word counts)
        var cleanMd = string.Join("\n",
            "- Step One Kickoff Discovery",
            "- Step Two Architecture Design",
            "- Step Three Engine Implementation",
            "- Step Four Quality Verification"
        );

        var cleanAudit = SmartArtDesignStudioViewModel.AuditAlternatingFlowAndRhythm(cleanMd, "Alternating Hexagons");
        Assert.Equal(4, cleanAudit.TotalSteps);
        Assert.Equal(2, cleanAudit.LeftSteps);
        Assert.Equal(2, cleanAudit.RightSteps);
        Assert.True(cleanAudit.WordBalanceRatio >= 0.85);
        Assert.Equal(100.0, cleanAudit.RhythmScore);
        Assert.Equal("Optimal Bilateral Alternating Rhythm (≥90%)", cleanAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatAlternatingFlowSummary(
            cleanAudit.TotalSteps, cleanAudit.LeftSteps, cleanAudit.RightSteps,
            cleanAudit.LeftWords, cleanAudit.RightWords, cleanAudit.WordBalanceRatio,
            cleanAudit.RhythmScore, cleanAudit.Category, cleanAudit.LayoutName);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Problematic alternating flow: unbalanced parity (4 Left vs 1 Right) and skewed text
        var probMd = string.Join("\n",
            "- Left Step A with lots and lots of extra detailed description and verbosity",
            "- Right Step B",
            "- Left Step C",
            "- Left Step D",
            "- Left Step E"
        );

        var probAudit = SmartArtDesignStudioViewModel.AuditAlternatingFlowAndRhythm(probMd, "Alternating Timeline");
        Assert.Equal(5, probAudit.TotalSteps);
        Assert.Equal(3, probAudit.LeftSteps);
        Assert.Equal(2, probAudit.RightSteps);
        Assert.True(probAudit.WordBalanceRatio < 0.8);
        Assert.True(probAudit.RhythmScore < 95.0);
    }

    [Fact]
    public void Run192_AuditRadialGearCycleEngagement_ValidatesAngularPitchAndDensity()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditRadialGearCycleEngagement("");
        Assert.Equal(0, emptyAudit.TotalGears);
        Assert.Equal(100.0, emptyAudit.RotationalBalanceScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("Empty outline", emptyAudit.Summary);

        // 2. Optimal 4-gear cycle (90 deg pitch)
        var cleanMd = string.Join("\n",
            "- Discovery & Analysis",
            "- Architectural Design",
            "- Core Implementation",
            "- Verification & Testing"
        );

        var cleanAudit = SmartArtDesignStudioViewModel.AuditRadialGearCycleEngagement(cleanMd, "Radial Gear Cycle");
        Assert.Equal(4, cleanAudit.TotalGears);
        Assert.Equal(90.0, cleanAudit.TargetPitchDegrees);
        Assert.True(cleanAudit.HasOptimalGearDensity);
        Assert.Equal(100.0, cleanAudit.RotationalBalanceScore);
        Assert.Equal("Optimal Radial Gear Engagement & Pitch Rhythm (≥90%)", cleanAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatRadialGearSummary(
            cleanAudit.TotalGears, cleanAudit.TargetPitchDegrees, cleanAudit.HasOptimalGearDensity,
            cleanAudit.RotationalBalanceScore, cleanAudit.Category, cleanAudit.LayoutName);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Sparse gear cycle (2 gears: cannot mechanically mesh)
        var sparseMd = string.Join("\n",
            "- Input Gear",
            "- Output Gear"
        );

        var sparseAudit = SmartArtDesignStudioViewModel.AuditRadialGearCycleEngagement(sparseMd, "Gear Mesh");
        Assert.Equal(2, sparseAudit.TotalGears);
        Assert.False(sparseAudit.HasOptimalGearDensity);
        Assert.True(sparseAudit.RotationalBalanceScore < 90.0);
    }

    [Fact]
    public void Run193_AuditFunnelPyramidVolumeTapering_ValidatesMonotonicProgression()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeTapering(null);
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal(100.0, emptyAudit.FlowHarmonyScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);
        Assert.Contains("0 stages", emptyAudit.Summary);

        // 2. Optimal 4-stage funnel pipeline (monotonic tapering)
        var funnelMd = string.Join("\n",
            "- Top of Funnel (100% Leads)",
            "- Marketing Qualified Leads (60%)",
            "- Sales Qualified Opportunities (30%)",
            "- Closed Won Revenue (10%)"
        );

        var funnelAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeTapering(funnelMd, "Sales Funnel");
        Assert.Equal(4, funnelAudit.TotalStages);
        Assert.False(funnelAudit.IsInverted);
        Assert.Equal(0, funnelAudit.MonotonicTaperViolations);
        Assert.Equal(100.0, funnelAudit.FlowHarmonyScore);
        Assert.Equal("Optimal Stage Progression & Tapering Rhythm (≥90%)", funnelAudit.Category);
        Assert.Equal(100.0, funnelAudit.Stages[0].CalculatedWidthTaper);
        Assert.Equal(20.0, funnelAudit.Stages[3].CalculatedWidthTaper);

        string formatted = SmartArtDesignStudioViewModel.FormatFunnelPyramidSummary(
            funnelAudit.TotalStages, funnelAudit.LayoutType, funnelAudit.IsInverted,
            funnelAudit.MonotonicTaperViolations, funnelAudit.FlowHarmonyScore, funnelAudit.Category);
        Assert.Equal(funnelAudit.Summary, formatted);

        // 3. Single stage truncated pipeline (<2 stages)
        var singleMd = "- Only Stage";
        var singleAudit = SmartArtDesignStudioViewModel.AuditFunnelPyramidVolumeTapering(singleMd, "Funnel");
        Assert.Equal(1, singleAudit.TotalStages);
        Assert.True(singleAudit.FlowHarmonyScore < 75.0);
    }

    [Fact]
    public void AuditMatrixQuadrantSymmetryAndBalance_EvaluatesFourQuadrantsAndParity()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetryAndBalance(null);
        Assert.Equal(4, emptyAudit.TotalQuadrants);
        Assert.Equal(0, emptyAudit.TotalItems);
        Assert.Equal(100.0, emptyAudit.QuadrantBalanceScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        // 2. Balanced 4-quadrant SWOT markdown
        var swotMd = string.Join("\n",
            "### Strengths",
            "- Core Product Reliability",
            "- High Performance Engine",
            "### Weaknesses",
            "- Complex Setup Flow",
            "- Limited Mobile Support",
            "### Opportunities",
            "- Cloud Integration Sync",
            "- Enterprise Workspace Suite",
            "### Threats",
            "- Rapid Feature Competitors",
            "- Platform Security Shifts"
        );

        var swotAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetryAndBalance(swotMd, "SWOT Matrix");
        Assert.Equal(4, swotAudit.TotalQuadrants);
        Assert.Equal(8, swotAudit.TotalItems);
        Assert.Equal(0.0, swotAudit.ItemCountVariance);
        Assert.True(swotAudit.QuadrantBalanceScore >= 90.0);
        Assert.Equal("Optimal Quadrant Symmetry & Balance (≥90%)", swotAudit.Category);

        string formatted = SmartArtDesignStudioViewModel.FormatMatrixQuadrantSummary(
            swotAudit.TotalQuadrants, swotAudit.TotalItems, swotAudit.TotalWords,
            swotAudit.ItemCountVariance, swotAudit.WordCountVariance, swotAudit.SymmetryRatio,
            swotAudit.QuadrantBalanceScore, swotAudit.Category);
        Assert.Equal(swotAudit.Summary, formatted);

        // 3. Imbalanced matrix (single quadrant dominated)
        var skewedMd = string.Join("\n",
            "### Quadrant 1",
            "- Item A",
            "- Item B",
            "- Item C",
            "- Item D",
            "- Item E",
            "- Item F",
            "- Item G"
        );

        var poorAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetryAndBalance(skewedMd, "Matrix");
        Assert.Equal(7, poorAudit.TotalItems);
        Assert.True(poorAudit.ItemCountVariance > 5.0);
        Assert.True(poorAudit.QuadrantBalanceScore < 70.0);
    }
    [Fact]
    public void AuditVennDiagramOverlapSymmetry_CalculatesPartitionsAndCardinalityBalance()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditVennDiagramOverlapSymmetry("");
        Assert.Equal(0, emptyAudit.TotalPartitions);
        Assert.Equal(100.0, emptyAudit.ClarityScore);

        // 2. Harmonious 2-set Venn diagram with intersection
        var md = string.Join("\n",
            "* Engineering Skills",
            "  * C# and .NET",
            "  * WinUI 3 XAML",
            "* Design Principles",
            "  * Typography rhythm",
            "  * Golden ratio proportion",
            "* Shared & Overlap",
            "  * Polished user experience",
            "  * Interactive feedback"
        );

        var audit = SmartArtDesignStudioViewModel.AuditVennDiagramOverlapSymmetry(md, "Basic Venn");
        Assert.Equal(2, audit.PrimarySetCount);
        Assert.Equal(1, audit.IntersectionCount);
        Assert.Equal(3, audit.TotalPartitions);
        Assert.Equal(6, audit.TotalItems);
        Assert.Equal(0, audit.MissingIntersectionCount);
        Assert.Equal(1.0, audit.CardinalityBalanceRatio);
        Assert.True(audit.ClarityScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatVennOverlapSummary(
            audit.PrimarySetCount, audit.IntersectionCount, audit.TotalItems, audit.TotalWords,
            audit.MissingIntersectionCount, audit.CardinalityBalanceRatio, audit.ClarityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. 2-Set Venn missing intersection
        var missingMd = string.Join("\n",
            "* Set Alpha",
            "  * Item 1",
            "* Set Beta",
            "  * Item 2"
        );
        var missingAudit = SmartArtDesignStudioViewModel.AuditVennDiagramOverlapSymmetry(missingMd, "Basic Venn");
        Assert.Equal(2, missingAudit.PrimarySetCount);
        Assert.Equal(0, missingAudit.IntersectionCount);
        Assert.Equal(1, missingAudit.MissingIntersectionCount);
        Assert.True(missingAudit.ClarityScore < 90.0);
    }

    [Fact]
    public void Run196_AuditCycleRadialAngularEquidistance_CalculatesPolarSymmetryAndJitter()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditCycleRadialAngularEquidistance("");
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal(100.0, emptyAudit.PolarSymmetryScore);

        // 2. Harmonious 5-stage radial cycle
        var md = string.Join("\n",
            "* Ingestion Phase",
            "  * Pull API telemetry",
            "* Parsing Phase",
            "  * Validate AST nodes",
            "* Transformation Phase",
            "  * Apply business rules",
            "* Verification Phase",
            "  * Execute unit tests",
            "* Export Phase",
            "  * Stream output artifacts"
        );

        var audit = SmartArtDesignStudioViewModel.AuditCycleRadialAngularEquidistance(md, "Radial Cycle");
        Assert.Equal(5, audit.TotalNodes);
        Assert.Equal(5, audit.TotalSubItems);
        Assert.Equal(72.0, audit.IdealStepDegrees);
        Assert.Equal(0, audit.CrowdedNodeCount);
        Assert.Equal(0, audit.AngularVoidCount);
        Assert.True(audit.PolarSymmetryScore >= 95.0);

        string formatted = SmartArtDesignStudioViewModel.FormatRadialAngularSummary(
            audit.TotalNodes, audit.TotalSubItems, audit.IdealStepDegrees, audit.MeanAngularJitter,
            audit.CrowdedNodeCount, audit.AngularVoidCount, audit.PolarSymmetryScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Overcrowded 10-node cycle
        var crowdMd = string.Join("\n",
            "* N1", "* N2", "* N3", "* N4", "* N5", "* N6", "* N7", "* N8", "* N9", "* N10"
        );
        var crowdAudit = SmartArtDesignStudioViewModel.AuditCycleRadialAngularEquidistance(crowdMd, "Radial Cycle");
        Assert.Equal(10, crowdAudit.TotalNodes);
        Assert.True(crowdAudit.CrowdedNodeCount >= 8);
        Assert.True(crowdAudit.PolarSymmetryScore < 80.0);
    }

    [Fact]
    public void Run197_AuditHierarchyTreeDepthAndBranchingBalance_ValidatesTreeSymmetry()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditHierarchyTreeDepthAndBranchingBalance("");
        Assert.Equal(0, emptyAudit.TotalNodes);
        Assert.Equal(100.0, emptyAudit.TreeBalanceScore);

        // 2. Symmetrical 2-level balanced tree
        var balancedMd = string.Join("\n",
            "* Root Strategy",
            "  * Objective Alpha",
            "    * Key Result A1",
            "    * Key Result A2",
            "  * Objective Beta",
            "    * Key Result B1",
            "    * Key Result B2"
        );

        var audit = SmartArtDesignStudioViewModel.AuditHierarchyTreeDepthAndBranchingBalance(balancedMd, "Hierarchy Org Chart");
        Assert.Equal(7, audit.TotalNodes);
        Assert.Equal(3, audit.MaxDepth);
        Assert.Equal(4, audit.LeafCount);
        Assert.Equal(3, audit.InternalNodeCount);
        Assert.Equal(0, audit.ImbalancedNodeCount);
        Assert.True(audit.TreeBalanceScore >= 95.0);

        string formatted = SmartArtDesignStudioViewModel.FormatHierarchyBranchingSummary(
            audit.TotalNodes, audit.MaxDepth, audit.LeafCount, audit.InternalNodeCount,
            audit.AverageBranchingFactor, audit.MinLeafDepth, audit.MaxLeafDepth,
            audit.ImbalancedNodeCount, audit.TreeBalanceScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Severely imbalanced single-branch stick
        var stickMd = string.Join("\n",
            "* Level 0",
            "  * Level 1",
            "    * Level 2",
            "      * Level 3",
            "        * Level 4"
        );

        var imbalancedAudit = SmartArtDesignStudioViewModel.AuditHierarchyTreeDepthAndBranchingBalance(stickMd, "Hierarchy");
        Assert.Equal(5, imbalancedAudit.TotalNodes);
        Assert.Equal(5, imbalancedAudit.MaxDepth);
        Assert.True(imbalancedAudit.TreeBalanceScore <= 80.0);
    }


    [Fact]
    public void Run198_AuditProcessFlowChevronUniformity_ValidatesMonotonicSizingAndNotches()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditProcessFlowChevronUniformity("");
        Assert.Equal(0, emptyAudit.TotalSteps);
        Assert.Equal(100.0, emptyAudit.ProcessCadenceScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        // 2. Uniform 4-step process flow
        var uniformMd = string.Join("\n",
            "* Step 1: Research",
            "* Step 2: Prototype",
            "* Step 3: Implement",
            "* Step 4: Validate"
        );

        var audit = SmartArtDesignStudioViewModel.AuditProcessFlowChevronUniformity(uniformMd, "Chevron Process");
        Assert.Equal(4, audit.TotalSteps);
        Assert.Equal(0, audit.OutlierCount);
        Assert.False(audit.IsCrowdedFlow);
        Assert.True(audit.ProcessCadenceScore >= 95.0);

        string formatted = SmartArtDesignStudioViewModel.FormatChevronUniformitySummary(
            audit.TotalSteps, audit.TotalWords, audit.AverageStepLength,
            audit.MinStepLength, audit.MaxStepLength, audit.LengthVarianceRatio,
            audit.OutlierCount, audit.IsCrowdedFlow, audit.ProcessCadenceScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Crowded sequence (> 8 steps) with extreme length variance
        var crowdedMd = string.Join("\n",
            "* A",
            "* B",
            "* C",
            "* D",
            "* E",
            "* F",
            "* G",
            "* H",
            "* Supercalifragilisticexpialidocious Ultra Long Enterprise Transformation Phase"
        );
        var crowdedAudit = SmartArtDesignStudioViewModel.AuditProcessFlowChevronUniformity(crowdedMd);
        Assert.True(crowdedAudit.IsCrowdedFlow);
        Assert.True(crowdedAudit.ProcessCadenceScore < 80.0);
    }


    [Fact]
    public void Run199_AuditMatrixQuadrantSymmetry_EvaluatesQuadrantsAndParity()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetry("");
        Assert.Equal(0, emptyAudit.TotalQuadrants);
        Assert.Equal(100.0, emptyAudit.SymmetryBalanceScore);
        Assert.Equal("Empty Matrix", emptyAudit.SymmetryCategory);

        // 2. 4 balanced quadrants
        var matrixMd = string.Join("\n",
            "- Strengths",
            "  - Strong brand",
            "  - Modern platform",
            "- Weaknesses",
            "  - Small team",
            "  - Limited budget",
            "- Opportunities",
            "  - New markets",
            "  - Cloud expansion",
            "- Threats",
            "  - New competitors",
            "  - Regulatory shifts"
        );
        var audit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantSymmetry(matrixMd);
        Assert.Equal(4, audit.TotalQuadrants);
        Assert.True(audit.TotalItems >= 4);
        Assert.True(audit.SymmetryBalanceScore >= 80.0);

        string formatted = SmartArtDesignStudioViewModel.FormatMatrixQuadrantSummary(
            audit.Quadrants.Count(q => !q.IsEmpty), audit.TotalItems, audit.TotalWords,
            audit.Quadrants.Count(q => q.IsEmpty), audit.Quadrants.Count(q => q.IsOverloaded),
            audit.SymmetryBalanceScore, audit.SymmetryCategory);
        Assert.Equal(audit.Summary, formatted);
    }
[Fact]
    public void Run200_AuditTargetBullseyeRadiusProgression_ValidatesConcentricRingsAndPerimeterFit()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditTargetBullseyeRadiusProgression("");
        Assert.Equal(0, emptyAudit.TotalRings);
        Assert.Equal(100.0, emptyAudit.RadialBalanceScore);
        Assert.Equal("Empty Target", emptyAudit.Category);

        // 2. 3 balanced concentric rings
        var targetMd = string.Join("\n",
            "- Core Vision",
            "- Strategic Objectives",
            "- Tactical Initiatives"
        );
        var audit = SmartArtDesignStudioViewModel.AuditTargetBullseyeRadiusProgression(targetMd);
        Assert.Equal(3, audit.TotalRings);
        Assert.True(audit.HasCenterCore);
        Assert.True(audit.IsMonotonicProgression);
        Assert.Equal(0, audit.CrowdedRingCount);
        Assert.True(audit.RadialBalanceScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatTargetBullseyeSummary(
            audit.TotalRings, audit.TotalWords, audit.HasCenterCore, audit.IsMonotonicProgression,
            audit.CrowdedRingCount, audit.RadialBalanceScore, audit.Category, "Target Diagram");
        Assert.Equal(audit.Summary, formatted);
    }

[Fact]
    public void Run201_AuditPyramidLayerProportionality_ValidatesTiersAndApexFit()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditPyramidLayerProportionality("");
        Assert.Equal(0, emptyAudit.TotalTiers);
        Assert.Equal(100.0, emptyAudit.ProportionalityScore);
        Assert.Equal("Empty Pyramid", emptyAudit.Category);

        // 2. 4 balanced pyramid tiers
        var pyramidMd = string.Join("\n",
            "- Vision",
            "- Strategy",
            "- Execution",
            "- Governance"
        );
        var goodAudit = SmartArtDesignStudioViewModel.AuditPyramidLayerProportionality(pyramidMd, "Pyramid List");
        Assert.Equal(4, goodAudit.TotalTiers);
        Assert.False(goodAudit.IsInverted);
        Assert.True(goodAudit.IsMonotonicProgression);
        Assert.Equal(0, goodAudit.TruncationRiskCount);
        Assert.True(goodAudit.ProportionalityScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatPyramidProportionalitySummary(
            goodAudit.TotalTiers, goodAudit.TotalWords, goodAudit.IsInverted,
            goodAudit.IsMonotonicProgression, goodAudit.TruncationRiskCount,
            goodAudit.ProportionalityScore, goodAudit.Category, "Pyramid List");
        Assert.Equal(goodAudit.Summary, formatted);

        // 3. Truncation risk at apex
        var longApexMd = string.Join("\n",
            "- Very Extremely Overly Verbose Apex Vision Statement That Exceeds Geometry",
            "- Strategy",
            "- Execution"
        );
        var riskAudit = SmartArtDesignStudioViewModel.AuditPyramidLayerProportionality(longApexMd, "Pyramid List");
        Assert.True(riskAudit.TruncationRiskCount >= 1);
        Assert.True(riskAudit.ProportionalityScore < 95.0);
    }


    [Fact]
    public void Run202_AuditFunnelStageProportionality_ValidatesTiersAndNeckTruncation()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditFunnelStageProportionality("");
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal(100.0, emptyAudit.FunnelGeometricScore);
        Assert.Equal("Empty Funnel", emptyAudit.Category);

        // 2. 4 balanced funnel stages
        var funnelMd = string.Join("\n",
            "- Awareness",
            "- Interest",
            "- Decision",
            "- Action"
        );
        var goodAudit = SmartArtDesignStudioViewModel.AuditFunnelStageProportionality(funnelMd, "Funnel");
        Assert.Equal(4, goodAudit.TotalStages);
        Assert.False(goodAudit.IsInvertedExpansion);
        Assert.True(goodAudit.IsStrictlyMonotonic);
        Assert.Equal(0, goodAudit.TruncationRiskCount);
        Assert.True(goodAudit.FunnelGeometricScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatFunnelProportionalitySummary(
            goodAudit.TotalStages, goodAudit.IsInvertedExpansion, goodAudit.IsStrictlyMonotonic,
            goodAudit.MaxWidthDropPercentage, goodAudit.TruncationRiskCount,
            goodAudit.FunnelGeometricScore, goodAudit.Category, "Funnel");
        Assert.Equal(goodAudit.Summary, formatted);

        // 3. Narrow neck truncation risk
        var neckTruncMd = string.Join("\n",
            "- Awareness",
            "- Interest",
            "- Extremely Long Verbose Text Inside Narrow Funnel Neck Spout"
        );
        var riskAudit = SmartArtDesignStudioViewModel.AuditFunnelStageProportionality(neckTruncMd, "Funnel");
        Assert.True(riskAudit.TruncationRiskCount >= 1);
        Assert.True(riskAudit.FunnelGeometricScore < 95.0);
    }

    [Fact]
    public void Run203_AuditChevronProcessGeometry_ValidatesStagesGapsAndTruncation()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditChevronProcessGeometry("");
        Assert.Equal(0, emptyAudit.TotalStages);
        Assert.Equal(100.0, emptyAudit.LayoutHarmonyScore);
        Assert.Equal("Empty Process", emptyAudit.Category);

        // 2. Harmonious 3-stage chevron process
        var goodProcessMd = string.Join("\n",
            "- Plan",
            "- Execute",
            "- Review"
        );
        var goodAudit = SmartArtDesignStudioViewModel.AuditChevronProcessGeometry(goodProcessMd, "Chevron Process");
        Assert.Equal(3, goodAudit.TotalStages);
        Assert.True(goodAudit.HasUniformGaps);
        Assert.True(goodAudit.HasUniformAngles);
        Assert.Equal(0, goodAudit.TruncationRiskCount);
        Assert.True(goodAudit.LayoutHarmonyScore >= 95.0);

        string formatted = SmartArtDesignStudioViewModel.FormatChevronGeometrySummary(
            goodAudit.TotalStages, goodAudit.AverageGap, goodAudit.GapVariance,
            goodAudit.HasUniformGaps, goodAudit.HasUniformAngles,
            goodAudit.TruncationRiskCount, goodAudit.LayoutHarmonyScore,
            goodAudit.Category, "Chevron Process");
        Assert.Equal(goodAudit.Summary, formatted);

        // 3. Stage with excessive label length causing truncation risk
        var longProcessMd = string.Join("\n",
            "- Step One",
            "- This is an absurdly long chevron stage label exceeding 28 characters threshold",
            "- Step Three"
        );
        var riskAudit = SmartArtDesignStudioViewModel.AuditChevronProcessGeometry(longProcessMd, "Chevron Process");
        Assert.True(riskAudit.TruncationRiskCount >= 1);
        Assert.True(riskAudit.LayoutHarmonyScore < 95.0);
    }


    [Fact]
    public void Run204_AuditVennDiagramGeometry_ValidatesRadiiOverlapAndLabels()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditVennDiagramGeometry("");
        Assert.Equal(0, emptyAudit.SetCount);
        Assert.Equal(100.0, emptyAudit.SetHarmonyScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        // 2. Harmonious 2-set Venn diagram
        var goodVennMd = string.Join("\n",
            "- Set Alpha",
            "- Set Beta"
        );
        var goodAudit = SmartArtDesignStudioViewModel.AuditVennDiagramGeometry(goodVennMd, "Linear Venn");
        Assert.Equal(2, goodAudit.SetCount);
        Assert.True(goodAudit.IsRadiusBalanced);
        Assert.True(goodAudit.SetHarmonyScore >= 95.0);

        string formatted = SmartArtDesignStudioViewModel.FormatVennGeometrySummary(
            goodAudit.SetCount, goodAudit.CircleRadius, goodAudit.OverlapDistance,
            goodAudit.OverlapRatio, goodAudit.IsRadiusBalanced, goodAudit.SetHarmonyScore,
            goodAudit.Category, "Linear Venn");
        Assert.Equal(goodAudit.Summary, formatted);

        // 3. Set with overly long label causing intersection clipping risk
        var longVennMd = string.Join("\n",
            "- Set Alpha With Very Long Text Exceeding Twenty Eight Characters Threshold",
            "- Set Beta"
        );
        var riskAudit = SmartArtDesignStudioViewModel.AuditVennDiagramGeometry(longVennMd, "Linear Venn");
        Assert.True(riskAudit.Sets.Any(s => s.Recommendation.Contains("overlap clipping")));
        Assert.True(riskAudit.SetHarmonyScore < 95.0);
    }


    [Fact]
    public void Run205_AuditMatrixQuadrantGeometry_Validates2x2BalanceAndSymmetry()
    {
        // 1. Empty outline
        var emptyAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantGeometry("");
        Assert.Equal(0, emptyAudit.QuadrantCount);
        Assert.Equal(100.0, emptyAudit.QuadrantSymmetryScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        // 2. Harmonious 4-quadrant SWOT matrix
        var swotMd = string.Join("\n",
            "- Strengths",
            "  High market velocity",
            "- Weaknesses",
            "  Legacy codebase debt",
            "- Opportunities",
            "  Enterprise cloud expansion",
            "- Threats",
            "  Aggressive startup competitors"
        );
        var swotAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantGeometry(swotMd, "SWOT Matrix");
        Assert.Equal(4, swotAudit.QuadrantCount);
        Assert.True(swotAudit.IsStandard2x2);
        Assert.True(swotAudit.QuadrantSymmetryScore >= 85.0);

        string formatted = SmartArtDesignStudioViewModel.FormatMatrixQuadrantGeometrySummary(
            swotAudit.QuadrantCount, swotAudit.IsStandard2x2, swotAudit.TotalWordCount,
            swotAudit.AverageWordCount, swotAudit.CoefficientOfVariation,
            swotAudit.QuadrantSymmetryScore, swotAudit.Category, "SWOT Matrix");
        Assert.Equal(swotAudit.Summary, formatted);

        // 3. Imbalanced matrix with missing 4th quadrant (only 3 items)
        var brokenMd = string.Join("\n",
            "- Quadrant One",
            "- Quadrant Two",
            "- Quadrant Three"
        );
        var brokenAudit = SmartArtDesignStudioViewModel.AuditMatrixQuadrantGeometry(brokenMd, "2x2 Matrix");
        Assert.Equal(3, brokenAudit.QuadrantCount);
        Assert.False(brokenAudit.IsStandard2x2);
        Assert.True(brokenAudit.QuadrantSymmetryScore < 85.0);
    }



    [Fact]
    public void Run206_AuditPyramidHierarchyGeometry_ValidatesTiersAndApexSymmetry()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditPyramidHierarchyGeometry("");
        Assert.Equal(0, emptyAudit.TierCount);
        Assert.Equal(100.0, emptyAudit.PyramidSymmetryScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        var pyramidMd = string.Join("\n",
            "- Strategic Vision",
            "- Executive Leadership",
            "- Operational Management",
            "- Frontline Execution"
        );
        var pyramidAudit = SmartArtDesignStudioViewModel.AuditPyramidHierarchyGeometry(pyramidMd, "Pyramid");
        Assert.Equal(4, pyramidAudit.TierCount);
        Assert.False(pyramidAudit.IsInvertedPyramid);
        Assert.True(pyramidAudit.PyramidSymmetryScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatPyramidHierarchySummary(
            pyramidAudit.TierCount, pyramidAudit.IsInvertedPyramid, pyramidAudit.TotalWordCount,
            pyramidAudit.AverageWordCount, pyramidAudit.TierHeightProportionality,
            pyramidAudit.PyramidSymmetryScore, pyramidAudit.Category, "Pyramid");
        Assert.Equal(pyramidAudit.Summary, formatted);

        var funnelAudit = SmartArtDesignStudioViewModel.AuditPyramidHierarchyGeometry(pyramidMd, "Inverted Funnel");
        Assert.True(funnelAudit.IsInvertedPyramid);
    }


    [Fact]
    public void Run207_AuditChevronProcessStrideGeometry_ValidatesStrideAndTextBalance()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditChevronProcessStrideGeometry("");
        Assert.Equal(0, emptyAudit.StageCount);
        Assert.Equal(100.0, emptyAudit.StrideSymmetryScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        var processMd = string.Join("\n",
            "- Discover",
            "- Design",
            "- Develop",
            "- Deliver"
        );
        var goodAudit = SmartArtDesignStudioViewModel.AuditChevronProcessStrideGeometry(processMd, "Chevron Process");
        Assert.Equal(4, goodAudit.StageCount);
        Assert.Equal(0, goodAudit.TruncationRiskCount);
        Assert.True(goodAudit.StrideSymmetryScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatChevronProcessStrideSummary(
            goodAudit.StageCount, goodAudit.TotalWordCount, goodAudit.AverageWordCount,
            goodAudit.TruncationRiskCount, goodAudit.StrideSymmetryScore, goodAudit.Category, "Chevron Process");
        Assert.Equal(goodAudit.Summary, formatted);

        var riskMd = string.Join("\n",
            "- Step One",
            "- This is an extremely long chevron process step description with way too many words overflowing the body container box",
            "- Step Three"
        );
        var riskAudit = SmartArtDesignStudioViewModel.AuditChevronProcessStrideGeometry(riskMd, "Chevron Process");
        Assert.True(riskAudit.TruncationRiskCount >= 1);
        Assert.True(riskAudit.StrideSymmetryScore < 90.0);
    }



    [Fact]
    public void Run208_AuditTargetBullseyeRingGeometry_ValidatesRadialSymmetryAndLabels()
    {
        var emptyAudit = SmartArtDesignStudioViewModel.AuditTargetBullseyeRingGeometry("");
        Assert.Equal(0, emptyAudit.RingCount);
        Assert.Equal(100.0, emptyAudit.RadialSymmetryScore);
        Assert.Equal("Empty Outline", emptyAudit.Category);

        var targetMd = string.Join("\n",
            "- Core Goal",
            "- Tactical Execution",
            "- Strategic Vision"
        );
        var goodAudit = SmartArtDesignStudioViewModel.AuditTargetBullseyeRingGeometry(targetMd, "Concentric Target");
        Assert.Equal(3, goodAudit.RingCount);
        Assert.Equal(0, goodAudit.OverflowRiskCount);
        Assert.True(goodAudit.RadialSymmetryScore >= 90.0);

        string formatted = SmartArtDesignStudioViewModel.FormatTargetBullseyeGeometrySummary(
            goodAudit.RingCount, goodAudit.TotalWordCount, goodAudit.AverageWordCount,
            goodAudit.CenterBullseyeWords, goodAudit.OverflowRiskCount, goodAudit.RadialSymmetryScore,
            goodAudit.Category, "Concentric Target");
        Assert.Equal(goodAudit.Summary, formatted);

        var riskMd = string.Join("\n",
            "- This center bullseye core has an extraordinarily verbose description that overfills the inner circle",
            "- Outer Ring Context"
        );
        var riskAudit = SmartArtDesignStudioViewModel.AuditTargetBullseyeRingGeometry(riskMd, "Concentric Target");
        Assert.True(riskAudit.OverflowRiskCount >= 1);
        Assert.True(riskAudit.RadialSymmetryScore < 90.0);
    }

}
