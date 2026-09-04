using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace MarkSmith.Core.Services;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public class ValidationIssue
{
    public int LineNumber { get; set; }
    public int Column { get; set; }
    public ValidationSeverity Severity { get; set; }
    public string RuleId { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Suggestion { get; set; }
    public string? Excerpt { get; set; }
}

public class ValidationReport
{
    public bool IsValid => ErrorsCount == 0;
    public int ErrorsCount => Issues.Count(i => i.Severity == ValidationSeverity.Error);
    public int WarningsCount => Issues.Count(i => i.Severity == ValidationSeverity.Warning);
    public int InfoCount => Issues.Count(i => i.Severity == ValidationSeverity.Info);
    public List<ValidationIssue> Issues { get; set; } = new();
    public int TotalLines { get; set; }
    public int TotalBlocks { get; set; }
}

public class MarkdownValidationService
{
    private static readonly HashSet<string> KnownContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "smartart", "workflow", "tabs", "chart", "columns", "timeline", "canvas", "shapes",
        "datagrid", "embed", "note", "tip", "warning", "caution", "important", "info",
        "abstract", "summary", "tldr", "question", "help", "faq", "quote", "cite",
        "example", "seealso", "hint", "success", "check", "done", "danger", "error",
        "bug", "failure", "fail", "missing", "deprecated", "toggle", "parallel"
    };

    private static readonly Regex ContainerOpener = new(@"^\s*:::+\s*([A-Za-z0-9_-]+)", RegexOptions.Compiled);
    private static readonly Regex ContainerCloser = new(@"^\s*:::+\s*$", RegexOptions.Compiled);
    private static readonly Regex ScriptTagRegex = new(@"<script\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PandocTableBorder = new(@"^\s*\+[=+-]+\+\s*$", RegexOptions.Compiled);

    public ValidationReport Validate(string markdown)
    {
        var report = new ValidationReport();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return report;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        report.TotalLines = lines.Length;

        ValidateContainersAndFences(lines, report);
        ValidateMathEquations(lines, report);
        ValidateTables(lines, report);
        ValidateSecurityAndHtml(lines, report);

        // Parse with Markdig to validate general syntax
        try
        {
            var pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseMathematics()
                .Build();
            var doc = Markdown.Parse(markdown, pipeline);
            report.TotalBlocks = doc.Count;
        }
        catch (Exception ex)
        {
            report.Issues.Add(new ValidationIssue
            {
                LineNumber = 1,
                Column = 1,
                Severity = ValidationSeverity.Error,
                RuleId = "AST_PARSE_EXCEPTION",
                Message = $"Markdown AST parser encountered fatal syntax error: {ex.Message}",
                Suggestion = "Verify document structure and unmatched formatting delimiters."
            });
        }

        return report;
    }

    private static void ValidateContainersAndFences(string[] lines, ValidationReport report)
    {
        var containerStack = new Stack<(string Kind, int LineNumber, string RawLine)>();
        bool inCodeFence = false;
        string? currentFence = null;
        int codeFenceStart = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.TrimStart();

            // Check code fence toggle
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                string fenceMarker = trimmed.Substring(0, 3);
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    currentFence = fenceMarker;
                    codeFenceStart = lineNum;
                }
                else if (currentFence != null && trimmed.StartsWith(currentFence, StringComparison.Ordinal))
                {
                    inCodeFence = false;
                    currentFence = null;
                }
                continue;
            }

            if (inCodeFence)
            {
                continue; // Ignore inside code fences
            }

            // Check for Container Opener
            var m = ContainerOpener.Match(line);
            if (m.Success)
            {
                string kind = m.Groups[1].Value;
                if (!KnownContainers.Contains(kind))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        LineNumber = lineNum,
                        Column = line.IndexOf(":::") + 1,
                        Severity = ValidationSeverity.Warning,
                        RuleId = "UNKNOWN_CONTAINER_TYPE",
                        Message = $"Unknown container type ':::{kind}'.",
                        Excerpt = line,
                        Suggestion = $"Verify if ':::{kind}' is intended. Supported containers: {string.Join(", ", KnownContainers.Take(10))}..."
                    });
                }

                containerStack.Push((kind, lineNum, line));
                continue;
            }

            // Check for Container Closer
            if (ContainerCloser.IsMatch(line))
            {
                if (containerStack.Count > 0)
                {
                    containerStack.Pop();
                }
                else
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        LineNumber = lineNum,
                        Column = line.IndexOf(":::") + 1,
                        Severity = ValidationSeverity.Error,
                        RuleId = "UNMATCHED_CONTAINER_CLOSER",
                        Message = "Found closing ':::' with no matching opening container.",
                        Excerpt = line,
                        Suggestion = "Remove orphan ':::' or add matching opening ':::<type>'."
                    });
                }
            }
        }

        // Unclosed code fence
        if (inCodeFence)
        {
            report.Issues.Add(new ValidationIssue
            {
                LineNumber = codeFenceStart,
                Column = 1,
                Severity = ValidationSeverity.Error,
                RuleId = "UNCLOSED_CODE_FENCE",
                Message = $"Code fence opened at line {codeFenceStart} is not closed before end of document.",
                Suggestion = $"Add closing '{currentFence}' on a new line."
            });
        }

        // Unclosed containers
        while (containerStack.Count > 0)
        {
            var unclosed = containerStack.Pop();
            report.Issues.Add(new ValidationIssue
            {
                LineNumber = unclosed.LineNumber,
                Column = 1,
                Severity = ValidationSeverity.Error,
                RuleId = "UNCLOSED_CONTAINER",
                Message = $"Container ':::{unclosed.Kind}' opened at line {unclosed.LineNumber} is never closed.",
                Excerpt = unclosed.RawLine,
                Suggestion = "Add a closing ':::' on a separate line after container content."
            });
        }
    }

    private static void ValidateMathEquations(string[] lines, ValidationReport report)
    {
        bool inDisplayMath = false;
        int displayMathStart = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.Trim();

            // Display math $$ toggle
            if (trimmed == "$$")
            {
                if (!inDisplayMath)
                {
                    inDisplayMath = true;
                    displayMathStart = lineNum;
                }
                else
                {
                    inDisplayMath = false;
                }
                continue;
            }

            if (!inDisplayMath)
            {
                // Check LaTeX inline delimiters \( and \) or $ ... $
                int dollarCount = 0;
                bool escaped = false;
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] == '\\') { escaped = !escaped; continue; }
                    if (line[c] == '$' && !escaped) dollarCount++;
                    escaped = false;
                }

                // Odd number of non-escaped $ in a single line (unless part of $$...$$)
                if (dollarCount % 2 != 0 && !line.Contains("$$"))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        LineNumber = lineNum,
                        Column = line.IndexOf('$') + 1,
                        Severity = ValidationSeverity.Warning,
                        RuleId = "UNPAIRED_DOLLAR_DELIMITER",
                        Message = $"Found odd number ({dollarCount}) of unescaped '$' math delimiters on line {lineNum}.",
                        Excerpt = line,
                        Suggestion = "Ensure inline math equations are properly closed with '$...$' or escape currency amounts as '\\$'."
                    });
                }
            }

            // Check LaTeX brace balancing inside math lines
            if (inDisplayMath || (line.Contains('$') && !line.StartsWith("```")))
            {
                int openBraces = 0;
                int closeBraces = 0;
                for (int c = 0; c < line.Length; c++)
                {
                    if (c > 0 && line[c - 1] == '\\') continue;
                    if (line[c] == '{') openBraces++;
                    if (line[c] == '}') closeBraces++;
                }

                if (openBraces != closeBraces)
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        LineNumber = lineNum,
                        Column = 1,
                        Severity = ValidationSeverity.Warning,
                        RuleId = "UNBALANCED_LATEX_BRACES",
                        Message = $"Unbalanced curly braces in math/LaTeX expressions on line {lineNum} ({openBraces} opening, {closeBraces} closing).",
                        Excerpt = line,
                        Suggestion = "Balance LaTeX grouping braces: e.g. \\frac{a}{b}."
                    });
                }
            }
        }

        if (inDisplayMath)
        {
            report.Issues.Add(new ValidationIssue
            {
                LineNumber = displayMathStart,
                Column = 1,
                Severity = ValidationSeverity.Error,
                RuleId = "UNCLOSED_DISPLAY_MATH",
                Message = $"Display math block opened at line {displayMathStart} is not closed before end of document.",
                Suggestion = "Add closing '$$' on a new line."
            });
        }
    }

    private static void ValidateTables(string[] lines, ValidationReport report)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            // Detect Pandoc table border leaks
            if (PandocTableBorder.IsMatch(line))
            {
                report.Issues.Add(new ValidationIssue
                {
                    LineNumber = lineNum,
                    Column = 1,
                    Severity = ValidationSeverity.Warning,
                    RuleId = "PANDOC_BORDER_LEAK",
                    Message = "Pandoc structural table border line detected. This may leak raw characters into document prose.",
                    Excerpt = line,
                    Suggestion = "Remove +---+ lines or use standard GFM table format |---|---|."
                });
            }

            // Detect table separator mismatch
            if (line.TrimStart().StartsWith('|') && line.Contains("---"))
            {
                var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts.All(p => p.Trim().All(c => c == '-' || c == ':')))
                {
                    // Check header line right above
                    if (i > 0 && lines[i - 1].TrimStart().StartsWith('|'))
                    {
                        var headerCols = lines[i - 1].Split('|', StringSplitOptions.RemoveEmptyEntries).Length;
                        var sepCols = parts.Length;
                        if (headerCols != sepCols)
                        {
                            report.Issues.Add(new ValidationIssue
                            {
                                LineNumber = lineNum,
                                Column = 1,
                                Severity = ValidationSeverity.Warning,
                                RuleId = "TABLE_COLUMN_MISMATCH",
                                Message = $"Table header has {headerCols} columns but separator line has {sepCols} columns.",
                                Excerpt = line,
                                Suggestion = $"Adjust table separator to have exactly {headerCols} columns."
                            });
                        }
                    }
                }
            }
        }
    }

    private static void ValidateSecurityAndHtml(string[] lines, ValidationReport report)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            if (ScriptTagRegex.IsMatch(line))
            {
                report.Issues.Add(new ValidationIssue
                {
                    LineNumber = lineNum,
                    Column = line.IndexOf("<script", StringComparison.OrdinalIgnoreCase) + 1,
                    Severity = ValidationSeverity.Error,
                    RuleId = "PROHIBITED_SCRIPT_TAG",
                    Message = "Active HTML script tags are prohibited by MarkSmith security and XSS governance rules.",
                    Excerpt = line,
                    Suggestion = "Remove <script> tags or place within fenced code blocks."
                });
            }
        }
    }
}
