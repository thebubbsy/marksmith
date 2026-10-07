using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// EPUB chapters are parsed as XML by every reader. These pin the 2026-10-08 rewrite: chapters are
// serialized from a DOM (never regex-patched HTML), diagrams ship as images, equations as MathML.
public class EpubXhtmlTests
{
    private const string Rich = """
        # Opening

        - [x] done
        - [ ] todo

        Inline $E = mc^2$ and a note[^1].

        $$
        \frac{a}{b} = \sum_{i=1}^{n} x_i^2
        $$

        Raw HTML: a&nbsp;b<br>c <input type="checkbox" checked> <img src="https://example.com/x.png">

        <details open><summary>More</summary>hidden</details>

        # Diagrams

        ```mermaid
        flowchart LR
          A --> B
        ```

        ```mermaid
        sequenceDiagram
          accTitle: Login handshake
          A->>B: hi
        ```

        [^1]: The note.
        """;

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    private static ZipArchive Export(string markdown, System.Collections.Generic.IReadOnlyList<byte[]?>? pngs = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "marksmith_epub_xhtml_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var epub = Path.Combine(dir, "book.epub");
        new EpubExportService().ExportAsync(markdown, epub, new AppSettings(), null, pngs).GetAwaiter().GetResult();
        return ZipFile.OpenRead(epub);
    }

    private static string Read(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        Assert.NotNull(e);
        using var r = new StreamReader(e!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    [Fact]
    public void Every_Xhtml_Entry_Is_Well_Formed_Xml()
    {
        using var zip = Export(Rich);
        var xhtml = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml")).ToList();
        Assert.True(xhtml.Count >= 3); // nav + two chapters
        foreach (var e in xhtml)
        {
            var text = Read(zip, e.FullName);
            var ex = Record.Exception(() => XDocument.Parse(text));
            Assert.True(ex is null, $"{e.FullName}: {ex?.Message}\n{text}");
        }
        Assert.NotNull(XDocument.Parse(Read(zip, "OEBPS/content.opf")));
    }

    [Fact]
    public void Boolean_Attributes_And_Entities_Become_Xml()
    {
        using var zip = Export(Rich);
        var ch = Read(zip, "OEBPS/ch001.xhtml");
        Assert.Contains("checked=\"checked\"", ch);
        Assert.DoesNotContain("&nbsp;", ch);
        Assert.Contains("a b", ch);
        Assert.Contains("<br />", ch);
        Assert.Contains("open=\"open\"", ch);
    }

    [Fact]
    public void Math_Is_MathMl_With_The_Source_As_Annotation()
    {
        using var zip = Export(Rich);
        var ch = Read(zip, "OEBPS/ch001.xhtml");
        Assert.DoesNotContain("\\(", ch);
        Assert.Contains("<math xmlns=\"http://www.w3.org/1998/Math/MathML\" display=\"inline\"", ch);
        Assert.Contains("display=\"block\"", ch);
        Assert.Contains("<mfrac>", ch);
        Assert.Contains("<munderover>", ch);
        Assert.Contains("encoding=\"application/x-tex\"", ch);

        var opf = Read(zip, "OEBPS/content.opf");
        Assert.Matches("id=\"ch001\"[^>]*properties=\"mathml remote-resources\"", opf);
        Assert.DoesNotMatch("id=\"ch002\"[^>]*properties=", opf);
    }

    [Fact]
    public void Diagrams_Ship_As_Packaged_Pngs_With_Alt_Text()
    {
        using var zip = Export(Rich, new byte[]?[] { Png, Png });
        var ch = Read(zip, "OEBPS/ch002.xhtml");
        Assert.DoesNotContain("flowchart LR", ch);
        Assert.Contains("<img src=\"images/diagram-001.png\" alt=\"Flowchart 1\" />", ch);
        Assert.Contains("alt=\"Login handshake\"", ch);
        Assert.NotNull(zip.GetEntry("OEBPS/images/diagram-002.png"));
        Assert.Contains("href=\"images/diagram-001.png\" media-type=\"image/png\"", Read(zip, "OEBPS/content.opf"));
    }

    [Fact]
    public void Without_A_Renderer_Diagrams_Are_Labelled_Source()
    {
        using var zip = Export(Rich, new byte[]?[] { null, Png });
        var ch = Read(zip, "OEBPS/ch002.xhtml");
        Assert.Contains("figure class=\"diagram diagram-source\"", ch);
        Assert.Contains("A --&gt; B", ch);
        Assert.Contains("Flowchart 1 (diagram source", ch);
        Assert.Contains("images/diagram-002.png", ch);
    }

    [Fact]
    public void Footnote_Links_Cross_Chapters_And_Pop_Up()
    {
        using var zip = Export(Rich);
        var ch1 = Read(zip, "OEBPS/ch001.xhtml");
        // The note is defined at the end of the book (chapter 2); its reference is in chapter 1.
        Assert.Contains("href=\"ch002.xhtml#fn:1\"", ch1);
        Assert.Contains("epub:type=\"noteref\"", ch1);
        var ch2 = Read(zip, "OEBPS/ch002.xhtml");
        Assert.Contains("epub:type=\"footnote\"", ch2);
        Assert.Contains("href=\"ch001.xhtml#fnref:1\"", ch2);
    }

    [Fact]
    public void H1_Inside_A_Blockquote_Does_Not_Split_A_Chapter()
    {
        using var zip = Export("# One\n\n> # Quoted\n> body\n\n# Two\n");
        Assert.NotNull(zip.GetEntry("OEBPS/ch002.xhtml"));
        Assert.Null(zip.GetEntry("OEBPS/ch003.xhtml"));
        XDocument.Parse(Read(zip, "OEBPS/ch001.xhtml"));
    }

    [Fact]
    public void Scripts_And_Bad_Attribute_Names_Are_Dropped()
    {
        var x = XhtmlWriter.Fragment("<p onclick=\"x()\" @click=\"y\" data-ok=\"1\">hi<script>alert(1)</script></p><custom-el>kept</custom-el>");
        Assert.Equal("<p data-ok=\"1\">hi</p><custom-el>kept</custom-el>", x);
    }

    [Fact]
    public void Inline_Svg_Gets_Its_Namespace()
    {
        var x = XhtmlWriter.Fragment("<p><svg viewBox=\"0 0 10 10\"><use xlink:href=\"#a\"/></svg></p>");
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">", x);
        XDocument.Parse("<root xmlns=\"http://www.w3.org/1999/xhtml\">" + x + "</root>");
    }

    [Theory]
    [InlineData("\\sqrt{x}", "<msqrt>")]
    [InlineData("\\sqrt[3]{x}", "<mroot>")]
    [InlineData("x_1", "<msub>")]
    [InlineData("\\binom{n}{k}", "linethickness=\"0\"")]
    [InlineData("\\left( x \\right)", "fence=\"true\"")]
    [InlineData("\\begin{pmatrix} 1 & 2 \\\\ 3 & 4 \\end{pmatrix}", "<mtable>")]
    [InlineData("\\sin x", "<mi mathvariant=\"normal\">sin</mi>")]
    [InlineData("\\int_0^1 f", "<msubsup>")]
    [InlineData("12.5 + \\alpha", "<mn>12.5</mn>")]
    public void MathMl_Covers_The_Common_Constructs(string latex, string expected)
    {
        var m = LatexToMathMl.Convert(latex, display: false);
        Assert.Contains(expected, m);
        XDocument.Parse(m);
    }

    [Fact]
    public void Code_Text_Uses_The_Text_Colour_Not_The_Code_Background()
    {
        // ThemeDefinition.Code is the code *background* (#f6f8fa in GitHub Light). The stylesheet
        // used to paint code text that colour on a same-coloured background: invisible code.
        using var zip = Export("# T\n\n`x`\n");
        var css = Read(zip, "OEBPS/style.css");
        var theme = AppServices.Themes.GetOrDefault(new AppSettings().Theme);
        Assert.Contains($"background: {theme.Code}; color: {theme.Text};", css);
    }

    [Fact]
    public void MathMl_Uses_A_Real_Minus_Sign()
    {
        var m = LatexToMathMl.Convert("-b - 4ac", display: false);
        Assert.Contains("<mo>−</mo>", m);
        Assert.DoesNotContain("<mo>-</mo>", m);
        Assert.Contains("-b - 4ac", m); // the annotation keeps the source as typed
    }

    [Fact]
    public void Bare_Checkbox_List_Items_Become_A_Bulletless_Task_List()
    {
        // The shape the app's own pipeline produces (no Markdig task-list classes).
        using var zip = Export("# T\n\n<ul><li><input type=\"checkbox\" checked> a</li><li><input type=\"checkbox\"> b</li></ul>\n");
        var ch = Read(zip, "OEBPS/ch001.xhtml");
        Assert.Contains("<ul class=\"contains-task-list\">", ch);
        Assert.Contains("<li class=\"task-list-item\"><input type=\"checkbox\" checked=\"checked\" disabled=\"disabled\" />", ch);
    }

    [Fact]
    public void Mermaid_Label_Css_Is_A_Js_String_And_Rejects_Injection()
    {
        var css = MermaidLabelStyle.ThemeCss("#FAFAFA");
        Assert.StartsWith("\"", css);
        Assert.Contains("opacity:1!important;fill:#FAFAFA", css);
        Assert.Contains("fill:#ffffff", MermaidLabelStyle.ThemeCss("red}body{display:none"));
    }

    [Fact]
    public void MathMl_Of_Unparseable_Input_Still_Keeps_The_Text()
    {
        var m = LatexToMathMl.Convert("\\frac{", display: true);
        XDocument.Parse(m);
        Assert.Contains("annotation", m);
    }
}
