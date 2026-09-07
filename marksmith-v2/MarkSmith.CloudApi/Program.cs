using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using MarkSmith.Models;
using MarkSmith.Services;

var builder = WebApplication.CreateBuilder(args);

// Port binding: Honor PORT environment variable from Render / Docker, default to 5000
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// CORS: allow webfront (onyachamp.com) and local dev to call the API
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("Content-Disposition", "X-MarkSmith-Compiler");
    });
});

var app = builder.Build();

app.UseCors();

// Rate limiting: simple in-memory tracker per client IP (30 requests per minute)
var rateLimiter = new ConcurrentDictionary<string, (int count, long windowStartMs)>();

bool IsRateLimited(HttpContext context)
{
    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var window = rateLimiter.AddOrUpdate(ip,
        _ => (1, now),
        (_, current) => (now - current.windowStartMs > 60_000) ? (1, now) : (current.count + 1, current.windowStartMs));

    return window.count > 30;
}

// ---- Health & Metadata ----
app.MapGet("/health", () => Results.Ok(new
{
    status = "online",
    service = "MarkSmith Cloud Compiler API",
    engine = "Pure ECMA-376 OpenXML (.NET 8)",
    version = "3.1.0",
    wordInterop = false,
    zeroRetention = true
}));

app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html>
<head><title>MarkSmith Cloud Compiler API</title></head>
<body style="font-family: sans-serif; max-width: 600px; margin: 40px auto; line-height: 1.6; color: #222;">
    <h2>MarkSmith Cloud Compiler API (v3.1.0)</h2>
    <p>Stateless in-memory ECMA-376 OpenXML compiler backend for <a href="https://onyachamp.com/marksmith.html">onyachamp.com/marksmith.html</a>.</p>
    <ul>
        <li><code>GET /health</code> — Health & engine metadata</li>
        <li><code>POST /api/demo/compile</code> — In-memory Markdown to DOCX compiler</li>
        <li><code>POST /api/convert</code> — Compatible conversion endpoint</li>
    </ul>
    <p style="color: #666; font-size: 0.9em;">Zero storage & zero tracking. All input is compiled in RAM and discarded immediately.</p>
</body>
</html>
""", "text/html"));

// ---- Compilation Endpoints ----
app.MapPost("/api/demo/compile", async (HttpContext ctx, CompileRequest req) =>
{
    return await HandleCompileAsync(ctx, req);
});

app.MapPost("/api/convert", async (HttpContext ctx, CompileRequest req) =>
{
    return await HandleCompileAsync(ctx, req);
});

async Task<IResult> HandleCompileAsync(HttpContext ctx, CompileRequest req)
{
    if (IsRateLimited(ctx))
    {
        return Results.StatusCode(429); // Too Many Requests
    }

    if (string.IsNullOrWhiteSpace(req.Markdown))
    {
        return Results.BadRequest(new { error = "Markdown content cannot be empty." });
    }

    // Safety guard against memory exhaustion (max 50,000 characters for demo)
    if (req.Markdown.Length > 50_000)
    {
        return Results.StatusCode(413); // Payload Too Large
    }

    try
    {
        var settings = new AppSettings();
        if (!string.IsNullOrWhiteSpace(req.Theme))
        {
            settings.Theme = req.Theme;
        }

        var exporter = new DocxExportService();
        var ms = new MemoryStream();

        // Compile in memory — zero disk I/O, zero cloud retention
        await exporter.ExportAsync(req.Markdown, ms, settings);
        ms.Position = 0;

        ctx.Response.Headers["X-MarkSmith-Compiler"] = "ECMA-376 Native (ShapeForge + OMML)";
        return Results.File(
            ms,
            contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            fileDownloadName: req.Filename ?? "MarkSmith-Live-Demo.docx"
        );
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 500,
            title: "Compilation Error"
        );
    }
}

app.Run();

public record CompileRequest(
    [property: JsonPropertyName("markdown")] string? Markdown,
    [property: JsonPropertyName("theme")] string? Theme,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("filename")] string? Filename
);
