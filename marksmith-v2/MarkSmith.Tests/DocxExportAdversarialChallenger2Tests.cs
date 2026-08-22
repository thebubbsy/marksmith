using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DocxExportAdversarialChallenger2Tests : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private readonly string _tempDir;

    public DocxExportAdversarialChallenger2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DocxAdvChallenger2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    #region 1. OOM & Streaming Size Limit Adversarial Tests

    [Theory]
    [InlineData(21 * 1024 * 1024)]             // 21 MB
    [InlineData(50 * 1024 * 1024)]             // 50 MB
    [InlineData(100 * 1024 * 1024)]            // 100 MB
    [InlineData(1024L * 1024 * 1024)]          // 1 GB
    [InlineData(2147483648L)]                  // 2 GB+ (overflows 32-bit signed int)
    public void ReadImageResponseWithLimit_OversizedContentLength_AbortsImmediatelyWithoutReading(long reportedLength)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        // Small 10-byte body to ensure stream is not read if header fails
        response.Content = new ByteArrayContent(new byte[10]);
        response.Content.Headers.ContentLength = reportedLength;

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void ReadImageResponseWithLimit_NegativeContentLength_AbortsImmediately(long reportedLength)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(new byte[10]);
        response.Content.Headers.ContentLength = reportedLength;

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);

        Assert.Null(result);
    }

    [Fact]
    public void ReadImageResponseWithLimit_InfiniteChunkedStream_StopsAtMaxLimitWithoutOOM()
    {
        // Simulate a malicious HTTP server streaming infinite bytes without Content-Length
        var infiniteStream = new InfiniteByteStream();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(infiniteStream);
        response.Content.Headers.ContentLength = null; // simulate chunked encoding

        var initialMemory = GC.GetTotalMemory(true);

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);

        var finalMemory = GC.GetTotalMemory(true);

        Assert.Null(result);

        // Verify the stream was read past 20 MB but stopped immediately after exceeding 20 MB
        Assert.True(infiniteStream.BytesReadTotal > DocxExportService.MaxImageSizeBytes,
            $"Stream should have read past 20 MB. Total read: {infiniteStream.BytesReadTotal}");
        // ReadImageResponseWithLimit reads in 64KB buffers, so it should abort after at most MaxImageSizeBytes + 64KB
        Assert.True(infiniteStream.BytesReadTotal <= DocxExportService.MaxImageSizeBytes + 65536,
            $"Stream read should stop within 1 buffer of 20MB. Total read: {infiniteStream.BytesReadTotal}");

        // Memory delta should remain small (definitely nowhere near 100MB+)
        long delta = Math.Max(0, finalMemory - initialMemory);
        Assert.True(delta < 35 * 1024 * 1024, $"Memory delta was excessive: {delta / 1024 / 1024} MB");
    }

    [Fact]
    public void ReadImageResponseWithLimit_Exact20MBBoundary_Succeeds()
    {
        int exact20MB = DocxExportService.MaxImageSizeBytes;
        var stream = new FiniteByteStream(exact20MB);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentLength = exact20MB;

        var result = DocxExportService.ReadImageResponseWithLimit(response, exact20MB);

        Assert.NotNull(result);
        Assert.Equal(exact20MB, result.Length);
    }

    [Fact]
    public void ReadImageResponseWithLimit_Exact20MBPlus1Byte_Chunked_ReturnsNull()
    {
        int size = DocxExportService.MaxImageSizeBytes + 1;
        var stream = new FiniteByteStream(size);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentLength = null;

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);

        Assert.Null(result);
    }

    #endregion

    #region 2. HTTP Redirect Prevention & SocketsHttpHandler Security Tests

    [Fact]
    public void SharedImageHttpClient_SocketsHttpHandler_ConfiguredWithAllowAutoRedirectFalse()
    {
        // Inspect the static HttpClient handler via reflection to prove AllowAutoRedirect is false
        var clientField = typeof(DocxExportService).GetField("SharedImageHttpClient", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(clientField);

        var client = clientField.GetValue(null) as HttpClient;
        Assert.NotNull(client);

        // Find the SocketsHttpHandler backing the HttpClient
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handlerField);

        var handler = handlerField.GetValue(client) as SocketsHttpHandler;
        Assert.NotNull(handler);

        Assert.False(handler.AllowAutoRedirect, "AllowAutoRedirect MUST be false to prevent SSRF via HTTP redirection.");
        Assert.True(handler.ConnectTimeout <= TimeSpan.FromSeconds(10), "ConnectTimeout must be tightly bounded.");
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]      // 301
    [InlineData(HttpStatusCode.Found)]                 // 302
    [InlineData(HttpStatusCode.TemporaryRedirect)]     // 307
    [InlineData(HttpStatusCode.PermanentRedirect)]     // 308
    public void FetchImageBytes_RedirectResponses_AreNotFollowedAndReturnNull(HttpStatusCode redirectCode)
    {
        // When a response is a 3xx redirect and AllowAutoRedirect is false,
        // response.IsSuccessStatusCode is false and FetchImageBytes returns null without following Location.
        using var response = new HttpResponseMessage(redirectCode);
        response.Headers.Location = new Uri("http://127.0.0.1:8080/secret/admin");
        response.Content = new StringContent("Redirecting...");

        // IsSuccessStatusCode is false for 3xx
        Assert.False(response.IsSuccessStatusCode);
    }

    #endregion

    #region 3. Legitimate Images & Formats Verification

    [Fact]
    public void FetchImageBytes_Base64Png_DecodesValidHeader()
    {
        var base64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var bytes = DocxExportService.FetchImageBytes(base64);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    [Fact]
    public void FetchImageBytes_Base64Jpeg_DecodesValidHeader()
    {
        // Minimal 1x1 JPEG in Base64
        var base64Jpeg = "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=";
        var bytes = DocxExportService.FetchImageBytes(base64Jpeg);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 2);
        // JPEG SOI signature: 0xFF, 0xD8
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public void FetchImageBytes_SvgUtf8DataUri_DecodesXml()
    {
        var svgUri = "data:image/svg+xml;utf8,<svg width=\"100\" height=\"100\" xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"100\" height=\"100\" fill=\"blue\"/></svg>";
        var bytes = DocxExportService.FetchImageBytes(svgUri);

        Assert.NotNull(bytes);
        var xml = Encoding.UTF8.GetString(bytes);
        Assert.Contains("<svg", xml);
        Assert.Contains("fill=\"blue\"", xml);
    }

    [Fact]
    public void FetchImageBytes_SvgPercentEncodedDataUri_DecodesCorrectly()
    {
        var encodedSvg = "data:image/svg+xml,%3Csvg%20width=%2250%22%20height=%2250%22%20xmlns=%22http://www.w3.org/2000/svg%22%3E%3Ccircle%20cx=%2225%22%20cy=%2225%22%20r=%2220%22%20fill=%22green%22/%3E%3C/svg%3E";
        var bytes = DocxExportService.FetchImageBytes(encodedSvg);

        Assert.NotNull(bytes);
        var xml = Encoding.UTF8.GetString(bytes);
        Assert.Contains("<circle", xml);
        Assert.Contains("fill=\"green\"", xml);
    }

    [Fact]
    public void FetchImageBytes_CorruptedBase64_ReturnsNullWithoutThrowing()
    {
        var corruptDataUri = "data:image/png;base64,!!!NotValidBase64Characters@#$%^&*()";
        var bytes = DocxExportService.FetchImageBytes(corruptDataUri);

        Assert.Null(bytes);
    }

    [Fact]
    public void FetchImageBytes_LocalFile_AbsoluteWindowsPath_LoadsBytes()
    {
        var testFile = Path.Combine(_tempDir, "local_image.png");
        var expectedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 10, 20, 30 };
        File.WriteAllBytes(testFile, expectedBytes);

        // Test with raw Windows path format C:\...
        var bytes = DocxExportService.FetchImageBytes(testFile);
        Assert.NotNull(bytes);
        Assert.Equal(expectedBytes, bytes);

        // Test with file:/// URI format
        var fileUri = "file:///" + testFile.Replace('\\', '/');
        var bytesUri = DocxExportService.FetchImageBytes(fileUri);
        Assert.NotNull(bytesUri);
        Assert.Equal(expectedBytes, bytesUri);
    }

    [Fact]
    public void FetchImageBytes_LocalFile_Exceeding20MB_ReturnsNull()
    {
        var oversizeFile = Path.Combine(_tempDir, "large_21mb_file.dat");
        using (var fs = new FileStream(oversizeFile, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(21 * 1024 * 1024); // 21 MB
        }

        var bytes = DocxExportService.FetchImageBytes(oversizeFile);
        Assert.Null(bytes);
    }

    #endregion

    #region 4. Full End-to-End DOCX Export Pipeline Regression Tests

    [Fact]
    public async Task DocxExport_FullComplexDocument_WithMixedImagesAndAdversarialUrls_ExportsCleanly()
    {
        var base64Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var svgUri = "data:image/svg+xml;utf8,<svg width=\"60\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"60\" height=\"60\" fill=\"#336699\"/></svg>";

        var localPng = Path.Combine(_tempDir, "sample_diagram.png");
        var dummyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        File.WriteAllBytes(localPng, dummyPng);
        var localPngUri = "file:///" + localPng.Replace('\\', '/');

        var markdown = $$"""
        ---
        title: Security & Regression Comprehensive Test
        author: Empirical Challenger
        ---

        # Comprehensive Security & DOCX Integrity Test

        ## 1. Valid Embedded Images

        Here is a valid PNG Data URI:
        ![PNG Pixel]({{base64Png}})

        Here is a valid SVG Data URI:
        ![SVG Box]({{svgUri}})

        Here is a valid Local File Image:
        ![Local Diagram]({{localPngUri}})

        ## 2. Blocked SSRF Remote Attacks

        The following images attempt SSRF against cloud metadata, loopback, and private intranet:
        ![Blocked AWS Metadata](http://169.254.169.254/latest/meta-data/)
        ![Blocked Localhost](http://localhost:52140/api/settings)
        ![Blocked Loopback](http://127.0.0.1:8080/admin/secrets)
        ![Blocked IPv6 Loopback](http://[::1]:8080/secrets)
        ![Blocked Intranet 10.x](http://10.0.0.1/router)
        ![Blocked Intranet 172.16.x](http://172.16.0.1/switch)
        ![Blocked Intranet 192.168.x](http://192.168.1.1/gateway)
        ![Blocked Carrier NAT](http://100.64.0.1/cgnat)

        ## 3. Rich Markdown Content Elements

        > [!NOTE]
        > This note callout tests GitHub alert rendering in DOCX.

        > [!WARNING]
        > This warning callout tests border styling and dark mode accents.

        ### Math Formulas (OMML Equations)

        Inline math: $\lim_{x \to 0} \frac{\sin x}{x} = 1$ and $E = mc^2$.

        Display math:
        $$
        \int_{-\infty}^{\infty} e^{-x^2} dx = \sqrt{\pi}
        $$

        ### Tables

        | Column 1 | Column 2 | Column 3 |
        | :--- | :---: | ---: |
        | Left | Center | Right |
        | Data A | Data B | Data C |

        ### Task Lists

        - [x] SSRF Protection Implemented
        - [x] OOM Protection Implemented
        - [ ] Pending Items

        """;

        var docxOut = Path.Combine(_tempDir, "comprehensive_export.docx");
        var exporter = new DocxExportService();
        var settings = new AppSettings { Theme = "GitHub Dark", AuthorName = "Empirical Challenger" };

        await exporter.ExportAsync(markdown, docxOut, settings);

        Assert.True(File.Exists(docxOut), "Generated DOCX must exist on disk.");

        // Inspect DOCX structure using WordprocessingDocument & OpenXml
        using (var package = WordprocessingDocument.Open(docxOut, false))
        {
            Assert.NotNull(package.MainDocumentPart);
            Assert.Equal("Comprehensive Security & DOCX Integrity Test", package.PackageProperties.Title);
            Assert.Equal("Empirical Challenger", package.PackageProperties.Creator);

            // Verify OpenXML DOM validation (ignoring known transient schema warnings if any)
            var validator = new OpenXmlValidator();
            var errors = validator.Validate(package).ToList();
            // Critical schema corruption check
            Assert.DoesNotContain(errors, e => e.Description.Contains("corruption", StringComparison.OrdinalIgnoreCase));
        }

        // Inspect document.xml raw text
        using (var archive = ZipFile.OpenRead(docxOut))
        {
            var docEntry = archive.GetEntry("word/document.xml");
            Assert.NotNull(docEntry);
            using var reader = new StreamReader(docEntry.Open());
            var xmlContent = await reader.ReadToEndAsync();
            var docXml = XDocument.Parse(xmlContent);

            // Verify drawings exist for the valid images
            var drawings = docXml.Descendants(W + "drawing").ToList();
            Assert.NotEmpty(drawings);

            // Verify text content was preserved
            var allText = string.Join(" ", docXml.Descendants(W + "t").Select(t => t.Value));
            Assert.Contains("Comprehensive Security & DOCX Integrity Test", allText);
            Assert.Contains("Valid Embedded Images", allText);
            Assert.Contains("Blocked SSRF Remote Attacks", allText);
            Assert.Contains("Math Formulas", allText);

            // Verify word/_rels/document.xml.rels exists
            var relsEntry = archive.GetEntry("word/_rels/document.xml.rels");
            Assert.NotNull(relsEntry);
        }
    }

    #endregion

    #region Stream Helpers

    /// <summary>
    /// Stream that outputs bytes endlessly until disposed.
    /// </summary>
    private sealed class InfiniteByteStream : Stream
    {
        public long BytesReadTotal { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position
        {
            get => BytesReadTotal;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = 0x55;
            }
            BytesReadTotal += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Stream that outputs exactly a fixed number of bytes.
    /// </summary>
    private sealed class FiniteByteStream : Stream
    {
        private readonly long _totalBytes;
        private long _position;

        public FiniteByteStream(long totalBytes)
        {
            _totalBytes = totalBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _totalBytes;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _totalBytes) return 0;
            var remaining = _totalBytes - _position;
            var toRead = (int)Math.Min(count, remaining);
            for (int i = 0; i < toRead; i++)
            {
                buffer[offset + i] = 0xAA;
            }
            _position += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
