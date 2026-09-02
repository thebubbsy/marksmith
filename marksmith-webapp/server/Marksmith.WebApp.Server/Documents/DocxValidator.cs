using DocumentFormat.OpenXml.Validation;

namespace MarkSmith.WebApp.Server.Documents;

/// <summary>
/// Wraps the OpenXml SDK validator. After each sequenced batch the session runs a schema
/// validation pass; if the mutated OOXML no longer validates, the batch is rolled back and the
/// client receives a rejection. This is the safety net that lets the server treat Core-quality
/// document manipulation as a black box: we never hand-tune OOXML, we only ever mutate through
/// the SDK's public API and verify the result.
/// </summary>
public sealed class DocxValidator
{
    private readonly OpenXmlValidator _validator = new();

    /// <summary>Validates the whole document. Returns an empty list when valid.</summary>
    public IReadOnlyList<string> Validate(DocxDocument doc)
    {
        var errors = _validator.Validate(doc.Package);
        return errors.Select(e => e.Description ?? e.Id.ToString()).ToList();
    }

    /// <summary>Structural sanity checks that the schema validator does not cover (v1 additions).</summary>
    public static IReadOnlyList<string> StructuralChecks(DocxDocument doc)
    {
        var problems = new List<string>();
        if (!doc.DocumentBody.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Any())
            problems.Add("document body must contain at least one paragraph");
        return problems;
    }
}
