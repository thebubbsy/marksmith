using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Verifies provenance stripping: Creator field and footer brand stamp behave correctly
/// for empty AuthorName, custom themes, and built-in themes.
/// </summary>
public class ProvenanceStrippingTests
{
    private static string ExportToTempPath(string md, AppSettings settings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-prov-{Guid.NewGuid():N}.docx");
        new DocxExportService().ExportAsync(md, path, settings).GetAwaiter().GetResult();
        return path;
    }

    private static string? ReadCreator(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        return doc.PackageProperties.Creator;
    }

    private static string ReadFooterXml(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        // Find any footer part.
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("word/footer") && e.FullName.EndsWith(".xml"));
        if (entry is null) return "";
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    // ---- Creator field -----------------------------------------------------------------------

    [Fact]
    public void Default_export_has_no_marksmith_creator_when_author_empty()
    {
        var settings = new AppSettings { AuthorName = "" };
        var path = ExportToTempPath("# Hello", settings);
        try
        {
            var creator = ReadCreator(path);
            Assert.True(string.IsNullOrEmpty(creator),
                $"Expected empty creator but got '{creator}'");
            Assert.DoesNotContain("Marksmith", creator ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Export_uses_configured_author_name()
    {
        var settings = new AppSettings { AuthorName = "Jane Doe" };
        var path = ExportToTempPath("# Hello", settings);
        try
        {
            var creator = ReadCreator(path);
            Assert.Equal("Jane Doe", creator);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- Footer brand stamp ------------------------------------------------------------------

    [Fact]
    public void Custom_theme_export_omits_marksmith_footer()
    {
        // A custom theme is any theme NOT in the built-in catalog.
        var settings = new AppSettings { Theme = "My Custom Corporate" };
        var path = ExportToTempPath("# Hello World", settings);
        try
        {
            var footer = ReadFooterXml(path);
            Assert.DoesNotContain("MarkSmith", footer);
            // Page numbers should still be present.
            Assert.Contains("PAGE", footer);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Builtin_theme_export_still_shows_footer()
    {
        // "GitHub Light" is a built-in theme — backward compat requires the brand stamp.
        var settings = new AppSettings { Theme = "GitHub Light" };
        var path = ExportToTempPath("# Hello World", settings);
        try
        {
            var footer = ReadFooterXml(path);
            Assert.Contains("MarkSmith", footer);
            Assert.Contains("PAGE", footer);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Builtin_dark_theme_export_still_shows_footer()
    {
        var settings = new AppSettings { Theme = "Dracula" };
        var path = ExportToTempPath("# Dark Mode", settings);
        try
        {
            var footer = ReadFooterXml(path);
            Assert.Contains("MarkSmith", footer);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
