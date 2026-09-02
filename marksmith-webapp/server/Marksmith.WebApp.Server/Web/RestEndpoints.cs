using System.Reflection;
using System.Security.Claims;
using MarkSmith.WebApp.Server.Auth;
using MarkSmith.WebApp.Server.Ot;
using MarkSmith.WebApp.Server.Sessions;

namespace MarkSmith.WebApp.Server.Web;

/// <summary>
/// REST surface (OpenAPI: docs/04-openapi.yaml). Complements the WebSocket channel:
///  * session lifecycle (create / resume / close),
///  * DOCX upload / download,
///  * a REST batch fallback for non-real-time callers,
///  * dev token issuance for the sample UI.
/// The real-time path (batching, transforms, broadcast) lives in the WebSocket hub.
/// </summary>
public static class RestEndpoints
{
    public static void Map(WebApplication app, JwtTokenService jwt, SessionManager sessions)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            service = "marksmith-webapp",
            version = AppVersion(),
            sessions = sessions.ActiveCount,
            sessionCapacity = SessionManager.MaxConcurrentSessions,
        }));

        // Dev token endpoint (sample UI + integration tests). Production hosts issue tokens via
        // their own identity layer and never expose this route.
        app.MapPost("/api/auth/token", (TokenRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.DocumentId))
                return Results.BadRequest(new { error = "userId and documentId are required" });
            var token = jwt.Issue(req.UserId, req.DocumentId);
            return Results.Ok(new { token, expiresInSeconds = (int)TimeSpan.FromHours(8).TotalSeconds });
        });

        // Create or resume a session. Body optional: when a docxBase64 payload is present the
        // session starts from it; otherwise it resumes from the persisted snapshot or a blank doc.
        app.MapPost("/api/sessions", async (HttpRequest http, CreateSessionRequest req) =>
        {
            var userId = http.HttpContext.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (userId is null) return Results.Unauthorized();

            var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
                ? $"doc-{Guid.NewGuid():N}"
                : req.SessionId;

            byte[]? uploaded = null;
            if (!string.IsNullOrWhiteSpace(req.DocxBase64))
            {
                try { uploaded = Convert.FromBase64String(req.DocxBase64); }
                catch { return Results.BadRequest(new { error = "docxBase64 is not valid base64" }); }
            }

            var session = await sessions.StartAsync(sessionId, userId, uploaded);
            var html = await sessions.WithSessionAsync(sessionId, s => s.RenderedHtml());
            var seq = await sessions.WithSessionAsync(sessionId, s => s.RenderedHtmlAtSeq());

            return Results.Ok(new SessionView(sessionId, session.OwnerId, seq, html));
        }).RequireAuthorization();

        app.MapGet("/api/sessions/{sessionId}", async (string sessionId) =>
        {
            if (!sessions.IsLoaded(sessionId))
            {
                var resumed = await sessions.StartAsync(sessionId, "system", null);
                _ = resumed;
            }
            var html = await sessions.WithSessionAsync(sessionId, s => s.RenderedHtml());
            var seq = await sessions.WithSessionAsync(sessionId, s => s.RenderedHtmlAtSeq());
            return Results.Ok(new SessionView(sessionId, "", seq, html));
        }).RequireAuthorization();

        // REST batch fallback: same semantics as the WebSocket batch message.
        app.MapPost("/api/sessions/{sessionId}/batch", async (string sessionId, HttpRequest http, BatchMessageRequest req) =>
        {
            var userId = http.HttpContext.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (userId is null) return Results.Unauthorized();
            var result = await sessions.WithSessionAsync(sessionId, s => s.ApplyBatch(userId, req.BaseSeq, req.Ops));
            if (!result.Ok) return Results.Conflict(new { error = result.Error });
            return Results.Ok(new
            {
                entries = result.Entries.Select(e => new { e.Seq, e.Op, e.WasNoOp }),
            });
        }).RequireAuthorization();

        // Explicit save: returns the DOCX as a download.
        app.MapGet("/api/sessions/{sessionId}/docx", async (string sessionId) =>
        {
            var bytes = await sessions.WithSessionAsync(sessionId, s => s.SaveToBytes());
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"{sessionId}.docx");
        }).RequireAuthorization();

        app.MapDelete("/api/sessions/{sessionId}", async (string sessionId) =>
        {
            await sessions.CloseAsync(sessionId);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string AppVersion()
    {
        var asm = typeof(RestEndpoints).Assembly;
        var iv = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(iv))
        {
            var plus = iv.IndexOf('+');
            return plus > 0 ? iv[..plus] : iv;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public sealed record TokenRequest(string UserId, string DocumentId);
    public sealed record CreateSessionRequest(string? SessionId, string? DocxBase64);
    public sealed record SessionView(string SessionId, string OwnerId, long Seq, string Html);
    public sealed record BatchMessageRequest(long BaseSeq, IReadOnlyList<Operation> Ops);
}
