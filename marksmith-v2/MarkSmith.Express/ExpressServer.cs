using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Express;

public sealed class ExpressServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener?.IsListening == true;

    public void Start(int preferredPort = 5000, bool openBrowser = true)
    {
        Stop();
        _cts = new CancellationTokenSource();

        int port = preferredPort;
        int attempts = 0;
        while (attempts < 20)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException)
            {
                port++;
                attempts++;
            }
        }

        if (_listener == null || !_listener.IsListening)
        {
            throw new InvalidOperationException($"Could not bind HTTP listener to port {preferredPort} (tried up to {port}).");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  __  __            _                  _ _   _       Express
 |  \/  |          | |                (_) | | |     
 | \  / | __ _ _ __| | _____ _ __ ___  _| |_| |__   
 | |\/| |/ _` | '__| |/ / __| '_ ` _ \| | __| '_ \  
 | |  | | (_| | |  |   <\__ \ | | | | | | |_| | | | 
 |_|  |_|\__,_|_|  |_|\_\___/_| |_| |_|_|\__|_| |_| 
");
        Console.ResetColor();
        Console.WriteLine($"⚡ Marksmith Express v2.18.0 is running!");
        Console.WriteLine($"🌐 Web UI:  http://localhost:{Port}/");
        Console.WriteLine($"🔌 REST API: http://localhost:{Port}/api/convert");
        Console.WriteLine($"Press Ctrl+C to stop the server.\n");

        if (openBrowser)
        {
            OpenBrowser($"http://localhost:{Port}/");
        }

        _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleRequestAsync(ctx), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            // Allow all local origins
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Format, X-Theme");

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            string method = ctx.Request.HttpMethod;

            if (method == "GET" && (path == "" || path == "/index.html"))
            {
                byte[] htmlBytes = Encoding.UTF8.GetBytes(ExpressUi.Html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.StatusCode = 200;
                await ctx.Response.OutputStream.WriteAsync(htmlBytes);
                ctx.Response.Close();
                return;
            }

            if (method == "GET" && path == "/api/health")
            {
                await WriteJsonAsync(ctx, 200, new
                {
                    status = "ok",
                    service = "Marksmith Express",
                    version = "2.18.0",
                    port = Port,
                    formats = new[] { "docx", "html", "pptx", "epub" }
                });
                return;
            }

            if (method == "GET" && path == "/api/themes")
            {
                await WriteJsonAsync(ctx, 200, AppServices.Themes.All.Select(t => new
                {
                    name = t.Name,
                    background = t.Background,
                    text = t.Text,
                    accent = t.Heading,
                    border = t.Border,
                    builtin = AppServices.Themes.IsBuiltin(t.Name),
                }));
                return;
            }

            if (method == "GET" && path == "/api/options")
            {
                // The UI builds its controls from this, so the catalogs stay a single source of
                // truth. Hard-coding them in the page is exactly how the theme list drifted into
                // six names the engine had never heard of.
                await WriteJsonAsync(ctx, 200, new
                {
                    formats = new[]
                    {
                        new { id = "docx", label = "Word", ext = ".docx" },
                        new { id = "html", label = "HTML", ext = ".html" },
                        new { id = "pptx", label = "Slides", ext = ".pptx" },
                        new { id = "epub", label = "eBook", ext = ".epub" },
                    },
                    themes = AppServices.Themes.All.Select(t => new
                    {
                        name = t.Name,
                        background = t.Background,
                        text = t.Text,
                        accent = t.Heading,
                        builtin = AppServices.Themes.IsBuiltin(t.Name),
                    }),
                    fonts = FontManagerService.Presets.Select(f => new { id = f.Id, label = f.DisplayName }),
                });
                return;
            }

            if (method == "POST" && path == "/api/convert")
            {
                await HandleConvertAsync(ctx);
                return;
            }

            ctx.Response.StatusCode = 404;
            await WriteJsonAsync(ctx, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteJsonAsync(ctx, 500, new { error = ex.Message });
            }
            catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    // `Options` is the same OutputOverride the desktop API and the browser extension post, so an
    // Express export honours the identical profile instead of a parallel, smaller contract.
    // `Theme`/`Format` stay top-level for the documented curl one-liner and older callers.
    private sealed record ConvertPayload(
        string? Markdown, string? Format, string? Theme, string? FileName, OutputOverride? Options);

    /// <summary>Strips any path and extension and drops characters a filename cannot carry.</summary>
    private static string? FileNameStem(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var stem = Path.GetFileNameWithoutExtension(candidate.Trim());
        foreach (var bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '-');
        stem = stem.Trim(' ', '.', '-');
        return string.IsNullOrEmpty(stem) ? null : stem[..Math.Min(stem.Length, 80)];
    }

    private async Task HandleConvertAsync(HttpListenerContext ctx)
    {
        string markdown = "";
        string format = "docx";
        string theme = "";
        string? requestedName = null;
        OutputOverride? options = null;

        string contentType = ctx.Request.ContentType ?? "";
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            var payload = JsonSerializer.Deserialize<ConvertPayload>(body, JsonOpts);
            markdown = payload?.Markdown ?? "";
            format = payload?.Format ?? payload?.Options?.Format ?? "docx";
            theme = payload?.Theme ?? "";
            requestedName = payload?.FileName;
            options = payload?.Options;
        }
        else
        {
            // Raw markdown body
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            markdown = await reader.ReadToEndAsync();
            if (!string.IsNullOrEmpty(ctx.Request.Headers["X-Format"]))
                format = ctx.Request.Headers["X-Format"]!;
            if (!string.IsNullOrEmpty(ctx.Request.Headers["X-Theme"]))
                theme = ctx.Request.Headers["X-Theme"]!;
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            await WriteJsonAsync(ctx, 400, new { error = "Markdown content is required." });
            return;
        }

        format = format.ToLowerInvariant().TrimStart('.');

        // Start from stock defaults, then layer the caller's profile on top. A top-level "theme"
        // still wins for the simple curl form, but only when the profile did not set one.
        var settings = new AppSettings().CloneWith(options);
        if (!string.IsNullOrWhiteSpace(theme) && options?.Theme is null) settings.Theme = theme;

        // The same preparation the desktop and batch paths run before handing markdown to an
        // exporter. Express called the exporters directly and skipped it, so identical input
        // produced different output here — chat artifacts survived, and NormalizeLlm did nothing.
        var classification = AppServices.LlmSource.Classify(markdown);
        (markdown, _) = AppServices.LlmSource.RepairArtifacts(markdown, classification);
        if (settings.NormalizeLlm)
        {
            (markdown, _) = AppServices.LlmSource.NormalizeStyle(markdown, classification);
        }

        byte[] outputBytes;
        string mimeType;
        // Name the download after the source the caller gave us (the dropped file, or a title from
        // the profile) rather than a flat "export" — otherwise every conversion lands in Downloads
        // as export.docx and silently overwrites the last one.
        string stem = FileNameStem(requestedName)
                      ?? FileNameStem(options?.SourceTitle)
                      ?? FileNameStem(options?.FileNameTemplate is { } t && !t.Contains('{') ? t : null)
                      ?? "document";
        string filename = $"{stem}.{format}";

        switch (format)
        {
            case "docx":
            case "dotx":
                string tempDocx = Path.Combine(Path.GetTempPath(), $"marksmith_express_{Guid.NewGuid():N}.docx");
                try
                {
                    var docxService = new DocxExportService();
                    await docxService.ExportAsync(markdown, tempDocx, settings);
                    outputBytes = await File.ReadAllBytesAsync(tempDocx);
                }
                finally
                {
                    if (File.Exists(tempDocx)) File.Delete(tempDocx);
                }
                mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                break;

            case "html":
            case "htm":
                var htmlService = new MarkdownHtmlService();
                var themeObj = AppServices.Themes.GetOrDefault(theme);
                string renderedHtml = htmlService.Render(markdown, settings, themeObj);
                outputBytes = Encoding.UTF8.GetBytes(renderedHtml);
                mimeType = "text/html; charset=utf-8";
                break;

            case "pptx":
                string tempPptx = Path.Combine(Path.GetTempPath(), $"marksmith_express_{Guid.NewGuid():N}.pptx");
                try
                {
                    var pptxService = new PptxExportService();
                    await pptxService.ExportAsync(markdown, tempPptx, settings);
                    outputBytes = await File.ReadAllBytesAsync(tempPptx);
                }
                finally
                {
                    if (File.Exists(tempPptx)) File.Delete(tempPptx);
                }
                mimeType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                break;

            case "epub":
                string tempEpub = Path.Combine(Path.GetTempPath(), $"marksmith_express_{Guid.NewGuid():N}.epub");
                try
                {
                    var epubService = new EpubExportService();
                    await epubService.ExportAsync(markdown, tempEpub, settings);
                    outputBytes = await File.ReadAllBytesAsync(tempEpub);
                }
                finally
                {
                    if (File.Exists(tempEpub)) File.Delete(tempEpub);
                }
                mimeType = "application/epub+zip";
                break;

            default:
                await WriteJsonAsync(ctx, 400, new { error = $"Unsupported format '{format}'. Use docx, html, pptx, or epub." });
                return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = mimeType;
        ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{filename}\"");
        ctx.Response.ContentLength64 = outputBytes.Length;
        await ctx.Response.OutputStream.WriteAsync(outputBytes);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
        catch { }
    }
}
