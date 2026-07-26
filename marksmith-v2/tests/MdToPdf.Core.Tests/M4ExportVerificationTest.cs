using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class M4ExportVerificationTest
{
    private class TestSkiaWebRenderHost : IWebRenderHost
    {
        private string _currentHtml = string.Empty;

        public Task<bool> EnsureReadyAsync() => Task.FromResult(true);

        public Task NavigateToStringAsync(string html)
        {
            _currentHtml = html;
            return Task.CompletedTask;
        }

        public Task<string?> ExecuteScriptAsync(string javaScript) => Task.FromResult<string?>("true");

        public Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
        {
            string plainText = System.Text.RegularExpressions.Regex.Replace(_currentHtml, "<.*?>", string.Empty);
            plainText = System.Net.WebUtility.HtmlDecode(plainText);
            string[] lines = plainText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            float widthPts = (float)((setup.PageWidthIn > 0 ? setup.PageWidthIn : 8.27) * 72);
            float heightPts = (float)((setup.PageHeightIn > 0 ? setup.PageHeightIn : 11.69) * 72);

            float marginLeft = (float)((setup.MarginLeftIn >= 0 ? setup.MarginLeftIn : 0.39) * 72);
            float marginTop = (float)((setup.MarginTopIn >= 0 ? setup.MarginTopIn : 0.39) * 72);
            float marginRight = (float)((setup.MarginRightIn >= 0 ? setup.MarginRightIn : 0.39) * 72);
            float marginBottom = (float)((setup.MarginBottomIn >= 0 ? setup.MarginBottomIn : 0.39) * 72);

            using var stream = File.Create(outputPath);
            using var document = SkiaSharp.SKDocument.CreatePdf(stream);
            using var paint = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.Black,
                TextSize = 12f,
                IsAntialias = true,
                Typeface = SkiaSharp.SKTypeface.FromFamilyName("Sans-Serif")
            };

            float lineHeight = paint.TextSize * 1.4f;
            SkiaSharp.SKCanvas canvas = document.BeginPage(widthPts, heightPts);
            float currentY = marginTop + paint.TextSize;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    currentY += lineHeight * 0.5f;
                    continue;
                }

                canvas.DrawText(line, marginLeft, currentY, paint);
                currentY += lineHeight;
            }

            document.EndPage();
            document.Close();

            return Task.FromResult(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0);
        }

        public Task BeginHarvestAsync() => Task.CompletedTask;
        public Task EndHarvestAsync() => Task.CompletedTask;
    }

    [Fact(Skip="SkiaSharp missing in test environment")]
    public async Task VerifyPdfAndDocxExportGeneration()
    {
        string exportDir = Path.Combine(AppContext.BaseDirectory, "ExportArtifacts");
        Directory.CreateDirectory(exportDir);

        string pdfPath = Path.Combine(exportDir, "MarkSmith_test_export.pdf");
        string docxPath = Path.Combine(exportDir, "MarkSmith_test_export.docx");

        string markdownContent = @"# MarkSmith Mobile E2E Export Verification Test

## Executive Summary
This document verifies genuine export functionality for MarkSmith Mobile on net8.0-android.

### Features Tested
- **Markdown Headers**: H1, H2, H3
- **Tables & Lists**:
  | Feature | Status |
  | --- | --- |
  | PDF Export | PASS |
  | DOCX Export | PASS |
- **Formatting**: *Italics*, **Bold**, `Inline Code`
";

        // 1. Export DOCX
        var docxService = new DocxExportService();
        await docxService.ExportAsync(markdownContent, docxPath, new AppSettings());

        Assert.True(File.Exists(docxPath), $"DOCX file should exist at {docxPath}");
        var docxFileInfo = new FileInfo(docxPath);
        Assert.True(docxFileInfo.Length > 0, $"DOCX file size should be > 0 bytes (actual: {docxFileInfo.Length})");

        byte[] docxHeader = new byte[4];
        using (var fs = File.OpenRead(docxPath))
        {
            fs.Read(docxHeader, 0, 4);
        }
        // ZIP magic bytes PK\x03\x04
        Assert.Equal(0x50, docxHeader[0]);
        Assert.Equal(0x4B, docxHeader[1]);
        Assert.Equal(0x03, docxHeader[2]);
        Assert.Equal(0x04, docxHeader[3]);

        // 2. Export PDF via TestSkiaWebRenderHost
        var htmlService = new MarkdownHtmlService();
        var htmlText = htmlService.Render(markdownContent, new AppSettings(), new ThemeCatalog().GetOrDefault("Classic Professional"), null, false);

        var webRenderHost = new TestSkiaWebRenderHost();
        var pdfService = new PdfExportService();
        await pdfService.ExportAsync(webRenderHost, htmlText, pdfPath, new AppSettings());

        Assert.True(File.Exists(pdfPath), $"PDF file should exist at {pdfPath}");
        var pdfFileInfo = new FileInfo(pdfPath);
        Assert.True(pdfFileInfo.Length > 0, $"PDF file size should be > 0 bytes (actual: {pdfFileInfo.Length})");

        byte[] pdfHeader = new byte[5];
        using (var fs = File.OpenRead(pdfPath))
        {
            fs.Read(pdfHeader, 0, 5);
        }
        // PDF magic bytes %PDF-
        string pdfMagic = Encoding.ASCII.GetString(pdfHeader);
        Assert.Equal("%PDF-", pdfMagic);
    }
}

