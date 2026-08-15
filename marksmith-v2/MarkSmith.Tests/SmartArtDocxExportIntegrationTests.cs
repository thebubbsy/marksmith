using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class SmartArtDocxExportIntegrationTests
{
    [Fact]
    public async Task Export_SmartArt_Pyramid_GeneratesValidParts()
    {
        string md = @":::smartart type=""pyramid1""
- Executive Board
  - CEO
    - Engineering Team
    - Product Team
  - New Node
  - CFO
  - New Node
  - CMO
  - New Node
:::";

        var tempPath = Path.Combine(Path.GetTempPath(), $"smartart-debug-{Guid.NewGuid():N}.docx");
        try
        {
            var exporter = new DocxExportService();
            await exporter.ExportAsync(md, tempPath, new AppSettings());

            using var zip = ZipFile.OpenRead(tempPath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            
            // Check if diagram parts were generated
            bool hasDiagramData = entries.Any(e => e.Contains("diagrams/data"));
            bool hasDiagramLayout = entries.Any(e => e.Contains("diagrams/layout"));

            var docEntry = zip.GetEntry("word/document.xml");
            Assert.NotNull(docEntry);
            using var reader = new StreamReader(docEntry.Open());
            string docXml = await reader.ReadToEndAsync();

            bool hasDrawing = docXml.Contains("<w:drawing>") || docXml.Contains("w:drawing");
            bool hasFallbackTable = docXml.Contains("<w:tbl");

            Assert.True(hasDrawing, $"Document should have native SmartArt drawing. Document XML: {docXml}");
            Assert.False(hasFallbackTable, "Document should not have fallen back to table");
            Assert.True(entries.Any(e => e.Contains("graphics/data")), "Zip should contain graphics/data part");
            Assert.True(entries.Any(e => e.Contains("graphics/layout")), "Zip should contain graphics/layout part");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
