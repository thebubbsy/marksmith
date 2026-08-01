using System.IO;
using System.Text;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// Markdown (.md) export — the "Export as Markdown" output format and the counterpart to the
// DOCX -> MD reverse pipeline (ReverseImportService). Verifies the recovered/cleaned source lands
// on disk as canonical Markdown: the shared cleanup pipeline is applied, the user's emoji/dash
// preferences are respected, and the file is UTF-8 without a BOM (what every editor expects).
public class MarkdownExportServiceTests
{
    private static string Export(string markdown, AppSettings? settings = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "marksmith_md_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var md = Path.Combine(dir, "out.md");
        new MarkdownExportService().ExportAsync(markdown, md, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return md;
    }

    [Fact]
    public void Markdown_Is_Written_To_Output_File()
    {
        var path = Export("# Title\n\nSome **body** text.\n");

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("# Title", content);
        Assert.Contains("Some **body** text.", content);
    }

    [Fact]
    public void Emoji_Are_Stripped_When_NoEmoji_Is_Set()
    {
        var settings = new AppSettings { NoEmoji = true };
        var path = Export("# Hi 🎉\n\nParty 🥳 time.\n", settings);

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("🎉", content);
        Assert.DoesNotContain("🥳", content);
        Assert.Contains("# Hi", content);
        Assert.Contains("Party", content);
    }

    [Fact]
    public void Emoji_Are_Kept_By_Default()
    {
        var path = Export("# Hi 🎉\n");

        Assert.Contains("🎉", File.ReadAllText(path));
    }

    [Fact]
    public void Output_Is_Utf8_Without_Bom()
    {
        var path = Export("# Café — résumé\n");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Markdown export must not carry a UTF-8 BOM");
        // Non-ASCII survives intact as valid UTF-8.
        Assert.Contains("Café", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Windows_Line_Endings_Are_Normalized()
    {
        var path = Export("# Title\r\n\r\nBody line.\r\n");

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("\r\n", content);
        Assert.Contains("# Title\n", content);
    }
}
