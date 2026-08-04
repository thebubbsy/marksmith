using System.Drawing;
using System.Drawing.Imaging;
using NetOffice.WordApi;

// marksmith-office-host — the Marksmith "Office Capability" plugin payload.
// Out-of-process helper that drives the INSTALLED Microsoft Word via NetOffice
// (MIT, version-agnostic COM wrapper) to produce the 100%-accurate render of a
// .docx that OpenXML parsing can never give you.
//
// Commands (all on an STA thread — COM requires it):
//   detect                       -> exit 0 if Word is installed, 1 otherwise
//   render <docx> <out.png>      -> open, repaginate, rasterize the first
//                                   InlineShape to PNG; HTML round-trip fallback
//   verify <docx>                -> JSON: inlineShapes / shapes / paragraphs counts
//   pdf <docx> <out.pdf>         -> Word's OWN PDF export (ExportAsFixedFormat): headers,
//                                   footers, page borders, vector fonts — the real page
//                                   furniture the range-EMF band render drops.
//   pdfpage <pdf> <page> <out.png> [scale]
//                                 -> rasterize one PDF page via Windows.Data.Pdf at any
//                                   DPI (crisp vector text), no extra dependency.
//   server                       -> persistent mode: one JSON command per stdin line,
//                                   one JSON response per stdout line. Keeps Word open
//                                   so page-band re-renders are cheap (tiled preview):
//                                     {"cmd":"open","docx":"..."}  -> {"ok":true,"pages":N}
//                                     {"cmd":"page","page":3,"out":"..."} -> {"ok":true,"bytes":N}
//                                     {"cmd":"close"}              -> {"ok":true}
public class Program
{
    // Word processes born during this invocation get force-killed after Quit — Word's Quit()
    // can leave a zombie on some Office builds, and a zombie blocks the next detect/render
    // (Word is single-instance), which is exactly how the Word-exact toggle "stops working".
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    private static void CleanupWord()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("WINWORD"))
            {
                try
                {
                    if (p.StartTime.ToUniversalTime() >= StartedAt.AddSeconds(-3))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { /* access denied / already gone */ }
            }
        }
        catch { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: marksmith-office-host <detect|render|verify> ..."); return 2; }
        try
        {
            return args[0] switch
            {
                "detect" => Detect(),
                "render" => args.Length >= 3 ? Render(args[1], args[2]) : Fail("render <docx> <out.png>"),
                "verify" => args.Length >= 2 ? Verify(args[1]) : Fail("verify <docx>"),
                "pdf" => args.Length >= 3 ? PdfExport(args[1], args[2], args.Length >= 5 ? int.Parse(args[3]) : 0, args.Length >= 5 ? int.Parse(args[4]) : 0) : Fail("pdf <docx> <out.pdf> [from] [to]"),
                "pdfpage" => args.Length >= 4 ? PdfPage(args[1], args[2], args[3], args.Length >= 5 ? args[4] : "2") : Fail("pdfpage <pdf> <page> <out.png> [scale]"),
                "server" => Server(),
                _ => Fail($"unknown command '{args[0]}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("OFFICE-HOST ERROR: " + ex.Message);
            return 1;
        }
    }

    static int Detect()
    {
        Application? app = null;
        try
        {
            app = new Application();
            app.Visible = false;
            app.DisplayAlerts = 0;
            Console.WriteLine("word-version=" + (app.Version ?? "?"));
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Word not available.");
            return 1;
        }
        finally
        {
            try { app?.Quit(); } catch { }
            app?.Dispose();
            CleanupWord();
        }
    }

    static int Render(string docxPath, string outPath)
    {
        Application? app = null;
        Document? doc = null;
        try
        {
            if (!File.Exists(docxPath)) return Fail($"docx not found: {docxPath}");
            File.Delete(outPath);

            app = new Application();
            app.Visible = false;
            app.DisplayAlerts = 0;
            doc = app.Documents.Open(Path.GetFullPath(docxPath), false, true, false); // ReadOnly
            doc.Repaginate();

            // SmartArt is an INLINE shape: Range.EnhMetaFileBits -> EMF -> PNG.
            if (doc.InlineShapes.Count > 0)
            {
                object? bits = doc.InlineShapes[1].Range.EnhMetaFileBits;
                if (TryBitsToPng(bits, outPath))
                {
                    Console.WriteLine($"rendered inline shape -> {outPath}");
                    return 0;
                }
            }

            // Floating shape groups (:::shapes / sketch): whole-page EMF is the reliable raster.
            try
            {
                object? pageBits = doc.Range().EnhMetaFileBits;
                if (TryBitsToPng(pageBits, outPath))
                {
                    Console.WriteLine($"rendered page (EMF) -> {outPath}");
                    return 0;
                }
            }
            catch { /* fall through */ }

            // HTML round-trip fallback: Word rasterizes the whole page; grab the image.
            string html = Path.ChangeExtension(outPath, ".htm");
            doc.SaveAs2(Path.GetFullPath(html), 8); // wdFormatHTML
            string? found = FindHtmlImage(html);
            if (found != null)
            {
                File.Copy(found, outPath, overwrite: true);
                Console.WriteLine($"rendered via HTML fallback -> {outPath}");
                return 0;
            }

            return Fail("no renderable content found");
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            try { app?.Quit(); } catch { }
            doc?.Dispose();
            app?.Dispose();
            CleanupWord();
        }
    }

    static int Verify(string docxPath)
    {
        Application? app = null;
        Document? doc = null;
        try
        {
            if (!File.Exists(docxPath)) return Fail($"docx not found: {docxPath}");
            app = new Application();
            app.Visible = false;
            app.DisplayAlerts = 0;
            doc = app.Documents.Open(Path.GetFullPath(docxPath), false, true, false);
            doc.Repaginate();
            Console.WriteLine($"{{\"inlineShapes\":{doc.InlineShapes.Count},\"shapes\":{doc.Shapes.Count},\"paragraphs\":{doc.Paragraphs.Count}}}");
            return 0;
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            try { app?.Quit(); } catch { }
            doc?.Dispose();
            app?.Dispose();
            CleanupWord();
        }
    }

    static bool TryBitsToPng(object? bits, string outPath, double scale = 1.0)
    {
        try
        {
            byte[] bytes = bits switch
            {
                byte[] b => b,
                object[] oa => oa.Select(o => Convert.ToByte(o)).ToArray(),
                _ => throw new InvalidOperationException("unexpected EnhMetaFileBits type")
            };
            if (bytes.Length == 0) return false;
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            if (scale > 0 && Math.Abs(scale - 1.0) > 0.001)
            {
                int w = Math.Max(1, (int)(img.Width * scale));
                int h = Math.Max(1, (int)(img.Height * scale));
                using var scaled = new Bitmap(w, h);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, w, h);
                }
                scaled.Save(outPath, ImageFormat.Png);
            }
            else
            {
                img.Save(outPath, ImageFormat.Png);
            }
            return File.Exists(outPath);
        }
        catch
        {
            return false;
        }
    }

    static string? FindHtmlImage(string htmlPath)
    {
        string dir = Path.GetDirectoryName(htmlPath)!;
        string baseName = Path.GetFileNameWithoutExtension(htmlPath);
        string[] candidates =
        {
            Path.Combine(dir, baseName + ".files"),
            Path.Combine(dir, baseName + "_files"),
            Path.Combine(dir, baseName + ".fld"),
            Path.Combine(dir, baseName + "_fld")
        };
        foreach (var folder in candidates)
        {
            if (!Directory.Exists(folder)) continue;
            var imgs = Directory.GetFiles(folder)
                .Where(f => f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToArray();
            if (imgs.Length > 0) return imgs[0];
        }
        return null;
    }

    // Persistent mode: keep Word open across commands so page-band re-renders reuse the
    // process. The document is reopened on each open command (cheap); Word itself stays up.
    static int Server()
    {
        Application? app = null;
        Document? doc = null;
        try
        {
            app = new Application();
            app.Visible = false;
            app.DisplayAlerts = 0;

            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(line);
                    var root = json.RootElement;
                    string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

                    if (cmd == "open")
                    {
                        string? path = root.TryGetProperty("docx", out var p) ? p.GetString() : null;
                        if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        {
                            Respond(new { ok = false, error = $"docx not found: {path}" });
                            continue;
                        }
                        try { doc?.Close(0); } catch { }
                        doc?.Dispose();
                        doc = app.Documents.Open(Path.GetFullPath(path), false, true, false);
                        doc.Repaginate();
                        int pages = Convert.ToInt32(doc.Content.Information(NetOffice.WordApi.Enums.WdInformation.wdNumberOfPagesInDocument));
                        Respond(new { ok = true, pages });
                    }
                    else if (cmd == "page")
                    {
                        if (doc == null)
                        {
                            Respond(new { ok = false, error = "no document open" });
                            continue;
                        }
                        int page = root.TryGetProperty("page", out var pg) ? pg.GetInt32() : 1;
                        double scale = root.TryGetProperty("scale", out var sc) ? sc.GetDouble() : 1.0;
                        string outPath = Path.GetFullPath(root.GetProperty("out").GetString()!);
                        File.Delete(outPath);
                        doc.Repaginate();
                        bool ok = RenderPageBand(app, doc, page, outPath, scale);
                        Respond(new { ok, bytes = ok ? new FileInfo(outPath).Length : 0, page });
                    }
                    else if (cmd == "pdf")
                    {
                        if (doc == null)
                        {
                            Respond(new { ok = false, error = "no document open" });
                            continue;
                        }
                        string outPath = Path.GetFullPath(root.GetProperty("out").GetString()!);
                        int from = root.TryGetProperty("from", out var fr) ? fr.GetInt32() : 0;
                        int to = root.TryGetProperty("to", out var tt) ? tt.GetInt32() : 0;
                        File.Delete(outPath);
                        try
                        {
                            if (from > 0 && to >= from)
                            {
                                doc.ExportAsFixedFormat(outPath,
                                    NetOffice.WordApi.Enums.WdExportFormat.wdExportFormatPDF,
                                    false,
                                    NetOffice.WordApi.Enums.WdExportOptimizeFor.wdExportOptimizeForPrint,
                                    NetOffice.WordApi.Enums.WdExportRange.wdExportFromTo,
                                    from, to,
                                    NetOffice.WordApi.Enums.WdExportItem.wdExportDocumentContent,
                                    true, true,
                                    NetOffice.WordApi.Enums.WdExportCreateBookmarks.wdExportCreateHeadingBookmarks,
                                    true, true, false);
                            }
                            else
                            {
                                doc.ExportAsFixedFormat(outPath, NetOffice.WordApi.Enums.WdExportFormat.wdExportFormatPDF);
                            }
                            Respond(new { ok = true, bytes = new FileInfo(outPath).Length, exported = to >= from ? to - from + 1 : 0 });
                        }
                        catch (Exception ex)
                        {
                            Respond(new { ok = false, error = ex.Message });
                        }
                    }
                    else if (cmd == "pdfpage")
                    {
                        string pdfPath = Path.GetFullPath(root.GetProperty("pdf").GetString()!);
                        int page = root.GetProperty("page").GetInt32();
                        string outPath = Path.GetFullPath(root.GetProperty("out").GetString()!);
                        double scale = root.TryGetProperty("scale", out var sc) ? sc.GetDouble() : 2.0;
                        try
                        {
                            RenderPdfPageAsync(pdfPath, page, scale, outPath).GetAwaiter().GetResult();
                            Respond(new { ok = File.Exists(outPath), bytes = File.Exists(outPath) ? new FileInfo(outPath).Length : 0 });
                        }
                        catch (Exception ex)
                        {
                            Respond(new { ok = false, error = ex.Message });
                        }
                    }
                    else if (cmd == "close")
                    {
                        Respond(new { ok = true });
                        break;
                    }
                    else
                    {
                        Respond(new { ok = false, error = $"unknown cmd: {cmd}" });
                    }
                }
                catch (Exception ex)
                {
                    Respond(new { ok = false, error = ex.Message });
                }
            }
            return 0;
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            try { app?.Quit(); } catch { }
            doc?.Dispose();
            app?.Dispose();
            CleanupWord();
        }
    }

    // Rasterize just page N's band: the range from page N's start to page N+1's start.
    // Range.GoTo(wdGoToPage=1, wdGoToAbsolute=1, count) collapses at the page start.
    static bool RenderPageBand(Application app, Document doc, int page, string outPath, double scale)
    {
        try
        {
            object wdGoToPage = 1, wdGoToAbsolute = 1;
            var startRange = doc.Content.GoTo(wdGoToPage, wdGoToAbsolute, page);
            long start = startRange.Start;

            long end;
            var nextRange = doc.Content.GoTo(wdGoToPage, wdGoToAbsolute, page + 1);
            if (nextRange.Start > start)
            {
                end = nextRange.Start - 1;
            }
            else
            {
                end = doc.Content.End;
            }

            var band = doc.Range(start, end);
            object? bits = band.EnhMetaFileBits;
            return TryBitsToPng(bits, outPath, scale);
        }
        catch
        {
            // Per-page band failed; fall back to the whole document EMF so the tile is still valid.
            try
            {
                object? bits = doc.Range().EnhMetaFileBits;
                return TryBitsToPng(bits, outPath, scale);
            }
            catch { return false; }
        }
    }

    static void Respond(object payload)
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }

    // Word's own PDF export — full document, or only a dirty page RANGE (From/To) so live
    // updates only pay Word's layout cost for the pages that changed.
    static int PdfExport(string docxPath, string outPdf, int from = 0, int to = 0)
    {
        Application? app = null;
        Document? doc = null;
        try
        {
            if (!File.Exists(docxPath)) return Fail($"docx not found: {docxPath}");
            File.Delete(outPdf);

            app = new Application();
            app.Visible = false;
            app.DisplayAlerts = 0;
            doc = app.Documents.Open(Path.GetFullPath(docxPath), false, true, false);
            doc.Repaginate();

            if (from > 0 && to >= from)
            {
                // wdExportRange.wdExportFromTo: pages From..To (1-based, inclusive) — live
                // updates only pay Word's layout cost for the pages that changed.
                doc.ExportAsFixedFormat(Path.GetFullPath(outPdf),
                    NetOffice.WordApi.Enums.WdExportFormat.wdExportFormatPDF,
                    false,
                    NetOffice.WordApi.Enums.WdExportOptimizeFor.wdExportOptimizeForPrint,
                    NetOffice.WordApi.Enums.WdExportRange.wdExportFromTo,
                    from, to,
                    NetOffice.WordApi.Enums.WdExportItem.wdExportDocumentContent,
                    true, true,
                    NetOffice.WordApi.Enums.WdExportCreateBookmarks.wdExportCreateHeadingBookmarks,
                    true, true, false);
            }
            else
            {
                doc.ExportAsFixedFormat(Path.GetFullPath(outPdf), NetOffice.WordApi.Enums.WdExportFormat.wdExportFormatPDF);
            }
            Console.WriteLine($"exported PDF -> {outPdf}");
            return File.Exists(outPdf) ? 0 : Fail("PDF export produced no file");
        }
        catch (Exception ex)
        {
            return Fail("PDF export failed: " + ex.Message);
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            try { app?.Quit(); } catch { }
            doc?.Dispose();
            app?.Dispose();
            CleanupWord();
        }
    }

    // Rasterize one PDF page with Windows.Data.Pdf (OS built-in, vector-crisp at any scale).
    static int PdfPage(string pdfPath, string pageArg, string outPng, string scaleArg)
    {
        try
        {
            if (!File.Exists(pdfPath)) return Fail($"pdf not found: {pdfPath}");
            int page = int.Parse(pageArg);
            double scale = double.Parse(scaleArg, System.Globalization.CultureInfo.InvariantCulture);
            File.Delete(outPng);

            // WinRT APIs need an STA-compatible async run; Main is STA already.
            var task = RenderPdfPageAsync(pdfPath, page, scale, outPng);
            task.GetAwaiter().GetResult();
            return File.Exists(outPng) ? 0 : Fail("pdfpage produced no PNG");
        }
        catch (Exception ex)
        {
            return Fail("pdfpage failed: " + ex.Message);
        }
    }

    static async System.Threading.Tasks.Task RenderPdfPageAsync(string pdfPath, int page, double scale, string outPng)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        if (page < 1 || page > (int)pdf.PageCount) throw new InvalidOperationException($"page {page} out of range (1..{pdf.PageCount})");
        var pdfPage = pdf.GetPage((uint)(page - 1));
        try
        {
            // Rasterize at scale x ~150 DPI letter width so vector text stays crisp; the OS PDF
            // renderer draws the page exactly as Word laid it out (header/border included).
            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, 1275 * scale)
            };
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream, options);
            using var ms = new MemoryStream();
            var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            File.WriteAllBytes(outPng, bytes);
        }
        finally
        {
            pdfPage.Dispose();
        }
    }

    static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }
}
