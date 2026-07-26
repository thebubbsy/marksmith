using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// QODER task 7: EPUB3 branding — embedded cover image (BrandCoverPage + BrandLogoPath) and
// Dublin Core metadata (dc:title / dc:creator / dc:language) driven by front matter or the
// ContentLanguage setting.
public class EpubCoverAndMetadataTests
{
    // 1x1 transparent PNG — just enough of a real image file to exercise the embed path.
    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00,
        0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
        0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
        0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82,
    };

    private static string Export(string markdown, AppSettings? settings = null, byte[]? logo = null, string logoExt = ".png")
    {
        var dir = Path.Combine(Path.GetTempPath(), "marksmith_epub_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var epub = Path.Combine(dir, "book.epub");
        if (logo is not null)
        {
            var logoPath = Path.Combine(dir, "logo" + logoExt);
            File.WriteAllBytes(logoPath, logo);
            (settings ??= new AppSettings()).BrandLogoPath = logoPath;
        }
        new EpubExportService().ExportAsync(markdown, epub, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return epub;
    }

    private static string ReadEntry(string epub, string name)
    {
        using var zip = ZipFile.OpenRead(epub);
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---- cover image ---------------------------------------------------------------------------

    [Fact]
    public void Cover_Image_Is_Embedded_When_Branding_Is_On()
    {
        var settings = new AppSettings { BrandCoverPage = true };
        var epub = Export("# Book\n\nBody.", settings, logo: TinyPng);

        using var zip = ZipFile.OpenRead(epub);
        var cover = zip.GetEntry("OEBPS/cover.png");
        Assert.NotNull(cover);
        using var s = cover!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(TinyPng, ms.ToArray());

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("properties=\"cover-image\"", opf);
        Assert.Contains("<item id=\"cover\" href=\"cover.xhtml\"", opf);
        Assert.True(File.Exists(epub));
    }

    [Fact]
    public void Cover_Page_Is_First_In_The_Spine()
    {
        var settings = new AppSettings { BrandCoverPage = true };
        var epub = Export("# Book\n\nBody.", settings, logo: TinyPng);

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        var coverRef = opf.IndexOf("<itemref idref=\"cover\"/>", System.StringComparison.Ordinal);
        var chapterRef = opf.IndexOf("<itemref idref=\"ch001\"/>", System.StringComparison.Ordinal);
        Assert.True(coverRef >= 0);
        Assert.True(chapterRef > coverRef, "cover must precede the first chapter in the spine");

        var coverXhtml = ReadEntry(epub, "OEBPS/cover.xhtml");
        Assert.Contains("epub:type=\"cover\"", coverXhtml);
        Assert.Contains("src=\"cover.png\"", coverXhtml);
    }

    [Fact]
    public void Jpeg_Logo_Gets_The_Jpeg_Media_Type()
    {
        var settings = new AppSettings { BrandCoverPage = true };
        var epub = Export("# Book\n\nBody.", settings, logo: new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, logoExt: ".jpg");

        using var zip = ZipFile.OpenRead(epub);
        Assert.NotNull(zip.GetEntry("OEBPS/cover.jpg"));
        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("media-type=\"image/jpeg\"", opf);
    }

    [Fact]
    public void No_Cover_When_Branding_Is_Off()
    {
        var settings = new AppSettings { BrandCoverPage = false };
        var epub = Export("# Book\n\nBody.", settings, logo: TinyPng);

        using var zip = ZipFile.OpenRead(epub);
        Assert.Null(zip.GetEntry("OEBPS/cover.png"));
        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.DoesNotContain("cover-image", opf);
    }

    [Fact]
    public void No_Cover_When_Logo_File_Is_Missing()
    {
        var settings = new AppSettings { BrandCoverPage = true, BrandLogoPath = Path.Combine(Path.GetTempPath(), "no_such_logo.png") };
        var epub = Export("# Book\n\nBody.", settings);

        using var zip = ZipFile.OpenRead(epub);
        Assert.Null(zip.GetEntry("OEBPS/cover.png"));
    }

    [Fact]
    public void Mimetype_Stays_First_Entry_Even_With_Cover()
    {
        var settings = new AppSettings { BrandCoverPage = true };
        var epub = Export("# Book\n\nBody.", settings, logo: TinyPng);

        using var zip = ZipFile.OpenRead(epub);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
    }

    // ---- Dublin Core metadata ------------------------------------------------------------------

    [Fact]
    public void Dublin_Core_Comes_From_Front_Matter()
    {
        var md = "---\ntitle: Field Notes\nauthor: Ada Lovelace\nlanguage: en-GB\n---\n\n# Field Notes\n\nBody.";
        var epub = Export(md);

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("<dc:title>Field Notes</dc:title>", opf);
        Assert.Contains("<dc:creator>Ada Lovelace</dc:creator>", opf);
        Assert.Contains("<dc:language>en-GB</dc:language>", opf);
        Assert.Contains("<dc:identifier id=\"bookid\">urn:uuid:", opf);
    }

    [Fact]
    public void Language_Falls_Back_To_ContentLanguage_Setting()
    {
        var settings = new AppSettings { ContentLanguage = "fr" };
        var epub = Export("# Livre\n\nCorps.", settings);

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("<dc:language>fr</dc:language>", opf);
        Assert.Contains("<dc:creator>Marksmith</dc:creator>", opf); // no author in front matter
    }

    [Fact]
    public void Defaults_Are_English_And_Marksmith_With_No_Metadata()
    {
        var epub = Export("# Plain\n\nBody.");

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("<dc:language>en</dc:language>", opf);
        Assert.Contains("<dc:creator>Marksmith</dc:creator>", opf);
        Assert.Contains("<dc:title>Plain</dc:title>", opf); // title still from the document
    }

    [Fact]
    public void Front_Matter_Title_Wins_Over_Document_Heading()
    {
        var md = "---\ntitle: The Real Title\n---\n\n# Some Heading\n\nBody.";
        var epub = Export(md);

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("<dc:title>The Real Title</dc:title>", opf);
    }

    [Fact]
    public void Metadata_Values_Are_Xml_Escaped()
    {
        var md = "---\ntitle: Q&A <Live>\nauthor: Tom & Jerry\n---\n\n# Body\n\ntext";
        var epub = Export(md);

        var opf = ReadEntry(epub, "OEBPS/content.opf");
        Assert.Contains("<dc:title>Q&amp;A &lt;Live&gt;</dc:title>", opf);
        Assert.Contains("<dc:creator>Tom &amp; Jerry</dc:creator>", opf);
    }
}
