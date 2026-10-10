using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Mermaid;
using SkiaSharp;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Word export on paper, part 2 (routine run #61): pictures, diagrams and tables, checked by
/// printing real exports through Word. Pictures were capped at a fixed 460pt (wider than A4's
/// text), never fitted a table cell or a column, and a tall one ran off the page and stranded its
/// heading; an SVG saved with a byte-order mark printed as "[Image: ...]"; short tables split
/// after one row.
/// </summary>
public class DocxPicturesTests : IDisposable
{
    private const long EmuPerTwip = 635;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ms-pics-{Guid.NewGuid():N}");

    public DocxPicturesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private string Png(string name, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.SteelBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path.Replace('\\', '/');
    }

    private sealed class Exported : IDisposable
    {
        private readonly string _path;
        public WordprocessingDocument Doc { get; }
        public W.Body Body => Doc.MainDocumentPart!.Document.Body!;

        public Exported(string md, AppSettings settings)
        {
            _path = Path.Combine(Path.GetTempPath(), $"ms-pics-{Guid.NewGuid():N}.docx");
            new DocxExportService().ExportAsync(md, _path, settings).GetAwaiter().GetResult();
            Doc = WordprocessingDocument.Open(_path, false);
        }

        public List<DW.Inline> Pictures => Body.Descendants<DW.Inline>().ToList();

        public void Dispose()
        {
            Doc.Dispose();
            try { File.Delete(_path); } catch { /* temp */ }
        }
    }

    private static AppSettings A4() => new() { A4FixedWidth = true };

    [Fact]
    public void A_wide_picture_fits_between_the_margins_of_an_A4_page()
    {
        var settings = A4();
        using var x = new Exported($"![wide](<{Png("wide.png", 1800, 1000)}>)", settings);
        var pic = Assert.Single(x.Pictures);
        Assert.True(pic.Extent!.Cx!.Value <= DocxExportService.TextWidthTwips(settings) * EmuPerTwip,
            $"{pic.Extent.Cx.Value} EMU is wider than the text");
        Assert.InRange((double)pic.Extent.Cx.Value / pic.Extent.Cy!.Value, 1.78, 1.82); // aspect kept
        var frame = pic.Descendants<A.Extents>().First();
        Assert.Equal(pic.Extent.Cx.Value, frame.Cx!.Value);
        Assert.Equal(pic.Extent.Cy.Value, frame.Cy!.Value);
    }

    [Fact]
    public void A_tall_picture_leaves_room_on_its_page_for_the_heading_above_it()
    {
        var settings = A4();
        using var x = new Exported($"## Phone\n\n![tall](<{Png("tall.png", 700, 2000)}>)", settings);
        var pic = Assert.Single(x.Pictures);
        var maxCy = (DocxExportService.TextHeightTwips(settings) - 1440) * EmuPerTwip;
        Assert.True(pic.Extent!.Cy!.Value <= maxCy, $"{pic.Extent.Cy.Value} EMU runs off the page");
        Assert.InRange((double)pic.Extent.Cy.Value / pic.Extent.Cx!.Value, 2.80, 2.92);
    }

    [Fact]
    public void A_picture_in_a_table_fits_its_cell()
    {
        using var x = new Exported(
            $"| Preview | What |\n|---|---|\n| ![thumb](<{Png("thumb.png", 1800, 1000)}>) | The dashboard. |", A4());
        var pic = Assert.Single(x.Pictures);
        var cell = pic.Ancestors<W.TableCell>().First();
        var cellWidth = int.Parse(cell.TableCellProperties!.TableCellWidth!.Width!.Value!);
        Assert.True(pic.Extent!.Cx!.Value <= (cellWidth - 280) * EmuPerTwip,
            $"{pic.Extent.Cx.Value / EmuPerTwip} twips in a {cellWidth}-twip cell");
        // The picture's column got a real share of the table, not the width of its alt text.
        Assert.True(cellWidth > DocxExportService.TextWidthTwips(A4()) / 3);
    }

    [Fact]
    public void A_picture_in_a_column_fits_the_column()
    {
        var settings = A4();
        using var x = new Exported(
            $":::columns\n![wide](<{Png("col.png", 1800, 1000)}>)\n\n===\n\nThe other column.\n:::", settings);
        var pic = Assert.Single(x.Pictures);
        var column = (DocxExportService.TextWidthTwips(settings) - 720) / 2;
        Assert.True(pic.Extent!.Cx!.Value <= column * EmuPerTwip);
    }

    [Fact]
    public void An_svg_saved_with_a_byte_order_mark_is_embedded_as_a_picture()
    {
        var path = Path.Combine(_dir, "chart.svg");
        File.WriteAllText(path,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"900\" height=\"400\"><rect width=\"900\" height=\"400\" fill=\"#2d6cdf\"/></svg>",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var x = new Exported($"![chart](<{path.Replace('\\', '/')}>)", A4());
        Assert.Single(x.Pictures);
        Assert.Contains(x.Doc.MainDocumentPart!.ImageParts, p => p.ContentType == "image/svg+xml");
        Assert.DoesNotContain("[Image", x.Body.InnerText);
    }

    // ---- Tables ----

    private static List<bool> RowsKeptWithNext(W.Table table) => table.Elements<W.TableRow>()
        .Select(r => r.Descendants<W.Paragraph>().All(p => p.ParagraphProperties?.KeepNext is not null))
        .ToList();

    [Fact]
    public void A_short_table_moves_to_the_next_page_whole()
    {
        using var x = new Exported("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n| 5 | 6 |", A4());
        var kept = RowsKeptWithNext(x.Body.Descendants<W.Table>().First());
        Assert.Equal(new[] { true, true, true, false }, kept);
    }

    [Fact]
    public void A_long_table_keeps_its_header_with_the_first_row_and_its_last_row_with_the_one_before()
    {
        var md = "| # | Item |\n|---|---|\n" + string.Concat(Enumerable.Range(1, 20).Select(i => $"| {i} | row {i} |\n"));
        using var x = new Exported(md, A4());
        var kept = RowsKeptWithNext(x.Body.Descendants<W.Table>().First());
        Assert.Equal(21, kept.Count);
        Assert.True(kept[0] && kept[1] && kept[19]);
        Assert.False(kept[20]);
        Assert.False(kept[10]); // the middle may break
    }

    [Fact]
    public void An_html_table_fits_the_text_width_with_fixed_columns()
    {
        var settings = A4();
        using var x = new Exported(
            "<table><tr><th>Region</th><th>Q1</th><th>Q2</th><th>Q3</th><th>Q4</th><th>Total</th><th>Notes</th></tr>" +
            "<tr><td>Europe, Middle East and Africa</td><td>904</td><td>977</td><td>1,020</td><td>1,111</td><td>4,012</td>" +
            "<td>https://example.com/reports/2026/units-by-region/final/export.csv</td></tr></table>\n\nAfter.", settings);
        var table = x.Body.Descendants<W.Table>().First();
        var grid = table.GetFirstChild<W.TableGrid>()!.Elements<W.GridColumn>().Select(g => int.Parse(g.Width!.Value!)).ToList();
        Assert.Equal(DocxExportService.TextWidthTwips(settings), grid.Sum());
        Assert.Equal(W.TableLayoutValues.Fixed, table.GetFirstChild<W.TableProperties>()!.TableLayout!.Type!.Value);
        Assert.IsType<W.Paragraph>(table.NextSibling()); // Word would join it to a following table
    }

    // ---- Diagrams ----

    [Fact]
    public void Chart_slices_are_distinct_bright_colours_even_when_the_theme_heading_is_near_black()
    {
        var theme = new ThemeDefinition("GitHub-ish", "#ffffff", "#24292f", "#1f2328", "#f6f8fa", "#d0d7de",
            "#0969da", "#f6f8fa", "#d0d7de");
        var palette = MermaidChartsRenderer.BuildPalette(theme);
        Assert.Equal(8, palette.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var hex in palette)
        {
            var v = Convert.ToInt32(hex.TrimStart('#'), 16);
            int r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
            Assert.True(Math.Max(r, Math.Max(g, b)) >= 150, $"{hex} is muddy");
            Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 80, $"{hex} is greyish");
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0 0", false)]
    [InlineData("0px", false)]
    [InlineData("none", false)]
    [InlineData("4 2", true)]
    [InlineData("3", true)]
    public void Only_a_real_dash_pattern_makes_a_diagram_line_dashed(string dasharray, bool dashed)
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
                  "<rect x=\"10\" y=\"10\" width=\"40\" height=\"30\" fill=\"#eeeeee\" stroke=\"#333333\"/>" +
                  "<rect x=\"150\" y=\"10\" width=\"40\" height=\"30\" fill=\"#eeeeee\" stroke=\"#333333\"/>" +
                  $"<path d=\"M50 25 L150 25\" fill=\"none\" stroke=\"#333333\" stroke-dasharray=\"{dasharray}\"/></svg>";
        var diagram = SvgShapeForge.Parse(svg);
        Assert.NotNull(diagram);
        var edge = Assert.Single(diagram!.Edges);
        Assert.Equal(dashed, edge.Dashed);
    }
}
