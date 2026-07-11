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

    // "diagram": fenced code block -> SVG (render spec). "importer": non-Markdown file ->
    // Markdown on open/drop (import spec). Both share the same install machinery.
    public string Type { get; set; } = "diagram";

    public List<string> FenceLanguages { get; set; } = new();

    public PluginRuntime? Runtime { get; set; }
    public List<PluginArtifact> Artifacts { get; set; } = new();
    public PluginRenderSpec Render { get; set; } = new();
    public PluginImportSpec? Import { get; set; }

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
        if (manifest.Type == "importer" && (manifest.Import == null || manifest.Import.Extensions.Count == 0))
            throw new InvalidOperationException($"Plugin '{manifest.Id}': importer plugins must declare import.extensions.");
        return manifest;
    }
}

// A managed runtime dependency the host knows how to provision. "jre" downloads a private
// Eclipse Temurin JRE (via Adoptium's official API); "node" downloads the current Node.js LTS
// (via nodejs.org's official dist index). Both land inside the plugin's folder — plugins must
// not assume anything is on the user's PATH.
public sealed class PluginRuntime
{
    public string Kind { get; set; } = "jre"; // "jre" | "node"
    public int MajorVersion { get; set; } = 17; // jre only; node always provisions current LTS
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
    // "npm": `npm install <package>` into <plugin>/npm using the plugin's provisioned Node
    // runtime (requires runtime.kind=node) — for JS tools that aren't distributed as a
    // self-contained bundle. The installed entry point lives at
    // {dir}/npm/node_modules/<package>/..., and `name` is ignored.
    public string Source { get; set; } = "url";
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public string? Repo { get; set; }
    public string? AssetPattern { get; set; }
    public string? Package { get; set; }      // npm: package name
    public string? PackageVersion { get; set; } // npm: exact version (recommended — pins the tree)

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

    // File extension for the {input} temp file (with the dot, e.g. ".typ"). Tools like Typst and
    // D2 sniff or enforce the input extension, so ".txt" isn't universally safe.
    public string InputExtension { get; set; } = ".txt";

    public PluginSourceWrap? Wrap { get; set; }
}

// How an importer plugin turns a non-Markdown file into Markdown. The user's real file path
// replaces {input}; the tool prints Markdown to stdout. (No temp copies — tools like Pandoc
// infer the input format from the file's own extension.)
public sealed class PluginImportSpec
{
    // Extensions claimed, lowercase, no dot (e.g. ["rst", "org", "docx"]).
    public List<string> Extensions { get; set; } = new();
    public string Command { get; set; } = "";
    // Per-OS overrides for tools whose archive layout differs by platform (e.g. Pandoc:
    // pandoc.exe at the archive root on Windows, bin/pandoc inside the Linux/macOS tarballs).
    public string? CommandWindows { get; set; }
    public string? CommandLinux { get; set; }
    public string? CommandMac { get; set; }
    public List<string> Args { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;

    public string EffectiveCommand =>
        (OperatingSystem.IsWindows() ? CommandWindows
        : OperatingSystem.IsMacOS() ? CommandMac
        : CommandLinux) ?? Command;
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
