using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ConcordanceIndexTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R6 - Concordance & Subject Index Generator)
    // =========================================================================

    [Fact]
    public async Task T1_01_Inline_Index_Term_Emits_XE_FieldCode()
    {
        var md = "Modern microservices^[index: \"Microservices\"] decouple distributed architectures.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:fldSimple", docXml);
            Assert.Contains("XE", docXml);
            Assert.Contains("Microservices", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Hierarchical_Index_Category_Topic()
    {
        var md = "Relational storage systems^[index: \"Databases:PostgreSQL\"] provide ACID transactions.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("XE", docXml);
            Assert.Contains("Databases:PostgreSQL", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Index_Block_Emits_INDEX_FieldCode()
    {
        var md = @"# Textbook Chapter

Topic A^[index: ""Algorithms""] and Topic B^[index: ""Data Structures""].

:::index count=2
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:fldSimple", docXml);
            Assert.Contains("INDEX", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_SettingsXml_Enables_UpdateFields()
    {
        var md = "Content^[index: \"Optimization\"]\n\n:::index\n:::";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml");
            Assert.NotNull(settingsXml);
            Assert.Contains("<w:updateFields", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_05_Html_Preview_Renders_Alphabetical_Index()
    {
        var md = @"# Architecture Manual

Event sourcing^[index: ""Architecture:Event Sourcing""] enables auditability.
CQRS pattern^[index: ""Architecture:CQRS""] separates read and write models.

:::index
:::
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("index", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architecture", html);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R6)
    // =========================================================================

    [Fact]
    public async Task T2_01_Duplicate_Index_Terms_Consolidated()
    {
        var md = @"# Section 1
First occurrence of term^[index: ""Security""].

# Section 2
Second occurrence of term^[index: ""Security""].

:::index
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Security", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Index_Terms_With_Special_Xml_Characters()
    {
        var md = "Programming languages^[index: \"C++ & C# <Languages>\"] are compiled.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("C++ &amp; C# &lt;Languages&gt;", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Index_Block_With_Custom_Columns_3()
    {
        var md = @"# Comprehensive Encyclopedia

Term 1^[index: ""Alpha""]. Term 2^[index: ""Beta""]. Term 3^[index: ""Gamma""].

:::index count=3
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("INDEX", docXml);
            Assert.Contains("3", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Document_With_Index_Block_And_No_Entries()
    {
        var md = @"# Clean Document

No index entries here.

:::index
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.NotNull(html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Multilingual_And_Accented_Index_Entries()
    {
        var md = "Concepts^[index: \"Économie\"] and^[index: \"Überwachung\"].";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Économie", docXml);
            Assert.Contains("Überwachung", docXml);
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
    public async Task T3_01_Index_Terms_Inside_Table_Headers_And_Callouts()
    {
        var md = @"> [!NOTE]
> Critical definition^[index: ""Standards:ISO 9001""] for quality control.

| Protocol^[index: ""Networking:HTTP/3""] | Transport |
| :--- | :--- |
| QUIC | UDP |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Standards:ISO 9001", docXml);
            Assert.Contains("Networking:HTTP/3", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Index_In_Document_With_Cover_Page_And_Watermark()
    {
        var md = @":::cover-page
title: Advanced Algorithms Monograph
author: CS Faculty
:::

:::watermark ""PRE-RELEASE DRAFT""

# Chapter 1: Graphs

Depth-first search^[index: ""Algorithms:DFS""] and breadth-first search^[index: ""Algorithms:BFS""].

:::index count=2
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("INDEX", docXml);
            Assert.Contains("Algorithms:DFS", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
