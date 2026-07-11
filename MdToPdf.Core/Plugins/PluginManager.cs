using System.Collections.Concurrent;

namespace MdToPdf.Plugins;

// Registry of optional plugins: built-in manifests (BuiltinPlugins.cs) plus user/community
// plugins discovered from %LOCALAPPDATA%\MdToPdf\Plugins\<id>\plugin.json — drop a manifest
// folder there and it appears in Settings -> Plugins on next launch. Authoring spec + examples:
// the marksmith-plugins repo.
public sealed class PluginManager
{
    public static string PluginsBaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf", "Plugins");

    public static string PluginsRoot(string pluginId) => Path.Combine(PluginsBaseDir, pluginId);

    public IReadOnlyList<IMarksmithPlugin> All { get; }

    // Non-fatal problems found while loading user manifests ("<folder>: <why it was skipped>"),
    // surfaced in Settings -> Plugins so a typo'd plugin.json fails visibly, not silently.
    public IReadOnlyList<string> LoadWarnings { get; }

    // Content hash -> rendered SVG (or null for "failed"). Diagram plugins render out-of-process,
    // which costs real wall-clock time (subprocess round-trip); the live preview re-renders on
    // every debounced keystroke, so an unchanged fence must be free the second time.
    private readonly ConcurrentDictionary<string, string?> _renderCache = new();

    public PluginManager()
    {
        var plugins = new List<IMarksmithPlugin>();
        var warnings = new List<string>();

        foreach (var json in BuiltinPlugins.ManifestJson)
            plugins.Add(new ManifestPlugin(PluginManifest.Parse(json)));

        // Built-in ids win over same-id user manifests: a dropped-in plugin.json must not be able
        // to silently replace how a first-party plugin installs or what it executes.
        var seenIds = new HashSet<string>(plugins.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(PluginsBaseDir))
        {
            foreach (var dir in Directory.GetDirectories(PluginsBaseDir))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue; // payload-only folder (e.g. a built-in's install dir)
                try
                {
                    var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
                    if (!seenIds.Add(manifest.Id))
                    {
                        warnings.Add($"{Path.GetFileName(dir)}: id '{manifest.Id}' is already taken — skipped.");
                        continue;
                    }
                    if (!string.Equals(Path.GetFileName(dir), manifest.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"{Path.GetFileName(dir)}: folder name must match the manifest id '{manifest.Id}' — skipped.");
                        continue;
                    }
                    plugins.Add(new ManifestPlugin(manifest));
                }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.GetFileName(dir)}: {ex.Message}");
                }
            }
        }

        All = plugins;
        LoadWarnings = warnings;
    }

    public IDiagramPlugin? FindDiagramRenderer(string fenceLanguage)
    {
        var plugin = FindAnyDiagramPlugin(fenceLanguage);
        return plugin?.State == PluginInstallState.Installed ? plugin : null;
    }

    // Matches by fence language regardless of install state — lets callers distinguish "no plugin
    // claims this language at all" (leave the code block alone) from "a plugin claims it but isn't
    // installed" (show an affordance to go install it) in MarkdownHtmlService's fence hook.
    public IDiagramPlugin? FindAnyDiagramPlugin(string fenceLanguage)
    {
        foreach (var plugin in All)
        {
            if (plugin is IDiagramPlugin diagram &&
                diagram.FenceLanguages.Contains(fenceLanguage, StringComparer.OrdinalIgnoreCase))
                return diagram;
        }
        return null;
    }

    // `extension` with or without the dot, any case.
    public IImporterPlugin? FindImporter(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        foreach (var plugin in All)
        {
            if (plugin is IImporterPlugin importer && plugin.State == PluginInstallState.Installed &&
                importer.ImportExtensions.Contains(ext))
                return importer;
        }
        return null;
    }

    // Every extension any registered importer claims (installed or not) — the shells use this to
    // widen file pickers/drop filters so users can discover the capability.
    public IReadOnlyList<string> AllImporterExtensions =>
        All.OfType<IImporterPlugin>().SelectMany(p => p.ImportExtensions).Distinct().ToList();

    public string? RenderToSvgCached(IDiagramPlugin plugin, string diagramSource)
    {
        var key = plugin.Id + ":" + diagramSource.GetHashCode();
        if (_renderCache.TryGetValue(key, out var cached)) return cached;
        var svg = plugin.RenderToSvg(diagramSource);
        _renderCache[key] = svg;
        return svg;
    }
}
