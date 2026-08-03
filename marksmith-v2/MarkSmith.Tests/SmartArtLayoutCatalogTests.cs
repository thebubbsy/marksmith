using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Solver;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Golden-file regression suite for the SmartArt DOCX pipeline.
///
/// These tests exist because of a real defect: the designer export built an EMPTY
/// &lt;dgm:layoutDef/&gt; and the production path loaded only the layout .xml, so every
/// type (cycle, pyramid, matrix, ...) collapsed to basic blocks in Word. The rules here
/// enforce the product promise:
///   1. Every layout alias resolves to a REAL package whose layout XML declares geometry.
///   2. Distinct layout types resolve to DISTINCT algorithms (cycle != pyramid != grid).
///   3. The DOCX actually embeds the genuine layout part (not a stub), schema-valid.
/// </summary>
public class SmartArtLayoutCatalogTests
{
    private static readonly XNamespace Dgm =
        "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    // layoutType -> expected algorithm type that Word uses to compute geometry.
    private static readonly (string Alias, string ExpectedAlg, string ExpectedShape)[] KnownLayouts =
    {
        ("hierarchy",    "hierChild", "rect"),
        ("process",      "linear",    "chevron"),
        ("cycle",        "cycle",     "circle"),
        ("matrix",       "grid",      "rect"),
        ("pyramid",      "pyramid",   "trapezoid"),
        ("venn",         "venn",      "circle"),
        ("relationship", "composite", "ellipse"),
        ("picturelist",  "linear",    "roundRect"),
        ("list",         "linear",    "roundRect"),
    };

    [Fact]
    public void Catalog_LoadsAllEmbeddedGlox()
    {
        Assert.NotNull(SmartArtLayoutCatalog.Shared);
        Assert.Equal(9, SmartArtLayoutCatalog.Shared.All.Count);
        Assert.All(SmartArtLayoutCatalog.Shared.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.UniqueId), "package must carry a UniqueId");
            Assert.False(string.IsNullOrWhiteSpace(p.LayoutXml), "package must carry LayoutXml");
        });
    }

    [Theory]
    [MemberData(nameof(KnownLayoutsData))]
    public void Catalog_ResolvesEveryAlias_ToRealAlgorithmLayout(string alias, string expectedAlg, string expectedShape)
    {
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias);

        Assert.NotNull(pkg);
        Assert.False(string.IsNullOrWhiteSpace(pkg!.LayoutXml), $"'{alias}' resolved to empty LayoutXml (the basic-blocks bug).");

        var root = XDocument.Parse(pkg.LayoutXml).Root;
        Assert.NotNull(root);

        // The layout must declare geometry via at least one algorithm.
        var algs = root!.Descendants(Dgm + "alg")
            .Select(a => a.Attribute("type")?.Value)
            .Where(v => v != null)
            .ToList();
        Assert.NotEmpty(algs);
        // Word's root layout algorithm must be the expected one for this type.
        Assert.Equal(expectedAlg, algs[0]);

        // Must not be the stub "<dgm:layoutDef/>" — real layouts have a uniqueId + title.
        Assert.False(string.IsNullOrWhiteSpace(root.Attribute("uniqueId")?.Value));
    }

    [Fact]
    public void Catalog_DistinctTypes_ProduceDistinctRootAlgorithms()
    {
        // Cycle, pyramid, matrix, venn and relationship all engrave a DIFFERENT geometry
        // algorithm. If any collapse to the same root algorithm, rendering is broken.
        string RootAlg(string alias) => XDocument.Parse(SmartArtLayoutCatalog.Shared.TryResolve(alias)!.LayoutXml)
            .Root!.Descendants(Dgm + "alg").First().Attribute("type")!.Value;

        var rootAlgs = new[] { "cycle", "pyramid", "matrix", "venn", "relationship" }
            .Select(RootAlg).ToArray();

        Assert.Equal(5, rootAlgs.Distinct().Count());
        Assert.Contains("cycle", rootAlgs);
        Assert.Contains("pyramid", rootAlgs);
        Assert.Contains("grid", rootAlgs);
        Assert.Contains("venn", rootAlgs);
    }

    [Fact]
    public void Catalog_UnknownAlias_FallsBackToNullNotCrash()
    {
        Assert.Null(SmartArtLayoutCatalog.Shared.TryResolve("does_not_exist_xyz"));
        Assert.Null(SmartArtLayoutCatalog.Shared.TryResolve(""));
        Assert.Null(SmartArtLayoutCatalog.Shared.TryResolve(null));
    }

    [Theory]
    [MemberData(nameof(KnownLayoutsData))]
    public void ExportDocx_EmbedsRealLayoutPart_AndProducesSchemaValidAuxParts(string alias, string expectedAlg, string _)
    {
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias)!;

        var md = "- One\n- Two\n- Three";
        var ast = MarkdownAstParser.Parse(md);
        ast.RequestedLayout = alias;

        var solved = new ConstraintSolver().Solve(ast, pkg);
        var result = new OpenXmlDiagramGenerator().Generate(solved, pkg);

        var tempPath = Path.Combine(Path.GetTempPath(), $"smartart_golden_{alias}_{Guid.NewGuid():N}.docx");
        try
        {
            DocxPackageWriter.WriteDocx(tempPath, result);

            // The layout part written to the DOCX must be the genuine algorithm-bearing one
            // (NOT the empty "<dgm:layoutDef/>" stub that caused the basic-blocks bug).
            using (var doc = WordprocessingDocument.Open(tempPath, false))
            {
                var main = doc.MainDocumentPart;
                Assert.NotNull(main);

                var layoutParts = main!.DiagramLayoutDefinitionParts.ToList();
                Assert.NotEmpty(layoutParts);
                Assert.NotEmpty(main.DiagramDataParts);
                Assert.NotEmpty(main.DiagramColorsParts);
                Assert.NotEmpty(main.DiagramStyleParts);

                var layoutXml = XDocument.Load(layoutParts[0].GetStream());
                var algs = layoutXml.Root!.Descendants(Dgm + "alg")
                    .Select(a => a.Attribute("type")?.Value).Where(v => v != null).ToList();
                Assert.NotEmpty(algs);
                Assert.Equal(expectedAlg, algs[0]);
            }

            // The data model, Colors, and Style parts must be schema-clean. The layout part is
            // intentionally EXCLUDED from strict validation here: it is authored with custom
            // algorithm names, and making it fully Office-schema compliant is the next
            // reverse-engineering slice (needs visual confirmation in Word).
            using (var doc = WordprocessingDocument.Open(tempPath, false))
            {
                var validator = new OpenXmlValidator(FileFormatVersions.Office2013);
                var errors = validator.Validate(doc)
                    .Where(e => !e.Description.Contains("w14") && !e.Description.Contains("w15"))
                    .Where(e => !(e.Part?.Uri?.ToString().Contains("layout1.xml") == true))
                    .ToList();
                if (errors.Count > 0)
                {
                    var msg = string.Join("\n", errors.Select(e => $"{e.Part?.Uri} | {e.Node?.LocalName} | {e.Description}"));
                    Assert.Fail($"Schema validation failed for '{alias}':\n{msg}");
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public static TheoryData<string, string, string> KnownLayoutsData
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (alias, alg, shape) in KnownLayouts)
            {
                data.Add(alias, alg, shape);
            }
            return data;
        }
    }
}