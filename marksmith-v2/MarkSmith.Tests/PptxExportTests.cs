using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Presentation;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// PowerPoint export, rebuilt. It used to split on headings and dump every line as a level-0 run
/// with no bullet glyphs (the master had no text styles), joined table cells with "·", dropped
/// every image and diagram, ignored links and emphasis, and let long sections run off the bottom
/// of the slide.
/// </summary>
public class PptxExportTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), $"ms-pptx-{Guid.NewGuid():N}.pptx");

    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static async Task<string> Export(string md, AppSettings? settings = null, IReadOnlyList<byte[]?>? pngs = null)
    {
        var path = Temp();
        await new PptxExportService().ExportAsync(md, path, settings ?? new AppSettings(), pngs);
        return path;
    }

    private static List<string> SlidesXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries
            .Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
            .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value))
            .Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); })
            .ToList();
    }

    private static string Text(string slideXml) =>
        string.Join("", Regex.Matches(slideXml, "<a:t>([^<]*)</a:t>").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));

    private static void AssertValid(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10)));
    }

    private const string Rich = """
        # Quarterly Review

        Where we are and what comes next.

        ## Agenda

        1. Highlights
        2. Metrics
           1. Lead time
        3. Next steps

        ## Highlights

        - Shipped **billing** with *zero* incidents and a [link](https://example.com/platform)
          - Build time `3m 40s`
        - [x] Retire the queue
        - [ ] Move search

        > [!WARNING]
        > Snapshot numbers.

        > A plain quote.

        | Metric | Q2 | Q3 |
        | :--- | ---: | :---: |
        | Deploys | 41 | 63 |

        Inline $a^2 + b^2 = c^2$ and a footnote[^1].

        $$
        E = mc^2
        $$

        ```python
        def f(x):
            return x * 2  # double
        ```

        ```mermaid
        flowchart LR
          A --> B
        ```

        <p align="center"><b>Bold HTML</b> text</p>

        [^1]: The footnote text.

        Term
        :   Definition.
        """;

    [Theory]
    [InlineData("GitHub Light")]
    [InlineData("Dracula")]
    public async Task Package_Is_Schema_Valid(string theme)
    {
        var path = await Export(Rich, new AppSettings { Theme = theme, AuthorName = "Jordan" }, new List<byte[]?> { Png1x1 });
        try { AssertValid(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Empty_Document_Still_Makes_A_Valid_Deck()
    {
        var path = await Export("");
        try
        {
            AssertValid(path);
            Assert.Single(SlidesXml(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Leading_H1_Becomes_A_Title_Slide_With_Subtitle_And_Author()
    {
        var deck = SlideDeckBuilder.Build(Rich, new SlideDeckOptions { AuthorName = "Jordan" });
        var first = deck.Slides[0];
        Assert.Equal(SlideKind.Title, first.Kind);
        Assert.Equal("Quarterly Review", first.Title);
        Assert.Equal("Where we are and what comes next.", first.Subtitle);
        Assert.Equal("Jordan", first.Byline);
        Assert.Equal("Agenda", deck.Slides[1].Title);
    }

    [Fact]
    public void Heading_With_Nothing_Under_It_Is_A_Section_Divider()
    {
        var deck = SlideDeckBuilder.Build("# Deck\n\n## One\n\nText\n\n# Appendix\n\n## Glossary\n\nWords");
        Assert.Contains(deck.Slides, s => s.Kind == SlideKind.Section && s.Title == "Appendix");
    }

    [Fact]
    public void Rule_Splits_A_Slide_But_Never_Leaves_An_Empty_One()
    {
        var deck = SlideDeckBuilder.Build("## A\n\nOne\n\n---\n\nTwo\n\n---\n\n## B\n\nThree");
        Assert.Equal(new[] { "A", "A", "B" }, deck.Slides.Select(s => s.Title));
        Assert.All(deck.Slides, s => Assert.Equal(SlideKind.Content, s.Kind));
    }

    [Fact]
    public void Long_Sections_Continue_On_The_Next_Slide_And_Nothing_Overflows()
    {
        var md = "## Long\n\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"- Item number {i} with a little text"))
               + "\n\n```\n" + string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}")) + "\n```\n\n"
               + "| A | B |\n|---|---|\n" + string.Join("\n", Enumerable.Range(1, 50).Select(i => $"| {i} | value {i} |"));
        var deck = SlideDeckBuilder.Build(md);
        Assert.True(deck.Slides.Count >= 5);
        Assert.Equal("Long", deck.Slides[0].Title);
        Assert.All(deck.Slides.Skip(1), s => Assert.Equal("Long (continued)", s.Title));
        foreach (var slide in deck.Slides)
        {
            var used = slide.Blocks.Sum(b => b.HeightPt) + Math.Max(0, slide.Blocks.Count - 1) * SlideGeometry.BlockGapPt;
            Assert.True(used <= SlideGeometry.BodyHeightPt + 0.5, $"{slide.Title}: {used}pt");
        }
        // Every list item, code line and table row made it onto some slide, once.
        var items = deck.Slides.SelectMany(s => s.Blocks.OfType<TextBlock>()).SelectMany(t => t.Paragraphs).Count(p => p.Style == ParaStyle.Bullet);
        Assert.Equal(40, items);
        Assert.Equal(60, deck.Slides.SelectMany(s => s.Blocks.OfType<CodeSlideBlock>()).Sum(c => c.Lines.Count));
        var tables = deck.Slides.SelectMany(s => s.Blocks.OfType<TableSlideBlock>()).ToList();
        Assert.Equal(50, tables.Sum(t => t.Rows.Count));
        Assert.True(tables.Count > 1);
        Assert.All(tables, t => Assert.Equal(2, t.Header.Count));   // header repeats
    }

    [Fact]
    public void A_Subheading_Is_Never_Stranded_At_The_Bottom_Of_A_Slide()
    {
        var md = "## S\n\n" + string.Join("\n\n", Enumerable.Range(1, 30).Select(i => i % 4 == 0 ? $"### Heading {i}" : $"Paragraph {i} with enough words to take a line."));
        var deck = SlideDeckBuilder.Build(md);
        foreach (var slide in deck.Slides)
        {
            var last = slide.Blocks.OfType<TextBlock>().LastOrDefault()?.Paragraphs.LastOrDefault();
            if (slide != deck.Slides[^1]) Assert.NotEqual(ParaStyle.Subheading, last?.Style);
        }
    }

    [Fact]
    public async Task Lists_Get_Real_Bullets_Numbers_And_Checkboxes()
    {
        var path = await Export(Rich);
        try
        {
            var xml = string.Join("", SlidesXml(path));
            Assert.Contains("<a:buAutoNum type=\"arabicPeriod\" startAt=\"2\"/>", xml);
            Assert.Contains("<a:buAutoNum type=\"alphaLcPeriod\" startAt=\"1\"/>", xml);
            Assert.Contains("<a:buChar char=\"•\"/>", xml);
            Assert.Contains("<a:buChar char=\"–\"/>", xml);
            Assert.Contains("<a:buChar char=\"☑\"/>", xml);
            Assert.Contains("<a:buChar char=\"☐\"/>", xml);
            Assert.DoesNotContain("[x]", Text(xml));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Emphasis_Code_And_Links_Are_Formatted_Not_Stripped()
    {
        var path = await Export(Rich);
        try
        {
            var xml = SlidesXml(path).First(s => s.Contains("billing"));
            Assert.Matches("<a:rPr[^>]* b=\"1\"[^>]*>.*?</a:rPr><a:t>billing</a:t>", xml);
            Assert.Matches("<a:rPr[^>]* i=\"1\"[^>]*>.*?</a:rPr><a:t>zero</a:t>", xml);
            Assert.Matches("<a:latin typeface=\"Consolas\"/>.*?</a:rPr><a:t>3m 40s</a:t>", xml);
            Assert.Contains("<a:hlinkClick r:id=", xml);

            using var doc = PresentationDocument.Open(path, false);
            var links = doc.PresentationPart!.SlideParts.SelectMany(p => p.HyperlinkRelationships).Select(r => r.Uri.ToString());
            Assert.Contains("https://example.com/platform", links);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Tables_Are_PowerPoint_Tables_With_A_Header_And_Alignment()
    {
        var path = await Export(Rich);
        try
        {
            var xml = SlidesXml(path).First(s => s.Contains("<a:tbl>"));
            Assert.Equal(3, Regex.Matches(xml, "<a:gridCol ").Count);
            Assert.Equal(2, Regex.Matches(xml, "<a:tr ").Count);
            Assert.Contains("algn=\"r\"", xml);
            Assert.Contains("algn=\"ctr\"", xml);
            Assert.Contains("Metric", Text(xml));
            Assert.DoesNotContain("|", Text(xml));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Code_Keeps_Its_Lines_Indentation_And_Colours()
    {
        var path = await Export(Rich);
        try
        {
            var xml = SlidesXml(path).First(s => s.Contains("def"));
            Assert.Contains("    ", Text(xml));                      // indentation kept
            Assert.Contains("x * 2", Text(xml));                     // nothing stripped
            var colours = Regex.Matches(xml, "Consolas\"/>").Count;
            Assert.True(colours > 3);
            Assert.True(Regex.Matches(xml, "<a:srgbClr val=\"([0-9A-F]{6})\"/></a:solidFill><a:latin typeface=\"Consolas\"")
                .Select(m => m.Groups[1].Value).Distinct().Count() > 1, "syntax colours");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Diagrams_Are_Pictures_When_Rendered_And_Labelled_Source_Otherwise()
    {
        var withPng = await Export(Rich, pngs: new List<byte[]?> { Png1x1 });
        var without = await Export(Rich);
        try
        {
            using (var doc = PresentationDocument.Open(withPng, false))
                Assert.Contains(doc.PresentationPart!.SlideParts, p => p.ImageParts.Any());
            Assert.Contains("descr=\"Flowchart 1\"", string.Join("", SlidesXml(withPng)));
            Assert.Contains("Flowchart 1 (diagram source)", Text(string.Join("", SlidesXml(without))));
        }
        finally { File.Delete(withPng); File.Delete(without); }
    }

    [Fact]
    public async Task Relative_Images_Are_Embedded_And_Missing_Ones_Say_So()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ms-pptx-doc {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "my images"));
        File.WriteAllBytes(Path.Combine(dir, "my images", "a b.png"), Png1x1);
        var md = "## Pictures\n\n![Chart](<my images/a b.png>)\n\n## Gone\n\n![Whiteboard](images/gone.png)";
        string path;
        using (DocumentImages.UseFolder(dir)) path = await Export(md);
        try
        {
            AssertValid(path);
            var slides = SlidesXml(path);
            Assert.Contains("descr=\"Chart\"", slides[0]);
            Assert.Contains("Image not found: gone.png", Text(slides[1]));
            Assert.Contains("Whiteboard", Text(slides[1]));
        }
        finally { File.Delete(path); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Footnotes_Become_A_Notes_Slide()
    {
        var path = await Export(Rich);
        try
        {
            var slides = SlidesXml(path);
            var all = string.Join("", slides.Select(Text));
            Assert.DoesNotContain("[^1]", all);
            Assert.Contains("Notes", Text(slides[^1]));
            Assert.Contains("The footnote text.", Text(slides[^1]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Html_Blocks_Keep_Their_Formatting()
    {
        var deck = SlideDeckBuilder.Build("## H\n\n<p align=\"center\"><b>Bold HTML</b> text</p>");
        var runs = deck.Slides.SelectMany(s => s.Blocks.OfType<TextBlock>()).SelectMany(t => t.Paragraphs).SelectMany(p => p.Runs).ToList();
        Assert.Contains(runs, r => r.Bold && r.Text.Contains("Bold HTML"));
        Assert.DoesNotContain(runs, r => r.Text.Contains('<'));
    }

    [Fact]
    public async Task Every_Slide_But_The_Title_Has_A_Slide_Number()
    {
        var path = await Export(Rich);
        try
        {
            var slides = SlidesXml(path);
            Assert.DoesNotContain("type=\"slidenum\"", slides[0]);
            Assert.All(slides.Skip(1), s => Assert.Contains("type=\"slidenum\"", s));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Master_Has_Text_Styles_And_Three_Layouts()
    {
        var path = await Export(Rich);
        try
        {
            using var doc = PresentationDocument.Open(path, false);
            var master = doc.PresentationPart!.SlideMasterParts.Single();
            Assert.NotNull(master.SlideMaster.TextStyles);
            var names = master.SlideLayoutParts.Select(l => l.SlideLayout.CommonSlideData!.Name!.Value).ToList();
            Assert.Equal(new[] { "Title Slide", "Title and Content", "Section Header" }, names);
        }
        finally { File.Delete(path); }
    }
}
