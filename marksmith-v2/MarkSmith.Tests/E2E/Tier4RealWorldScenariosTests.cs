using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 4: Real-World End-to-End Application Scenarios (≥5 comprehensive workflows).
/// Simulates enterprise production pipelines, legal negotiation redlines, academic publishing,
/// automated AI agent MCP workflows, and massive document streaming.
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

            // 2. Multi-author legal redline markdown
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

            // 5. Surgical In-Place Clause Patch
            var patchReq = new DocxPatchRequest
            {
                Operations = new[]
                {
                    new DocxPatchOperationItem
                    {
                        Op = PatchOperation.InsertAfter,
                        Target = new BlockSelector { HeadingPath = "Governing Law" },
                        Content = "## 4. Injunctive Relief\nBoth parties acknowledge that unauthorized disclosure causes irreparable harm warranting immediate injunctive relief."
                    }
                }
            };

            var (patchedBytes, patchResult) = E2ETestContext.ApplyDocxPatch(docxBytes, patchReq);
            Assert.True(patchResult.Success);

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
    public async Task T4_Scenario2_AcademicMathPaperWithFootnotes()
    {
        var academicMd = @"# On the Convergence of Stochastic Optimization in Deep Networks

## Abstract
We present a rigorous analysis of adaptive gradient descent methods.

## Mathematical Formulation
The optimization objective is given by:

$$\min_{\mathbf{w} \in \mathbb{R}^d} \mathbb{E}_{\xi} [f(\mathbf{w}, \xi)] + \frac{\lambda}{2} \|\mathbf{w}\|_2^2$$

where $\xi$ represents stochastic batch samples.

## Empirical Convergence
| Epoch | Train Loss | Validation Loss | Accuracy (%) |
|---|---|---|---|
| 10 | 0.421 | 0.450 | 88.5 |
| 50 | 0.112 | 0.130 | 96.2 |
| 100 | 0.035 | 0.048 | 99.1 |

### Key Results
1. Quadratic convergence guaranteed under Polyak-Łojasiewicz condition.
2. Step size scaling preserves asymptotic stability.";

        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(academicMd);
        var errors = E2ETestContext.ValidateDocxSchema(docxBytes);
        Assert.Empty(errors);

        var report = E2ETestContext.InspectDocx(docxBytes);
        Assert.Equal("On the Convergence of Stochastic Optimization in Deep Networks", report.Title);
        Assert.Equal(1, report.TotalTables);
        Assert.True(report.TotalParagraphs >= 8);
    }

    [Fact]
    public async Task T4_Scenario3_AiAgentMcpDocumentPipeline()
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"agent-doc-{Guid.NewGuid():N}.docx");
        try
        {
            // Step 1: Agent calls render_markdown_to_docx
            var renderReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "agent-step-1",
                method = "tools/call",
                @params = new
                {
                    name = "render_markdown_to_docx",
                    arguments = new
                    {
                        markdown = "# Automated Infrastructure Report\n\nGenerated by Autonomous Cloud Agent.",
                        output_path = tempDocx
                    }
                }
            });
            var renderRes = await E2ETestContext.SimulateMcpJsonRpcAsync(renderReq);
            using (var doc = JsonDocument.Parse(renderRes))
            {
                Assert.True(doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            }

            // Step 2: Agent calls inspect_docx
            var inspectReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "agent-step-2",
                method = "tools/call",
                @params = new { name = "inspect_docx", arguments = new { docx_path = tempDocx } }
            });
            var inspectRes = await E2ETestContext.SimulateMcpJsonRpcAsync(inspectReq);
            using (var doc = JsonDocument.Parse(inspectRes))
            {
                var title = doc.RootElement.GetProperty("result").GetProperty("report").GetProperty("title").GetString();
                Assert.Equal("Automated Infrastructure Report", title);
            }

            // Step 3: Agent patches docx
            var patchReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "agent-step-3",
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
                                    Content = "## Cluster Health\nAll 64 nodes operating in healthy status (0% packet drop)."
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

            // Step 4: Agent converts back to markdown
            var convReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "agent-step-4",
                method = "tools/call",
                @params = new { name = "convert_docx_to_markdown", arguments = new { docx_path = tempDocx } }
            });
            var convRes = await E2ETestContext.SimulateMcpJsonRpcAsync(convReq);
            using (var doc = JsonDocument.Parse(convRes))
            {
                var md = doc.RootElement.GetProperty("result").GetProperty("markdown").GetString();
                Assert.Contains("Cluster Health", md);
            }
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T4_Scenario4_CollaborativePolicyDocumentRoundTrip()
    {
        var originalPolicyMd = @"# Global Remote Work Policy

## 1. Eligibility
All full-time employees with at least {~~3 months~>6 months~~} tenure are eligible for remote flexibility.

## 2. Core Working Hours
Team members must be available during core collaboration hours: {++10:00 AM to 3:00 PM local time++}.

## 3. Equipment Reimbursement
Home office stipend provides up to {==$1,000 annually==}{>>People Ops: Confirm budget allocation for APAC region<<}.";

        // 1. Export
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(originalPolicyMd);
        var errors = E2ETestContext.ValidateDocxSchema(docxBytes);
        Assert.Empty(errors);

        // 2. Word Reviewer In-Place Patch
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Append,
                    Content = "## 4. Security & Compliance\nEmployees must use corporate VPN and hardware-backed 2FA tokens for all system access."
                }
            }
        };

        var (patchedBytes, patchResult) = E2ETestContext.ApplyDocxPatch(docxBytes, patchReq);
        Assert.True(patchResult.Success);

        // 3. Reverse import back to markdown
        var tempDocx = Path.Combine(Path.GetTempPath(), $"policy-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, patchedBytes);
        try
        {
            var reverse = new ReverseImportService();
            var reversedMd = reverse.ImportFromDocx(tempDocx);
            Assert.Contains("Global Remote Work Policy", reversedMd);
            Assert.Contains("Eligibility", reversedMd);
            Assert.Contains("Security & Compliance", reversedMd);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T4_Scenario5_Massive1000PageDocumentSaxStreamAndPatch()
    {
        var sb = new StringBuilder("# Enterprise Master Document\n\n");
        for (int i = 1; i <= 500; i++)
        {
            sb.AppendLine($"## Section {i}\nParagraph content for high volume streaming benchmark section {i}.");
        }

        // 1. Stream export
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.NotEmpty(docxBytes);

        var errors = E2ETestContext.ValidateDocxSchema(docxBytes);
        Assert.Empty(errors);

        // 2. Inspect structure
        var report = E2ETestContext.InspectDocx(docxBytes);
        Assert.Equal("Enterprise Master Document", report.Title);
        Assert.True(report.TotalParagraphs >= 500);

        // 3. Surgical patch at paragraph 250
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 250 },
                    Content = "Surgically updated critical block at middle of massive document."
                }
            }
        };

        var (patchedBytes, patchResult) = E2ETestContext.ApplyDocxPatch(docxBytes, patchReq);
        Assert.True(patchResult.Success);
        Assert.Equal(1, patchResult.ModifiedBlocks);

        var docXml = E2ETestContext.ReadZipPartXml(patchedBytes, "word/document.xml")!;
        Assert.Contains("Surgically updated critical block", docXml);
    }
}
