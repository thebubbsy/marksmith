using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MarkSmith.Plugins;

// The generic engine that turns a PluginManifest into a working IDiagramPlugin: installs the
// declared runtime + artifacts into the plugin's private folder, and renders diagrams by running
// the manifest's command spec as a subprocess. Every diagram plugin — built-in (PlantUML) or
// user-authored — goes through this one class; authoring a plugin means writing plugin.json, not C#.
public sealed class ManifestPlugin : IDiagramPlugin, IImporterPlugin
{
    private readonly PluginManifest _manifest;

    public ManifestPlugin(PluginManifest manifest) => _manifest = manifest;

    public string Id => _manifest.Id;
    public string Name => _manifest.Name;
    public string Description => _manifest.Description;
    public string Version => _manifest.Version;
    public IReadOnlyList<string> FenceLanguages => _manifest.FenceLanguages;
    public IReadOnlyList<string> ImportExtensions => _manifest.Import?.Extensions ?? (IReadOnlyList<string>)Array.Empty<string>();
    public bool IsDiagram => _manifest.Type == "diagram";
    public bool IsImporter => _manifest.Type == "importer";

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
            if (_manifest.Runtime is { Kind: "node" } && !File.Exists(PluginInstall.NodeExePath(RootDir)))
                return PluginInstallState.NotInstalled;
            foreach (var artifact in PlatformArtifacts())
            {
                // Extracted archives spread many files; the command path is the real health check
                // for those. npm artifacts are healthy when the package dir exists. Plain-file
                // artifacts must exist under their declared name.
                if (artifact.Source == "npm")
                {
                    if (artifact.Package == null ||
                        !Directory.Exists(Path.Combine(RootDir, "npm", "node_modules", artifact.Package)))
                        return PluginInstallState.NotInstalled;
                }
                else if (!artifact.Extract && !File.Exists(Path.Combine(RootDir, artifact.Name)))
                {
                    return PluginInstallState.NotInstalled;
                }
            }
            var command = ResolveCommandPath(_manifest.Type == "importer" ? _manifest.Import?.EffectiveCommand : _manifest.Render.Command);
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
        else if (_manifest.Runtime is { Kind: "node" })
        {
            await PluginInstall.InstallNodeAsync(http, RootDir, Report, cancellationToken);
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

    public string? ImportToMarkdown(string filePath)
    {
        if (_manifest.Import is not { } spec || State != PluginInstallState.Installed) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveCommandPath(spec.EffectiveCommand) ?? throw new InvalidOperationException($"Plugin '{Id}': import.command is empty."),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RootDir,
            };
            foreach (var arg in spec.Args)
                psi.ArgumentList.Add(arg
                    .Replace("{java}", PluginInstall.JavaExePath(RootDir))
                    .Replace("{node}", PluginInstall.NodeExePath(RootDir))
                    .Replace("{dir}", RootDir)
                    .Replace("{input}", filePath));

            using var process = Process.Start(psi);
            if (process == null) return null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            var timeoutMs = Math.Clamp(spec.TimeoutSeconds, 1, 300) * 1000;
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            if (process.ExitCode != 0) return null;

            var markdown = stdoutTask.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(markdown) ? null : markdown;
        }
        catch
        {
            return null;
        }
    }

    // Resolves {java}/{dir} in the command and applies the Windows ".exe" convention. Returns null
    // if the command is empty; returns strings still containing "{" only for {input}/{output},
    // which are per-render, not install-time.
    private string? ResolveCommandPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command
            .Replace("{java}", PluginInstall.JavaExePath(RootDir))
            .Replace("{node}", PluginInstall.NodeExePath(RootDir))
            .Replace("{dir}", RootDir);
        if (OperatingSystem.IsWindows() && !command.Contains('{') && !Path.HasExtension(command))
        {
            var withExe = command + ".exe";
            if (File.Exists(withExe)) command = withExe;
        }
        return command;
    }

    public bool IsThemeAware => _manifest.Render.ThemeInject != null ||
        _manifest.Render.Args.Any(a => a.Contains("{theme", StringComparison.Ordinal));

    private static string ExpandTheme(string s, PluginTheme theme) => s
        .Replace("{themeBackground}", theme.Background)
        .Replace("{themeText}", theme.Text)
        .Replace("{themeLine}", theme.Line)
        .Replace("{themeAccent}", theme.Accent);

    // Insert `styling` right after the diagram's opening directive line (e.g. PlantUML's
    // "@startuml") — skinparams have to live INSIDE the @startuml/@enduml block. Falls back to
    // prepending if no start directive is found.
    private static string InsertAfterStartTag(string source, string styling)
    {
        var nl = source.IndexOf('\n');
        if (nl >= 0 && source.AsSpan(0, nl).Trim().StartsWith("@start", StringComparison.OrdinalIgnoreCase))
            return source[..(nl + 1)] + styling + "\n" + source[(nl + 1)..];
        return styling + "\n" + source;
    }

    public string? RenderToSvg(string diagramSource, PluginTheme? theme = null)
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

            // Theme injection: splice the manifest's themed-styling snippet (colors filled from the
            // document theme) into the diagram source so the engine's own output matches the theme.
            if (theme != null && spec.ThemeInject is { } inject)
            {
                var styling = ExpandTheme(inject.Text, theme);
                diagramSource = inject.Mode == "afterStartTag"
                    ? InsertAfterStartTag(diagramSource, styling)
                    : styling + "\n" + diagramSource;
            }

            if (spec.Input == "file")
            {
                var ext = string.IsNullOrWhiteSpace(spec.InputExtension) ? ".txt" : spec.InputExtension;
                inputFile = Path.Combine(Path.GetTempPath(), $"marksmith-{Id}-{Guid.NewGuid():N}{ext}");
                File.WriteAllText(inputFile, diagramSource);
            }
            if (spec.Output == "file")
                outputFile = Path.Combine(Path.GetTempPath(), $"marksmith-{Id}-{Guid.NewGuid():N}.svg");

            // {outputBase} = {output} minus its .svg extension, for tools (LilyPond) that take an
            // output BASENAME and append the extension themselves. Theme placeholders are expanded
            // in args too, for engines themed via CLI flags rather than injected source.
            string Expand(string s)
            {
                s = s
                    .Replace("{java}", PluginInstall.JavaExePath(RootDir))
                    .Replace("{node}", PluginInstall.NodeExePath(RootDir))
                    .Replace("{dir}", RootDir)
                    .Replace("{input}", inputFile ?? "")
                    .Replace("{outputBase}", outputFile != null ? Path.ChangeExtension(outputFile, null) : "")
                    .Replace("{output}", outputFile ?? "");
                return theme != null ? ExpandTheme(s, theme) : s;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ResolveCommandPath(spec.Command) ?? throw new InvalidOperationException($"Plugin '{Id}': render.command is empty."),
                RedirectStandardInput = spec.Input == "stdin",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Match the UTF-8 output encoding on input too — without this, stdin uses the
                // console's OEM/ANSI code page and mangles non-ASCII diagram text (é, →, 日本語)
                // before the tool (which the manifests tell to read UTF-8) ever sees it.
                StandardInputEncoding = spec.Input == "stdin" ? new System.Text.UTF8Encoding(false) : null,
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

            // Some engines (D2) emit a root <svg> with a viewBox but NO width/height. An SVG
            // without intrinsic dimensions collapses inside a width:fit-content container (the
            // .plugin-diagram card), rendering as an empty sliver — verified live with D2's
            // output, which nests the sized svg one level down. Give the root explicit
            // dimensions from its own viewBox so it lays out like every other engine's output.
            var rootMatch = Regex.Match(svg, "^<svg\\b[^>]*>", RegexOptions.Singleline);
            if (rootMatch.Success && !Regex.IsMatch(rootMatch.Value, "\\bwidth\\s*=") )
            {
                var vb = Regex.Match(rootMatch.Value, "viewBox\\s*=\\s*\"[-\\d.]+[ ,]+[-\\d.]+[ ,]+([\\d.]+)[ ,]+([\\d.]+)\"");
                if (vb.Success)
                {
                    var sized = rootMatch.Value.Insert("<svg".Length, $" width=\"{vb.Groups[1].Value}\" height=\"{vb.Groups[2].Value}\"");
                    svg = sized + svg.Substring(rootMatch.Length);
                }
            }

            // Plugin output is injected raw into the preview/export HTML — never allow active
            // content through, whatever the tool produced. Full SVG sanitize (script elements,
            // on* handlers, javascript: hrefs, foreignObject), plus WCAG contrast enforcement.
            return SvgSanitizer.Sanitize(svg, theme?.Background);
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
