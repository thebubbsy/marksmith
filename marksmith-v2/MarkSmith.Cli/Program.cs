using System;
using System.IO;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Mosaic;
using MarkSmith.Core.Resolver;
using MarkSmith.Core.Solver;
using System.Text.Json;
using MarkSmith.Core.Glox.Builder;
using MarkSmith.Core.Glox.Packager;

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
                Console.WriteLine("       marksmith build-layout <input_layout.json> <output.glox>");
                return;
            }

            string commandOrInput = args[0];
            string arg2 = args[1];

            try
            {
                if (commandOrInput.Equals("build-layout", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Invoking GLOX Packager...");
                    string json = File.ReadAllText(arg2);
                    string outputGlox = args.Length > 2 ? args[2] : arg2.Replace(".json", ".glox");
                    
                    var def = JsonLayoutParser.Parse(json);
                    var xml = GloxXmlSerializer.Serialize(def);
                    MarkSmith.Core.Glox.Packager.GloxPackager.Package(xml, outputGlox);
                    
                    Console.WriteLine($"Successfully generated custom SmartArt GLOX package: {outputGlox}");
                    return;
                }

                string inputPath = commandOrInput;
                string outputPath = arg2;
                string? layoutOverride = args.Length > 2 ? args[2] : null;

                var resolver = MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared;

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
