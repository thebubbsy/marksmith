using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public static class StreamingService
    {
        private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "application/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",
            [".ico"] = "image/x-icon",
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime",
            [".avi"] = "video/x-msvideo",
            [".wmv"] = "video/x-ms-wmv",
            [".mp3"] = "audio/mpeg",
            [".wav"] = "audio/wav",
            [".ogg"] = "audio/ogg",
            [".flac"] = "audio/flac",
            [".m4a"] = "audio/mp4",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain; charset=utf-8",
            [".zip"] = "application/zip",
            [".rar"] = "application/x-rar-compressed",
            [".7z"] = "application/x-7z-compressed"
        };

        public static string GetMimeType(string path)
        {
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && MimeTypes.TryGetValue(ext, out var mime))
            {
                return mime;
            }
            return "application/octet-stream";
        }

        public static async Task StreamFileAsync(HttpListenerContext context, string filePath, bool asAttachment = false)
        {
            var response = context.Response;
            var request = context.Request;

            if (!File.Exists(filePath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteStringAsync(response, "File not found");
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var totalLength = fileInfo.Length;
            var mime = GetMimeType(filePath);

            response.Headers["Accept-Ranges"] = "bytes";
            response.ContentType = mime;

            if (asAttachment)
            {
                var safeName = Uri.EscapeDataString(Path.GetFileName(filePath));
                response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"; filename*=UTF-8''{safeName}";
            }

            var rangeHeader = request.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var rangeSpec = rangeHeader["bytes=".Length..].Trim();
                long start = 0;
                long end = totalLength - 1;

                var parts = rangeSpec.Split('-');
                if (parts.Length == 2)
                {
                    if (string.IsNullOrEmpty(parts[0]) && long.TryParse(parts[1], out var suffixLength))
                    {
                        // Suffix byte range: bytes=-500 (last 500 bytes)
                        start = Math.Max(0, totalLength - suffixLength);
                        end = totalLength - 1;
                    }
                    else
                    {
                        if (long.TryParse(parts[0], out var pStart))
                        {
                            start = pStart;
                        }

                        if (long.TryParse(parts[1], out var pEnd))
                        {
                            end = pEnd;
                        }
                        else if (string.IsNullOrEmpty(parts[1]))
                        {
                            end = totalLength - 1;
                        }
                    }
                }

                if (start >= totalLength || end >= totalLength || start > end || start < 0)
                {
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.Headers["Content-Range"] = $"bytes */{totalLength}";
                    response.Close();
                    return;
                }

                long chunkLength = end - start + 1;
                response.StatusCode = (int)HttpStatusCode.PartialContent;
                response.Headers["Content-Range"] = $"bytes {start}-{end}/{totalLength}";
                response.ContentLength64 = chunkLength;

                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                    fs.Seek(start, SeekOrigin.Begin);

                    var buffer = new byte[64 * 1024];
                    long bytesRemaining = chunkLength;

                    while (bytesRemaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
                        if (read <= 0) break;

                        await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                        bytesRemaining -= read;
                    }
                }
                catch
                {
                    // Client disconnected or closed connection
                }
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentLength64 = totalLength;

                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                    await fs.CopyToAsync(response.OutputStream);
                }
                catch
                {
                    // Client disconnected
                }
            }

            try
            {
                response.Close();
            }
            catch { }
        }

        private static async Task WriteStringAsync(HttpListenerResponse res, string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }
}
