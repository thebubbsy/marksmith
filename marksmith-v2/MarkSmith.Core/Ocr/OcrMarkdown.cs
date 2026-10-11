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

    public static string FromPage(OcrPageResult page) =>
        string.Join("\n\n", Blocks(page).Select(b => b.Markdown)).Trim();

    /// <summary>The page's Markdown as blocks in reading order, each with the lines it came from.
    /// Joining their Markdown with blank lines gives exactly <see cref="FromPage"/>.</summary>
    public static IReadOnlyList<OcrMarkdownBlock> Blocks(OcrPageResult page)
    {
        var lines = page.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (lines.Count == 0) return Array.Empty<OcrMarkdownBlock>();
        float body = OcrLayout.Median(lines.Select(l => l.Height));
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

        foreach (var l in lines)
        {
            var text = l.Text.Trim();
            float x = Start(l);
            var (left, _) = Column(l);
            float gap = prev is null ? 0 : l.Y - (prev.Y + prev.Height);
            bool heading = l.Height > body * 1.35f && text.Length < 120 && !text.EndsWith('.');
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
                Open((l.Height > body * 1.9f ? "# " : "## ") + text, l, OcrBlockKind.Heading);
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
}
