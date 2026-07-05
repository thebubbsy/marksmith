namespace MdToPdf.Models;

// Mirrors the THEMES dict in md_to_pdf_tui.py so PDFs produced by either app look identical.
public sealed record ThemeDefinition(
    string Name,
    string Background,
    string Text,
    string Heading,
    string Code,
    string Border,
    string Primary,
    string Secondary,
    string Line);
