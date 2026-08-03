using System;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Custom CSS Injection &amp; User Theme Stylesheet Manager (Task 27). Validates, scopes, and injects
/// custom user CSS overrides into HTML, PDF, and EPUB document rendering pipelines.
/// </summary>
public static class UserThemeStylesheetService
{
    private static readonly Regex ExpressionRe = new(@"expression\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JavascriptRe = new(@"javascript\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ScopeCss(string rawCss, string scopeClass = "markdown-body")
    {
        if (string.IsNullOrWhiteSpace(rawCss)) return "";

        // Strip known malicious CSS constructs
        var clean = ExpressionRe.Replace(rawCss, "/* stripped */");
        clean = JavascriptRe.Replace(clean, "/* stripped */");

        var targetClass = string.IsNullOrWhiteSpace(scopeClass) ? "markdown-body" : scopeClass.TrimStart('.');
        return $"/* User Custom Overrides */\n.{targetClass} {{\n{clean}\n}}";
    }

    public static string InjectIntoHtml(string html, string customCss, string scopeClass = "markdown-body")
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(customCss)) return html ?? "";

        var scoped = ScopeCss(customCss, scopeClass);
        var styleTag = $"<style id=\"user-custom-stylesheet\">\n{scoped}\n</style>\n</head>";

        if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(html, @"</head>", styleTag, RegexOptions.IgnoreCase);
        }

        return styleTag + html;
    }
}
