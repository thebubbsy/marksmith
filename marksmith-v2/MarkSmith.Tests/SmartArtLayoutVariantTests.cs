using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using Xunit;

namespace MarkSmith.Tests;

// Run #50: 176 layouts shared 25 drawings, so "Basic Cycle", "Block Cycle" and "Segmented Cycle" had
// the same thumbnail and the same preview. Each layout now has a variant within its family.
public class SmartArtLayoutVariantTests
{
    private static readonly List<GloxPackage> Layouts =
        SmartArtLayoutCatalog.Shared.All.DistinctBy(p => p.UniqueId).ToList();

    private static string Tail(string urn) => urn[(urn.LastIndexOf('/') + 1)..];

    private static string Render(string md, string alias) =>
        HtmlPreviewRenderer.RenderHtml(MarkdownAstParser.Parse(md), alias, alias);

    [Fact]
    public void Basic_block_and_segmented_cycle_no_longer_look_alike()
    {
        var thumbs = new[] { "cycle2", "cycle5", "cycle8" }.Select(HtmlPreviewRenderer.RenderThumbnailSvg).ToList();
        Assert.Equal(3, thumbs.Distinct().Count());
        var previews = new[] { "cycle2", "cycle5", "cycle8" }.Select(a => Render("- A\n- B\n- C\n- D", a)).ToList();
        Assert.Contains("<circle", previews[0]);
        Assert.Contains("rx=\"10\"", previews[1]);                // rounded blocks
        Assert.Matches("<path d=\"M[^\"]*A[^\"]*L[^\"]*A[^\"]*Z\"", previews[2]); // donut segments
    }

    [Theory]
    [InlineData(SmartArtPreviewFamily.Cycle)]
    [InlineData(SmartArtPreviewFamily.Radial)]
    [InlineData(SmartArtPreviewFamily.Process)]
    [InlineData(SmartArtPreviewFamily.Timeline)]
    [InlineData(SmartArtPreviewFamily.HorizontalList)]
    [InlineData(SmartArtPreviewFamily.Balance)]
    [InlineData(SmartArtPreviewFamily.Hierarchy)]
    [InlineData(SmartArtPreviewFamily.Chevron)]
    public void Every_layout_in_the_big_families_has_its_own_thumbnail(SmartArtPreviewFamily family)
    {
        var tails = Layouts.Where(p => HtmlPreviewRenderer.ResolveFamily(p.UniqueId) == family).Select(p => Tail(p.UniqueId)).ToList();
        var thumbs = tails.Select(HtmlPreviewRenderer.RenderThumbnailSvg).ToList();
        var dupes = tails.Zip(thumbs).GroupBy(t => t.Second).Where(g => g.Count() > 1).Select(g => string.Join("=", g.Select(x => x.First)));
        Assert.Empty(dupes);
    }

    [Fact]
    public void The_gallery_has_far_more_than_one_drawing_per_family()
    {
        int distinct = Layouts.Select(p => HtmlPreviewRenderer.RenderThumbnailSvg(p.UniqueId)).Distinct().Count();
        // 25 before run #50, 89 after it, 122 after run #51. Some layouts still share a drawing (Meet the Team / Meet the Team Oval).
        Assert.True(distinct >= 120, $"{distinct} distinct thumbnails");
    }

    [Fact]
    public void Every_variant_is_used_by_a_real_layout()
    {
        var used = Layouts.Select(p => HtmlPreviewRenderer.ResolveVariant(p.UniqueId)).ToHashSet();
        var unused = Enum.GetValues<PreviewVariant>().Where(v => v != PreviewVariant.Default && !used.Contains(v));
        Assert.Empty(unused);
    }

    [Fact]
    public void Variants_resolve_by_alias_urn_and_case()
    {
        Assert.Equal(PreviewVariant.CycleGears, HtmlPreviewRenderer.ResolveVariant("gear1"));
        Assert.Equal(PreviewVariant.CycleGears, HtmlPreviewRenderer.ResolveVariant("urn:microsoft.com/office/officeart/2005/8/layout/gear1"));
        Assert.Equal(PreviewVariant.CyclePie, HtmlPreviewRenderer.ResolveVariant("CHART3"));
        Assert.Equal(PreviewVariant.Default, HtmlPreviewRenderer.ResolveVariant("cycle2"));
        Assert.Equal(PreviewVariant.Default, HtmlPreviewRenderer.ResolveVariant("no-such-layout"));
    }

    public static IEnumerable<object[]> Outlines() => new[]
    {
        new object[] { "- Solo" },
        new object[] { "- Plan\n  - Scope the work\n- Build\n- Test" },
        new object[] { string.Join("\n", Enumerable.Range(1, 9).Select(i => $"- Step {i}\n  - Detail {i}")) },
    };

    [Theory]
    [MemberData(nameof(Outlines))]
    public void Every_layout_draws_every_item_or_says_how_many_it_left_out(string md)
    {
        var titles = Regex.Matches(md, "^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        foreach (var p in Layouts)
        {
            string html = Render(md, p.UniqueId);
            string words = WebUtility.HtmlDecode(string.Concat(Regex.Matches(html, "<tspan[^>]*>([^<]*)</tspan>").Select(m => m.Groups[1].Value + " ")));
            words = words.Replace((char)160, ' ');
            bool all = titles.All(t => words.Contains(t, StringComparison.Ordinal) || html.Contains("<title>" + t, StringComparison.Ordinal));
            Assert.True(all || html.Contains("more not shown", StringComparison.Ordinal), $"{Tail(p.UniqueId)} lost an item: {words}");
        }
    }

    [Theory]
    [MemberData(nameof(Outlines))]
    public void Every_layout_keeps_its_shapes_inside_the_drawing(string md)
    {
        foreach (var p in Layouts)
        {
            string html = Render(md, p.UniqueId);
            var vb = Regex.Match(html, "viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"");
            double w = double.Parse(vb.Groups[1].Value, CultureInfo.InvariantCulture), h = double.Parse(vb.Groups[2].Value, CultureInfo.InvariantCulture);
            int start = html.IndexOf("<svg", StringComparison.Ordinal);
            var (x0, y0, x1, y1) = HtmlPreviewRenderer.ShapeBounds(html[start..]);
            Assert.True(x0 >= -2 && y0 >= -2 && x1 <= w + 2 && y1 <= h + 2,
                $"{Tail(p.UniqueId)}: shapes span {x0:0},{y0:0} to {x1:0},{y1:0} in a {w:0} x {h:0} drawing");
        }
    }

    [Fact]
    public void Every_layout_thumbnail_is_text_free_and_sized()
    {
        foreach (var p in Layouts)
        {
            var svg = HtmlPreviewRenderer.RenderThumbnailSvg(p.UniqueId);
            Assert.DoesNotContain("<text", svg);
            Assert.Matches("width=\"[0-9.]+\" height=\"[0-9.]+\"", svg);
        }
    }

    [Fact]
    public void Thumbnails_draw_text_on_the_page_as_bars()
    {
        // Text Card layouts are text and a rule: without bars their tile was a few coloured lines.
        var svg = HtmlPreviewRenderer.RenderThumbnailSvg("TextCardShortLine");
        Assert.Contains("fill=\"#a19f9d\"", svg);
        Assert.Contains("fill=\"#d2d0ce\"", svg);
    }

    [Fact]
    public void Thumbnail_connectors_are_thick_enough_to_see()
    {
        var svg = HtmlPreviewRenderer.RenderThumbnailSvg("cycle6");
        var widths = Regex.Matches(svg, "<circle[^>]*fill=\"none\"[^>]*stroke-width=\"([0-9.]+)\"|<path[^>]*fill=\"none\"[^>]*stroke-width=\"([0-9.]+)\"")
            .Select(m => double.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(widths);
        Assert.All(widths, w => Assert.True(w >= 7, $"stroke {w}"));
        Assert.Contains("markerWidth=\"22\"", HtmlPreviewRenderer.RenderThumbnailSvg("cycle5"));
    }

    [Fact]
    public void Cycle_arrowheads_are_shapes_the_gallery_can_draw()
    {
        // Direct2D's SVG renderer (the gallery tiles) draws no markers.
        Assert.Equal(4, Regex.Matches(HtmlPreviewRenderer.RenderThumbnailSvg("cycle5"), "<polygon").Count);   // one head per link
        Assert.Equal(8, Regex.Matches(HtmlPreviewRenderer.RenderThumbnailSvg("cycle7"), "<polygon").Count);  // two per link
        Assert.Equal(0, Regex.Matches(HtmlPreviewRenderer.RenderThumbnailSvg("cycle6"), "<polygon").Count);   // none
        Assert.DoesNotContain("marker-end", Render(string.Join('\n', "- A", "- B", "- C"), "cycle5"));
    }

    [Fact]
    public void The_preview_says_which_variant_it_drew()
    {
        Assert.Contains("data-variant=\"CycleSegmented\"", Render("- A\n- B", "cycle8"));
        Assert.Contains("data-variant=\"Default\"", Render("- A\n- B", "cycle2"));
    }

    // Run #51: timelines, horizontal lists, balances, chevrons and org charts.

    [Theory]
    [InlineData("NumberedDotsHorizontal", "TimelineNumberedDots")]
    [InlineData("SmallDotsVertical", "TimelineSmallDotsVertical")]
    [InlineData("BulletTimelineInverted", "TimelineBulletInverted")]
    [InlineData("AlternatingCircleProcess", "TimelineAlternatingCircles")]
    [InlineData("hList6", "HListTrapezoids")]
    [InlineData("TabList", "HListTabs")]
    [InlineData("hChevron3", "ChevronClosed")]
    [InlineData("arrow5", "BalanceConverging")]
    [InlineData("HalfCircleOrganizationChart", "TreeHalfCircle")]
    [InlineData("orgChart1", "Default")]
    public void Linear_and_org_chart_layouts_draw_their_own_variant(string alias, string variant)
    {
        Assert.Contains($"data-variant=\"{variant}\"", Render("- A\n  - a\n- B", alias));
    }

    [Fact]
    public void Labels_hanging_under_timeline_markers_start_level()
    {
        // A one-line label used to be centred in a slot sized for the tallest, so it sagged below its neighbours.
        var html = Render("- Plan\n  - Scope\n  - Agree\n  - Budget\n- Ship", "NumberedDotsHorizontal");
        double Top(string word) => double.Parse(Regex.Match(html, $"<tspan[^>]*y=\"([0-9.]+)\">{word}</tspan>").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(Top("Plan"), Top("Ship"));
    }

    [Fact]
    public void Arrow_layouts_keep_their_text_inside_the_shaft()
    {
        var md = "- Plan\n  - Scope the work\n  - Agree goals\n- Build\n  - Write the code\n- Test\n  - Automated checks\n- Ship\n- Learn";
        foreach (var alias in new[] { "arrow1", "arrow5", "arrow6" })
        {
            var html = Render(md, alias);
            var shafts = Regex.Matches(html, "<polygon points=\"([^\"]+)\"").Select(m => m.Groups[1].Value.Split(' ')
                .Select(p => double.Parse(p.Split(',')[1], CultureInfo.InvariantCulture)).ToArray()).ToList();
            // The shaft's top and bottom edges are the first and last points of each arrow.
            var lines = Regex.Matches(html, "<tspan[^>]*y=\"([0-9.]+)\"").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            Assert.All(lines, y => Assert.Contains(shafts, s => y > Math.Min(s[0], s[^1]) && y < Math.Max(s[0], s[^1]) + 1));
        }
    }

    [Fact]
    public void Org_chart_variants_keep_every_connector()
    {
        // A, its two children and their four: six edges whichever way the boxes are drawn.
        var md = "- A\n  - B\n    - D\n    - E\n  - C\n    - F\n    - G";
        foreach (var alias in new[] { "orgChart1", "hierarchy1", "pictureOrgChart+Icon", "NameandTitleOrganizationalChart", "HalfCircleOrganizationChart", "CirclePictureHierarchy", "hierarchy6" })
            Assert.Equal(6, Regex.Matches(Render(md, alias), "<path d=\"M[^\"]*V[^\"]*H[^\"]*V[^\"]*\" fill=\"none\" stroke=\"#8a8886\"").Count);
    }

    [Fact]
    public void Gear_holds_three_and_counts_the_rest()
    {
        var html = Render("- A\n- B\n- C\n- D\n- E", "gear1");
        Assert.Equal(3, Regex.Matches(html, "<polygon").Count);
        Assert.Contains("2 more not shown.", html);
    }
}
