using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MdToPdf.Plugins;

// The generic engine that turns a PluginManifest into a working IDiagramPlugin: installs the
// declared runtime + artifacts into the plugin's private folder, and renders diagrams by running
// the manifest's command spec as a subprocess. Every diagram plugin — built-in (PlantUML) or
// user-authored — goes through this one class; authoring a plugin means writing plugin.json, not C#.
public sealed class ManifestPlugin : IDiagramPlugin
{
    private readonly PluginManifest _manifest;

    public ManifestPlugin(PluginManifest manifest) => _manifest = manifest;

    public string Id => _manifest.Id;
    public string Name => _manifest.Name;
    public string Description => _manifest.Description;
    public string Version => _manifest.Version;
    public IReadOnlyList<string> FenceLanguages => _manifest.FenceLanguages;

    private string RootDir => PluginManager.PluginsRoot(_manifest.Id);

    private IEnumerable<PluginArtifact> PlatformArtifacts()
    {
        var (os, arch) = PluginInstall.PlatformIds();
        return _manifest.Artifacts.Where(a =>
            (a.Os == null || string.Equals(a.Os, os, StringComparison.OrdinalIgnoreCase)) &&
            (a.Arch == null || string.Equals(a.Arch, arch, StringComparison.OrdinalIgnoreCase)));
    }

    public PluginInstallState State
    {
        get
        {
            if (_manifest.Runtime is { Kind: "jre" } && !File.Exists(PluginInstall.JavaExePath(RootDir)))
                return PluginInstallState.NotInstalled;
            foreach (var artifact in PlatformArtifacts())
            {
                // Extracted archives spread many files; the command path is the real health check
                // for those. Plain-file artifacts must exist under their declared name.
                if (!artifact.Extract && !File.Exists(Path.Combine(RootDir, artifact.Name)))
                    return PluginInstallState.NotInstalled;
            }
            var command = ResolveCommandPath();
            if (command != null && !command.StartsWith("{") && !File.Exists(command))
                return PluginInstallState.NotInstalled;
            return PluginInstallState.Installed;
        }
    }

    public async Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootDir);
        using var http = PluginInstall.NewHttpClient();

        var artifacts = PlatformArtifacts().ToList();
        var steps = (_manifest.Runtime != null ? 1 : 0) + artifacts.Count;
        if (steps == 0) { progress?.Report(1.0); return; }
        var step = 0;

        void Report(double withinStep) => progress?.Report((step + withinStep) / steps);

        if (_manifest.Runtime is { Kind: "jre" } jre)
        {
            await PluginInstall.InstallJreAsync(http, RootDir, jre.MajorVersion, Report, cancellationToken);
            step++;
        }
        else if (_manifest.Runtime != null)
        {
            throw new InvalidOperationException($"Plugin '{Id}': unsupported runtime kind '{_manifest.Runtime.Kind}'.");
        }

        foreach (var artifact in artifacts)
        {
            await PluginInstall.InstallArtifactAsync(http, RootDir, artifact, Report, cancellationToken);
            step++;
        }

        if (State != PluginInstallState.Installed)
            throw new InvalidOperationException($"Plugin '{Id}': install completed but required files are still missing.");
        progress?.Report(1.0);
    }

    public void Uninstall()
    {
        if (Directory.Exists(RootDir)) Directory.Delete(RootDir, recursive: true);
    }

    // Resolves {java}/{dir} in the command and applies the Windows ".exe" convention. Returns null
    // if the command is empty; returns strings still containing "{" only for {input}/{output},
    // which are per-render, not install-time.
    private string? ResolveCommandPath()
    {
        var command = _manifest.Render.Command;
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command
            .Replace("{java}", PluginInstall.JavaExePath(RootDir))
            .Replace("{dir}", RootDir);
        if (OperatingSystem.IsWindows() && !command.Contains('{') && !Path.HasExtension(command))
        {
            var withExe = command + ".exe";
            if (File.Exists(withExe)) command = withExe;
        }
        return command;
    }

    public string? RenderToSvg(string diagramSource)
    {
        if (State != PluginInstallState.Installed) return null;

        string? inputFile = null, outputFile = null;
        try
        {
            var spec = _manifest.Render;
            if (spec.Wrap is { } wrap &&
                (wrap.UnlessContains == null || !diagramSource.Contains(wrap.UnlessContains, StringComparison.Ordinal)))
            {
                diagramSource = wrap.Prefix + diagramSource.Trim() + wrap.Suffix;
            }
            if (spec.Input == "file")
            {
                var ext = string.IsNullOrWhiteSpace(spec.InputExtension) ? ".txt" : spec.InputExtension;
                inputFile = Path.Combine(Path.GetTempPath(), $"marksmith-{Id}-{Guid.NewGuid():N}{ext}");
                File.WriteAllText(inputFile, diagramSource);
            }
            if (spec.Output == "file")
                outputFile = Path.Combine(Path.GetTempPath(), $"marksmith-{Id}-{Guid.NewGuid():N}.svg");

            string Expand(string s) => s
                .Replace("{java}", PluginInstall.JavaExePath(RootDir))
                .Replace("{dir}", RootDir)
                .Replace("{input}", inputFile ?? "")
                .Replace("{output}", outputFile ?? "");

            var psi = new ProcessStartInfo
            {
                FileName = ResolveCommandPath() ?? throw new InvalidOperationException($"Plugin '{Id}': render.command is empty."),
                RedirectStandardInput = spec.Input == "stdin",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RootDir,
            };
            foreach (var arg in spec.Args) psi.ArgumentList.Add(Expand(arg));

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (spec.Input == "stdin")
            {
                process.StandardInput.Write(diagramSource);
                process.StandardInput.Close();
            }

            var timeoutMs = Math.Clamp(spec.TimeoutSeconds, 1, 120) * 1000;
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            var raw = spec.Output == "file"
                ? (File.Exists(outputFile) ? File.ReadAllText(outputFile!) : "")
                : stdoutTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var start = raw.IndexOf("<svg", StringComparison.Ordinal);
            var end = raw.LastIndexOf("</svg>", StringComparison.Ordinal);
            if (start < 0 || end < 0) return null;
            var svg = raw.Substring(start, end + "</svg>".Length - start);

            // Defensive: plugin output is injected raw into the preview/export HTML — never allow
            // active content through, whatever the tool produced.
            return Regex.Replace(svg, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (inputFile != null && File.Exists(inputFile)) File.Delete(inputFile); } catch { }
            try { if (outputFile != null && File.Exists(outputFile)) File.Delete(outputFile); } catch { }
        }
    }
}
