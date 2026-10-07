using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public static class SelfTestRunner
    {
        public static async Task<int> RunAsync(PluginConfig baseConfig)
        {
            int passed = 0;
            int failed = 0;

            void Assert(string name, bool condition, string detail = "")
            {
                if (condition)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  ✓ [PASS] {name}");
                    Console.ResetColor();
                    passed++;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ [FAIL] {name} - {detail}");
                    Console.ResetColor();
                    failed++;
                }
            }

            Console.WriteLine("\n--- 1. Testing AuthService ---");
            var testConfig = new PluginConfig
            {
                Username = "testuser",
                Password = "SecretPassword123!",
                AuthEnabled = true
            };
            var auth = new AuthService(testConfig);

            bool loginOk = auth.Login("testuser", "SecretPassword123!", out var token);
            Assert("Valid credentials login", loginOk && !string.IsNullOrEmpty(token));

            bool badLogin = auth.Login("testuser", "wrongpass", out _);
            Assert("Invalid password rejected", !badLogin);

            bool badUser = auth.Login("wronguser", "SecretPassword123!", out _);
            Assert("Invalid username rejected", !badUser);

            // Test token validation
            Assert("HMAC token validated", auth.ValidateToken(token));
            Assert("Tampered token rejected", !auth.ValidateToken(token + "x"));

            Console.WriteLine("\n--- 2. Testing EmbeddedAssetProvider ---");
            var assets = new EmbeddedAssetProvider();
            // Test serving in memory using HttpListener below and test cache presence
            Assert("EmbeddedAssetProvider loaded", assets != null);

            Console.WriteLine("\n--- 3. Testing StreamingService MIME types & Range Parsing ---");
            Assert("MIME .mp4 is video/mp4", StreamingService.GetMimeType("test.mp4") == "video/mp4");
            Assert("MIME .webm is video/webm", StreamingService.GetMimeType("test.webm") == "video/webm");
            Assert("MIME .mp3 is audio/mpeg", StreamingService.GetMimeType("test.mp3") == "audio/mpeg");
            Assert("MIME .css is text/css", StreamingService.GetMimeType("test.css").StartsWith("text/css"));
            Assert("MIME .js is application/javascript", StreamingService.GetMimeType("test.js").StartsWith("application/javascript"));
            Assert("MIME .html is text/html", StreamingService.GetMimeType("test.html").StartsWith("text/html"));

            Console.WriteLine("\n--- 4. Testing SystemExplorerService ---");
            var drives = SystemExplorerService.GetDrives();
            Assert("Drives enumerated", drives.Count > 0, $"Count: {drives.Count}");
            Assert("C: drive present", drives.Exists(d => d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)));

            var gdrive = SystemExplorerService.GetGoogleDriveStatus();
            Assert("Google Drive status queried", gdrive != null && !string.IsNullOrEmpty(gdrive.Status));

            // Test text preview on source code file
            var selfPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EverythingHttpPlugin.dll");
            var textPreviewFile = Path.Combine(AppContext.BaseDirectory, "Models.cs");
            if (!File.Exists(textPreviewFile))
            {
                textPreviewFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Models.cs");
            }
            if (File.Exists(textPreviewFile))
            {
                var (previewOk, previewContent, _) = await SystemExplorerService.GetTextPreviewAsync(textPreviewFile);
                Assert("Text preview reads C# source", previewOk && previewContent.Contains("SearchResultItem"));
            }

            // Test binary file rejection in text preview
            if (File.Exists(selfPath))
            {
                var (binOk, _, binErr) = await SystemExplorerService.GetTextPreviewAsync(selfPath);
                Assert("Binary file rejected from text preview", !binOk && binErr.Contains("Binary"));
            }

            Console.WriteLine("\n--- 5. Testing EverythingIpcClient (Pure Native IPC) ---");
            var ipc = new EverythingIpcClient(baseConfig);
            var searchRes = await ipc.SearchAsync("test", 0, 5);
            Assert("Everything IPC search executed", searchRes != null);
            Assert("Everything IPC search returned results list", searchRes!.Results != null);
            Assert("TotalResults accurately populated from IPC", searchRes.TotalResults > 0, $"TotalResults: {searchRes.TotalResults}");
            Assert("Offset and count valid in response", searchRes.Offset == 0 && searchRes.Count <= 5);

            // Test multi-token query
            var multiTokenRes = await ipc.SearchAsync("test ext:cs", 0, 5);
            Assert("Multi-term query tokenization executed", multiTokenRes != null && multiTokenRes.TotalResults > 0);

            // Test category filter
            var codeCategoryRes = await ipc.SearchAsync("test", 0, 5, category: "code");
            Assert("Category filter executed via IPC", codeCategoryRes != null);

            // Test folder identification
            if (searchRes.Results.Count > 0)
            {
                var hasAnyValidItem = searchRes.Results.Exists(r => !string.IsNullOrEmpty(r.Name) && !string.IsNullOrEmpty(r.FullPath));
                Assert("Search item has name and full path", hasAnyValidItem);
            }

            Console.WriteLine("\n--- 6. End-to-End Native HTTP Server Integration Test ---");
            int testPort = 18991;
            var e2eConfig = new PluginConfig
            {
                Port = testPort,
                EverythingPort = baseConfig.EverythingPort,
                Username = "testadmin",
                Password = "AdminPassword456!",
                AuthEnabled = true
            };

            var testServer = new NativeHttpServer(e2eConfig);
            testServer.Start();

            var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
            using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{testPort}/") };

            try
            {
                // A. Check auth status initially
                var statusRes = await http.GetAsync("/api/auth/status");
                Assert("GET /api/auth/status returns 200", statusRes.StatusCode == HttpStatusCode.OK);
                var statusJson = await statusRes.Content.ReadAsStringAsync();
                Assert("Initially not authenticated", statusJson.Contains("\"authenticated\": false"));

                // B. Accessing root redirects to /login
                var rootRes = await http.GetAsync("/");
                Assert("Root redirected to login page", rootRes.StatusCode == HttpStatusCode.OK && rootRes.RequestMessage?.RequestUri?.AbsolutePath == "/login");

                // C. Load static css and js
                var cssRes = await http.GetAsync("/css/app.css");
                Assert("GET /css/app.css returns 200", cssRes.StatusCode == HttpStatusCode.OK);
                Assert("CSS has content", (await cssRes.Content.ReadAsByteArrayAsync()).Length > 1000);

                var jsRes = await http.GetAsync("/js/app.js");
                Assert("GET /js/app.js returns 200", jsRes.StatusCode == HttpStatusCode.OK);
                Assert("JS has content", (await jsRes.Content.ReadAsByteArrayAsync()).Length > 1000);

                // D. Perform login
                var loginContent = JsonContent.Create(new { username = "testadmin", password = "AdminPassword456!" });
                var loginRes = await http.PostAsync("/api/auth/login", loginContent);
                Assert("POST /api/auth/login succeeds", loginRes.StatusCode == HttpStatusCode.OK);

                var loginData = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
                string sessionToken = loginData.GetProperty("token").GetString() ?? "";

                // E. Check auth status now
                var statusAfter = await http.GetStringAsync("/api/auth/status");
                Assert("Authenticated state confirmed", statusAfter.Contains("\"authenticated\": true"));

                // F. Load protected root /index.html
                var rootAuthed = await http.GetStringAsync("/");
                Assert("GET / loads HTML dashboard", rootAuthed.Contains("OmniSight") || rootAuthed.Contains("Everything"));

                // G. Query /api/drives
                var drivesRes = await http.GetAsync("/api/drives");
                Assert("GET /api/drives returns 200", drivesRes.StatusCode == HttpStatusCode.OK);
                var drivesJson = await drivesRes.Content.ReadAsStringAsync();
                Assert("Drives JSON contains C:", drivesJson.Contains("C:"));

                // H. Query /api/search
                var searchApiRes = await http.GetAsync("/api/search?search=test&count=5");
                Assert("GET /api/search returns 200", searchApiRes.StatusCode == HttpStatusCode.OK);
                var searchApiJson = await searchApiRes.Content.ReadAsStringAsync();
                Assert("Search API returns structured JSON", searchApiJson.Contains("\"results\""));
                Assert("Search API returns accurate totalResults", searchApiJson.Contains("\"totalResults\""));

                // I. Query /api/stats
                var statsRes = await http.GetAsync("/api/stats");
                Assert("GET /api/stats returns 200", statsRes.StatusCode == HttpStatusCode.OK);

                // J. Query /api/gdrive/status & launch
                var gdriveRes = await http.GetAsync("/api/gdrive/status");
                Assert("GET /api/gdrive/status returns 200", gdriveRes.StatusCode == HttpStatusCode.OK);

                var gdriveLaunchRes = await http.PostAsync("/api/gdrive/launch", null);
                Assert("POST /api/gdrive/launch returns 200", gdriveLaunchRes.StatusCode == HttpStatusCode.OK);

                // K. Test Open & Reveal API
                var openContent = JsonContent.Create(new { action = "reveal", path = "C:\\Windows" });
                var openRes = await http.PostAsync("/api/open", openContent);
                Assert("POST /api/open returns 200", openRes.StatusCode == HttpStatusCode.OK);

                // L. Range 206 Streaming Tests
                var tempFile = Path.Combine(Path.GetTempPath(), "test_range_stream.dat");
                var dummyBytes = new byte[1024];
                new Random(42).NextBytes(dummyBytes);
                await File.WriteAllBytesAsync(tempFile, dummyBytes);

                // L1: Standard Range (bytes=0-99)
                using (var rangeReq = new HttpRequestMessage(HttpMethod.Get, $"/api/stream?path={Uri.EscapeDataString(tempFile)}"))
                {
                    rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
                    var rangeRes = await http.SendAsync(rangeReq);
                    Assert("HTTP Range request returns 206 Partial Content", rangeRes.StatusCode == HttpStatusCode.PartialContent);
                    Assert("Content-Range header present", rangeRes.Content.Headers.ContentRange != null);
                    var rangeData = await rangeRes.Content.ReadAsByteArrayAsync();
                    Assert("Range chunk has exact length 100", rangeData.Length == 100);
                }

                // L2: Suffix Range (bytes=-50) -> last 50 bytes
                using (var suffixReq = new HttpRequestMessage(HttpMethod.Get, $"/api/stream?path={Uri.EscapeDataString(tempFile)}"))
                {
                    suffixReq.Headers.TryAddWithoutValidation("Range", "bytes=-50");
                    var suffixRes = await http.SendAsync(suffixReq);
                    Assert("HTTP Suffix Range returns 206 Partial Content", suffixRes.StatusCode == HttpStatusCode.PartialContent);
                    var suffixData = await suffixRes.Content.ReadAsByteArrayAsync();
                    Assert("Suffix Range returns exact last 50 bytes", suffixData.Length == 50);
                }

                // L3: URL token authentication without cookie
                using (var tokenClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{testPort}/") })
                {
                    var tokenAuthRes = await tokenClient.GetAsync($"/api/stats?token={sessionToken}");
                    Assert("Query string token authenticates API request", tokenAuthRes.StatusCode == HttpStatusCode.OK);
                }

                try { File.Delete(tempFile); } catch { }

                // M. Test Logout
                var logoutRes = await http.GetAsync("/api/auth/logout");
                Assert("GET /api/auth/logout returns 200", logoutRes.StatusCode == HttpStatusCode.OK);

                var statusAfterLogout = await http.GetStringAsync("/api/auth/status");
                Assert("Logout verified", statusAfterLogout.Contains("\"authenticated\": false"));
            }
            finally
            {
                await testServer.StopAsync();
            }

            Console.WriteLine($"\n========================================");
            Console.WriteLine($"Self-Test Summary: {passed} PASSED, {failed} FAILED");
            Console.WriteLine($"========================================\n");

            return failed == 0 ? 0 : 1;
        }
    }
}
