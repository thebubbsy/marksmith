using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Tier 1 of the dual-mode DOCX engine: lossless round-trip by construction. On export Marksmith
// tucks the ORIGINAL Markdown source into a private custom-XML part inside the .docx. On re-import
// the reverse engine finds that part first and returns the exact source — byte-for-byte, no
// reconstruction guesswork. The part is invisible to Word users (it is not referenced by the
// document body) and travels with the file, so a Marksmith-made document is always perfectly
// reversible on any machine.
//
// The embedded copy is authoritative only until the user edits the document in Word. Read() compares
// the package's Modified timestamp against the export time and flags IsStale when Word has touched
// it since — the caller can then surface a warning that the recovered source may lag the visible
// content (and optionally fall through to the Universal Engine for the current state).
public static class MarksmithSourceStore
{
    // Bumped only on a breaking change to the embedded payload's shape. Read() ignores parts whose
    // schema marker it does not recognize, so a future v2 can coexist with v1 readers.
    public const string Schema = "marksmith-source-v1";

    // The recovered source plus the metadata needed to judge its freshness and provenance.
    public sealed record EmbeddedSource(
        string Markdown,
        DateTime ExportedUtc,
        string Version,
        string Theme,
        string MermaidMode,
        bool IsStale,
        DateTime? ModifiedUtc);

    private static string AssemblyVersion =>
        typeof(MarksmithSourceStore).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Embeds the original Markdown source into a private custom-XML part on the main document
    /// part. Mirrors the RenderReferences custom-part pattern in DocxExportService. Idempotent: any
    /// pre-existing marksmith-source part is removed first so an append/re-export never duplicates.
    /// </summary>
    public static void Embed(MainDocumentPart main, string originalMarkdown, AppSettings settings)
    {
        // Drop any prior source part (re-export / append) so we never carry two.
        foreach (var existing in main.CustomXmlParts
                     .Where(p => IsSourcePart(p)).ToList())
        {
            main.DeletePart(existing);
        }

        var exportedUtc = DateTime.UtcNow;
        // Align the package Modified stamp with the embed time so a fresh export is never judged
        // stale by sub-second rounding between the two timestamps (OPC stores Modified at second
        // precision). A genuine later edit in Word pushes Modified well past this.
        main.OpenXmlPackage.PackageProperties.Modified = exportedUtc;
        var root = new XElement("marksmithSource",
            new XAttribute("schema", Schema),
            new XAttribute("version", AssemblyVersion),
            new XAttribute("exportedUtc", exportedUtc.ToString("O")),
            new XAttribute("theme", settings.Theme ?? ""),
            new XAttribute("mermaidMode", settings.MermaidDocxMode.ToString()),
            new XElement("markdown", new XCData(originalMarkdown ?? "")));

        var part = main.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(root.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>
    /// Scans the document's custom-XML parts for the marksmith-source marker and, if found, returns
    /// the recovered source with a computed staleness flag. Returns null when the document was not
    /// produced by Marksmith (or predates source embedding) — the caller then uses the Universal
    /// Engine.
    /// </summary>
    public static EmbeddedSource? Read(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart;
        if (main is null) return null;

        foreach (var part in main.CustomXmlParts)
        {
            XElement? root;
            try
            {
                using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
                root = XDocument.Load(stream).Root;
            }
            catch
            {
                continue; // a corrupt/unrelated custom part must never break reimport
            }

            if (root is null) continue;
            if (root.Name.LocalName != "marksmithSource") continue;
            if (root.Attribute("schema")?.Value != Schema) continue;

            var markdown = root.Element("markdown")?.Value ?? "";
            var exportedUtc = ParseUtc(root.Attribute("exportedUtc")?.Value);
            // PackageProperties.Modified comes back Kind=Local; normalize to UTC so the comparison
            // below is instant-accurate regardless of the machine's timezone (raw DateTime compares
            // ignore Kind, which would otherwise skew the result by the UTC offset).
            var modifiedUtc = doc.PackageProperties.Modified?.ToUniversalTime();

            // Stale when Word (or any editor) saved the file after we exported it. A 2-second
            // tolerance absorbs OPC's second-precision rounding of the Modified stamp so a fresh
            // export is never flagged; a real edit lands well outside that window. A missing
            // Modified stamp is treated as "not stale" — we have no evidence of an edit.
            var isStale = exportedUtc is not null && modifiedUtc is not null &&
                          modifiedUtc > exportedUtc.Value.AddSeconds(2);

            return new EmbeddedSource(
                Markdown: markdown,
                ExportedUtc: exportedUtc ?? DateTime.MinValue,
                Version: root.Attribute("version")?.Value ?? "",
                Theme: root.Attribute("theme")?.Value ?? "",
                MermaidMode: root.Attribute("mermaidMode")?.Value ?? "",
                IsStale: isStale,
                ModifiedUtc: modifiedUtc);
        }

        return null;
    }

    private static bool IsSourcePart(CustomXmlPart part)
    {
        try
        {
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            var root = XDocument.Load(stream).Root;
            return root?.Name.LocalName == "marksmithSource" &&
                   root.Attribute("schema")?.Value == Schema;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime()
            : null;
}
