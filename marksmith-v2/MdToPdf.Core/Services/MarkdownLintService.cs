namespace MdToPdf.Services;

/// <summary>
/// A lightweight Markdown "linter": flags common authoring issues (trailing whitespace, hard tabs,
/// runaway blank runs, missing image alt text, unclosed code fences, etc.) so they can be cleaned up
/// before export. Pure and allocation-light — safe to run on every keystroke for a live issue count.
/// Style rules are suspended inside fenced code blocks, where "issues" are usually intentional.
/// </summary>
public static class MarkdownLintService
{
    /// <param name="Line">1-based line number where the issue was found.</param>
    /// <param name="Message">Short human-readable description of the issue.</param>
    public readonly record struct LintIssue(int Line, string Message);

    public static List<LintIssue> Analyze(string? markdown)
    {
        var issues = new List<LintIssue>();
        if (string.IsNullOrWhiteSpace(markdown)) return issues;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? fence = null;   // the marker (``` or ~~~) that opened the current code block, or null
        int blankRun = 0;       // consecutive blank lines seen so far

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNo = i + 1;

            // Track fenced code blocks so style rules don't fire inside code.
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                continue; // inside a code block — skip all checks
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                continue;
            }

            // Trailing whitespace (only when the line has real content — a blank line is handled below).
            if (line.Length > 0 && char.IsWhiteSpace(line[^1]) && line.TrimEnd().Length > 0)
                issues.Add(new LintIssue(lineNo, "Trailing whitespace"));

            // Hard tab characters (Markdown style guides recommend spaces).
            if (line.Contains('\t'))
                issues.Add(new LintIssue(lineNo, "Hard tab character"));

            // Runs of 3+ consecutive blank lines add dead space to the rendered document.
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                if (blankRun == 3) issues.Add(new LintIssue(lineNo, "3+ consecutive blank lines"));
            }
            else
            {
                blankRun = 0;
            }

            // Images with no alt text are inaccessible and render with an empty caption.
            if (line.Contains("![](", StringComparison.Ordinal))
                issues.Add(new LintIssue(lineNo, "Image missing alt text"));

            // Extremely long lines are hard to edit and usually a paste artifact.
            if (line.Length > 500)
                issues.Add(new LintIssue(lineNo, $"Very long line ({line.Length} chars)"));

            // A heading marker with no space after the hashes isn't a heading in CommonMark.
            if (trimmed.Length > 0 && trimmed[0] == '#')
            {
                var h = 0;
                while (h < trimmed.Length && trimmed[h] == '#') h++;
                if (h <= 6 && h < trimmed.Length && trimmed[h] != ' ')
                    issues.Add(new LintIssue(lineNo, "Missing space after '#'"));
            }
        }

        // A fence that never closed means everything after it renders as code.
        if (fence is not null)
            issues.Add(new LintIssue(lines.Length, "Unclosed code fence"));

        return issues;
    }
}
