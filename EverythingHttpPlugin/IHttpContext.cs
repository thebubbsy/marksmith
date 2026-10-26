using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;

namespace EverythingHttpPlugin
{
    public interface IHttpRequest
    {
        string HttpMethod { get; }
        Uri Url { get; }
        string Path { get; }
        NameValueCollection Headers { get; }
        NameValueCollection QueryString { get; }
        IDictionary<string, string> Cookies { get; }
        Stream InputStream { get; }
    }

    public interface IHttpResponse
    {
        int StatusCode { get; set; }
        string? ContentType { get; set; }
        long ContentLength64 { get; set; }
        NameValueCollection Headers { get; }
        Stream OutputStream { get; }
        void AppendCookie(string name, string value, TimeSpan? maxAge = null, bool httpOnly = true, string sameSite = "Lax", string path = "/");
        void Close();
    }

    public interface IHttpContext
    {
        IHttpRequest Request { get; }
        IHttpResponse Response { get; }
    }
}
