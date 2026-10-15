using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

// Live document metrics for the editor status bar. The old inline counter in MainViewModel just did
// PastedMarkdown.Split(' ') on the RAW markdown, so it counted `#`, `|`, `*`, fence backticks and
// every line of code as "words" — a heading like "## Overview" scored 2 words, and a 40-line code
// block inflated the count by ~200. This computes the number a reader actually cares about: prose
// words with Markdown syntax and fenced code stripped out, plus an estimated reading time and a
// quick structural breakdown (headings / code blocks / tables / images / links / Mermaid diagrams).
public static partial class DocumentStatsService
{
    // Average adult silent-reading speed for prose is ~238 wpm (Brysbaert 2019 meta-analysis); we
    // round to 225 to leave a little headroom for the technical/diagram-heavy documents this app
    // targets, which read slower than pure narrative.
    private const double WordsPerMinute = 225.0;

    // A GFM table delimiter row: |---|:--:|--- etc. One per table, so it's an exact table count.
    [GeneratedRegex(@"^\s{0,3}\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)+\|?\s*$")]
    private static partial Regex TableDelimiterRow();

    // ATX heading: up to three leading spaces, 1–6 #, then a space (or end of line for a bare "#").
    [GeneratedRegex(@"^\s{0,3}#{1,6}(\s|$)")]
    private static partial Regex AtxHeading();

    // ![alt](url) — captured so the alt text still contributes to the word count.
    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageRe();

    // [text](url) — reference/inline link; the visible text is kept for word counting.
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRe();

    // Inline code spans `like this` — dropped from the prose word count entirely.
    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodeRe();

    // Emphasis / strikethrough / leftover markers we peel off before splitting into words.
    [GeneratedRegex(@"[*_~>#]+")]
    private static partial Regex MarkerRe();

    public static DocumentStats Analyze(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return DocumentStats.Empty;

        int codeBlocks = 0, mermaid = 0, headings = 0, tables = 0;
        var prose = new StringBuilder(markdown.Length);

        // Walk lines via ReadOnlySpan<char> so fenced code is handled without allocating a full-document
        // normalized string copy or a string[] array on every status-bar update. Everything OUTSIDE a
        // fence is prose we scan for headings/tables and accumulate for word counting.
        int lineCount = 0;
        ReadOnlySpan<char> fenceMarker = default; // the ``` or ~~~ run that opened the current block, or empty
        int pos = 0;
        int len = markdown.Length;

        while (pos <= len)
        {
            int lineStart = pos;
            while (pos < len && markdown[pos] != '\r' && markdown[pos] != '\n') pos++;
            var line = markdown.AsSpan(lineStart, pos - lineStart);
            bool hasNewline = pos < len;
            if (hasNewline)
            {
                if (markdown[pos] == '\r' && pos + 1 < len && markdown[pos + 1] == '\n') pos += 2;
                else pos++;
            }

            // Line count for the status bar: a single trailing newline shouldn't register as an extra
            // blank line ("hello\n" is one line), but a genuine blank line still counts ("a\n\n" = 2).
            if (!hasNewline && line.Length == 0 && lineCount > 0)
                break;

            lineCount++;
            var trimmed = line.TrimStart();

            if (fenceMarker.IsEmpty)
            {
                var marker = FenceMarker(trimmed);
                if (!marker.IsEmpty)
                {
                    fenceMarker = marker;
                    codeBlocks++;
                    // Info string after the fence tells us whether this block is a Mermaid diagram.
                    var info = trimmed[marker.Length..].Trim();
                    if (info.StartsWith("mermaid", StringComparison.OrdinalIgnoreCase))
                        mermaid++;
                }
                else
                {
                    if (!trimmed.IsEmpty && trimmed[0] == '#' && AtxHeading().IsMatch(line)) headings++;
                    if (!trimmed.IsEmpty && (trimmed[0] == '|' || trimmed[0] == ':' || trimmed[0] == '-') && TableDelimiterRow().IsMatch(line)) tables++;
                    prose.Append(line).Append('\n');
                }
            }
            else if (trimmed.StartsWith(fenceMarker, StringComparison.Ordinal))
            {
                fenceMarker = default; // closing fence — resume prose on the next line
            }
            // else: inside a code block — skip entirely.

            if (!hasNewline) break;
        }

        var proseText = prose.ToString();

        // Single-pass: count images/links WHILE stripping them for word-counting, so the
        // regexes run once instead of twice (Matches here + Replace in CountWords).
        int images = 0, links = 0;
        if (proseText.Contains("![", StringComparison.Ordinal))
            proseText = ImageRe().Replace(proseText, m => { images++; return m.Groups[1].Value; });
        // After images are stripped, [text](url) only matches genuine links (no ![...] overlap).
        if (proseText.Contains("](", StringComparison.Ordinal))
            proseText = LinkRe().Replace(proseText, m => { links++; return m.Groups[1].Value; });

        int words = CountWords(proseText);
        var readingTime = TimeSpan.FromMinutes(words / WordsPerMinute);

        // Characters without whitespace — the "dense" character count some style guides prefer.
        int charsNoSpaces = 0;
        foreach (var ch in markdown) if (!char.IsWhiteSpace(ch)) charsNoSpaces++;

        return new DocumentStats
        {
            Words = words,
            Characters = markdown.Length,
            CharactersNoSpaces = charsNoSpaces,
            Lines = lineCount,
            Headings = headings,
            CodeBlocks = codeBlocks,
            Tables = tables,
            Images = images,
            Links = links,
            MermaidDiagrams = mermaid,
            ReadingTime = readingTime,
        };
    }

    // Returns the fence marker span (a run of >= 3 backticks or tildes) a line opens with, or empty.
    private static ReadOnlySpan<char> FenceMarker(ReadOnlySpan<char> trimmed)
    {
        char c = trimmed.Length > 0 ? trimmed[0] : '\0';
        if (c != '`' && c != '~') return default;
        int n = 0;
        while (n < trimmed.Length && trimmed[n] == c) n++;
        return n >= 3 ? trimmed[..n] : default;
    }

    private static int CountWords(string prose)
    {
        // Images and links have already been stripped to their visible text by the caller
        // (counting them in the same pass). Only inline code and emphasis markers remain.
        if (prose.Contains('`'))
            prose = InlineCodeRe().Replace(prose, " ");
        prose = MarkerRe().Replace(prose, " ");

        int words = 0;
        bool inToken = false;      // currently inside a run of non-whitespace
        bool tokenHasAlnum = false; // that run has produced at least one letter/digit
        foreach (var ch in prose)
        {
            if (char.IsWhiteSpace(ch))
            {
                // End of a token: count it only if it held a letter or digit, so a lone "-", "|" or
                // "•" surrounded by spaces isn't mistaken for a word.
                if (inToken && tokenHasAlnum) words++;
                inToken = false;
                tokenHasAlnum = false;
            }
            else
            {
                inToken = true;
                if (char.IsLetterOrDigit(ch)) tokenHasAlnum = true;
            }
        }
        if (inToken && tokenHasAlnum) words++; // trailing token with no closing whitespace
        return words;
    }

    /// <summary>
    /// Formats the editor status-bar cursor readout ("Ln 12, Col 8") with zero-allocation selection
    /// telemetry ("(3 words, 18 chars selected)" or "(42 words, 248 chars, 4 lines selected)") when
    /// text is selected.
    /// </summary>
    public static string FormatCursorAndSelection(int line, int col, ReadOnlySpan<char> selection)
    {
        if (selection.IsEmpty)
            return $"Ln {line}, Col {col}";

        int words = 0;
        int lines = 1;
        bool inToken = false;
        bool tokenHasAlnum = false;

        for (int i = 0; i < selection.Length; i++)
        {
            char ch = selection[i];
            if (ch == '\r')
            {
                lines++;
                if (inToken && tokenHasAlnum) words++;
                inToken = false;
                tokenHasAlnum = false;
                if (i + 1 < selection.Length && selection[i + 1] == '\n') i++;
            }
            else if (ch == '\n')
            {
                lines++;
                if (inToken && tokenHasAlnum) words++;
                inToken = false;
                tokenHasAlnum = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (inToken && tokenHasAlnum) words++;
                inToken = false;
                tokenHasAlnum = false;
            }
            else
            {
                inToken = true;
                if (char.IsLetterOrDigit(ch)) tokenHasAlnum = true;
            }
        }
        if (inToken && tokenHasAlnum) words++;

        var wordLabel = words == 1 ? "1 word" : $"{words:N0} words";
        var charLabel = selection.Length == 1 ? "1 char" : $"{selection.Length:N0} chars";
        return lines > 1
            ? $"Ln {line}, Col {col}  ({wordLabel}, {charLabel}, {lines:N0} lines selected)"
            : $"Ln {line}, Col {col}  ({wordLabel}, {charLabel} selected)";
    }
}

// Immutable snapshot of a document's metrics. Purely derived from the markdown text, so it's
// deterministic and unit-testable with no UI or file-system dependency.
public readonly record struct DocumentStats
{
    public int Words { get; init; }
    public int Characters { get; init; }
    public int CharactersNoSpaces { get; init; }
    public int Lines { get; init; }
    public int Headings { get; init; }
    public int CodeBlocks { get; init; }
    public int Tables { get; init; }
    public int Images { get; init; }
    public int Links { get; init; }
    public int MermaidDiagrams { get; init; }
    public TimeSpan ReadingTime { get; init; }

    public static DocumentStats Empty => new();

    // "< 1 min", "1 min", "7 min" — the reading-time chip shown next to the word count.
    public string ReadingTimeText
    {
        get
        {
            if (Words == 0) return "0 min";
            var minutes = ReadingTime.TotalMinutes;
            if (minutes < 1) return "< 1 min";
            return $"{(int)Math.Round(minutes)} min";
        }
    }

    // Compact one-liner for the status bar, e.g. "1,204 words · 5 min read".
    public string SummaryText => $"{Words:N0} words · {ReadingTimeText} read";

    // Richer breakdown for a tooltip/flyout — only the structural elements that are actually present.
    public string DetailText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"{Words:N0} words · {Characters:N0} characters ({CharactersNoSpaces:N0} without spaces)\n");
            sb.Append($"{Lines:N0} lines · Estimated reading time: {ReadingTimeText}");
            AppendCount(sb, "heading", Headings);
            AppendCount(sb, "code block", CodeBlocks);
            AppendCount(sb, "table", Tables);
            AppendCount(sb, "image", Images);
            AppendCount(sb, "link", Links);
            AppendCount(sb, "Mermaid diagram", MermaidDiagrams);
            return sb.ToString();
        }
    }

    private static void AppendCount(StringBuilder sb, string noun, int count)
    {
        if (count <= 0) return;
        sb.Append('\n').Append(count).Append(' ').Append(noun);
        if (count != 1) sb.Append('s');
    }
}
