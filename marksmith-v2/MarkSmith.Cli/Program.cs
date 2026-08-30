using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Composer;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Cli
{
    class Program
    {
        /// <summary>The running assembly's version — the banner used to hard-code a literal.</summary>
        private static string AppVersion
        {
            get
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Program).Assembly;
                var iv = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
                            .InformationalVersion;
                if (string.IsNullOrWhiteSpace(iv)) return asm.GetName().Version?.ToString(3) ?? "0.0.0";
                var plus = iv.IndexOf('+');
                return plus > 0 ? iv[..plus] : iv;
            }
        }

        static async Task<int> Main(string[] args)
        {
            // Without this the checkmark and the progress bar came out as "?" boxes on a default
            // Windows console, because stdout stayed on the OEM code page.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "/?")
            {
                PrintHelp();
                return 0;
            }

            string cmd = args[0].ToLowerInvariant();

            try
            {
                if (cmd == "batch" || args[0] == "--batch")
                {
                    string inputPattern = "./*.md";
                    string format = "docx";
                    // null = alongside each input file. This used to default to "." — the shell's
                    // working directory — so "marksmith batch <some-folder>" scattered its output
                    // wherever you happened to be standing rather than next to the documents, with
                    // nothing in the help text to say so.
                    string? outputDir = null;
                    int concurrency = Environment.ProcessorCount;
                    bool recursive = false;
                    bool continueOnError = false;

                    for (int i = 1; i < args.Length; i++)
                    {
                        if (i == 1 && !args[i].StartsWith("-", StringComparison.Ordinal))
                        {
                            inputPattern = args[i];
                            continue;
                        }

                        if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                        else if (args[i] == "--output" && i + 1 < args.Length) outputDir = args[++i];
                        else if (args[i] == "--concurrency" && i + 1 < args.Length && int.TryParse(args[i + 1], out int c)) concurrency = c;
                        else if (args[i] == "-r" || args[i] == "--recursive" || args[i] == "/r" || args[i] == "/s") recursive = true;
                        else if (args[i] == "-f" || args[i] == "--force" || args[i] == "--continue-on-error" || args[i] == "--ignore-errors" || args[i] == "-k" || args[i] == "--keep-going") continueOnError = true;
                        else if (!args[i].StartsWith("-", StringComparison.Ordinal) && inputPattern == "./*.md") inputPattern = args[i];
                    }

                    if ((format == "docx" || format == "dotx") && !EnsureDocxEntitlement()) return 1;
                    return await ExecuteBatchAsync(inputPattern, outputDir, format, concurrency, recursive, continueOnError);
                }

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
                        if (!EnsureDocxEntitlement()) return 1;
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
                        if (!EnsureDocxEntitlement()) return 1;
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

                if (cmd == "render-image" && args.Length >= 3)
                {
                    string inputMd = args[1];
                    string outputFile = args[2];
                    int width = 1200;
                    int height = 0;
                    double scale = 2.0;
                    int quality = 100;
                    string themeName = "GitHub Light";

                    for (int i = 3; i < args.Length; i++)
                    {
                        if (args[i] == "--width" && i + 1 < args.Length && int.TryParse(args[i + 1], out int w)) width = w;
                        if (args[i] == "--height" && i + 1 < args.Length && int.TryParse(args[i + 1], out int h)) height = h;
                        if (args[i] == "--scale" && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s)) scale = s;
                        if (args[i] == "--quality" && i + 1 < args.Length && int.TryParse(args[i + 1], out int q)) quality = q;
                        if (args[i] == "--theme" && i + 1 < args.Length) themeName = args[i + 1];
                    }

                    if (!File.Exists(inputMd))
                    {
                        Console.Error.WriteLine($"Error: Input file '{inputMd}' does not exist.");
                        return 1;
                    }

                    Console.WriteLine($"Rendering snapshot '{inputMd}' -> '{outputFile}' ({width}x{(height > 0 ? height.ToString() : "auto")}, scale={scale}x, theme='{themeName}')...");
                    var mdContent = await File.ReadAllTextAsync(inputMd);
                    var rasterizer = new DocumentImageRasterizerService();
                    var renderSettings = new AppSettings { Theme = themeName };
                    var theme = AppServices.Themes.GetOrDefault(themeName);
                    var options = new ImageRenderOptions(width, height, scale, quality, themeName);
                    await rasterizer.RenderPngToFileAsync(mdContent, outputFile, renderSettings, theme, options);
                    Console.WriteLine($"✓ Generated PNG snapshot: {outputFile}");
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

                // Gate the whole compile+watch session up front when the target is a Pro format.
                if (Path.GetExtension(outputPath).ToLowerInvariant() is ".docx" or ".dotx"
                    && !EnsureDocxEntitlement())
                {
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

                    // The same preparation the desktop and batch paths run before handing markdown
                    // to an exporter. The CLI called the exporters directly and skipped it, so the
                    // same input produced different output here than in the app: chat artifacts
                    // survived and NormalizeLlm did nothing.
                    var classification = AppServices.LlmSource.Classify(markdown);
                    (markdown, _) = AppServices.LlmSource.RepairArtifacts(markdown, classification);
                    if (settings.NormalizeLlm)
                    {
                        (markdown, _) = AppServices.LlmSource.NormalizeStyle(markdown, classification);
                    }

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
                    else if (ext == ".png")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Rasterizing snapshot '{Path.GetFileName(inputPath)}' -> '{Path.GetFileName(outputPath)}'...");
                        var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                        var rasterizer = new DocumentImageRasterizerService();
                        var options = new ImageRenderOptions { Theme = settings.Theme ?? "GitHub Light" };
                        await rasterizer.RenderPngToFileAsync(markdown, outputPath, settings, theme, options);
                        Console.WriteLine($"✓ [{DateTime.Now:HH:mm:ss}] Exported PNG snapshot: {outputPath}");
                    }
                    // EPUB, PPTX and Markdown exporters have existed in Core and been reachable
                    // from the app and from Express all along; only the CLI never wired them up,
                    // while the README listed them as available "or the CLI".
                    else if (ext == ".epub")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Compiling '{Path.GetFileName(inputPath)}' -> '{Path.GetFileName(outputPath)}'...");
                        await new EpubExportService().ExportAsync(markdown, outputPath, settings);
                        Console.WriteLine($"✓ [{DateTime.Now:HH:mm:ss}] Exported EPUB: {outputPath}");
                    }
                    else if (ext == ".pptx")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Compiling '{Path.GetFileName(inputPath)}' -> '{Path.GetFileName(outputPath)}'...");
                        await new PptxExportService().ExportAsync(markdown, outputPath, settings);
                        Console.WriteLine($"✓ [{DateTime.Now:HH:mm:ss}] Exported PPTX: {outputPath}");
                    }
                    else if (ext == ".md" || ext == ".markdown")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Normalizing '{Path.GetFileName(inputPath)}' -> '{Path.GetFileName(outputPath)}'...");
                        await new MarkdownExportService().ExportAsync(markdown, outputPath, settings);
                        Console.WriteLine($"✓ [{DateTime.Now:HH:mm:ss}] Exported Markdown: {outputPath}");
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"Unsupported output extension '{ext}'. Use .docx, .dotx, .html, .epub, .pptx, .md or .png.");
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

        static async Task<int> ExecuteBatchAsync(string inputPattern, string? outputDir, string format, int concurrency, bool recursive = false, bool continueOnError = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string searchDir = Directory.Exists(inputPattern) ? inputPattern : (Path.GetDirectoryName(inputPattern) ?? ".");
            if (string.IsNullOrEmpty(searchDir)) searchDir = ".";
            string pattern = Directory.Exists(inputPattern) ? "*.md" : (Path.GetFileName(inputPattern) ?? "*.md");

            if (!Directory.Exists(searchDir))
            {
                Console.Error.WriteLine($"[ERROR] Directory '{searchDir}' not found.");
                return continueOnError ? 0 : 1;
            }

            string[] files;
            try
            {
                var enumOpts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = recursive,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                    ReturnSpecialDirectories = false
                };
                files = Directory.EnumerateFiles(searchDir, pattern, enumOpts).ToArray();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Failed to scan directory '{searchDir}': {ex.Message}");
                return continueOnError ? 0 : 1;
            }

            if (files.Length == 0)
            {
                Console.WriteLine($"No files matched pattern '{pattern}' in '{searchDir}' (recursive={recursive}).");
                return 0;
            }

            // No --output: write each result beside the document it came from.
            if (outputDir is not null) Directory.CreateDirectory(outputDir);
            Console.WriteLine($"[BATCH] Converting {files.Length} file(s) to .{format} (concurrency={concurrency}, recursive={recursive}, continueOnError={continueOnError})...");

            int completed = 0;
            int errors = 0;
            var docxService = new DocxExportService();
            var htmlService = new MarkdownHtmlService();
            var settings = new AppSettings();
            var theme = AppServices.Themes.GetOrDefault(settings.Theme);

            using var semaphore = new SemaphoreSlim(Math.Max(1, concurrency));

            await Task.WhenAll(files.Select(async file =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    string targetDir = outputDir ?? (Path.GetDirectoryName(file) ?? searchDir);
                    if (recursive)
                    {
                        try
                        {
                            string relPath = Path.GetRelativePath(searchDir, file);
                            string relDir = Path.GetDirectoryName(relPath) ?? "";
                            if (!string.IsNullOrEmpty(relDir))
                            {
                                targetDir = Path.Combine(outputDir, relDir);
                                Directory.CreateDirectory(targetDir);
                            }
                        }
                        catch
                        {
                            targetDir = outputDir;
                        }
                    }

                    string outFile = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(file) + "." + format);

                    // Never write over the document being read. With output defaulting alongside
                    // the input, "--format md" would otherwise replace every source file with its
                    // own normalized output.
                    if (string.Equals(Path.GetFullPath(outFile), Path.GetFullPath(file),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[BATCH] Skipped {Path.GetFileName(file)}: output would overwrite the input. Pass --output <dir>.");
                        Interlocked.Increment(ref completed);
                        return;
                    }

                    string md = await File.ReadAllTextAsync(file).ConfigureAwait(false);

                    // Each format goes to its own exporter. Everything that was not docx used to
                    // fall through to the HTML renderer, so "--format epub" and "--format pptx"
                    // wrote an HTML document under an .epub/.pptx name — a file no reader opens.
                    switch (format)
                    {
                        case "docx":
                        case "dotx":
                            await docxService.ExportAsync(md, outFile, settings).ConfigureAwait(false);
                            break;
                        case "epub":
                            await new EpubExportService().ExportAsync(md, outFile, settings).ConfigureAwait(false);
                            break;
                        case "pptx":
                            await new PptxExportService().ExportAsync(md, outFile, settings).ConfigureAwait(false);
                            break;
                        case "md":
                        case "markdown":
                            await new MarkdownExportService().ExportAsync(md, outFile, settings).ConfigureAwait(false);
                            break;
                        default:
                            await File.WriteAllTextAsync(outFile, htmlService.Render(md, settings, theme))
                                      .ConfigureAwait(false);
                            break;
                    }

                    int cur = Interlocked.Increment(ref completed);
                    int pct = (int)((cur / (double)files.Length) * 100);
                    int barFilled = pct / 10;
                    string bar = new string('█', barFilled) + new string('░', 10 - barFilled);
                    Console.Write($"\r[BATCH] [{bar}] {pct}% ({cur}/{files.Length})");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    if (continueOnError)
                    {
                        Console.Error.WriteLine($"\n[WARN] Skipped '{file}': {ex.Message}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"\n[ERROR] Failed to convert '{file}': {ex.Message}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            })).ConfigureAwait(false);

            sw.Stop();
            Console.WriteLine($"\n[BATCH] ✓ Completed {completed} file(s) in {sw.ElapsedMilliseconds}ms ({errors} error(s)).");
            return (errors > 0 && !continueOnError) ? 1 : 0;
        }

        // Go-live licensing: the CLI is another entrance to the DOCX exporter, so it enforces the
        // same paywall as the desktop app. Loads the shared license file from AppPaths.ConfigDir
        // (so an activation or a trial started in the app is honored here — and trial exports spent
        // here are consumed by the same chokepoint inside DocxExportService).
        static bool EnsureDocxEntitlement()
        {
            AppServices.License.Load();
            if (AppServices.License.CanExportDocx) return true;
            Console.Error.WriteLine("✗ DOCX export is a MarkSmith Pro feature. Activate Pro (or start the 3-export trial) in the MarkSmith app, then retry.");
            return false;
        }

        static void PrintHelp()
        {
            Console.WriteLine($"MarkSmith CLI v{AppVersion}");
            Console.WriteLine("Universal Markdown, DrawingML Vector Shapes & SmartArt Compiler");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  marksmith <input.md> <output.docx|.dotx|.html|.epub|.pptx|.md|.png> [--theme <name>] [--watch]");
            Console.WriteLine("  marksmith render-image <input.md> <output.png> [--width <w>] [--height <h>] [--scale <s>] [--theme <theme>]");
            Console.WriteLine("  marksmith batch <folder|glob> [--output <dir>] [--format <docx|html|epub|pptx|md>] [--concurrency <n>] [-r|--recursive] [-f|--continue-on-error]");
            Console.WriteLine("  marksmith compose <image.png> <output.md|output.docx> [--grid <n>] [--compact]");
            Console.WriteLine("  marksmith trace <image.png> <output.md|output.docx> [--rows <n>] [--mode <mode>] [--compact]");
            Console.WriteLine();
            Console.WriteLine("Flags:");
            Console.WriteLine("  -w, --watch               Watch the input file for changes and recompile automatically");
            Console.WriteLine("  -r, --recursive           Recursively process subdirectories in batch conversion");
            Console.WriteLine("  -f, --continue-on-error   Keep going and skip unreadable/corrupt files without halting (aliases: --ignore-errors, -k)");
            Console.WriteLine("  --batch                   Batch process multiple markdown documents concurrently");
            Console.WriteLine("  --compact                 Compress vector shapes in markdown using dense deflate format");
            Console.WriteLine("  --output <dir>            Batch output folder (default: alongside each input file)");
            Console.WriteLine("  --theme <name>            Apply named theme (e.g. 'GitHub Light', 'Nordic', 'Obsidian')");
            Console.WriteLine("  --width <w>               Snapshot logical width in pixels (default 1200)");
            Console.WriteLine("  --height <h>              Snapshot logical height in pixels (0 = auto height)");
            Console.WriteLine("  --scale <s>               High-DPI device scale multiplier (default 2.0)");
            Console.WriteLine();
            Console.WriteLine("Trace Modes: CrossHatch, TopographicWaves, Calligraphic, Engraved, Edges, Scanlines, Silhouette");
        }
    }
}
