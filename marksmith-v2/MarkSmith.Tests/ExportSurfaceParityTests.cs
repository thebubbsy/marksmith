using System.IO.Compression;
using System.Text.RegularExpressions;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Cover for exporters that produced a file but not a usable one.
///
/// EPUB referenced local images it never embedded, so every image was broken in every reader and
/// the package failed spec validation. PPTX emitted the Markdown *syntax* onto slides — link and
/// image markup, whole pipe tables, "&gt;" quote markers, "$…$" math — and stripped emphasis
/// characters from inside fenced code, turning "x * 2" into "x  2".
/// </summary>
public class ExportSurfaceParityTests
{
    private const string Doc = """
        # Deck Title

        Intro with a [link](https://example.com) and an image ![Alt text](docs/images/logo.png).

        ## Details

        - [ ] Unchecked
        - [x] Checked

        | Left | Right |
        | :--- | ----: |
        | a | 1 |

        ---

        > Quoted line

        Inline $a^2 + b^2 = c^2$ math.

        ```python
        y = x * 2
        ```
        """;

    private static string Temp(string ext) =>
        Path.Combine(Path.GetTempPath(), $"ms-surface-{Guid.NewGuid():N}{ext}");

    // ---- EPUB ---------------------------------------------------------------------------------

    /// <summary>A real 1x1 PNG on disk, so the test does not depend on the repo's layout.</summary>
    private static string WriteTempPng()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var path = Path.Combine(Path.GetTempPath(), $"ms-img-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    [Fact]
    public void Epub_Embeds_Local_Images_And_Declares_Them()
    {
        var path = Temp(".epub");
        var img = WriteTempPng();
        var doc = "# Deck Title\n\nAn image ![Alt text](" + img.Replace("\\", "/") + ").\n";
        try
        {
            new EpubExportService().ExportAsync(doc, path, new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);

            var images = zip.Entries.Where(e => e.FullName.StartsWith("OEBPS/images/")).ToList();
            Assert.NotEmpty(images);

            using var opfReader = new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open());
            var opf = opfReader.ReadToEnd();
            foreach (var entry in images)
            {
                var href = entry.FullName["OEBPS/".Length..];
                Assert.Contains($"href=\"{href}\"", opf);   // unmanifested = invalid package
            }

            using var chReader = new StreamReader(zip.GetEntry("OEBPS/ch001.xhtml")!.Open());
            var chapter = chReader.ReadToEnd();
            Assert.DoesNotContain(Path.GetFileName(img), chapter);   // src must be rewritten
            Assert.Contains("images/", chapter);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(img)) File.Delete(img);
        }
    }

    [Fact]
    public void Epub_Leaves_Remote_Images_Alone()
    {
        var path = Temp(".epub");
        try
        {
            new EpubExportService()
                .ExportAsync("# T\n\n![x](https://example.com/a.png)\n", path, new AppSettings())
                .GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("OEBPS/ch001.xhtml")!.Open());
            Assert.Contains("https://example.com/a.png", reader.ReadToEnd());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- PPTX ---------------------------------------------------------------------------------

    private static string SlideText(string pptxPath)
    {
        using var zip = ZipFile.OpenRead(pptxPath);
        var sb = new System.Text.StringBuilder();
        foreach (var e in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")))
        {
            using var r = new StreamReader(e.Open());
            foreach (Match m in Regex.Matches(r.ReadToEnd(), "<a:t>([^<]*)</a:t>"))
                sb.Append(m.Groups[1].Value).Append(' ');
        }
        return sb.ToString();
    }

    [Fact]
    public void Pptx_Shows_Content_Rather_Than_Markdown_Syntax()
    {
        var path = Temp(".pptx");
        try
        {
            new PptxExportService().ExportAsync(Doc, path, new AppSettings()).GetAwaiter().GetResult();
            var text = SlideText(path);

            Assert.Contains("link", text);
            Assert.DoesNotContain("](https://example.com)", text);   // link markup
            Assert.Contains("Alt text", text);
            Assert.DoesNotContain("docs/images/logo.png", text);     // image markup
            Assert.DoesNotContain("| Left |", text);                 // raw table row
            Assert.DoesNotContain("| :--- |", text);                 // separator row
            Assert.DoesNotContain("> Quoted", text);                 // quote marker
            Assert.DoesNotContain("$a^2", text);                     // math delimiters
            Assert.DoesNotContain("[ ]", text);                      // raw checkbox
            Assert.Contains("☐", text);
            Assert.Contains("☑", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Pptx_Does_Not_Mangle_Code_Blocks()
    {
        // Emphasis stripping ran over fenced code, so "y = x * 2" lost its operator.
        var path = Temp(".pptx");
        try
        {
            new PptxExportService().ExportAsync(Doc, path, new AppSettings()).GetAwaiter().GetResult();
            Assert.Contains("y = x * 2", SlideText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Pptx_Keeps_Table_Cells_Readable()
    {
        var path = Temp(".pptx");
        try
        {
            new PptxExportService().ExportAsync(Doc, path, new AppSettings()).GetAwaiter().GetResult();
            var text = SlideText(path);
            Assert.Contains("Left", text);
            Assert.Contains("Right", text);
            Assert.Contains("·", text);   // cells joined, not dumped as pipes
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
