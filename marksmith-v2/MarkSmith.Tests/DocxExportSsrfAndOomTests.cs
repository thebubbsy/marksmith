using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DocxExportSsrfAndOomTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    #region 1. IP Filtering Unit Tests (IsRestrictedIp)

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("127.0.1.5", true)]
    [InlineData("127.255.255.255", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.254.255.255", true)]
    [InlineData("10.0.0.0", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.254.254.254", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.0", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.20.10.5", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.0", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("239.255.255.250", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.1.1", false)]
    [InlineData("11.0.0.1", false)]
    public void IsRestrictedIp_IPv4Addresses_EvaluatedCorrectly(string ipStr, bool expectedRestricted)
    {
        var ip = IPAddress.Parse(ipStr);
        var result = DocxExportService.IsRestrictedIp(ip);
        Assert.Equal(expectedRestricted, result);
    }

    [Theory]
    [InlineData("::1", true)]                          // IPv6 Loopback
    [InlineData("::", true)]                           // IPv6 Unspecified
    [InlineData("fe80::1", true)]                      // IPv6 Link-Local
    [InlineData("fe80::dead:beef", true)]              // IPv6 Link-Local
    [InlineData("fec0::1", true)]                      // IPv6 Site-Local
    [InlineData("ff02::1", true)]                      // IPv6 Multicast
    [InlineData("fc00::1", true)]                      // IPv6 ULA
    [InlineData("fd00::1", true)]                      // IPv6 ULA
    [InlineData("fd12:3456:789a::1", true)]            // IPv6 ULA
    [InlineData("::ffff:127.0.0.1", true)]             // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254", true)]       // IPv4-mapped cloud metadata
    [InlineData("::ffff:10.0.0.1", true)]              // IPv4-mapped RFC 1918 Class A
    [InlineData("::ffff:172.16.0.1", true)]            // IPv4-mapped RFC 1918 Class B
    [InlineData("::ffff:192.168.1.1", true)]           // IPv4-mapped RFC 1918 Class C
    [InlineData("2606:4700:4700::1111", false)]        // Cloudflare DNS IPv6 (Public)
    [InlineData("2001:4860:4860::8888", false)]        // Google DNS IPv6 (Public)
    public void IsRestrictedIp_IPv6Addresses_EvaluatedCorrectly(string ipStr, bool expectedRestricted)
    {
        var ip = IPAddress.Parse(ipStr);
        var result = DocxExportService.IsRestrictedIp(ip);
        Assert.Equal(expectedRestricted, result);
    }

    #endregion

    #region 2. Safe Remote URL Unit Tests (IsSafeRemoteUrl)

    [Theory]
    [InlineData("http://127.0.0.1/img.png", false)]
    [InlineData("http://127.0.1.5:8080/img.png", false)]
    [InlineData("http://user:pass@127.0.0.1:8080/secret.png?foo=bar#top", false)]
    [InlineData("http://localhost/img.png", false)]
    [InlineData("http://localhost:5000/api/img", false)]
    [InlineData("http://sub.localhost/img.png", false)]
    [InlineData("http://test.local/img.png", false)]
    [InlineData("http://service.internal/img.png", false)]
    [InlineData("http://[::1]/img.png", false)]
    [InlineData("http://[::1]:8080/img.png", false)]
    [InlineData("http://169.254.169.254/latest/meta-data/image.png", false)]
    [InlineData("http://169.254.1.1/img.png", false)]
    [InlineData("http://10.0.0.1/img.png", false)]
    [InlineData("http://172.16.0.1/img.png", false)]
    [InlineData("http://192.168.1.1/img.png", false)]
    [InlineData("http://[fe80::1]/img.png", false)]
    [InlineData("http://[fc00::1]/img.png", false)]
    [InlineData("http://[fd12:3456:789a::1]/img.png", false)]
    [InlineData("http://[::ffff:127.0.0.1]/img.png", false)]
    [InlineData("http://[::ffff:169.254.169.254]/img.png", false)]
    [InlineData("http://[::ffff:10.0.0.1]/img.png", false)]
    [InlineData("ftp://example.com/img.png", false)]
    [InlineData("file:///C:/test.png", false)]
    [InlineData("gopher://127.0.0.1/test", false)]
    [InlineData("http://non-existent-domain-for-marksmith-test-xyz987654321.com/img.png", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-valid-url", false)]
    public void IsSafeRemoteUrl_RestrictedAndInvalidUrls_Rejected(string rawUrl, bool expectedSafe)
    {
        var result = DocxExportService.IsSafeRemoteUrl(rawUrl, out var validUri);
        Assert.Equal(expectedSafe, result);
        if (!expectedSafe)
        {
            Assert.Null(validUri);
        }
    }

    [Fact]
    public void MaxImageSizeBytes_ConfiguredTo20MB()
    {
        Assert.Equal(20 * 1024 * 1024, DocxExportService.MaxImageSizeBytes);
    }

    #endregion

    #region 3. FetchImageBytes SSRF & Data URI & Local File Tests

    [Theory]
    [InlineData("http://127.0.0.1:8080/secret.png")]
    [InlineData("http://localhost:8080/secret.png")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.1/intranet.png")]
    [InlineData("http://172.16.0.1/intranet.png")]
    [InlineData("http://192.168.1.1/router.png")]
    [InlineData("http://[::1]/secret.png")]
    [InlineData("http://[::ffff:127.0.0.1]/secret.png")]
    [InlineData("http://[::ffff:169.254.169.254]/secret.png")]
    public void FetchImageBytes_RestrictedRemoteUrls_ReturnsNullImmediately(string rawUrl)
    {
        var bytes = DocxExportService.FetchImageBytes(rawUrl);
        Assert.Null(bytes);
    }

    [Fact]
    public void FetchImageBytes_ValidBase64DataUri_ReturnsDecodedBytes()
    {
        // 1x1 transparent PNG
        var base64Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var bytes = DocxExportService.FetchImageBytes(base64Png);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        // Verify PNG magic header: 0x89, 0x50, 0x4E, 0x47
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    [Fact]
    public void FetchImageBytes_ValidSvgDataUri_ReturnsUtf8Bytes()
    {
        var svgDataUri = "data:image/svg+xml;utf8,<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"red\"/></svg>";
        var bytes = DocxExportService.FetchImageBytes(svgDataUri);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        var content = Encoding.UTF8.GetString(bytes);
        Assert.Contains("<circle", content);
    }

    [Fact]
    public void FetchImageBytes_InvalidDataUri_ReturnsNull()
    {
        var invalidDataUri = "data:image/png;no_comma_here";
        var bytes = DocxExportService.FetchImageBytes(invalidDataUri);
        Assert.Null(bytes);
    }

    [Fact]
    public void FetchImageBytes_LocalFile_ReturnsBytes()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_img_" + Guid.NewGuid().ToString("N") + ".png");
        var expectedBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        File.WriteAllBytes(tempFile, expectedBytes);
        try
        {
            var bytes = DocxExportService.FetchImageBytes("file:///" + tempFile.Replace('\\', '/'));
            Assert.NotNull(bytes);
            Assert.Equal(expectedBytes, bytes);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FetchImageBytes_NonExistentLocalFile_ReturnsNull()
    {
        var bytes = DocxExportService.FetchImageBytes("file:///C:/non_existent_folder_xyz/no_image.png");
        Assert.Null(bytes);
    }

    [Fact]
    public void FetchImageBytes_LocalFileExceedingMaxSizeBytes_ReturnsNull()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "oversize_img_" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            // Create a sparse/zeroed file slightly exceeding 20 MB (20 MB + 1024 bytes)
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(DocxExportService.MaxImageSizeBytes + 1024);
            }

            var bytes = DocxExportService.FetchImageBytes("file:///" + tempFile.Replace('\\', '/'));
            Assert.Null(bytes);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    #endregion

    #region 4. OOM Protection & Stream Capping Tests

    [Fact]
    public void ReadImageResponseWithLimit_ContentLengthHeaderExceeding20MB_ReturnsNullImmediately()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        // Simulate a response with Content-Length = 25 MB
        response.Content = new ByteArrayContent(new byte[100]);
        response.Content.Headers.ContentLength = 25 * 1024 * 1024; // 25 MB

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);
        Assert.Null(result);
    }

    [Fact]
    public void ReadImageResponseWithLimit_NegativeContentLengthHeader_ReturnsNull()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(new byte[100]);
        response.Content.Headers.ContentLength = -1;

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);
        Assert.Null(result);
    }

    [Fact]
    public void ReadImageResponseWithLimit_ChunkedStreamExceeding20MB_AbortsAndReturnsNull()
    {
        // Simulate an endless or oversized chunked stream that exceeds 20 MB
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var infiniteStream = new RepeatingByteStream(totalBytesToProduce: 25 * 1024 * 1024); // 25 MB
        response.Content = new StreamContent(infiniteStream);
        // Explicitly clear ContentLength so it simulates chunked transfer
        response.Content.Headers.ContentLength = null;

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);
        Assert.Null(result);
    }

    [Fact]
    public void ReadImageResponseWithLimit_PayloadUnder20MB_Succeeds()
    {
        var payloadSize = 500 * 1024; // 500 KB
        var dummyData = new byte[payloadSize];
        new Random(42).NextBytes(dummyData);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(dummyData);

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);
        Assert.NotNull(result);
        Assert.Equal(payloadSize, result.Length);
        Assert.Equal(dummyData, result);
    }

    [Fact]
    public void ReadImageResponseWithLimit_PayloadExactly20MB_Succeeds()
    {
        var payloadSize = DocxExportService.MaxImageSizeBytes; // Exactly 20 MB
        var stream = new RepeatingByteStream(totalBytesToProduce: payloadSize);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentLength = payloadSize;

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);
        Assert.NotNull(result);
        Assert.Equal(payloadSize, result.Length);
    }

    [Fact]
    public void ReadImageResponseWithLimit_Payload20MBPlusOneByte_ReturnsNull()
    {
        var payloadSize = DocxExportService.MaxImageSizeBytes + 1;
        var stream = new RepeatingByteStream(totalBytesToProduce: payloadSize);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentLength = null; // simulate streaming

        var result = DocxExportService.ReadImageResponseWithLimit(response, DocxExportService.MaxImageSizeBytes);
        Assert.Null(result);
    }

    #endregion

    #region 5. End-to-End Docx Export Integration Test with SSRF Mitigation

    [Fact]
    public async Task DocxExport_MarkdownWithSsrfUrls_SafelyExportsWithoutExceptions()
    {
        var base64Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        var markdown = $"""
        # SSRF Security & OOM Test Document

        This markdown contains legitimate images and SSRF attack vectors:

        1. Valid embedded data URI:
        ![Safe Pixel]({base64Png})

        2. Malicious Loopback SSRF:
        ![Evil Localhost](http://localhost:52140/api/settings)
        ![Evil Loopback IP](http://127.0.0.1:8080/admin)
        ![Evil IPv6 Loopback](http://[::1]:8080/secret)

        3. Malicious Cloud Metadata SSRF:
        ![Evil AWS Metadata](http://169.254.169.254/latest/meta-data/)

        4. Malicious Intranet RFC1918 SSRF:
        ![Evil Intranet A](http://10.0.0.1/firewall_config.png)
        ![Evil Intranet B](http://172.16.0.1/switch.png)
        ![Evil Intranet C](http://192.168.1.1/router_admin.png)
        """;

        var tempPath = Path.Combine(Path.GetTempPath(), "ssrf_test_" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var exporter = new DocxExportService();
            await exporter.ExportAsync(markdown, tempPath, new AppSettings());

            Assert.True(File.Exists(tempPath));

            using var archive = ZipFile.OpenRead(tempPath);
            var entry = archive.GetEntry("word/document.xml");
            Assert.NotNull(entry);

            using var reader = new StreamReader(entry.Open());
            var xml = await reader.ReadToEndAsync();
            var doc = XDocument.Parse(xml);

            // Verify Drawing element <w:drawing> exists for the valid Data URI image
            var drawings = doc.Descendants(W + "drawing").ToList();
            Assert.NotEmpty(drawings);

            // Verify Picture element <pic:pic> exists
            var pictures = doc.Descendants(Pic + "pic").ToList();
            Assert.NotEmpty(pictures);

            // Document should have rendered without crashing and text should be intact
            var allText = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value));
            Assert.Contains("SSRF Security & OOM Test Document", allText);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// A test stream producing a specified number of repeating bytes to test stream reading limits without allocating huge memory buffers.
    /// </summary>
    private sealed class RepeatingByteStream : Stream
    {
        private readonly long _totalBytes;
        private long _position;

        public RepeatingByteStream(long totalBytesToProduce)
        {
            _totalBytes = totalBytesToProduce;
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
            if (_position >= _totalBytes)
                return 0;

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
