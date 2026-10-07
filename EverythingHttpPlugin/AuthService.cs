using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace EverythingHttpPlugin
{
    public sealed class AuthService
    {
        private readonly PluginConfig _config;
        private readonly ConcurrentDictionary<string, DateTime> _activeSessions = new();
        private const string CookieName = "omnisight_session";
        private static readonly TimeSpan SessionDuration = TimeSpan.FromDays(7);

        public AuthService(PluginConfig config)
        {
            _config = config;
        }

        public bool IsAuthenticated(HttpListenerRequest request)
        {
            if (!_config.AuthEnabled) return true;

            // 1. Check Cookie
            var cookie = request.Cookies[CookieName];
            if (cookie != null && ValidateToken(cookie.Value))
            {
                return true;
            }

            // 2. Check Authorization Bearer or Basic header
            var authHeader = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader))
            {
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var token = authHeader["Bearer ".Length..].Trim();
                    if (ValidateToken(token)) return true;
                }
                else if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var b64 = authHeader["Basic ".Length..].Trim();
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        var idx = decoded.IndexOf(':');
                        if (idx >= 0)
                        {
                            var u = decoded[..idx];
                            var p = decoded[(idx + 1)..];
                            if (VerifyCredentials(u, p)) return true;
                        }
                    }
                    catch { }
                }
            }

            // 3. Check Query parameter token or auth
            var qToken = request.QueryString["token"] ?? request.QueryString["auth"];
            if (!string.IsNullOrEmpty(qToken) && ValidateToken(qToken))
            {
                return true;
            }

            return false;
        }

        public bool Login(string username, string password, out string token)
        {
            token = "";
            if (!VerifyCredentials(username, password))
            {
                return false;
            }

            token = GenerateToken(username);
            _activeSessions[token] = DateTime.UtcNow.Add(SessionDuration);
            return true;
        }

        public void Logout(HttpListenerRequest request, HttpListenerResponse response)
        {
            var cookie = request.Cookies[CookieName];
            if (cookie != null)
            {
                _activeSessions.TryRemove(cookie.Value, out _);
            }

            // Invalidate cookie
            var expiredCookie = new Cookie(CookieName, "")
            {
                Path = "/",
                Expires = DateTime.UtcNow.AddDays(-1),
                HttpOnly = true
            };
            response.AppendCookie(expiredCookie);
        }

        public void SetSessionCookie(HttpListenerResponse response, string token)
        {
            var cookie = new Cookie(CookieName, token)
            {
                Path = "/",
                Expires = DateTime.UtcNow.Add(SessionDuration),
                HttpOnly = true
            };
            response.AppendCookie(cookie);
        }

        private bool VerifyCredentials(string username, string password)
        {
            if (!_config.AuthEnabled) return true;

            bool userMatch = FixedTimeEquals(username ?? "", _config.Username ?? "");
            bool passMatch = FixedTimeEquals(password ?? "", _config.Password ?? "");
            return userMatch && passMatch;
        }

        public bool ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            if (_activeSessions.TryGetValue(token, out var exp))
            {
                if (exp > DateTime.UtcNow) return true;
                _activeSessions.TryRemove(token, out _);
                return false;
            }

            // Verify HMAC signature
            var parts = token.Split('.');
            if (parts.Length != 3) return false;

            var userB64 = parts[0];
            var expStr = parts[1];
            var sig = parts[2];

            if (!long.TryParse(expStr, out var expTicks)) return false;
            if (DateTime.UtcNow.Ticks > expTicks) return false;

            var payload = $"{userB64}.{expStr}";
            var expectedSig = ComputeHmac(payload);

            if (FixedTimeEquals(sig, expectedSig))
            {
                _activeSessions[token] = new DateTime(expTicks, DateTimeKind.Utc);
                return true;
            }

            return false;
        }

        private string GenerateToken(string username)
        {
            var userB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(username));
            var exp = DateTime.UtcNow.Add(SessionDuration).Ticks.ToString();
            var payload = $"{userB64}.{exp}";
            var sig = ComputeHmac(payload);
            return $"{payload}.{sig}";
        }

        private string ComputeHmac(string payload)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.SessionSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var aBytes = Encoding.UTF8.GetBytes(a);
            var bBytes = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
        }
    }
}
