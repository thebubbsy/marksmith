namespace MarkSmith.Services;

/// <summary>
/// Low-key, professional product attribution stamped into the metadata of every exported file
/// (DOCX/PPTX/EPUB package properties, PDF Info dictionary). It lives in standard metadata fields
/// only — never rendered in the document body — so exports look clean while quietly carrying
/// "Marksmith by Matthew Bubb" in their file properties.
/// </summary>
public static class ExportBranding
{
    /// <summary>The attribution string written to each format's metadata.</summary>
    public const string Tag = "Marksmith by Matthew Bubb";

    /// <summary>The product name used where a short generator label fits better (e.g. PDF /Creator).</summary>
    public const string Producer = "Marksmith";

    /// <summary>"Created in …" line written to each format's Subject/provenance metadata — brand
    /// exposure in file properties wherever a document used to say "Generated from Markdown".</summary>
    public const string CreatedIn = "Created in Marksmith";

    /// <summary>
    /// Writes the attribution into the OPC extended-properties part (docProps/app.xml → Company),
    /// which the SDK's IPackageProperties does not expose. Creates the part when the document has
    /// none. Used by the DOCX and PPTX exporters; PDF and EPUB use their own metadata slots.
    /// </summary>
    public static void SetCompany(DocumentFormat.OpenXml.Packaging.OpenXmlPackage package, string? value = null)
    {
        var ext = package.GetPartsOfType<DocumentFormat.OpenXml.Packaging.ExtendedFilePropertiesPart>().FirstOrDefault()
                  ?? package.AddNewPart<DocumentFormat.OpenXml.Packaging.ExtendedFilePropertiesPart>();
        ext.Properties ??= new DocumentFormat.OpenXml.ExtendedProperties.Properties();
        ext.Properties.Company = new DocumentFormat.OpenXml.ExtendedProperties.Company(value ?? Tag);
        ext.Properties.Save();
    }
}
