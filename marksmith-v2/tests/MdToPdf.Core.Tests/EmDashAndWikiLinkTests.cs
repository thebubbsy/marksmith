using System.IO.Compression;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class EmDashAndWikiLinkTests
{
    private static string Export(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-test-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void EmDash_InProse_ConvertsDoubleHyphenToEmDash()
    {
        var xml = Export("text -- text");
        Assert.Contains("text — text", xml);
    }

    [Fact]
    public void EmDash_InFencedCodeBlock_PreservesDoubleHyphen()
    {
        var xml = Export("```csharp\nint x = --y;\n```");
        Assert.Contains("--", xml);
        Assert.DoesNotContain("—", xml);
    }

    [Fact]
    public void EmDash_InInlineCode_PreservesDoubleHyphen()
    {
        var xml = Export("`cmd --flag`");
        Assert.Contains("cmd --flag", xml);
        Assert.DoesNotContain("cmd —flag", xml);
    }

    [Fact]
    public void EmDash_InHtmlComment_PreservesDoubleHyphen()
    {
        var xml = Export("<!-- comment -- text -->");
        Assert.DoesNotContain("comment — text", xml);
    }

    [Fact]
    public void EmDash_HorizontalRule_PreservesTripleHyphen()
    {
        var xml = Export("above\n\n---\n\nbelow");
        Assert.Contains("above", xml);
        Assert.Contains("below", xml);
        Assert.DoesNotContain("—", xml);
    }

    [Fact]
    public void NormalizeDoubleHyphens_MultiLineHtmlComment_PreservesDoubleHyphen()
    {
        var input = "<!--\ncomment -- line 1\ncomment -- line 2\n-->";
        var result = DashReplacer.NormalizeDoubleHyphens(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeDoubleHyphens_MarkdownLinksAndAutolinks_PreservesUrlDoubleHyphen()
    {
        var input = "[link text -- description](https://example.com/foo--bar)";
        var expected = "[link text — description](https://example.com/foo--bar)";
        Assert.Equal(expected, DashReplacer.NormalizeDoubleHyphens(input));

        var autolink = "Check out <https://example.com/api--v2/test--endpoint>";
        Assert.Equal(autolink, DashReplacer.NormalizeDoubleHyphens(autolink));

        var bareUrl = "Visit https://example.com/api--v2/test--endpoint today";
        Assert.Equal(bareUrl, DashReplacer.NormalizeDoubleHyphens(bareUrl));
    }

    [Fact]
    public void NormalizeDoubleHyphens_HtmlTagAndAttributes_PreservesAttributesDoubleHyphen()
    {
        var input = "<div class=\"btn--primary\" data-id=\"item--123\">text -- text</div>";
        var expected = "<div class=\"btn--primary\" data-id=\"item--123\">text — text</div>";
        Assert.Equal(expected, DashReplacer.NormalizeDoubleHyphens(input));
    }

    [Fact]
    public void NormalizeDoubleHyphens_LatexMathExpressions_PreservesMathDoubleHyphen()
    {
        var input = "Formula $a--b$ and $$x = --y$$ in text -- text";
        var expected = "Formula $a--b$ and $$x = --y$$ in text — text";
        Assert.Equal(expected, DashReplacer.NormalizeDoubleHyphens(input));
    }

    [Fact]
    public void NormalizeDoubleHyphens_NestedMultiBacktickCodeBlocks_PreservesInnerFences()
    {
        var input = "````markdown\n```csharp\nint x = --y;\n```\n````";
        Assert.Equal(input, DashReplacer.NormalizeDoubleHyphens(input));
    }


    [Fact]
    public void WikiLink_SingleTarget_ProducesNoProofAndStyling()
    {
        var xml = Export("see [[ProjectPhoenix]] now");
        Assert.Contains("ProjectPhoenix", xml);
        Assert.DoesNotContain("[[", xml);
        Assert.Contains("<w:noProof", xml);
        Assert.Contains("w:val=\"dash\"", xml);
        Assert.Contains("<w:color ", xml);
    }

    [Fact]
    public void WikiLink_TargetAndAlias_ProducesAliasWithNoProofAndStyling()
    {
        var xml = Export("see [[Target|Alias]] now");
        Assert.Contains("Alias", xml);
        Assert.DoesNotContain("Target", xml);
        Assert.DoesNotContain("[[", xml);
        Assert.Contains("<w:noProof", xml);
        Assert.Contains("w:val=\"dash\"", xml);
        Assert.Contains("<w:color ", xml);
    }

    [Fact]
    public void WikiLink_RunProperties_NoProofPrecedesColorAndFontSize()
    {
        var xml = Export("see `code` and [[WikiLink]] now");
        var noProofIdx = xml.IndexOf("<w:noProof");
        var colorIdx = xml.IndexOf("<w:color");
        var szIdx = xml.IndexOf("<w:sz");

        Assert.True(noProofIdx >= 0, "<w:noProof/> should be present");
        Assert.True(colorIdx >= 0, "<w:color/> should be present");
        Assert.True(szIdx >= 0, "<w:sz/> should be present");
        Assert.True(noProofIdx < colorIdx, "<w:noProof/> must precede <w:color/> in w:rPr schema sequence");
        Assert.True(noProofIdx < szIdx, "<w:noProof/> must precede <w:sz/> in w:rPr schema sequence");
    }

    [Fact]
    public void ExportSampleDocumentsForVerification()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MdToPdf.sln")))
        {
            dir = dir.Parent;
        }
        var root = dir?.FullName ?? Path.GetTempPath();
        var outputDir = Path.Combine(root, "tests", "docx_verify_output");
        Directory.CreateDirectory(outputDir);

        var sample1 = "Prose text -- text with em-dash.\n\n```csharp\nint x = --y;\n```\n\ninline `cmd --flag` and <!-- comment -- text -->\n\n---";
        var path1 = Path.Combine(outputDir, "emdash_sample.docx");
        new DocxExportService().ExportAsync(sample1, path1, new AppSettings()).GetAwaiter().GetResult();

        var sample2 = "Here is [[ProjectPhoenix]] and [[Target|Alias]] in text.";
        var path2 = Path.Combine(outputDir, "wikilink_sample.docx");
        new DocxExportService().ExportAsync(sample2, path2, new AppSettings()).GetAwaiter().GetResult();

        Assert.True(File.Exists(path1));
        Assert.True(File.Exists(path2));
    }
}
