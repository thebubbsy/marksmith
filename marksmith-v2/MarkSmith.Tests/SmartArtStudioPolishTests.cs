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
}
