using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// The ::: blocks in an e-book. They used to print as raw source: a cover page as
// "title: … subtitle: …", a chart as "Q1,10 Q2,25", tabs with their === separators.
public class EpubBlocksTests
{
    private static ZipArchive Export(string markdown, AppSettings? settings = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "marksmith_epub_blocks_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var epub = Path.Combine(dir, "book.epub");
        new EpubExportService().ExportAsync(markdown, epub, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return ZipFile.OpenRead(epub);
    }

    private static string Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Chapters(ZipArchive zip) => string.Concat(zip.Entries
        .Where(e => e.FullName.StartsWith("OEBPS/ch", System.StringComparison.Ordinal))
        .OrderBy(e => e.FullName).Select(e => Read(zip, e.FullName)));

    [Fact]
    public void Chart_becomes_a_packaged_picture_with_its_data_in_the_alt_text()
    {
        using var zip = Export("# Book\n\n:::chart type=\"bar\"\nQ1,10\nQ2,25\n:::\n");
        var html = Chapters(zip);
        Assert.DoesNotContain("Q1,10", html);
        Assert.Contains("alt=\"Bar chart: Q1 10, Q2 25\"", html);
        var png = zip.GetEntry("OEBPS/images/ms-block-001.png");
        Assert.NotNull(png);
        Assert.Contains("href=\"images/ms-block-001.png\" media-type=\"image/png\"", Read(zip, "OEBPS/content.opf"));
        // drawn at 2x, shown at its natural 460 px
        Assert.Contains("width=\"460\"", html);
    }

    [Fact]
    public void Metrics_become_cards_without_markdown_asterisks()
    {
        using var zip = Export("# Book\n\n:::metrics\n- **99.9%** Uptime\n- **12M** Requests\n:::\n");
        var html = Chapters(zip);
        Assert.Contains("<p class=\"ms-metric-value\">99.9%</p>", html);
        Assert.Contains("<p class=\"ms-metric-label\">Uptime</p>", html);
        Assert.DoesNotContain("**", html);
    }

    [Fact]
    public void SmartArt_is_drawn_and_described()
    {
        using var zip = Export("# Book\n\n:::workflow\n- Plan\n- Build\n- Ship\n:::\n");
        var html = Chapters(zip);
        Assert.Contains("alt=\"Diagram: Plan · Build · Ship\"", html);
        Assert.NotNull(zip.GetEntry("OEBPS/images/ms-block-001.png"));
    }

    [Fact]
    public void A_hierarchy_describes_every_level()
    {
        using var zip = Export("# Book\n\n:::smartart type=\"hierarchy\"\n- CTO\n  - Platform\n  - Data\n:::\n");
        Assert.Contains("alt=\"Diagram: CTO · Platform · Data\"", Chapters(zip));
    }

    [Fact]
    public void Cover_page_feeds_the_title_page_and_metadata_instead_of_printing()
    {
        using var zip = Export(":::cover-page\ntitle: \"Platform Review\"\nsubtitle: \"What shipped\"\nauthor: \"Platform team\"\norganization: \"Northwind\"\ndate: \"2026-10-10\"\nversion: \"v1.2\"\n:::\n\nBody text.\n");
        Assert.DoesNotContain("subtitle:", Chapters(zip));
        var title = Read(zip, "OEBPS/title.xhtml");
        Assert.Contains("<h1 class=\"title\">Platform Review</h1>", title);
        Assert.Contains("<p class=\"subtitle\">What shipped</p>", title);
        Assert.Contains("<p class=\"publisher\">Northwind</p>", title);
        Assert.Contains("2026-10-10 · v1.2", title);
        var opf = Read(zip, "OEBPS/content.opf");
        Assert.Contains("<dc:title>Platform Review</dc:title>", opf);
        Assert.Contains("<dc:creator>Platform team</dc:creator>", opf);
    }

    [Fact]
    public void Text_blocks_take_their_readable_shape_and_furniture_is_dropped()
    {
        var md = "# Book\n\n:::watermark \"DRAFT\" opacity=0.10\n\n:::ai-context\npromptHash: abc\nmodel: X\n:::\n\n" +
                 ":::tabs\n=== Option A\nHire two.\n=== Option B\nHire one.\n:::\n\n" +
                 ":::columns count=\"2\"\n**Left** side.\n===\n**Right** side.\n:::\n\n" +
                 ":::datagrid\nlabel,value\nQ1,10\n:::\n";
        using var zip = Export(md);
        var html = Chapters(zip);
        Assert.DoesNotContain("===", html);
        Assert.DoesNotContain("promptHash", html);
        Assert.DoesNotContain("DRAFT", html);
        Assert.Contains(">Option A</h4>", html);
        Assert.Contains("<th>label</th>", html);
        Assert.DoesNotContain("<h1><strong>Left", html);   // a === under a line used to make it a heading
    }

    [Fact]
    public void Every_chapter_is_well_formed_xml_with_blocks()
    {
        using var zip = Export("# Book\n\n:::chart type=\"pie\"\nA & B,3\n<C>,4\n:::\n\n:::metrics\n- 5 < 6: \"quoted\" & more\n:::\n");
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", System.StringComparison.Ordinal)))
            new XmlDocument().LoadXml(Read(zip, e.FullName));
    }

    [Fact]
    public void A_fenced_example_of_a_block_is_left_as_code()
    {
        using var zip = Export("# Book\n\n```markdown\n:::chart type=\"bar\"\nQ1,10\n:::\n```\n");
        var html = Chapters(zip);
        Assert.Contains("Q1,10", html);
        Assert.Null(zip.GetEntry("OEBPS/images/ms-block-001.png"));
    }
}
