using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using MarkSmith.Services.Import;

namespace MarkSmith.Ocr;

/// <summary>
/// Turns OCR lines (any engine) into Markdown the way a reader sees the page: paragraphs where the
/// line spacing opens up or a line is indented, headings for lines set clearly larger than the
/// body, bullet and numbered lists, and words hyphenated across lines mended.
/// </summary>
public static class OcrMarkdown
{
    private static readonly Regex Bullet = new(@"^([•●▪◦‣∙·*\-–])\s+", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\d{1,3}[.)]|[a-z][.)])\s+", RegexOptions.Compiled);

    public static string FromPage(OcrPageResult page)
    {
        var lines = page.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (lines.Count == 0) return "";
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

        // Output blocks; a list is one block whose items are separate lines.
        var blocks = new List<List<string>>();
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
                blocks.Add(new List<string> { (l.Height > body * 1.9f ? "# " : "## ") + text });
                listOpen = false;
            }
            else if (item)
            {
                var entry = bm.Success ? "- " + text[bm.Length..] : text;
                if (listOpen && gap <= body * 1.5f) blocks[^1].Add(entry);
                else blocks.Add(new List<string> { entry });
                listOpen = true;
                itemX = x;
            }
            else if (continuesItem || (listOpen && !newPara))
            {
                blocks[^1][^1] = PdfMarkdownImporter.JoinLines(blocks[^1][^1], text);
            }
            else if (newPara || blocks.Count == 0 || listOpen)
            {
                blocks.Add(new List<string> { text });
                listOpen = false;
            }
            else
            {
                blocks[^1][^1] = PdfMarkdownImporter.JoinLines(blocks[^1][^1], text);
            }
            prev = l;
        }
        return string.Join("\n\n", blocks.Select(b => string.Join("\n", b))).Trim();
    }
}
