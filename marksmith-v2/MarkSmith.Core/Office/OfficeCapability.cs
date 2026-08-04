using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Core.Office
{
    /// <summary>Result of opening a docx in Word itself (the only 100%-accurate renderer).</summary>
    public sealed record DocxVerification(int InlineShapes, int Shapes, int Paragraphs);

    /// <summary>
    /// Seam for the marksmith-office plugin: out-of-process Word automation via NetOffice.
    /// The core app never references Office COM — it shells to the plugin's office-host.exe,
    /// which is exactly how diagram plugins (PlantUML JRE etc.) already work.
    /// Returns null / false when the plugin payload or Word is absent — callers degrade.
    /// </summary>
    public interface IOfficeCapability
    {
        bool IsAvailable { get; }
        Task<DocxVerification?> VerifyDocxAsync(string docxPath, CancellationToken ct = default);
        Task<(byte[]? Bytes, string Mime)?> RenderDocxToImageAsync(string docxPath, CancellationToken ct = default);
    }

    public sealed class OfficeCapability : IOfficeCapability
    {
        public static readonly OfficeCapability Shared = new();
        private const int TimeoutMs = 240_000;

        private readonly string? _hostPath;

        public OfficeCapability(string? hostPath = null)
        {
            _hostPath = hostPath ?? LocateHost();
        }

        public static string PluginInstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarkSmith", "Plugins", "marksmith-office");

        /// <summary>
        /// Locates marksmith-office-host.exe: app-adjacent (shipped/dev) first, then the
        /// plugin install directory (downloaded payload), then common roots.
        /// </summary>
        public static string? LocateHost()
        {
            var roots = new List<string>();
            try { roots.Add(AppContext.BaseDirectory); } catch { }
            try { roots.Add(Path.Combine(AppContext.BaseDirectory, "plugins")); } catch { }
            try { roots.Add(Path.Combine(AppContext.BaseDirectory, "plugins", "marksmith-office")); } catch { }
            try { roots.Add(Path.Combine(AppContext.BaseDirectory, "plugins", "marksmith-office", "office-host")); } catch { }
            roots.Add(PluginInstallDir);
            roots.Add(Path.Combine(PluginInstallDir, "office-host"));

            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, "marksmith-office-host.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public bool IsAvailable => _hostPath != null && File.Exists(_hostPath) && Detect();

        private bool Detect()
        {
            var (code, _) = RunHost("detect");
            return code == 0;
        }

        public async Task<DocxVerification?> VerifyDocxAsync(string docxPath, CancellationToken ct = default)
        {
            if (_hostPath == null || !File.Exists(docxPath)) return null;
            var (code, stdout) = await RunHostAsync($"verify \"{docxPath}\"", ct).ConfigureAwait(false);
            if (code != 0) return null;
            try
            {
                return JsonSerializer.Deserialize<DocxVerification>(stdout);
            }
            catch
            {
                return null;
            }
        }

        public async Task<(byte[]? Bytes, string Mime)?> RenderDocxToImageAsync(string docxPath, CancellationToken ct = default)
        {
            if (_hostPath == null || !File.Exists(docxPath)) return null;

            string outPath = Path.Combine(Path.GetTempPath(),
                $"ms-office-render-{Guid.NewGuid():N}.img");
            try
            {
                var (code, _) = await RunHostAsync($"render \"{docxPath}\" \"{outPath}\"", ct).ConfigureAwait(false);
                if (code != 0 || !File.Exists(outPath)) return null;

                byte[] bytes = await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
                string ext = Path.GetExtension(outPath).ToLowerInvariant();
                string mime = ext switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "image/png"
                };
                return (bytes, mime);
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        private (int Code, string Stdout) RunHost(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(_hostPath!, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (1, "");
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(); } catch { } return (1, stderr); }
                return (p.ExitCode, stdout);
            }
            catch (Exception)
            {
                return (1, ""); // host missing / not runnable — degrade
            }
        }

        private async Task<(int Code, string Stdout)> RunHostAsync(string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(_hostPath!, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (1, "");
                var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = p.StandardError.ReadToEndAsync(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));
                try
                {
                    await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(); } catch { }
                    return (1, "");
                }
                string stdout = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                return (p.ExitCode, stdout);
            }
            catch (Exception)
            {
                return (1, ""); // host missing / not runnable — degrade
            }
        }
    }
}
