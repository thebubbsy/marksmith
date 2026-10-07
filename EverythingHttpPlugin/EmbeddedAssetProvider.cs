using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public sealed class EmbeddedAssetProvider
    {
        private sealed class CachedAsset
        {
            public byte[] RawBytes { get; init; } = Array.Empty<byte>();
            public byte[] GzipBytes { get; init; } = Array.Empty<byte>();
            public string ContentType { get; init; } = "text/plain";
            public string ETag { get; init; } = "";
        }

        private readonly ConcurrentDictionary<string, CachedAsset> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _overrideDir;

        public EmbeddedAssetProvider(string? overrideDir = null)
        {
            _overrideDir = overrideDir;
            LoadEmbeddedAssets();
        }

        private void LoadEmbeddedAssets()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames();

            foreach (var resName in names)
            {
                // Names are e.g. "EverythingHttpPlugin.Embedded.index.html" or "EverythingHttpPlugin.Embedded.css.app.css"
                if (!resName.Contains(".Embedded.", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = resName[(resName.IndexOf(".Embedded.", StringComparison.OrdinalIgnoreCase) + ".Embedded.".Length)..];
                // In embedded resource names, subdirectories are dots. Let us normalize known paths:
                var routePath = NormalizeResourceRoute(relative);

                using var stream = assembly.GetManifestResourceStream(resName);
                if (stream == null) continue;

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var rawBytes = ms.ToArray();

                var gzipBytes = CompressGzip(rawBytes);
                var etag = $"\"{ComputeHash(rawBytes)}\"";
                var ctype = StreamingService.GetMimeType(routePath);

                var asset = new CachedAsset
                {
                    RawBytes = rawBytes,
                    GzipBytes = gzipBytes,
                    ContentType = ctype,
                    ETag = etag
                };

                _cache[routePath] = asset;
                if (routePath.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    _cache["/"] = asset;
                }
                if (routePath.Equals("/login.html", StringComparison.OrdinalIgnoreCase))
                {
                    _cache["/login"] = asset;
                }
            }
        }

        private static string NormalizeResourceRoute(string relative)
        {
            // e.g. css.app.css -> /css/app.css
            // js.app.js -> /js/app.js
            // index.html -> /index.html
            if (relative.StartsWith("css.", StringComparison.OrdinalIgnoreCase))
            {
                return "/css/" + relative["css.".Length..];
            }
            if (relative.StartsWith("js.", StringComparison.OrdinalIgnoreCase))
            {
                return "/js/" + relative["js.".Length..];
            }
            return "/" + relative;
        }

        public async Task<bool> ServeAssetAsync(HttpListenerContext context, string path)
        {
            // 1. Check override directory
            if (!string.IsNullOrEmpty(_overrideDir) && Directory.Exists(_overrideDir))
            {
                var cleanPath = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var localFile = Path.Combine(_overrideDir, cleanPath);
                if (File.Exists(localFile))
                {
                    await StreamingService.StreamFileAsync(context, localFile);
                    return true;
                }
            }

            // 2. Normalize path
            if (string.IsNullOrEmpty(path) || path == "/") path = "/";
            if (!_cache.TryGetValue(path, out var asset))
            {
                // Check without leading slash or with .html
                if (!_cache.TryGetValue("/" + path.TrimStart('/'), out asset))
                {
                    if (!_cache.TryGetValue(path + ".html", out asset))
                    {
                        return false;
                    }
                }
            }

            var req = context.Request;
            var res = context.Response;

            // Check ETag
            var ifNoneMatch = req.Headers["If-None-Match"];
            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(asset.ETag))
            {
                res.StatusCode = (int)HttpStatusCode.NotModified;
                res.Headers["ETag"] = asset.ETag;
                res.Close();
                return true;
            }

            res.Headers["ETag"] = asset.ETag;
            res.Headers["Cache-Control"] = "public, max-age=3600";
            res.ContentType = asset.ContentType;

            var acceptEncoding = req.Headers["Accept-Encoding"] ?? "";
            bool useGzip = acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase) && asset.GzipBytes.Length < asset.RawBytes.Length;

            if (useGzip)
            {
                res.Headers["Content-Encoding"] = "gzip";
                res.ContentLength64 = asset.GzipBytes.Length;
                await res.OutputStream.WriteAsync(asset.GzipBytes);
            }
            else
            {
                res.ContentLength64 = asset.RawBytes.Length;
                await res.OutputStream.WriteAsync(asset.RawBytes);
            }

            res.Close();
            return true;
        }

        private static byte[] CompressGzip(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            {
                gz.Write(raw, 0, raw.Length);
            }
            return ms.ToArray();
        }

        private static string ComputeHash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(bytes);
            return Convert.ToHexString(h).ToLowerInvariant()[..16];
        }
    }
}
