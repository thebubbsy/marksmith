using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Tests;

// Advanced .dotx house-style extraction: importing a template inherits page layout, custom
// margins, multi-column definitions AND complex headers/footers (with their images) — not just
// colors and fonts — and DOCX export replays that geometry on every converted document.
public class HouseLayoutTests
{
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static void SetAttr(OpenXmlElement el, string name, string value) =>
        el.SetAttribute(new OpenXmlAttribute("w", name, WNs, value));

    /// <summary>Builds a corporate template: 15552x21008 twips page, custom margins, 2 columns,
    /// a header with text + a PNG logo, and a footer with page text.</summary>
    private static string CreateLayoutDotx(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Template);
        var main = doc.AddMainDocumentPart();

        var pgMar = new W.PageMargin();
        SetAttr(pgMar, "top", "1440");
        SetAttr(pgMar, "right", "720");
        SetAttr(pgMar, "bottom", "1800");
        SetAttr(pgMar, "left", "1080");
        SetAttr(pgMar, "header", "600");
        SetAttr(pgMar, "footer", "650");
        SetAttr(pgMar, "gutter", "0");

        main.Document = new W.Document(new W.Body(
            new W.Paragraph(new W.Run(new W.Text("body"))),
            new W.SectionProperties(
                new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = "rIdHdr" },
                new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = "rIdFtr" },
                new W.PageSize { Width = 15552, Height = 21008 },
                pgMar,
                new W.Columns { ColumnCount = 2, Space = "360", EqualWidth = true })));

        var headerPart = main.AddNewPart<HeaderPart>("rIdHdr");
        headerPart.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text("CONFIDENTIAL"))));
        var imgPart = headerPart.AddImagePart("image/png", "rIdImg1");
        using (var ms = new MemoryStream(CreatePng())) imgPart.FeedData(ms);

        var footerPart = main.AddNewPart<FooterPart>("rIdFtr");
        footerPart.Footer = new W.Footer(new W.Paragraph(new W.Run(new W.Text("Page "))));

        doc.Save();
        return path;
    }

    private static byte[] CreatePng()
    {
        using var bmp = new SkiaSharp.SKBitmap(4, 4);
        using (var c = new SkiaSharp.SKCanvas(bmp)) c.Clear(SkiaSharp.SKColors.Red);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    [Fact]
    public void ParseLayout_extracts_page_geometry_margins_columns_and_header_footer()
    {
        string dotx = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.dotx");
        try
        {
            CreateLayoutDotx(dotx);
            var layout = TemplateThemeService.ParseLayout(dotx);

            Assert.Equal(15552u, layout.PageWidthTwips);
            Assert.Equal(21008u, layout.PageHeightTwips);
            Assert.Equal(1440, layout.MarginTop);
            Assert.Equal(720, layout.MarginRight);
            Assert.Equal(1800, layout.MarginBottom);
            Assert.Equal(1080, layout.MarginLeft);
            Assert.Equal(600, layout.HeaderDistance);
            Assert.Equal(650, layout.FooterDistance);
            Assert.Equal(2, layout.ColumnCount);
            Assert.Equal(360, layout.ColumnSpace);

            // Complex header/footer: verbatim XML.
            Assert.Contains("CONFIDENTIAL", layout.HeaderXml);
            Assert.Contains("Page", layout.FooterXml);
            Assert.False(layout.IsEmpty);
        }
        finally { if (File.Exists(dotx)) File.Delete(dotx); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_applies_house_layout()
    {
        string dotx = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.dotx");
        string docx = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.docx");
        try
        {
            CreateLayoutDotx(dotx);
            var layout = TemplateThemeService.ParseLayout(dotx);
            var settings = new AppSettings
            {
                Theme = "GitHub Light",
                ShowAttribution = false,
                BrandTemplatePath = dotx,
                BrandLayout = layout,
            };

            await new DocxExportService().ExportAsync("# Title\n\nHello from the house style.", docx, settings);

            using var doc = WordprocessingDocument.Open(docx, false);
            var main = doc.MainDocumentPart!;
            var sectPr = main.Document.Body!.Elements<W.SectionProperties>().Last();

            // Page geometry inherited from the template.
            var pgSz = sectPr.Elements<W.PageSize>().First();
            Assert.Equal(15552u, pgSz.Width!.Value);
            Assert.Equal(21008u, pgSz.Height!.Value);

            var pgMar = sectPr.Elements<W.PageMargin>().First();
            Assert.Equal("1440", pgMar.GetAttribute("top", WNs).Value);
            Assert.Equal("720", pgMar.GetAttribute("right", WNs).Value);
            Assert.Equal("1800", pgMar.GetAttribute("bottom", WNs).Value);
            Assert.Equal("1080", pgMar.GetAttribute("left", WNs).Value);
            Assert.Equal("600", pgMar.GetAttribute("header", WNs).Value);
            Assert.Equal("650", pgMar.GetAttribute("footer", WNs).Value);

            // Multi-column definition.
            var cols = sectPr.Elements<W.Columns>().First();
            Assert.Equal(2, cols.ColumnCount!.Value);
            Assert.Equal("360", cols.Space!.Value);

            // Complex header/footer: the template's own content, with its image copied under the
            // same relationship id so the logo actually renders.
            var headerPart = main.HeaderParts.Single();
            Assert.Contains("CONFIDENTIAL", headerPart.Header!.OuterXml);
            Assert.Contains(headerPart.Parts, p => p.OpenXmlPart is ImagePart && p.RelationshipId == "rIdImg1");

            var footerPart = main.FooterParts.Single();
            Assert.Contains("Page", footerPart.Footer!.OuterXml);

            Assert.Empty(new OpenXmlValidator().Validate(doc).ToList());
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
            if (File.Exists(docx)) File.Delete(docx);
        }
    }

    [Fact]
    public void Export_without_house_layout_keeps_default_geometry()
    {
        string docx = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync("# T", docx, new AppSettings { A4FixedWidth = false }).GetAwaiter().GetResult();
            using var doc = WordprocessingDocument.Open(docx, false);
            var main = doc.MainDocumentPart!;
            var sectPr = main.Document.Body!.Elements<W.SectionProperties>().Last();
            var pgSz = sectPr.Elements<W.PageSize>().First();
            Assert.Equal(12240u, pgSz.Width!.Value); // Letter default
            Assert.Equal(15840u, pgSz.Height!.Value);
            Assert.Empty(sectPr.Elements<W.Columns>());
            // Generated "Page X of Y" footer, not a template one.
            Assert.Contains("PAGE", main.FooterParts.Single().Footer!.OuterXml);
        }
        finally { if (File.Exists(docx)) File.Delete(docx); }
    }
}
