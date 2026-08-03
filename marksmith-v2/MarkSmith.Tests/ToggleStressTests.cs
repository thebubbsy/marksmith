using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ToggleStressTests
{
    private static string ExportToXml(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-toggle-stress-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void ValidateOpenXmlDocument(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-toggle-val-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
            using var wordDoc = WordprocessingDocument.Open(path, false);
            var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
            var errors = validator.Validate(wordDoc)
                .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility &&
                            e.Node?.LocalName != "collapsed" &&
                            !(e.Description?.Contains("collapsed") ?? false))
                .ToList();

            if (errors.Count > 0)
            {
                var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
                Assert.Fail($"Validation failed with {errors.Count} errors:\n{msg}");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // =========================================================================
    // Category 1: Empty Strings & Whitespace Edge Cases
    // =========================================================================

    [Fact]
    public void Stress_EmptyTitleInToggle_FallsBackGracefully()
    {
        var md = ":::toggle []\nContent inside empty title toggle.\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("Toggle", xml);
        Assert.Contains("Content inside empty title toggle", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_WhitespaceOnlyTitle_FallsBackGracefully()
    {
        var md = ":::toggle [   ]\nContent inside whitespace title toggle.\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("Toggle", xml);
        Assert.Contains("Content inside whitespace title toggle", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_EmptySummaryInHtmlDetails_FallsBackToDetails()
    {
        var md = "<details>\n<summary></summary>\nBody under empty summary.\n</details>\n";
        var xml = ExportToXml(md);
        Assert.Contains("Body under empty summary", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_EmptyBodyToggle_RendersHeaderWithoutError()
    {
        var md = ":::toggle [Empty Body Header]\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("Empty Body Header", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_EmptyDetailsElement_RendersWithoutCrashing()
    {
        var md = "<details></details>\n";
        var xml = ExportToXml(md);
        Assert.NotNull(xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_ExtremelyLargeTitle_RendersAndValidates()
    {
        var longTitle = new string('A', 5000);
        var md = $":::toggle [{longTitle}]\nLarge title body.\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains(longTitle, xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_ExtremelyLargeBody_RendersAndValidates()
    {
        var bodyLines = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"Paragraph {i}: " + new string('X', 100)));
        var md = $":::toggle [Huge Body]\n{bodyLines}\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("Huge Body", xml);
        Assert.Contains("Paragraph 200", xml);
        ValidateOpenXmlDocument(md);
    }

    // =========================================================================
    // Category 2: Deep Nesting Edge Cases
    // =========================================================================

    [Fact]
    public void Stress_DeepNesting_5Levels_RendersAllTitlesAndValidates()
    {
        var md = @":::toggle [Level 1]
L1 Content
:::toggle [Level 2]
L2 Content
:::toggle [Level 3]
L3 Content
:::toggle [Level 4]
L4 Content
:::toggle [Level 5]
L5 Deepest Content
:::
:::
:::
:::
:::
";
        var xml = ExportToXml(md);
        Assert.Contains("Level 1", xml);
        Assert.Contains("Level 2", xml);
        Assert.Contains("Level 3", xml);
        Assert.Contains("Level 4", xml);
        Assert.Contains("Level 5", xml);
        Assert.Contains("L5 Deepest Content", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_DeepNesting_10Levels_RendersWithoutStackOverflow()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 10; i++)
        {
            sb.AppendLine($":::toggle [Nested Level {i}]");
            sb.AppendLine($"Content at level {i}");
        }
        for (int i = 10; i >= 1; i--)
        {
            sb.AppendLine(":::");
        }

        var xml = ExportToXml(sb.ToString());
        Assert.Contains("Nested Level 1", xml);
        Assert.Contains("Nested Level 10", xml);
        Assert.Contains("Content at level 10", xml);
        ValidateOpenXmlDocument(sb.ToString());
    }

    [Fact]
    public void Stress_InterleavedHtmlDetailsAndMarkdownToggleNesting()
    {
        var md = @"<details>
<summary>Outer HTML Details</summary>
Outer content.

:::toggle [Inner Markdown Toggle]
Inner content.

<details>
<summary>Deepest HTML Details</summary>
Deepest content.
</details>
:::
</details>
";
        var xml = ExportToXml(md);
        Assert.Contains("Outer HTML Details", xml);
        Assert.Contains("Inner Markdown Toggle", xml);
        Assert.Contains("Deepest HTML Details", xml);
        ValidateOpenXmlDocument(md);
    }

    // =========================================================================
    // Category 3: Special Characters, Encoding & Unicode
    // =========================================================================

    [Fact]
    public void Stress_SpecialXmlCharactersInTitleAndBody_EscapedCorrectly()
    {
        var md = ":::toggle [<Header> & \"Quotes\" 'Single' <script>alert(1)</script>]\nBody with <xml> & \"chars\" & 'more'.\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("&lt;Header&gt;", xml);
        Assert.Contains("&amp;", xml);
        Assert.Contains("alert(1)", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_UnicodeAndEmojisInTitleAndBody()
    {
        var md = ":::toggle [🚀 Launching Quantum Server ⚛️ 示例 🚀]\n🔥 Status: 100% operational! ⚡\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("Launching Quantum Server", xml);
        Assert.Contains("示例", xml);
        Assert.Contains("Status: 100%", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_NewlinesAndTabsInTitle()
    {
        var md = ":::toggle [Title with \t tabs]\nBody text.\n:::\n";
        var xml = ExportToXml(md);
        Assert.Contains("Title with", xml);
        ValidateOpenXmlDocument(md);
    }

    // =========================================================================
    // Category 4: Mixed HTML and Markdown Edge Cases
    // =========================================================================

    [Fact]
    public void Stress_MarkdownFormattingInHtmlSummary_StripsOrRendersCleanly()
    {
        var md = "<details>\n<summary>**Bold Summary** and *Italic*</summary>\nBody content.\n</details>\n";
        var xml = ExportToXml(md);
        Assert.Contains("Summary", xml);
        Assert.Contains("Body content", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_HtmlTableInsideMarkdownToggle()
    {
        var md = @":::toggle [Toggle With HTML Table]
<table>
<tr><th>Col 1</th><th>Col 2</th></tr>
<tr><td>Val 1</td><td>Val 2</td></tr>
</table>
:::
";
        var xml = ExportToXml(md);
        Assert.Contains("Toggle With HTML Table", xml);
        Assert.Contains("Col 1", xml);
        Assert.Contains("Val 1", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_CodeBlockInsideToggleWithClosingMarkerInsideCode()
    {
        var md = @":::toggle [Code Sample Toggle]
Here is how to write a toggle:
```markdown
:::toggle [Nested Code Example]
Sample code inside
:::
```
More body after code block.
:::
";
        var xml = ExportToXml(md);
        Assert.Contains("Code Sample Toggle", xml);
        Assert.Contains("Nested Code Example", xml);
        Assert.Contains("More body after code block", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_UnclosedToggleAtEof_GracefullyConsumesRestOfDocument()
    {
        var md = ":::toggle [Unclosed Toggle]\nThis content is inside the unclosed toggle.\nNo closing fence provided.\n";
        var xml = ExportToXml(md);
        Assert.Contains("Unclosed Toggle", xml);
        Assert.Contains("This content is inside the unclosed toggle", xml);
        Assert.Contains("No closing fence provided", xml);
        ValidateOpenXmlDocument(md);
    }

    [Fact]
    public void Stress_FoldedObsidianCallout_RendersAsCollapsibleToggle()
    {
        var md = "> [!note]- Folded Obsidian Note\n> This note is collapsed by default.\n> Line 2 of folded note.\n";
        var xml = ExportToXml(md);
        Assert.Contains("NOTE · Folded Obsidian Note", xml);
        Assert.Contains("This note is collapsed by default", xml);
        Assert.Contains("outlineLvl", xml);
        ValidateOpenXmlDocument(md);
    }
}
