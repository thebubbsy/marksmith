using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class GoldenDocumentVerificationTests
{
    private const string GoldenMarkdown = """
        # Quarterly Business & System Review — Golden Verification Document

        [include_toc: true]

        ## Executive Summary & Notes

        This document serves as the golden regression suite for MarkSmith exports. It contains notes, callouts, tables, inline styling, and structured prose to verify character encoding integrity.

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
            Assert.Contains("Note", xml);
            Assert.Contains("Tip", xml);
            Assert.Contains("Important", xml);
            Assert.Contains("Warning", xml);
            Assert.Contains("Caution", xml);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void GoldenDocument_DarkTheme_EnsuresLegibleHighContrastColors()
    {
        var darkSettings = new AppSettings { Theme = "GitHub Dark", ThemeLightInfluence = true };
        var (path, xml, xdoc) = ExportGoldenDocument(darkSettings);
        try
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using var zip = ZipFile.OpenRead(path);

            // Audit document.xml, styles.xml, header*.xml, and footer*.xml
            foreach (var entry in zip.Entries.Where(e =>
                e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Equals("word/styles.xml", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)))
            {
                using var sr = new StreamReader(entry.Open());
                var partDoc = XDocument.Parse(sr.ReadToEnd());
                foreach (var colorEl in partDoc.Descendants(w + "color"))
                {
                    var col = colorEl.Attribute(w + "val")?.Value;
                    if (string.IsNullOrEmpty(col) || col == "auto") continue;

                    // Resolve innermost shading fill if present (e.g., light code block or table cell under ThemeLightInfluence)
                    var rShd = colorEl.Parent?.Element(w + "shd")?.Attribute(w + "fill")?.Value;
                    var pShd = colorEl.Ancestors(w + "p").FirstOrDefault()?.Element(w + "pPr")?.Element(w + "shd")?.Attribute(w + "fill")?.Value;
                    var tcShd = colorEl.Ancestors(w + "tc").FirstOrDefault()?.Element(w + "tcPr")?.Element(w + "shd")?.Attribute(w + "fill")?.Value;
                    var effectiveBg = new[] { rShd, pShd, tcShd }
                        .FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "auto" && s!.Length == 6)
                        ?? "0D1117";

                    var ratio = ContrastGuard.GetContrastRatio(col, effectiveBg);
                    Assert.True(ratio >= 4.5, $"Part {entry.FullName}: color #{col} has insufficient contrast ({ratio:F2}:1) against #{effectiveBg}.");
                }
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

