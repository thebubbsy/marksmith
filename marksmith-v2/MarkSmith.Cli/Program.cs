using System;
using System.IO;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Mosaic;
using MarkSmith.Core.Resolver;
using MarkSmith.Core.Solver;

namespace MarkSmith.Cli
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("MarkSmith SmartArt Compiler v2.0");
            Console.WriteLine("Universal SmartArt Reverse-Engineering & Rendering Compiler");

            if (args.Length < 2)
            {
                Console.WriteLine("Usage: marksmith <input.md|input.json|input.png> <output.docx> [layout_alias]");
                return;
            }

            string inputPath = args[0];
            string outputPath = args[1];
            string? layoutOverride = args.Length > 2 ? args[2] : null;

            try
            {
                var resolver = new UrnResolver();
                var assembly = typeof(MarkSmith.Core.Glox.GloxExtractor).Assembly;
                foreach (string resourceName in assembly.GetManifestResourceNames())
                {
                    if (resourceName.EndsWith(".xml") && resourceName.Contains("EmbeddedGlox"))
                    {
                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            using var reader = new System.IO.StreamReader(stream);
                            var glox = MarkSmith.Core.Glox.GloxExtractor.ExtractFromXmlString(reader.ReadToEnd());
                            resolver.RegisterLayout(glox);
                        }
                    }
                }

                CanonicalAst ast;
                string ext = Path.GetExtension(inputPath).ToLower();

                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                {
                    Console.WriteLine("Raster input detected. Invoking Mosaic Engine...");
                    var mosaicOptions = new RasterMosaicOptions
                    {
                        GridWidth = 8,
                        GridHeight = 8,
                        TargetLayout = layoutOverride ?? "picturelist"
                    };
                    ast = RasterMosaicEngine.GenerateMosaicAst(inputPath, mosaicOptions);
                }
                else if (ext == ".json")
                {
                    string json = File.ReadAllText(inputPath);
                    ast = JsonAstParser.Parse(json);
                }
                else
                {
                    string md = File.ReadAllText(inputPath);
                    ast = MarkdownAstParser.Parse(md);
                }

                string targetLayout = layoutOverride ?? ast.RequestedLayout ?? "hierarchy";
                Console.WriteLine($"Resolving layout URN for target layout '{targetLayout}'...");

                var gloxPkg = resolver.Resolve(targetLayout);
                var solver = new ConstraintSolver();
                var solvedStructure = solver.Solve(ast, gloxPkg);

                var generator = new OpenXmlDiagramGenerator();
                var genResult = generator.Generate(solvedStructure, gloxPkg);

                DocxPackageWriter.WriteDocx(outputPath, genResult);
                Console.WriteLine($"Successfully generated Word document with native SmartArt: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COMPILER ERROR] {ex.Message}");
            }
        }
    }
}
