using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Ocr;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace MarkSmith.Services.Import;

/// <summary>How a PDF is imported.</summary>
public sealed class PdfImportOptions
{
    /// <summary>Where pictures from the PDF are saved (null = pictures are left out).</summary>
    public string? MediaDirectory { get; init; }
    /// <summary>The Markdown link to <see cref="MediaDirectory"/> (relative to the document).</summary>
    public string? MediaLink { get; init; }
    /// <summary>OCR engine id (Settings › Import); null = Settings' choice.</summary>
    public string? OcrEngine { get; init; }
    /// <summary>Draws page N (1-based) as a bitmap, for pages whose text can't be read and that
    /// aren't a single scanned picture. The desktop app supplies Windows' own PDF renderer.</summary>
    public Func<int, SKBitmap?>? RenderPage { get; init; }
    public IProgress<string>? Progress { get; init; }
}

/// <summary>What an import produced and what it had to do.</summary>
public sealed record PdfImportReport(string Markdown, int Pages, int OcrPages, string? OcrEngine, int Pictures, IReadOnlyList<string> Notes);

/// <summary>
/// PDF → Markdown. Pages with real text go through PdfPig (Apache-2.0): words, blocks and reading
/// order, then headings (by size, or a bold line on its own), lists, bold/italic, links, code,
/// pictures, de-hyphenation, and running headers, footers and page numbers removed. Pages
/// without usable text (scans, or text in a font with no character map) are read by OCR.
/// </summary>
public static class PdfMarkdownImporter
{
    public static PdfImportReport Import(Stream pdf, PdfImportOptions? options = null)
    {
        options ??= new PdfImportOptions();
        var bytes = ReadAll(pdf);
        using var doc = PdfDocument.Open(bytes);
        var notes = new List<string>();
        var pages = new List<PageContent>();
        int pictures = 0;

        for (int n = 1; n <= doc.NumberOfPages; n++)
        {
            var page = doc.GetPage(n);
            options.Progress?.Report($"Reading page {n} of {doc.NumberOfPages}…");
            var content = new PageContent { Number = n, Height = page.Height, Width = page.Width };
            var letters = page.Letters;
            int usable = letters.Count(l => IsReal(l.Value));
            if (letters.Count > 0 && usable >= Math.Max(3, letters.Count * 0.7))
            {
                ReadTextPage(page, content);
            }
            else if (letters.Count > 0 || page.GetImages().Any() || page.ExperimentalAccess.Paths.Count() > 200)
            {
                // Text with no character map, a scan, or text drawn as outlines: read it by OCR.
                content.NeedsOcr = true;
            }
            // Otherwise the page is blank or holds only a few vector shapes: nothing to read.
            if (!content.NeedsOcr && options.MediaDirectory is not null)
                pictures += SavePictures(page, content, options, pictures);
            pages.Add(content);
        }

        // OCR the pages that need it, with the engine Settings picked. Each page's picture is
        // decoded (or rendered) only when its turn comes and freed straight after: a 200-page
        // scan held in memory at once runs to gigabytes.
        int ocrPages = 0;
        string? engineName = null;
        if (pages.Any(p => p.NeedsOcr))
        {
            var engine = OcrEngines.Create(options.OcrEngine ?? AppServices.Settings.Current.OcrEngine, out bool fellBack);
            engineName = engine.EngineName;
            if (fellBack) notes.Add($"The chosen OCR engine isn't available here, so {engine.EngineName} read the scanned pages.");
            foreach (var p in pages.Where(p => p.NeedsOcr))
            {
                SKBitmap? scan = null;
                try
                {
                    var page = doc.GetPage(p.Number);
                    scan = ScanImage(page) ?? options.RenderPage?.Invoke(p.Number);
                    if (scan is null)
                    {
                        notes.Add($"Page {p.Number} has no readable text and couldn't be turned into a picture to read.");
                        continue;
                    }
                    options.Progress?.Report($"Reading scanned page {p.Number} with {engine.EngineName}…");
                    var straight = OcrPreprocess.Deskew(scan, out _);
                    try { p.OcrMarkdown = OcrMarkdown.FromPage(engine.RecognizeAsync(straight).GetAwaiter().GetResult()); }
                    finally { if (!ReferenceEquals(straight, scan)) straight.Dispose(); }
                    ocrPages++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad page (an engine error, a broken image) costs that page, not the import.
                    notes.Add($"Page {p.Number} couldn't be read: {ex.Message}");
                }
                finally { scan?.Dispose(); }
            }
        }

        RemoveRunningHeadersAndFooters(pages);
        var markdown = Compose(pages);
        return new PdfImportReport(markdown, pages.Count, ocrPages, engineName, pictures, notes);
    }

    // ---- text pages ----

    private sealed class PageContent
    {
        public int Number;
        public double Width, Height;
        public bool NeedsOcr;
        public string? OcrMarkdown;
        public List<BlockContent> Blocks = new();
        /// <summary>Where body text starts on this page (the left margin of the text column).</summary>
        public double BodyLeft;
    }

    private sealed class BlockContent
    {
        public List<LineContent> Lines = new();
        public double Top;      // PDF units from the bottom; larger = higher on the page
        public string? Picture; // a picture placed here instead of text
    }

    private sealed class LineContent
    {
        public List<WordContent> Words = new();
        public double Size, Top, Bottom, Left;
        public bool Drop;
        public string Text => string.Join(" ", Words.Select(w => w.Text));
    }

    private sealed record WordContent(string Text, bool Bold, bool Italic, bool Mono, string? Link, double Left);

    private static void ReadTextPage(Page page, PageContent content)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return;
        var links = page.GetHyperlinks().Where(h => !string.IsNullOrWhiteSpace(h.Uri)).ToList();
        // Monospace is a property of the font: decide it once per font from the words long enough
        // to tell (evenly spaced vs clearly not), then every word in that font follows.
        var monoFonts = words.GroupBy(w => w.FontName ?? "")
            .Where(g => { int even = g.Count(EvenlySpaced), uneven = g.Count(Uneven); return even > 0 && even >= uneven * 2; })
            .Select(g => g.Key).ToHashSet();
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();
        foreach (var block in ordered)
        {
            var bc = new BlockContent { Top = block.BoundingBox.Top };
            foreach (var line in block.TextLines)
            {
                var lc = new LineContent { Top = line.BoundingBox.Top, Bottom = line.BoundingBox.Bottom, Left = line.BoundingBox.Left };
                var sizes = line.Words.SelectMany(w => w.Letters).Select(l => l.PointSize > 0.5 ? l.PointSize : l.GlyphRectangle.Height).OrderBy(x => x).ToList();
                lc.Size = sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
                foreach (var w in line.Words)
                {
                    var font = (w.FontName ?? "").ToLowerInvariant();
                    var link = links.FirstOrDefault(h => Inside(w.BoundingBox.Centroid, h.Bounds))?.Uri;
                    lc.Words.Add(new WordContent(Clean(w.Text),
                        Bold: Regex.IsMatch(font, "bold|black|heavy|semibold|demi"),
                        Italic: Regex.IsMatch(font, "italic|oblique"),
                        Mono: Regex.IsMatch(font, "mono|courier|consol|menlo|code") || monoFonts.Contains(w.FontName ?? ""),
                        Link: link, Left: w.BoundingBox.Left));
                }
                if (lc.Words.Count > 0) bc.Lines.Add(lc);
            }
            if (bc.Lines.Count > 0) content.Blocks.Add(bc);
        }
    }

    /// <summary>
    /// Monospace by measurement, for fonts whose name says nothing (Chromium writes code fonts as
    /// "Type3"): a word of at least three different characters, including both narrow and wide
    /// ones, whose letters all advance by the same width.
    /// </summary>
    private static bool EvenlySpaced(Word w)
    {
        var letters = w.Letters;
        if (letters.Count < 4 || letters.Select(l => l.Value).Distinct().Count() < 3) return false;
        bool mixed = letters.Any(l => "iljtfr.,:;'!|".Contains(l.Value)) && letters.Any(l => "mwMWOQDGH@%".Contains(l.Value) || char.IsUpper(l.Value.FirstOrDefault()));
        if (!mixed) return false;
        var widths = letters.Select(l => l.Width).Where(x => x > 0).ToList();
        if (widths.Count < 4) return false;
        double mean = widths.Average();
        return widths.All(x => Math.Abs(x - mean) < mean * 0.04);
    }

    /// <summary>A word that shows its font is proportional: narrow and wide letters of clearly
    /// different widths.</summary>
    private static bool Uneven(Word w)
    {
        var narrow = w.Letters.Where(l => "iljtfr".Contains(l.Value) && l.Width > 0).Select(l => l.Width).ToList();
        var wide = w.Letters.Where(l => "mwMW".Contains(l.Value) && l.Width > 0).Select(l => l.Width).ToList();
        return narrow.Count > 0 && wide.Count > 0 && wide.Average() > narrow.Average() * 1.3;
    }

    private static bool Inside(UglyToad.PdfPig.Core.PdfPoint p, UglyToad.PdfPig.Core.PdfRectangle r) =>
        p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;

    private static bool IsReal(string v) =>
        !string.IsNullOrEmpty(v) && v.All(c => c != '�' && !(c >= '' && c <= '') && !char.IsControl(c));

    private static string Clean(string s) => s.Replace("ﬁ", "fi").Replace("ﬂ", "fl").Replace("ﬀ", "ff").Replace("ﬃ", "ffi").Replace("ﬄ", "ffl").Replace(' ', ' ');

    /// <summary>The page's scan: a single picture covering most of it (what scanners write).</summary>
    private static SKBitmap? ScanImage(Page page)
    {
        IPdfImage? best = null;
        double bestArea = 0;
        foreach (var img in page.GetImages())
        {
            double area = img.Bounds.Width * img.Bounds.Height;
            if (area > bestArea) { bestArea = area; best = img; }
        }
        // The page has no usable text, so its biggest picture (a scan, a photo of a page) is what
        // there is to read, unless it's a small logo or icon.
        if (best is null || bestArea < page.Width * page.Height * 0.05 || best.WidthInSamples < 200) return null;
        var bmp = Decode(best);
        // A page turned with /Rotate shows its scan turned the same way.
        int turn = ((page.Rotation.Value % 360) + 360) % 360;
        if (bmp is null || turn == 0) return bmp;
        var turned = OcrGeometry.RotateQuarter(bmp, turn);
        bmp.Dispose();
        return turned;
    }

    private static SKBitmap? Decode(IPdfImage img)
    {
        try
        {
            if (img.TryGetPng(out var png)) return SKBitmap.Decode(png);
            // JPEG (DCTDecode) data is a complete JPEG file.
            var raw = img.RawBytes.ToArray();
            if (raw.Length > 2 && raw[0] == 0xFF && raw[1] == 0xD8) return SKBitmap.Decode(raw);
        }
        catch { /* undecodable picture */ }
        return null;
    }

    private static int SavePictures(Page page, PageContent content, PdfImportOptions options, int already)
    {
        int saved = 0;
        foreach (var img in page.GetImages())
        {
            double area = img.Bounds.Width * img.Bounds.Height;
            // Specks and rules aren't pictures; a full-page picture behind real text is a background.
            if (img.WidthInSamples < 24 || img.HeightInSamples < 24 || area < page.Width * page.Height * 0.004) continue;
            if (area > page.Width * page.Height * 0.85) continue;
            using var bmp = Decode(img);
            if (bmp is null) continue;
            Directory.CreateDirectory(options.MediaDirectory!);
            var name = $"page{page.Number}-picture{already + saved + 1}.png";
            using (var f = File.Create(Path.Combine(options.MediaDirectory!, name)))
                bmp.Encode(SKEncodedImageFormat.Png, 100).SaveTo(f);
            var link = (options.MediaLink ?? Path.GetFileName(options.MediaDirectory!)) + "/" + name;
            content.Blocks.Add(new BlockContent { Top = img.Bounds.Top, Picture = $"![Picture from page {page.Number}]({link.Replace(" ", "%20")})" });
            saved++;
        }
        // Pictures go where they sit on the page, between the text blocks above and below them.
        var text = content.Blocks.Where(b => b.Picture is null).ToList();
        var pics = content.Blocks.Where(b => b.Picture is not null).OrderByDescending(b => b.Top).ToList();
        foreach (var pic in pics)
        {
            content.Blocks.Remove(pic);
            int at = content.Blocks.FindIndex(b => b.Picture is null && b.Top < pic.Top);
            if (at < 0) content.Blocks.Add(pic); else content.Blocks.Insert(at, pic);
        }
        return saved;
    }

    // ---- running headers and footers ----

    private static void RemoveRunningHeadersAndFooters(List<PageContent> pages)
    {
        var textPages = pages.Where(p => !p.NeedsOcr && p.Blocks.Count > 0).ToList();
        string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"\d+", "#").Trim();
        bool InMargin(LineContent l, PageContent p) => l.Top > p.Height * 0.92 || l.Bottom < p.Height * 0.08;
        var counts = new Dictionary<string, int>();
        foreach (var p in textPages)
            foreach (var key in p.Blocks.SelectMany(b => b.Lines).Where(l => InMargin(l, p)).Select(l => Key(l.Text)).Distinct())
                counts[key] = counts.GetValueOrDefault(key) + 1;
        int repeatAt = Math.Max(3, (int)Math.Ceiling(textPages.Count * 0.5));
        foreach (var p in textPages)
            foreach (var l in p.Blocks.SelectMany(b => b.Lines))
            {
                if (!InMargin(l, p)) continue;
                var text = l.Text.Trim();
                bool pageNumber = Regex.IsMatch(text, @"^(page\s*)?\d+(\s*(of|/)\s*\d+)?$|^[-–—]\s*\d+\s*[-–—]$", RegexOptions.IgnoreCase);
                if (pageNumber || (textPages.Count >= 3 && counts.GetValueOrDefault(Key(text)) >= repeatAt)) l.Drop = true;
            }
    }

    // ---- Markdown ----

    private static readonly Regex Bullet = new(@"^([•●▪◦‣∙·\-–*])\s+", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\(?\d{1,3}[.)]|\(?[a-z][.)])\s+", RegexOptions.Compiled);

    private static string Compose(List<PageContent> pages)
    {
        var lines = pages.SelectMany(p => p.Blocks).SelectMany(b => b.Lines).Where(l => !l.Drop).ToList();
        // Body size: the size most of the text is set in, by character count.
        var bySize = lines.GroupBy(l => Math.Round(l.Size * 2) / 2).Select(g => (Size: g.Key, Chars: g.Sum(l => l.Text.Length))).ToList();
        double body = bySize.Count == 0 ? 10 : bySize.OrderByDescending(g => g.Chars).First().Size;
        // Heading levels rank the sizes of blocks that look like headings (short, few lines, no
        // closing full stop), not every size on the pages: big lettering inside a diagram or a
        // single drop cap used to take a level and push real headings out.
        foreach (var p in pages.Where(p => !p.NeedsOcr))
        {
            // The text column's left edge: the left most body-size characters start at.
            var lefts = p.Blocks.SelectMany(b => b.Lines).Where(l => !l.Drop && Math.Abs(l.Size - body) < body * 0.1)
                .GroupBy(l => Math.Round(l.Left / 4) * 4).OrderByDescending(g => g.Sum(l => l.Text.Length)).FirstOrDefault();
            p.BodyLeft = lefts?.Key ?? 0;
        }
        var headingSizes = pages.Where(p => !p.NeedsOcr).SelectMany(p => p.Blocks.Select(b => (p, b)))
            .Where(t => t.b.Picture is null && LooksLikeHeading(t.b.Lines.Where(l => !l.Drop).ToList(), t.p))
            .Select(t => Math.Round(t.b.Lines.Average(l => l.Size) * 2) / 2)
            .Where(sz => sz >= body * 1.15).Distinct().OrderByDescending(sz => sz).ToList();

        var sb = new StringBuilder();
        foreach (var p in pages)
        {
            if (p.NeedsOcr)
            {
                if (!string.IsNullOrWhiteSpace(p.OcrMarkdown)) Append(sb, p.OcrMarkdown!);
                continue;
            }
            // Tables first (their cells can be in a code font, and must not be merged as code), then
            // code blocks among the rest, then everything in order.
            var tableAt = new Dictionary<BlockContent, string>();
            var inTable = new HashSet<BlockContent>();
            for (int i = 0; i < p.Blocks.Count; i++)
            {
                var b = p.Blocks[i];
                int used;
                string table;
                if (TryTable(p.Blocks, i, null, out table, out used)) { }
                // A header row written as one line (LibreOffice, Word) above a table whose columns its words line up with.
                else if (b.Picture is null && b.Lines.Count(l => !l.Drop) == 1 && TryTable(p.Blocks, i + 1, b.Lines.First(l => !l.Drop), out table, out used)) used++;
                else continue;
                tableAt[b] = table;
                for (int k = i; k < i + used; k++) inTable.Add(p.Blocks[k]);
                i += used - 1;
            }
            MergeCodeBlocks(p.Blocks, inTable);
            foreach (var b in p.Blocks)
            {
                if (tableAt.TryGetValue(b, out var table)) { Append(sb, table); continue; }
                if (inTable.Contains(b)) continue;
                if (b.Picture is not null) { Append(sb, b.Picture); continue; }
                var kept = b.Lines.Where(l => !l.Drop).ToList();
                if (kept.Count == 0) continue;
                Append(sb, BlockMarkdown(kept, body, headingSizes, p));
            }
        }
        return sb.ToString().Trim() + "\n";
    }

    /// <summary>
    /// A table: from block <paramref name="start"/>, a run of single-line cells (the layout
    /// analysis makes each cell its own block) that line up in at least two rows of at least two
    /// columns, with the same columns in every row. Written as a Markdown table, first row as header.
    /// </summary>
    private static bool TryTable(List<BlockContent> blocks, int start, LineContent? headerLine, out string table, out int used)
    {
        table = ""; used = 0;
        if (start >= blocks.Count) return false;
        var cells = new List<(BlockContent B, double Top, double Left)>();
        for (int i = start; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var kept = b.Lines.Where(l => !l.Drop).ToList();
            if (b.Picture is not null || kept.Count != 1 || kept[0].Text.Length > 60) break;
            // A row is cells level on their centre line: cells in different fonts have different
            // glyph boxes, so their tops can sit several points apart on the same row.
            cells.Add((b, (kept[0].Top + kept[0].Bottom) / 2, kept[0].Left));
        }
        if (cells.Count < 4) return false;

        // Rows: cells whose tops are level (within half a line).
        // The font size, not the glyph boxes' height (which can be well under it), is the line height.
        double lineH = cells.Average(c => Math.Max(c.B.Lines[0].Size, c.B.Lines[0].Top - c.B.Lines[0].Bottom));
        var rows = new List<List<(BlockContent B, double Top, double Left)>>();
        // Top to bottom, a new row starts where the next cell is more than a line below the row's
        // first cell; within a row, cells drift a few points (vertically centred cell content).
        foreach (var c in cells.OrderByDescending(c => c.Top))
        {
            if (rows.Count > 0 && rows[^1][0].Top - c.Top < lineH * 0.95) rows[^1].Add(c);
            else rows.Add(new List<(BlockContent, double, double)> { c });
        }
        // Keep the leading run of rows that agree on the columns.
        var first = rows.OrderByDescending(r => r[0].Top).ToList();
        int cols = first[0].Count;
        if (cols < 2) return false;
        var lefts = first[0].OrderBy(c => c.Left).Select(c => c.Left).ToList();
        var tableRows = new List<List<(BlockContent B, double Top, double Left)>>();
        foreach (var r in first)
        {
            var sorted = r.OrderBy(c => c.Left).ToList();
            if (sorted.Count != cols || sorted.Select((c, k) => Math.Abs(c.Left - lefts[k])).Any(d => d > Math.Max(12, lineH * 1.5))) break;
            tableRows.Add(sorted);
        }
        if (tableRows.Count < (headerLine is null ? 2 : 1)) return false;

        static string Clean(string s) => s.Replace("|", "\\|").Replace("**", "");
        static string Cell(BlockContent b) => Clean(Inline(b.Lines[0].Words));
        var rowsText = tableRows.Select(r => r.Select(c => Cell(c.B)).ToList()).ToList();
        if (headerLine is not null)
        {
            // The header's words go to the column whose left edge they are at or right of.
            var header = Enumerable.Range(0, cols).Select(_ => new List<string>()).ToList();
            foreach (var w in headerLine.Words)
            {
                int col = 0;
                for (int k = 0; k < cols; k++) if (w.Left >= lefts[k] - Math.Max(12, lineH)) col = k;
                header[col].Add(w.Text);
            }
            if (header.Any(h => h.Count == 0)) return false; // its words don't line up: not this table's header
            rowsText.Insert(0, header.Select(h => Clean(string.Join(" ", h))).ToList());
        }
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", rowsText[0])).Append(" |\n");
        sb.Append("|").Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
        foreach (var r in rowsText.Skip(1))
            sb.Append("| ").Append(string.Join(" | ", r)).Append(" |\n");
        table = sb.ToString().TrimEnd();
        // The cells used are exactly those in the table rows; they were consecutive blocks.
        var usedSet = tableRows.SelectMany(r => r.Select(c => c.B)).ToHashSet();
        used = 0;
        for (int i = start; i < blocks.Count && usedSet.Contains(blocks[i]); i++) used++;
        if (used != usedSet.Count) { table = ""; used = 0; return false; }
        return true;
    }

    /// <summary>Code set in a monospace font is one block even where the layout analysis split
    /// it at an indent: neighbouring all-monospace blocks with no more than a line's gap merge.</summary>
    private static void MergeCodeBlocks(List<BlockContent> blocks, HashSet<BlockContent> tableCells)
    {
        bool IsMono(BlockContent b) => b.Picture is null && !tableCells.Contains(b) && b.Lines.Count > 0 && b.Lines.SelectMany(l => l.Words).All(w => w.Mono);
        for (int i = 0; i + 1 < blocks.Count; i++)
        {
            var a = blocks[i]; var b = blocks[i + 1];
            if (!IsMono(a) || !IsMono(b)) continue;
            // Some fonts (Chromium's Type3 code fonts) report no usable size; glyph boxes are about
            // three quarters of a line, so they stand in for it.
            double lineH = a.Lines.Average(l => Math.Max(l.Size, (l.Top - l.Bottom) * 1.35));
            double gap = a.Lines.Min(l => l.Bottom) - b.Lines.Max(l => l.Top);
            // Code often has a blank line in it (after the imports): one is allowed. Blocks side by
            // side (overlapping vertically) are table cells in a code font, not lines of code.
            if (gap > lineH * 2.8 || gap < -lineH * 0.3) continue;
            if (b.Lines.Min(l => l.Left) < a.Lines.Min(l => l.Left) - lineH) continue;
            a.Lines.AddRange(b.Lines);
            a.Lines.Sort((x, y) => y.Top.CompareTo(x.Top));
            blocks.RemoveAt(i + 1);
            i--;
        }
    }

    private static void Append(StringBuilder sb, string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return;
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append(chunk.Trim());
    }

    /// <summary>
    /// Short, a line or three, no closing full stop, and where headings go: at the text column's
    /// left edge or centred on the page. Labels inside a diagram or a table are none of those.
    /// </summary>
    private static bool LooksLikeHeading(List<LineContent> lines, PageContent page)
    {
        if (lines.Count is 0 or > 3 || lines.Sum(l => l.Text.Length) >= 160 || lines[^1].Text.TrimEnd().EndsWith('.')) return false;
        double left = lines.Min(l => l.Left);
        bool atMargin = Math.Abs(left - page.BodyLeft) < page.Width * 0.04 || left < page.BodyLeft;
        double width = lines.Max(l => l.Words.Count == 0 ? l.Left : l.Words[^1].Left) - left;
        bool centred = Math.Abs(left + width / 2 - page.Width / 2) < page.Width * 0.08;
        return atMargin || centred;
    }

    private static string BlockMarkdown(List<LineContent> lines, double body, List<double> headingSizes, PageContent page)
    {
        double size = lines.Average(l => l.Size);
        // Headings: set larger than the body (level by size rank, at most 4), or a short bold line
        // on its own at body size (the next level down).
        if (LooksLikeHeading(lines, page))
        {
            double rounded = Math.Round(size * 2) / 2;
            int rank = headingSizes.FindIndex(s => Math.Abs(s - rounded) < 0.6);
            bool allBold = lines.SelectMany(l => l.Words).All(w => w.Bold);
            string text = string.Join(" ", lines.Select(l => l.Text)).Trim();
            if (rank >= 0) return new string('#', Math.Min(4, rank + 1)) + " " + text;
            if (allBold && lines.Count == 1 && text.Length < 90 && Math.Abs(size - body) < body * 0.15)
                return new string('#', Math.Min(4, headingSizes.Count + 1)) + " " + text;
        }

        // Code: a block set entirely in a monospace font keeps its lines and indentation.
        if ((lines.Count >= 2 || lines[0].Words.Count >= 3) && lines.SelectMany(l => l.Words).All(w => w.Mono))
        {
            double left = lines.Min(l => l.Left);
            double charWidth = Math.Max(1, size * 0.6);
            // Blank lines: a step from one line to the next well over the block's usual line pitch.
            var pitches = lines.Zip(lines.Skip(1), (u, v) => u.Top - v.Top).Where(d => d > 0).OrderBy(d => d).ToList();
            double pitch = pitches.Count == 0 ? double.MaxValue : pitches[pitches.Count / 2];
            var code = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0 && lines[i - 1].Top - lines[i].Top > pitch * 1.6) code.Add("");
                code.Add(new string(' ', (int)Math.Round((lines[i].Left - left) / charWidth)) + lines[i].Text);
            }
            return "```\n" + string.Join("\n", code) + "\n```";
        }

        // Lists: each bullet or number starts an item; other lines continue the item above.
        if (lines.Any(l => Bullet.IsMatch(l.Text) || Numbered.IsMatch(l.Text)))
        {
            var items = new List<string>();
            foreach (var l in lines)
            {
                var t = Inline(l.Words);
                var bm = Bullet.Match(t);
                var nm = Numbered.Match(t);
                if (bm.Success) items.Add("- " + t[bm.Length..]);
                else if (nm.Success) items.Add(nm.Groups[1].Value.Trim('(', ')').TrimEnd('.', ')') + ". " + t[nm.Length..]);
                else if (items.Count > 0) items[^1] = JoinLines(items[^1], t);
                else items.Add(t);
            }
            return string.Join("\n", items);
        }

        // A paragraph: lines joined, broken words mended.
        var para = "";
        foreach (var l in lines) para = para.Length == 0 ? Inline(l.Words) : JoinLines(para, Inline(l.Words));
        return para;
    }

    /// <summary>Joins two lines of a paragraph, mending a word hyphenated across the break.</summary>
    public static string JoinLines(string first, string second)
    {
        if (first.EndsWith('-') && first.Length > 1 && char.IsLetter(first[^2]) && second.Length > 0 && char.IsLower(second[0]))
            return first[..^1] + second;
        return first + " " + second;
    }

    /// <summary>A line's words with **bold**, *italic*, `code` and [links](url) runs marked.</summary>
    private static string Inline(List<WordContent> words)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < words.Count)
        {
            var w = words[i];
            int j = i;
            while (j + 1 < words.Count && words[j + 1].Bold == w.Bold && words[j + 1].Italic == w.Italic && words[j + 1].Mono == w.Mono && words[j + 1].Link == w.Link) j++;
            var run = string.Join(" ", words.Skip(i).Take(j - i + 1).Select(x => x.Text));
            // Keep punctuation outside the markers so "**bold**," renders. In code only a
            // sentence's own comma or full stop is outside: print() and f(x); keep theirs.
            var m = Regex.Match(run, w.Mono ? @"^(.*?)([.,]?)$" : @"^(.*?)([.,;:!?)]*)$", RegexOptions.Singleline);
            string core = m.Groups[1].Value, tail = m.Groups[2].Value;
            if (!w.Mono) core = EscapeMarkdown(core);
            string marked = core;
            if (w.Mono && !w.Bold) marked = "`" + core + "`";
            else
            {
                if (w.Bold) marked = "**" + marked + "**";
                if (w.Italic) marked = "*" + marked + "*";
            }
            if (w.Link is not null && !core.Equals(w.Link, StringComparison.OrdinalIgnoreCase)) marked = $"[{marked}]({w.Link})";
            if (core.Length == 0) marked = "";
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(marked).Append(tail);
            i = j + 1;
        }
        return sb.ToString();
    }

    private static readonly Regex MarkdownSpecial = new(@"([\\`*_\[\]<])", RegexOptions.Compiled);

    /// <summary>
    /// Printed text is text: a literal *, _, [, &lt; or ` in the PDF would otherwise turn into
    /// emphasis, a link or HTML. Web and mail addresses are left as written so they stay links.
    /// </summary>
    private static string EscapeMarkdown(string text) =>
        string.Join(" ", text.Split(' ').Select(t =>
            Regex.IsMatch(t, @"^(https?://|www\.)|@[\w-]+\.", RegexOptions.IgnoreCase) ? t : MarkdownSpecial.Replace(t, @"\$1")));

    private static byte[] ReadAll(Stream s)
    {
        if (s is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == ms.Length) return ms.ToArray();
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }
}
