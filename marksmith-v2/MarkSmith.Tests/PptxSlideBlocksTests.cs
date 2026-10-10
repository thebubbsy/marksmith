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
/// PowerPoint export of the ::: blocks the Insert menu adds, and pagination that keeps a heading
/// with what it introduces. Every one of these blocks used to print on a slide as its raw source
/// (":::chart type=bar Q1,10 Q2,25", "title: … subtitle: …"), and a heading before a table or a
/// code block was routinely left alone at the foot of a slide.
/// </summary>
public class PptxSlideBlocksTests
{
    private static async Task<string> Export(string md, AppSettings? settings = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-pptx-{Guid.NewGuid():N}.pptx");
        await new PptxExportService().ExportAsync(md, path, settings ?? new AppSettings());
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

    private static string AllText(PptxDeck deck) => string.Join("\n", deck.Slides.SelectMany(s =>
        new[] { s.Title, s.Subtitle ?? "", s.Byline ?? "" }.Concat(s.Blocks.OfType<TextBlock>().SelectMany(t => t.Paragraphs.Select(p => p.PlainText)))));

    // ── cover page and page furniture ──

    [Fact]
    public void A_cover_page_is_the_title_slide()
    {
        var deck = SlideDeckBuilder.Build("""
            :::cover-page
            title: "Platform Review"
            subtitle: "What shipped"
            author: "Platform team"
            date: "2026-10-10"
            :::

            # Platform Review

            ## Numbers

            Up and to the right.
            """);
        var cover = deck.Slides[0];
        Assert.Equal(SlideKind.Title, cover.Kind);
        Assert.Equal("Platform Review", cover.Title);
        Assert.Equal("What shipped", cover.Subtitle);
        Assert.Equal("Platform team  ·  2026-10-10", cover.Byline);
        // The H1 repeating the cover's title, with nothing under it, adds no second title slide.
        Assert.Equal(1, deck.Slides.Count(s => s.Title == "Platform Review"));
        Assert.DoesNotContain("subtitle:", AllText(deck));
    }

    [Fact]
    public void Page_furniture_never_reaches_a_slide()
    {
        var deck = SlideDeckBuilder.Build("""
            # Doc

            :::watermark "DRAFT" opacity=0.10

            :::ai-context
            promptHash: abc123
            model: Gemini Pro
            :::

            :::line-numbers count-by=5

            Body text.
            """);
        var text = AllText(deck);
        Assert.DoesNotContain(":::", text);
        Assert.DoesNotContain("DRAFT", text);
        Assert.DoesNotContain("promptHash", text);
        Assert.Contains("Body text.", text);
    }

    // ── blocks that become slide content ──

    [Fact]
    public void Tabs_become_headed_sections_and_columns_lose_their_separators()
    {
        var deck = SlideDeckBuilder.Build("""
            ## Options

            :::tabs
            === Option A
            Hire two.
            === Option B
            Hire one.
            :::

            :::columns count="2"
            Left side.
            ===
            Right side.
            :::
            """);
        var paras = deck.Slides.SelectMany(s => s.Blocks.OfType<TextBlock>()).SelectMany(t => t.Paragraphs).ToList();
        Assert.Contains(paras, p => p.Style == ParaStyle.Subheading && p.PlainText == "Option A");
        Assert.Contains(paras, p => p.Style == ParaStyle.Subheading && p.PlainText == "Option B");
        Assert.Contains(paras, p => p.PlainText == "Left side.");
        Assert.Contains(paras, p => p.PlainText == "Right side.");
        // "Left side.\n===" would otherwise be a setext H1 and start a slide of its own.
        Assert.Single(deck.Slides);
        Assert.DoesNotContain("===", AllText(deck));
    }

    [Fact]
    public void A_datagrid_is_a_table_with_its_first_line_as_the_header()
    {
        var deck = SlideDeckBuilder.Build("## Data\n\n:::datagrid\nlabel,value\nQ1,10\nQ2,25\n:::\n");
        var table = Assert.Single(deck.Slides.SelectMany(s => s.Blocks).OfType<TableSlideBlock>());
        Assert.Equal(new[] { "label", "value" }, table.Header.Select(c => c[0].PlainText));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("25", table.Rows[1][1][0].PlainText);
    }

    [Fact]
    public void References_and_embeds_read_as_text_and_links()
    {
        var deck = SlideDeckBuilder.Build("""
            ## Reading

            :::references
            @paper-id
            author: Ada Lovelace
            title: Notes on the Engine
            year: 1843
            :::

            :::embed provider="youtube" src="https://www.youtube.com/watch?v=abc"
            :::
            """);
        var text = AllText(deck);
        Assert.Contains("Ada Lovelace (1843). Notes on the Engine.", text);
        Assert.DoesNotContain("@paper-id", text);
        var link = deck.Slides.SelectMany(s => s.Blocks.OfType<TextBlock>()).SelectMany(t => t.Paragraphs).SelectMany(p => p.Runs)
            .Single(r => r.Url is not null);
        Assert.Equal("YouTube", link.Text);
        Assert.Equal("https://www.youtube.com/watch?v=abc", link.Url);
    }

    [Fact]
    public void A_canvas_is_a_picture()
    {
        var deck = SlideDeckBuilder.Build("## Drawing\n\n:::canvas\n<svg viewBox=\"0 0 100 100\" width=\"200\" height=\"200\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"red\"/></svg>\n:::\n");
        var pic = Assert.Single(deck.Slides.SelectMany(s => s.Blocks).OfType<PictureSlideBlock>());
        Assert.Equal("image/png", pic.ContentType);
        Assert.True(pic.PixelWidth > 0 && pic.PixelHeight > 0);
    }

    // ── native blocks ──

    [Theory]
    [InlineData("bar", ChartKind.Bar)]
    [InlineData("line", ChartKind.Line)]
    [InlineData("pie", ChartKind.Pie)]
    public void A_chart_keeps_every_data_point(string type, ChartKind kind)
    {
        var deck = SlideDeckBuilder.Build($"## Releases\n\n:::chart type=\"{type}\"\nQ1,10\nQ2,25\nQ3,15.5\n:::\n");
        var chart = Assert.Single(deck.Slides.SelectMany(s => s.Blocks).OfType<ChartSlideBlock>());
        Assert.Equal(kind, chart.Kind);
        // No header line: the first row is data, not a header to skip.
        Assert.Equal(new[] { "Q1", "Q2", "Q3" }, chart.Labels);
        Assert.Equal(new[] { 10, 25, 15.5 }, chart.Values);
    }

    [Theory]
    [InlineData("- **99.9%** Uptime", "99.9%", "Uptime")]
    [InlineData("- 142 ms: P99 latency", "142 ms", "P99 latency")]
    [InlineData("* **12.4M** — Requests / day", "12.4M", "Requests / day")]
    public void Metrics_read_the_figure_and_its_label(string line, string value, string label)
    {
        var deck = SlideDeckBuilder.Build($"## KPIs\n\n:::metrics\n{line}\n:::\n");
        var metrics = Assert.Single(deck.Slides.SelectMany(s => s.Blocks).OfType<MetricsSlideBlock>());
        Assert.Equal((value, label), Assert.Single(metrics.Items));
    }

    [Fact]
    public void Smartart_workflow_and_timeline_are_diagrams()
    {
        var deck = SlideDeckBuilder.Build("""
            ## A

            :::workflow
            - Plan
            - Build
            :::

            ## B

            :::timeline
            2020: Started
            2026: Done
            :::

            ## C

            :::smartart type="hierarchy"
            - CTO
              - Platform
            :::
            """);
        var arts = deck.Slides.SelectMany(s => s.Blocks).OfType<SmartArtSlideBlock>().ToList();
        Assert.Equal(3, arts.Count);
        // The documented bare "year: label" timeline form gets its bullets.
        Assert.Contains("- 2020: Started", arts[1].Body);
        Assert.Equal("hierarchy", arts[2].Layout);
        Assert.Equal(2, arts[2].Depth);
    }

    [Fact]
    public async Task Charts_smartart_and_kpis_are_native_and_valid()
    {
        var path = await Export("""
            :::cover-page
            title: "Review"
            :::

            ## KPIs

            :::metrics
            - **99.9%** Uptime
            - **142 ms** Latency
            :::

            ## Chart

            :::chart type="pie"
            Build,35
            Review,25
            :::

            ## Steps

            :::workflow
            - Plan
            - Build
            - Ship
            :::
            """);
        try
        {
            AssertValid(path);
            using var doc = PresentationDocument.Open(path, false);
            var slides = doc.PresentationPart!.SlideParts.ToList();
            var chartPart = Assert.Single(slides.SelectMany(s => s.ChartParts));
            // The chart carries its data, so "Edit Data" opens it in Excel.
            Assert.Single(chartPart.Parts, p => p.OpenXmlPart is EmbeddedPackagePart);
            var chartXml = new StreamReader(chartPart.GetStream()).ReadToEnd();
            Assert.Contains("<c:pieChart>", chartXml);
            Assert.Contains("<c:v>Build</c:v>", chartXml);
            Assert.Contains("<c:legend>", chartXml);

            var diagram = Assert.Single(slides, s => s.DiagramDataParts.Any());
            Assert.Single(diagram.DiagramLayoutDefinitionParts);
            Assert.Single(diagram.DiagramColorsParts);
            Assert.Single(diagram.DiagramStyleParts);

            var xml = SlidesXml(path);
            var kpis = xml.Single(x => x.Contains("name=\"Metric 1\""));
            Assert.Contains("99.9%", Text(kpis));
            Assert.Contains("Uptime", Text(kpis));
            Assert.DoesNotContain(xml, x => Text(x).Contains(":::"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Mermaid_charts_take_the_chart_palette_and_the_surface_they_sit_on()
    {
        var theme = new ThemeDefinition("Dark", "#282A36", "#F8F8F2", "#BD93F9", "#44475A", "#6272A4", "#BD93F9", "#44475A", "#6272A4");
        var vars = MermaidLabelStyle.ChartVariables(theme, "#44475A");
        var palette = MarkSmith.Services.Mermaid.MermaidChartsRenderer.BuildPalette(theme);
        // Pies used to derive every slice from primaryColor (the page colour): white wedges.
        Assert.Contains($"pie1: \"{palette[0]}\"", vars);
        // An xychart painted a white box on dark themes.
        Assert.Contains("xyChart: { backgroundColor: \"#44475A\"", vars);
        Assert.Contains("xAxisLabelColor: \"#F8F8F2\"", vars);
    }

    // ── pagination ──

    private static string Filler(int paragraphs) => string.Join("\n\n", Enumerable.Range(1, paragraphs).Select(i =>
        $"Paragraph {i}. Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua."));

    private static string Table(int rows) =>
        "| Team | Releases |\n|---|---:|\n" + string.Join("\n", Enumerable.Range(1, rows).Select(i => $"| Team {i} | {i} |"));

    [Fact]
    public void A_slide_never_ends_on_a_heading()
    {
        foreach (var n in Enumerable.Range(1, 9))
        {
            var deck = SlideDeckBuilder.Build($"## Section\n\n{Filler(n)}\n\n### The table\n\n{Table(6)}\n\n### The code\n\n```cs\nvar a = 1;\nvar b = 2;\nvar c = 3;\nvar d = 4;\n```\n");
            foreach (var slide in deck.Slides)
            {
                var last = slide.Blocks.LastOrDefault() as TextBlock;
                Assert.False(last?.Paragraphs.LastOrDefault()?.Style == ParaStyle.Subheading,
                    $"{n} paragraphs: slide '{slide.Title}' ends on a heading");
            }
        }
    }

    [Fact]
    public void A_table_that_fits_a_slide_is_never_split()
    {
        foreach (var n in Enumerable.Range(1, 7))
        {
            var deck = SlideDeckBuilder.Build($"## Section\n\n{Filler(n)}\n\n{Table(10)}\n");
            var parts = deck.Slides.SelectMany(s => s.Blocks).OfType<TableSlideBlock>().ToList();
            Assert.True(parts.Count == 1, $"{n} paragraphs: the table was split over {parts.Count} slides");
            Assert.Equal(10, parts[0].Rows.Count);
        }
    }

    [Fact]
    public void A_short_paragraph_after_a_big_picture_stays_on_its_slide()
    {
        using var bmp = new SkiaSharp.SKBitmap(1800, 1000);
        using (var canvas = new SkiaSharp.SKCanvas(bmp)) canvas.Clear(SkiaSharp.SKColors.SteelBlue);
        using var png = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        var uri = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());

        var deck = SlideDeckBuilder.Build($"## A wide photo\n\n![Dashboard]({uri} \"Figure 1: the dashboard\")\n\n{Filler(2)}\n");
        var slide = Assert.Single(deck.Slides);
        Assert.Single(slide.Blocks.OfType<PictureSlideBlock>());
        Assert.Contains(slide.Blocks.OfType<TextBlock>().SelectMany(t => t.Paragraphs), p => p.PlainText.StartsWith("Paragraph 2."));
    }

    [Fact]
    public void An_introduction_ending_in_a_colon_stays_with_its_code()
    {
        foreach (var n in Enumerable.Range(1, 7))
        {
            var deck = SlideDeckBuilder.Build($"## Section\n\n{Filler(n)}\n\nThe check looks like this:\n\n```cs\n{string.Join("\n", Enumerable.Range(1, 8).Select(i => $"var x{i} = {i};"))}\n```\n");
            var codeSlide = deck.Slides.First(s => s.Blocks.OfType<CodeSlideBlock>().Any());
            var before = codeSlide.Blocks.TakeWhile(b => b is not CodeSlideBlock).OfType<TextBlock>().SelectMany(t => t.Paragraphs);
            Assert.True(before.Any(p => p.PlainText == "The check looks like this:"), $"{n} paragraphs: the introduction was left behind");
        }
    }
}
