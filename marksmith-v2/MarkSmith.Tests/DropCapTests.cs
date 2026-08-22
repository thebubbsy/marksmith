using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DropCapTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R4 - Editorial Drop Caps)
    // =========================================================================

    [Fact]
    public async Task T1_01_Default_Drop_Cap_Emits_FramePr_Lines_3()
    {
        var md = @":::dropcap
Once upon a time in a distant kingdom, there lived a legendary software engineer.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("w:dropCap=\"drop\"", docXml);
            Assert.Contains("w:lines=\"3\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Custom_Drop_Cap_Line_Count()
    {
        var md = @":::dropcap 4
In the beginning, the cosmos was formed out of swirling dust and raw elements.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("w:lines=\"4\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_03_Html_Preview_Renders_Dropcap_Css_FirstLetter()
    {
        var md = @":::dropcap 3
Technology moves fast, but core engineering principles remain invariant.
:::
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("dropcap", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Technology moves fast", html);
    }

    [Fact]
    public async Task T1_04_Docx_Emits_DropCapLocation_And_WrapAround()
    {
        var md = @":::dropcap lines=3
Chapter one opens with a dramatic exploration of quantum computation.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("w:wrap=\"around\"", docXml);
            Assert.Contains("w:dropCap=\"drop\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_05_Paragraph_Text_Preserved_After_DropCap()
    {
        var md = @":::dropcap
Marksmith is designed for professional technical authors who demand pixel perfection.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("arksmith is designed for professional", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R4)
    // =========================================================================

    [Fact]
    public async Task T2_01_Drop_Cap_With_Leading_Quotation_Mark()
    {
        var md = @":::dropcap
""The journey of a thousand miles begins with a single step,"" wrote the ancient philosopher.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Drop_Cap_With_Unicode_Accented_Character()
    {
        var md = @":::dropcap
Érase una vez en un pueblo lejano donde los programadores creaban mundos virtuales.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("É", docXml);
            Assert.Contains("rase una vez", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Single_Character_Paragraph_DropCap()
    {
        var md = @":::dropcap
A
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("A", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Drop_Cap_With_Large_Line_Count()
    {
        var md = @":::dropcap lines=8
Massive illuminated initial capital letter designed for antique manuscripts.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("w:lines=\"8\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Drop_Cap_Paragraph_With_Inline_Formatting()
    {
        var md = @":::dropcap
**Bold** beginnings lead to *remarkable* outcomes when backed by `rigorous` execution.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.True(docXml.Contains("<w:b/>") || docXml.Contains("<w:b />") || docXml.Contains("<w:b"));
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
    public async Task T3_01_Drop_Cap_With_TrackChanges_Additions_And_Deletions()
    {
        var md = @":::dropcap
Ancient manuscripts were {--copied manually by scribes--}{++typeset using movable type++}.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Drop_Cap_In_Document_With_Line_Numbers_And_Watermark()
    {
        var md = @":::watermark ""DRAFT PUBLICATION""

:::line-numbers count-by=5

:::dropcap
Scholarly investigations reveal substantial historic precedents in typography.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("<w:framePr", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
