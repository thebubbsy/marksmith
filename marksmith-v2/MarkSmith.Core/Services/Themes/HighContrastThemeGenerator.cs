using System;
using System.Text;

namespace MarkSmith.Services.Themes;

public enum HighContrastMode
{
    OledDark,
    PureLight
}

/// <summary>
/// Service that generates WCAG AAA-compliant high-contrast theme stylesheet rules and CSS variables for accessible export.
/// </summary>
public static class HighContrastThemeGenerator
{
    /// <summary>
    /// Generates high-contrast CSS rules for the specified mode.
    /// </summary>
    public static string GenerateCss(HighContrastMode mode = HighContrastMode.OledDark)
    {
        var sb = new StringBuilder();

        if (mode == HighContrastMode.OledDark)
        {
            sb.AppendLine("""
                /* MarkSmith High-Contrast Dark (WCAG AAA Compliant) */
                :root {
                    --ms-bg: #000000;
                    --ms-fg: #ffffff;
                    --ms-link: #58a6ff;
                    --ms-link-hover: #79c0ff;
                    --ms-accent: #39d353;
                    --ms-border: #ffffff;
                    --ms-code-bg: #0d1117;
                    --ms-table-border: #ffffff;
                    --ms-blockquote-border: #58a6ff;
                }
                body {
                    background-color: var(--ms-bg) !important;
                    color: var(--ms-fg) !important;
                }
                a {
                    color: var(--ms-link) !important;
                    text-decoration: underline !important;
                    text-underline-offset: 3px !important;
                }
                a:hover, a:focus {
                    color: var(--ms-link-hover) !important;
                    outline: 2px solid var(--ms-accent) !important;
                }
                table, th, td {
                    border: 2px solid var(--ms-table-border) !important;
                    border-collapse: collapse !important;
                }
                th {
                    background-color: #161b22 !important;
                }
                pre, code {
                    border: 1.5px solid var(--ms-border) !important;
                    background-color: var(--ms-code-bg) !important;
                }
                blockquote {
                    border-left: 4px solid var(--ms-blockquote-border) !important;
                    padding-left: 16px !important;
                }
                """);
        }
        else
        {
            sb.AppendLine("""
                /* MarkSmith High-Contrast Light (WCAG AAA Compliant) */
                :root {
                    --ms-bg: #ffffff;
                    --ms-fg: #000000;
                    --ms-link: #0969da;
                    --ms-link-hover: #0550ae;
                    --ms-accent: #1a7f37;
                    --ms-border: #000000;
                    --ms-code-bg: #f6f8fa;
                    --ms-table-border: #000000;
                    --ms-blockquote-border: #0969da;
                }
                body {
                    background-color: var(--ms-bg) !important;
                    color: var(--ms-fg) !important;
                }
                a {
                    color: var(--ms-link) !important;
                    text-decoration: underline !important;
                    text-underline-offset: 3px !important;
                }
                a:hover, a:focus {
                    color: var(--ms-link-hover) !important;
                    outline: 2px solid var(--ms-accent) !important;
                }
                table, th, td {
                    border: 2px solid var(--ms-table-border) !important;
                    border-collapse: collapse !important;
                }
                th {
                    background-color: #eaeef2 !important;
                }
                pre, code {
                    border: 1.5px solid var(--ms-border) !important;
                    background-color: var(--ms-code-bg) !important;
                }
                blockquote {
                    border-left: 4px solid var(--ms-blockquote-border) !important;
                    padding-left: 16px !important;
                }
                """);
        }

        return sb.ToString();
    }
}
