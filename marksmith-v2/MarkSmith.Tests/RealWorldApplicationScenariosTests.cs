using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class RealWorldApplicationScenariosTests
{
    // =========================================================================
    // Tier 4: Real-World Application Scenarios
    // =========================================================================

    [Fact]
    public async Task Scenario_01_Bilingual_International_Commercial_Contract()
    {
        var md = @":::cover-page theme=""corporate""
title: Cross-Border Master Services Agreement
subtitle: International Technology Licensing
author: General Counsel
organization: Global Enterprises Inc. & SAS International
date: 2026-09-01
:::

:::watermark ""BINDING CONTRACT & CONFIDENTIAL""

# Recitals / Exposé

:::parallel ""English (Governing Language)"" | ""Français (Traduction Juridique)""
This Master Services Agreement is entered into by and between Global Enterprises Inc. and SAS International.
===
Le présent Contrat-Cadre de Prestations est conclu entre Global Enterprises Inc. et SAS International.
---
Clause 1: Scope of Licensed Technology. The Licensor grants a non-exclusive license.
===
Article 1 : Portée de la Technologie Concédée. Le Concédant accorde une licence non exclusive.
:::

# Fee Schedule & Milestones

| Milestone | Deliverable | Amount (USD) |
| :--- | :--- | ---: |
| Phase 1 | Architecture Design | $25,000.00 |
| Phase 2 | Implementation | $75,000.00 |
| Phase 3 | Deployment & UAT | $50,000.00 |
| Total Contract Value | | =SUM(ABOVE) ""$#,##0.00"" |

# Execution & Signatures

Signatory Name: [text: ""Authorized Officer""]
Execution Date: [date: 2026-09-01]
Jurisdiction: [dropdown: State of California | Tribunal de Commerce de Paris]
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("<w:sdt>", docXml);
            Assert.Contains("=SUM(ABOVE)", docXml);
            Assert.Contains("Global Enterprises Inc.", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("cover-page", html);
            Assert.Contains("parallel", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("150,000.00", html);
            Assert.Contains("type=\"date\"", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Scenario_02_Executive_Whitepaper_And_Corporate_Architecture_Brief()
    {
        var md = @":::cover-page theme=""modern""
title: Next-Gen Cloud Platform Architecture
subtitle: Multi-Region Event-Driven Processing at Scale
author: Cloud Architecture Council
organization: Enterprise Cloud Consortium
date: 2026-08-23
abstract: This whitepaper details the architectural paradigm shift toward zero-trust, event-driven distributed microservices.
:::

:::watermark ""PROPRIETARY & CONFIDENTIAL""

# Executive Overview

:::dropcap 3
Enterprise software infrastructure in 2026 demands unparalleled fault tolerance, deterministic low-latency messaging, and automated cross-region replication.
:::

> [!NOTE]
> All core data pipelines comply with SOC-2 Type II and ISO/IEC 27001 certifications.

## Key Architectural Highlights

- **99.999% SLA**: Automated failover within 250 milliseconds.
- **Zero-Trust Networking**: Mutual TLS across all ingress/egress boundaries.
- **Native OpenXML Engine**: Deterministic document streaming with zero memory fragmentation.
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("Next-Gen Cloud Platform Architecture", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("cover-page", html);
            Assert.Contains("dropcap", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Scenario_03_Peer_Reviewed_Academic_Article_And_Legal_Brief()
    {
        var md = @":::line-numbers count-by=5 restart=""per-page""

# Empirical Analysis of Distributed Consensus Under High Latency

**Dr. Alex Rivera**  
*Department of Computer Science, Institute of Advanced Computation*

:::dropcap
Consensus protocols form the foundation of fault-tolerant distributed computation. In this paper, we evaluate the performance bounds of Raft^[index: ""Consensus:Raft""] and Byzantine Fault Tolerance^[index: ""Consensus:PBFT""] under simulated oceanic WAN latency conditions.

## Mathematical Formulation

The bound on message complexity is given by:

$$\Omega(n \log n)$$

Where $n$ represents the total quorum size[^1].

## Experimental Findings

Extensive benchmark trials confirm that pipelined state machine replication^[index: ""Replication:State Machine""] reduces commit latency by 42%.

[^1]: Verified across 500-node simulated clusters running on bare metal infrastructure.

:::index count=2
:::
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("Consensus:Raft", docXml);
            Assert.Contains("INDEX", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("line-number", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("index", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Scenario_04_Corporate_Quarterly_Financial_Statement()
    {
        var md = @":::cover-page theme=""classic""
title: Q2 2026 Consolidated Financial Statements
subtitle: Unaudited Interim Earnings Report
author: Office of the Chief Financial Officer
organization: Apex Global Holdings
date: 2026-08-23
:::

:::watermark ""AUDITED FINANCIAL STATEMENT""

# Condensed Consolidated Balance Sheet

| Operating Segment | Q1 (USD) | Q2 (USD) | Half-Year Sum |
| :--- | ---: | ---: | ---: |
| Cloud Services | 450000 | 550000 | =SUM(LEFT) |
| Enterprise Software | 320000 | 380000 | =SUM(LEFT) |
| Professional Services | 120000 | 140000 | =SUM(LEFT) |
| Total Revenue | =SUM(ABOVE) | =SUM(ABOVE) | =SUM(ABOVE) |
| Average Monthly Run-Rate | =AVERAGE(ABOVE) | =AVERAGE(ABOVE) | =AVERAGE(ABOVE) |

:::parallel ""Management Discussion"" | ""Explication de la Direction""
Net profit margins expanded by 340 basis points year-over-year.
===
Les marges bénéficiaires nettes ont augmenté de 340 points de base par rapport à l'année précédente.
:::
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("=SUM(LEFT)", docXml);
            Assert.Contains("=SUM(ABOVE)", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("1000000", html); // 450k + 550k = 1M
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Scenario_05_Government_Regulatory_Compliance_And_Medical_Intake_Form()
    {
        var md = @":::watermark ""OFFICIAL HEALTH RECORD""

# Patient Clinical Intake & Regulatory Consent Form

**Provider**: National Health Services Network  
**Form ID**: NHS-MED-2026-V4  

## Patient Information

Full Legal Name: [text: ""Family Name, Given Name""]  
Date of Birth: [date: 1980-01-01]  
Primary Care Facility: [dropdown: St. Jude Hospital | Central Health Clinic | Metro University Hospital]  

## Medical History & Compliance Verification

| Clinical Assessment Checklist | Verified | Staff Sign-off |
| :--- | :---: | :--- |
| HIPAA Privacy Notice Acknowledged | - [x] | [text: ""Staff Initials""] |
| Informed Consent for Treatment Signed | - [x] | [text: ""Staff Initials""] |
| Known Drug Allergies Documented | - [ ] | [text: ""Staff Initials""] |

Reviewing Physician: [text: ""Dr. Signature""]  
Verification Date: [date: 2026-08-23]  
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:sdt>", docXml);
            Assert.Contains("<w14:checkbox", docXml);
            Assert.Contains("<w:dropDownList", docXml);
            Assert.Contains("<w:date", docXml);
            Assert.Contains("<w:headerReference", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("<select", html);
            Assert.Contains("type=\"date\"", html);
            Assert.Contains("type=\"checkbox\"", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Scenario_06_Published_Book_Chapter_And_Archival_Monograph()
    {
        var md = @":::cover-page theme=""classic""
title: The Chronicles of Typography
subtitle: From Gutenberg to OpenXML
author: Professor Marcus Vance
organization: Oxford University Press
date: 2026-08-23
:::

# Chapter 1: The Incunabula Era

:::dropcap 3
Printing in Western Europe began with movable metal type in Mainz^[index: ""History:Mainz""], Germany. The innovation revolutionized information dissemination across the continent.
:::

The original manuscript contained {--erroneous dates--}{++verified historical records++}.^[Editor (2026-08-23): ""Confirmed against original parchment archives.""]

The development of the printing press^[index: ""Technology:Printing Press""] spurred the Renaissance and scientific enlightenment.

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
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("History:Mainz", docXml);
            Assert.Contains("INDEX", docXml);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml");
            Assert.NotNull(commentsXml);
            Assert.Contains("Editor", commentsXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("cover-page", html);
            Assert.Contains("dropcap", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<del", html);
            Assert.Contains("<ins", html);
            Assert.Contains("index", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
