using System.IO;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// Every exported format must carry the low-key "Marksmith by Matthew Bubb" attribution in its
// standard metadata (never in the visible document body), and the provenance Subject line must
// say "Created in Marksmith" (brand exposure in file properties) rather than a generic
// "Generated from Markdown". Creator stays the user's AuthorName.
public class ExportBrandingTests
{
    [Fact]
    public async Task Docx_Carries_LowKey_Branding_In_Company_Metadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms_brand_{Guid.NewGuid():N}.docx");
        try
        {
            await new DocxExportService().ExportAsync("# Hello\n\nWorld", path, new AppSettings { AuthorName = "Jane" });

            using var doc = WordprocessingDocument.Open(path, false);
            Assert.Equal(ExportBranding.Tag, doc.ExtendedFilePropertiesPart?.Properties?.Company?.Text);
            Assert.Equal("Jane", doc.PackageProperties.Creator); // user author preserved
            Assert.Equal(ExportBranding.CreatedIn, doc.PackageProperties.Subject);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Pptx_Carries_LowKey_Branding_In_Package_Metadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms_brand_{Guid.NewGuid():N}.pptx");
        try
        {
            await new PptxExportService().ExportAsync("# Deck Title\n\n- One\n- Two", path, new AppSettings { AuthorName = "Jane" });

            using var doc = PresentationDocument.Open(path, false);
            Assert.Equal(ExportBranding.Tag, doc.ExtendedFilePropertiesPart?.Properties?.Company?.Text);
            Assert.Equal("Jane", doc.PackageProperties.Creator);
            Assert.Equal("Deck Title", doc.PackageProperties.Title);
            Assert.Equal(ExportBranding.CreatedIn, doc.PackageProperties.Subject);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Epub_Publisher_Defaults_To_Branding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms_brand_{Guid.NewGuid():N}.epub");
        try
        {
            new EpubExportService().ExportAsync("# Plain\n\nBody.", path, new AppSettings()).GetAwaiter().GetResult();

            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("OEBPS/content.opf");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
            Assert.Contains("<dc:publisher>Marksmith by Matthew Bubb</dc:publisher>", reader.ReadToEnd());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
