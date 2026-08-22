using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DocxExportAdversarialChallenger1Tests : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    private readonly string _tempDir;

    public DocxExportAdversarialChallenger1Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DocxAdvChallenger1_" + Guid.NewGuid().ToString("N"));
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

    #region 1. Tricky IP Notations and Bypasses (IsRestrictedIp & IsSafeRemoteUrl)

    [Theory]
    // 127.0.0.1 and loopback variants
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("127.0.1.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("127.255.255.255", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("0.0.0.1", true)]
    // Cloud metadata & Link-Local IPv4
    [InlineData("169.254.169.254", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.254.254.254", true)]
    [InlineData("169.254.255.255", true)]
    // RFC 1918 Class A (10.0.0.0/8)
    [InlineData("10.0.0.0", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.254", true)]
    [InlineData("10.255.255.255", true)]
    // RFC 1918 Class B (172.16.0.0/12: 172.16.0.0 - 172.31.255.255)
    [InlineData("172.16.0.0", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.20.10.5", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.31.255.255", true)]
    // RFC 1918 Class C (192.168.0.0/16)
    [InlineData("192.168.0.0", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.255.254", true)]
    [InlineData("192.168.255.255", true)]
    // CGNAT RFC 6598 (100.64.0.0/10: 100.64.0.0 - 100.127.255.255)
    [InlineData("100.64.0.0", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.100.100.100", true)]
    [InlineData("100.127.255.255", true)]
    // IETF / Test-nets / Benchmarking
    [InlineData("192.0.0.1", true)]
    [InlineData("192.0.2.1", true)]
    [InlineData("198.18.0.1", true)]
    [InlineData("198.19.255.255", true)]
    [InlineData("198.51.100.1", true)]
    [InlineData("203.0.113.1", true)]
    // Multicast & Reserved/Broadcast
    [InlineData("224.0.0.1", true)]
    [InlineData("239.255.255.255", true)]
    [InlineData("240.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    // Valid Public IPv4 Addresses
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.167.1.1", false)]
    [InlineData("192.169.1.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("11.0.0.1", false)]
    public void IsRestrictedIp_IPv4Exhaustive_CorrectlyRestricts(string ipString, bool expectedRestricted)
    {
        var ip = IPAddress.Parse(ipString);
        var result = DocxExportService.IsRestrictedIp(ip);
        Assert.Equal(expectedRestricted, result);
    }

    [Theory]
    // IPv6 Loopback
    [InlineData("::1", true)]
    [InlineData("0:0:0:0:0:0:0:1", true)]
    // IPv6 Unspecified
    [InlineData("::", true)]
    [InlineData("0:0:0:0:0:0:0:0", true)]
    // IPv6 Link-Local (fe80::/10)
    [InlineData("fe80::1", true)]
    [InlineData("fe80::dead:beef", true)]
    [InlineData("fe80::ffff:ffff:ffff:ffff", true)]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    // IPv6 Site-Local (fec0::/10)
    [InlineData("fec0::1", true)]
    [InlineData("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    // IPv6 ULA (fc00::/7)
    [InlineData("fc00::1", true)]
    [InlineData("fc00:0000:0000:0000:0000:0000:0000:0001", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    // IPv6 Multicast (ff00::/8)
    [InlineData("ff00::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("ff05::1:3", true)]
    // IPv4-mapped IPv6
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("::ffff:10.255.255.254", true)]
    [InlineData("::ffff:172.31.255.255", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:0.0.0.0", true)]
    [InlineData("::ffff:100.64.0.1", true)]
    [InlineData("::ffff:224.0.0.1", true)]
    [InlineData("0:0:0:0:0:ffff:127.0.0.1", true)]
    // Discard and Documentation prefixes
    [InlineData("0100::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("2001:db8:85a3::8a2e:370:7334", true)]
    // Public IPv6 Addresses
    [InlineData("2606:4700:4700::1111", false)] // Cloudflare DNS
    [InlineData("2001:4860:4860::8888", false)] // Google DNS
    [InlineData("2a00:1450:4009:81f::200e", false)] // Google Public IPv6
    public void IsRestrictedIp_IPv6Exhaustive_CorrectlyRestricts(string ipString, bool expectedRestricted)
    {
        var ip = IPAddress.Parse(ipString);
        var result = DocxExportService.IsRestrictedIp(ip);
        Assert.Equal(expectedRestricted, result);
    }

    [Theory]
    // Loopback
    [InlineData("http://127.0.0.1/img.png")]
    [InlineData("http://127.0.0.2:8080/img.png")]
    [InlineData("http://127.255.255.255/avatar.jpg")]
    [InlineData("http://[::1]/img.png")]
    [InlineData("http://[::1]:8080/img.png")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:9000/img.png")]
    // Dword / Hex / Octal IPv4 representations (canonicalized by Uri)
    [InlineData("http://2130706433/img.png")]        // 127.0.0.1 as dword
    [InlineData("http://0x7f000001/img.png")]        // 127.0.0.1 as hex
    [InlineData("http://0177.0.0.1/img.png")]        // 127.0.0.1 with octal prefix
    [InlineData("http://0x7f.0.0.1/img.png")]        // 127.0.0.1 mixed
    [InlineData("http://2852039166/img.png")]        // 169.254.169.254 as dword
    // IPv4-mapped IPv6
    [InlineData("http://[::ffff:127.0.0.1]/img.png")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data")]
    [InlineData("http://[::ffff:10.255.255.254]/admin.png")]
    [InlineData("http://[::ffff:172.31.255.255]/dashboard.png")]
    [InlineData("http://[::ffff:192.168.1.1]/router.png")]
    // Hostnames
    [InlineData("http://localhost/image.png")]
    [InlineData("http://localhost:52140/api/settings")]
    [InlineData("http://LOCALHOST:8080/secret.png")]
    [InlineData("http://sub.localhost/img.png")]
    [InlineData("http://test.sub.localhost:3000/test.png")]
    [InlineData("http://internal-db.local/data.png")]
    [InlineData("http://k8s-service.internal/status.png")]
    // Cloud metadata
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://169.254.169.254/latest/dynamic/instance-identity/document")]
    [InlineData("http://169.254.0.1/metadata.json")]
    [InlineData("http://169.254.255.255/test.png")]
    // RFC 1918
    [InlineData("http://10.0.0.1/private.png")]
    [InlineData("http://10.255.255.254/admin.png")]
    [InlineData("http://172.16.0.1/intranet.png")]
    [InlineData("http://172.31.255.255/switch.png")]
    [InlineData("http://192.168.1.1/gateway.png")]
    [InlineData("http://192.168.255.255/nas.png")]
    // IPv6 link-local and ULA
    [InlineData("http://[fe80::1]/img.png")]
    [InlineData("http://[fe80::dead:beef]/img.png")]
    [InlineData("http://[fc00::1]/img.png")]
    [InlineData("http://[fd00::1]/img.png")]
    [InlineData("http://[fd12:3456:789a::1]/img.png")]
    // Non-HTTP(S) remote protocols
    [InlineData("ftp://127.0.0.1/test.png")]
    [InlineData("gopher://127.0.0.1:70/1")]
    [InlineData("ldap://127.0.0.1/o=Company")]
    [InlineData("dict://127.0.0.1:2628/")]
    public void IsSafeRemoteUrl_And_FetchImageBytes_StrictlyRejectMaliciousUrls(string attackUrl)
    {
        // 1. IsSafeRemoteUrl check
        bool isSafe = DocxExportService.IsSafeRemoteUrl(attackUrl, out var validUri);
        Assert.False(isSafe, $"URL '{attackUrl}' should be considered unsafe by IsSafeRemoteUrl.");
        Assert.Null(validUri);

        // 2. FetchImageBytes check
        byte[]? fetchedBytes = DocxExportService.FetchImageBytes(attackUrl);
        Assert.Null(fetchedBytes);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///C:/test.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,invalid_base64")]
    public void IsSafeRemoteUrl_NonHttpSchemes_RejectedAsNotRemoteUrl(string rawUrl)
    {
        bool isSafe = DocxExportService.IsSafeRemoteUrl(rawUrl, out var validUri);
        Assert.False(isSafe);
        Assert.Null(validUri);
    }

    #endregion

    #region 2. Live HTTP Listener: Empirical Zero-Network-Egress Verification

    [Fact]
    public async Task FetchImageBytes_WithLiveLocalListener_ProducesZeroNetworkEgress()
    {
        // Find an open local port
        int port = GetRandomOpenPort();
        string prefix = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        int requestCount = 0;
        using var cts = new CancellationTokenSource();

        // Background listener task counting any received HTTP requests
        var listenTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var getContextTask = listener.GetContextAsync();
                    var completedTask = await Task.WhenAny(getContextTask, Task.Delay(100, cts.Token));
                    if (completedTask == getContextTask)
                    {
                        var context = await getContextTask;
                        Interlocked.Increment(ref requestCount);
                        context.Response.StatusCode = 200;
                        var dummyBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
                        await context.Response.OutputStream.WriteAsync(dummyBytes, 0, dummyBytes.Length);
                        context.Response.Close();
                    }
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        });

        try
        {
            // Adversarial URLs targeting the live listener
            var attackUrls = new[]
            {
                $"http://127.0.0.1:{port}/exfiltrate.png",
                $"http://127.0.0.1:{port}/secret_admin",
                $"http://localhost:{port}/api/keys",
                $"http://[::ffff:127.0.0.1]:{port}/token.png",
            };

            foreach (var url in attackUrls)
            {
                var bytes = DocxExportService.FetchImageBytes(url);
                Assert.Null(bytes);
            }

            // Also test full DOCX export with markdown targeting this listener
            var exporter = new DocxExportService();
            var docxPath = Path.Combine(_tempDir, "zero_egress_test.docx");
            var markdown = $"""
            # Zero Network Egress Test
            ![Egress 1](http://127.0.0.1:{port}/exfil1.png)
            ![Egress 2](http://localhost:{port}/exfil2.png)
            ![Egress 3](http://[::ffff:127.0.0.1]:{port}/exfil3.png)
            """;

            await exporter.ExportAsync(markdown, docxPath, new AppSettings());
            Assert.True(File.Exists(docxPath));

            // Wait a brief moment to ensure no delayed network calls hit the listener
            await Task.Delay(200);

            // EMPIRICAL ASSERTION: 0 requests were received by the listener!
            Assert.Equal(0, requestCount);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await listenTask; } catch { }
        }
    }

    #endregion

    #region 3. Concurrency Hammer Stress Test

    [Fact]
    public void FetchImageBytes_ConcurrentHammer_ThreadSafeAndZeroExceptions()
    {
        var attackUrls = new[]
        {
            "http://127.0.0.1:8080/test1.png",
            "http://localhost:5000/test2.png",
            "http://169.254.169.254/latest/meta-data",
            "http://10.255.255.254/test3.png",
            "http://172.31.255.255/test4.png",
            "http://192.168.1.1/test5.png",
            "http://[::1]:8080/test6.png",
            "http://[::ffff:127.0.0.1]/test7.png",
            "http://[::ffff:169.254.169.254]/test8.png",
            "http://[fe80::1]/test9.png",
            "http://[fc00::1]/test10.png",
            "http://[fd00::1]/test11.png",
        };

        const string validBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        int iterations = 200;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            try
            {
                var url = attackUrls[i % attackUrls.Length];
                var badResult = DocxExportService.FetchImageBytes(url);
                if (badResult != null)
                {
                    exceptions.Add(new Exception($"Attack URL '{url}' unexpectedly returned non-null bytes."));
                }

                var goodResult = DocxExportService.FetchImageBytes(validBase64);
                if (goodResult == null || goodResult.Length == 0)
                {
                    exceptions.Add(new Exception("Valid base64 image failed to decode during concurrency stress."));
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    #endregion

    #region 4. End-to-End DOCX Document Package Verification

    [Fact]
    public async Task DocxExport_ComprehensiveSsrfPayload_ProducesCleanDocumentWithoutMaliciousDrawings()
    {
        const string validDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        var markdown = $"""
        # Security Adversarial Test Document

        Paragraph 1 before images.

        ![Valid Image]({validDataUri})
        ![Blocked Loopback](http://127.0.0.1/evil.png)
        ![Blocked IPv6](http://[::1]/evil.png)
        ![Blocked IPv4-Mapped](http://[::ffff:127.0.0.1]/evil.png)
        ![Blocked Metadata](http://169.254.169.254/latest/meta-data)
        ![Blocked IPv4-Mapped Metadata](http://[::ffff:169.254.169.254]/latest/meta-data)
        ![Blocked Localhost](http://localhost/evil.png)
        ![Blocked Sub Localhost](http://sub.localhost/evil.png)
        ![Blocked Class A](http://10.255.255.254/evil.png)
        ![Blocked Class B](http://172.31.255.255/evil.png)
        ![Blocked Class C](http://192.168.1.1/evil.png)
        ![Blocked Link Local](http://[fe80::1]/evil.png)
        ![Blocked ULA 1](http://[fc00::1]/evil.png)
        ![Blocked ULA 2](http://[fd00::1]/evil.png)

        Paragraph 2 after images.
        """;

        var docxOut = Path.Combine(_tempDir, "ssrf_comprehensive_result.docx");
        var exporter = new DocxExportService();
        await exporter.ExportAsync(markdown, docxOut, new AppSettings());

        Assert.True(File.Exists(docxOut));

        using var archive = ZipFile.OpenRead(docxOut);
        var docEntry = archive.GetEntry("word/document.xml");
        Assert.NotNull(docEntry);

        using var stream = docEntry.Open();
        var xDoc = XDocument.Parse(new StreamReader(stream).ReadToEnd());

        // Count pictures in the OpenXML document
        var pictures = xDoc.Descendants(Pic + "pic").ToList();
        // Exactly ONE picture must exist (the valid data URI), all 13 malicious images must be omitted!
        Assert.Single(pictures);

        // Verify document text content is intact
        var fullText = string.Join(" ", xDoc.Descendants(W + "t").Select(e => e.Value));
        Assert.Contains("Security Adversarial Test Document", fullText);
        Assert.Contains("Paragraph 1 before images.", fullText);
        Assert.Contains("Paragraph 2 after images.", fullText);
    }

    #endregion

    #region Helpers

    private static int GetRandomOpenPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    #endregion
}
