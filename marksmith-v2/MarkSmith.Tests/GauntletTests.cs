using System;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class GauntletTests
{
    private static string GetGauntletPath()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var path = Path.Combine(dir, "examples", "gauntlet.md");
            if (File.Exists(path)) return path;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not find gauntlet.md in parent directories.");
    }

    [Fact]
    public void Gauntlet_renders_html_successfully()
    {
        var path = GetGauntletPath();
        var md = File.ReadAllText(path);
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"), interactive: true);
        Assert.NotNull(html);
        Assert.Contains("The MarkSmith OpenXML Gauntlet", html);
        Assert.Contains("Schrödinger", html);
    }

    [Fact]
    public void Gauntlet_exports_docx_successfully()
    {
        var path = GetGauntletPath();
        var md = File.ReadAllText(path);
        var outPath = Path.Combine(Path.GetTempPath(), $"gauntlet-test-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, outPath, new AppSettings()).GetAwaiter().GetResult();
            Assert.True(File.Exists(outPath));
            using var zip = ZipFile.OpenRead(outPath);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            
            // Check that key complex elements exist in the generated OpenXML document
            Assert.Contains("Schrödinger", xml);
            Assert.Contains("<m:oMath", xml); // Math operator
            Assert.Contains("<w:tbl>", xml); // Tables
            Assert.Contains("main.rs", xml); // Code block caption
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }

    [Fact]
    public void Export_gauntlet_xml_for_inspection()
    {
        var path = GetGauntletPath();
        var md = File.ReadAllText(path);
        var outPath = Path.Combine(Path.GetTempPath(), $"gauntlet-inspect.docx");
        var xmlPath = Path.Combine(Path.GetDirectoryName(path), "gauntlet_document.xml");
        try
        {
            new DocxExportService().ExportAsync(md, outPath, new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(outPath);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            
            var doc = XDocument.Parse(xml);
            doc.Save(xmlPath);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}

