using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public sealed class TcpHttpContext : IHttpContext, IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly TcpHttpRequest _request;
        private readonly TcpHttpResponse _response;

        public IHttpRequest Request => _request;
        public IHttpResponse Response => _response;

        private TcpHttpContext(TcpClient client, NetworkStream stream, TcpHttpRequest request)
        {
            _client = client;
            _stream = stream;
            _request = request;
            _response = new TcpHttpResponse(stream, client);
        }

        public static async Task<TcpHttpContext?> CreateAsync(TcpClient client, CancellationToken ct = default)
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            stream.WriteTimeout = 30000;

            using var ms = new MemoryStream();
            var buffer = new byte[1024];
            int headerEndIndex = -1;

            while (headerEndIndex < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                {
                    return null;
                }

                ms.Write(buffer, 0, read);
                if (ms.Length > 65536)
                {
                    throw new InvalidOperationException("HTTP header too large");
                }

                var bytes = ms.GetBuffer();
                int len = (int)ms.Length;
                for (int i = 0; i <= len - 4; i++)
                {
                    if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    {
                        headerEndIndex = i;
                        break;
                    }
                }
            }

            var allBytes = ms.ToArray();
            var headerText = Encoding.UTF8.GetString(allBytes, 0, headerEndIndex);
            var bodyStartIndex = headerEndIndex + 4;
            var initialBodyBytesCount = allBytes.Length - bodyStartIndex;

            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            {
                return null;
            }

            var requestLineParts = lines[0].Split(' ', 3);
            if (requestLineParts.Length < 2)
            {
                return null;
            }

            var method = requestLineParts[0].ToUpperInvariant();
            var rawTarget = requestLineParts[1];

            var headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var k = line[..colon].Trim();
                    var v = line[(colon + 1)..].Trim();
                    headers.Add(k, v);
                }
            }

            var host = headers["Host"] ?? "localhost";
            var urlStr = rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rawTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? rawTarget
                : $"http://{host}{rawTarget}";

            Uri url;
            try
            {
                url = new Uri(urlStr);
            }
            catch
            {
                url = new Uri($"http://localhost{rawTarget}");
            }

            // Body handling (supports Content-Length and Transfer-Encoding: chunked)
            var inputStream = new MemoryStream();
            bool isChunked = headers["Transfer-Encoding"]?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;

            var unreadBodyStream = new MemoryStream(allBytes, bodyStartIndex, initialBodyBytesCount, writable: false);

            async Task<int> ReadByteAsync()
            {
                int b = unreadBodyStream.ReadByte();
                if (b >= 0) return b;
                var singleBuf = new byte[1];
                int r = await stream.ReadAsync(singleBuf.AsMemory(0, 1), ct);
                return r > 0 ? singleBuf[0] : -1;
            }

            async Task<int> ReadExactAsync(byte[] dest, int offset, int count)
            {
                int total = 0;
                while (total < count)
                {
                    int fromMem = unreadBodyStream.Read(dest, offset + total, count - total);
                    if (fromMem > 0)
                    {
                        total += fromMem;
                        continue;
                    }
                    int r = await stream.ReadAsync(dest.AsMemory(offset + total, count - total), ct);
                    if (r <= 0) break;
                    total += r;
                }
                return total;
            }

            async Task<string> ReadLineAsync()
            {
                var lineSb = new StringBuilder();
                int b;
                while ((b = await ReadByteAsync()) >= 0)
                {
                    if (b == '\n') break;
                    if (b != '\r') lineSb.Append((char)b);
                }
                return lineSb.ToString();
            }

            if (isChunked)
            {
                while (true)
                {
                    var chunkLine = await ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(chunkLine)) continue;
                    int semicolon = chunkLine.IndexOf(';');
                    var hex = semicolon >= 0 ? chunkLine[..semicolon].Trim() : chunkLine.Trim();
                    if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
                    {
                        break;
                    }
                    if (chunkSize <= 0)
                    {
                        while (true)
                        {
                            var tr = await ReadLineAsync();
                            if (string.IsNullOrEmpty(tr)) break;
                        }
                        break;
                    }
                    var chunkBuf = new byte[chunkSize];
                    int readChunk = await ReadExactAsync(chunkBuf, 0, chunkSize);
                    if (readChunk > 0)
                    {
                        inputStream.Write(chunkBuf, 0, readChunk);
                    }
                    int cr = await ReadByteAsync();
                    if (cr == '\r') await ReadByteAsync();
                }
            }
            else if (long.TryParse(headers["Content-Length"], out var cl) && cl > 0)
            {
                var bodyBuf = new byte[cl];
                int readLen = await ReadExactAsync(bodyBuf, 0, (int)cl);
                if (readLen > 0)
                {
                    inputStream.Write(bodyBuf, 0, readLen);
                }
            }

            inputStream.Position = 0;

            var req = new TcpHttpRequest(method, url, headers, inputStream);
            return new TcpHttpContext(client, stream, req);
        }

        public void Dispose()
        {
            try { _response.Close(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }

    public sealed class TcpHttpRequest : IHttpRequest
    {
        public string HttpMethod { get; }
        public Uri Url { get; }
        public string Path { get; }
        public NameValueCollection Headers { get; }
        public NameValueCollection QueryString { get; }
        public IDictionary<string, string> Cookies { get; }
        public Stream InputStream { get; }

        public TcpHttpRequest(string method, Uri url, NameValueCollection headers, Stream body)
        {
            HttpMethod = method;
            Url = url;
            Path = url.AbsolutePath;
            Headers = headers;
            InputStream = body;

            // Parse QueryString
            QueryString = new NameValueCollection();
            var q = url.Query;
            if (!string.IsNullOrEmpty(q) && q.StartsWith('?'))
            {
                q = q[1..];
                var pairs = q.Split('&');
                foreach (var pair in pairs)
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq >= 0)
                    {
                        var k = Uri.UnescapeDataString(pair[..eq]);
                        var v = Uri.UnescapeDataString(pair[(eq + 1)..]);
                        QueryString.Add(k, v);
                    }
                    else
                    {
                        QueryString.Add(Uri.UnescapeDataString(pair), "");
                    }
                }
            }

            // Parse Cookies
            Cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cookieHeader = headers["Cookie"];
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                var parts = cookieHeader.Split(';');
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    int eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        var cn = trimmed[..eq].Trim();
                        var cv = trimmed[(eq + 1)..].Trim();
                        Cookies[cn] = cv;
                    }
                }
            }
        }
    }

    public sealed class TcpHttpResponse : IHttpResponse
    {
        private readonly NetworkStream _networkStream;
        private readonly TcpClient _client;
        private readonly List<string> _cookieHeaders = new();
        private bool _headersSent;
        private bool _isClosed;
        private readonly HttpResponseStream _outputStream;

        public int StatusCode { get; set; } = 200;
        public string? ContentType { get; set; }
        public long ContentLength64 { get; set; } = -1;
        public NameValueCollection Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Stream OutputStream => _outputStream;

        public TcpHttpResponse(NetworkStream networkStream, TcpClient client)
        {
            _networkStream = networkStream;
            _client = client;
            _outputStream = new HttpResponseStream(this, networkStream);
        }

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

            _cookieHeaders.Add(string.Join("; ", parts));
        }

        internal void EnsureHeadersSent()
        {
            if (_headersSent) return;
            _headersSent = true;

            string statusDesc = StatusCode switch
            {
                200 => "OK",
                204 => "No Content",
                206 => "Partial Content",
                301 => "Moved Permanently",
                302 => "Found",
                304 => "Not Modified",
                400 => "Bad Request",
                401 => "Unauthorized",
                403 => "Forbidden",
                404 => "Not Found",
                416 => "Range Not Satisfiable",
                500 => "Internal Server Error",
                _ => "Unknown"
            };

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {StatusCode} {statusDesc}\r\n");

            if (!string.IsNullOrEmpty(ContentType) && Headers["Content-Type"] == null)
            {
                Headers["Content-Type"] = ContentType;
            }

            if (ContentLength64 >= 0 && Headers["Content-Length"] == null)
            {
                Headers["Content-Length"] = ContentLength64.ToString();
            }

            if (Headers["Connection"] == null)
            {
                Headers["Connection"] = "close";
            }

            foreach (string key in Headers.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                var values = Headers.GetValues(key);
                if (values != null)
                {
                    foreach (var val in values)
                    {
                        sb.Append($"{key}: {val}\r\n");
                    }
                }
            }

            foreach (var ch in _cookieHeaders)
            {
                sb.Append($"Set-Cookie: {ch}\r\n");
            }

            sb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            _networkStream.Write(headerBytes, 0, headerBytes.Length);
            _networkStream.Flush();
        }

        public void Close()
        {
            if (_isClosed) return;
            _isClosed = true;

            try
            {
                EnsureHeadersSent();
                _networkStream.Flush();
            }
            catch { }

            try
            {
                _client.Close();
            }
            catch { }
        }

        private sealed class HttpResponseStream : Stream
        {
            private readonly TcpHttpResponse _response;
            private readonly NetworkStream _inner;

            public HttpResponseStream(TcpHttpResponse response, NetworkStream inner)
            {
                _response = response;
                _inner = inner;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush()
            {
                _response.EnsureHeadersSent();
                _inner.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                _response.EnsureHeadersSent();
                return _inner.FlushAsync(cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _response.EnsureHeadersSent();
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _response.EnsureHeadersSent();
                return _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                _response.EnsureHeadersSent();
                return _inner.WriteAsync(buffer, cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
