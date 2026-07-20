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
    // Bumped (unix ms) when the app wants the extension's "Copy as Markdown" buttons to pulse —
    // e.g. after a plain-text paste. Static so UI code can set it without holding the server.
    public static long AttentionTs;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Ceiling for a request body (any real document paste fits comfortably). Enforced up front via
    // Content-Length, and again while reading in case the header lies or is absent (chunked).
    internal const long MaxBodyBytes = 25 * 1024 * 1024;

    private readonly LlmSourceService _llm;
    private readonly Func<IReadOnlyList<string>> _themeNames;
    private readonly Action<string, string, OutputOverride?> _ingest;      // (markdown, origin, output profile)
    private readonly Func<string, OutputOverride?, Task<byte[]>> _convert; // (markdown, output profile) -> pdf bytes
    private readonly GovernanceService _governance;
    private readonly Func<string> _allowedExtensionId;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }

    // `output` carries the full output profile (extension/automation). `theme`/`normalize` are kept
    // as shorthand for simple /api/convert calls and folded into the override when `output` is absent.
    private sealed record ApiRequest(string? Markdown, string? Theme, bool? Normalize, string? Format, OutputOverride? Output);

    // Governance report from a managed extension. 
    private sealed record GovDlpMatch(string? Category, string? Masked, string? Remediation);

    private sealed record GovReport(
        string? User, string? Device, string? Assistant, string? Url, string? Title,
        int? CharCount, int? WordCount, int? TimeSpentSeconds,
        List<string>? DlpFlags, int? DlpHitCount, List<GovDlpMatch>? DlpMatches,
        string? RedactedContext, double? SecretDensity, string? RawMessage);

    // Defense-in-depth: the extension is supposed to mask every DLP value before sending it, but a
    // buggy or tampered client could forward raw text. Refuse (rather than silently store) any
    // "masked" value that itself still matches a live secret pattern, so an unmasked leak can never
    // reach the collector's storage even if the client-side masking has a bug.
    private static bool LooksUnmasked(string masked)
    {
        if (masked.Contains('•') || masked.Contains('*') || masked.StartsWith('[')) return false;
        // AWS Access Key IDs are deliberately revealed in full (identifiers, not secrets) — a
        // correctly revealed key must not be flagged as a masking failure. Everything else that
        // still matches a mask-worthy pattern here means the client failed to mask it.
        return DlpScanService.ContainsUnmaskedSecret(masked);
    }

    public ApiServer(
        LlmSourceService llm,
        Func<IReadOnlyList<string>> themeNames,
        Action<string, string, OutputOverride?> ingest,
        Func<string, OutputOverride?, Task<byte[]>> convert,
        GovernanceService governance,
        Func<string> allowedExtensionId)
    {
        _llm = llm;
        _themeNames = themeNames;
        _ingest = ingest;
        _convert = convert;
        _governance = governance;
        _allowedExtensionId = allowedExtensionId;
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

    // Which cross-origin callers may use the local API. Exact-host matching via Uri parsing — a
    // naive StartsWith("http://127.0.0.1") would also accept "http://127.0.0.1.evil.com".
    // A browser-originated request (a web page OR any extension). Used to bar governance reads,
    // which must not be reachable from a page/extension even though IsAllowedOrigin permits the
    // extension for its normal ingest/report flow. Empty/"null" (non-browser or opaque) is NOT a
    // browser origin.
    private bool IsBrowserOrigin(string? origin) =>
        !string.IsNullOrEmpty(origin) && origin != "null";

    private bool IsAllowedOrigin(string? origin)
    {
        // No Origin header: a non-browser client (script, curl, the extension's direct fetch).
        // "null": file:// pages and sandboxed/opaque contexts (a locally-opened dashboard).
        if (string.IsNullOrEmpty(origin) || origin == "null") return true;

        // Browser extensions — specifically matching the configured AllowedExtensionId.
        var allowedId = _allowedExtensionId?.Invoke();
        if (!string.IsNullOrWhiteSpace(allowedId) &&
            (origin.Equals($"chrome-extension://{allowedId}", StringComparison.OrdinalIgnoreCase) ||
             origin.Equals($"moz-extension://{allowedId}", StringComparison.OrdinalIgnoreCase) ||
             origin.Equals($"safari-web-extension://{allowedId}", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Loopback only, matched on the exact host so look-alike public hosts can't slip through.
        if (Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == "file"))
        {
            return u.Scheme == "file" || u.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        }
        return false;
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // CORS/CSRF hardening: the listener is bound to 127.0.0.1, but any public website the
            // user visits could still fetch() this API. Blanket "Access-Control-Allow-Origin: *" let
            // such a page READ responses (data exfiltration) and, worse, drive state-changing
            // endpoints (/api/ingest, /api/convert). Only trusted callers are allowed: the browser
            // extension, a loopback-hosted dashboard, file:// / opaque origins, and non-browser
            // clients (no Origin header). Everything else is rejected BEFORE any handler runs, so a
            // malicious origin can neither read data nor cause a side effect.
            var origin = ctx.Request.Headers["Origin"];
            if (!IsAllowedOrigin(origin))
            {
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "null");
                await WriteJsonAsync(ctx, 403, new { error = "forbidden origin" });
                return;
            }
            // Echo the specific caller's origin (never blanket "*"), so only trusted origins can read.
            ctx.Response.AddHeader("Access-Control-Allow-Origin", string.IsNullOrEmpty(origin) ? "*" : origin);
            ctx.Response.AddHeader("Vary", "Origin");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            var method = ctx.Request.HttpMethod;

            if (method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }

            // Reject oversized bodies before reading them. HttpListener imposes no limit, so a
            // multi-GB POST to /api/convert would be read into a single string and OOM the process
            // (the outer catch can't help — the allocation already failed). 25 MB comfortably fits
            // any real document paste.
            if (method == "POST" && ctx.Request.ContentLength64 > MaxBodyBytes)
            {
                await WriteJsonAsync(ctx, 413, new { error = "request body too large" });
                return;
            }

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

                case ("GET", "/api/attention"):
                    // The browser extension polls this; a fresh timestamp makes its "Copy as
                    // Markdown" buttons pulse (set when the app detects a plain-text paste).
                    await WriteJsonAsync(ctx, 200, new { flashTs = AttentionTs });
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
                    var ovr = req.Output ?? new OutputOverride { Theme = req.Theme, NormalizeLlm = req.Normalize, Format = req.Format };
                    var bytes = await _convert(md, ovr);
                    ctx.Response.StatusCode = 200;
                    if (ovr.Format?.Equals("docx", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ctx.Response.ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                        ctx.Response.AddHeader("Content-Disposition", "attachment; filename=export.docx");
                    }
                    else
                    {
                        ctx.Response.ContentType = "application/pdf";
                        ctx.Response.AddHeader("Content-Disposition", "attachment; filename=export.pdf");
                    }
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    break;
                }

                case ("POST", "/api/governance/report"):
                {
                    var body = await ReadBoundedBodyAsync(ctx);
                    var r = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<GovReport>(body, JsonOpts);
                    if (r is null) { await WriteJsonAsync(ctx, 400, new { error = "empty report" }); break; }

                    var safeMatches = new List<DlpFinding>();
                    var droppedUnmasked = 0;
                    var safeContext = "";
                    var safeDensity = 0.0;
                    var safeRaw = "";

                    if (!string.IsNullOrEmpty(r.RawMessage))
                    {
                        // Raw Capture mode: Skip masking safety net, clamp length.
                        safeRaw = r.RawMessage.Length > 10000 ? r.RawMessage[..10000] + "…" : r.RawMessage;
                    }
                    else
                    {
                        foreach (var m in r.DlpMatches ?? new())
                        {
                            if (string.IsNullOrEmpty(m.Masked) || LooksUnmasked(m.Masked)) { droppedUnmasked++; continue; }
                            safeMatches.Add(new DlpFinding { Category = m.Category ?? "", Masked = m.Masked, Remediation = m.Remediation ?? "" });
                        }

                        // Same defense-in-depth for the redacted-context string: re-run the residual
                        // redaction pass server-side too, so a bug in the client's span math can't
                        // leave a raw secret sitting inside stored context. Also enforces the length
                        // cap and clamps density — never trust client-computed bounds.
                        safeContext = DlpScanService.RedactResidual(
                            (r.RedactedContext ?? "").Length > 500 ? r.RedactedContext![..500] + "…" : r.RedactedContext ?? "");
                        safeDensity = Math.Clamp(r.SecretDensity ?? 0, 0, 1);
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
                        TimeSpentSeconds = Math.Clamp(r.TimeSpentSeconds ?? 0, 0, 3600), // one report can't claim >1h
                        DlpFlags = r.DlpFlags ?? new(),
                        DlpHitCount = r.DlpHitCount ?? 0,
                        DlpMatches = safeMatches,
                        RedactedContext = safeContext,
                        SecretDensity = safeDensity,
                        RawMessage = safeRaw,
                    });
                    await WriteJsonAsync(ctx, 200, new { ok = true, droppedUnmasked });
                    break;
                }

                // Governance READS expose recorded DLP/usage data (redacted context, categories,
                // URLs). A blanket chrome-extension:// allow (see IsAllowedOrigin) would let ANY
                // installed browser extension read the org's captured log. These reads are for the
                // app's own UI (in-process) and local admin tooling, so restrict them to non-browser
                // callers (no/opaque Origin) — a browser or extension Origin is refused.
                case ("GET", "/api/governance/events"):
                    if (IsBrowserOrigin(origin)) { await WriteJsonAsync(ctx, 403, new { error = "governance data is not readable cross-origin" }); break; }
                    await WriteJsonAsync(ctx, 200, _governance.Recent());
                    break;

                case ("GET", "/api/governance/summary"):
                    if (IsBrowserOrigin(origin)) { await WriteJsonAsync(ctx, 403, new { error = "governance data is not readable cross-origin" }); break; }
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
        var body = await ReadBoundedBodyAsync(ctx);
        if (string.IsNullOrWhiteSpace(body)) return null;
        return JsonSerializer.Deserialize<ApiRequest>(body, JsonOpts);
    }

    // Reads the body but stops at MaxBodyBytes even if Content-Length was absent or understated
    // (chunked transfer-encoding), returning null past the cap so a lying client can't stream an
    // unbounded body into memory.
    internal static async Task<string?> ReadBoundedBodyAsync(HttpListenerContext ctx)
    {
        var buffer = new char[8192];
        var sb = new System.Text.StringBuilder();
        var enc = ctx.Request.ContentEncoding;
        if (ctx.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            enc = Encoding.UTF8;
        }
        using var reader = new StreamReader(ctx.Request.InputStream, enc ?? Encoding.UTF8);
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > MaxBodyBytes) return null;
        }
        return sb.ToString();
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
