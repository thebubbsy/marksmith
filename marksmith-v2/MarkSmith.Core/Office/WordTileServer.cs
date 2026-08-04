using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Core.Office
{
    /// <summary>
    /// Client for the marksmith-office host's persistent `server` mode: Word stays open across
    /// commands, so re-rasterizing only the dirty page-band of a document is cheap (tiled /
    /// incremental Word-exact preview) instead of paying a Word cold start per render.
    /// </summary>
    public sealed class WordTileServer : IDisposable
    {
        private const int CommandTimeoutMs = 180_000;

        private readonly Process? _proc;
        private readonly StreamWriter? _stdin;
        private readonly StreamReader? _stdout;
        private readonly string _tempDir;
        private bool _disposed;

        public int PageCount { get; private set; }

        private WordTileServer(Process proc, string tempDir)
        {
            _proc = proc;
            _stdin = proc.StandardInput;
            _stdout = proc.StandardOutput;
            _tempDir = tempDir;
        }

        /// <summary>Starts the persistent host and opens the document; null if the host or Word is unavailable.</summary>
        public static WordTileServer? Start(string docxPath)
        {
            string? host = OfficeCapability.LocateHost();
            if (host == null || !File.Exists(docxPath)) return null;

            try
            {
                var psi = new ProcessStartInfo(host, "server")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                if (proc == null) return null;

                string tempDir = Path.Combine(Path.GetTempPath(), "MarkSmithFidelity");
                Directory.CreateDirectory(tempDir);

                var server = new WordTileServer(proc, tempDir);
                if (!server.Send("open", new System.Collections.Generic.Dictionary<string, object?> { ["docx"] = Path.GetFullPath(docxPath) },
                        out var open, out var pages) || open != true)
                {
                    server.Dispose();
                    return null;
                }
                server.PageCount = pages;
                return server;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Re-opens the document in the SAME Word instance (closes the old one first).</summary>
        public bool Reopen(string docxPath)
        {
            if (!Send("open", new System.Collections.Generic.Dictionary<string, object?> { ["docx"] = Path.GetFullPath(docxPath) },
                    out var ok, out var pages)) return false;
            if (ok != true) return false;
            PageCount = pages;
            return true;
        }

        /// <summary>Rasterizes one page band to PNG bytes; null on failure.</summary>
        public byte[]? RenderPage(int page, double scale = 0.5)
        {
            string outPath = Path.Combine(_tempDir, $"tile_{page:D4}.png");
            File.Delete(outPath);
            if (!Send("page", new System.Collections.Generic.Dictionary<string, object?> { ["page"] = page, ["scale"] = scale, ["out"] = outPath },
                    out var ok, out var bytes)) return null;
            if (ok != true) return null;
            try
            {
                return File.ReadAllBytes(outPath);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_stdin != null && _proc != null && !_proc.HasExited)
                {
                    try
                    {
                        _stdin.WriteLine(JsonSerializer.Serialize(new { cmd = "close" }));
                        _stdin.Flush();
                        _proc.WaitForExit(5000);
                    }
                    catch { }
                }
            }
            catch { }
            try { _proc?.Kill(entireProcessTree: true); } catch { }
            try { _proc?.Dispose(); } catch { }
        }

        private bool Send(string cmd, System.Collections.Generic.Dictionary<string, object?> payload, out bool ok, out int value)
        {
            ok = false;
            value = 0;
            if (_stdin == null || _stdout == null || _proc == null || _proc.HasExited) return false;
            try
            {
                payload["cmd"] = cmd;
                _stdin.WriteLine(JsonSerializer.Serialize(payload));
                _stdin.Flush();
                string? line = _stdout.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(CommandTimeoutMs)).GetAwaiter().GetResult();
                if (line == null) return false;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("ok", out var o) || o.GetBoolean() != true) return false;
                ok = true;
                if (root.TryGetProperty("pages", out var p)) value = p.GetInt32();
                if (root.TryGetProperty("bytes", out var b)) value = b.GetInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
