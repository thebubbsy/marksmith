using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class WatermarkTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R2 - Vector Watermarks)
    // =========================================================================

    [Fact]
    public async Task T1_01_Default_Watermark_Creates_Header_And_Reference()
    {
        var md = ":::watermark \"CONFIDENTIAL\"\n\n# Document Title\n\nBody content of confidential document.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:headerReference", docXml);

            // Watermark resides in header1.xml
            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("CONFIDENTIAL", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Custom_Color_And_Opacity_Watermark()
    {
        var md = ":::watermark \"DRAFT\" color=\"#FF0000\" opacity=\"0.25\"\n\n# Project Draft\n\nPreliminary specifications.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("DRAFT", headerXml);
            Assert.True(headerXml.Contains("FF0000", StringComparison.OrdinalIgnoreCase) || headerXml.Contains("red", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Horizontal_Orientation_Watermark()
    {
        var md = ":::watermark \"INTERNAL ONLY\" diagonal=false\n\n# Policy\n\nInternal operating procedure.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("INTERNAL ONLY", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_04_Html_Preview_Renders_Watermark_Overlay()
    {
        var md = ":::watermark \"RESTRICTED\" color=\"#CC0000\" opacity=\"0.15\"\n\n# System Architecture\n\nConfidential details.";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("RESTRICTED", html);
        Assert.True(html.Contains("watermark", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T1_05_Docx_Package_Uses_Dynamic_Relationship_Ids()
    {
        var md = ":::watermark \"TOP SECRET\"\n\n# Defense Analysis\n\nSensitive materials.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            using var doc = WordprocessingDocument.Open(docxPath, false);
            var main = doc.MainDocumentPart!;
            var headerParts = main.HeaderParts.ToList();
            Assert.NotEmpty(headerParts);

            var headerPart = headerParts.First();
            var relId = main.GetIdOfPart(headerPart);
            Assert.False(string.IsNullOrEmpty(relId));

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains($"r:id=\"{relId}\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R2)
    // =========================================================================

    [Fact]
    public async Task T2_01_Watermark_With_Special_Xml_Characters()
    {
        var md = ":::watermark \"CONFIDENTIAL & RESTRICTED <TOP SECRET>\"\n\n# Escaped Watermark\n\nContent.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("CONFIDENTIAL &amp; RESTRICTED &lt;TOP SECRET&gt;", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Watermark_With_Long_Text_String()
    {
        var longText = "CLASSIFIED - DO NOT DISTRIBUTE - FOR AUTHORIZED EYES ONLY - PROPERTY OF MARKSMITH CORPORATION";
        var md = $":::watermark \"{longText}\"\n\n# Security Briefing\n\nLong watermark text stress test.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("CLASSIFIED", headerXml);
            Assert.Contains("MARKSMITH CORPORATION", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Watermark_With_Multilingual_Unicode()
    {
        var md = ":::watermark \"機密 - CONFIDENTIEL\"\n\n# International Treaty\n\nMultilingual text preservation.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("機密", headerXml);
            Assert.Contains("CONFIDENTIEL", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Empty_Or_Whitespace_Watermark_Handled_Safely()
    {
        var md = ":::watermark \"   \"\n\n# Normal Title\n\nDocument with blank watermark.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("Normal Title", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Watermark_With_Extreme_Opacity_Values()
    {
        var md = ":::watermark \"FAINT\" opacity=\"0.01\"\n\n# Faint Watermark\n\nTesting edge opacity.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("FAINT", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // Tier 3: Cross-Feature Interactions
    // =========================================================================

    [Fact]
    public async Task T3_01_Watermark_With_Cover_Page_Unlinks_Cover_Header()
    {
        var md = @":::cover-page
title: Annual Financial Review
author: CFO Office
date: 2026-08-23
:::

:::watermark ""CONFIDENTIAL""

# Executive Summary

Body page content with watermark.";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:headerReference", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Watermark_With_Complex_Tables_And_CodeBlocks()
    {
        var md = @":::watermark ""DO NOT COPY""

# Technical Appendix

| Metric | Threshold | Status |
| :--- | :---: | ---: |
| Latency | < 50ms | PASS |
| Memory | < 512MB | PASS |

```csharp
public void ProcessSecurityToken()
{
    var token = Auth.Generate();
}
```
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("ProcessSecurityToken", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
