using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.CodePlayground;

public record CodeSnippetBlock(
    int BlockIndex,
    string Language,
    string Code,
    string? Title = null,
    bool IsExecutable = true,
    int LineNumber = 1);

/// <summary>
/// Service for detecting, extracting, and configuring interactive runnable code playgrounds in Markdown.
/// </summary>
public static class MarkdownCodeReplService
{
    private static readonly HashSet<string> ExecutableLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "cs", "python", "py", "javascript", "js", "typescript", "ts", "sql", "powershell", "ps1", "bash", "sh"
    };

    private static readonly Regex CodeBlockRegex = new(
        @"```([a-zA-Z0-9_\-]+)?(?:\s+title=""([^""]+)"")?\r?\n([\s\S]*?)```",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans Markdown content and extracts all declared code snippets.
    /// </summary>
    public static List<CodeSnippetBlock> ExtractSnippets(string markdown)
    {
        var list = new List<CodeSnippetBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
            return list;

        int index = 1;
        foreach (Match match in CodeBlockRegex.Matches(markdown))
        {
            string lang = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "text";
            string? title = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            string code = match.Groups[3].Value;

            bool isExec = ExecutableLanguages.Contains(lang);

            // Compute line number
            int lineNum = 1 + (match.Index > 0 ? markdown.Substring(0, match.Index).Split('\n').Length - 1 : 0);

            list.Add(new CodeSnippetBlock(index++, lang, code, title, isExec, lineNum));
        }

        return list;
    }

    /// <summary>
    /// Wraps a code snippet in an interactive REPL container for live HTML preview.
    /// </summary>
    public static string RenderReplContainer(CodeSnippetBlock snippet)
    {
        string titleHtml = !string.IsNullOrEmpty(snippet.Title)
            ? $"<div class=\"repl-header-title\">{System.Net.WebUtility.HtmlEncode(snippet.Title)} ({System.Net.WebUtility.HtmlEncode(snippet.Language)})</div>"
            : $"<div class=\"repl-header-title\">{System.Net.WebUtility.HtmlEncode(snippet.Language)} Playground</div>";

        string runBtn = snippet.IsExecutable
            ? "<button class=\"repl-run-btn\" onclick=\"runCodeBlock(this)\">&#9654; Run</button>"
            : "";

        return $"""
            <div class="ms-code-repl" data-lang="{snippet.Language}">
                <div class="repl-toolbar">
                    {titleHtml}
                    {runBtn}
                </div>
                <pre><code class="language-{snippet.Language}">{System.Net.WebUtility.HtmlEncode(snippet.Code)}</code></pre>
                <div class="repl-output" style="display:none;"><pre class="repl-output-content"></pre></div>
            </div>
            """;
    }
}
