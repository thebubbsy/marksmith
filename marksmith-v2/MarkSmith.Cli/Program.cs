using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Core.Composer;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Cli
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "/?")
            {
                PrintHelp();
                return 0;
            }

            string cmd = args[0].ToLowerInvariant();

            try
            {
                if (cmd == "compose" && args.Length >= 3)
                {
                    string inputImage = args[1];
                    string outputFile = args[2];
                    int grid = 32;
                    bool compact = args.Contains("--compact");
                    for (int i = 3; i < args.Length; i++)
                    {
                        if (args[i] == "--grid" && i + 1 < args.Length && int.TryParse(args[i + 1], out int g))
                            grid = g;
                    }

                    Console.WriteLine($"Composing '{inputImage}' into vector shapes (grid={grid})...");
                    var shapes = ImageShapeComposer.Compose(inputImage, new ShapeComposerOptions { Grid = grid });
                    string md = ShapeMarkdownCodec.Serialize(shapes, compact: compact);

                    string outExt = Path.GetExtension(outputFile).ToLowerInvariant();
                    if (outExt == ".docx" || outExt == ".dotx")
                    {
                        var docxService = new DocxExportService();
                        await docxService.ExportAsync(md, outputFile, new AppSettings());
                        Console.WriteLine($"✓ Generated Word document: {outputFile} ({shapes.Count} native DrawingML shapes)");
                    }
                    else
                    {
                        await File.WriteAllTextAsync(outputFile, md);
                        Console.WriteLine($"✓ Generated Markdown shapes block: {outputFile} ({shapes.Count} shapes)");
                    }
                    return 0;
                }

                if (cmd == "trace" && args.Length >= 3)
                {
                    string inputImage = args[1];
                    string outputFile = args[2];
                    int rows = 300;
                    var mode = LineTraceMode.CrossHatch;
                    bool compact = args.Contains("--compact");
                    for (int i = 3; i < args.Length; i++)
                    {
                        if (args[i] == "--rows" && i + 1 < args.Length && int.TryParse(args[i + 1], out int r))
                            rows = r;
                        if (args[i] == "--mode" && i + 1 < args.Length && Enum.TryParse<LineTraceMode>(args[i + 1], true, out var m))
                            mode = m;
                    }

                    Console.WriteLine($"Tracing '{inputImage}' into line art ({mode}, rows={rows})...");
                    var lines = ImageLineTracer.TraceLines(inputImage, new LineTraceOptions { Rows = rows, Mode = mode });
                    string md = ShapeMarkdownCodec.Serialize(lines, compact: compact);

                    string outExt = Path.GetExtension(outputFile).ToLowerInvariant();
                    if (outExt == ".docx" || outExt == ".dotx")
                    {
                        var docxService = new DocxExportService();
                        await docxService.ExportAsync(md, outputFile, new AppSettings());
                        Console.WriteLine($"✓ Generated Word document: {outputFile} ({lines.Count} vector line strokes)");
                    }
                    else
                    {
                        await File.WriteAllTextAsync(outputFile, md);
                        Console.WriteLine($"✓ Generated Markdown line art: {outputFile} ({lines.Count} lines)");
                    }
                    return 0;
                }

                if (args.Length < 2)
                {
                    PrintHelp();
                    return 1;
                }

                string inputPath = args[0];
                string outputPath = args[1];

                if (!File.Exists(inputPath))
                {
                    Console.Error.WriteLine($"Error: Input file '{inputPath}' does not exist.");
                    return 1;
                }

                string markdown = await File.ReadAllTextAsync(inputPath);
                string ext = Path.GetExtension(outputPath).ToLowerInvariant();

                var settings = new AppSettings();
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--theme" && i + 1 < args.Length)
                        settings.Theme = args[i + 1];
                }

                if (ext == ".docx" || ext == ".dotx")
                {
                    Console.WriteLine($"Compiling '{inputPath}' -> '{outputPath}' via DocxExportService...");
                    var docxService = new DocxExportService();
                    await docxService.ExportAsync(markdown, outputPath, settings);
                    Console.WriteLine($"✓ Successfully exported native DOCX: {outputPath}");
                }
                else if (ext == ".html" || ext == ".htm")
                {
                    Console.WriteLine($"Rendering '{inputPath}' -> '{outputPath}'...");
                    var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                    string html = new MarkdownHtmlService().Render(markdown, settings, theme);
                    await File.WriteAllTextAsync(outputPath, html);
                    Console.WriteLine($"✓ Successfully exported HTML: {outputPath}");
                }
                else
                {
                    Console.Error.WriteLine($"Unsupported output extension '{ext}'. Use .docx, .dotx, or .html.");
                    return 1;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.Message}");
                return 1;
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine("MarkSmith CLI v2.18.0");
            Console.WriteLine("Universal Markdown, DrawingML Vector Shapes & SmartArt Compiler");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  marksmith <input.md> <output.docx|output.html> [--theme <name>]");
            Console.WriteLine("  marksmith compose <image.png> <output.md|output.docx> [--grid <n>] [--compact]");
            Console.WriteLine("  marksmith trace <image.png> <output.md|output.docx> [--rows <n>] [--mode <mode>] [--compact]");
            Console.WriteLine();
            Console.WriteLine("Trace Modes: CrossHatch, TopographicWaves, Calligraphic, Engraved, Edges, Scanlines, Silhouette");
        }
    }
}
