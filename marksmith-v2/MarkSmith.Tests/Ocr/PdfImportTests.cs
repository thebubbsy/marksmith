using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkSmith.Ocr;
using MarkSmith.Ocr.Benchmark;
using MarkSmith.Services;
using MarkSmith.Services.Import;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace MarkSmith.Core.Tests.Ocr;

/// <summary>
/// PDF import used to regex-scan raw content streams: compressed pages (nearly every real PDF),
/// CID fonts and hex strings came back empty or as garbage, and scans gave "try OCR". These build
/// real PDFs with PdfPig's writer and check what the import makes of them.
/// </summary>
public class PdfImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ms_pdfimport_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Doc
    {
        public readonly PdfDocumentBuilder B = new();
        public readonly PdfDocumentBuilder.AddedFont Regular, Bold, Mono;
        public Doc()
        {
            Regular = B.AddStandard14Font(Standard14Font.Helvetica);
            Bold = B.AddStandard14Font(Standard14Font.HelveticaBold);
            Mono = B.AddStandard14Font(Standard14Font.Courier);
        }
        public PdfPageBuilder Page() => B.AddPage(595, 842); // A4 in points
    }

    private static double Y(int line, double top = 780, double step = 16) => top - line * step;

    private PdfImportReport Import(byte[] pdf, string? ocr = null) =>
        PdfMarkdownImporter.Import(new MemoryStream(pdf), new PdfImportOptions { MediaDirectory = Path.Combine(_dir, "doc_media"), MediaLink = "doc_media", OcrEngine = ocr ?? OcrEngines.MarkSmithId });

    [Fact]
    public void Headings_paragraphs_bold_lists_and_mended_hyphenation()
    {
        var d = new Doc();
        var p = d.Page();
        p.AddText("Quarterly Review", 22, new PdfPoint(60, Y(0)), d.Bold);
        p.AddText("Findings", 15, new PdfPoint(60, Y(2, 780, 18)), d.Bold);
        p.AddText("Most delays came from late approvals rather than from the work it-", 11, new PdfPoint(60, Y(4)), d.Regular);
        p.AddText("self, and teams with one owner per decision shipped sooner.", 11, new PdfPoint(60, Y(5)), d.Regular);
        var w = p.AddText("Important:", 11, new PdfPoint(60, Y(7)), d.Bold);
        p.AddText("read this first.", 11, new PdfPoint(60 + 60, Y(7)), d.Regular);
        p.AddText("• Name an owner", 11, new PdfPoint(60, Y(9)), d.Regular);
        p.AddText("• Keep questions short", 11, new PdfPoint(60, Y(10)), d.Regular);
        p.AddText("1. Plan", 11, new PdfPoint(60, Y(12)), d.Regular);
        p.AddText("2. Ship", 11, new PdfPoint(60, Y(13)), d.Regular);
        var md = Import(d.B.Build()).Markdown;

        Assert.Contains("# Quarterly Review", md);
        Assert.Contains("## Findings", md);
        Assert.Contains("from the work itself, and teams", md); // "it-" + "self" mended
        Assert.Contains("**Important**: read this first.", md); // punctuation stays outside the markers
        Assert.Contains("- Name an owner\n- Keep questions short", md);
        Assert.Contains("1. Plan\n2. Ship", md);
    }

    [Fact]
    public void Links_become_markdown_links()
    {
        var d = new Doc();
        var p = d.Page();
        p.AddText("See the", 11, new PdfPoint(60, 700), d.Regular);
        var letters = p.AddText("documentation", 11, new PdfPoint(102, 700), d.Regular);
        p.AddText("for details.", 11, new PdfPoint(180, 700), d.Regular);
        p.AddLink("https://example.com/docs", new PdfRectangle(100, 696, 178, 712));
        var md = Import(d.B.Build()).Markdown;
        Assert.Contains("[documentation](https://example.com/docs)", md);
    }

    [Fact]
    public void A_monospace_block_becomes_a_code_block_with_its_indent()
    {
        var d = new Doc();
        var p = d.Page();
        p.AddText("Example:", 11, new PdfPoint(60, 760), d.Regular);
        p.AddText("if (ready)", 10, new PdfPoint(60, 730), d.Mono);
        p.AddText("{", 10, new PdfPoint(60, 718), d.Mono);
        p.AddText("return 1;", 10, new PdfPoint(84, 706), d.Mono);
        p.AddText("}", 10, new PdfPoint(60, 694), d.Mono);
        var md = Import(d.B.Build()).Markdown;
        Assert.Contains("```\nif (ready)\n{\n    return 1;\n}\n```", md);
    }

    [Fact]
    public void Running_headers_footers_and_page_numbers_are_left_out()
    {
        var d = new Doc();
        for (int i = 1; i <= 4; i++)
        {
            var p = d.Page();
            p.AddText("Acme Corp — Confidential", 9, new PdfPoint(60, 815), d.Regular);
            p.AddText($"Body text of page {i} talks about something different each time.", 11, new PdfPoint(60, 700), d.Regular);
            p.AddText($"Page {i} of 4", 9, new PdfPoint(270, 25), d.Regular);
        }
        var md = Import(d.B.Build()).Markdown;
        Assert.DoesNotContain("Confidential", md);
        Assert.DoesNotContain("of 4", md);
        for (int i = 1; i <= 4; i++) Assert.Contains($"Body text of page {i}", md);
    }

    [Fact]
    public void Pictures_are_saved_beside_the_document_and_linked_where_they_sit()
    {
        var d = new Doc();
        var p = d.Page();
        p.AddText("Above the chart.", 11, new PdfPoint(60, 760), d.Regular);
        p.AddPng(Png(300, 150, SKColors.SteelBlue), new PdfRectangle(60, 500, 360, 650));
        p.AddText("Below the chart.", 11, new PdfPoint(60, 450), d.Regular);
        var report = Import(d.B.Build());
        Assert.Equal(1, report.Pictures);
        Assert.True(File.Exists(Path.Combine(_dir, "doc_media", "page1-picture1.png")));
        var md = report.Markdown;
        int above = md.IndexOf("Above the chart", StringComparison.Ordinal), pic = md.IndexOf("![", StringComparison.Ordinal), below = md.IndexOf("Below the chart", StringComparison.Ordinal);
        Assert.True(above < pic && pic < below, md);
        Assert.Contains("(doc_media/page1-picture1.png)", md);
    }

    [Fact]
    public void A_scanned_page_is_read_by_the_chosen_OCR_engine()
    {
        const string text = "Please call us if anything is still unclear about the corrected balance on your statement.";
        var d = new Doc();
        var p = d.Page();
        using var scan = OcrSynth.Render(new[] { new SynthBlock(text) }, new SynthStyle { Family = "Liberation Serif", PointSize = 12, WidthInches = 6 });
        p.AddPng(Encode(scan.Image), new PdfRectangle(0, 842 - scan.Image.Height * 72.0 / 300, scan.Image.Width * 72.0 / 300, 842));
        var report = Import(d.B.Build(), OcrEngines.MarkSmithId);

        Assert.Equal(1, report.OcrPages);
        Assert.Equal("MarkSmith OCR", report.OcrEngine);
        Assert.True(OcrAccuracy.CharacterAccuracy(scan.Text, report.Markdown) >= 0.9, report.Markdown);
    }

    [Fact]
    public void Text_pages_and_scanned_pages_mix_in_order()
    {
        var d = new Doc();
        d.Page().AddText("The typed first page.", 12, new PdfPoint(60, 760), d.Regular);
        using var scan = OcrSynth.Render(new[] { new SynthBlock("The scanned second page.") }, new SynthStyle { Family = "Liberation Sans", PointSize = 14, WidthInches = 6 });
        d.Page().AddPng(Encode(scan.Image), new PdfRectangle(0, 842 - scan.Image.Height * 72.0 / 300, scan.Image.Width * 72.0 / 300, 842));
        var report = Import(d.B.Build());
        Assert.Equal(2, report.Pages);
        Assert.Equal(1, report.OcrPages);
        var md = report.Markdown;
        int first = md.IndexOf("typed first page", StringComparison.Ordinal), second = md.IndexOf("second page", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, md);
    }

    [Fact]
    public void The_import_through_the_app_path_reports_ocr_and_keeps_marksmith_source_first()
    {
        var d = new Doc();
        d.Page().AddText("Plain words in a plain PDF.", 12, new PdfPoint(60, 760), d.Regular);
        var path = Path.Combine(_dir, "plain.pdf");
        File.WriteAllBytes(path, d.B.Build());
        var result = new ReverseImportService().ImportFromPdf(path);
        Assert.Equal(ImportTier.UniversalEngine, result.Tier);
        Assert.Contains("Plain words in a plain PDF.", result.Markdown);
        Assert.NotNull(result.Pdf);
    }

    [Fact]
    public void Picture_links_point_at_the_media_folder_wherever_it_is()
    {
        var doc = Path.Combine(_dir, "report.pdf");
        Assert.Equal("report_media", Plugins.PluginFileReader.MediaLinkFor(doc, Path.Combine(_dir, "report_media")));
        // The private fallback for a PDF in a temporary or read-only folder isn't beside the
        // document, so the link is its full path rather than a relative "report_media".
        var elsewhere = Path.Combine(Directory.CreateTempSubdirectory("ms_media_").FullName, "report-abc");
        Assert.Equal(Path.GetFullPath(elsewhere).Replace('\\', '/'), Plugins.PluginFileReader.MediaLinkFor(doc, elsewhere));
    }

    [Fact]
    public void Blank_pages_are_not_sent_to_OCR()
    {
        var d = new Doc();
        d.Page().AddText("Words on the first page.", 12, new PdfPoint(60, 760), d.Regular);
        d.Page(); // a blank separator page
        var report = Import(d.B.Build());
        Assert.Equal(2, report.Pages);
        Assert.Equal(0, report.OcrPages);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Code_keeps_its_brackets_and_text_keeps_its_symbols_literal()
    {
        var d = new Doc();
        var p = d.Page();
        p.AddText("Call", 11, new PdfPoint(60, 700), d.Regular);
        p.AddText("print()", 11, new PdfPoint(90, 700), d.Mono);
        p.AddText("to see a*b*c and <div> in [brackets].", 11, new PdfPoint(150, 700), d.Regular);
        var md = Import(d.B.Build()).Markdown;
        Assert.Contains("`print()`", md);
        Assert.Contains(@"a\*b\*c", md);
        Assert.Contains(@"\<div>", md);
        Assert.Contains(@"\[brackets\]", md);
    }

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        return Encode(bmp);
    }

    private static byte[] Encode(SKBitmap bmp)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
