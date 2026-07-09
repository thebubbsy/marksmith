using System.Net;

namespace MdToPdf.Avalonia.Hosting;

// Serves the bundled web assets (mermaid, KaTeX + fonts, highlight.js) to NativeWebView over a
// loopback HTTP origin. Avalonia.Controls.WebView has no WebView2-style virtual-host mapping and
// its WebResourceRequested event is read-only (no way to supply a response), so unlike the WinUI
// build (MainWindow.MapAssetHost), local files have to be served over a real (if loopback-only)
// HTTP server instead. MdToPdf.Services.WebAssets.Base is pointed here once at startup — see
// App.axaml.cs — so every render page's asset URLs (already routed through WebAssets.*) resolve
// against this origin instead of the WinUI build's virtual host.
public sealed class LocalAssetServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _rootDir;
    private readonly CancellationTokenSource _cts = new();

    public string BaseUrl { get; }

    public LocalAssetServer(string rootDir)
    {
        _rootDir = Path.GetFullPath(rootDir);
        var port = GetFreeLoopbackPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped/disposed
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var relative = ctx.Request.Url?.AbsolutePath.TrimStart('/') ?? "";
            var fullPath = Path.GetFullPath(Path.Combine(_rootDir, relative));
            // Reject any path that escapes the assets folder (defence in depth; requests are
            // loopback-only and the URLs are all ours, but cheap to check).
            if (!fullPath.StartsWith(_rootDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = ContentTypeFor(fullPath);
            ctx.Response.Headers.Add("Cache-Control", "no-cache");
            var bytes = await File.ReadAllBytesAsync(fullPath);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" => "application/javascript",
        ".css" => "text/css",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".html" => "text/html",
        _ => "application/octet-stream",
    };

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
