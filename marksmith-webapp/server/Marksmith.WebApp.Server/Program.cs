using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MarkSmith.WebApp.Server.Auth;
using MarkSmith.WebApp.Server.Ot;
using MarkSmith.WebApp.Server.Sessions;
using MarkSmith.WebApp.Server.Web;

var builder = WebApplication.CreateBuilder(args);

// ---- configuration ----
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? Environment.GetEnvironmentVariable("MARKSMITH_WEBAPP_JWT_SECRET")
    ?? "dev-only-secret-change-me-0123456789abcdef"; // >= 32 chars for local dev
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "marksmith-webapp";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "marksmith-webapp";
var sessionRoot = builder.Configuration["Sessions:Root"]; // null => %TEMP%/marksmith-webapp/sessions

builder.Services.AddSingleton(new JwtTokenService(jwtSecret, jwtIssuer, jwtAudience));
builder.Services.AddSingleton<SessionStore>(sp =>
    new SessionStore(sessionRoot, sp.GetRequiredService<ILogger<SessionStore>>()));
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<WsHub>();

// REST uses the same flat discriminated-op JSON as the WebSocket protocol.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new OperationJsonConverter());
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // WebSocket handshakes carry the token in the query string, not the Authorization header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/ws") && context.Request.Query.ContainsKey("token"))
                {
                    context.Token = context.Request.Query["token"];
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// CORS for the sample UI / SDK embeddable (standalone mode). Restrict in production.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowAnyOrigin()));

builder.Services.AddLogging(l => l.AddConsole());

var app = builder.Build();

app.UseCors();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

// ---- WebSocket endpoint ----
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("websocket upgrade required");
        return;
    }

    var token = context.Request.Query["token"].ToString();
    var sessionId = context.Request.Query["session"].ToString();

    var hub = context.RequestServices.GetRequiredService<WsHub>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket, token, sessionId);
});

app.UseAuthentication();
app.UseAuthorization();

// ---- REST endpoints ----
var jwt = app.Services.GetRequiredService<JwtTokenService>();
var sessions = app.Services.GetRequiredService<SessionManager>();
RestEndpoints.Map(app, jwt, sessions);

// ---- static sample UI (client/dist when present) ----
var clientDistCandidates = new[]
{
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "client", "dist"),
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "client", "dist"),
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", "client", "dist"),
    Path.Combine(Directory.GetCurrentDirectory(), "client", "dist"),
    Path.Combine(AppContext.BaseDirectory, "client-dist"),
};

var clientDist = clientDistCandidates.FirstOrDefault(Directory.Exists);
if (clientDist is not null)
{
    var fullPath = Path.GetFullPath(clientDist);
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(fullPath);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider,
        DefaultFileNames = { "index.html" }
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider
    });
    // SPA fallback for deep links into the sample UI.
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        FileProvider = fileProvider
    });
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    // Best-effort persist on shutdown; the session store owns crash recovery.
    _ = sessions.PersistAllAsync();
});

app.Run();

public partial class Program { }
