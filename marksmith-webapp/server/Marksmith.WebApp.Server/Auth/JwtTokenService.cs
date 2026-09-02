using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MarkSmith.WebApp.Server.Auth;

/// <summary>
/// JWT issuance and validation for the collaboration server. v1 scope: a token binds a user id
/// to a document (tenant isolation at the session level). No enterprise SSO, no refresh tokens:
/// tokens are short-lived (default 8h) and issued by the host app after its own auth; the
/// WebApp itself only issues dev tokens for the sample UI.
///
/// The same secret must be configured in both the WebSocket handshake path and the REST path.
/// </summary>
public sealed class JwtTokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _lifetime;

    public JwtTokenService(string secret, string issuer, string audience, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new ArgumentException("JWT secret must be at least 32 characters", nameof(secret));
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _issuer = issuer;
        _audience = audience;
        _lifetime = lifetime ?? TimeSpan.FromHours(8);
    }

    /// <summary>Issues a token binding userId to documentId. Claims are the client's identity
    /// (sub + name) and the session tenant (doc).</summary>
    public string Issue(string userId, string documentId)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Name, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("doc", documentId),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(_lifetime).UtcDateTime,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Validates a token (also used for WebSocket handshake where JwtBearer middleware
    /// does not run). Returns the principal or null when invalid/expired.</summary>
    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            };
            return handler.ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }

    public static string? UserId(ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    public static string? DocumentId(ClaimsPrincipal? principal) =>
        principal?.FindFirstValue("doc");
}
