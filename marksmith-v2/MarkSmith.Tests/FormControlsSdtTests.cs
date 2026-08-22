using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class FormControlsSdtTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R8 - Fillable Form SDTs)
    // =========================================================================

    [Fact]
    public async Task T1_01_Dropdown_Form_Control_Emits_w_dropDownList_And_Options()
    {
        var md = "Select priority: [dropdown: High | Medium | Low]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:sdt>", docXml);
            Assert.Contains("<w:dropDownList", docXml);
            Assert.Contains("High", docXml);
            Assert.Contains("Medium", docXml);
            Assert.Contains("Low", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Date_Picker_Form_Control_Emits_w_date()
    {
        var md = "Effective Date: [date: 2026-12-31]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:sdt>", docXml);
            Assert.Contains("<w:date", docXml);
            Assert.Contains("2026-12-31", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Text_Input_Form_Control_Emits_w_text()
    {
        var md = "Authorized Representative: [text: \"Full Name\"]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:sdt>", docXml);
            Assert.Contains("<w:text", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_Tasklist_Checkbox_Form_Control_Emits_w14_checkbox()
    {
        var md = "- [ ] Unchecked requirement\n- [x] Checked requirement";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w14:checkbox", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_05_Html_Preview_Renders_Html5_Form_Controls()
    {
        var md = @"Status: [dropdown: Pending | Approved | Rejected]
Deadline: [date: 2026-10-15]
Owner: [text: ""Username""]
- [ ] Task item
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("<select", html);
        Assert.Contains("<option", html);
        Assert.Contains("Pending", html);
        Assert.Contains("type=\"date\"", html);
        Assert.Contains("type=\"text\"", html);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R8)
    // =========================================================================

    [Fact]
    public async Task T2_01_Dropdown_With_Special_Xml_Characters()
    {
        var md = "Role: [dropdown: Tom & Jerry | <None> | \"Custom Admin\"]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Tom &amp; Jerry", docXml);
            Assert.Contains("&lt;None&gt;", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Date_Picker_With_Empty_Or_Default_Date()
    {
        var md = "Submission Date: [date]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:date", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Dropdown_With_Single_Option()
    {
        var md = "Choice: [dropdown: Standard]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Standard", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Form_Controls_Inside_Table_Cells()
    {
        var md = @"| Field | Input Control |
| :--- | :--- |
| Approval Status | [dropdown: Approved | Denied] |
| Target Date | [date: 2026-11-01] |
| Sign-off | [text: ""Signature""] |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("<w:sdt>", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Multiple_Form_Controls_On_Single_Line()
    {
        var md = "User: [text: \"Name\"] Date: [date: 2026-08-23] Status: [dropdown: Active | Inactive]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.True(System.Text.RegularExpressions.Regex.Matches(docXml, "<w:sdt>").Count >= 3);
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
    public async Task T3_01_Form_Controls_Inside_Parallel_Columns()
    {
        var md = @":::parallel ""English"" | ""Français""
Select Language: [dropdown: English | Spanish]
===
Choisir la langue : [dropdown: Français | Espagnol]
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("<w:dropDownList", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Form_Controls_In_Compliance_Document_With_CoverPage()
    {
        var md = @":::cover-page
title: Regulatory Audit Questionnaire
author: Compliance Office
:::

# Section 1: Verification

- [x] Security Assessment Completed
- [ ] Penetration Testing Signed Off
Department: [dropdown: Engineering | Legal | HR]
Date: [date: 2026-08-23]
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:sdt>", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
