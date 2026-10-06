using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using Xunit;

namespace MarkSmith.Tests;

// The SmartArt preview draws each layout family its own way (it used to pick one of six drawings by
// alias keyword, so most of the 176 layouts previewed as a flat row of boxes).
public class SmartArtPreviewFamilyTests
{
    private const string Org = "- Executive Board\n  - CEO\n    - Engineering Team\n    - Product Team\n  - CFO\n  - CMO";

    [Theory]
    [InlineData("orgChart1", SmartArtPreviewFamily.Hierarchy)]
    [InlineData("hierarchy", SmartArtPreviewFamily.Hierarchy)]
    [InlineData("architecture", SmartArtPreviewFamily.BlockHierarchy)]
    [InlineData("HorizontalOrganizationChart", SmartArtPreviewFamily.HorizontalHierarchy)]
    [InlineData("process1", SmartArtPreviewFamily.Process)]
    [InlineData("process2", SmartArtPreviewFamily.VerticalProcess)]
    [InlineData("chevron1", SmartArtPreviewFamily.Chevron)]
    [InlineData("bProcess3", SmartArtPreviewFamily.BendingProcess)]
    [InlineData("StepUpProcess", SmartArtPreviewFamily.StepsUp)]
    [InlineData("BulletTimeline", SmartArtPreviewFamily.Timeline)]
    [InlineData("cycle", SmartArtPreviewFamily.Cycle)]
    [InlineData("radial1", SmartArtPreviewFamily.Radial)]
    [InlineData("matrix1", SmartArtPreviewFamily.Matrix)]
    [InlineData("pyramid1", SmartArtPreviewFamily.Pyramid)]
    [InlineData("pyramid3", SmartArtPreviewFamily.InvertedPyramid)]
    [InlineData("funnel1", SmartArtPreviewFamily.InvertedPyramid)]
    [InlineData("venn1", SmartArtPreviewFamily.Venn)]
    [InlineData("venn3", SmartArtPreviewFamily.LinearVenn)]
    [InlineData("target1", SmartArtPreviewFamily.Target)]
    [InlineData("balance1", SmartArtPreviewFamily.Balance)]
    [InlineData("equation1", SmartArtPreviewFamily.Equation)]
    [InlineData("picturelist", SmartArtPreviewFamily.Pictures)]
    [InlineData("MeetTheTeam", SmartArtPreviewFamily.Pictures)]
    [InlineData("list", SmartArtPreviewFamily.BlockList)]
    [InlineData("vList2", SmartArtPreviewFamily.VerticalList)]
    [InlineData("hList1", SmartArtPreviewFamily.HorizontalList)]
    public void Layout_PreviewsAsItsFamily(string alias, SmartArtPreviewFamily expected)
    {
        Assert.Equal(expected, HtmlPreviewRenderer.ResolveFamily(alias));
    }

    [Fact]
    public void Catalog_ExactTailWins_OverALongerUrnEndingTheSame()
    {
        // "process2" used to resolve to whichever of bProcess2 / lProcess2 sorted first — the wrong
        // layout in the preview AND in the DOCX export.
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve("process2");
        Assert.NotNull(pkg);
        Assert.EndsWith("/process2", pkg!.UniqueId);
    }

    [Fact]
    public void EveryCatalogLayout_RendersOneSvgWithEveryItem()
    {
        var ast = MarkdownAstParser.Parse(Org);
        foreach (var pkg in SmartArtLayoutCatalog.Shared.All)
        {
            var html = HtmlPreviewRenderer.RenderHtml(ast, pkg.UniqueId, pkg.Title);
            Assert.Single(Regex.Matches(html, "<svg"));
            // Every item is in the drawing, if only in a shape's tooltip.
            foreach (var label in new[] { "Executive Board", "CEO", "Engineering Team", "Product Team", "CFO", "CMO" })
                Assert.True(html.Contains(label), $"{pkg.UniqueId} lost '{label}'");
        }
    }

    [Fact]
    public void Coordinates_AreCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var html = HtmlPreviewRenderer.RenderHtml(MarkdownAstParser.Parse("- A\n- B\n- C"), "cycle1", "Basic Cycle");
            // A comma decimal ("123,4") inside a coordinate list breaks the SVG.
            Assert.DoesNotMatch(new Regex(@"(?:cx|cy|r|x|y|width|height)=""-?\d+,\d"), html);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EmptyOutline_ShowsAHint_NotABlankCard()
    {
        var html = HtmlPreviewRenderer.RenderHtml(MarkdownAstParser.Parse(""), "process1", "Basic Process");
        Assert.Contains("Add items to the outline", html);
    }

    [Fact]
    public void ProcessShapes_CarryTheirSubItemsAsBullets()
    {
        // Top-level items are the shapes; sub-items are bullet text inside them (Word's data model).
        var html = HtmlPreviewRenderer.RenderHtml(MarkdownAstParser.Parse("- Plan\n  - Scope\n- Build"), "process1", "Basic Process");
        Assert.Equal(2, Regex.Matches(html, "<rect ").Count);
        Assert.Contains("Scope", html);
    }

    [Fact]
    public void WideOrgChart_HangsLeaves_InsteadOfShrinkingToASliver()
    {
        var md = "- Company\n" + string.Join("\n", Enumerable.Range(1, 4).Select(i => $"  - Division {i}\n    - Team {i}a\n    - Team {i}b\n    - Team {i}c"));
        var html = HtmlPreviewRenderer.RenderHtml(MarkdownAstParser.Parse(md), "orgChart1", "Organization Chart");
        var viewBox = Regex.Match(html, @"viewBox=""0 0 (\d+(?:\.\d+)?) ").Groups[1].Value;
        Assert.Equal("800", viewBox); // 12 leaves side by side would need ~1,500 px and scale to unreadable
    }

    [Fact]
    public void Studio_SingleRootOutline_ExplainsTheOneShapeLayout()
    {
        var vm = new MarkSmith.ViewModels.SmartArtStudio.SmartArtDesignStudioViewModel();
        vm.SelectedLayout = vm.Layouts.First(l => l.Alias == "process1");
        Assert.True(vm.HasPreviewHint); // the default outline has one root: a process draws one box
        Assert.Contains("Executive Board", vm.PreviewHint);

        vm.SelectedLayout = vm.Layouts.First(l => l.Alias == "orgChart1");
        Assert.False(vm.HasPreviewHint); // hierarchies draw every level
    }
}
