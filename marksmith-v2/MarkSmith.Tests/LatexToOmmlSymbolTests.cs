using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Regression cover for LaTeX commands that used to fall through to the converter's verbatim
/// fallback and print their own name mid-equation — <c>A^\top</c> rendering as a superscript
/// "\top", and <c>\boldsymbol\mu</c> as the literal text "\boldsymbol" beside a mu.
///
/// Asserted through the DOCX export rather than against the converter directly: the exported
/// package is where the defect was visible, and it is the surface users actually open in Word.
/// </summary>
public class LatexToOmmlSymbolTests
{
    private static string ExportMath(string latex)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-omml-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService()
                .ExportAsync($"$$\n{latex}\n$$\n", path, new AppSettings())
                .GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            return reader.ReadToEnd();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"A^\top", "⊤")]
    [InlineData(@"x \bot y", "⊥")]
    [InlineData(@"\Gamma \vdash x", "⊢")]
    [InlineData(@"\langle x, y \rangle", "⟨")]
    [InlineData(@"\lceil x \rceil", "⌈")]
    public void Converts_Symbols_That_Previously_Leaked_As_Text(string latex, string expected)
    {
        var xml = ExportMath(latex);
        Assert.Contains(expected, xml);
    }

    [Fact]
    public void Transpose_Does_Not_Leave_The_Command_Name_In_The_Document()
    {
        var xml = ExportMath(@"(AB)^\top = B^\top A^\top");
        // Match the command, not the word: "top" legitimately appears in OOXML attribute names
        // such as w:top and m:topJc.
        Assert.DoesNotContain(@"\top", xml);
        Assert.Equal(3, xml.Split('⊤').Length - 1);
    }

    [Fact]
    public void Boldsymbol_Converts_Its_Unbraced_Argument_And_Marks_It_Bold()
    {
        // \boldsymbol takes the next atom, and it is nearly always written unbraced.
        var xml = ExportMath(@"(\mathbf{x}-\boldsymbol\mu)");
        Assert.Contains("μ", xml);
        Assert.DoesNotContain("boldsymbol", xml);
        Assert.Contains(@"m:val=""bi""", xml); // bold italic
    }

    [Fact]
    public void Boldsymbol_Also_Accepts_A_Braced_Argument()
    {
        var xml = ExportMath(@"\boldsymbol{\Sigma}");
        Assert.Contains("Σ", xml);
        Assert.DoesNotContain("boldsymbol", xml);
    }

    [Fact]
    public void Mathbf_Is_Bold_Not_Merely_Upright()
    {
        var xml = ExportMath(@"\mathbf{v}");
        Assert.Contains(@"m:val=""b""", xml);
    }

    [Theory]
    [InlineData(@"\mathbb{R}", "ℝ")]
    [InlineData(@"\mathbb{N}", "ℕ")]
    [InlineData(@"\mathbb{Z}", "ℤ")]
    [InlineData(@"\mathbb{C}", "ℂ")]
    public void Mathbb_Becomes_Real_Double_Struck_Characters(string latex, string expected)
    {
        // Word has no \mathbb font run, so the codepoint itself has to carry the meaning.
        var xml = ExportMath(latex);
        Assert.Contains(expected, xml);
    }
}
