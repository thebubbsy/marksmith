using MdToPdf.Models;

namespace MdToPdf.Services;

// EPUB export — GROUNDWORK, not yet implemented (see the roadmap in README.md). The intended build:
// wrap the already-themed HTML from MarkdownHtmlService into an EPUB container (mimetype +
// META-INF/container.xml + an OPF manifest + one XHTML chapter per top-level heading), zipped with
// System.IO.Compression. Wired into the export dispatch already: implement ExportAsync and it lights
// up everywhere EPUB is offered.
public sealed class EpubExportService
{
    public const string Extension = "epub";

    public Task ExportAsync(string markdown, string epubPath, AppSettings settings) =>
        throw new NotImplementedException("EPUB export is on the roadmap — not yet available.");
}
