using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Ocr;
using MarkSmith.Ocr.MarkSmith;
using MarkSmith.Services;
using MarkSmith.Services.Import;
using MarkSmith.ViewModels;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MarkSmith.Core.Tests.Ocr;

/// <summary>How OCR'd lines become Markdown (any engine).</summary>
public class OcrMarkdownTests
{
    private readonly ITestOutputHelper _output;

    public OcrMarkdownTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // A line of words starting at x, 30 px tall, at row y, with words 60 px apart.
    private static OcrLine Line(string text, float x, float y)
    {
        var words = text.Split(' ').Select((w, i) => new OcrWord(w, x + i * 60, y, 55, 30)).ToList();
        return new OcrLine(text, words, y, 30);
    }

    [Fact]
    public void A_wrapped_list_item_stays_one_item()
    {
        var page = new OcrPageResult(new List<OcrLine>
        {
            Line("• First item that wraps", 100, 100),
            Line("onto a second line", 140, 140),
            Line("• Next item", 100, 180),
        }, 1000, 1000);
        Assert.Equal("- First item that wraps onto a second line\n- Next item", OcrMarkdown.FromPage(page));
    }

    [Fact]
    public void A_right_hand_column_reads_as_paragraphs_not_one_line_each()
    {
        var lines = new List<OcrLine>();
        for (int i = 0; i < 4; i++) lines.Add(Line("left column words here and more", 50, 100 + i * 40));
        for (int i = 0; i < 4; i++) lines.Add(Line("right column words here and more", 520, 100 + i * 40));
        var md = OcrMarkdown.FromPage(new OcrPageResult(lines, 1000, 1000));
        Assert.Equal(2, md.Split("\n\n").Length);
    }

    [Theory]
    [InlineData("Carerpayment", "Carer payment")]
    [InlineData("Disabilitysupportpension", "Disability support pension")]
    [InlineData("natureofcommitment", "nature of commitment")]
    [InlineData("HaveyoutoIdusaboutyourseparation", "Have you told us about your separation")]
    public void MsLexicon_SplitsMultiWordJoinedStrings_AndFixesInternalConfusables(string joined, string expected)
    {
        var lex = MsLexicon.LoadEmbedded()!;
        Assert.NotNull(lex);

        var chars = joined.Select((ch, i) => new ReadChar(
            ch.ToString(),
            0.95f,
            i * 12,
            i * 12 + 10,
            new[] { (ch.ToString(), 0.95f) })).ToList();

        var words = new List<string>();
        var current = chars;
        while (lex.SplitJoined(current, 2f) is { } split)
        {
            words.Add(string.Concat(split.First.Select(c => c.Text)));
            current = split.Second;
        }
        words.Add(string.Concat(current.Select(c => c.Text)));

        Assert.Equal(expected, string.Join(" ", words));
    }

    [Fact]
    public void OcrMarkdown_NormalizesCheckboxes_StripsArrowGarbage_AndFiltersBarcodes()
    {
        var page = new OcrPageResult(new List<OcrLine>
        {
            Line("l||||l||l||||l|l|ll|||l|ll", 40, 50),
            Line("(D", 60, 80),
            Line("No |g", 100, 120),
            Line("Yes [0 Go to 5 |iZ)", 100, 160),
            Line("Yes [X Go to 17 @i5V", 100, 200),
        }, 1000, 1000);

        var md = OcrMarkdown.FromPage(page);
        Assert.DoesNotContain("||||", md);
        Assert.DoesNotContain("(D", md);
        Assert.DoesNotContain("|iZ)", md);
        Assert.DoesNotContain("@i5V", md);
        Assert.Contains("No [ ]", md);
        Assert.Contains("Yes [ ] Go to 5", md);
        Assert.Contains("Yes [x] Go to 17", md);
    }

    [Fact]
    public void OcrMarkdown_TallNoiseGlyphOnLine_DoesNotPromoteBodyLineToHeading()
    {
        var bodyLine1 = Line("Regular body text line one", 100, 100);
        var bodyLine2 = Line("Regular body text line two", 100, 140);
        // Line 3 has normal 30px words plus one tall 65px bracket artifact that inflates Line.Height to 65px.
        var wordsWithTallNoise = new List<OcrWord>
        {
            new("No", 100, 180, 30, 30),
            new("[g", 140, 165, 25, 65),
            new("Go", 180, 180, 30, 30),
            new("to", 220, 180, 25, 30),
            new("17", 255, 180, 30, 30),
        };
        var noisyLine = new OcrLine("No [g Go to 17", wordsWithTallNoise, 165, 65);

        var page = new OcrPageResult(new List<OcrLine> { bodyLine1, bodyLine2, noisyLine }, 1000, 1000);
        var blocks = OcrMarkdown.Blocks(page);
        Assert.DoesNotContain(blocks, b => b.Kind == OcrBlockKind.Heading);
    }

    [Fact]
    public void OcrMarkdown_ReconstructsAlignedMultiColumnTable()
    {
        var row1 = new OcrLine("Payment Type Fortnightly Rate", new List<OcrWord>
        {
            new("Payment", 80, 100, 60, 24),
            new("Type", 148, 100, 45, 24),
            new("Fortnightly", 420, 100, 85, 24),
            new("Rate", 512, 100, 40, 24),
        }, 100, 24);

        var row2 = new OcrLine("Carer Payment $1,026.50", new List<OcrWord>
        {
            new("Carer", 80, 132, 48, 24),
            new("Payment", 135, 132, 65, 24),
            new("$1,026.50", 420, 132, 80, 24),
        }, 132, 24);

        var page = new OcrPageResult(new List<OcrLine> { row1, row2 }, 1000, 1000);
        var blocks = OcrMarkdown.Blocks(page);

        Assert.Single(blocks);
        Assert.Equal(OcrBlockKind.Table, blocks[0].Kind);
        Assert.Contains("| Payment Type | Fortnightly Rate |", blocks[0].Markdown);
        Assert.Contains("| Carer Payment | $1,026.50 |", blocks[0].Markdown);
    }

    [Fact]
    public void OcrVisualElements_ExtractsMonochromeCoatOfArmsAsPicture_WhenMediaDirectoryProvided()
    {
        using var bmp = new SKBitmap(800, 600);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
            // Draw a solid crest/logo graphic in the top-left header area (x: 60..220, y: 40..150)
            canvas.DrawCircle(110, 95, 32, paint);
            canvas.DrawRect(130, 65, 45, 55, paint);
            canvas.DrawCircle(185, 95, 28, paint);
        }

        // Suppose OCR hallucinates tiny noise fragments inside the crest
        var fakeNoiseLine = new OcrLine("(D gbQ", new List<OcrWord>
        {
            new("(D", 95, 80, 22, 18),
            new("gbQ", 140, 85, 30, 20),
        }, 80, 25);

        string tempMedia = Path.Combine(Path.GetTempPath(), "marksmith_ocr_pic_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var opts = new OcrVisualOptions
            {
                MediaDirectory = tempMedia,
                MediaLink = "form_media",
                PageNumber = 1,
                PreferShapeMosaicForPictures = false,
            };

            var res = OcrVisualElements.ExtractPage(
                bmp,
                new OcrPageResult(new List<OcrLine> { fakeNoiseLine }, bmp.Width, bmp.Height),
                opts);

            Assert.Contains(res.Blocks, v => v.Kind == OcrBlockKind.Picture && v.Markdown.Contains("![Picture from page 1]"));
            Assert.DoesNotContain(res.Blocks, v => v.Markdown.Contains("(D") || v.Markdown.Contains("gbQ"));
            Assert.True(res.PicturesSaved > 0);
            Assert.True(Directory.Exists(tempMedia) && Directory.GetFiles(tempMedia, "*.png").Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempMedia))
                Directory.Delete(tempMedia, recursive: true);
        }
    }

    [Fact]
    public void DetachFromOpenFile_WithImportedDocumentFolder_PreservesRelativeMediaResolution()
    {
        var vm = new MainViewModel();
        vm.DetachFromOpenFile(@"C:\Users\Tony\Downloads");

        Assert.Equal(@"C:\Users\Tony\Downloads", vm.DocumentFolder);

        // Plain DetachFromOpenFile() without folder still clears DocumentFolder to null
        vm.DetachFromOpenFile();
        Assert.Null(vm.DocumentFolder);
    }

    [Fact]
    public void RealWorld_FormPdf_Verification()
    {
        const string pdfPath = @"C:\Users\Tony\Downloads\form.pdf";
        if (!File.Exists(pdfPath))
        {
            _output.WriteLine("Skipping real-world form.pdf check because C:\\Users\\Tony\\Downloads\\form.pdf is not present.");
            return;
        }

        string tempMedia = Path.Combine(Path.GetTempPath(), "marksmith_form_verify_" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] twoPageSubset;
            using (var srcDoc = UglyToad.PdfPig.PdfDocument.Open(pdfPath))
            {
                var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                builder.AddPage(srcDoc, 1);
                if (srcDoc.NumberOfPages >= 3)
                    builder.AddPage(srcDoc, 3);
                twoPageSubset = builder.Build();
            }

            using var ms = new MemoryStream(twoPageSubset);
            var report = PdfMarkdownImporter.Import(ms, new PdfImportOptions
            {
                MediaDirectory = tempMedia,
                MediaLink = "form_media",
                OcrEngine = "marksmith",
            });

            _output.WriteLine($"Pages: {report.Pages}, OCR pages: {report.OcrPages}, Pictures: {report.Pictures}");
            foreach (var note in report.Notes)
                _output.WriteLine($"Note: {note}");
            _output.WriteLine("--- GENERATED MARKDOWN (PAGES 1 & 3) ---");
            _output.WriteLine(report.Markdown);

            Assert.True(report.Pictures > 0, "Expected at least one extracted picture (logo/crest) from form.pdf");
            Assert.Contains("![Picture from page", report.Markdown);
            Assert.Contains("Carer Payment", report.Markdown);
            Assert.Contains("Disability Support Pension", report.Markdown);
            Assert.Contains("Youth Allowance", report.Markdown);
            Assert.Contains("nature of commitment", report.Markdown);
            Assert.DoesNotContain("l||||l||l||||", report.Markdown);
            Assert.DoesNotContain("|iZ)", report.Markdown);
            Assert.DoesNotContain("@i5V", report.Markdown);
        }
        finally
        {
            if (Directory.Exists(tempMedia))
                Directory.Delete(tempMedia, recursive: true);
        }
    }
}

