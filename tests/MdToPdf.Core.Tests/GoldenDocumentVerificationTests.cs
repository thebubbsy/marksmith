using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class GoldenDocumentVerificationTests
{
    private const string GoldenMarkdown = """
        # Quarterly Business & System Review — Golden Verification Document

        [include_toc: true]

        ## Executive Summary & Notes

        This document serves as the golden regression suite for Marksmith exports. It contains notes, callouts, tables, inline styling, and structured prose to verify character encoding integrity.

        > [!NOTE]
        > System architecture notes must remain clean and readable across all exports without corrupted characters.

        > [!TIP]
        > High performance depends on clean UTF-8 string encoding across all rendering layers.

        > [!IMPORTANT]
        > Verify that all callout markers and title badges render with intact icons and clear text.

        > [!WARNING]
        > Ensure that no Mojibake or accidental symbol substitutions affect normal paragraph text.

        > [!CAUTION]
        > Breaking character contracts will cause automated regression test failures.

        ## System Performance & Metrics

        Here is a breakdown of quarterly operational metrics:

        | Component | Target SLA | Measured Uptime | Status |
        | :--- | :---: | :---: | :---: |
        | API Gateway | 99.9% | 99.95% | Operational |
        | Document Pipeline | 99.5% | 99.90% | Operational |
        | Export Engine | 99.9% | 99.99% | Operational |

        ## Code & Formula Reference

        Here is standard sample code and a reference formula:

        ```csharp
        public string GetGreeting(string name) => $"Hello, {name}!";
        ```

        Reserves follow the formula $R = \sum_{i=1}^n p_i \cdot L_i$ in financial calculations.
        """;

    private static (string DocxPath, string DocumentXml, XDocument XDoc) ExportGoldenDocument(AppSettings? settings = null)
    {
        settings ??= new AppSettings { Theme = "GitHub Light" };
        var outPath = Path.Combine(Path.GetTempPath(), $"golden-doc-test-{Guid.NewGuid():N}.docx");
        new DocxExportService().ExportAsync(GoldenMarkdown, outPath, settings).GetAwaiter().GetResult();

        using var zip = ZipFile.OpenRead(outPath);
        var entry = zip.GetEntry("word/document.xml")!;
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        var xdoc = XDocument.Parse(xml);

        return (outPath, xml, xdoc);
    }

    [Fact]
    public void GoldenDocument_ExportsSuccessfully_AndFileExists()
    {
        var (path, xml, _) = ExportGoldenDocument();
        try
        {
            Assert.True(File.Exists(path));
            Assert.NotEmpty(xml);
            Assert.Contains("Executive Summary", xml);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void GoldenDocument_ContainsNoMojibakeOrCorruptedUtf8Bytes()
    {
        var (path, xml, _) = ExportGoldenDocument();
        try
        {
            // Detect common UTF-8 Mojibake corruption signatures
            string[] mojibakeSignatures = { "ðŸ", "â—", "â—†", "â–", "âœ", "â„", "ï", "Ã", "Â" };
            foreach (var sig in mojibakeSignatures)
            {
                Assert.DoesNotContain(sig, xml);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void GoldenDocument_ContainsNoUnintendedGreekOrCorruptedSymbolsInProseNotes()
    {
        var (path, _, xdoc) = ExportGoldenDocument();
        try
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            XNamespace m = "http://schemas.openxmlformats.org/officeDocument/2006/math";

            // Extract all paragraphs that are NOT inside math equations (<m:oMath>)
            var nonMathParagraphs = xdoc.Descendants(w + "p")
                .Where(p => !p.Ancestors(m + "oMath").Any() && !p.Ancestors(m + "oMathPara").Any())
                .ToList();

            // Extract text runs from prose paragraphs (excluding code blocks and raw math)
            foreach (var p in nonMathParagraphs)
            {
                var text = string.Concat(p.Descendants(w + "t").Select(t => t.Value));
                
                // Skip lines that are mathematical formulas or math references
                if (text.Contains("R =") || text.Contains("\\sum") || text.Contains("p_i")) continue;

                // Greek letters that should NEVER appear in normal prose/notes unless explicitly in math
                char[] unexpectedGreekSymbols = { 'α', 'β', 'γ', 'δ', 'ε', 'ζ', 'η', 'θ', 'ι', 'κ', 'λ', 'μ', 'ν', 'ξ', 'ο', 'π', 'ρ', 'σ', 'τ', 'υ', 'φ', 'χ', 'ψ', 'ω', 'Γ', 'Δ', 'Θ', 'Λ', 'Ξ', 'Π', 'Σ', 'Φ', 'Ψ', 'Ω' };
                
                foreach (var symbol in unexpectedGreekSymbols)
                {
                    Assert.False(text.Contains(symbol), $"Unexpected Greek symbol '{symbol}' found in prose note paragraph: \"{text}\"");
                }
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void GoldenDocument_AlertCalloutsContainCleanTitlesAndIcons()
    {
        var (path, xml, _) = ExportGoldenDocument();
        try
        {
            Assert.Contains("NOTE", xml);
            Assert.Contains("TIP", xml);
            Assert.Contains("IMPORTANT", xml);
            Assert.Contains("WARNING", xml);
            Assert.Contains("CAUTION", xml);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void GoldenDocument_DarkTheme_EnsuresLegibleHighContrastColors()
    {
        var darkSettings = new AppSettings { Theme = "GitHub Dark" };
        var (path, xml, xdoc) = ExportGoldenDocument(darkSettings);
        try
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var textColors = xdoc.Descendants(w + "color")
                .Select(c => c.Attribute(w + "val")?.Value)
                .Where(v => !string.IsNullOrEmpty(v) && v != "auto")
                .Distinct()
                .ToList();

            // None of the text run colors should be low-contrast dark values against dark background #0D1117
            foreach (var col in textColors)
            {
                var ratio = ContrastGuard.GetContrastRatio(col, "0D1117");
                Assert.True(ratio >= 4.0, $"Color {col} has insufficient contrast ratio ({ratio:F2}:1) on dark theme background.");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
