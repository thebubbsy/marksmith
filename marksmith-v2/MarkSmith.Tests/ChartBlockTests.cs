using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// The <c>:::chart</c> wrapper. Its detector demanded a JSON spec or <c>spec:</c>/<c>data:</c> keys,
/// so the plain <c>label,value</c> body the wrapper catalog documents — and that
/// <see cref="InsertSnippetBuilder.Chart"/> itself writes — failed validation and fell through to
/// the raw-text path. The exporter then discarded the first data row as a header that was never
/// there, and the preview pipeline had no handler at all.
/// </summary>
public class ChartBlockTests
{
    private const string NoHeader = """
        :::chart type="bar"
        Alpha,10
        Beta,25
        Gamma,17
        :::
        """;

    private const string WithHeader = """
        :::chart type="line"
        Quarter,Revenue
        Q1,120
        Q2,148
        :::
        """;

    // ---- the shared row test ------------------------------------------------------------------

    [Theory]
    [InlineData("Alpha,10", true)]
    [InlineData("Some label, 12.5", true)]
    [InlineData("Label with, commas,3", true)]   // only the last comma separates
    [InlineData("Quarter,Revenue", false)]       // a header row
    [InlineData("no comma here", false)]
    [InlineData(",10", false)]
    public void IsLabelValueLine_Recognises_Data_Rows(string line, bool expected)
        => Assert.Equal(expected, ChartDetector.IsLabelValueLine(line));

    [Fact]
    public void Detector_Accepts_The_Body_The_App_Itself_Writes()
    {
        var snippet = InsertSnippetBuilder.Chart("bar", new[] { "Q1,10", "Q2,25", "Q3,15" });
        var (valid, confidence, errors) = new ChartDetector().Validate(snippet.Trim());
        Assert.True(valid, string.Join("; ", errors));
        Assert.True(confidence >= new ChartDetector().Threshold);
    }

    [Fact]
    public void Detector_Still_Accepts_A_Json_Body()
    {
        var block = ":::chart type=\"bar\"\n{\"data\":{\"labels\":[\"A\"],\"values\":[1]}}\n:::";
        Assert.True(new ChartDetector().Validate(block).IsValid);
    }

    [Fact]
    public void Detector_Rejects_A_Body_With_No_Data_At_All()
        => Assert.False(new ChartDetector().Validate(":::chart type=\"bar\"\njust prose\n:::").IsValid);

    // ---- DOCX ---------------------------------------------------------------------------------

    private static (string Document, Dictionary<string, string> Parts) Export(string markdown)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-chart-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(markdown, path, new AppSettings())
                .GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var parts = new Dictionary<string, string>();
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".xml")) continue;
                using var r = new StreamReader(e.Open());
                parts[e.FullName] = r.ReadToEnd();
            }
            return (parts["word/document.xml"], parts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Docx_Emits_A_Native_Chart_Part_Not_Raw_Text()
    {
        var (doc, parts) = Export(NoHeader);
        Assert.DoesNotContain("Alpha,10", doc);
        Assert.Contains(parts.Keys, k => k.StartsWith("word/charts/chart"));
    }

    [Fact]
    public void Docx_Keeps_The_First_Row_When_There_Is_No_Header()
    {
        var (_, parts) = Export(NoHeader);
        var chart = parts.First(p => p.Key.StartsWith("word/charts/chart")).Value;
        Assert.Contains("<c:v>Alpha</c:v>", chart);   // was silently dropped as a "header"
        Assert.Contains("<c:v>Gamma</c:v>", chart);
    }

    [Fact]
    public void Docx_Still_Skips_A_Real_Header_Row()
    {
        var (_, parts) = Export(WithHeader);
        var chart = parts.First(p => p.Key.StartsWith("word/charts/chart")).Value;
        Assert.DoesNotContain("<c:v>Quarter</c:v>", chart);
        Assert.Contains("<c:v>Q1</c:v>", chart);
    }

    [Fact]
    public void Docx_Axes_Are_Not_Deleted_So_Word_Draws_The_Labels()
    {
        var (_, parts) = Export(NoHeader);
        var chart = parts.First(p => p.Key.StartsWith("word/charts/chart")).Value;
        // Without an explicit c:delete Word treats the axis as deleted and omits every label.
        Assert.Equal(2, chart.Split("<c:delete val=\"0\" />").Length - 1);
    }

    // ---- HTML preview -------------------------------------------------------------------------

    private static string RenderHtml(string markdown) =>
        new MarkdownHtmlService().Render(markdown, new AppSettings(),
            AppServices.Themes.GetOrDefault("GitHub Light"));

    [Fact]
    public void Preview_Renders_A_Chart_Instead_Of_Printing_The_Data()
    {
        var html = RenderHtml(NoHeader);
        Assert.Contains("ms-chart", html);
        Assert.DoesNotContain("Alpha,10", html);
        Assert.Contains("Alpha", html);   // as an axis label
    }

    [Theory]
    [InlineData("bar", "<rect")]
    [InlineData("line", "<polyline")]
    [InlineData("pie", "<path")]
    public void Preview_Draws_The_Requested_Form(string kind, string mark)
    {
        var html = RenderHtml($":::chart type=\"{kind}\"\nA,3\nB,6\nC,9\n:::");
        Assert.Contains($"ms-chart-{kind}", html);
        Assert.Contains(mark, html);
    }

    [Fact]
    public void Preview_Uses_A_Single_Hue_For_A_Single_Series()
    {
        // Colour carries identity only in the pie; a bar chart's categories are named on the axis,
        // so eight hues for one series would be meaningless.
        var html = RenderHtml(NoHeader);
        var start = html.IndexOf("ms-chart-bar", StringComparison.Ordinal);
        var svg = html[start..html.IndexOf("</svg>", start, StringComparison.Ordinal)];
        var fills = System.Text.RegularExpressions.Regex
            .Matches(svg, "<rect[^>]*fill=\"(#[0-9a-fA-F]{6})\"")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Single(fills);
    }

    [Fact]
    public void Preview_Leaves_A_Chart_Fence_Inside_A_Code_Block_Alone()
    {
        var html = RenderHtml("```\n:::chart type=\"bar\"\nA,1\n:::\n```");
        Assert.DoesNotContain("ms-chart", html);
    }
}
