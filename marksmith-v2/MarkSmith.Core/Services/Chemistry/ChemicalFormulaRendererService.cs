using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Chemistry;

/// <summary>
/// Service for parsing chemical formulas (\ce{...}) and rendering chemical notation and reaction formulas.
/// </summary>
public static class ChemicalFormulaRendererService
{
    private static readonly Regex CeTagRegex = new(@"\\ce\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex SubscriptRegex = new(@"([A-Za-z)\]])(\d+)", RegexOptions.Compiled);
    private static readonly Regex ChargeRegex = new(@"\^\{?([0-9]*[+-])\}?", RegexOptions.Compiled);

    /// <summary>
    /// Parses and converts all \ce{...} chemical equations in Markdown to formatted HTML notation.
    /// </summary>
    public static string RenderChemicalFormulas(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return CeTagRegex.Replace(markdown, match =>
        {
            string rawFormula = match.Groups[1].Value;
            string html = FormatFormula(rawFormula);
            return $"<span class=\"ms-chem-formula\">{html}</span>";
        });
    }

    /// <summary>
    /// Formats a single chemical formula string (subscripts, charges, arrows).
    /// </summary>
    public static string FormatFormula(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return formula;

        string text = formula.Trim();

        // 1. Replace reaction arrows
        text = text.Replace("<=>", " &#8644; ")
                   .Replace("->", " &#8594; ")
                   .Replace("<-", " &#8592; ");

        // 2. Charges: ^{2-} or ^+
        text = ChargeRegex.Replace(text, "<sup>$1</sup>");

        // 3. Subscripts: H2 -> H<sub>2</sub>
        text = SubscriptRegex.Replace(text, "$1<sub>$2</sub>");

        return text;
    }
}
