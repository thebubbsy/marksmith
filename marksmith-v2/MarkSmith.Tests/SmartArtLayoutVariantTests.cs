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
    [InlineData(SmartArtPreviewFamily.Matrix)]
    [InlineData(SmartArtPreviewFamily.Pyramid)]
    [InlineData(SmartArtPreviewFamily.InvertedPyramid)]
    [InlineData(SmartArtPreviewFamily.Target)]
    [InlineData(SmartArtPreviewFamily.LinearVenn)]
    [InlineData(SmartArtPreviewFamily.StepsUp)]
    [InlineData(SmartArtPreviewFamily.StepsDown)]
    [InlineData(SmartArtPreviewFamily.VerticalProcess)]
    [InlineData(SmartArtPreviewFamily.BendingProcess)]
    [InlineData(SmartArtPreviewFamily.Equation)]
    [InlineData(SmartArtPreviewFamily.HierarchyList)]
    [InlineData(SmartArtPreviewFamily.BlockList)]
    [InlineData(SmartArtPreviewFamily.BlockHierarchy)]
    [InlineData(SmartArtPreviewFamily.Pictures)]
    [InlineData(SmartArtPreviewFamily.HorizontalHierarchy)]
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
        // 25 before run #50, 89 after it, 155 after run #51. Since run #52 no two layouts share one.
        var dupes = Layouts.GroupBy(p => HtmlPreviewRenderer.RenderThumbnailSvg(p.UniqueId)).Where(g => g.Count() > 1)
            .Select(g => string.Join("=", g.Select(p => Tail(p.UniqueId))));
        Assert.Empty(dupes);
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
    public void Gallery_tiles_keep_marks_that_used_to_be_text()
    {
        // Tiles strip text, so operators, card numbers and quote marks drawn as glyphs vanished:
        // Numbered and Quote cards drew one tile, and Equation lost its plus and equals signs.
        Assert.NotEqual(HtmlPreviewRenderer.RenderThumbnailSvg("TextCardSideLineNumbered"), HtmlPreviewRenderer.RenderThumbnailSvg("TextCardSideLineQuote"));
        Assert.NotEqual(HtmlPreviewRenderer.RenderThumbnailSvg("TextCardShortLineNumber"), HtmlPreviewRenderer.RenderThumbnailSvg("TextCardShortLineQuote"));
        Assert.True(Regex.Matches(HtmlPreviewRenderer.RenderThumbnailSvg("equation1"), "<rect").Count >= 4, "plus and equals as shapes");
        Assert.DoesNotContain(">+</text>", Render("- A\n- B\n- C", "equation1"));
    }

    [Fact]
    public void Cycle_matrix_wedges_sit_in_their_own_cards_corners()
    {
        // Each item's wedge is the inner corner of its card: Plan's card is top left, so is its wedge.
        var html = Render("- Plan\n- Build\n- Test\n- Ship", "cycle4");
        double cx = 400;
        foreach (var (name, left, top) in new[] { ("Plan", true, true), ("Build", false, true), ("Test", true, false), ("Ship", false, false) })
        {
            var m = Regex.Match(html, $"<tspan x=\"([0-9.]+)\" y=\"([0-9.]+)\">{name}</tspan>");
            double x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            Assert.True(left ? x < cx : x > cx, $"{name} at x {x}");
            Assert.True(top ? y < 28 + 210 : y > 28 + 210, $"{name} at y {y}");
        }
    }

    [Fact]
    public void Gear_holds_three_and_counts_the_rest()
    {
        var html = Render("- A\n- B\n- C\n- D\n- E", "gear1");
        Assert.Equal(3, Regex.Matches(html, "<polygon").Count);
        Assert.Contains("2 more not shown.", html);
    }

    [Fact]
    public void Bubble_picture_list_makes_the_first_bubble_the_big_one()
    {
        var html = Render("- Lead\n- Two\n- Three", "BubblePictureList");
        var radii = Regex.Matches(html, "<circle cx=\"[0-9.]+\" cy=\"[0-9.]+\" r=\"([0-9.]+)\" fill=\"none\"")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(3, radii.Count); // a donut ring round every picture
        Assert.True(radii[0] > radii[1] * 2, $"rings {string.Join(",", radii)}");
    }

    [Fact]
    public void Alternating_picture_circles_put_the_text_above_and_below_in_turn()
    {
        var html = Render("- One\n- Two\n- Three\n- Four", "AlternatingPictureCircles");
        var ys = new[] { "One", "Two", "Three", "Four" }
            .Select(t => double.Parse(Regex.Match(html, $"<tspan x=\"[0-9.]+\" y=\"([0-9.]+)\">{t}</tspan>").Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.True(ys[1] < ys[0] && ys[3] < ys[2] && ys[1] < ys[2], $"y {string.Join(",", ys)}");
    }

    [Fact]
    public void Picture_accent_list_gives_every_child_its_own_picture()
    {
        var html = Render("- Team\n  - Alice\n  - Bob\n- Plan", "PictureAccentList");
        // A picture placeholder is a hill path: one per header, one per child.
        Assert.Equal(4, Regex.Matches(html, "<path d=\"M[^\"]*Z\" fill=\"#c8c6c4\"/>").Count);
        Assert.Contains(">Alice</tspan>", html);
    }

    [Fact]
    public void Horizontal_hierarchies_each_draw_their_own_way()
    {
        const string md = "- Root\n  - A\n  - B";
        var plain = Render(md, "hierarchy2");
        Assert.Contains("Level 1", Render(md, "hierarchy5"));
        Assert.DoesNotContain("Level 1", plain);
        // Multi-level: the root is a bar as tall as both children together.
        var multi = Render(md, "HorizontalMultiLevelHierarchy");
        var heights = Regex.Matches(multi, "<rect x=\"[0-9.]+\" y=\"[0-9.]+\" width=\"[0-9.]+\" height=\"([0-9.]+)\"")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.True(heights[0] > heights[1] * 1.8, $"heights {string.Join(",", heights)}");
    }
}
