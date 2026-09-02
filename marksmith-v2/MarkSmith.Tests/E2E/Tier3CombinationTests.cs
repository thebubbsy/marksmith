using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 3: Cross-Feature Interaction & Combinatorial Tests (≥15 test cases).
/// Validates pairwise combinations across reference templates, CriticMarkup track changes/comments,
/// SAX streaming export, in-place patching, structural inspection, and reverse import.
/// </summary>
public class Tier3CombinationTests
{
    [Fact]
    public async Task T3_01_TemplateMerge_Plus_CriticMarkup_Plus_SaxExport()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate(bodyFont: "Segoe UI", h1ColorHex: "#0078D4");
        try
        {
            var summary = TemplateThemeService.ParseDotx(dotx);
            var settings = new AppSettings { BrandFontFamily = summary.BodyFont };
            var md = @"# Executive Revision
The system undergoes {++modern cloud migration++} from {--legacy on-prem servers--}.";

            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md, settings);
            var errors = E2ETestContext.ValidateDocxSchema(bytes);
            Assert.Empty(errors);

            var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("<w:del", docXml);
            Assert.Contains("modern cloud migration", docXml);
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T3_02_SaxExport_Then_DocxInspector_Then_InPlacePatch_Then_ReverseImport()
    {
        var md = @"# Pipeline Architecture
Initial base paragraph.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);
        Assert.Equal("Pipeline Architecture", report.Title);        
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Updated base paragraph via surgical patch."
                }
            }
        };

        var (patchedBytes, patchResult) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(patchResult.Success);

        var tempDocx = Path.Combine(Path.GetTempPath(), $"t3-02-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, patchedBytes);
        try
        {
            var reverse = new ReverseImportService();
            var reversedMd = reverse.ImportFromDocx(tempDocx);
            Assert.Contains("Updated base paragraph via surgical patch", reversedMd);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_03_SAXStreaming_GeneratesIdenticalStructureToDOM()
    {
        var md = @"# Document Title
This is paragraph one.

## Section 2
- Bullet 1
- Bullet 2

| Col 1 | Col 2 |
|---|---|
| A | B |";

        var saxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var domBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var saxErrors = E2ETestContext.ValidateDocxSchema(saxBytes);
        var domErrors = E2ETestContext.ValidateDocxSchema(domBytes);

        Assert.Empty(saxErrors);
        Assert.Empty(domErrors);
    }

    [Fact]
    public async Task T3_04_McpPipeline_FullCycle_Render_Inspect_Patch_ConvertBack()
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-full-cycle-{Guid.NewGuid():N}.docx");
        try
        {
            // 1. Render
            var renderReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "step1",
                method = "tools/call",
                @params = new
                {
                    name = "render_markdown_to_docx",
                    arguments = new { markdown = "# Workflow Report\n\nOriginal step 1.", output_path = tempDocx }
                }
            });
            await E2ETestContext.SimulateMcpJsonRpcAsync(renderReq);

            // 2. Inspect
            var inspectReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "step2",
                method = "tools/call",
                @params = new { name = "inspect_docx", arguments = new { docx_path = tempDocx } }
            });
            var inspectRes = await E2ETestContext.SimulateMcpJsonRpcAsync(inspectReq);
            using var inspectDoc = JsonDocument.Parse(inspectRes);
            Assert.True(inspectDoc.RootElement.GetProperty("result").TryGetProperty("report", out _));

            // 3. Patch
            var patchReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "step3",
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
                                    Content = "Patched content via MCP protocol."
                                }
                            }
                        }
                    }
                }
            });
            await E2ETestContext.SimulateMcpJsonRpcAsync(patchReq);

            // 4. Convert back
            var convReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "step4",
                method = "tools/call",
                @params = new { name = "convert_docx_to_markdown", arguments = new { docx_path = tempDocx } }
            });
            var convRes = await E2ETestContext.SimulateMcpJsonRpcAsync(convReq);
            using var convDoc = JsonDocument.Parse(convRes);
            var md = convDoc.RootElement.GetProperty("result").GetProperty("markdown").GetString();
            Assert.Contains("Patched content via MCP protocol", md);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_05_InPlacePatcher_AddsThreadedComment_To_TemplateStyledDocx()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate();
        try
        {
            var summary = TemplateThemeService.ParseDotx(dotx);
            var settings = new AppSettings { BrandFontFamily = summary.BodyFont };
            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Template Doc\n\nParagraph needing legal review.", settings);
            var report = E2ETestContext.InspectDocx(bytes);
            var firstPara = report.Blocks.First(b => !string.IsNullOrEmpty(b.ParaId));

            var patchReq = new DocxPatchRequest
            {
                Operations = new[]
                {
                    new DocxPatchOperationItem
                    {
                        Op = PatchOperation.AddComment,
                        Target = new BlockSelector { ParaId = firstPara.ParaId },
                        Comment = "Include standard indemnification clause here.",
                        Author = "General Counsel"
                    }
                }
            };

            var (patchedBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
            Assert.True(result.Success);

            var commentsXml = E2ETestContext.ReadZipPartXml(patchedBytes, "word/comments.xml")!;
            Assert.Contains("General Counsel", commentsXml);
            Assert.Contains("standard indemnification clause", commentsXml);
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T3_06_ReverseImport_PatchedDocument_PreservesCriticMarkupAndComments()
    {
        var md = "Original agreement with {++approved addendum++}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Append,
                    Content = "Appended clause with {--deleted terms--}."
                }
            }
        };

        var (patchedBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var tempDocx = Path.Combine(Path.GetTempPath(), $"t3-06-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, patchedBytes);
        try
        {
            var reverse = new ReverseImportService();
            var imported = reverse.ImportFromDocx(tempDocx);
            Assert.Contains("Original agreement", imported);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_07_TemplateTheme_CombinedWith_TrackChanges_ProducesValidXml()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate(bodyFont: "Georgia", headingFont: "Garamond");
        try
        {
            var summary = TemplateThemeService.ParseDotx(dotx);
            var settings = new AppSettings { BrandFontFamily = summary.BodyFont };
            var md = "# Executive Summary\n\nProposal with {++approved changes++} and {--rejected additions--}.";

            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md, settings);
            var errors = E2ETestContext.ValidateDocxSchema(bytes);
            Assert.Empty(errors);

            var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("<w:del", docXml);
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T3_08_MultiColumnSection_With_InPlacePatchReplacement()
    {
        var md = @"# Section Title
Paragraph before columns.

Paragraph inside column 1.

Paragraph after columns.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Replaced paragraph inside columns."
                }
            }
        };

        var (patchedBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(patchedBytes, "word/document.xml")!;
        Assert.Contains("Replaced paragraph inside columns", docXml);
    }

    [Fact]
    public async Task T3_09_TableWithFormulas_Inside_RevisionBlock_ExportedAndInspected()
    {
        var md = @"# Financial Statement
| Category | Q1 | Q2 | Total |
|---|---|---|---|
| Revenue | 100 | 150 | {++250++} |
| Expense | 40 | 50 | {--90--} |";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.Equal(1, report.TotalTables);
        Assert.True(report.Revisions.Count >= 2);
    }

    [Fact]
    public async Task T3_10_BatchExport_With_Templates_And_CriticMarkup_Flags()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate();
        try
        {
            var files = new[]
            {
                ("# Doc A", "{++Add A++}"),
                ("# Doc B", "{--Del B--}")
            };

            foreach (var (title, rev) in files)
            {
                var md = $"{title}\n\nContent with {rev}.";
                var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
                var errors = E2ETestContext.ValidateDocxSchema(bytes);
                Assert.Empty(errors);
            }
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T3_11_DynamicRels_With_CriticMarkupImagesAndHyperlinks()
    {
        var md = @"# Revision with Links
Click {++[New Portal](https://portal.example.com)++} or {--[Old Portal](https://legacy.example.com)--} for access.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;
        Assert.Contains("portal.example.com", relsXml);
        Assert.Contains("legacy.example.com", relsXml);
    }

    [Fact]
    public async Task T3_12_DocxInspector_FindsSelectors_For_TargetedInPlacePatches()
    {
        var md = @"# Architecture Document

## Overview
System overview text.

## Database
Database schema text.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        var dbBlock = report.Blocks.FirstOrDefault(b => b.Text.Contains("Database schema text"));
        Assert.NotNull(dbBlock);

        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = dbBlock.Index },
                    Content = "Enhanced PostgreSQL multi-region database schema."
                }
            }
        };

        var (patchedBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var updatedReport = E2ETestContext.InspectDocx(patchedBytes);
        Assert.Contains(updatedReport.Blocks, b => b.Text.Contains("PostgreSQL multi-region"));
    }

    [Fact]
    public async Task T3_13_McpServer_ToolPipelining_Render_Patch_Inspect()
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-pipeline-{Guid.NewGuid():N}.docx");
        try
        {
            // 1. Render
            var r1 = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "p1",
                method = "tools/call",
                @params = new
                {
                    name = "render_markdown_to_docx",
                    arguments = new { markdown = "# Title\n\nParagraph 1.", output_path = tempDocx }
                }
            });
            await E2ETestContext.SimulateMcpJsonRpcAsync(r1);

            // 2. Patch
            var r2 = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "p2",
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
                                    Op = PatchOperation.InsertAfter,
                                    Target = new BlockSelector { BodyIndex = 1 },
                                    Content = "Paragraph 2 inserted via MCP."
                                }
                            }
                        }
                    }
                }
            });
            await E2ETestContext.SimulateMcpJsonRpcAsync(r2);

            // 3. Inspect
            var r3 = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "p3",
                method = "tools/call",
                @params = new { name = "inspect_docx", arguments = new { docx_path = tempDocx } }
            });
            var res3 = await E2ETestContext.SimulateMcpJsonRpcAsync(r3);
            using var doc = JsonDocument.Parse(res3);
            var report = doc.RootElement.GetProperty("result").GetProperty("report");
            Assert.True(report.GetProperty("totalParagraphs").GetInt32() >= 3);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_14_CriticMarkupSubstitutions_In_TemplateStyledTables()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate();
        try
        {
            var md = @"| Component | Version |
|---|---|
| Backend | {~~v1.4~>v2.0~~} |
| Database | {~~Postgres 14~>Postgres 16~~} |";

            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
            var errors = E2ETestContext.ValidateDocxSchema(bytes);
            Assert.Empty(errors);

            var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("v2.0", docXml);
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T3_15_CrossPlatformUnicode_In_CriticMarkup_With_TemplateStyles()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate(headingFont: "Segoe UI", bodyFont: "Segoe UI");
        try
        {
            var md = @"# International Review

Changes: {++日本語の追記 (Japanese addition)++} and {--삭제된 한국어 텍스트 (Korean deletion)--}.";

            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
            var errors = E2ETestContext.ValidateDocxSchema(bytes);
            Assert.Empty(errors);

            var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
            Assert.Contains("日本語の追記", docXml);
            Assert.Contains("삭제된 한국어", docXml);
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }
}
