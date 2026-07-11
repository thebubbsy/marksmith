namespace MdToPdf.Plugins;

// A plugin that turns fenced-code-block text into an SVG diagram, the same role Mermaid plays
// natively — except Mermaid renders client-side (mermaid.min.js inside the WebView) while a
// diagram plugin renders out-of-process, synchronously, before the HTML is ever built. See
// MarkdownHtmlService.cs's plugin-fence hook for where RenderToSvg gets called.
public interface IDiagramPlugin : IMarksmithPlugin
{
    // Fenced-code-block languages this plugin claims, lowercase, e.g. ["plantuml", "puml"].
    IReadOnlyList<string> FenceLanguages { get; }

    // `diagramSource` is the raw (HTML-decoded) fence content. Returns well-formed <svg ...>...</svg>
    // markup on success, or null on failure (caller falls back to the plain code-block rendering —
    // never throw for "the user's diagram syntax is wrong", only for genuine plugin-infra failures).
    string? RenderToSvg(string diagramSource);
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
