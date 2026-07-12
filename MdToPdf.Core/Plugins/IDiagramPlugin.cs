namespace MdToPdf.Plugins;

// The document theme's colors, as seen by diagram plugins. A subset of ThemeDefinition on
// purpose: manifests reference these via {themeBackground}/{themeText}/{themeLine}/{themeAccent}
// placeholders, so this is public plugin-facing API — keep it small and stable.
public sealed record PluginTheme(string Background, string Text, string Line, string Accent)
{
    public static PluginTheme From(Models.ThemeDefinition theme) =>
        new(theme.Background, theme.Primary, theme.Line, theme.Heading);
}

// A plugin that turns fenced-code-block text into an SVG diagram, the same role Mermaid plays
// natively — except Mermaid renders client-side (mermaid.min.js inside the WebView) while a
// diagram plugin renders out-of-process, synchronously, before the HTML is ever built. See
// MarkdownHtmlService.cs's plugin-fence hook for where RenderToSvg gets called.
public interface IDiagramPlugin : IMarksmithPlugin
{
    // Fenced-code-block languages this plugin claims, lowercase, e.g. ["plantuml", "puml"].
    IReadOnlyList<string> FenceLanguages { get; }

    // `diagramSource` is the raw (HTML-decoded) fence content. `theme` carries the document
    // theme's colors for engines whose manifest opts into theming (null = render unthemed).
    // Returns well-formed <svg ...>...</svg> markup on success, or null on failure (caller falls
    // back to the plain code-block rendering — never throw for "the user's diagram syntax is
    // wrong", only for genuine plugin-infra failures).
    string? RenderToSvg(string diagramSource, PluginTheme? theme = null);

    // True when the engine's manifest declares it produces theme-matched output (render.themeInject
    // or theme placeholders in its args). Non-theme-aware engines emit artwork that assumes a light
    // page (black strokes, white/transparent background), so the host gives their diagrams a light
    // card background instead of the theme's code background — otherwise a dark theme renders
    // black-on-black (the PlantUML-arrows-invisible bug this whole mechanism exists to fix).
    bool IsThemeAware { get; }
}

// A plugin that converts non-Markdown files (reStructuredText, Org, DOCX, …) into Markdown when
// the user opens or drops one — see PluginFileReader for the shells' single entry point.
public interface IImporterPlugin : IMarksmithPlugin
{
    // Extensions claimed, lowercase, no dot (e.g. ["rst", "org", "docx"]).
    IReadOnlyList<string> ImportExtensions { get; }

    // Returns Markdown, or null on conversion failure (caller falls back to reading the raw file).
    string? ImportToMarkdown(string filePath);
}
