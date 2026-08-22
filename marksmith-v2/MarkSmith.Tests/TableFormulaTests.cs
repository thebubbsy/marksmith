using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class TableFormulaTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R10 - Native Word Table Formulas)
    // =========================================================================

    [Fact]
    public async Task T1_01_Sum_Above_Calculation_In_Docx_And_Html()
    {
        var md = @"| Quarter | Revenue |
| :--- | ---: |
| Q1 | 1000 |
| Q2 | 2000 |
| Q3 | 3000 |
| Total | =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:fldSimple", docXml);
            Assert.Contains("=SUM(ABOVE)", docXml);
            Assert.Contains("6000", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("6000", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Average_Left_Calculation_In_Docx_And_Html()
    {
        var md = @"| Region | Jan | Feb | Mar | Average |
| :--- | ---: | ---: | ---: | ---: |
| North | 10 | 20 | 30 | =AVERAGE(LEFT) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:fldSimple", docXml);
            Assert.Contains("=AVERAGE(LEFT)", docXml);
            Assert.Contains("20", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("20", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Count_Min_Max_Table_Formulas()
    {
        var md = @"| Item | Score |
| :--- | ---: |
| Test 1 | 80 |
| Test 2 | 95 |
| Test 3 | 65 |
| Count | =COUNT(ABOVE) |
| Min | =MIN(ABOVE) |
| Max | =MAX(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("=COUNT(ABOVE)", docXml);
            Assert.Contains("=MIN(ABOVE)", docXml);
            Assert.Contains("=MAX(ABOVE)", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_Currency_Formatting_Switch_In_Formula()
    {
        var md = @"| Service | Amount |
| :--- | ---: |
| Hosting | $1,500.00 |
| Support | $500.00 |
| Total | =SUM(ABOVE) ""$#,##0.00"" |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("=SUM(ABOVE)", docXml);
            Assert.Contains("2,000.00", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("2,000.00", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_05_Docx_Emits_fldSimple_And_SettingsXml_UpdatesFields()
    {
        var md = @"| A | B | Total |
| ---: | ---: | ---: |
| 5 | 10 | =SUM(LEFT) |
";
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

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R10)
    // =========================================================================

    [Fact]
    public async Task T2_01_Formulas_With_Formatted_Currency_Inputs()
    {
        var md = @"| Category | Cost |
| :--- | ---: |
| Software | €1,200.50 |
| Hardware | €2,799.50 |
| Sum | =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("4000", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Formulas_With_Empty_Or_NonNumeric_Cells()
    {
        var md = @"| Phase | Hours |
| :--- | ---: |
| Design | 40 |
| Review | N/A |
| Build | 80 |
| Total | =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("120", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Formula_With_Zero_Predecessor_Cells()
    {
        var md = @"| Header |
| =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("0", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Multiple_Formulas_Row_And_Column_In_Same_Table()
    {
        var md = @"| Product | Unit Price | Units | Total |
| :--- | ---: | ---: | ---: |
| Widget A | 10 | 5 | =PRODUCT(LEFT) |
| Widget B | 20 | 3 | =PRODUCT(LEFT) |
| Overall Sum | | | =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("=PRODUCT(LEFT)", docXml);
            Assert.Contains("=SUM(ABOVE)", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Negative_Numbers_In_Parentheses_And_Minus()
    {
        var md = @"| Transaction | Net |
| :--- | ---: |
| Inflow | 500 |
| Outflow | -200 |
| Refund | (100) |
| Net Balance | =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("200", docXml);
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
    public async Task T3_01_Table_Formulas_Inside_Parallel_Columns()
    {
        var md = @":::parallel ""English Calculation"" | ""Calcul Français""
| Subtotal | 100 |
| Tax | 20 |
| Total | =SUM(ABOVE) |
===
| Sous-total | 100 |
| Taxe | 20 |
| Total | =SUM(ABOVE) |
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("=SUM(ABOVE)", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Financial_Table_With_Watermark_And_CoverPage()
    {
        var md = @":::cover-page
title: Q4 Financial Audited Statement
author: Corporate Controller
:::

:::watermark ""AUDITED FINANCIALS""

# Income Statement

| Line Item | 2025 | 2026 | Variance |
| :--- | ---: | ---: | ---: |
| Gross Revenue | 100000 | 125000 | 25000 |
| Total | =SUM(ABOVE) | =SUM(ABOVE) | =SUM(ABOVE) |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("=SUM(ABOVE)", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
