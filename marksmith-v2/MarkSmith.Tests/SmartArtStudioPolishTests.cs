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
}
