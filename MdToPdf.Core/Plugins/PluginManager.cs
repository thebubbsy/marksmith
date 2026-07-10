using System.Collections.Concurrent;

namespace MdToPdf.Plugins;

// Registry of optional plugins. Static list for now (one entry: PlantUML) — the natural next step
// if this grows is a catalog fetched over HTTP, same spirit as AppServices being a hand-rolled
// composition root until the app outgrows it (see AppServices.cs's own comment to that effect).
public sealed class PluginManager
{
    public static string PluginsRoot(string pluginId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf", "Plugins", pluginId);

    public IReadOnlyList<IMarksmithPlugin> All { get; }

    // Content hash -> rendered SVG (or null for "failed"). Diagram plugins render out-of-process,
    // which costs real wall-clock time (subprocess round-trip); the live preview re-renders on
    // every debounced keystroke, so an unchanged fence must be free the second time.
    private readonly ConcurrentDictionary<string, string?> _renderCache = new();

    public PluginManager()
    {
        All = new IMarksmithPlugin[] { new PlantUml.PlantUmlPlugin() };
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

    public string? RenderToSvgCached(IDiagramPlugin plugin, string diagramSource)
    {
        var key = plugin.Id + ":" + diagramSource.GetHashCode();
        if (_renderCache.TryGetValue(key, out var cached)) return cached;
        var svg = plugin.RenderToSvg(diagramSource);
        _renderCache[key] = svg;
        return svg;
    }
}
