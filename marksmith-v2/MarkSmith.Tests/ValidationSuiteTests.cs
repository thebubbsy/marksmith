using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Mosaic;
using MarkSmith.Core.Resolver;
using MarkSmith.Core.Solver;

namespace MarkSmith.Tests
{
    public class ValidationSuite
    {
        private static readonly XNamespace DgmNs = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        private static readonly XNamespace ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";

        public void RunAllTests()
        {
            Console.WriteLine("=== MarkSmith SmartArt Compiler Validation Suite ===");

            TestUrnResolverAndZeroFallback();
            TestLayoutValidation("hierarchy");
            TestLayoutValidation("cycle");
            TestLayoutValidation("picturelist");
            TestImageValidation();
            TestRasterMosaicValidation();

            Console.WriteLine("\n[SUCCESS] All Validation Suite assertions passed cleanly!");
        }

        public void TestUrnResolverAndZeroFallback()
        {
            Console.Write("Testing URN Resolver & Zero Fallback Guarantee... ");

            var resolver = new UrnResolver();
            var glox = GloxExtractor.ExtractFromXmlString(@"<dgm:layoutDef xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram"" uniqueId=""urn:microsoft.com/office/officeart/2005/8/layout/hierarchy1"" title=""Hierarchy""><dgm:layoutNode name=""diagram""/></dgm:layoutDef>");
            resolver.RegisterLayout(glox);

            var resolved = resolver.Resolve("hierarchy");
            if (resolved.UniqueId != "urn:microsoft.com/office/officeart/2005/8/layout/hierarchy1")
            {
                throw new Exception("URN resolution returned incorrect uniqueId.");
            }

            try
            {
                resolver.Resolve("invalid_nonexistent_layout_xyz");
                throw new Exception("Zero fallback guarantee violated! Invalid URN did not throw UrnResolutionException.");
            }
            catch (UrnResolutionException)
            {
                // Expected behaviour
            }

            Console.WriteLine("PASSED!");
        }

        public void TestLayoutValidation(string layoutAlias)
        {
            Console.Write($"Testing Layout Validation for '{layoutAlias}'... ");

            string markdown = $@"---
layout: {layoutAlias}
---
- Executive Level
  - Engineering
    - Backend
    - Frontend
  - Product
";

            var ast = MarkdownAstParser.Parse(markdown);
            var resolver = new UrnResolver();
            
            // Load embedded layout
            string gloxXml = GetEmbeddedGloxXml(layoutAlias);
            var glox = GloxExtractor.ExtractFromXmlString(gloxXml);
            resolver.RegisterLayout(glox);

            var resolvedGlox = resolver.Resolve(layoutAlias);
            var solver = new ConstraintSolver();
            var solved = solver.Solve(ast, resolvedGlox);

            var generator = new OpenXmlDiagramGenerator();
            var genResult = generator.Generate(solved, resolvedGlox);

            string docxPath = Path.Combine(Path.GetTempPath(), $"test_{layoutAlias}.docx");
            DocxPackageWriter.WriteDocx(docxPath, genResult);

            // Structural & Layout Assertions on docx
            using var archive = ZipFile.OpenRead(docxPath);
            var dataEntry = archive.GetEntry("word/diagrams/data1.xml");
            if (dataEntry == null) throw new Exception("Missing word/diagrams/data1.xml in docx package.");

            using var reader = new StreamReader(dataEntry.Open());
            string xmlData = reader.ReadToEnd();

            var doc = XDocument.Parse(xmlData);
            var ptElements = doc.Descendants(DgmNs + "pt").ToList();
            var cxnElements = doc.Descendants(DgmNs + "cxn").ToList();

            // 1. Assert all modelIds are unique
            var modelIds = ptElements.Select(e => e.Attribute("modelId")?.Value).Where(id => id != null).ToList();
            if (modelIds.Count != modelIds.Distinct().Count())
            {
                throw new Exception("Duplicate modelId found in ptLst.");
            }

            // 2. Assert all connection references exist in point list
            var ptIdSet = new HashSet<string>(modelIds!);
            foreach (var cxn in cxnElements)
            {
                string src = cxn.Attribute("srcId")?.Value ?? "";
                string dest = cxn.Attribute("destId")?.Value ?? "";
                string parTrans = cxn.Attribute("parTransId")?.Value ?? "";
                string sibTrans = cxn.Attribute("sibTransId")?.Value ?? "";

                if (!ptIdSet.Contains(src)) throw new Exception($"Connection srcId '{src}' not found in ptLst.");
                if (!ptIdSet.Contains(dest)) throw new Exception($"Connection destId '{dest}' not found in ptLst.");
                if (!string.IsNullOrEmpty(parTrans) && !ptIdSet.Contains(parTrans)) throw new Exception($"Connection parTransId '{parTrans}' not found in ptLst.");
                if (!string.IsNullOrEmpty(sibTrans) && !ptIdSet.Contains(sibTrans)) throw new Exception($"Connection sibTransId '{sibTrans}' not found in ptLst.");
            }

            // 3. Assert no fallback blocklist elements
            if (xmlData.Contains("blocklist") || xmlData.Contains("fallbackBlock"))
            {
                throw new Exception("Fallback blocklist element detected in generated diagramData.xml.");
            }

            File.Delete(docxPath);
            Console.WriteLine("PASSED!");
        }

        public void TestImageValidation()
        {
            Console.Write("Testing Image Validation & BlipFill Injection... ");

            string markdown = @"---
layout: picturelist
---
- ![Profile Pic](profile.png)
";

            var ast = MarkdownAstParser.Parse(markdown);
            var glox = GloxExtractor.ExtractFromXmlString(GetEmbeddedGloxXml("picturelist"));

            var solver = new ConstraintSolver();
            var solved = solver.Solve(ast, glox);

            var generator = new OpenXmlDiagramGenerator();
            var genResult = generator.Generate(solved, glox);

            string docxPath = Path.Combine(Path.GetTempPath(), "test_image.docx");
            DocxPackageWriter.WriteDocx(docxPath, genResult);

            using var archive = ZipFile.OpenRead(docxPath);
            
            // Assert image part exists
            var imageEntry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("word/media/"));
            if (imageEntry == null) throw new Exception("Image part missing in word/media/ folder.");

            // Assert blipFill exists in diagramData.xml
            var dataEntry = archive.GetEntry("word/diagrams/data1.xml");
            using var reader = new StreamReader(dataEntry!.Open());
            string xmlData = reader.ReadToEnd();

            if (!xmlData.Contains("blipFill") || !xmlData.Contains("r:embed"))
            {
                throw new Exception("Missing <a:blipFill> element or r:embed attribute in diagramData.xml.");
            }

            File.Delete(docxPath);
            Console.WriteLine("PASSED!");
        }

        public void TestRasterMosaicValidation()
        {
            Console.Write("Testing Raster Mosaic Engine... ");

            var options = new RasterMosaicOptions
            {
                GridWidth = 4,
                GridHeight = 4,
                TargetLayout = "picturelist"
            };

            var ast = RasterMosaicEngine.GenerateMosaicAst("sample.png", options);
            if (ast.Root.Children.Count != 16)
            {
                throw new Exception($"Mosaic grid node count mismatch: expected 16, got {ast.Root.Children.Count}");
            }

            // Every tile must carry a 6-digit hex fill attribute (real quantization path).
            foreach (var node in ast.Root.Children)
            {
                if (!node.Attributes.TryGetValue("hexColor", out string? hex)
                    || string.IsNullOrWhiteSpace(hex)
                    || hex.Length != 6
                    || !hex.All(c => Uri.IsHexDigit(c)))
                {
                    throw new Exception($"Mosaic tile {node.NodeId} has invalid hexColor: '{hex}'");
                }
            }

            Console.WriteLine("PASSED!");
        }

        public void TestMarkdownHeadingNesting()
        {
            Console.Write("Testing Markdown heading nesting... ");

            var ast = MarkdownAstParser.Parse("# Root\n## Child A\n### Grandchild\n## Child B\n# Second Root");

            var rootChildren = ast.Root.Children;
            if (rootChildren.Count != 2)
            {
                throw new Exception($"Expected 2 top-level headings, got {rootChildren.Count}");
            }

            var first = rootChildren[0];
            if (first.Text != "Root" || first.Children.Count != 2)
            {
                throw new Exception($"'Root' should have 2 children, got {first.Children.Count}");
            }

            var childA = first.Children[0];
            if (childA.Text != "Child A" || childA.Children.Count != 1)
            {
                throw new Exception($"'Child A' should have 1 child, got {childA.Children.Count}");
            }

            if (childA.Children[0].Text != "Grandchild")
            {
                throw new Exception("'Grandchild' should nest under 'Child A'");
            }

            var childB = first.Children[1];
            if (childB.Text != "Child B" || childB.Children.Count != 0)
            {
                throw new Exception("'Child B' should be a leaf sibling of 'Child A'");
            }

            if (first.Children.All(n => n.ParentId != first.NodeId))
            {
                throw new Exception("Children must reference the parent heading's NodeId");
            }

            Console.WriteLine("PASSED!");
        }

        public void TestGloxHeaderUrnExtraction()
        {
            Console.Write("Testing native glox header URN extraction... ");

            // Mimics a native Office .glox: uniqueId lives in layoutHeader1.xml, layoutDef has none.
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var layout = archive.CreateEntry("diagrams/layout1.xml");
                using (var w = new StreamWriter(layout.Open()))
                {
                    w.Write(@"<dgm:layoutDef xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram"">
  <dgm:layoutNode name=""diagram""><dgm:alg type=""linear""/><dgm:shape type=""rect""/></dgm:layoutNode>
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

            if (pkg.UniqueId != "urn:microsoft.com/office/officeart/2005/8/layout/architecture")
            {
                throw new Exception($"Header URN not extracted: '{pkg.UniqueId}'");
            }
            if (pkg.Title != "Architecture Layout")
            {
                throw new Exception($"Header title not extracted: '{pkg.Title}'");
            }
            if (pkg.ShapeMappings.GetValueOrDefault("diagram") != "rect")
            {
                throw new Exception($"Shape mapping not extracted: {string.Join(",", pkg.ShapeMappings)}");
            }
            if (pkg.ChooseBlocks.Count != 1 || pkg.ChooseBlocks[0].Conditions.Count != 2)
            {
                throw new Exception($"choose blocks not extracted: {pkg.ChooseBlocks.Count}");
            }
            if (pkg.Rules.Count != 1 || pkg.Rules[0].Type != "primFontSz")
            {
                throw new Exception($"rules not extracted: {pkg.Rules.Count}");
            }

            Console.WriteLine("PASSED!");
        }

        private static string GetEmbeddedGloxXml(string name)
        {
            string path = $"/working_dir/c_e56ad443be9377d2/marksmith-v2/src/MarkSmith.Core/Resources/EmbeddedGlox/{name}.xml";
            if (File.Exists(path)) return File.ReadAllText(path);
            
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<dgm:layoutDef xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram""
               uniqueId=""urn:microsoft.com/office/officeart/2005/8/layout/{name}1""
               title=""{name}"" desc=""{name} layout"">
  <dgm:layoutNode name=""diagram"">
    <dgm:alg type=""linear""/>
    <dgm:shape type=""rect""/>
  </dgm:layoutNode>
</dgm:layoutDef>";
        }
    }
}
