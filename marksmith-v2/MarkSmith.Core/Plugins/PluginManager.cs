using System.Collections.Concurrent;

namespace MarkSmith.Plugins;

// Registry of optional plugins: built-in manifests (BuiltinPlugins.cs) plus user/community
// plugins discovered from %LOCALAPPDATA%\MarkSmith\Plugins\<id>\plugin.json — drop a manifest
// folder there and it appears in Settings -> Plugins on next launch. Authoring spec + examples:
// the marksmith-plugins repo.
public sealed class PluginManager
{
    public static string PluginsBaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "Plugins");

    public static string PluginsRoot(string pluginId) => Path.Combine(PluginsBaseDir, pluginId);

    public IReadOnlyList<IMarksmithPlugin> All { get; }

    // Non-fatal problems found while loading user manifests ("<folder>: <why it was skipped>"),
    // surfaced in Settings -> Plugins so a typo'd plugin.json fails visibly, not silently.
    public IReadOnlyList<string> LoadWarnings { get; }

    // Content hash -> rendered SVG (or null for "failed"). Diagram plugins render out-of-process,
    // which costs real wall-clock time (subprocess round-trip); the live preview re-renders on
    // every debounced keystroke, so an unchanged fence must be free the second time. Bounded: the
    // preview produces a new key on every keystroke (each intermediate diagram source is distinct),
    // so an unbounded cache leaks monotonically over a long editing session. When it exceeds the
    // cap, the oldest half is dropped (insertion-ordered by _cacheOrder).
    private const int RenderCacheCap = 256;
    private readonly ConcurrentDictionary<string, string?> _renderCache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public PluginManager()
    {
        var plugins = new List<IMarksmithPlugin>();
        var warnings = new List<string>();

        foreach (var json in BuiltinPlugins.ManifestJson)
            plugins.Add(new ManifestPlugin(PluginManifest.Parse(json)));

        // Built-in ids win over same-id user manifests: a dropped-in plugin.json must not be able
        // to silently replace how a first-party plugin installs or what it executes. The builtin's
        // listing + install-dir state is authoritative, so a same-id user manifest is skipped
        // WITHOUT a warning — the user folder is just the builtin's own install payload.
        var builtinIds = new HashSet<string>(plugins.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(builtinIds, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(PluginsBaseDir))
        {
            // Sort so plugin precedence (which of two same-language plugins is authoritative) is
            // deterministic across machines — Directory.GetDirectories order is filesystem-defined.
            foreach (var dir in Directory.GetDirectories(PluginsBaseDir)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue; // payload-only folder (e.g. a built-in's install dir)
                try
                {
                    var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
                    if (!seenIds.Add(manifest.Id))
                    {
                        // Same-id USER plugin (two dropped-in manifests): warn loudly so the author
                        // sees the collision. Same-id as a builtin: expected (install dir), silent.
                        if (!builtinIds.Contains(manifest.Id))
                        {
                            warnings.Add($"{Path.GetFileName(dir)}: id '{manifest.Id}' is already taken — skipped.");
                        }
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

    // The INSTALLED renderer for this language, if any. Scans all claimants and returns the first
    // installed one — an earlier-enumerated but uninstalled plugin claiming the same language must
    // not mask a later installed one (which would show "install the plugin" for a plugin the user
    // already has).
    public IDiagramPlugin? FindDiagramRenderer(string fenceLanguage)
    {
        IDiagramPlugin? firstClaimant = null;
        foreach (var plugin in All)
        {
            if (plugin is IDiagramPlugin diagram &&
                diagram.FenceLanguages.Contains(fenceLanguage, StringComparer.OrdinalIgnoreCase))
            {
                if (plugin.State == PluginInstallState.Installed) return diagram;
                firstClaimant ??= diagram;
            }
        }
        return null;
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

    public string? RenderToSvgCached(IDiagramPlugin plugin, string diagramSource, PluginTheme? theme = null)
    {
        // Content-addressed key: SHA256 of id + source (+ theme colors, since the same source
        // renders differently under a different theme). string.GetHashCode() was a 32-bit,
        // collidable hash — two different diagrams could collide and the second would silently
        // render as the first (wrong-diagram bug, the worst kind for a document tool).
        var themeKey = theme is null ? "" : $"{theme.Background}|{theme.Text}|{theme.Line}|{theme.Accent}";
        var raw = plugin.Id + "\0" + themeKey + "\0" + diagramSource;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        if (_renderCache.TryGetValue(key, out var cached)) return cached;
        var svg = plugin.RenderToSvg(diagramSource, theme);
        if (_renderCache.TryAdd(key, svg))
        {
            _cacheOrder.Enqueue(key);
            while (_renderCache.Count > RenderCacheCap && _cacheOrder.TryDequeue(out var oldest))
                _renderCache.TryRemove(oldest, out _);
        }
        return svg;
    }
}
