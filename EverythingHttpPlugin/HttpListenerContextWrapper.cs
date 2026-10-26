using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;

namespace EverythingHttpPlugin
{
    public sealed class HttpListenerRequestWrapper : IHttpRequest
    {
        private readonly HttpListenerRequest _request;
        private readonly Dictionary<string, string> _cookies;

        public HttpListenerRequestWrapper(HttpListenerRequest request)
        {
            _request = request;
            _cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.Cookies != null)
            {
                foreach (Cookie c in request.Cookies)
                {
                    _cookies[c.Name] = c.Value;
                }
            }
        }

        public string HttpMethod => _request.HttpMethod;
        public Uri Url => _request.Url ?? new Uri("http://localhost/");
        public string Path => _request.Url?.AbsolutePath ?? "/";
        public NameValueCollection Headers => _request.Headers;
        public NameValueCollection QueryString => _request.QueryString;
        public IDictionary<string, string> Cookies => _cookies;
        public Stream InputStream => _request.InputStream;
    }

    public sealed class HttpListenerResponseWrapper : IHttpResponse
    {
        private readonly HttpListenerResponse _response;

        public HttpListenerResponseWrapper(HttpListenerResponse response)
        {
            _response = response;
        }

        public int StatusCode
        {
            get => _response.StatusCode;
            set => _response.StatusCode = value;
        }

        public string? ContentType
        {
            get => _response.ContentType;
            set => _response.ContentType = value;
        }

        public long ContentLength64
        {
            get => _response.ContentLength64;
            set => _response.ContentLength64 = value;
        }

        public NameValueCollection Headers => _response.Headers;
        public Stream OutputStream => _response.OutputStream;

        public void AppendCookie(string name, string value, TimeSpan? maxAge = null, bool httpOnly = true, string sameSite = "Lax", string path = "/")
        {
            var parts = new List<string> { $"{name}={value}", $"Path={path}" };
            if (maxAge.HasValue)
            {
                parts.Add($"Max-Age={(long)maxAge.Value.TotalSeconds}");
                parts.Add($"Expires={DateTime.UtcNow.Add(maxAge.Value):R}");
            }
            if (httpOnly) parts.Add("HttpOnly");
            if (!string.IsNullOrEmpty(sameSite)) parts.Add($"SameSite={sameSite}");

            _response.Headers.Add("Set-Cookie", string.Join("; ", parts));
        }

        public void Close()
        {
            try { _response.Close(); } catch { }
        }
    }

    public sealed class HttpListenerContextWrapper : IHttpContext
    {
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }

        public HttpListenerContextWrapper(HttpListenerContext context)
        {
            Request = new HttpListenerRequestWrapper(context.Request);
            Response = new HttpListenerResponseWrapper(context.Response);
        }
    }
}
