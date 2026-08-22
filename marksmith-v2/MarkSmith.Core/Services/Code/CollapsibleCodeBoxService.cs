using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Code;

public record CodeFoldRegion(int StartLine, int EndLine, string RegionType, string SummaryLabel);

public class ProcessedCodeBox
{
    public string Language { get; set; } = "text";
    public string RawCode { get; set; } = string.Empty;
    public string FormattedHtml { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public List<CodeFoldRegion> FoldRegions { get; } = new();
}

/// <summary>
/// Service that enhances standard Markdown <pre><code> code blocks with interactive collapsible headers,
/// syntax-aware foldable sub-elements (imports, comments, functions), copy triggers, and quick snappy animations.
/// </summary>
public static class CollapsibleCodeBoxService
{
    private static readonly Regex PreCodeRegex = new(
        @"<pre><code(?:\s+class=""(?:language-)?([a-zA-Z0-9_\-]+)"")?>([\s\S]*?)</code></pre>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Transforms all raw <pre><code> blocks in rendered HTML into rich interactive collapsible code containers.
    /// </summary>
    public static string EnhanceCodeBlocks(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;

        if (!html.Contains("<pre", StringComparison.OrdinalIgnoreCase))
            return html;

        int boxIndex = 1;
        return PreCodeRegex.Replace(html, match =>
        {
            string lang = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "text";
            string rawCodeHtml = match.Groups[2].Value;

            // Don't wrap mermaid or math or custom plugin blocks
            if (lang.Equals("mermaid", StringComparison.OrdinalIgnoreCase) ||
                lang.Equals("math", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            var processed = ProcessCodeContent(lang, rawCodeHtml);
            return RenderCodeBoxHtml(processed, boxIndex++);
        });
    }

    /// <summary>
    /// Analyzes code lines and identifies foldable sub-regions (imports, multi-line comments, blocks).
    /// </summary>
    public static ProcessedCodeBox ProcessCodeContent(string language, string codeHtml)
    {
        var box = new ProcessedCodeBox
        {
            Language = string.IsNullOrWhiteSpace(language) ? "text" : language,
            RawCode = codeHtml
        };

        var lines = codeHtml.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        box.LineCount = lines.Length;

        // Detect foldable import headers (C#, Python, JS/TS, Java, Go, Rust)
        int importStart = -1;
        int importEnd = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (IsImportLine(line, box.Language))
            {
                if (importStart == -1) importStart = i;
                importEnd = i;
            }
            else if (!string.IsNullOrWhiteSpace(line) && importStart != -1)
            {
                break;
            }
        }

        if (importStart != -1 && importEnd > importStart)
        {
            int count = (importEnd - importStart) + 1;
            box.FoldRegions.Add(new CodeFoldRegion(importStart + 1, importEnd + 1, "imports", $"{count} imports"));
        }

        // Build line-numbered HTML with folding regions
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string lineText = lines[i];

            bool isInsideImport = importStart != -1 && i >= importStart && i <= importEnd;
            if (i == importStart && importEnd > importStart)
            {
                sb.AppendLine($"<div class=\"ms-code-subfold ms-fold-imports\" data-fold-title=\"{box.FoldRegions[0].SummaryLabel}\">");
                sb.AppendLine($"  <div class=\"ms-subfold-header\" onclick=\"toggleSubFold(this)\"><span class=\"ms-subfold-arrow\">&#9662;</span> <span class=\"ms-subfold-title\">{box.FoldRegions[0].SummaryLabel}</span></div>");
                sb.AppendLine("  <div class=\"ms-subfold-body\">");
            }

            sb.AppendLine($"<span class=\"ms-code-line\"><span class=\"ms-line-num\" data-num=\"{lineNum}\"></span><span class=\"ms-line-content\">{lineText}</span></span>");

            if (i == importEnd && importEnd > importStart)
            {
                sb.AppendLine("  </div>");
                sb.AppendLine("</div>");
            }
        }

        box.FormattedHtml = sb.ToString().TrimEnd();
        return box;
    }

    private static bool IsImportLine(string line, string lang)
    {
        string l = line.ToLowerInvariant();
        return lang switch
        {
            "csharp" or "cs" => l.StartsWith("using ") && l.EndsWith(";"),
            "python" or "py" => l.StartsWith("import ") || l.StartsWith("from "),
            "javascript" or "js" or "typescript" or "ts" => l.StartsWith("import ") || l.StartsWith("const ") && l.Contains("require("),
            "go" or "golang" => l.StartsWith("import ") || l.StartsWith("package "),
            "rust" or "rs" => l.StartsWith("use ") || l.StartsWith("extern crate "),
            "java" => l.StartsWith("import ") || l.StartsWith("package "),
            "c" or "cpp" or "c++" => l.StartsWith("#include "),
            _ => false
        };
    }

    /// <summary>
    /// Generates full HTML container for a collapsible code box with top header toolbar and animations.
    /// </summary>
    public static string RenderCodeBoxHtml(ProcessedCodeBox box, int boxId)
    {
        string displayLang = GetDisplayLanguage(box.Language);
        string langColor = GetLanguageColor(box.Language);

        return $"""
            <div class="ms-code-box" id="code-box-{boxId}" data-lang="{box.Language}">
              <div class="ms-code-header" ondblclick="toggleCodeBox('code-box-{boxId}')">
                <div class="ms-code-header-left">
                  <button class="ms-code-fold-btn" onclick="toggleCodeBox('code-box-{boxId}')" title="Collapse / Expand Code Box (Ctrl+Click to collapse all)" aria-label="Toggle collapse">
                    <span class="ms-code-chevron">&#9662;</span>
                  </button>
                  <span class="ms-code-lang-badge">
                    <span class="ms-lang-dot" style="background-color: {langColor};"></span>
                    <span class="ms-lang-name">{System.Net.WebUtility.HtmlEncode(displayLang)}</span>
                  </span>
                  <span class="ms-code-meta-badge">{box.LineCount} lines</span>
                </div>
                <div class="ms-code-header-right">
                  <button class="ms-code-btn ms-code-wrap-btn" onclick="toggleCodeWrap('code-box-{boxId}')" title="Toggle Word Wrap">Wrap</button>
                  <button class="ms-code-btn ms-code-copy-btn" onclick="copyCodeBox('code-box-{boxId}', this)" title="Copy Code to Clipboard">
                    <span class="ms-copy-icon">&#128203;</span> Copy
                  </button>
                </div>
              </div>
              <div class="ms-code-body">
                <pre><code class="language-{box.Language}">{box.FormattedHtml}</code></pre>
              </div>
              <div class="ms-code-collapsed-footer" onclick="toggleCodeBox('code-box-{boxId}')">
                <span>&#9656; {box.LineCount} lines of {System.Net.WebUtility.HtmlEncode(displayLang)} hidden &mdash; Click to expand</span>
              </div>
            </div>
            """;
    }

    private static string GetDisplayLanguage(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "csharp" or "cs" => "C#",
            "python" or "py" => "Python",
            "javascript" or "js" => "JavaScript",
            "typescript" or "ts" => "TypeScript",
            "cpp" or "c++" => "C++",
            "c" => "C",
            "go" or "golang" => "Go",
            "rust" or "rs" => "Rust",
            "sql" => "SQL",
            "json" => "JSON",
            "html" or "xml" => "HTML/XML",
            "css" or "scss" or "less" => "CSS",
            "bash" or "sh" or "shell" or "zsh" => "Bash",
            "powershell" or "ps1" => "PowerShell",
            "yaml" or "yml" => "YAML",
            "dockerfile" or "docker" => "Docker",
            "markdown" or "md" => "Markdown",
            _ => lang.Length > 0 ? char.ToUpperInvariant(lang[0]) + lang.Substring(1) : "Code"
        };
    }

    private static string GetLanguageColor(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "csharp" or "cs" => "#178600",
            "python" or "py" => "#3572A5",
            "javascript" or "js" => "#F1E05A",
            "typescript" or "ts" => "#3178C6",
            "cpp" or "c++" => "#F34B7D",
            "c" => "#555555",
            "go" or "golang" => "#00ADD8",
            "rust" or "rs" => "#DEA584",
            "sql" => "#E38C00",
            "json" => "#292929",
            "html" or "xml" => "#E34C26",
            "css" => "#563D7C",
            "bash" or "sh" or "shell" => "#89E051",
            "powershell" or "ps1" => "#012456",
            "yaml" or "yml" => "#CB171E",
            _ => "#58A6FF"
        };
    }

    /// <summary>
    /// CSS Styles for snappy collapsible code boxes with quick cubic-bezier transitions.
    /// </summary>
    public static string GetCss()
    {
        return """
            /* --- MarkSmith Interactive Collapsible Code Boxes --- */
            .ms-code-box {
                margin: 20px 0;
                border: 1px solid var(--ms-border, #30363d);
                border-radius: 8px;
                background-color: var(--ms-code-bg, #0d1117);
                overflow: hidden;
                box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);
                transition: border-color 0.16s ease, box-shadow 0.16s ease;
            }
            .ms-code-box:hover {
                border-color: var(--ms-link, #58a6ff);
                box-shadow: 0 6px 18px rgba(0, 0, 0, 0.14);
            }
            .ms-code-header {
                display: flex;
                align-items: center;
                justify-content: space-between;
                padding: 6px 12px;
                background: rgba(255, 255, 255, 0.04);
                border-bottom: 1px solid var(--ms-border, #30363d);
                font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                user-select: none;
                cursor: pointer;
            }
            .ms-code-header-left, .ms-code-header-right {
                display: flex;
                align-items: center;
                gap: 8px;
            }
            .ms-code-fold-btn {
                background: transparent;
                border: none;
                color: var(--ms-fg, #c9d1d9);
                cursor: pointer;
                padding: 2px 4px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 4px;
                transition: background 0.12s ease;
            }
            .ms-code-fold-btn:hover {
                background: rgba(255, 255, 255, 0.1);
            }
            .ms-code-chevron {
                display: inline-block;
                font-size: 13px;
                line-height: 1;
                transition: transform 0.18s cubic-bezier(0.16, 1, 0.3, 1);
            }
            .ms-code-box.collapsed .ms-code-chevron {
                transform: rotate(-90deg);
            }
            .ms-code-lang-badge {
                display: inline-flex;
                align-items: center;
                gap: 6px;
                font-size: 12px;
                font-weight: 600;
                color: var(--ms-fg, #e6edf3);
            }
            .ms-lang-dot {
                width: 8px;
                height: 8px;
                border-radius: 50%;
                display: inline-block;
            }
            .ms-code-meta-badge {
                font-size: 11px;
                color: #8b949e;
                background: rgba(255, 255, 255, 0.05);
                padding: 1px 6px;
                border-radius: 999px;
            }
            .ms-code-btn {
                background: rgba(255, 255, 255, 0.06);
                border: 1px solid var(--ms-border, #30363d);
                color: var(--ms-fg, #c9d1d9);
                border-radius: 5px;
                font-size: 11px;
                font-weight: 500;
                padding: 3px 8px;
                cursor: pointer;
                display: inline-flex;
                align-items: center;
                gap: 4px;
                transition: all 0.12s cubic-bezier(0.16, 1, 0.3, 1);
            }
            .ms-code-btn:hover {
                background: rgba(255, 255, 255, 0.12);
                border-color: var(--ms-link, #58a6ff);
                color: #ffffff;
                transform: translateY(-1px);
            }
            .ms-code-btn:active {
                transform: scale(0.96);
            }
            .ms-code-copy-btn.copied {
                background: #238636 !important;
                border-color: #2ea043 !important;
                color: #ffffff !important;
            }
            .ms-code-body {
                overflow: hidden;
                transition: max-height 0.22s cubic-bezier(0.16, 1, 0.3, 1),
                            opacity 0.18s ease-out,
                            padding 0.2s cubic-bezier(0.16, 1, 0.3, 1);
                max-height: 5000px;
                opacity: 1;
            }
            .ms-code-box.collapsed .ms-code-body {
                max-height: 0 !important;
                opacity: 0 !important;
                padding-top: 0 !important;
                padding-bottom: 0 !important;
                pointer-events: none;
            }
            .ms-code-box pre {
                margin: 0 !important;
                padding: 12px 16px !important;
                border: none !important;
                border-radius: 0 !important;
                background: transparent !important;
                font-size: 13px !important;
                line-height: 1.5 !important;
            }
            .ms-code-box.wrap-enabled pre {
                white-space: pre-wrap !important;
                word-break: break-all !important;
            }
            .ms-code-line {
                display: flex;
                min-height: 20px;
            }
            .ms-line-num {
                width: 32px;
                min-width: 32px;
                user-select: none;
                opacity: 0.35;
                text-align: right;
                padding-right: 12px;
                font-family: Consolas, monospace;
                font-size: 11px;
            }
            .ms-line-num::before {
                content: attr(data-num);
            }
            .ms-line-content {
                flex: 1;
            }
            .ms-code-collapsed-footer {
                display: none;
                padding: 6px 14px;
                font-size: 11px;
                font-weight: 500;
                color: #8b949e;
                background: rgba(0, 0, 0, 0.2);
                border-top: 1px dashed var(--ms-border, #30363d);
                cursor: pointer;
                user-select: none;
                transition: color 0.12s ease, background 0.12s ease;
            }
            .ms-code-collapsed-footer:hover {
                color: var(--ms-link, #58a6ff);
                background: rgba(88, 166, 255, 0.08);
            }
            .ms-code-box.collapsed .ms-code-collapsed-footer {
                display: block;
            }
            /* Sub-fold elements (e.g. Imports / Comments) */
            .ms-code-subfold {
                margin: 4px 0;
                border: 1px dashed rgba(255, 255, 255, 0.15);
                border-radius: 5px;
                background: rgba(255, 255, 255, 0.02);
            }
            .ms-subfold-header {
                padding: 2px 8px;
                font-size: 11px;
                color: #8b949e;
                cursor: pointer;
                user-select: none;
                display: flex;
                align-items: center;
                gap: 6px;
                background: rgba(255, 255, 255, 0.04);
                border-radius: 4px;
                transition: color 0.12s ease, background 0.12s ease;
            }
            .ms-subfold-header:hover {
                color: var(--ms-link, #58a6ff);
                background: rgba(88, 166, 255, 0.1);
            }
            .ms-subfold-arrow {
                display: inline-block;
                font-size: 11px;
                transition: transform 0.16s cubic-bezier(0.16, 1, 0.3, 1);
            }
            .ms-code-subfold.collapsed .ms-subfold-arrow {
                transform: rotate(-90deg);
            }
            .ms-subfold-body {
                overflow: hidden;
                transition: max-height 0.18s cubic-bezier(0.16, 1, 0.3, 1), opacity 0.15s ease-out;
                max-height: 800px;
                opacity: 1;
            }
            .ms-code-subfold.collapsed .ms-subfold-body {
                max-height: 0 !important;
                opacity: 0 !important;
                pointer-events: none;
            }
            @media print {
                .ms-code-box { box-shadow: none !important; break-inside: avoid; }
                .ms-code-header, .ms-code-collapsed-footer { display: none !important; }
                .ms-code-body { max-height: none !important; opacity: 1 !important; }
                .ms-subfold-body { max-height: none !important; opacity: 1 !important; }
            }
            """;
    }

    /// <summary>
    /// Client-side JavaScript for handling snappy code box folding, copy-to-clipboard, sub-folds, and word wrap.
    /// </summary>
    public static string GetJavaScript()
    {
        return """
            window.toggleCodeBox = function(id) {
                var el = document.getElementById(id);
                if (el) {
                    el.classList.toggle('collapsed');
                }
            };
            window.toggleCodeWrap = function(id) {
                var el = document.getElementById(id);
                if (el) {
                    el.classList.toggle('wrap-enabled');
                }
            };
            window.toggleSubFold = function(headerEl) {
                var fold = headerEl.closest('.ms-code-subfold');
                if (fold) {
                    fold.classList.toggle('collapsed');
                }
            };
            window.copyCodeBox = function(id, btn) {
                var el = document.getElementById(id);
                if (!el) return;
                var codeEl = el.querySelector('code');
                if (!codeEl) return;
                
                // Get clean text without line numbers
                var lines = Array.from(codeEl.querySelectorAll('.ms-line-content')).map(function(s) { return s.innerText; });
                var text = lines.length > 0 ? lines.join('\n') : codeEl.innerText;
                
                navigator.clipboard.writeText(text).then(function() {
                    var orig = btn.innerHTML;
                    btn.classList.add('copied');
                    btn.innerHTML = '<span>&#10003;</span> Copied!';
                    setTimeout(function() {
                        btn.classList.remove('copied');
                        btn.innerHTML = orig;
                    }, 1800);
                });
            };
            """;
    }
}
