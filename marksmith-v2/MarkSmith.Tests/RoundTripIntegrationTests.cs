using System;
using System.IO;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class RoundTripIntegrationTests
{
    private readonly DocxExportService _exportService = new();
    private readonly ReverseImportService _importService = new();

    [Fact]
    public async Task EndToEnd_Markdown_To_Docx_And_ReverseImport_RoundTrip()
    {
        var sampleMarkdown = @"# Project Document Title

This is an introductory paragraph with **bold** text and *italic* emphasis.

## Mathematical Formulation

Here is an equation:

$$
\sum_{i=1}^{n} i = \frac{n(n+1)}{2}
$$

## System Architecture

```mermaid
graph TD
    A[Client] --> B[API Gateway]
    B --> C[Microservice]
```

## Action Items

- [x] Initial design review
- [ ] Implementation phase
- [ ] Quality assurance

> [!NOTE]
> Ensure all security guidelines are followed during deployment.

## Data Overview

| ID | Component | Status |
|----|-----------|--------|
| 1  | Frontend  | Active |
| 2  | Backend   | Active |
";

        var tempDocxPath = Path.Combine(Path.GetTempPath(), $"roundtrip_test_{Guid.NewGuid():N}.docx");
        var settings = new AppSettings
        {
            Theme = "GitHub Light",
            MermaidDocxMode = 1
        };

        try
        {
            // 1. Export Markdown to DOCX
            await _exportService.ExportAsync(sampleMarkdown, tempDocxPath, settings);

            Assert.True(File.Exists(tempDocxPath), "Exported DOCX file must exist.");
            var docxBytes = await File.ReadAllBytesAsync(tempDocxPath);
            Assert.NotEmpty(docxBytes);

            // 2. Verify OpenXML structural validity
            using (var doc = WordprocessingDocument.Open(tempDocxPath, false))
            {
                Assert.NotNull(doc.MainDocumentPart);
                Assert.NotNull(doc.MainDocumentPart.Document);
                Assert.NotNull(doc.MainDocumentPart.Document.Body);

                // Body should contain paragraphs and tables
                var body = doc.MainDocumentPart.Document.Body;
                Assert.True(body.ChildElements.Count > 0, "Document body must contain elements.");
            }

            // 3. Reverse import DOCX back into Markdown
            var importResult = await _importService.ImportFromDocxAsync(tempDocxPath);
            var reimportedMarkdown = importResult.Markdown;

            Assert.NotNull(reimportedMarkdown);
            Assert.NotEmpty(reimportedMarkdown);

            // Verify essential content sections were preserved during round-trip
            Assert.Contains("Project Document Title", reimportedMarkdown);
            Assert.Contains("Component", reimportedMarkdown);
            Assert.Contains("Frontend", reimportedMarkdown);
        }
        finally
        {
            if (File.Exists(tempDocxPath))
            {
                File.Delete(tempDocxPath);
            }
        }
    }
}
