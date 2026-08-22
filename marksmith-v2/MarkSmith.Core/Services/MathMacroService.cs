using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// MathJax &amp; KaTeX Math Macro Preprocessor Engine (Task 24). Pre-expands custom LaTeX macros
/// (\newcommand{\name}{expansion} or \def\name{expansion}) within Markdown math blocks ($...$ and $$...$$).
/// </summary>
public static class MathMacroService
{
    private static readonly Regex NewCommandSimpleRe = new(
        @"\\newcommand\s*\{\\([a-zA-Z]+)\}\s*\{((?:[^{}]|\{[^{}]*\})*)\}",
        RegexOptions.Compiled);

    private static readonly Regex DefSimpleRe = new(
        @"\\def\s*\\([a-zA-Z]+)\s*\{((?:[^{}]|\{[^{}]*\})*)\}",
        RegexOptions.Compiled);

    private static readonly Regex NewCommandArgsRe = new(
        @"\\newcommand\s*\{\\([a-zA-Z]+)\}\s*\[(\d+)\]\s*\{((?:[^{}]|\{[^{}]*\})*)\}",
        RegexOptions.Compiled);

    // Compiled once — the math-region scan used to rebuild this regex on every Apply call.
    private static readonly Regex MathBlockRe = new(
        @"(\$\$[\s\S]*?\$\$|\$[^$\n]+\$)",
        RegexOptions.Compiled);

    // Macro-call / argument patterns are keyed by macro name; a document defines few macros but
    // Apply re-runs per preview render, so cache the compiled regexes instead of rebuilding them
    // once per formula × per macro.
    private static readonly ConcurrentDictionary<string, Regex> MacroCallCache = new();
    private static readonly ConcurrentDictionary<(string Name, int ArgCount), Regex> ArgMacroCache = new();

    public static string Apply(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown ?? "";

        var simpleMacros = new Dictionary<string, string>();
        var argMacros = new Dictionary<string, (int argCount, string body)>();

        // 1. Extract \newcommand{\name}[N]{body}
        markdown = NewCommandArgsRe.Replace(markdown, m =>
        {
            var name = m.Groups[1].Value;
            if (int.TryParse(m.Groups[2].Value, out var count))
            {
                argMacros[name] = (count, m.Groups[3].Value);
            }
            return "";
        });

        // 2. Extract \newcommand{\name}{body}
        markdown = NewCommandSimpleRe.Replace(markdown, m =>
        {
            var name = m.Groups[1].Value;
            simpleMacros[name] = m.Groups[2].Value;
            return "";
        });

        // 3. Extract \def\name{body}
        markdown = DefSimpleRe.Replace(markdown, m =>
        {
            var name = m.Groups[1].Value;
            simpleMacros[name] = m.Groups[2].Value;
            return "";
        });

        if (simpleMacros.Count == 0 && argMacros.Count == 0) return markdown;

        // 4. Substitute macro calls inside math regions ($...$ or $$...$$)
        var sb = new StringBuilder();
        var pos = 0;

        foreach (Match m in MathBlockRe.Matches(markdown))
        {
            sb.Append(markdown.Substring(pos, m.Index - pos));
            var formula = m.Value;

            // Expand simple macros first
            foreach (var (name, body) in simpleMacros)
            {
                // Avoid replacing longer macro names starting with the same prefix
                var macroCallRe = MacroCallCache.GetOrAdd(name, n => new Regex(@"\\" + n + @"(?![a-zA-Z])", RegexOptions.Compiled));
                formula = macroCallRe.Replace(formula, body);
            }

            // Expand parametrized macros
            foreach (var (name, (argCount, body)) in argMacros)
            {
                formula = ExpandArgMacro(formula, name, argCount, body);
            }

            sb.Append(formula);
            pos = m.Index + m.Length;
        }

        sb.Append(markdown[pos..]);
        return sb.ToString();
    }

    private static string ExpandArgMacro(string formula, string macroName, int argCount, string body)
    {
        var re = ArgMacroCache.GetOrAdd((macroName, argCount), key =>
        {
            var pattern = @"\\" + key.Name;
            for (int i = 0; i < key.ArgCount; i++)
            {
                pattern += @"\s*\{([^{}]*)\}";
            }
            return new Regex(pattern, RegexOptions.Compiled);
        });

        return re.Replace(formula, m =>
        {
            var result = body;
            for (int i = 1; i <= argCount; i++)
            {
                var argVal = m.Groups[i].Value;
                result = result.Replace("#" + i, argVal);
            }
            return result;
        });
    }
}
