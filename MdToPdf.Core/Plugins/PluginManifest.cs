using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdToPdf.Plugins;

// The declarative "plugin language": everything a diagram plugin is — identity, what fence
// languages it claims, what it downloads, and how to invoke it — expressed as a plugin.json
// manifest instead of C#. Built-in plugins (PlantUML) are embedded manifests in
// BuiltinPlugins.cs; user/community plugins are the same JSON dropped into
// %LOCALAPPDATA%\MdToPdf\Plugins\<id>\plugin.json. The authoring spec lives in the
// marksmith-plugins repo (SPEC.md + a JSON schema) — keep it in sync with these models.
public sealed class PluginManifest
{
    public int ManifestVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string License { get; set; } = "";

    // "diagram" is the only type today (fenced code block -> SVG). The field exists so future
    // types (importers, exporters, themes) can share the same manifest/install machinery.
    public string Type { get; set; } = "diagram";

    public List<string> FenceLanguages { get; set; } = new();

    public PluginRuntime? Runtime { get; set; }
    public List<PluginArtifact> Artifacts { get; set; } = new();
    public PluginRenderSpec Render { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PluginManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("plugin.json parsed to null.");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidOperationException("plugin.json is missing \"id\".");
        if (manifest.Type == "diagram" && manifest.FenceLanguages.Count == 0)
            throw new InvalidOperationException($"Plugin '{manifest.Id}': diagram plugins must declare fenceLanguages.");
        return manifest;
    }
}

// A managed runtime dependency the host knows how to provision. "jre" downloads a private
// Eclipse Temurin JRE (via Adoptium's official API) into the plugin's folder — plugins must not
// assume anything is on the user's PATH.
public sealed class PluginRuntime
{
    public string Kind { get; set; } = "jre";
    public int MajorVersion { get; set; } = 17;
}

// One downloadable file. os/arch null = applies everywhere; otherwise filtered to the current
// platform ("windows"/"linux"/"mac", "x64"/"aarch64") so a manifest lists all platform variants
// side by side and each machine downloads only its own.
public sealed class PluginArtifact
{
    public string Name { get; set; } = "";
    public string? Os { get; set; }
    public string? Arch { get; set; }

    // "url": direct download (pair with sha256 — required for registry-listed plugins).
    // "github-latest": resolve the newest release asset of `repo` matching `assetPattern`
    // (floating version, so no pin — the tradeoff for auto-tracking upstream releases).
    public string Source { get; set; } = "url";
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public string? Repo { get; set; }
    public string? AssetPattern { get; set; }

    // Archives (.zip / .tar.gz) are extracted into the plugin folder; stripRoot removes a single
    // top-level wrapper directory (the common "toolname-1.2.3/" layout).
    public bool Extract { get; set; }
    public bool StripRoot { get; set; }
}

public sealed class PluginRenderSpec
{
    // Placeholders: {java} = the plugin's private JRE java executable (requires runtime.kind=jre),
    // {dir} = the plugin's folder, {input}/{output} = temp file paths when the respective mode is
    // "file". On Windows a command with no extension gets ".exe" appended automatically, so one
    // manifest works cross-platform ("{dir}/d2" -> d2.exe / d2).
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();

    public string Input { get; set; } = "stdin";   // "stdin" | "file"
    public string Output { get; set; } = "stdout"; // "stdout" | "file"
    public int TimeoutSeconds { get; set; } = 20;

    public PluginSourceWrap? Wrap { get; set; }
}

// Optional convenience wrapper around the user's fence content before it reaches the tool — e.g.
// PlantUML requires @startuml/@enduml delimiters, but users pasting from docs often omit them.
public sealed class PluginSourceWrap
{
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    // Skip wrapping when the source already contains this token (e.g. "@start" for PlantUML,
    // matching any of @startuml/@startmindmap/@startgantt/...).
    public string? UnlessContains { get; set; }
}
