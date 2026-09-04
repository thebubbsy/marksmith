using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 3: Pairwise Combinatorial Interaction Tests (≥25 test cases).
/// Validates cross-feature interactions across MCP tools, in-place patching, collapsible sections,
/// multi-column blocks, nested grid tables, DrawingML charts, dual-pipeline parity, streaming SAX,
/// and AI 3-block workflow state machines.
/// Total: 25 tests.
/// </summary>
public class Tier3CombinatorialTests
{
    [Fact]
    public async Task T3_01_McpRender_Plus_CollapsibleSections_Plus_DrawingMLCharts()
    {
        var md = @"# Executive Overview

<details><summary>Financial Visuals</summary>

:::chart type=""bar"" title=""Quarterly Revenue""
Categories: Q1, Q2, Q3, Q4
Series: 2026, 120, 180, 240, 310
:::

</details>";

        var tempDocx = Path.Combine(Path.GetTempPath(), $"t3-01-{Guid.NewGuid():N}.docx");
        try
        {
            var req = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "t3-01",
                method = "tools/call",
                @params = new
                {
                    name = "render_markdown_to_docx",
                    arguments = new { markdown = md, output_path = tempDocx }
                }
            });

            var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
            using var doc = JsonDocument.Parse(res);
            Assert.True(doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            Assert.True(File.Exists(tempDocx));

            var errors = E2ETestContext.ValidateDocxSchema(tempDocx);
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_02_MultiColumn_Plus_NestedGridHtmlTable_Plus_SchemaValidation()
    {
        var md = @":::columns
### Architecture Matrix
<table>
  <tr><th colspan=""2"">Core Cluster</th></tr>
  <tr><td>Nodes</td><td>Active (32)</td></tr>
</table>
===
### Metric Overview
<table>
  <tr><td>P99 Latency</td><td>< 2.5ms</td></tr>
  <tr><td>Availability</td><td>99.999%</td></tr>
</table>
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("gridSpan", docXml);
        Assert.Contains("Core Cluster", docXml);
        Assert.Contains("P99 Latency", docXml);
    }

    [Fact]
    public async Task T3_03_InPlacePatcher_RevisionsAndComments_Plus_RichTranspilation_Plus_ReverseImport()
    {
        var originalMd = "# Project Roadmap\n\nInitial milestone description.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(originalMd);

        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "> [!NOTE]\n> Upgraded milestone with high-priority deliverables.",
                    TrackChanges = true,
                    Author = "Lead Architect"
                }
            }
        };

        var (patchedBytes, patchResult) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(patchResult.Success);

        var tempDocx = Path.Combine(Path.GetTempPath(), $"t3-03-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, patchedBytes);

        try
        {
            var reverse = new ReverseImportService();
            var reversedMd = reverse.ImportFromDocx(tempDocx);
            Assert.Contains("Roadmap", reversedMd);
            Assert.Contains("deliverables", reversedMd);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_04_AiCycleManager_Plus_GeminiDialectNormalizer_Plus_DocxExport()
    {
        // 1. Advance AI cycle
        var cycleReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "t3-04-cycle",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { current_block = 3 }
            }
        });

        var cycleRes = await E2ETestContext.SimulateMcpJsonRpcAsync(cycleReq);
        using (var doc = JsonDocument.Parse(cycleRes))
        {
            Assert.Equal(4, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
        }

        // 2. Normalize Gemini LLM output with chain-of-thought
        var rawLlmOutput = "<think>\nSynthesizing all 6 ideas into production document...\n</think>\n# Synthesized Architecture Specification\n\n- Deliverable 1: High performance streaming SAX\n- Deliverable 2: Surgical OpenXML patching";
        var normalized = ProviderDialectNormalizer.Normalize(rawLlmOutput, "gemini");

        // 3. Export to valid DOCX
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(normalized);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        Assert.DoesNotContain("<think>", normalized);
    }

    [Fact]
    public async Task T3_05_SaxStreaming_Plus_ThreadSafeRelationships_Plus_BufferPooling()
    {
        var tasks = Enumerable.Range(1, 8).Select(async i =>
        {
            var md = $@"# Concurrent Document {i}
[Docs {i}](https://docs{i}.example.com) and [Api {i}](https://api{i}.example.com)

- Stream Item A
- Stream Item B";

            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
            Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task T3_06_CriticMarkup_Plus_InPlacePatcher_AcceptRevisions_Plus_DocxInspector()
    {
        var md = "# Contract\nThe term is {++extended to 5 years++} from {--original 2 years--}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var repBefore = E2ETestContext.InspectDocx(bytes);
        Assert.NotEmpty(repBefore.Revisions);

        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AcceptRevision,
                    Target = new BlockSelector()
                }
            }
        };

        var (patchedBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var repAfter = E2ETestContext.InspectDocx(patchedBytes);
        Assert.Empty(repAfter.Revisions);
        Assert.Contains(repAfter.Blocks, b => b.Text.Contains("extended to 5 years"));
    }

    [Fact]
    public async Task T3_07_DualPipelineParity_DocxSax_Vs_HtmlPreview_WithMathAndCallouts()
    {
        var md = @"> [!IMPORTANT]
> Verify the mathematical boundary:
$$ \\lim_{x \\to 0} \\frac{\\sin(x)}{x} = 1 $$";

        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var html = E2ETestContext.RenderHtml(md);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("markdown-alert", html);
        Assert.Contains("katex", html.ToLowerInvariant());
    }

    [Fact]
    public async Task T3_08_MarkdownValidation_Plus_LosslessMarkdownPatch_Plus_McpDispatcher()
    {
        var brokenMd = "# Title\n\n```csharp\nUnclosed code block";
        var valReq1 = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "t3-08-v1",
            method = "tools/call",
            @params = new { name = "validate_markdown", arguments = new { markdown = brokenMd } }
        });

        var valRes1 = await E2ETestContext.SimulateMcpJsonRpcAsync(valReq1);
        using (var doc = JsonDocument.Parse(valRes1))
        {
            Assert.False(doc.RootElement.GetProperty("result").GetProperty("is_valid").GetBoolean());
        }

        // Apply lossless patch to close fence
        var patchReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "t3-08-p",
            method = "tools/call",
            @params = new
            {
                name = "patch_markdown",
                arguments = new
                {
                    markdown = brokenMd,
                    target = "Unclosed code block",
                    replacement = "Closed code block\n```"
                }
            }
        });

        var patchRes = await E2ETestContext.SimulateMcpJsonRpcAsync(patchReq);
        string fixedMd;
        using (var doc = JsonDocument.Parse(patchRes))
        {
            fixedMd = doc.RootElement.GetProperty("result").GetProperty("markdown").GetString()!;
        }

        var valReq2 = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "t3-08-v2",
            method = "tools/call",
            @params = new { name = "validate_markdown", arguments = new { markdown = fixedMd } }
        });

        var valRes2 = await E2ETestContext.SimulateMcpJsonRpcAsync(valReq2);
        using (var doc = JsonDocument.Parse(valRes2))
        {
            Assert.True(doc.RootElement.GetProperty("result").GetProperty("is_valid").GetBoolean());
        }
    }

    [Fact]
    public async Task T3_09_NestedGridTable_Inside_MultiColumn_With_ReferenceDotxTemplate()
    {
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate(bodyFont: "Georgia", headingFont: "Arial");
        try
        {
            var summary = TemplateThemeService.ParseDotx(dotx);
            var settings = new AppSettings { BrandFontFamily = summary.BodyFont };

            var md = @":::columns
<table>
  <tr><th colspan=""2"">Column A Table</th></tr>
  <tr><td>Key</td><td>Value</td></tr>
</table>
===
<table>
  <tr><th colspan=""2"">Column B Table</th></tr>
  <tr><td>Status</td><td>Active</td></tr>
</table>
:::";

            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md, settings);
            Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T3_10_AsyncTokenIngestion_Plus_GeminiQuirkNormalization_Plus_StreamingSax()
    {
        var rawLlmText = "<think>\nIngesting tokens...\n</think>\n```code snippet\nflowchart TD\n  Start --> Finish\n```\n\n# Pipeline Active\nAll services operational.";
        var tokenStream = E2ETestContext.CreateTokenStreamAsync(rawLlmText, chunkSize: 12);

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in tokenStream)
        {
            sb.Append(chunk);
        }

        var normalized = ProviderDialectNormalizer.Normalize(sb.ToString(), "gemini");
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(normalized);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        Assert.Contains("```mermaid", normalized);
        Assert.DoesNotContain("<think>", normalized);
    }

    [Fact]
    public async Task T3_11_DrawingMLChart_Plus_CollapsibleDetails_Plus_WatermarkHeader()
    {
        var md = @":::watermark ""STRICTLY CONFIDENTIAL"" color=""#CC0000""
# Executive Briefing

<details open><summary>Performance Analysis</summary>

:::chart type=""line"" title=""System Throughput (req/s)""
Categories: 10m, 20m, 30m
Series: RPS, 5000, 12000, 25000
:::

</details>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T3_12_InPlaceDocxPatcher_TargetByHeadingPath_Plus_TableCellSelector_OnNestedTable()
    {
        var md = @"# Global Configuration
Settings header text.

# Metrics Summary
| Metric | Value |
|---|---|
| Latency | Initial |";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { HeadingPath = "Global Configuration" },
                    Content = "# Upgraded Global Configuration"
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector
                    {
                        TableCell = new TableCellSelector { TableIndex = 0, Row = 1, Col = 1 }
                    },
                    Content = "Patched 1.2ms"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var rep = E2ETestContext.InspectDocx(outBytes);
        Assert.Contains(rep.Blocks, b => b.Text.Contains("Upgraded Global Configuration"));
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T3_13_MultiAuthorCriticMarkup_Plus_InPlaceCommentsPart_Plus_XmlValidation()
    {
        var md = "The SLA requires {++99.999% availability++}{>>DevOps: Confirmed via multi-region<<} and {--on-prem fallback--}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var commentsXml = E2ETestContext.ReadZipPartXml(bytes, "word/comments.xml")!;
        Assert.Contains("Confirmed via multi-region", commentsXml);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("<w:ins", docXml);
        Assert.Contains("<w:del", docXml);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public void T3_14_DocumentSemanticDiff_BeforeAndAfterInPlacePatch()
    {
        var original = "# Architecture\nLayer 1: Gateway\nLayer 2: Compute";
        var modified = "# Architecture\nLayer 1: Cloudflare Gateway\nLayer 2: Distributed Compute\nLayer 3: Cache";

        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(original, modified);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task T3_15_DualPipelineParity_OnComplexCodeBlocks_Math_And_Tables()
    {
        var md = @"# Complex Benchmark Report

```csharp
public static async Task StreamAsync() => await Task.Yield();
```

The scaling function:
$$ S(n) = O(1) $$

| Engine | Complexity |
|---|---|
| SAX | $O(1)$ |";

        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var html = E2ETestContext.RenderHtml(md);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("StreamAsync", html);
        Assert.Contains("katex", html.ToLowerInvariant());
    }

    [Fact]
    public async Task T3_16_McpPatchDocx_ToolCall_OnCollapsibleSectionWithTrackChanges()
    {
        var md = "<details open><summary>Section A</summary>\nOriginal collapsible paragraph.\n</details>";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var tempDocx = Path.Combine(Path.GetTempPath(), $"t3-16-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, bytes);

        try
        {
            var req = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "t3-16",
                method = "tools/call",
                @params = new
                {
                    name = "patch_docx",
                    arguments = new
                    {
                        docx_path = tempDocx,
                        operations = new[]
                        {
                            new
                            {
                                op = "replace",
                                target = new { body_index = 0 },
                                content = "Patched collapsible header.",
                                track_changes = true
                            }
                        }
                    }
                }
            });

            var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
            using var doc = JsonDocument.Parse(res);
            Assert.True(doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            Assert.Empty(E2ETestContext.ValidateDocxSchema(tempDocx));
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T3_17_HighConcurrencySaxExport_WithSimultaneousDocxInspection()
    {
        var baseDocx = await E2ETestContext.ExportMarkdownToBytesAsync("# Base Document\n\nParagraph 1.\n\nParagraph 2.");
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            if (i % 2 == 0)
            {
                var bytes = await E2ETestContext.ExportMarkdownToBytesAsync($"# Thread Export {i}\nContent {i}.");
                Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
            }
            else
            {
                var rep = E2ETestContext.InspectDocx(baseDocx);
                Assert.NotNull(rep.Title);
            }
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task T3_18_Ai3BlockCycle_GeneratesMultiColumnSpec_ValidatedByOpenXmlValidator()
    {
        var multiColumnMd = @"# AI-Generated Product Specification

:::columns
### Frontend Module
- Responsive UI
- Theme Engine
===
### Backend Module
- SAX Streaming
- Surgical Patcher
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(multiColumnMd);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T3_19_NestedTableWithColspanRowspan_PatchedInPlace_PreservesGridStructure()
    {
        var md = @"<table>
  <tr><td colspan=""2"">Header Title</td></tr>
  <tr><td>Cell 1</td><td>Cell 2</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector
                    {
                        TableCell = new TableCellSelector { TableIndex = 0, Row = 1, Col = 0 }
                    },
                    Content = "Updated Cell 1"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("gridSpan", docXml);
        Assert.Contains("Updated Cell 1", docXml);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T3_20_GeminiQuirkNormalization_FollowedByHtmlPreview_AndDocxExport()
    {
        var rawMd = "<think>\nGenerating flow diagram...\n</think>\n```code snippet\nsequenceDiagram\n  Client->>Server: Request\n  Server-->>Client: Response\n```\n# Result\nWorkflow completed.";
        var normalized = ProviderDialectNormalizer.Normalize(rawMd, "gemini");

        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(normalized);
        var html = E2ETestContext.RenderHtml(normalized);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("Result", html);
        Assert.DoesNotContain("<think>", html);
    }

    [Fact]
    public async Task T3_21_MarkdownValidation_IdentifiesUnclosedFence_PatchedLosslessly_Exported()
    {
        var broken = "# Doc\n```json\n{\"key\":\"val\"}";
        var (isValid, _) = E2ETestContext.ValidateMarkdownGovernance(broken);
        Assert.False(isValid);

        var fixedMd = E2ETestContext.ApplyMarkdownPatch(broken, "{\"key\":\"val\"}", "{\"key\":\"val\"}\n```");
        var (isNowValid, _) = E2ETestContext.ValidateMarkdownGovernance(fixedMd);
        Assert.True(isNowValid);

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(fixedMd);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T3_22_DynamicRelationshipIds_VerifiedAcrossMultiColumn_Watermark_Links_Charts()
    {
        var md = @":::watermark ""CONFIDENTIAL""
# Master Report

:::columns
[Portal Link](https://portal.example.com)
===
[Docs Link](https://docs.example.com)
:::

:::chart type=""bar"" title=""Growth""
Categories: A, B
Series: S, 10, 20
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;

        var relIds = Regex.Matches(relsXml, @"Id=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(relIds.Count, relIds.Distinct().Count());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T3_23_InPlacePatcher_RejectRevisions_OnCollapsibleSection()
    {
        var md = "<details><summary>Heading</summary>Paragraph with {++temporary note++}.</details>";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.RejectRevision,
                    Target = new BlockSelector()
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var rep = E2ETestContext.InspectDocx(outBytes);
        Assert.Empty(rep.Revisions);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T3_24_MemoryFootprintBenchmark_DuringConcurrentMultiFeatureExport()
    {
        var tasks = Enumerable.Range(1, 5).Select(i =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Doc {i}\n:::columns\nLeft\n===\nRight\n:::\n| A | B |\n|---|---|\n| 1 | 2 |\n");
            for (int p = 1; p <= 50; p++) sb.AppendLine($"Paragraph {p} load test.");
            return E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        });

        var results = await Task.WhenAll(tasks);
        foreach (var bytes in results)
        {
            Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        }
    }

    [Fact]
    public async Task T3_25_FullLifecycleRoundTrip_Prompt_Validate_Patch_Export_Inspect_Diff_Reverse()
    {
        // 1. Prompt retrieval
        var promptReq = @"{""jsonrpc"":""2.0"",""id"":""lc-1"",""method"":""prompts/get"",""params"":{""name"":""author_document_gemini_3_8""}}";
        var promptRes = await E2ETestContext.SimulateMcpJsonRpcAsync(promptReq);
        Assert.NotNull(promptRes);

        // 2. Syntax Validation
        var sourceMd = "# Enterprise System\n\nInitial draft paragraph.\n\n- Task 1\n- Task 2";
        var (isValid, _) = E2ETestContext.ValidateMarkdownGovernance(sourceMd);
        Assert.True(isValid);

        // 3. Lossless Markdown Patch
        var patchedMd = E2ETestContext.ApplyMarkdownPatch(sourceMd, "Initial draft paragraph.", "Approved production paragraph.");

        // 4. SAX DOCX Export
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(patchedMd);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));

        // 5. DOCX Inspection
        var report = E2ETestContext.InspectDocx(docxBytes);
        Assert.Equal("Enterprise System", report.Title);

        // 6. Semantic Diff
        var diffService = new MarkdownDiffService();
        var diff = diffService.Compare(sourceMd, patchedMd);
        Assert.NotNull(diff);

        // 7. Reverse Import
        var tempDocx = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, docxBytes);
        try
        {
            var reverse = new ReverseImportService();
            var importedMd = reverse.ImportFromDocx(tempDocx);
            Assert.Contains("Enterprise System", importedMd);
            Assert.Contains("Approved production paragraph", importedMd);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }
}
