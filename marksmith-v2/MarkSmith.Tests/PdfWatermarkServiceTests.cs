using System.IO;
using MarkSmith.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace MarkSmith.Core.Tests;

public class PdfWatermarkServiceTests
{
    private static byte[] CreateMinimalPdf()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Apply_ReturnsSameBytes_WhenPdfBytesNullOrEmpty()
    {
        var result1 = PdfWatermarkService.Apply(null!, new PdfWatermarkOptions());
        Assert.Empty(result1);

        var empty = System.Array.Empty<byte>();
        var result2 = PdfWatermarkService.Apply(empty, new PdfWatermarkOptions());
        Assert.Same(empty, result2);
    }

    [Fact]
    public void Apply_ReturnsSameBytes_WhenOptionsNullOrTextBlank()
    {
        var pdf = CreateMinimalPdf();
        var result1 = PdfWatermarkService.Apply(pdf, null!);
        Assert.Same(pdf, result1);

        var result2 = PdfWatermarkService.Apply(pdf, new PdfWatermarkOptions { Text = "   " });
        Assert.Same(pdf, result2);
    }

    [Fact]
    public void Apply_AppliesWatermarkToPdf_Successfully()
    {
        var pdf = CreateMinimalPdf();
        var options = new PdfWatermarkOptions
        {
            Text = "CONFIDENTIAL TEST",
            FontSize = 48.0,
            Opacity = 0.2,
            RotationAngle = 45.0,
            ColorHex = "#FF0000"
        };

        var watermarkedPdf = PdfWatermarkService.Apply(pdf, options);

        Assert.NotNull(watermarkedPdf);
        Assert.True(watermarkedPdf.Length > 0, "Watermarked PDF bytes must be non-empty");

        using var ms = new MemoryStream(watermarkedPdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
    }
}
