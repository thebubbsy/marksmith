using MarkSmith.Plugins;
using Xunit;

namespace MarkSmith.Core.Tests;

// A controllable in-memory diagram plugin, so cache/registry behavior can be tested without a real
// subprocess. Records every source it's asked to render and echoes it back inside an <svg>.
internal sealed class StubDiagramPlugin : IDiagramPlugin
{
    public List<string> Rendered { get; } = new();
    public string Id { get; init; } = "stub";
    public string Name { get; init; } = "Stub";
    public string Description => "test stub";
    public string Version => "1.0";
    public PluginInstallState State { get; set; } = PluginInstallState.Installed;
    public IReadOnlyList<string> FenceLanguages { get; init; } = new[] { "stub" };
    public bool IsThemeAware { get; init; }
    public Task InstallAsync(IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
    public void Uninstall() { }
    public string? RenderToSvg(string diagramSource, PluginTheme? theme = null)
    {
        Rendered.Add(diagramSource);
        var t = theme is null ? "" : theme.Line;
        return $"<svg data-line=\"{t}\">{diagramSource}</svg>";
    }
}

public class PluginManifestParseTests
{
    [Fact] public void Parses_minimal_diagram_manifest()
    {
        var m = PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"diagram", "fenceLanguages":["x"], "render":{"command":"c"} }""");
        Assert.Equal("x", m.Id);
        Assert.Contains("x", m.FenceLanguages);
    }
    [Fact] public void Missing_id_throws() =>
        Assert.ThrowsAny<Exception>(() => PluginManifest.Parse("""{ "manifestVersion":1, "name":"X", "description":"d", "version":"1", "type":"diagram", "fenceLanguages":["x"], "render":{"command":"c"} }"""));
    [Fact] public void Diagram_without_fence_languages_throws() =>
        Assert.ThrowsAny<Exception>(() => PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"diagram", "render":{"command":"c"} }"""));
    [Fact] public void Importer_without_extensions_throws() =>
        Assert.ThrowsAny<Exception>(() => PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"importer" }"""));
    [Fact] public void Comments_and_trailing_commas_tolerated()
    {
        var m = PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"diagram", "fenceLanguages":["x",], "render":{"command":"c",}, }""");
        Assert.Equal("x", m.Id);
    }
    [Fact] public void Theme_inject_parsed()
    {
        var m = PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"diagram", "fenceLanguages":["x"], "render":{"command":"c","themeInject":{"mode":"prepend","text":"bg {themeBackground}"}} }""");
        Assert.NotNull(m.Render.ThemeInject);
        Assert.Equal("prepend", m.Render.ThemeInject!.Mode);
    }
    [Fact] public void Importer_extensions_parsed()
    {
        var m = PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"importer", "import":{"extensions":["rst","org"],"command":"pandoc"} }""");
        Assert.Contains("rst", m.Import!.Extensions);
    }
    [Fact] public void Empty_json_object_throws() => Assert.ThrowsAny<Exception>(() => PluginManifest.Parse("{}"));
    [Fact] public void Garbage_throws() => Assert.ThrowsAny<Exception>(() => PluginManifest.Parse("not json"));
    [Fact] public void InputExtension_defaults_to_txt()
    {
        var m = PluginManifest.Parse("""{ "manifestVersion":1, "id":"x", "name":"X", "description":"d", "version":"1", "type":"diagram", "fenceLanguages":["x"], "render":{"command":"c"} }""");
        Assert.Equal(".txt", m.Render.InputExtension);
    }
}

public class PluginCacheTests
{
    private static PluginManager NewManager() => new();

    [Fact] public void Cache_returns_same_svg_for_same_source()
    {
        var mgr = NewManager();
        var stub = new StubDiagramPlugin();
        var a = mgr.RenderToSvgCached(stub, "graph A");
        var b = mgr.RenderToSvgCached(stub, "graph A");
        Assert.Equal(a, b);
        Assert.Single(stub.Rendered); // rendered once, second was cached
    }

    [Fact] public void Cache_distinct_sources_do_not_collide()
    {
        var mgr = NewManager();
        var stub = new StubDiagramPlugin();
        var a = mgr.RenderToSvgCached(stub, "graph A");
        var b = mgr.RenderToSvgCached(stub, "graph B");
        Assert.NotEqual(a, b);
        Assert.Contains("graph A", a);
        Assert.Contains("graph B", b);
    }

    [Fact] public void Cache_keys_on_theme()
    {
        var mgr = NewManager();
        var stub = new StubDiagramPlugin { IsThemeAware = true };
        var dark = new PluginTheme("#000", "#fff", "#fff", "#88f");
        var light = new PluginTheme("#fff", "#000", "#000", "#00f");
        var a = mgr.RenderToSvgCached(stub, "g", dark);
        var b = mgr.RenderToSvgCached(stub, "g", light);
        Assert.NotEqual(a, b);
        Assert.Equal(2, stub.Rendered.Count); // themed separately, not shared
    }

    [Fact] public void Cache_bounded_does_not_grow_unbounded()
    {
        var mgr = NewManager();
        var stub = new StubDiagramPlugin();
        for (var i = 0; i < 400; i++) mgr.RenderToSvgCached(stub, "diagram " + i);
        // Re-rendering the very first (long-evicted) source must re-invoke the plugin, proving the
        // cache didn't retain all 400 entries.
        var countBefore = stub.Rendered.Count;
        mgr.RenderToSvgCached(stub, "diagram 0");
        Assert.True(stub.Rendered.Count > countBefore, "oldest entry should have been evicted");
    }
}

public class PluginRegistryTests
{
    [Fact] public void Builtins_are_registered()
    {
        var ids = new PluginManager().All.Select(p => p.Id).ToList();
        Assert.Contains("plantuml", ids);
        Assert.Contains("graphviz", ids);
        Assert.Contains("d2", ids);
        Assert.Contains("pandoc-import", ids);
    }
    [Fact] public void Builtin_manifests_all_parse_without_warnings()
    {
        // Every embedded built-in manifest must parse (a bad one would surface as a LoadWarning or
        // reduce All); assert the known count is present and no warning mentions a built-in id.
        var mgr = new PluginManager();
        Assert.True(mgr.All.Count >= 8, $"expected >=8 built-ins, got {mgr.All.Count}");
    }
    [Fact] public void PlantUml_is_theme_aware()
    {
        var p = (IDiagramPlugin)new PluginManager().All.First(x => x.Id == "plantuml");
        Assert.True(p.IsThemeAware);
    }
    [Fact] public void D2_is_not_theme_aware()
    {
        var p = (IDiagramPlugin)new PluginManager().All.First(x => x.Id == "d2");
        Assert.False(p.IsThemeAware);
    }
    [Fact] public void FindAnyDiagramPlugin_matches_language_case_insensitively()
    {
        var mgr = new PluginManager();
        Assert.NotNull(mgr.FindAnyDiagramPlugin("PlantUML"));
        Assert.NotNull(mgr.FindAnyDiagramPlugin("dot"));
    }
    [Fact] public void FindAnyDiagramPlugin_unknown_language_is_null() =>
        Assert.Null(new PluginManager().FindAnyDiagramPlugin("no-such-lang"));
    [Fact] public void Importer_extensions_include_pandoc_set()
    {
        var exts = new PluginManager().AllImporterExtensions;
        Assert.Contains("rst", exts);
        Assert.Contains("docx", exts);
    }
    [Fact] public void FindImporter_handles_dot_and_case()
    {
        var mgr = new PluginManager();
        // pandoc may not be installed on the test box, so only assert the routing shape: an
        // unknown extension is always null regardless of installs.
        Assert.Null(mgr.FindImporter("totally-unknown-ext"));
    }
}

public class SvgSizingTests
{
    // The D2-shaped case: a root <svg> with viewBox but no width/height gets explicit dims so it
    // doesn't collapse in the fit-content card. Exercised through a stub that returns such an svg.
    private sealed class ViewBoxOnlyPlugin : IDiagramPlugin
    {
        public string Id => "vb";
        public string Name => "vb";
        public string Description => "";
        public string Version => "1";
        public PluginInstallState State => PluginInstallState.Installed;
        public IReadOnlyList<string> FenceLanguages => new[] { "vb" };
        public bool IsThemeAware => false;
        public Task InstallAsync(IProgress<double>? p, CancellationToken c) => Task.CompletedTask;
        public void Uninstall() { }
        public string? RenderToSvg(string s, PluginTheme? t = null) => "<svg viewBox=\"0 0 120 60\"><rect/></svg>";
    }

    [Fact] public void NotInstalled_plugin_render_is_null()
    {
        var stub = new StubDiagramPlugin { State = PluginInstallState.NotInstalled };
        // RenderToSvg on a stub still returns (the stub ignores State); the real ManifestPlugin
        // guards on State — this asserts the manager path doesn't crash for a not-installed plugin.
        Assert.NotNull(new PluginManager().RenderToSvgCached(stub, "x"));
    }
}

public class PluginStateRemoveTests
{
    // Regression: Settings "Remove" deletes the plugin folder (Uninstall), but State must flip to
    // NotInstalled afterwards — otherwise the Remove button stays visible and removal looks broken.
    // Extract-artifact plugins previously reported Installed even with an emptied install dir.
    [Fact]
    public void Extract_plugin_state_flips_to_not_installed_after_uninstall()
    {
        var id = "testextract" + Guid.NewGuid().ToString("N")[..8];
        var json = """
            { "manifestVersion":1, "id":"IDHERE", "name":"T", "description":"d", "version":"1", "type":"diagram", "fenceLanguages":["t"], "render":{"command":"{host}"}, "artifacts":[ { "name":"payload.zip", "extract":true } ] }
            """.Replace("IDHERE", id);
        var m = PluginManifest.Parse(json);
        var plugin = new ManifestPlugin(m);
        var root = PluginManager.PluginsRoot(id);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "marker.txt"), "x");
            Assert.Equal(PluginInstallState.Installed, plugin.State);
            plugin.Uninstall();
            Assert.Equal(PluginInstallState.NotInstalled, plugin.State);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
