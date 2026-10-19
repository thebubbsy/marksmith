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
}






