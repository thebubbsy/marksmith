using System;
using System.IO;
using System.Linq;
using System.Threading;
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

                bool watchMode = args.Contains("--watch") || args.Contains("-w");
                var remainingArgs = args.Where(a => a != "--watch" && a != "-w").ToArray();

                if (remainingArgs.Length < 2)
                {
                    PrintHelp();
                    return 1;
                }

                string inputPath = Path.GetFullPath(remainingArgs[0]);
                string outputPath = Path.GetFullPath(remainingArgs[1]);

                if (!File.Exists(inputPath))
                {
                    Console.Error.WriteLine($"Error: Input file '{inputPath}' does not exist.");
                    return 1;
                }

                var settings = new AppSettings();
                for (int i = 2; i < remainingArgs.Length; i++)
                {
                    if (remainingArgs[i] == "--theme" && i + 1 < remainingArgs.Length)
                        settings.Theme = remainingArgs[i + 1];
                }

                async Task CompileAsync()
                {
                    string markdown = await File.ReadAllTextAsync(inputPath);
                    string ext = Path.GetExtension(outputPath).ToLowerInvariant();

                    if (ext == ".docx" || ext == ".dotx")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Compiling '{Path.GetFileName(inputPath)}' -> '{Path.GetFileName(outputPath)}'...");
                        var docxService = new DocxExportService();
                        await docxService.ExportAsync(markdown, outputPath, settings);
                        Console.WriteLine($"✓ [{DateTime.Now:HH:mm:ss}] Exported native DOCX: {outputPath}");
                    }
                    else if (ext == ".html" || ext == ".htm")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Rendering '{Path.GetFileName(inputPath)}' -> '{Path.GetFileName(outputPath)}'...");
                        var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                        string html = new MarkdownHtmlService().Render(markdown, settings, theme);
                        await File.WriteAllTextAsync(outputPath, html);
                        Console.WriteLine($"✓ [{DateTime.Now:HH:mm:ss}] Exported HTML: {outputPath}");
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported output extension '{ext}'. Use .docx, .dotx, or .html.");
                    }
                }

                await CompileAsync();

                if (watchMode)
                {
                    Console.WriteLine($"[WATCH] Watching '{inputPath}' for changes (Ctrl+C to stop)...");
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (s, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                    };

                    string inDir = Path.GetDirectoryName(inputPath) ?? ".";
                    string inName = Path.GetFileName(inputPath);
                    using var fsw = new FileSystemWatcher(inDir, inName)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };

                    Timer? debounceTimer = null;
                    fsw.Changed += (s, e) =>
                    {
                        debounceTimer?.Dispose();
                        debounceTimer = new Timer(async _ =>
                        {
                            try
                            {
                                await CompileAsync();
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[WATCH ERROR] {ex.Message}");
                            }
                        }, null, 250, Timeout.Infinite);
                    };

                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    }
                    Console.WriteLine("[WATCH] Stopped watching.");
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
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
            Console.WriteLine("  marksmith <input.md> <output.docx|output.html> [--theme <name>] [--watch]");
            Console.WriteLine("  marksmith compose <image.png> <output.md|output.docx> [--grid <n>] [--compact]");
            Console.WriteLine("  marksmith trace <image.png> <output.md|output.docx> [--rows <n>] [--mode <mode>] [--compact]");
            Console.WriteLine();
            Console.WriteLine("Flags:");
            Console.WriteLine("  -w, --watch    Watch the input file for changes and recompile automatically");
            Console.WriteLine("  --compact      Compress vector shapes in markdown using dense deflate format");
            Console.WriteLine("  --theme <name> Apply named theme (e.g. 'GitHub Light', 'Nordic', 'Obsidian')");
            Console.WriteLine();
            Console.WriteLine("Trace Modes: CrossHatch, TopographicWaves, Calligraphic, Engraved, Edges, Scanlines, Silhouette");
        }
    }
}
