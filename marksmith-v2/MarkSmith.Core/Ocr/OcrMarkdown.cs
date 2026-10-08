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
        float left = OcrLayout.Median(lines.Select(l => l.Words.Count > 0 ? l.Words[0].X : 0));

        // Output blocks; a list is one block whose items are separate lines.
        var blocks = new List<List<string>>();
        bool listOpen = false;
        OcrLine? prev = null;

        foreach (var l in lines)
        {
            var text = l.Text.Trim();
            float x = l.Words.Count > 0 ? l.Words[0].X : left;
            float gap = prev is null ? 0 : l.Y - (prev.Y + prev.Height);
            bool heading = l.Height > body * 1.35f && text.Length < 120 && !text.EndsWith('.');
            var bm = Bullet.Match(text);
            bool item = bm.Success || Numbered.IsMatch(text);
            bool prevEndedShort = prev is not null && prev.Text.TrimEnd().EndsWith('.') && prev.Words.Count > 0
                                  && prev.Words[^1].X + prev.Words[^1].Width < page.ImageWidth * 0.55f;
            bool newPara = prev is null || gap > body * 0.75f || x - left > body * 0.8f || prevEndedShort;

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
            }
            else if (listOpen && !newPara)
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
