using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Detects which AI assistant a Markdown blob came from (each web UI has formatting tells) and
// normalizes those quirks so the export renders cleanly:
//   ChatGPT — \( \) / \[ \] LaTeX delimiters, 【12†source】 citation pips, :contentReference
//             artifacts, "Copy code" button text captured by select-all copies.
//   Gemini  — bold-line pseudo-headings instead of #-headings, "Gemini can make mistakes"
//             footer, trailing "Sources" blocks, * bullets with inconsistent indents.
//   Claude  — <thinking>/<*> tag remnants, "Would you like me to..." trailing offers.
public sealed partial class LlmSourceService
{
    [GeneratedRegex(@"【\d+†[^】]*】")] private static partial Regex ChatGptCitations();
    [GeneratedRegex(@":contentReference\[oaicite:\d+\]\{index=\d+\}")] private static partial Regex OaiCiteArtifacts();
    [GeneratedRegex(@"\\\[(.+?)\\\]", RegexOptions.Singleline)] private static partial Regex LatexBlock();
    [GeneratedRegex(@"\\\((.+?)\\\)", RegexOptions.Singleline)] private static partial Regex LatexInline();
    [GeneratedRegex(@"^\*\*([^*\n]{3,80})\*\*:?\s*$", RegexOptions.Multiline)] private static partial Regex BoldPseudoHeading();
    [GeneratedRegex(@"^(Copy code|Copy)\s*$", RegexOptions.Multiline)] private static partial Regex CopyCodeButtons();
    [GeneratedRegex(@"^(ChatGPT can make mistakes\..*|Gemini can make mistakes.*|Claude can make mistakes.*)$", RegexOptions.Multiline)] private static partial Regex DisclaimerFooters();
    [GeneratedRegex(@"</?(thinking|artifact|search_reminders|automated_reminder_from_anthropic)[^>]*>")] private static partial Regex ClaudeTagRemnants();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ExcessBlankLines();
    [GeneratedRegex(@"\$\$?.+?\$\$?", RegexOptions.Singleline)] private static partial Regex DollarMath();
    // Seed commands for undelimited-math recovery: each is a self-contained structural construct
    // (fraction, root, binomial, box) that is ALWAYS real math — a bare one glued into text is never
    // prose, unlike a lone \times or \approx which might be. The unit's brace arguments are consumed
    // by a balance-aware scanner (WrapBareLatexMath), not a regex, so nesting like \frac{\sqrt x}{y}
    // is handled correctly.
    [GeneratedRegex(@"\\(frac|dfrac|tfrac|boxed|sqrt|binom|dbinom|tbinom)(?![a-zA-Z])")] private static partial Regex BareLatexSeed();
    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)] private static partial Regex FencedCode();
    [GeneratedRegex(@"`[^`\n]+`")] private static partial Regex InlineCode();

    private static readonly Dictionary<string, int> SeedArgCount = new()
    {
        ["frac"] = 2, ["dfrac"] = 2, ["tfrac"] = 2, ["binom"] = 2, ["dbinom"] = 2, ["tbinom"] = 2,
        ["boxed"] = 1, ["sqrt"] = 1,
    };

    // Maps the extension's canonical hostname-derived source id to the enum. Ground truth — the
    // extension knows for certain which site a reply came from — so callers use this to REPLACE the
    // content-pattern guess in Classify() rather than merge with it. Returns null for anything
    // unrecognized (falls back to the heuristic).
    public static LlmSource? ParseSourceId(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "chatgpt" or "openai" => LlmSource.ChatGpt,
        "gemini" or "bard" => LlmSource.Gemini,
        "claude" or "anthropic" => LlmSource.Claude,
        "copilot" => LlmSource.Copilot,
        _ => null,
    };

    public LlmClassification Classify(string markdown)
    {
        markdown = TextNormalizer.Newlines(markdown);
        var signals = new List<string>();
        int chatgpt = 0, gemini = 0, claude = 0;

        if (ChatGptCitations().IsMatch(markdown)) { chatgpt += 40; signals.Add("【n†source】 citation pips"); }
        if (OaiCiteArtifacts().IsMatch(markdown)) { chatgpt += 40; signals.Add(":contentReference artifacts"); }
        if (LatexInline().IsMatch(markdown) || LatexBlock().IsMatch(markdown)) { chatgpt += 20; signals.Add(@"\(..\) LaTeX delimiters"); }
        if (markdown.Contains("ChatGPT can make mistakes")) { chatgpt += 50; signals.Add("ChatGPT footer"); }
        if (CopyCodeButtons().IsMatch(markdown)) { chatgpt += 15; gemini += 10; signals.Add("Copy-code button text"); }

        if (markdown.Contains("Gemini can make mistakes")) { gemini += 50; signals.Add("Gemini footer"); }
        if (BoldPseudoHeading().Matches(markdown).Count >= 2 && !markdown.Contains("\n# ") && !markdown.Contains("\n## "))
        { gemini += 25; signals.Add("bold-line pseudo-headings"); }
        if (Regex.IsMatch(markdown, @"^\s*Sources\s*$", RegexOptions.Multiline) &&
            Regex.IsMatch(markdown, @"^\d+\.\s+\S+\.(com|org|net|io|dev)", RegexOptions.Multiline))
        { gemini += 25; signals.Add("trailing Sources block"); }

        if (ClaudeTagRemnants().IsMatch(markdown)) { claude += 50; signals.Add("Claude tag remnants"); }
        if (markdown.Contains("Claude can make mistakes")) { claude += 50; signals.Add("Claude footer"); }
        if (Regex.IsMatch(markdown, @"^Would you like me to", RegexOptions.Multiline)) { claude += 15; signals.Add("trailing offer"); }

        var (source, score) = (chatgpt, gemini, claude) switch
        {
            var (c, g, l) when c >= g && c >= l && c > 0 => (LlmSource.ChatGpt, c),
            var (_, g, l) when g >= l && g > 0 => (LlmSource.Gemini, g),
            var (_, _, l) when l > 0 => (LlmSource.Claude, l),
            _ => (LlmSource.Generic, 0),
        };

        return new LlmClassification
        {
            Source = source,
            Confidence = Math.Min(100, score),
            Signals = signals,
            HasMath = LatexInline().IsMatch(markdown) || LatexBlock().IsMatch(markdown) || DollarMath().IsMatch(markdown),
        };
    }

    public (string Cleaned, List<string> Fixes) Normalize(string markdown, LlmClassification classification)
    {
        var fixes = new List<string>();
        var text = markdown;

        void Apply(Regex rx, string replacement, string description)
        {
            var count = rx.Matches(text).Count;
            if (count == 0) return;
            text = rx.Replace(text, replacement);
            fixes.Add($"{description} ({count})");
        }

        Apply(OaiCiteArtifacts(), "", "Removed ChatGPT citation artifacts");
        Apply(ChatGptCitations(), "", "Removed 【n†source】 pips");
        Apply(CopyCodeButtons(), "", "Removed copy-button text");
        Apply(DisclaimerFooters(), "", "Removed assistant disclaimer footer");
        Apply(ClaudeTagRemnants(), "", "Removed internal tag remnants");

        // ChatGPT emits \( \) / \[ \]; Markdig's math extension wants $ / $$.
        Apply(LatexBlock(), "\n$$$$\n$1\n$$$$\n", "Converted LaTeX display math to $$");
        Apply(LatexInline(), "$$$1$$", "Converted LaTeX inline math to $");

        text = WrapBareLatexMath(text, fixes);

        // Gemini loves "**Heading:**" lines instead of real headings — promote them so the
        // document gets a navigable structure (and the TOC has something to index).
        Apply(BoldPseudoHeading(), "### $1", "Promoted bold pseudo-headings");

        Apply(ExcessBlankLines(), "\n\n", "Collapsed excess blank lines");

        classification.AppliedFixes = fixes;
        classification.HasMath = classification.HasMath || DollarMath().IsMatch(text);
        return (text.Trim(), fixes);
    }

    // Recovers real math that arrives with NO delimiters at all — a common artifact of copying
    // *rendered* math out of a browser-based AI chat UI (as opposed to a clean "Copy as Markdown"
    // export): the browser's copy handler for KaTeX/MathJax output frequently captures the raw TeX
    // source (meant to stay hidden, accessibility-only) as plain text sitting right next to the
    // rendered glyphs, with no $ / \( \) / \[ \] wrapper at all. Markdig's math extension only
    // recognizes $-delimited spans, so a bare \frac{a}{b} or \boxed{x} renders as literal escaped
    // text instead of a real stacked fraction / border box — on BOTH the PDF (KaTeX) and DOCX
    // (LatexToOmml) paths, since neither even attempts to parse it as math without the delimiter.
    //
    // Scoped to the self-contained STRUCTURAL commands in SeedArgCount (\frac, \sqrt, \binom, \boxed
    // and their d-/t- variants), NOT a blanket "wrap anything backslash-like" rule. Each of these is
    // always genuine math when it appears — a bare \frac{...}{...} is never prose — whereas a lone
    // \times or \approx could be text discussing LaTeX, so those are left alone (they render fine
    // once they sit inside a $-span that a seed pulls them into). The command's brace arguments are
    // consumed by a brace-balance scanner so nested cases like \frac{\sqrt{x}}{y} wrap as one unit;
    // seeds landing inside an already-wrapped unit, or inside existing $-math / inline / fenced code,
    // are skipped so nothing double-wraps or mangles a code sample showing \frac as literal text.
    private static string WrapBareLatexMath(string text, List<string> fixes)
    {
        var protectedRanges = new List<(int Start, int End)>();
        void Collect(Regex rx) { foreach (Match m in rx.Matches(text)) protectedRanges.Add((m.Index, m.Index + m.Length)); }
        Collect(FencedCode());
        Collect(InlineCode());
        Collect(DollarMath());
        bool IsProtected(int index) => protectedRanges.Any(r => index >= r.Start && index < r.End);

        // Collect the [start,end) span of each seed's full \cmd{..}{..} unit, left to right,
        // skipping any seed that starts inside a unit already claimed (handles nesting).
        var spans = new List<(int Start, int End)>();
        int claimedTo = 0;
        foreach (Match m in BareLatexSeed().Matches(text))
        {
            if (m.Index < claimedTo || IsProtected(m.Index)) continue;
            var name = m.Groups[1].Value;
            var end = ConsumeLatexUnit(text, m.Index, name, SeedArgCount[name]);
            if (end < 0) continue; // malformed / missing braces — leave as-is
            spans.Add((m.Index, end));
            claimedTo = end;
        }
        if (spans.Count == 0) return text;

        var sb = new StringBuilder();
        var last = 0;
        foreach (var (start, end) in spans)
        {
            sb.Append(text, last, start - last);
            // Markdig's inline-math parser only treats $...$ as math when the delimiters are
            // "flanked" — opening $ preceded by whitespace/line-start, closing $ followed by
            // whitespace/line-end. Browser-copied math glues the bare command straight against
            // surrounding digits/text (e.g. "…0.0183\frac{a}{b}2454…"), so wrapping in $ alone isn't
            // enough; without inserted spaces Markdig rejects the span and leaves it literal. Add a
            // space on each side only when the adjacent char isn't already whitespace.
            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
            sb.Append('$').Append(text, start, end - start).Append('$');
            last = end;
            if (last < text.Length && !char.IsWhiteSpace(text[last])) sb.Append(' ');
        }
        sb.Append(text, last, text.Length - last);
        fixes.Add($"Wrapped undelimited LaTeX math (\\frac/\\sqrt/\\boxed/…) in $ ({spans.Count})");
        return sb.ToString();
    }

    // Returns the exclusive end index of a "\<name>" followed by `argc` balanced {…} groups (plus,
    // for \sqrt, an optional [..] degree first). Returns -1 if the required braces aren't present or
    // are unbalanced, so a bare command word with no argument is left untouched rather than mangled.
    private static int ConsumeLatexUnit(string s, int cmdStart, string name, int argc)
    {
        int i = cmdStart + 1 + name.Length; // past the backslash + command name

        static int SkipSpaces(string s, int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++; return i; }

        if (name == "sqrt")
        {
            int j = SkipSpaces(s, i);
            if (j < s.Length && s[j] == '[')
            {
                int close = s.IndexOf(']', j);
                if (close < 0) return -1;
                i = close + 1;
            }
        }

        for (int a = 0; a < argc; a++)
        {
            int j = SkipSpaces(s, i);
            if (j >= s.Length || s[j] != '{') return -1;
            int depth = 0, k = j;
            for (; k < s.Length; k++)
            {
                if (s[k] == '{') depth++;
                else if (s[k] == '}') { depth--; if (depth == 0) { k++; break; } }
            }
            if (depth != 0) return -1; // unbalanced
            i = k;
        }
        return i;
    }
}
