using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Solver;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// SmartArt Amendability Recipe — the upgrade contract:
//   1. The 176 native Office layouts are embedded and registered (not stubs).
//   2. Aliases resolve to authoritative Microsoft URNs (hierarchy -> orgChart1, ...).
//   3. The solver emits native Office coloring: per-LEVEL presStyleLbl (node0..node4) and
//      per-SIBLING presStyleIdx/Cnt, exactly like the corpus data models.
//   4. Every DOCX export carries a theme (accent1..6) so colors resolve in Word.
public class SmartArtAmendabilityTests
{
    private static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    [Fact]
    public void Catalog_RegistersAllNativeLayouts()
    {
        var catalog = SmartArtLayoutCatalog.Shared;
        Assert.True(catalog.All.Count >= 176, $"registered {catalog.All.Count}, expected >= 176");
        Assert.True(catalog.All.Count <= 186, $"registered {catalog.All.Count}, expected <= 186");
    }

    [Theory]
    [InlineData("hierarchy", "orgChart1")]
    [InlineData("orgchart", "orgChart1")]
    [InlineData("org", "orgChart1")]
    [InlineData("tree", "orgChart1")]
    [InlineData("process", "process1")]
    [InlineData("workflow", "process1")]
    [InlineData("cycle", "cycle1")]
    [InlineData("matrix", "matrix1")]
    [InlineData("pyramid", "pyramid1")]
    [InlineData("venn", "venn1")]
    [InlineData("picturelist", "pList1")]
    [InlineData("relationship", "CircleRelationship")]
    public void Alias_ResolvesToAuthoritativeOfficeUrn(string alias, string urnSuffix)
    {
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias);
        Assert.NotNull(pkg);
        Assert.EndsWith("/" + urnSuffix, pkg!.UniqueId);
        Assert.True(pkg.UniqueId.StartsWith("urn:microsoft.com/office/officeart/"),
            $"'{alias}' resolved to non-authoritative URN {pkg.UniqueId}");
    }

    [Fact]
    public void Solver_AssignsPerLevelStyleLabels_AndPerSiblingIndices()
    {
        // - CEO (level 0, 2 siblings) -> node0, idx 0, cnt 2
        //   - Eng (level 1, 1 sibling) -> node1, idx 0, cnt 1
        //     - Dev (level 2)          -> node2, idx 0, cnt 2
        //     - QA  (level 2)          -> node2, idx 1, cnt 2
        // - Mkt (level 0, 2 siblings)  -> node0, idx 1, cnt 2
        var ast = MarkdownAstParser.Parse("- CEO\n  - Eng\n    - Dev\n    - QA\n- Mkt");
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve("hierarchy")!;
        var solved = new ConstraintSolver().Solve(ast, pkg);

        string StyleLabelFor(string text)
        {
            var dataPt = solved.Points.First(p => p.PointType == "node" && p.Text == text);
            var presPt = solved.Points.First(p => p.PointType == "pres" && p.PresAssocId == dataPt.ModelId);
            return presPt.PresStyleLbl;
        }

        (int Idx, int Cnt) StyleFor(string text)
        {
            var dataPt = solved.Points.First(p => p.PointType == "node" && p.Text == text);
            var presPt = solved.Points.First(p => p.PointType == "pres" && p.PresAssocId == dataPt.ModelId);
            return (presPt.PresStyleIdx, presPt.PresStyleCnt);
        }

        Assert.Equal("node0", StyleLabelFor("CEO"));
        Assert.Equal("node0", StyleLabelFor("Mkt"));
        Assert.Equal("node1", StyleLabelFor("Eng"));
        Assert.Equal("node2", StyleLabelFor("Dev"));
        Assert.Equal("node2", StyleLabelFor("QA"));

        Assert.Equal((0, 2), StyleFor("CEO"));
        Assert.Equal((1, 2), StyleFor("Mkt"));
        Assert.Equal((0, 1), StyleFor("Eng"));
        Assert.Equal((0, 2), StyleFor("Dev"));
        Assert.Equal((1, 2), StyleFor("QA"));

        // Per-level means non-uniform colors: at least two distinct style labels in one tree.
        var labels = new[] { "CEO", "Eng", "Dev", "QA", "Mkt" }.Select(StyleLabelFor).Distinct();
        Assert.True(labels.Count() >= 2, "a hierarchy must cycle styles per depth level");
    }

    [Fact]
    public async Task Export_IncludesTheme_AndPopulatedStyleColors_AndAuthoritativeLayout()
    {
        const string md = ":::smartart type=\"hierarchy\"\n- CEO\n  - Eng\n  - Mkt\n:::";
        var path = Path.Combine(Path.GetTempPath(), $"smartart_amend_{System.Guid.NewGuid():N}.docx");
        try
        {
            await new DocxExportService().ExportAsync(md, path, new AppSettings());

            using var doc = WordprocessingDocument.Open(path, false);
            var main = doc.MainDocumentPart!;

            // 1) Theme part ships with the accent palette.
            Assert.NotNull(main.ThemePart);
            var themeXml = XDocument.Load(main.ThemePart!.GetStream());
            var accentNames = themeXml.Descendants()
                .Where(e => e.Name.LocalName.StartsWith("accent"))
                .Select(e => e.Name.LocalName)
                .Distinct()
                .ToList();
            Assert.Contains("accent1", accentNames);

            // 2) Style + colors parts are the real native definitions, not empty stubs.
            var styleXml = XDocument.Load(main.DiagramStyleParts.First().GetStream());
            Assert.NotNull(styleXml.Root);
            Assert.Equal("styleDef", styleXml.Root!.Name.LocalName);
            Assert.NotEmpty(styleXml.Descendants().Where(e => e.Name.LocalName == "styleLbl"));

            var colorsXml = XDocument.Load(main.DiagramColorsParts.First().GetStream());
            Assert.Equal("colorsDef", colorsXml.Root!.Name.LocalName);

            // 3) Layout part is the authoritative org chart URN.
            var layoutXml = XDocument.Load(main.DiagramLayoutDefinitionParts.First().GetStream());
            Assert.EndsWith("/orgChart1", layoutXml.Root!.Attribute("uniqueId")!.Value);

            // 4) Data part carries the per-level coloring contract.
            var dataXml = XDocument.Load(main.DiagramDataParts.First().GetStream());
            var lbls = dataXml.Descendants(Dgm + "prSet")
                .Select(p => p.Attribute("presStyleLbl")?.Value)
                .Where(v => v != null)
                .Distinct()
                .ToList();
            Assert.Contains("node0", lbls);
            Assert.Contains("node1", lbls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
