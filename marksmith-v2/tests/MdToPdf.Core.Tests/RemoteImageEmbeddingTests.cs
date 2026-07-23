using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class RemoteImageEmbeddingTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    [Fact]
    public void Base64DataUriImage_EmbedsAsRealDrawingInDocx()
    {
        AppServices.License.Load();

        // 1x1 PNG transparent pixel base64
        var base64Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var markdown = $"# Data URI Image Test\n\n![Embedded Pixel]({base64Png})";

        var tempPath = Path.Combine(Path.GetTempPath(), "data_uri_test_" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var exporter = new DocxExportService();
            exporter.ExportAsync(markdown, tempPath, new AppSettings()).GetAwaiter().GetResult();

            Assert.True(File.Exists(tempPath));

            using var archive = ZipFile.OpenRead(tempPath);
            var entry = archive.GetEntry("word/document.xml");
            Assert.NotNull(entry);

            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            var doc = XDocument.Parse(xml);

            // Verify Drawing element <w:drawing>
            var drawings = doc.Descendants(W + "drawing").ToList();
            Assert.NotEmpty(drawings);

            // Verify Picture element <pic:pic>
            var pictures = doc.Descendants(Pic + "pic").ToList();
            Assert.NotEmpty(pictures);

            // Ensure NO raw hyperlinked text "[Image:" was output
            var allText = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value));
            Assert.DoesNotContain("[Image:", allText);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void RemoteHttpsImage_EmbedsAsRealDrawingInDocx()
    {
        AppServices.License.Load();

        // Public reliable test image URL
        var remoteUrl = "https://dummyimage.com/100x100/000/fff.png";
        var markdown = $"# Remote Https Image Test\n\n![Remote Store Logo]({remoteUrl})";

        var tempPath = Path.Combine(Path.GetTempPath(), "remote_https_test_" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var exporter = new DocxExportService();
            exporter.ExportAsync(markdown, tempPath, new AppSettings()).GetAwaiter().GetResult();

            Assert.True(File.Exists(tempPath));

            using var archive = ZipFile.OpenRead(tempPath);
            var entry = archive.GetEntry("word/document.xml");
            Assert.NotNull(entry);

            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            var doc = XDocument.Parse(xml);

            // Verify Drawing element <w:drawing>
            var drawings = doc.Descendants(W + "drawing").ToList();
            Assert.NotEmpty(drawings);

            // Verify Picture element <pic:pic>
            var pictures = doc.Descendants(Pic + "pic").ToList();
            Assert.NotEmpty(pictures);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
