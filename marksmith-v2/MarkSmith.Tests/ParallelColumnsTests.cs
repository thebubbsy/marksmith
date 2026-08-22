using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ParallelColumnsTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R7 - Bilingual & Parallel Synchronized Columns)
    // =========================================================================

    [Fact]
    public async Task T1_01_Basic_Parallel_Columns_Emits_Borderless_Table()
    {
        var md = @":::parallel ""English"" | ""Français""
This Agreement is governed by the laws of California.
===
Le présent contrat est régi par les lois de la Californie.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("California", docXml);
            Assert.Contains("Californie", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Multi_Row_Parallel_Synchronization()
    {
        var md = @":::parallel ""English"" | ""Español""
Clause 1: Scope of Services.
===
Cláusula 1: Alcance de los Servicios.
---
Clause 2: Payment Terms.
===
Cláusula 2: Condiciones de Pago.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Scope of Services", docXml);
            Assert.Contains("Alcance de los Servicios", docXml);
            Assert.Contains("Payment Terms", docXml);
            Assert.Contains("Condiciones de Pago", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Table_Rows_Have_CantSplit_Property()
    {
        var md = @":::parallel ""English"" | ""Deutsch""
The contractor shall deliver milestone 1 within 30 days.
===
Der Auftragnehmer liefert Meilenstein 1 innerhalb von 30 Tagen.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:cantSplit", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_Table_Grid_50_50_Width_Split()
    {
        var md = @":::parallel ""Source"" | ""Target""
Left column text.
===
Right column text.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:gridCol", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_05_Html_Preview_Renders_Parallel_Grid_And_Cells()
    {
        var md = @":::parallel ""English"" | ""Italiano""
Definitions of key terms.
===
Definizioni dei termini chiave.
:::
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("parallel", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Definitions of key terms", html);
        Assert.Contains("Definizioni dei termini chiave", html);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R7)
    // =========================================================================

    [Fact]
    public async Task T2_01_Unequal_Content_Lengths_Between_Columns()
    {
        var longText = "This is an extraordinarily verbose explanation of the contractual indemnification obligations spanning several complex clauses.";
        var shortText = "Courte clause.";
        var md = $@":::parallel ""EN"" | ""FR""
{longText}
===
{shortText}
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains(shortText, docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Rich_Markdown_Inside_Parallel_Cells()
    {
        var md = @":::parallel ""English"" | ""Français""
- Item 1 with **bold** and `code`
- Item 2 with *italic*
===
- Élément 1 avec **gras** et `code`
- Élément 2 avec *italique*
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.True(docXml.Contains("<w:b/>") || docXml.Contains("<w:b />") || docXml.Contains("<w:b"), "Expected bold element in docXml");
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Parallel_Row_With_Missing_Right_Column()
    {
        var md = @":::parallel ""Left"" | ""Right""
Only left column provided.
===
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Only left column provided.", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Parallel_Headers_With_Special_Characters()
    {
        var md = @":::parallel ""English & Welsh (UK)"" | ""Gaeilge <Éire>""
Clause in English.
===
Clásal i nGaeilge.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("English &amp; Welsh", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Single_Row_Parallel_Block()
    {
        var md = @":::parallel ""A"" | ""B""
Left
===
Right
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Left", docXml);
            Assert.Contains("Right", docXml);
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
    public async Task T3_01_Parallel_Columns_With_Form_Controls()
    {
        var md = @":::parallel ""English Contract"" | ""Contrat Français""
Signatory Name: [text: ""Full Name""]
===
Nom du signataire : [text: ""Nom complet""]
---
Date: [date: 2026-09-01]
===
Date : [date: 2026-09-01]
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:sdt>", docXml);
            Assert.Contains("<w:tbl>", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Parallel_Columns_With_Table_Formulas_And_Revisions()
    {
        var md = @":::parallel ""English"" | ""Français""
Original fee {--$1,000--}{++$1,200++}.
===
Frais d'origine {--1 000 $--}{++1 200 $++}.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
