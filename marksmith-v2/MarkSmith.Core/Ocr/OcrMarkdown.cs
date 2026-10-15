using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using MarkSmith.Services.Import;
using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>What a block of OCR Markdown is.</summary>
public enum OcrBlockKind { Heading, Paragraph, List, Picture, Diagram, ShapeMosaic, Table, KeyValue }

/// <summary>One Markdown block read off a page, and the OCR lines or visual bounds it was built
/// from (in page pixels), so a viewer can show where on the page each block came from.</summary>
public sealed record OcrMarkdownBlock(string Markdown, OcrBlockKind Kind, IReadOnlyList<OcrLine> Lines, SKRect? ExplicitBounds = null)
{
    public OcrMarkdownBlock(OcrBlockKind kind, string markdown, OcrNormRect normBounds, int imageWidth = 1000, int imageHeight = 1000)
        : this(markdown, kind, Array.Empty<OcrLine>(), new SKRect(normBounds.X * imageWidth, normBounds.Y * imageHeight, normBounds.Right * imageWidth, normBounds.Bottom * imageHeight)) { }

    /// <summary>The area of the page the block covers (explicit visual bounds or word bounds).</summary>
    public SKRect Bounds
    {
        get
        {
            if (ExplicitBounds is { IsEmpty: false } eb) return eb;
            var withWords = Lines.Where(l => l.Words.Count > 0).ToList();
            if (withWords.Count == 0) return SKRect.Empty;
            float left = withWords.Min(l => l.Words.Min(w => w.X));
            float right = withWords.Max(l => l.Words.Max(w => w.X + w.Width));
            float top = withWords.Min(l => Math.Min(l.Y, l.Words.Min(w => w.Y)));
            float bottom = withWords.Max(l => Math.Max(l.Y + l.Height, l.Words.Max(w => w.Y + w.Height)));
            return new SKRect(left, top, right, bottom);
        }
    }
}

/// <summary>
/// Turns OCR lines (any engine) into Markdown the way a reader sees the page: paragraphs where the
/// line spacing opens up or a line is indented, headings for lines set clearly larger than the
/// body, bullet and numbered lists, and words hyphenated across lines mended.
/// </summary>
public static class OcrMarkdown
{
    private static readonly Regex Bullet = new(@"^([•●▪◦‣∙·*\-–])\s+", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\d{1,3}[.)]|[a-z][.)])\s+", RegexOptions.Compiled);

    // Arrow-badge OCR artifacts produced by black/white "Go to" triangles in scanned forms.
    private static readonly Regex ArrowBadgeGarbage = new(
        @"(?:(?<=^|\s)(?:\|i[ZX2S5]\)|\|i[ZX2S5]\b|@i5V|gZ\)|Zl\)|iZ\)|\[iZ\))(?=\s|$))",
        RegexOptions.Compiled);

    // Checkbox OCR artifacts next to Yes/No or option labels.
    private static readonly Regex YesNoEmptyBox = new(
        @"\b(Yes|No)\s+(?:\|g|\[g|g\]|\[0|\|0|0\]|\[O|\|O|\[9|9\]|Z\)|g|\[\s*\]|\[_\]|\[[Il1|]\]|\|\]|\[\|)(?=\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex YesNoCheckedBox = new(
        @"\b(Yes|No)\s+(?:\[X|X\]|\[x|x\]|\[T|\[[vV]\])(?=\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex StandaloneEmptyBox = new(
        @"(?<=^|\s)(?:\|g|\[g|\[0|\|0|\[9|\[\s*\]|\[_\])(?=\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex StandaloneCheckedBox = new(
        @"(?<=^|\s)(?:\[X|\[T|\[[vV]\])(?=\s|$)",
        RegexOptions.Compiled);

    public static string FromPage(OcrPageResult page) =>
        string.Join("\n\n", Blocks(page).Select(b => b.Markdown)).Trim();

    /// <summary>The page's Markdown as blocks in reading order, each with the lines it came from.
    /// Joining their Markdown with blank lines gives exactly <see cref="FromPage"/>.</summary>
    public static IReadOnlyList<OcrMarkdownBlock> Blocks(OcrPageResult page)
    {
        var cleaned = new List<(OcrLine Line, string Text, float Height)>();
        foreach (var rawLine in page.Lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine.Text)) continue;
            var text = CleanLineText(rawLine.Text);
            if (string.IsNullOrWhiteSpace(text) || IsNoiseOrBarcodeLine(text)) continue;
            cleaned.Add((rawLine, text, EffectiveHeight(rawLine)));
        }
        if (cleaned.Count == 0) return Array.Empty<OcrMarkdownBlock>();

        var lines = cleaned.Select(c => c.Line).ToList();
        float body = OcrLayout.Median(cleaned.Select(c => c.Height));
        static float Start(OcrLine l) => l.Words.Count > 0 ? l.Words[0].X : 0;
        static float End(OcrLine l) => l.Words.Count > 0 ? l.Words[^1].X + l.Words[^1].Width : 0;

        // Each line is measured against its own column: the usual start and end of the lines
        // that start near it. (One page-wide margin made every line of a right-hand column look
        // indented, so each became a paragraph of its own.)
        (float Left, float Right) Column(OcrLine l)
        {
            float x = Start(l);
            var near = lines.Where(o => Math.Abs(Start(o) - x) < body * 4).ToList();
            var ends = near.Select(End).OrderBy(e => e).ToList();
            return (OcrLayout.Median(near.Select(Start)), ends[(int)(ends.Count * 0.9f)]);
        }

        // Output blocks; a list is one block whose items are separate lines. Each block also keeps
        // the lines it was read from and what kind of block it is (for the OCR viewer).
        var blocks = new List<List<string>>();
        var sources = new List<List<OcrLine>>();
        var kinds = new List<OcrBlockKind>();
        void Open(string first, OcrLine from, OcrBlockKind kind)
        {
            blocks.Add(new List<string> { first });
            sources.Add(new List<OcrLine> { from });
            kinds.Add(kind);
        }
        bool listOpen = false;
        float itemX = 0;
        OcrLine? prev = null;

        for (int idx = 0; idx < cleaned.Count; idx++)
        {
            var (l, text, lh) = cleaned[idx];

            // Check if a multi-column aligned table starts at idx.
            if (TryExtractTable(cleaned, idx, body, out var tableMarkdown, out var tableLines, out int consumed))
            {
                blocks.Add(new List<string> { tableMarkdown });
                sources.Add(tableLines);
                kinds.Add(OcrBlockKind.Table);
                listOpen = false;
                prev = tableLines[^1];
                idx += consumed - 1;
                continue;
            }

            float x = Start(l);
            var (left, _) = Column(l);
            float gap = prev is null ? 0 : l.Y - (prev.Y + prev.Height);
            bool heading = IsHeadingLine(text, lh, body);
            var bm = Bullet.Match(text);
            bool item = bm.Success || Numbered.IsMatch(text);
            bool prevEndedShort = prev is not null && prev.Text.TrimEnd().EndsWith('.') && prev.Words.Count > 0
                                  && End(prev) < Column(prev).Right - body * 3;
            // Back up the page: the top of the next column.
            bool newColumn = prev is not null && l.Y < prev.Y;
            bool newPara = prev is null || newColumn || gap > body * 0.75f || x - left > body * 0.8f || prevEndedShort;
            // A wrapped list item's next line hangs under the item's text, not its bullet.
            bool continuesItem = listOpen && !item && prev is not null && !newColumn && gap <= body * 0.75f && x > itemX + body * 0.3f;

            if (heading)
            {
                Open((lh > body * 1.9f ? "# " : "## ") + text, l, OcrBlockKind.Heading);
                listOpen = false;
            }
            else if (item)
            {
                var entry = bm.Success ? "- " + text[bm.Length..] : text;
                if (listOpen && gap <= body * 1.5f) { blocks[^1].Add(entry); sources[^1].Add(l); }
                else Open(entry, l, OcrBlockKind.List);
                listOpen = true;
                itemX = x;
            }
            else if (continuesItem || (listOpen && !newPara))
            {
                blocks[^1][^1] = PdfMarkdownImporter.JoinLines(blocks[^1][^1], text);
                sources[^1].Add(l);
            }
            else if (newPara || blocks.Count == 0 || listOpen)
            {
                Open(text, l, OcrBlockKind.Paragraph);
                listOpen = false;
            }
            else
            {
                blocks[^1][^1] = PdfMarkdownImporter.JoinLines(blocks[^1][^1], text);
                sources[^1].Add(l);
            }
            prev = l;
        }
        return blocks.Select((b, i) => new OcrMarkdownBlock(string.Join("\n", b), kinds[i], sources[i])).ToList();
    }

    private static float EffectiveHeight(OcrLine line)
    {
        if (line.Words.Count == 0) return line.Height;
        var alphaHeights = line.Words
            .Where(w => w.Height > 0 && w.Text.Any(char.IsLetter) && !w.Text.Contains('|') && !w.Text.Contains('['))
            .Select(w => w.Height)
            .ToList();
        if (alphaHeights.Count > 0)
            return OcrLayout.Median(alphaHeights);
        return line.Height;
    }

    private static bool IsHeadingLine(string text, float lineHeight, float bodyHeight)
    {
        if (text.Length < 3 || text.Length >= 95) return false;
        if (text.EndsWith('.') || text.EndsWith(':') || text.EndsWith(',') || text.EndsWith(';')) return false;
        if (text.Contains('[') || text.Contains(']') || text.Contains('~') || text.Contains('|')) return false;
        if (char.IsLower(text[0])) return false;

        int letters = text.Count(char.IsLetter);
        if (letters < 3) return false;

        // Form instructions and routing prompts are never headings even if set in larger/bold type.
        if (Regex.IsMatch(text, @"\b(Go to \d|Give details|if known|attach a|tick all|Please read)\b", RegexOptions.IgnoreCase))
            return false;

        return lineHeight > bodyHeight * 1.35f;
    }

    public static string CleanLineText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();

        // Remove arrow-badge OCR garbage tokens (e.g. |iZ), @i5V, gZ), Zl)).
        s = ArrowBadgeGarbage.Replace(s, " ");

        // Normalize Yes/No checkbox artifacts.
        s = YesNoEmptyBox.Replace(s, "$1 [ ]");
        s = YesNoCheckedBox.Replace(s, "$1 [x]");
        s = StandaloneEmptyBox.Replace(s, "[ ]");
        s = StandaloneCheckedBox.Replace(s, "[x]");

        // Repair common split/mangled form phrases and sentence-initial 'l' for 'I'.
        s = Regex.Replace(s, @"(?:^|(?<=[.?!]\s+))l([stfn])\b", "I$1");
        s = Regex.Replace(s, @"\bEx-partner\s+s\b", "Ex-partner's");
        s = Regex.Replace(s, @"\bCustomer\s+eference\s+umber\s*\(\s*nown\)", "Customer Reference Number (if known)");
        s = Regex.Replace(s, @"\bYouth\s*A(?:ii|ll)owance\b", "Youth Allowance");
        s = Regex.Replace(s, @"\b([Cc])ali(?=\s+\d)", "$1all");
        s = Regex.Replace(s, @"(?<=\s|^)[—\-_]{2,}[EIl1|](?=\s|$)", "");

        // Collapse multiple spaces.
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    public static bool IsNoiseOrBarcodeLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var nonSpace = text.Where(c => !char.IsWhiteSpace(c)).ToList();
        if (nonSpace.Count == 0) return true;

        // 1D vertical barcode lines read as runs of |, l, I, 1, !, [, ].
        int barChars = nonSpace.Count(c => c is '|' or 'l' or 'I' or '1' or '!' or '[' or ']');
        if (nonSpace.Count >= 5 && barChars >= nonSpace.Count * 0.65)
            return true;
        if (text.Contains("|||") || Regex.IsMatch(text, @"[|Il1!]{6,}"))
            return true;

        // Empty date/character comb boxes and border artifacts (e.g. "[i~ [iE", "[iE", "iii]", "---l").
        if (Regex.IsMatch(text, @"^[\[\]|iIeE1l~_\-\s]{1,14}$") && text != "[ ]" && text != "[x]")
            return true;

        // Tiny isolated noise fragments from logos/borders (e.g. "(D", "gbQ", "*", "—").
        if (nonSpace.Count <= 3 && text != "[ ]" && text != "[x]")
        {
            int lettersOrDigits = nonSpace.Count(char.IsLetterOrDigit);
            if (lettersOrDigits == 0) return true;
            if (text is "(D" or "gbQ" or "gZ)" or "Zl)" or "|g" or "[g" or "Z)" or "[9")
                return true;
        }

        return false;
    }

    private static bool TryExtractTable(
        IReadOnlyList<(OcrLine Line, string Text, float Height)> cleaned,
        int startIndex,
        float body,
        out string tableMarkdown,
        out List<OcrLine> tableLines,
        out int consumed)
    {
        tableMarkdown = string.Empty;
        tableLines = new List<OcrLine>();
        consumed = 0;

        if (startIndex + 1 >= cleaned.Count) return false;

        float colGapThreshold = Math.Max(body * 2.2f, 36f);
        var rows = new List<(OcrLine Line, List<(float X, string Text)> Cells)>();

        for (int i = startIndex; i < cleaned.Count; i++)
        {
            var (line, text, lh) = cleaned[i];
            if (IsHeadingLine(text, lh, body)) break;
            if (Bullet.IsMatch(text) || Numbered.IsMatch(text)) break;
            if (text.Contains("[ ]") || text.Contains("[x]")) break;

            if (rows.Count > 0)
            {
                var prevLine = rows[^1].Line;
                float vGap = line.Y - (prevLine.Y + prevLine.Height);
                if (line.Y < prevLine.Y || vGap > body * 1.6f) break;
            }

            var cells = SplitLineIntoTableCells(line, colGapThreshold);
            if (cells.Count < 2 || cells.Count > 6) break;

            if (rows.Count > 0)
            {
                var firstCells = rows[0].Cells;
                if (cells.Count != firstCells.Count) break;
                bool aligned = true;
                for (int c = 0; c < cells.Count; c++)
                {
                    if (Math.Abs(cells[c].X - firstCells[c].X) > body * 2.0f)
                    {
                        aligned = false;
                        break;
                    }
                }
                if (!aligned) break;
            }

            rows.Add((line, cells));
        }

        if (rows.Count < 2) return false;

        int colCount = rows[0].Cells.Count;
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", rows[0].Cells.Select(c => EscapePipe(c.Text)))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCount))).AppendLine(" |");
        for (int r = 1; r < rows.Count; r++)
        {
            sb.Append("| ").Append(string.Join(" | ", rows[r].Cells.Select(c => EscapePipe(c.Text)))).AppendLine(" |");
        }

        tableMarkdown = sb.ToString().TrimEnd();
        tableLines = rows.Select(r => r.Line).ToList();
        consumed = rows.Count;
        return true;
    }

    private static List<(float X, string Text)> SplitLineIntoTableCells(OcrLine line, float colGapThreshold)
    {
        var result = new List<(float X, string Text)>();
        if (line.Words.Count < 2) return result;

        var ordered = line.Words.OrderBy(w => w.X).ToList();
        float cellX = ordered[0].X;
        var current = new List<string> { CleanLineText(ordered[0].Text) };
        float prevRight = ordered[0].X + ordered[0].Width;

        for (int i = 1; i < ordered.Count; i++)
        {
            float gap = ordered[i].X - prevRight;
            if (gap >= colGapThreshold)
            {
                var cellText = string.Join(" ", current.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
                if (!string.IsNullOrEmpty(cellText))
                    result.Add((cellX, cellText));
                cellX = ordered[i].X;
                current.Clear();
            }
            current.Add(CleanLineText(ordered[i].Text));
            prevRight = Math.Max(prevRight, ordered[i].X + ordered[i].Width);
        }

        var lastText = string.Join(" ", current.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
        if (!string.IsNullOrEmpty(lastText))
            result.Add((cellX, lastText));

        return result;
    }

    private static string EscapePipe(string s) => s.Replace("|", "\\|");
}

