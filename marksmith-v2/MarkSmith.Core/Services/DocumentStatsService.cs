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

        // Walk lines so fenced code is handled exactly (a regex can't reliably tell an opening fence
        // from a closing one across the whole document). Everything OUTSIDE a fence is prose we scan
        // for headings/tables and accumulate for word counting; fence bodies are excluded wholesale.
        var lines = TextNormalizer.Newlines(markdown).Split('\n');
        // Line count for the status bar: a single trailing newline shouldn't register as an extra
        // blank line ("hello\n" is one line), but a genuine blank line still counts ("a\n\n" = 2).
        int lineCount = lines.Length;
        if (lineCount > 1 && lines[^1].Length == 0) lineCount--;
        string? fenceMarker = null; // the ``` or ~~~ run that opened the current block, or null

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (fenceMarker is null)
            {
                var marker = FenceMarker(trimmed);
                if (marker is not null)
                {
                    fenceMarker = marker;
                    codeBlocks++;
                    // Info string after the fence tells us whether this block is a Mermaid diagram.
                    var info = trimmed[marker.Length..].Trim();
                    if (info.StartsWith("mermaid", StringComparison.OrdinalIgnoreCase))
                        mermaid++;
                    continue;
                }

                if (AtxHeading().IsMatch(line)) headings++;
                if (TableDelimiterRow().IsMatch(line)) tables++;
                prose.Append(line).Append('\n');
            }
            else if (trimmed.StartsWith(fenceMarker, StringComparison.Ordinal))
            {
                fenceMarker = null; // closing fence — resume prose on the next line
            }
            // else: inside a code block — skip entirely.
        }

        var proseText = prose.ToString();

        // Single-pass: count images/links WHILE stripping them for word-counting, so the
        // regexes run once instead of twice (Matches here + Replace in CountWords).
        int images = 0, links = 0;
        proseText = ImageRe().Replace(proseText, m => { images++; return m.Groups[1].Value; });
        // After images are stripped, [text](url) only matches genuine links (no ![...] overlap).
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

    // Returns the fence marker (a run of >= 3 backticks or tildes) a line opens with, or null.
    private static string? FenceMarker(string trimmed)
    {
        char c = trimmed.Length > 0 ? trimmed[0] : '\0';
        if (c != '`' && c != '~') return null;
        int n = 0;
        while (n < trimmed.Length && trimmed[n] == c) n++;
        return n >= 3 ? new string(c, n) : null;
    }

    private static int CountWords(string prose)
    {
        // Images and links have already been stripped to their visible text by the caller
        // (counting them in the same pass). Only inline code and emphasis markers remain.
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
