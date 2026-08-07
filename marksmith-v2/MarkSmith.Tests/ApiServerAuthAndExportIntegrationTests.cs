using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// QODER Task 4a: the AllowedExtensionId pinning check in ApiServer.IsAllowedOrigin.
// When an extension ID is pinned, ONLY that exact extension origin may call the API; when it is
// blank (the default) any installed extension is trusted, but drive-by web origins stay rejected.
[Collection("ApiServer")]
public class ApiServerExtensionAuthTests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ApiServer CreateServer(string allowedExtensionId)
    {
        return new ApiServer(
            new LlmSourceService(),
            () => new List<string> { "GitHub Dark" },
            (md, orig, ovr) => { },
            (md, ovr) => Task.FromResult(Array.Empty<byte>()),
            new GovernanceService(),
            () => allowedExtensionId,
            () => new AppSettings(),
            s => { },
            (folder, fmt, ovr) => Task.FromResult<object>(new { done = 0 })
        );
    }

    private static async Task<HttpStatusCode> GetHealthWithOriginAsync(string allowedExtensionId, string origin)
    {
        using var server = CreateServer(allowedExtensionId);
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using var client = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
            req.Headers.Add("Origin", origin);
            var resp = await client.SendAsync(req);
            return resp.StatusCode;
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Pinned_Id_Admits_Matching_Chrome_Extension()
    {
        var status = await GetHealthWithOriginAsync("abcdefghijklmnop", "chrome-extension://abcdefghijklmnop");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Pinned_Id_Rejects_Different_Extension()
    {
        var status = await GetHealthWithOriginAsync("abcdefghijklmnop", "chrome-extension://zzzzattackerzzzz");
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Blank_Pin_Trusts_Any_Extension()
    {
        var status = await GetHealthWithOriginAsync("", "chrome-extension://anyrandomextension");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Pinned_Id_Admits_Matching_Moz_Extension()
    {
        var status = await GetHealthWithOriginAsync("abcdefghijklmnop", "moz-extension://abcdefghijklmnop");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Pinned_Id_Match_Is_Case_Insensitive()
    {
        var status = await GetHealthWithOriginAsync("AbCdEfGhIjKlMnOp", "chrome-extension://abcdefghijklmnop");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Blank_Pin_Still_Rejects_Web_Origins()
    {
        // The blank default trusts extensions, never drive-by web pages.
        var status = await GetHealthWithOriginAsync("", "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Governance_Reads_Refuse_Even_The_Pinned_Extension()
    {
        // The pinned extension may ingest/convert, but governance data must never be readable
        // from any browser origin — including the trusted extension itself.
        using var server = CreateServer("abcdefghijklmnop");
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using var client = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/governance/events");
            req.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { server.Stop(); }
    }
}

// QODER Task 4b: multi-format export integration — each exporter must produce a non-zero-byte,
// structurally valid archive from a representative document without throwing. PDF rendering needs
// a live WebView2 host, so the PDF leg exercises the /api/convert plumbing end-to-end instead
// (correct content type + the converted bytes delivered intact).
public class MultiFormatExportIntegrationTests
{
    private const string SampleMarkdown = """
        # Integration Export Document

        Some **bold** prose with a [link](https://example.com) and `inline code`.

        | Format | Native |
        |--------|--------|
        | DOCX   | yes    |
        | PPTX   | yes    |

        ```mermaid
        flowchart LR
          A[Markdown] --> B{Marksmith}
          B --> C[Documents]
        ```

        ## Second Section

        - bullet one
        - bullet two
        """;

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"mk-export-int-{Guid.NewGuid():N}.{ext}");

    [Fact]
    public async Task Docx_Export_Produces_Valid_NonEmpty_Archive()
    {
        var path = TempPath("docx");
        try
        {
            await new DocxExportService().ExportAsync(SampleMarkdown, path, new AppSettings());

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0, "DOCX export produced a zero-byte file");
            using var zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("word/document.xml"));
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Pptx_Export_Produces_Valid_NonEmpty_Archive()
    {
        var path = TempPath("pptx");
        try
        {
            await new PptxExportService().ExportAsync(SampleMarkdown, path, new AppSettings());

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0, "PPTX export produced a zero-byte file");
            using var zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("ppt/presentation.xml"));
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            // Two H1/H2 headings -> at least two slides in the deck.
            Assert.True(zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide")) >= 2);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Epub_Export_Produces_Valid_NonEmpty_Container()
    {
        var path = TempPath("epub");
        try
        {
            await new EpubExportService().ExportAsync(SampleMarkdown, path, new AppSettings());

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0, "EPUB export produced a zero-byte file");
            using var zip = ZipFile.OpenRead(path);

            // EPUB OCF: the mimetype entry must exist, be stored uncompressed, and come first.
            var mimetype = zip.GetEntry("mimetype");
            Assert.NotNull(mimetype);
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            using (var reader = new StreamReader(mimetype!.Open()))
                Assert.Equal("application/epub+zip", reader.ReadToEnd());
            Assert.NotNull(zip.GetEntry("META-INF/container.xml"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Api_Convert_Docx_Roundtrips_A_Real_Archive()
    {
        // End-to-end through /api/convert: the convert delegate runs the real DOCX exporter and the
        // response bytes must still be a valid OOXML archive with the right content type.
        using var server = new ApiServer(
            new LlmSourceService(),
            () => new List<string> { "GitHub Dark" },
            (md, orig, ovr) => { },
            async (md, ovr) =>
            {
                var path = TempPath("docx");
                try
                {
                    await new DocxExportService().ExportAsync(md, path, new AppSettings());
                    return await File.ReadAllBytesAsync(path);
                }
                finally { try { File.Delete(path); } catch { } }
            },
            new GovernanceService(),
            () => "",
            () => new AppSettings(),
            s => { },
            (folder, fmt, ovr) => Task.FromResult<object>(new { done = 0 }));

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        server.Start(port);
        try
        {
            using var client = new HttpClient();
            var body = new StringContent("{\"markdown\":\"# Api Export\\n\\nBody text.\",\"format\":\"docx\"}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/convert", body);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                resp.Content.Headers.ContentType?.MediaType);

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 0, "/api/convert returned zero bytes");
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            Assert.NotNull(zip.GetEntry("word/document.xml"));
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Api_Convert_Default_Format_Delivers_Pdf_Bytes_Intact()
    {
        // The real PDF renderer needs a live WebView2 host, so this leg verifies the API contract:
        // default format is PDF, the content type says so, and the converted bytes arrive unmodified.
        var pdfStub = Encoding.ASCII.GetBytes("%PDF-1.7\n%stub-marksmith-integration\n%%EOF");
        using var server = new ApiServer(
            new LlmSourceService(),
            () => new List<string> { "GitHub Dark" },
            (md, orig, ovr) => { },
            (md, ovr) => Task.FromResult(pdfStub),
            new GovernanceService(),
            () => "",
            () => new AppSettings(),
            s => { },
            (folder, fmt, ovr) => Task.FromResult<object>(new { done = 0 }));

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        server.Start(port);
        try
        {
            using var client = new HttpClient();
            var body = new StringContent("{\"markdown\":\"# Pdf Doc\"}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/convert", body);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/pdf", resp.Content.Headers.ContentType?.MediaType);

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 0, "/api/convert returned zero bytes");
            Assert.Equal(pdfStub, bytes);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        }
        finally { server.Stop(); }
    }
}
