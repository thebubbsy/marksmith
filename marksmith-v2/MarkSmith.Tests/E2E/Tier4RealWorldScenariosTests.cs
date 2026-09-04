using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 4: Real-World End-to-End Application Scenarios (5 comprehensive enterprise workflows).
/// 1. Enterprise NDA Legal Redline & Patching
/// 2. Executive Board Technical Report with Advanced Layouts
/// 3. AI 3-Block Autonomous Cycle Document Generation
/// 4. Massive Multi-Section Technical Specification Ingestion
/// 5. Cross-Engine Dual-Pipeline Publishing Verification
/// </summary>
public class Tier4RealWorldScenariosTests
{
    [Fact]
    public async Task T4_Scenario1_EnterpriseNdaLegalRedline()
    {
        // 1. Corporate reference template
        var dotx = E2ETestContext.CreateSyntheticDotxTemplate(
            bodyFont: "Calibri",
            headingFont: "Calibri Light",
            h1ColorHex: "#003366");

        try
        {
            var summary = TemplateThemeService.ParseDotx(dotx);
            var settings = new AppSettings { BrandFontFamily = summary.BodyFont };

            // 2. Multi-author legal redline markdown with CriticMarkup
            var ndaMarkdown = @"# MUTUAL NON-DISCLOSURE AGREEMENT

## 1. Confidentiality Obligations
The Receiving Party agrees to protect the Confidential Information of the Disclosing Party with the {++highest standard of reasonable care++} from {--unauthorized disclosure--}.

## 2. Term and Termination
This Agreement shall remain in effect for a period of {~~three (3) years~>five (5) years~~} from the Effective Date.

## 3. Governing Law and Jurisdiction
This Agreement shall be governed by the laws of {==the State of Delaware==}{>>General Counsel: Confirm Delaware chancery court selection<<}.";

            // 3. Export to Word DOCX via SAX streaming
            var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(ndaMarkdown, settings);
            var errors = E2ETestContext.ValidateDocxSchema(docxBytes);
            Assert.Empty(errors);

            // 4. Structural Inspection
            var report = E2ETestContext.InspectDocx(docxBytes);
            Assert.Equal("MUTUAL NON-DISCLOSURE AGREEMENT", report.Title);
            Assert.NotEmpty(report.Revisions);
            Assert.NotEmpty(report.Comments);

            // 5. Surgical In-Place Clause Patch using exact HeadingPath
            var patchReq = new DocxPatchRequest
            {
                Operations = new[]
                {
                    new DocxPatchOperationItem
                    {
                        Op = PatchOperation.InsertAfter,
                        Target = new BlockSelector { HeadingPath = "3. Governing Law and Jurisdiction" },
                        Content = "## 4. Injunctive Relief\nBoth parties acknowledge that unauthorized disclosure causes irreparable harm warranting immediate injunctive relief."
                    }
                }
            };

            var (patchedBytes, patchResult) = E2ETestContext.ApplyDocxPatch(docxBytes, patchReq);
            Assert.True(patchResult.Success);
            Assert.Empty(E2ETestContext.ValidateDocxSchema(patchedBytes));

            // 6. Reverse Import back to Markdown
            var tempDocx = Path.Combine(Path.GetTempPath(), $"nda-{Guid.NewGuid():N}.docx");
            await File.WriteAllBytesAsync(tempDocx, patchedBytes);
            try
            {
                var reverse = new ReverseImportService();
                var reversedMd = reverse.ImportFromDocx(tempDocx);
                Assert.Contains("MUTUAL NON-DISCLOSURE AGREEMENT", reversedMd);
                Assert.Contains("Injunctive Relief", reversedMd);
            }
            finally
            {
                if (File.Exists(tempDocx)) File.Delete(tempDocx);
            }
        }
        finally
        {
            if (File.Exists(dotx)) File.Delete(dotx);
        }
    }

    [Fact]
    public async Task T4_Scenario2_ExecutiveBoardTechnicalReportWithAdvancedLayouts()
    {
        var executiveMd = @":::watermark ""BOARD CONFIDENTIAL"" color=""#990000"" opacity=""0.15""
# Executive Technical Review 2026

## 1. Strategic Infrastructure Overview
This report outlines the Q3 technological transformation and infrastructure performance.

:::columns
### Cloud Migration
- Multi-region Kubernetes
- 99.999% uptime target
- $O(1)$ memory streaming
===
### Key Financial KPIs
- Operational Cost: -22%
- Throughput: 100k req/s
- P99 Latency: 1.4ms
:::

## 2. Risk Factors and Mitigation
<details><summary>Security and Compliance Risks</summary>

All cloud endpoints enforce zero-trust authentication with mTLS and automated key rotation every 24 hours.

</details>

## 3. Financial Performance Breakdown
<table>
  <tr><th colspan=""3"">Quarterly Cost Optimization</th></tr>
  <tr><td rowspan=""2"">Infrastructure</td><td>Compute</td><td>-$120,000</td></tr>
  <tr><td>Storage</td><td>-$45,000</td></tr>
  <tr><td colspan=""2""><strong>Total Net Savings</strong></td><td><strong>-$165,000</strong></td></tr>
</table>

## 4. Throughput Growth
:::chart type=""bar"" title=""Quarterly Request Volume (Millions)""
Categories: Q1, Q2, Q3, Q4
Series: 2026, 45, 80, 130, 210
:::";

        // 1. Export DOCX with strict schema validation
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(executiveMd);
        var docxErrors = E2ETestContext.ValidateDocxSchema(docxBytes);
        Assert.Empty(docxErrors);

        // 2. Render HTML Preview with strict XSS governance
        var html = E2ETestContext.RenderHtml(executiveMd);
        Assert.Contains("ms-columns", html);
        Assert.Contains("<details", html);
        Assert.Contains("Quarterly Cost Optimization", html);

        // 3. Structural Inspection
        var report = E2ETestContext.InspectDocx(docxBytes);
        Assert.Equal("Executive Technical Review 2026", report.Title);
        Assert.True(report.TotalParagraphs >= 8);
    }

    [Fact]
    public async Task T4_Scenario3_Ai3BlockAutonomousCycleDocumentGeneration()
    {
        // Stage 1: Block 1 Idea Generation
        var b1Req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "ai-b1",
            method = "tools/call",
            @params = new { name = "manage_3block_cycle", arguments = new { action = "generate", current_block = 1 } }
        });
        var b1Res = await E2ETestContext.SimulateMcpJsonRpcAsync(b1Req);
        using (var doc = JsonDocument.Parse(b1Res))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
        }

        // Stage 2: Block 2 Refinement Pass 1 + Generation
        var b2Req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "ai-b2",
            method = "tools/call",
            @params = new { name = "manage_3block_cycle", arguments = new { action = "refine_and_generate", current_block = 2 } }
        });
        var b2Res = await E2ETestContext.SimulateMcpJsonRpcAsync(b2Req);
        using (var doc = JsonDocument.Parse(b2Res))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
        }

        // Stage 3: Block 3 Refinement Pass 2 + Refinement + Generation
        var b3Req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "ai-b3",
            method = "tools/call",
            @params = new { name = "manage_3block_cycle", arguments = new { action = "refine_all", current_block = 3 } }
        });
        var b3Res = await E2ETestContext.SimulateMcpJsonRpcAsync(b3Req);
        using (var doc = JsonDocument.Parse(b3Res))
        {
            Assert.Equal(4, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
            Assert.True(doc.RootElement.GetProperty("result").GetProperty("is_execution_phase").GetBoolean());
        }

        // Stage 4: Block 4 Execution Phase
        var b4Req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "ai-b4",
            method = "tools/call",
            @params = new { name = "manage_3block_cycle", arguments = new { action = "execute_code", current_block = 4 } }
        });
        var b4Res = await E2ETestContext.SimulateMcpJsonRpcAsync(b4Req);
        using (var doc = JsonDocument.Parse(b4Res))
        {
            Assert.Equal(6, doc.RootElement.GetProperty("result").GetProperty("total_refined_ideas").GetInt32());
        }

        // Simulate AI output with Gemini reasoning header and Mermaid block
        var aiMarkdown = @"<think>
Synthesizing all 6 ideas:
1. Multi-threaded SAX streaming
2. Thread-safe relationship staging
3. Buffer pooling O(1)
4. Collapsible sections
5. Multi-column blocks
6. Nested grid table parser
</think>

```code snippet
flowchart TD
  Ideas --> Refinement
  Refinement --> ProductionCode
```

# Autonomous System Architecture
The AI-driven pipeline synthesizes high-performance document components.";

        // Normalization & Validation
        var normalized = ProviderDialectNormalizer.Normalize(aiMarkdown, "gemini");
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(normalized);
        Assert.True(isValid);

        // DOCX Export
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(normalized);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("```mermaid", normalized);
        Assert.DoesNotContain("<think>", normalized);
    }

    [Fact]
    public async Task T4_Scenario4_MassiveMultiSectionTechnicalSpecificationIngestion()
    {
        // Generate massive technical specification (200 sections, tables, code)
        var sb = new StringBuilder();
        sb.AppendLine("# Enterprise Distributed System Technical Specification\n");
        sb.AppendLine("Comprehensive architecture definition for large-scale enterprise deployment.\n");

        for (int i = 1; i <= 100; i++)
        {
            sb.AppendLine($"## Section {i}: Subsystem {i} Architecture");
            sb.AppendLine($"Detailed operational specification and resilience metrics for subsystem {i}.\n");

            if (i % 5 == 0)
            {
                sb.AppendLine(@"| Parameter | Value | SLA |
|---|---|---|
| Latency | < 5ms | 99.9% |
| Concurrency | 10,000 | 99.99% |
");
            }

            if (i % 10 == 0)
            {
                sb.AppendLine("```csharp\npublic async Task ProcessItemAsync(int id) => await Task.Yield();\n```\n");
            }
        }

        var fullSpec = sb.ToString();

        // 1. Ingest via token stream
        var tokenStream = E2ETestContext.CreateTokenStreamAsync(fullSpec, chunkSize: 256);
        var collected = new StringBuilder();
        await foreach (var token in tokenStream)
        {
            collected.Append(token);
        }
        Assert.Equal(fullSpec.Length, collected.Length);

        // 2. Export via streaming SAX engine
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(collected.ToString());
        Assert.NotEmpty(docxBytes);

        // 3. Schema validation
        var errors = E2ETestContext.ValidateDocxSchema(docxBytes);
        Assert.Empty(errors);

        // 4. Inspection
        var report = E2ETestContext.InspectDocx(docxBytes);
        Assert.Equal("Enterprise Distributed System Technical Specification", report.Title);
        Assert.True(report.TotalParagraphs >= 200);
    }

    [Fact]
    public async Task T4_Scenario5_CrossEngineDualPipelinePublishingVerification()
    {
        var complexMd = @":::watermark ""OFFICIAL RELEASE"" color=""#003366"" opacity=""0.10""
# Dual-Pipeline Publishing Verification

## 1. Architecture Summary
> [!NOTE]
> This document verifies exact cross-pipeline consistency between DOCX and HTML preview.

## 2. Computational Complexity
The mathematical derivation:
$$ T(n) = 2 T\\left(\\frac{n}{2}\\right) + O(n) = O(n \\log n) $$

## 3. High-Density Layout
:::columns
### Ingestion Engine
- Streaming Token Parser
- Dialect Normalizer
===
### SAX Export Engine
- OpenXmlWriter Streaming
- Thread-Safe Relationships
:::

## 4. Operational Metrics
<table>
  <tr><th colspan=""2"">Engine Benchmarks</th><th>Target</th></tr>
  <tr><td rowspan=""2"">Throughput</td><td>Paragraphs/sec</td><td>> 500</td></tr>
  <tr><td>Tokens/sec</td><td>> 5,000</td></tr>
  <tr><td colspan=""2"">Memory Bound</td><td>$O(1)$</td></tr>
</table>

## 5. Security & Sanitization
```html
<div class=""safe-container"">Escaped Text Content</div>
```";

        // 1. Validate Governance
        var (isValid, govErrors) = E2ETestContext.ValidateMarkdownGovernance(complexMd);
        Assert.True(isValid);
        Assert.Empty(govErrors);

        // 2. DOCX Pipeline Export & OpenXML Schema Validation
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(complexMd);
        var docxErrors = E2ETestContext.ValidateDocxSchema(docxBytes);
        Assert.Empty(docxErrors);

        // 3. HTML Preview Pipeline & XSS Sanitization Check
        var html = E2ETestContext.RenderHtml(complexMd);
        Assert.Contains("markdown-alert", html);
        Assert.Contains("katex", html.ToLowerInvariant());
        Assert.Contains("ms-columns", html);
        Assert.Contains("Engine Benchmarks", html);
        Assert.Contains("&lt;div class=&quot;safe-container&quot;&gt;", html);
        Assert.DoesNotContain("<script>alert", html);

        // 4. Reverse Import Parity Spot-Check
        var tempDocx = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, docxBytes);
        try
        {
            var reverse = new ReverseImportService();
            var imported = reverse.ImportFromDocx(tempDocx);
            Assert.Contains("Dual-Pipeline Publishing Verification", imported);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }
}
