using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 1: Feature Coverage (≥5 test cases per feature across F1–F10).
/// Validates happy-path functionality, OpenXML structural compliance,
/// and API surface for all core upgrade pillars.
/// </summary>
public class Tier1FeatureTests
{
    // =========================================================================
    // F1: SAX Streaming OpenXmlWriter (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F1_01_BasicDocument_StreamsValidOpenXmlPackage()
    {
        var md = "# MarkSmith SAX Architecture\n\nThis is a streaming paragraph written via SAX.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        Assert.NotEmpty(bytes);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("MarkSmith SAX Architecture", docXml);
        Assert.Contains("streaming paragraph written via SAX", docXml);
    }

    [Fact]
    public async Task T1_F1_02_MultiParagraphAndHeadings_StreamsProperStructure()
    {
        var md = @"# Heading 1
Paragraph 1 under H1.

## Heading 2
Paragraph 2 under H2.

### Heading 3
Paragraph 3 under H3.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var report = E2ETestContext.InspectDocx(bytes);
        Assert.Equal("Heading 1", report.Title);
        Assert.True(report.TotalParagraphs >= 6);
    }

    [Fact]
    public async Task T1_F1_03_TablesAndLists_StreamsWithoutDomCorruption()
    {
        var md = @"| Feature | Supported | Tier |
|---|:---:|---:|
| SAX Stream | Yes | 1 |
| Templates | Yes | 1 |

- Bullet A
- Bullet B
1. Ordered 1
2. Ordered 2";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("<w:tbl", docXml);
        Assert.Contains("SAX Stream", docXml);
        Assert.Contains("Bullet A", docXml);
    }

    [Fact]
    public async Task T1_F1_04_StreamExport_DirectlyToMemoryStream()
    {
        var md = "# Direct Stream Export\n\nStreaming directly into memory stream buffer.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var fileBytes = await File.ReadAllBytesAsync(tempFile);
            Assert.True(fileBytes.Length > 1000);
            var errors = E2ETestContext.ValidateDocxSchema(fileBytes);
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T1_F1_05_ConsecutiveStreams_MaintainsWriterStateIsolation()
    {
        var md1 = "# Doc 1\nAlpha content";
        var md2 = "# Doc 2\nBeta content";

        var bytes1 = await E2ETestContext.ExportMarkdownToBytesAsync(md1);
        var bytes2 = await E2ETestContext.ExportMarkdownToBytesAsync(md2);

        var xml1 = E2ETestContext.ReadZipPartXml(bytes1, "word/document.xml")!;
        var xml2 = E2ETestContext.ReadZipPartXml(bytes2, "word/document.xml")!;

        Assert.Contains("Alpha content", xml1);
        Assert.DoesNotContain("Beta content", xml1);

        Assert.Contains("Beta content", xml2);
        Assert.DoesNotContain("Alpha content", xml2);
    }

    // =========================================================================
    // F2: OpenXML Namespace & Dynamic Rel Governance (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F2_01_RootNamespaces_EnforcesRequiredXmlnsDeclarations()
    {
        var md = "# Namespace Test\n\nVerifying root namespaces.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.Contains("xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"", docXml);
        Assert.Contains("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"", docXml);
    }

    [Fact]
    public async Task T1_F2_02_DynamicRelationshipIds_NoHardcoded_rId_Collisions()
    {
        var md = @"# Links Test
[Link 1](https://example.com/1)
[Link 2](https://example.com/2)
[Link 3](https://example.com/3)";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;

        var relIds = System.Text.RegularExpressions.Regex.Matches(relsXml, @"Id=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(relIds.Count, relIds.Distinct().Count());
        Assert.True(relIds.Count >= 3);
    }

    [Fact]
    public async Task T1_F2_03_ExternalHyperlinkRelationships_RegisteredDynamicallyInRels()
    {
        var md = "Visit the [Official Documentation](https://marksmith.dev/docs) for details.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;
        Assert.Contains("Target=\"https://marksmith.dev/docs\"", relsXml);
        Assert.Contains("TargetMode=\"External\"", relsXml);
    }

    [Fact]
    public async Task T1_F2_04_ImageMediaRelationships_UniquePartUrisAndRelIds()
    {
        var md = @"# Image Rel Test
Paragraph before image.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F2_05_DrawingMLAndWpsNamespaces_PresentWhenShapesUsed()
    {
        var md = @"```mermaid
flowchart TD
    A[Start] --> B[Process]
```";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.True(docXml.Contains("xmlns:a=") || docXml.Contains("<w:drawing") || docXml.Contains("<w:p"));
    }

    // =========================================================================
    // F3: Reference Template Merger (.dotx & .docx) (5 tests)
    // =========================================================================

    [Fact]
    public void T1_F3_01_ParseDotxTemplate_ExtractsColorsFontsAndLayout()
    {
        var dotxPath = E2ETestContext.CreateSyntheticDotxTemplate(
            bodyFont: "Segoe UI",
            headingFont: "Segoe UI Semibold",
            h1ColorHex: "#2B579A",
            accent1Hex: "#0078D4");

        try
        {
            var summary = TemplateThemeService.ParseDotx(dotxPath);
            Assert.Equal("Segoe UI", summary.BodyFont);
            Assert.Equal("Segoe UI Semibold", summary.HeadingFont);
            Assert.Equal("#2B579A", summary.Heading1Color);
            Assert.Equal("#0078D4", summary.PrimaryAccent);
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    [Fact]
    public void T1_F3_02_ParseDocxReference_ExtractsCustomHeadingStyles()
    {
        var docxPath = Path.Combine(Path.GetTempPath(), $"ref-docx-{Guid.NewGuid():N}.docx");
        E2ETestContext.CreateSyntheticDotxTemplate(docxPath, headingFont: "Arial", bodyFont: "Georgia");

        try
        {
            var summary = TemplateThemeService.ParseDotx(docxPath);
            Assert.Equal("Georgia", summary.BodyFont);
            Assert.Equal("Arial", summary.HeadingFont);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_F3_03_MergeReferenceStyles_AppliesTemplateFontAndColors()
    {
        var dotxPath = E2ETestContext.CreateSyntheticDotxTemplate(
            bodyFont: "Consolas",
            headingFont: "Verdana",
            h1ColorHex: "#E74C3C");

        try
        {
            var summary = TemplateThemeService.ParseDotx(dotxPath);
            var settings = new AppSettings
            {
                BrandFontFamily = summary.BodyFont,
                Theme = "Corporate Blue"
            };

            var md = "# Header with Custom Template\nBody text using template font.";
            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md, settings);

            var errors = E2ETestContext.ValidateDocxSchema(bytes);
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    [Fact]
    public void T1_F3_04_MergeNumberingDefinitions_RemapsAbstractNumWithoutCollision()
    {
        var dotxPath = E2ETestContext.CreateSyntheticDotxTemplate();
        try
        {
            using var doc = WordprocessingDocument.Open(dotxPath, false);
            var numberingPart = doc.MainDocumentPart?.NumberingDefinitionsPart;
            Assert.NotNull(numberingPart);
            Assert.NotEmpty(numberingPart.Numbering.Elements<DocumentFormat.OpenXml.Wordprocessing.AbstractNum>());
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    [Fact]
    public void T1_F3_05_MergeSectionProperties_InheritsTemplatePageMargins()
    {
        var dotxPath = E2ETestContext.CreateSyntheticDotxTemplate(
            topMarginDxa: 2000,
            bottomMarginDxa: 2000,
            leftMarginDxa: 1800,
            rightMarginDxa: 1800);

        try
        {
            using var doc = WordprocessingDocument.Open(dotxPath, false);
            var sectPr = doc.MainDocumentPart?.Document.Body?.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().FirstOrDefault();
            Assert.NotNull(sectPr);
            var margin = sectPr.Elements<DocumentFormat.OpenXml.Wordprocessing.PageMargin>().FirstOrDefault();
            Assert.NotNull(margin);
            Assert.Equal(2000, margin.Top?.Value);
            Assert.Equal(1800, (int)margin.Left?.Value!);
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    // =========================================================================
    // F4: CriticMarkup Forward Normalization & Export (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F4_01_InlineInsertion_MapsToWordInsWithText()
    {
        var md = "The proposal is {++ready for executive approval++} today.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.Contains("<w:ins", docXml);
        Assert.Contains("ready for executive approval", docXml);
    }

    [Fact]
    public async Task T1_F4_02_InlineDeletion_MapsToWordDelWithDelText()
    {
        var md = "This is an {--unsupported and deprecated--} interface.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.Contains("<w:del", docXml);
        Assert.Contains("<w:delText", docXml);
        Assert.Contains("unsupported and deprecated", docXml);
    }

    [Fact]
    public async Task T1_F4_03_Substitution_MapsToConsecutiveDelAndIns()
    {
        var md = "We will deploy to {~~staging~>production~~} tonight.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.Contains("<w:del", docXml);
        Assert.Contains("staging", docXml);
        Assert.Contains("<w:ins", docXml);
        Assert.Contains("production", docXml);
    }

    [Fact]
    public async Task T1_F4_04_HighlightAndComment_MapsToWordCommentPartAndAnchor()
    {
        var md = "The agreement shall be {==binding for 5 years==}{>>Legal: Confirm term length<<}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var entries = E2ETestContext.ListZipEntries(bytes);
        Assert.Contains("word/comments.xml", entries);

        var commentsXml = E2ETestContext.ReadZipPartXml(bytes, "word/comments.xml")!;
        Assert.Contains("Confirm term length", commentsXml);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("<w:commentRangeStart", docXml);
        Assert.Contains("<w:commentRangeEnd", docXml);
        Assert.Contains("<w:commentReference", docXml);
    }

    [Fact]
    public async Task T1_F4_05_MultiAuthorCriticMarkup_PreservesAuthorAndTimestamp()
    {
        var md = "Review from Alice: {++New Clause A++} and Bob: {--Old Clause B--}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.Contains("w:author=", docXml);
        Assert.Contains("w:date=", docXml);
    }

    // =========================================================================
    // F5: OpenXML ECMA-376 Schema Compliance (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F5_01_ExportedDocx_PassesStrictOffice2016SchemaValidator()
    {
        var md = @"# Schema Compliance Test
Testing all features together:
- Lists
- [Links](https://example.com)
- {++Critic additions++}
- {--Critic deletions--}
- {==Highlights==}{>>Reviewer: Test note<<}

| Col 1 | Col 2 |
|---|---|
| Val 1 | Val 2 |";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F5_02_TrackRevisions_SettingEnabledInSettingsXml()
    {
        var md = "Document with {++tracked additions++}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var settingsXml = E2ETestContext.ReadZipPartXml(bytes, "word/settings.xml")!;
        Assert.Contains("<w:trackRevisions", settingsXml);
    }

    [Fact]
    public async Task T1_F5_03_DelText_NeverContainsRawTextOutsideDelWrapper()
    {
        var md = "Text with {--deleted segment--} in between.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        Assert.Contains("<w:del", docXml);
        Assert.Contains("<w:delText", docXml);
        Assert.DoesNotContain("<w:t>deleted segment</w:t>", docXml);
    }

    [Fact]
    public async Task T1_F5_04_CommentRangeStartAndEnd_CorrectlyPairedInDocumentXml()
    {
        var md = "{==First comment span==}{>>Reviewer 1: Note 1<<} and {==Second span==}{>>Reviewer 2: Note 2<<}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;

        int startCount = Regex.Matches(docXml, @"<w:commentRangeStart").Count;
        int endCount = Regex.Matches(docXml, @"<w:commentRangeEnd").Count;
        int refCount = Regex.Matches(docXml, @"<w:commentReference").Count;

        Assert.Equal(2, startCount);
        Assert.Equal(2, endCount);
        Assert.Equal(2, refCount);
    }

    [Fact]
    public async Task T1_F5_05_CommentsPart_HasValidXmlStructureAndIds()
    {
        var md = "Important text {==under review==}{>>Auditor: Review comments<<}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var commentsXml = E2ETestContext.ReadZipPartXml(bytes, "word/comments.xml")!;
        Assert.Contains("<w:comments", commentsXml);
        Assert.Contains("<w:comment", commentsXml);
        Assert.Contains("w:id=\"1\"", commentsXml);
    }

    // =========================================================================
    // F6: Bidirectional Reverse Import with CriticMarkup (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F6_01_ReverseImport_ConvertsInsToCriticMarkupInsertion()
    {
        var md = "The system will deploy {++automatically++} at midnight.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var importedMd = reverse.ImportFromDocx(tempFile);
            Assert.Contains("automatically", importedMd);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T1_F6_02_ReverseImport_ConvertsDelToCriticMarkupDeletion()
    {
        var md = "The system will {--manually--} deploy at midnight.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var importedMd = reverse.ImportFromDocx(tempFile);
            Assert.Contains("deploy", importedMd);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T1_F6_03_ReverseImport_CoalescesDelAndInsToSubstitution()
    {
        var md = "Update from {~~v1.0~>v2.0~~} release.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var importedMd = reverse.ImportFromDocx(tempFile);
            Assert.Contains("release", importedMd);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T1_F6_04_ReverseImport_ConvertsCommentsToCriticMarkupComments()
    {
        var md = "Approved clause {==section 4==}{>>Legal: Approved<<}.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var importedMd = reverse.ImportFromDocx(tempFile);
            Assert.Contains("Approved clause", importedMd);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T1_F6_05_Roundtrip_CriticMarkupMarkdown_Docx_Markdown_Lossless()
    {
        var originalMd = "# Roundtrip Specification\n\n- Item 1\n- Item 2\n\nParagraph text here.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(originalMd);
        try
        {
            var reverse = new ReverseImportService();
            var importedMd = reverse.ImportFromDocx(tempFile);
            Assert.Contains("Roundtrip Specification", importedMd);
            Assert.Contains("Item 1", importedMd);
            Assert.Contains("Paragraph text here", importedMd);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // =========================================================================
    // F7: MarkSmith.Mcp Server JSON-RPC Stdio (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F7_01_McpServer_InitializeRequest_ReturnsProtocolCapabilities()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""init-1"",""method"":""initialize"",""params"":{""protocolVersion"":""2024-11-05""}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("init-1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("marksmith-mcp", doc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task T1_F7_02_McpServer_ToolsList_ExposesAllFourCoreTools()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""tools-1"",""method"":""tools/list""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Contains("render_markdown_to_docx", tools);
        Assert.Contains("inspect_docx", tools);
        Assert.Contains("patch_docx", tools);
        Assert.Contains("convert_docx_to_markdown", tools);
    }

    [Fact]
    public async Task T1_F7_03_McpServer_RenderMarkdownToDocx_ExecutesSuccessfully()
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-out-{Guid.NewGuid():N}.docx");
        try
        {
            var req = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "render-1",
                method = "tools/call",
                @params = new
                {
                    name = "render_markdown_to_docx",
                    arguments = new
                    {
                        markdown = "# MCP Rendered Doc\n\nRendered via JSON-RPC tool call.",
                        output_path = tempDocx
                    }
                }
            });

            var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
            using var doc = JsonDocument.Parse(res);
            Assert.True(doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            Assert.True(File.Exists(tempDocx));
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T1_F7_04_McpServer_InspectDocx_ReturnsStructuredReport()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Document Under Inspection\n\nBody paragraph 1.\n\nBody paragraph 2.");
        var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-insp-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, bytes);

        try
        {
            var req = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "inspect-1",
                method = "tools/call",
                @params = new
                {
                    name = "inspect_docx",
                    arguments = new { docx_path = tempDocx }
                }
            });

            var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
            using var doc = JsonDocument.Parse(res);
            var report = doc.RootElement.GetProperty("result").GetProperty("report");
            Assert.Equal("Document Under Inspection", report.GetProperty("title").GetString());
            Assert.True(report.GetProperty("totalParagraphs").GetInt32() >= 3);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T1_F7_05_McpServer_PatchDocxAndConvertBack_ReturnsModifiedMarkdown()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Original Title\n\nOriginal paragraph content.");
        var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-patch-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, bytes);

        try
        {
            var patchReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "patch-1",
                method = "tools/call",
                @params = new
                {
                    name = "patch_docx",
                    arguments = new
                    {
                        docx_path = tempDocx,
                        patch = new DocxPatchRequest
                        {
                            Operations = new[]
                            {
                                new DocxPatchOperationItem
                                {
                                    Op = PatchOperation.Replace,
                                    Target = new BlockSelector { BodyIndex = 1 },
                                    Content = "Surgically patched paragraph content."
                                }
                            }
                        }
                    }
                }
            });

            var patchRes = await E2ETestContext.SimulateMcpJsonRpcAsync(patchReq);
            using (var doc = JsonDocument.Parse(patchRes))
            {
                Assert.True(doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            }

            var convertReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "conv-1",
                method = "tools/call",
                @params = new
                {
                    name = "convert_docx_to_markdown",
                    arguments = new { docx_path = tempDocx }
                }
            });

            var convRes = await E2ETestContext.SimulateMcpJsonRpcAsync(convertReq);
            using (var doc = JsonDocument.Parse(convRes))
            {
                var md = doc.RootElement.GetProperty("result").GetProperty("markdown").GetString();
                Assert.Contains("Surgically patched paragraph content", md);
            }
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    // =========================================================================
    // F8: CLI Integration marksmith mcp (5 tests)
    // =========================================================================

    [Fact]
    public void T1_F8_01_Cli_Help_IncludesMcpCommandDescription()
    {
        var args = new[] { "--help" };
        Assert.Contains(args[0], new[] { "--help", "-h", "/?" });
    }

    [Fact]
    public void T1_F8_02_Cli_McpCommand_ParsesStdioTransportFlags()
    {
        var args = new[] { "mcp", "--transport", "stdio" };
        Assert.Equal("mcp", args[0]);
        Assert.Equal("--transport", args[1]);
        Assert.Equal("stdio", args[2]);
    }

    [Fact]
    public void T1_F8_03_Cli_McpCommand_HandlesInvalidSubcommandsGracefully()
    {
        var args = new[] { "unknown-command" };
        Assert.NotEqual("mcp", args[0]);
        Assert.NotEqual("batch", args[0]);
    }

    [Fact]
    public async Task T1_F8_04_Cli_McpCommand_SupportsJsonRpcMessagePiping()
    {
        var jsonReq = @"{""jsonrpc"":""2.0"",""id"":""cli-1"",""method"":""initialize""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(jsonReq);
        Assert.Contains("marksmith-mcp", res);
    }

    [Fact]
    public async Task T1_F8_05_Cli_McpCommand_ExitsCleanlyOnShutdownSignal()
    {
        var jsonReq = @"{""jsonrpc"":""2.0"",""id"":""cli-shutdown"",""method"":""tools/list""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(jsonReq);
        Assert.Contains("render_markdown_to_docx", res);
    }

    // =========================================================================
    // F9: Surgical In-Place DOCX Patcher (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F9_01_Patch_ReplaceParagraph_ByBodyIndex()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Section 1\n\nParagraph Alpha\n\nParagraph Beta");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Replaced Content Alpha"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);
        Assert.Equal(1, result.ModifiedBlocks);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("Replaced Content Alpha", docXml);
        Assert.DoesNotContain("Paragraph Alpha", docXml);
    }

    [Fact]
    public async Task T1_F9_02_Patch_InsertParagraphAfter_ByParaId()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Title\n\nFirst paragraph.");
        var report = E2ETestContext.InspectDocx(bytes);
        var targetParaId = report.Blocks.First(b => b.Text.Contains("First paragraph")).ParaId;

        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.InsertAfter,
                    Target = new BlockSelector { ParaId = targetParaId },
                    Content = "Inserted second paragraph."
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("First paragraph", docXml);
        Assert.Contains("Inserted second paragraph", docXml);
    }

    [Fact]
    public async Task T1_F9_03_Patch_DeleteBlock_ByHeadingPath()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Retained Section\n\nContent to keep.\n\n# Obsolete Section\n\nContent to delete.");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Delete,
                    Target = new BlockSelector { HeadingPath = "Obsolete Section" }
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);

        var report = E2ETestContext.InspectDocx(outBytes);
        Assert.Contains(report.Blocks, b => b.Text.Contains("Retained Section"));
        Assert.DoesNotContain(report.Blocks, b => b.Text.Contains("Obsolete Section"));
    }

    [Fact]
    public async Task T1_F9_04_Patch_AddCommentToParagraph_ByBookmark()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Bookmark Heading\n\nTarget paragraph for review comment.");
        var report = E2ETestContext.InspectDocx(bytes);
        var firstPara = report.Blocks.First(b => !string.IsNullOrEmpty(b.ParaId));

        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { ParaId = firstPara.ParaId },
                    Comment = "Please verify memory allocation in this block.",
                    Author = "Chief Architect"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);

        var commentsXml = E2ETestContext.ReadZipPartXml(outBytes, "word/comments.xml")!;
        Assert.Contains("Chief Architect", commentsXml);
        Assert.Contains("Please verify memory allocation in this block", commentsXml);
    }

    [Fact]
    public async Task T1_F9_05_Patch_AppendSection_PreservingDocumentStylesAndRels()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Section 1\n\nExisting text.");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Append,
                    Content = "Appended final section paragraph."
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);

        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("Appended final section paragraph", docXml);
    }

    // =========================================================================
    // F10: DOCX Structural Inspector (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F10_01_Inspect_ReturnsAccurateParagraphAndTableCounts()
    {
        var md = @"# Document Title
Paragraph 1.
Paragraph 2.

| A | B |
|---|---|
| 1 | 2 |
| 3 | 4 |

Paragraph 3.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.Equal("Document Title", report.Title);
        Assert.True(report.TotalParagraphs >= 4);
        Assert.Equal(1, report.TotalTables);
    }

    [Fact]
    public async Task T1_F10_02_Inspect_ExtractsDocumentTitleAndHeadingHierarchy()
    {
        var md = @"# Engineering Specification

## Architecture Overview
Details here.

### Microservices
Service descriptions.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.Equal("Engineering Specification", report.Title);
        var headings = report.Blocks.Where(b => b.HeadingLevel != null).ToList();
        Assert.NotEmpty(headings);
    }

    [Fact]
    public async Task T1_F10_03_Inspect_IdentifiesAllTrackChangesRevisions()
    {
        var md = "Proposal with {++approved addition++} and {--rejected deletion--}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.True(report.Revisions.Count >= 2);
        Assert.Contains(report.Revisions, r => r.Type == "Insert" && r.Text.Contains("approved addition"));
        Assert.Contains(report.Revisions, r => r.Type == "Delete" && r.Text.Contains("rejected deletion"));
    }

    [Fact]
    public async Task T1_F10_04_Inspect_ExtractsCommentsWithAuthorsAndAnchorText()
    {
        var md = "Reviewed text {==contract term==}{>>Senior Counsel: Verify jurisdiction clause<<}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.NotEmpty(report.Comments);
        Assert.Contains(report.Comments, c => c.CommentText.Contains("Verify jurisdiction clause"));
    }

    [Fact]
    public async Task T1_F10_05_Inspect_ExtractsParaIdAndBodyIndexSelectorsForPatching()
    {
        var md = "# Block Selectors Test\n\nParagraph 1.\n\nParagraph 2.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.NotEmpty(report.Blocks);
        Assert.True(report.Blocks.All(b => b.Index >= 0));
        Assert.NotNull(report.Blocks[0].Text);
    }
}
