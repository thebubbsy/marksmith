using System;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Specifications;

/// <summary>
/// Service that scans technical specifications for IETF RFC 2119 requirement keywords and formats high-visibility compliance badges.
/// </summary>
public static class RfcRequirementBadgeService
{
    private static readonly Regex RfcKeywordRegex = new(
        @"\b(MUST\s+NOT|SHALL\s+NOT|SHOULD\s+NOT|MUST|REQUIRED|SHALL|SHOULD|RECOMMENDED|NOT\s+RECOMMENDED|MAY|OPTIONAL)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Replaces all RFC 2119 keywords with accessible styled HTML badge spans.
    /// </summary>
    public static string HighlightRfcKeywords(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return RfcKeywordRegex.Replace(markdown, match =>
        {
            string kw = match.Value;
            string upper = Regex.Replace(kw.ToUpperInvariant(), @"\s+", " ");
            string cssClass = GetBadgeClass(upper);

            return $"<span class=\"ms-rfc-badge {cssClass}\" title=\"IETF RFC 2119 Requirement: {System.Net.WebUtility.HtmlEncode(upper)}\">{System.Net.WebUtility.HtmlEncode(upper)}</span>";
        });
    }

    private static string GetBadgeClass(string keyword)
    {
        return keyword switch
        {
            "MUST" or "REQUIRED" or "SHALL" => "ms-rfc-must",
            "MUST NOT" or "SHALL NOT" => "ms-rfc-must-not",
            "SHOULD" or "RECOMMENDED" => "ms-rfc-should",
            "SHOULD NOT" or "NOT RECOMMENDED" => "ms-rfc-should-not",
            "MAY" or "OPTIONAL" => "ms-rfc-may",
            _ => "ms-rfc-default"
        };
    }
}
