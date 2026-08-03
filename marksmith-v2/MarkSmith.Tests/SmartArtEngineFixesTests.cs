using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Mosaic;
using MarkSmith.Core.Solver;
using Xunit;

namespace MarkSmith.Tests;

// Regression tests for the engine-spec fixes:
//  R1  Markdown heading nesting (## A / ### B => B nests under A)
//  R2  GloxExtractor: native header URN + shape/choose/rule extraction
//  R6  Real raster mosaic: sampling + quantization + Floyd-Steinberg hex fills end-to-end
public class SmartArtEngineFixesTests
{
    [Fact]
    public void Headings_NestByLevel()
    {
        var ast = MarkdownAstParser.Parse("# Root\n## Child A\n### Grandchild\n## Child B\n# Second Root");

        Assert.Equal(2, ast.Root.Children.Count);

        var root = ast.Root.Children[0];
        Assert.Equal("Root", root.Text);
        Assert.Equal(2, root.Children.Count);

        var childA = root.Children[0];
        Assert.Equal("Child A", childA.Text);
        Assert.Single(childA.Children);
        Assert.Equal("Grandchild", childA.Children[0].Text);
        Assert.Equal(childA.NodeId, childA.Children[0].ParentId);

        var childB = root.Children[1];
        Assert.Equal("Child B", childB.Text);
        Assert.Empty(childB.Children);
    }

    [Fact]
    public void NativeGlox_HeaderUrn_And_ShapeChooseRule_Extraction()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var layout = archive.CreateEntry("diagrams/layout1.xml");
            using (var w = new StreamWriter(layout.Open()))
            {
                w.Write(@"<dgm:layoutDef xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram"">
  <dgm:layoutNode name=""diagram""><dgm:alg type=""linear""/><dgm:shape type=""rect""/></dgm:layoutNode>
  <dgm:layoutNode name=""node""><dgm:shape type=""roundRect""/></dgm:layoutNode>
  <dgm:choose name=""pick""><dgm:if name=""one""/><dgm:else/></dgm:choose>
  <dgm:rule type=""primFontSz"" val=""0.5""/>
</dgm:layoutDef>");
            }
            var header = archive.CreateEntry("diagrams/layoutHeader1.xml");
            using (var w = new StreamWriter(header.Open()))
            {
                w.Write(@"<layoutDefHdr xmlns=""http://schemas.openxmlformats.org/drawingml/2006/diagram"" uniqueId=""urn:microsoft.com/office/officeart/2005/8/layout/architecture""><title val=""Architecture Layout""/></layoutDefHdr>");
            }
        }

        stream.Position = 0;
        var pkg = GloxExtractor.ExtractFromZip(stream);

        Assert.Equal("urn:microsoft.com/office/officeart/2005/8/layout/architecture", pkg.UniqueId);
        Assert.Equal("Architecture Layout", pkg.Title);
        Assert.Equal("rect", pkg.ShapeMappings.GetValueOrDefault("diagram"));
        Assert.Equal("roundRect", pkg.ShapeMappings.GetValueOrDefault("node"));
        Assert.Single(pkg.ChooseBlocks);
        Assert.Equal(2, pkg.ChooseBlocks[0].Conditions.Count);
        Assert.Equal("one", pkg.ChooseBlocks[0].Conditions[0]);
        Assert.Single(pkg.Rules);
        Assert.Equal("primFontSz", pkg.Rules[0].Type);
    }

    [Fact]
    public void Mosaic_Tiles_Have_Quantized_Hex_Fills()
    {
        var options = new RasterMosaicOptions { GridWidth = 4, GridHeight = 4 };
        var ast = RasterMosaicEngine.GenerateMosaicAst("does-not-exist.png", options);

        Assert.Equal(16, ast.Root.Children.Count);

        foreach (var node in ast.Root.Children)
        {
            Assert.True(node.Attributes.TryGetValue("hexColor", out var hex));
            Assert.Equal(6, hex!.Length);
            Assert.All(hex, c => Assert.True(Uri.IsHexDigit(c)));
            Assert.True(node.Attributes.TryGetValue("gridX", out _));
            Assert.True(node.Attributes.TryGetValue("gridY", out _));
        }
    }

    [Fact]
    public void Mosaic_HexFill_Flows_Into_Generated_DiagramData()
    {
        var options = new RasterMosaicOptions { GridWidth = 3, GridHeight = 3, PaletteColors = 8 };
        var ast = RasterMosaicEngine.GenerateMosaicAst("missing.png", options);

        var pkg = SmartArtLayoutCatalog.Shared.TryResolve("grid")
                  ?? SmartArtLayoutCatalog.Shared.TryResolve("default");
        Assert.NotNull(pkg);

        var solved = new ConstraintSolver().Solve(ast, pkg!);
        var genRes = new OpenXmlDiagramGenerator().Generate(solved, pkg!);

        // Every tile node's spPr must carry an a:solidFill with an srgbClr val="#RRGGBB".
        int solidFills = 0;
        foreach (var node in ast.Root.Children)
        {
            string hex = node.Attributes["hexColor"];
            if (genRes.DiagramDataXml.Contains($"val=\"{hex}\"")) solidFills++;
        }

        Assert.Equal(9, solidFills);
        Assert.DoesNotContain("rIdImg", genRes.DiagramDataXml); // no full-image blipFill per tile
    }
}
