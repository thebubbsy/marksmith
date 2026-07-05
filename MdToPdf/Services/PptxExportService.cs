using MdToPdf.Models;

namespace MdToPdf.Services;

// PPTX export — GROUNDWORK, not yet implemented (see the roadmap in README.md). The intended build:
// split the Markdown on headings (each H1/H2 becomes a slide), lay the body out as bullet levels,
// and reuse the theme colours — generated with DocumentFormat.OpenXml's PresentationDocument, the
// same way DocxExportService builds a WordprocessingDocument from Markdig's AST. Wired into the
// export dispatch already: implement ExportAsync and it lights up everywhere PPTX is offered.
public sealed class PptxExportService
{
    public const string Extension = "pptx";

    public Task ExportAsync(string markdown, string pptxPath, AppSettings settings) =>
        throw new NotImplementedException("PPTX export is on the roadmap — not yet available.");
}
