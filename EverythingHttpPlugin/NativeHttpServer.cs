using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public sealed class NativeHttpServer
    {
        private readonly PluginConfig _config;
        private HttpListener _listener;
        private readonly EverythingIpcClient _ipcClient;
        private readonly NativeThumbnailService _thumbService;
        private readonly EmbeddedAssetProvider _assetProvider;
        private readonly AuthService _authService;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;

        public NativeHttpServer(PluginConfig config, string? overrideAssetsDir = null)
        {
            _config = config;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{_config.Port}/");

            _ipcClient = new EverythingIpcClient(config);
            _thumbService = new NativeThumbnailService(config.ThumbnailCacheDir);
            _assetProvider = new EmbeddedAssetProvider(overrideAssetsDir);
            _authService = new AuthService(config);
        }

        public void Start()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://*:{_config.Port}/");
                _listener.Start();
            }
            catch (Exception)
            {
                try { _listener.Close(); } catch { }
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_config.Port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_config.Port}/");
                _listener.Start();
            }

            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(ListenLoopAsync);
            Console.WriteLine($"[OmniSight] Native HTTP Server running on http://127.0.0.1:{_config.Port}/");
        }

        public async Task StopAsync()
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }

            try
            {
                _listener.Stop();
            }
            catch { }

            if (_listenTask != null)
            {
                try { await _listenTask; } catch { }
            }
        }

        private async Task ListenLoopAsync()
        {
            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch (HttpListenerException) when (_cts?.IsCancellationRequested == true)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Server Error] {ex.Message}");
                }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            // Enable CORS for local integration
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = (int)HttpStatusCode.NoContent;
                res.Close();
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";

            try
            {
                // 1. Static Assets (css, js, assets) do not require auth
                if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    if (await _assetProvider.ServeAssetAsync(context, path))
                    {
                        return;
                    }
                }

                // 2. Auth Endpoints
                if (path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase))
                {
                    bool authed = _authService.IsAuthenticated(req);
                    await WriteJsonAsync(res, new { authenticated = authed, username = authed ? _config.Username : "" });
                    return;
                }

                if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) && req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    string u = "", p = "";

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("username", out var pu)) u = pu.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("password", out var pp)) p = pp.GetString() ?? "";
                    }
                    catch { }

                    if (_authService.Login(u, p, out var token))
                    {
                        _authService.SetSessionCookie(res, token);
                        await WriteJsonAsync(res, new { success = true, token });
                    }
                    else
                    {
                        res.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await WriteJsonAsync(res, new { success = false, error = "Invalid username or password" });
                    }
                    return;
                }

                if (path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
                {
                    _authService.Logout(req, res);
                    await WriteJsonAsync(res, new { success = true });
                    return;
                }

                // 3. Login page route
                if (path.Equals("/login", StringComparison.OrdinalIgnoreCase) || path.Equals("/login.html", StringComparison.OrdinalIgnoreCase))
                {
                    if (await _assetProvider.ServeAssetAsync(context, "/login.html"))
                    {
                        return;
                    }
                }

                // 4. Auth Gate for protected routes
                bool isAuthenticated = _authService.IsAuthenticated(req);
                if (!isAuthenticated)
                {
                    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                    {
                        res.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await WriteJsonAsync(res, new { error = "Unauthorized", requiresAuth = true });
                        return;
                    }

                    // Redirect HTML pages to /login
                    res.StatusCode = (int)HttpStatusCode.Redirect;
                    res.Headers["Location"] = "/login";
                    res.Close();
                    return;
                }

                // 5. Root & App HTML
                if (path.Equals("/") || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    if (await _assetProvider.ServeAssetAsync(context, "/index.html"))
                    {
                        return;
                    }
                }

                // 6. Search API
                if (path.Equals("/api/search", StringComparison.OrdinalIgnoreCase) ||
                    (path.Equals("/") && req.QueryString["search"] != null && req.QueryString["json"] == "1"))
                {
                    var q = req.QueryString["search"] ?? req.QueryString["q"] ?? "";
                    int offset = int.TryParse(req.QueryString["offset"] ?? req.QueryString["o"], out var o) ? o : 0;
                    int count = int.TryParse(req.QueryString["count"] ?? req.QueryString["c"] ?? req.QueryString["n"], out var c) ? c : 50;
                    string sort = req.QueryString["sort"] ?? req.QueryString["s"] ?? "name";
                    bool asc = (req.QueryString["ascending"] ?? "1") != "0";
                    bool matchCase = req.QueryString["case"] == "1";
                    bool wholeWord = req.QueryString["wholeword"] == "1" || req.QueryString["whole_word"] == "1";
                    bool matchPath = req.QueryString["path"] == "1" || req.QueryString["matchpath"] == "1";
                    bool regex = req.QueryString["regex"] == "1";
                    string category = req.QueryString["category"] ?? "all";
                    string folder = req.QueryString["folder"] ?? "";

                    var searchResult = await _ipcClient.SearchAsync(q, offset, count, sort, asc, matchCase, wholeWord, matchPath, regex, category, folder);
                    await WriteJsonAsync(res, searchResult);
                    return;
                }

                // 7. Thumbnail API
                if (path.Equals("/api/thumbnail", StringComparison.OrdinalIgnoreCase) || path.Equals("/thumbnail", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = req.QueryString["path"] ?? "";
                    int size = int.TryParse(req.QueryString["size"], out var s) ? s : 360;

                    var thumbBytes = await _thumbService.GetThumbnailBytesAsync(filePath, size);
                    if (thumbBytes != null && thumbBytes.Length > 0)
                    {
                        res.ContentType = "image/jpeg";
                        res.Headers["Cache-Control"] = "public, max-age=86400";
                        res.ContentLength64 = thumbBytes.Length;
                        await res.OutputStream.WriteAsync(thumbBytes);
                        res.Close();
                        return;
                    }

                    res.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteStringAsync(res, "Thumbnail not available");
                    return;
                }

                // 8. Stream API (Range 206)
                if (path.Equals("/api/stream", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = req.QueryString["path"] ?? "";
                    await StreamingService.StreamFileAsync(context, filePath, asAttachment: false);
                    return;
                }

                // 9. Download API
                if (path.Equals("/api/download", StringComparison.OrdinalIgnoreCase) || path.Equals("/download", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = req.QueryString["path"] ?? "";
                    await StreamingService.StreamFileAsync(context, filePath, asAttachment: true);
                    return;
                }

                // 10. Drives API
                if (path.Equals("/api/drives", StringComparison.OrdinalIgnoreCase))
                {
                    var drives = SystemExplorerService.GetDrives();
                    await WriteJsonAsync(res, new { drives });
                    return;
                }

                // 11. Google Drive API
                if (path.Equals("/api/gdrive/status", StringComparison.OrdinalIgnoreCase))
                {
                    var gdrive = SystemExplorerService.GetGoogleDriveStatus();
                    await WriteJsonAsync(res, gdrive);
                    return;
                }

                if (path.Equals("/api/gdrive/launch", StringComparison.OrdinalIgnoreCase) && req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    bool launched = SystemExplorerService.LaunchGoogleDrive();
                    await WriteJsonAsync(res, new { success = launched });
                    return;
                }

                // 12. Reveal & Open in Explorer API
                if (path.Equals("/api/open", StringComparison.OrdinalIgnoreCase) && req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    using var r = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await r.ReadToEndAsync();
                    string targetPath = "";
                    string action = "reveal";
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("path", out var pPath)) targetPath = pPath.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("action", out var pAction)) action = pAction.GetString() ?? "reveal";
                    }
                    catch { }

                    bool ok = action.Equals("open", StringComparison.OrdinalIgnoreCase)
                        ? SystemExplorerService.OpenFile(targetPath)
                        : SystemExplorerService.RevealInExplorer(targetPath);

                    await WriteJsonAsync(res, new { success = ok });
                    return;
                }

                // 13. Text content / Code preview API
                if (path.Equals("/api/text-content", StringComparison.OrdinalIgnoreCase))
                {
                    var targetPath = req.QueryString["path"] ?? "";
                    var (success, content, error) = await SystemExplorerService.GetTextPreviewAsync(targetPath);
                    if (success)
                    {
                        await WriteJsonAsync(res, new { success = true, content });
                    }
                    else
                    {
                        res.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(res, new { success = false, error });
                    }
                    return;
                }

                // 14. Stats API
                if (path.Equals("/api/stats", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
                {
                    var testSearch = await _ipcClient.SearchAsync("", 0, 1);
                    await WriteJsonAsync(res, new
                    {
                        server = "OmniSight Everything HTTP Plugin",
                        version = "2.0.0",
                        everythingPort = _config.EverythingPort,
                        everythingOnline = testSearch.TotalResults > 0,
                        totalIndexedItems = testSearch.TotalResults,
                        authEnabled = _config.AuthEnabled
                    });
                    return;
                }

                // Fallback: Try static asset provider
                if (await _assetProvider.ServeAssetAsync(context, path))
                {
                    return;
                }

                // 404
                res.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteStringAsync(res, "404 Not Found");
            }
            catch (Exception ex)
            {
                try
                {
                    res.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteJsonAsync(res, new { error = ex.Message });
                }
                catch { }
            }
        }

        private static async Task WriteJsonAsync<T>(HttpListenerResponse res, T obj)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, new JsonSerializerOptions { WriteIndented = true });
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }

        private static async Task WriteStringAsync(HttpListenerResponse res, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }
}
