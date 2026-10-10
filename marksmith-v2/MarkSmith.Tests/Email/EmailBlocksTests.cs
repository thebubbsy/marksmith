using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Email;
using Xunit;

namespace MarkSmith.Tests.Email;

/// <summary>
/// Run #65: every <c>:::</c> block in an email. The tabs printed their tab strip ("Option AOption
/// B"), the AI context printed its fields at the top (and so kept the title in the body), metrics
/// read "99.98%** Uptime", a canvas was an empty box, an embed vanished, references doubled their
/// heading and leaked "[@paper-id]", and charts were drawn in the document theme's colours.
/// </summary>
public class EmailBlocksTests
{
    private const string Blocks = """
        :::cover-page
        title: "Platform Review 2026"
        author: "Platform team"
        :::

        :::ai-context
        promptHash: abc123
        model: Gemini Pro
        :::

        # Platform Review 2026

        ## Headline numbers

        :::metrics
        - **99.98%** Uptime
        - **142 ms** P99 latency
        :::

        :::chart type="bar"
        Q1,10
        Q2,25
        :::

        :::workflow
        - Plan
        - Ship
        :::

        :::tabs
        === Option A
        Hire two senior engineers.
        === Option B
        Hire one.
        :::

        :::datagrid
        label,value
        Q1,10
        :::

        :::canvas
        <svg viewBox="0 0 100 100" width="200" height="200"><circle cx="50" cy="50" r="40" fill="red" /></svg>
        :::

        ## Further reading

        :::references
        @paper-id
        author: Author Name
        title: Publication Title
        year: 2026
        :::

        :::embed provider="youtube" src="https://www.youtube.com/watch?v=EXAMPLE_ID"
        :::
        """;

    private static readonly ThemeDefinition Dracula = new("Dracula", "#282a36", "#f8f8f2", "#bd93f9", "#44475a", "#6272a4", "#ff79c6", "#44475a", "#6272a4");

    private static EmailRenderResult Render(string md, ThemeDefinition? theme = null) =>
        new EmailHtmlRenderer(new AppSettings(), theme ?? new ThemeCatalog().GetOrDefault("GitHub Light")).Render(md);

    [Fact]
    public void No_block_prints_its_source()
    {
        var r = Render(Blocks);
        foreach (var raw in new[] { ":::", "===", "promptHash", "Option AOption B", "[@paper-id]", "label,value", "**" })
        {
            Assert.DoesNotContain(raw, r.Html);
            Assert.DoesNotContain(raw, r.Text);
        }
    }

    [Fact]
    public void The_title_leaves_the_body_even_after_an_ai_context_block()
    {
        var r = Render(Blocks);
        Assert.Equal("Platform Review 2026", r.Title);
        Assert.DoesNotMatch("<h1[^>]*>Platform Review 2026</h1>", r.Html);
        Assert.StartsWith("Headline numbers", r.Text);
    }

    [Fact]
    public void Metrics_are_kpi_cards_with_value_and_label_apart()
    {
        var r = Render(Blocks);
        Assert.Matches(@">99\.98%</div><div [^>]*>Uptime</div>", r.Html);
        Assert.Contains("cellspacing=\"8\"", r.Html);
        Assert.Contains("99.98%  Uptime", r.Text);
    }

    [Fact]
    public void Charts_diagrams_and_canvas_are_pictures_with_their_data_in_the_alt()
    {
        var r = Render(Blocks);
        Assert.Equal(3, r.Images.Count); // chart, workflow, canvas
        Assert.Contains("alt=\"Bar chart: Q1 10, Q2 25\"", r.Html);
        Assert.Contains("alt=\"Diagram: Plan · Ship\"", r.Html);
        Assert.Contains("[Bar chart: Q1 10, Q2 25]", r.Text);
        Assert.All(r.Images, i => Assert.True(i.Bytes.Length > 1000, i.FileName + " is blank"));
    }

    [Fact]
    public void Text_blocks_read_as_text()
    {
        var r = Render(Blocks);
        Assert.Matches(@"<h4[^>]*>Option A</h4>", r.Html);
        Assert.Matches(@"<th[^>]*>label</th>", r.Html);
        Assert.Contains("Author Name (2026). <em style=\"font-style:italic;\">Publication Title</em>.", r.Html);
        Assert.Single(Regex.Matches(r.Html, "Bibliography|Further reading"));
        Assert.Contains("href=\"https://www.youtube.com/watch?v=EXAMPLE_ID\"", r.Html);
    }

    [Fact]
    public void Still_well_formed_and_outlook_safe()
    {
        var html = Render(Blocks).Html;
        foreach (var banned in new[] { "<script", "<svg", "data:", "display:flex", "display:grid", "<style" })
            Assert.DoesNotContain(banned, html);
        using var reader = XmlReader.Create(new StringReader(html), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        while (reader.Read()) { }
    }

    [Fact]
    public void A_dark_theme_still_draws_a_light_chart()
    {
        var chart = Render(Blocks, Dracula).Images[0];
        using var bmp = SkiaSharp.SKBitmap.Decode(chart.Bytes);
        Assert.True(bmp.GetPixel(2, 2).Red > 240, "chart ground should be the email's white page");
    }

    [Fact]
    public void Table_cells_carry_the_font_classic_outlook_will_not_inherit()
    {
        var html = Render("| a | b |\n|---|---|\n| 1 | 2 |\n").Html;
        Assert.All(Regex.Matches(html, "<t[dh] [^>]*>").Select(m => m.Value).Where(t => !t.Contains("padding:4px 2px")),
            t => Assert.Contains("font-family:", t));
    }
}

/// <summary>Run #65: the preview, PDF and every export shared these.</summary>
public class SharedBlockRenderingTests
{
    [Fact]
    public void Formula_tables_leave_code_blocks_alone()
    {
        var md = "$$\nN = x\n$$\n\n```text\n+--+\n|  Edge  | ---> |  Data  |\n+--+\n```\n";
        Assert.Equal(md, TableFormulaEvaluator.EvaluateTableMarkdown(md));
    }

    [Fact]
    public void Each_table_is_its_own_grid()
    {
        var md = "| n |\n|---|\n| 100 |\n\n| n |\n|---|\n| 1 |\n| 2 |\n| =SUM(ABOVE) |\n";
        var outText = TableFormulaEvaluator.EvaluateTableMarkdown(md);
        Assert.Contains("| 3 |", outText);
        Assert.DoesNotContain("103", outText);
    }

    [Fact]
    public void Only_the_formula_row_is_rewritten()
    {
        var md = "| a | b |\n|---|---|\n| x \\| y  |  `p | q` |\n| 1 | 2 |\n| 3 | 4 |\n| =SUM(ABOVE) | =SUM(ABOVE) |";
        var lines = TableFormulaEvaluator.EvaluateTableMarkdown(md).Split('\n');
        Assert.Equal("| x \\| y  |  `p | q` |", lines[2]);
        Assert.Equal("| 1 | 2 |", lines[3]);
        Assert.Equal("| 4 | 6 |", lines[5]);
    }

    [Fact]
    public void Metric_cards_keep_bold_values_apart_from_their_labels()
    {
        var html = new MarkdownHtmlService().Render(":::metrics\n- **99.9%** Uptime\n- 12 ms: Latency\n:::\n",
            new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Matches(@"metric-value[^>]*>99\.9%</div>", html);
        Assert.Matches(@"metric-label[^>]*>Uptime</div>", html);
        Assert.Matches(@"metric-value[^>]*>12 ms</div>", html);
        Assert.DoesNotContain("99.9%**", html);
    }

    [Theory]
    [InlineData(31, new[] { "10", "20", "30", "40" })]
    [InlineData(180, new[] { "50", "100", "150", "200" })]
    [InlineData(12000, new[] { "5,000", "10,000", "15,000" })]
    public void Chart_axes_land_on_round_numbers(double max, string[] ticks)
    {
        var svg = MarkdownHtmlService.BuildChartSvg("bar", new() { "a", "b" }, new() { max / 2, max },
            new ThemeCatalog().GetOrDefault("GitHub Light"));
        foreach (var t in ticks) Assert.Contains($">{t}</text>", svg);
        Assert.DoesNotMatch(@">\d+\.\d{2}</text>", svg);
    }
}
