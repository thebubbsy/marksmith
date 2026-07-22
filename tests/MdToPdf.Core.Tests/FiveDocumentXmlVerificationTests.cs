using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class FiveDocumentXmlVerificationTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    private static string ConvertAndExtractXml(string markdown, string filenamePrefix)
    {
        AppServices.License.Load();
        var tempDir = Path.Combine(Path.GetTempPath(), "marksmith_5docs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var docxPath = Path.Combine(tempDir, filenamePrefix + ".docx");
        var exporter = new DocxExportService();
        var settings = new AppSettings { Theme = "Cyberpunk" };

        exporter.ExportAsync(markdown, docxPath, settings).GetAwaiter().GetResult();

        Assert.True(File.Exists(docxPath));

        using var archive = ZipFile.OpenRead(docxPath);
        var entry = archive.GetEntry("word/document.xml");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open());
        var xmlText = reader.ReadToEnd();

        var outputXmlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scratch", filenamePrefix + "_document.xml");
        try
        {
            var scratchDir = Path.GetDirectoryName(outputXmlPath);
            if (!string.IsNullOrEmpty(scratchDir)) Directory.CreateDirectory(scratchDir);
            File.WriteAllText(outputXmlPath, xmlText);
        }
        catch { }

        try { Directory.Delete(tempDir, true); } catch { }

        return xmlText;
    }

    [Fact]
    public void Doc1_ExecutiveReport_ContainsValidTableShadingAndFootnotes()
    {
        var markdown = """
            # Executive Quarterly Performance Report

            > [!NOTE]
            > All financial metrics in this report have been audited according to GAAP standards.

            ## Revenue Summary

            | Region | Q1 Revenue | Q2 Target | YoY Growth | Status |
            |--------|------------|-----------|------------|--------|
            | APAC   | $14.2M     | $16.0M    | +18.5%     | Exceeded |
            | EMEA   | $9.8M      | $10.5M    | +8.2%      | On Track |
            | AMER   | $22.4M     | $21.0M    | +24.1%     | Exceeded |

            Financial projections assume sustained operational efficiency[^1].

            [^1]: Verified by Chief Financial Officer on Q2 close date.
            """;

        var xml = ConvertAndExtractXml(markdown, "Doc1_ExecutiveReport");
        var doc = XDocument.Parse(xml);

        // 1. Check Table structure (<w:tbl>, <w:tr>, <w:tc>)
        var tables = doc.Descendants(W + "tbl").ToList();
        Assert.NotEmpty(tables);

        // Data table should have 4 rows (1 header + 3 data rows)
        var dataTable = tables.FirstOrDefault(t => t.Descendants(W + "tr").Count() >= 4);
        Assert.NotNull(dataTable);

        // Check zebra shading / background shading (<w:shd>)
        var shadings = dataTable.Descendants(W + "shd").ToList();
        Assert.NotEmpty(shadings);

        // 2. Check Footnote Reference superscript (<w:vertAlign w:val="superscript"/>)
        var superscripts = doc.Descendants(W + "vertAlign").Where(v => v.Attribute(W + "val")?.Value == "superscript").ToList();
        Assert.NotEmpty(superscripts);

        // 3. Check NOTE callout box icon & title text
        var allText = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value));
        Assert.Contains("Executive Quarterly Performance Report", allText);
        Assert.Contains("APAC", allText);
        Assert.Contains("EMEA", allText);
        Assert.Contains("AMER", allText);
        Assert.Contains("GAAP", allText);
    }

    [Fact]
    public void Doc2_EngineeringMath_ContainsRealOMMLEquations()
    {
        var markdown = """
            # Quantum Harmonic Oscillator Specification

            ## 1. Energy Quantization

            The energy eigenvalues for a one-dimensional quantum harmonic oscillator are given by:

            $$E_n = \hbar \omega \left(n + \frac{1}{2}\right)$$

            where $n \in \mathbb{N}_0$ represents the quantum number and $\hbar$ is the reduced Planck constant.

            ## 2. Wavefunction Normalization

            The spatial probability density follows:

            $$\psi_n(x) = \frac{1}{\sqrt{2^n n!}} \left(\frac{m\omega}{\pi \hbar}\right)^{1/4} e^{-\frac{m\omega x^2}{2\hbar}} H_n\left(\sqrt{\frac{m\omega}{\hbar}} x\right)$$

            Use `qho_solver.py` with `--state=3` to compute wavefunctions.
            """;

        var xml = ConvertAndExtractXml(markdown, "Doc2_EngineeringMath");
        var doc = XDocument.Parse(xml);

        // Verify OMML Equation nodes (<m:oMath> or <m:oMathPara>)
        var mathElements = doc.Descendants(M + "oMath").ToList();
        Assert.NotEmpty(mathElements);

        // Verify Fraction elements (<m:f>, <m:num>, <m:den>)
        var fractions = doc.Descendants(M + "f").ToList();
        Assert.NotEmpty(fractions);

        // Verify Radical elements (<m:rad>)
        var radicals = doc.Descendants(M + "rad").ToList();
        Assert.NotEmpty(radicals);

        var allText = string.Join(" ", doc.Descendants(W + "t").Concat(doc.Descendants(M + "t")).Select(t => t.Value));
        Assert.Contains("Quantum Harmonic Oscillator Specification", allText);
        Assert.Contains("qho_solver.py", allText);
    }

    [Fact]
    public void Doc3_MermaidFlowchart_ContainsNativeWordShapes()
    {
        var markdown = """
            # Automated Order Processing Architecture

            ## Pipeline Workflow

            ```mermaid
            flowchart LR
              A[Customer Order] --> B{Payment Gateway}
              B -- Approved --> C[Warehouse Fulfillment]
              B -- Declined --> D[Notify Customer]
              C --> E[Carrier Dispatch]
            ```

            Every stage in this pipeline emits real-time telemetry events to Kafka.
            """;

        var xml = ConvertAndExtractXml(markdown, "Doc3_MermaidFlowchart");
        var doc = XDocument.Parse(xml);

        // Verify drawing container (<w:drawing>)
        var drawings = doc.Descendants(W + "drawing").ToList();
        Assert.NotEmpty(drawings);

        var allText = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value));
        Assert.Contains("Automated Order Processing Architecture", allText);
        Assert.Contains("Customer Order", allText);
        Assert.Contains("Payment Gateway", allText);
        Assert.Contains("Warehouse Fulfillment", allText);
        Assert.Contains("Carrier Dispatch", allText);
    }

    [Fact]
    public void Doc4_AlertCalloutsAndInlineColor_ContainsIconsAndColorRuns()
    {
        var markdown = """
            # Infrastructure Alert & Security Playbook

            > [!NOTE]
            > Standard system maintenance occurs every Sunday at 02:00 UTC.

            > [!TIP]
            > Enable multi-factor authentication across all admin accounts for 99.9% protection.

            > [!IMPORTANT]
            > API keys must never be committed to public git repositories!

            > [!WARNING]
            > High CPU utilization detected on cluster `us-east-prod-04`.

            > [!CAUTION]
            > Initiating factory reset will permanently erase all unbacked data!

            Text can also use <span style="color: #ff0055">custom neon pink</span> and <font color="#00ff9f">electric green</font> styling!
            """;

        var xml = ConvertAndExtractXml(markdown, "Doc4_AlertCalloutsAndInlineColor");
        var doc = XDocument.Parse(xml);

        // Verify color runs (<w:color w:val="FF0055"/> and <w:color w:val="00FF9F"/>)
        var colorElements = doc.Descendants(W + "color").Select(c => c.Attribute(W + "val")?.Value?.ToUpperInvariant()).ToList();
        Assert.Contains("FF0055", colorElements);
        Assert.Contains("00FF9F", colorElements);

        var allText = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value));
        Assert.Contains("NOTE", allText);
        Assert.Contains("TIP", allText);
        Assert.Contains("IMPORTANT", allText);
        Assert.Contains("WARNING", allText);
        Assert.Contains("CAUTION", allText);
        Assert.Contains("custom neon pink", allText);
        Assert.Contains("electric green", allText);
    }

    [Fact]
    public void Doc5_ListsAndBlockquotes_ContainsNumberingAndCheckboxes()
    {
        var markdown = """
            # Product Launch Task Checklist

            ## 1. Pre-launch Requirements
            - [x] Code freeze completed and tagged `v1.0.0`
            - [x] Security audit & penetration testing passed
            - [ ] Finalize customer documentation and release notes

            ## 2. Infrastructure Setup
            1. Provision production clusters
               - Region 1: US East (N. Virginia)
               - Region 2: EU West (Ireland)
            2. Configure DNS & Cloudflare CDN

            > "Quality means doing it right when no one is looking."
            > — *Henry Ford*
            """;

        var xml = ConvertAndExtractXml(markdown, "Doc5_ListsAndBlockquotes");
        var doc = XDocument.Parse(xml);

        // Verify List Numbering (<w:numPr>)
        var numProps = doc.Descendants(W + "numPr").ToList();
        Assert.NotEmpty(numProps);

        // Verify Paragraph left borders for Blockquote (<w:pBdr><w:left .../></w:pBdr>)
        var pBorders = doc.Descendants(W + "pBdr").ToList();
        Assert.NotEmpty(pBorders);

        var allText = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value));
        Assert.Contains("Product Launch Task Checklist", allText);
        Assert.Contains("Code freeze completed", allText);
        Assert.Contains("Henry Ford", allText);
    }
}
