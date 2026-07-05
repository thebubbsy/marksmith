using System.Net;
using System.Text;
using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Local REST API so scripts, browser userscripts, and other tools can drive the converter:
//   GET  /api/health              -> { status, version, endpoints }
//   GET  /api/themes              -> [ "GitHub Dark", ... ]
//   POST /api/classify            -> { source, confidence, signals, hasMath }   body: { markdown }
//   POST /api/ingest              -> { ok }        body: { markdown } — pushes into the app UI
//   POST /api/convert             -> PDF bytes     body: { markdown, theme?, normalize? }
// Bound to 127.0.0.1 only — this is a local automation surface, not a network service.
public sealed class ApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LlmSourceService _llm;
    private readonly Func<IReadOnlyList<string>> _themeNames;
    private readonly Action<string, string, OutputOverride?> _ingest;      // (markdown, origin, output profile)
    private readonly Func<string, OutputOverride?, Task<byte[]>> _convert; // (markdown, output profile) -> pdf bytes
    private readonly GovernanceService _governance;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }

    // `output` carries the full output profile (extension/automation). `theme`/`normalize` are kept
    // as shorthand for simple /api/convert calls and folded into the override when `output` is absent.
    private sealed record ApiRequest(string? Markdown, string? Theme, bool? Normalize, OutputOverride? Output);

    // Governance report from a managed extension. consentAcknowledged MUST be true — the collector
    // rejects reports that don't assert the user saw the monitoring notice, so the transparency
    // requirement is enforced server-side, not just in the extension UI.
    private sealed record GovReport(
        string? User, string? Device, string? Assistant, string? Url, string? Title,
        int? CharCount, int? WordCount, List<string>? DlpFlags, int? DlpHitCount, bool? ConsentAcknowledged);

    public ApiServer(
        LlmSourceService llm,
        Func<IReadOnlyList<string>> themeNames,
        Action<string, string, OutputOverride?> ingest,
        Func<string, OutputOverride?, Task<byte[]>> convert,
        GovernanceService governance)
    {
        _llm = llm;
        _themeNames = themeNames;
        _ingest = ingest;
        _convert = convert;
        _governance = governance;
    }

    public void Start(int port)
    {
        Stop();
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener stopped
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // The admin dashboard is a separate origin (served file/localhost); allow it to read the
            // 127.0.0.1-bound API. Safe: the listener only accepts loopback connections regardless.
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            var method = ctx.Request.HttpMethod;

            if (method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }

            switch (method, path)
            {
                case ("GET", "/api/health"):
                    await WriteJsonAsync(ctx, 200, new
                    {
                        status = "ok",
                        app = "Marksmith",
                        endpoints = new[] { "GET /api/health", "GET /api/themes", "POST /api/classify", "POST /api/ingest", "POST /api/convert", "POST /api/governance/report", "GET /api/governance/events", "GET /api/governance/summary" },
                    });
                    break;

                case ("GET", "/api/themes"):
                    await WriteJsonAsync(ctx, 200, _themeNames());
                    break;

                case ("POST", "/api/classify"):
                {
                    var req = await ReadBodyAsync(ctx);
                    if (req?.Markdown is not { Length: > 0 } md) { await WriteJsonAsync(ctx, 400, new { error = "markdown is required" }); break; }
                    var c = _llm.Classify(md);
                    await WriteJsonAsync(ctx, 200, new { source = c.SourceName, confidence = c.Confidence, signals = c.Signals, hasMath = c.HasMath });
                    break;
                }

                case ("POST", "/api/ingest"):
                {
                    var req = await ReadBodyAsync(ctx);
                    if (req?.Markdown is not { Length: > 0 } md) { await WriteJsonAsync(ctx, 400, new { error = "markdown is required" }); break; }
                    _ingest(md, "api", req.Output);
                    await WriteJsonAsync(ctx, 200, new { ok = true });
                    break;
                }

                case ("POST", "/api/convert"):
                {
                    var req = await ReadBodyAsync(ctx);
                    if (req?.Markdown is not { Length: > 0 } md) { await WriteJsonAsync(ctx, 400, new { error = "markdown is required" }); break; }
                    var ovr = req.Output ?? new OutputOverride { Theme = req.Theme, NormalizeLlm = req.Normalize };
                    var pdf = await _convert(md, ovr);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/pdf";
                    ctx.Response.AddHeader("Content-Disposition", "attachment; filename=export.pdf");
                    await ctx.Response.OutputStream.WriteAsync(pdf);
                    break;
                }

                case ("POST", "/api/governance/report"):
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    var r = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<GovReport>(body, JsonOpts);
                    if (r is null) { await WriteJsonAsync(ctx, 400, new { error = "empty report" }); break; }

                    // Transparency is enforced here, not just client-side: no consent flag, no record.
                    if (r.ConsentAcknowledged != true)
                    {
                        await WriteJsonAsync(ctx, 403, new { error = "consentAcknowledged must be true; monitoring requires the employee to have seen the notice" });
                        break;
                    }

                    _governance.Record(new UsageEvent
                    {
                        User = r.User ?? "unknown",
                        Device = r.Device ?? "",
                        Assistant = r.Assistant ?? "",
                        Url = r.Url ?? "",
                        Title = r.Title ?? "",
                        CharCount = r.CharCount ?? 0,
                        WordCount = r.WordCount ?? 0,
                        DlpFlags = r.DlpFlags ?? new(),
                        DlpHitCount = r.DlpHitCount ?? 0,
                        ConsentAcknowledged = true,
                    });
                    await WriteJsonAsync(ctx, 200, new { ok = true });
                    break;
                }

                case ("GET", "/api/governance/events"):
                    await WriteJsonAsync(ctx, 200, _governance.Recent());
                    break;

                case ("GET", "/api/governance/summary"):
                    await WriteJsonAsync(ctx, 200, _governance.Summary());
                    break;

                default:
                    await WriteJsonAsync(ctx, 404, new { error = "unknown endpoint", hint = "GET /api/health" });
                    break;
            }
        }
        catch (Exception ex)
        {
            try { await WriteJsonAsync(ctx, 500, new { error = ex.Message }); } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static async Task<ApiRequest?> ReadBodyAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return null;
        return JsonSerializer.Deserialize<ApiRequest>(body, JsonOpts);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose() => Stop();
}
