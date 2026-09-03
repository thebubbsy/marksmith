using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Models;

namespace MarkSmith.Services;

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
    // "Copy code" is an unambiguous ChatGPT copy-button artifact — strip it. A BARE "Copy" line is
    // NOT stripped: it's indistinguishable from legitimate prose (a UI-walkthrough step, a one-word
    // list item), and the earlier `Copy|Copy code` pattern silently deleted those. (Removing a bare
    // "Copy" only when adjacent to a fence isn't possible here — RepairArtifacts splits the text on
    // code fences before this runs, so the fence isn't visible to a lookahead.)
    [GeneratedRegex(@"^Copy code[ \t]*$", RegexOptions.Multiline)] private static partial Regex CopyCodeButtons();
    [GeneratedRegex(@"^(ChatGPT can make mistakes\..*|Gemini can make mistakes.*|Claude can make mistakes.*)$", RegexOptions.Multiline)] private static partial Regex DisclaimerFooters();
    [GeneratedRegex(@"</?(thought|thinking|reasoning|artifact|search_reminders|automated_reminder_from_anthropic)[^>]*>")] private static partial Regex TagRemnants();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ExcessBlankLines();
    [GeneratedRegex(@"\$\$?.+?\$\$?", RegexOptions.Singleline)] private static partial Regex DollarMath();
    // A proper $$...$$ display-math block, used ONLY to protect already-delimited math from the
    // recovery passes below. The looser DollarMath pattern (kept for HasMath detection) can mis-pair
    // a lone $ with a $$ elsewhere in the document — e.g. an inline $...$ closing $ pairing with a
    // following block's opening $$ — leaving a real $$...$$ block's \begin{...}...\end{...} body
    // unprotected. That let RecoverMatrixEnvironments re-wrap an already-correct matrix in a SECOND
    // set of $$, which Markdig then parsed as two empty math blocks around a literal
    // "\begin 1 & 2 & 3 \ ..." paragraph (the reported sample-doc preview bug).
    [GeneratedRegex(@"\$\$[\s\S]*?\$\$")] private static partial Regex DisplayMathBlock();
    // Seed commands for undelimited-math recovery: each is a self-contained structural construct
    // (fraction, root, binomial, box) that is ALWAYS real math — a bare one glued into text is never
    // prose, unlike a lone \times or \approx which might be. The unit's brace arguments are consumed
    // by a balance-aware scanner (WrapBareLatexMath), not a regex, so nesting like \frac{\sqrt x}{y}
    // is handled correctly.
    [GeneratedRegex(@"\\(frac|dfrac|tfrac|boxed|sqrt|binom|dbinom|tbinom)(?![a-zA-Z])")] private static partial Regex BareLatexSeed();
    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)] private static partial Regex FencedCode();
    [GeneratedRegex(@"`[^`\n]+`")] private static partial Regex InlineCode();

    // A LaTeX \begin…\end environment that arrived as plain text from a browser copy of RENDERED
    // math (a matrix, a system of equations, an aligned block). KaTeX/MathJax copy handlers mangle
    // these badly: the environment NAME is dropped (\begin{bmatrix} -> \begin), the \\ row breaks
    // collapse to single \, and the rendered glyphs get glued on as flat digit runs before and
    // after — e.g. "[123456789]\begin 1 & 2 & 3\ 4 & 5 & 6\ 7 & 8 & 9 \end147258369" for a 3x3
    // matrix. Captures the optional flattened-reading runs (lead/trail) so they can be dropped, and
    // the body/env so it can be rebuilt. Requires the body to contain '&' (a cell separator) to
    // qualify, checked in code, so a stray \begin{itemize}…\end{itemize} in prose is never touched.
    [GeneratedRegex(@"(?<lead>\[\d+\])?\\begin\s*(?<env>\{[A-Za-z*]+\})?(?<body>.*?)\\end\s*(?<env2>\{[A-Za-z*]+\})?(?<trail>\d+)?", RegexOptions.Singleline)]
    private static partial Regex MatrixEnvArtifact();
    // A run of one or more backslashes NOT followed by a letter — i.e. mangled \\ row separators
    // (a lone "\" or "\ " between cells), as opposed to a real command like \frac. Normalized to \\.
    [GeneratedRegex(@"\\+(?![A-Za-z])")] private static partial Regex MangledRowBreak();

    // Classification signals (Classify) — source-generated rather than inline Regex.IsMatch so the
    // hot "which AI did this come from" path doesn't re-parse its patterns on every call.
    [GeneratedRegex(@"^\s*Sources\s*$", RegexOptions.Multiline)] private static partial Regex SourcesHeading();
    [GeneratedRegex(@"^\d+\.\s+\S+\.(com|org|net|io|dev)", RegexOptions.Multiline)] private static partial Regex NumberedSourceUrl();
    [GeneratedRegex(@"^Would you like me to", RegexOptions.Multiline)] private static partial Regex TrailingOffer();
    // Repair passes (RepairArtifacts / RecoverMatrixEnvironments) — same source-gen rationale.
    [GeneratedRegex(@"\$\$\s*(\\begin\{[A-Za-z*]+\}.*?\\end\{[A-Za-z*]+\})\s*\$\$", RegexOptions.Singleline)] private static partial Regex SingleLineDisplayEnv();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex HorizontalWhitespace();

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
        "gemini" or "bard" or "gemini-3.8" or "gemini-3-8" or "gemini38" or "gemini_3_8" or "gemini-pro" or "gemini-flash" or "gemini-exp" => LlmSource.Gemini,
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
        if (markdown.Contains("<thought>") || markdown.Contains("</thought>")) { gemini += 45; signals.Add("Gemini reasoning <thought> tokens"); }
        if (markdown.Contains("Gemini 3.8") || markdown.Contains("gemini-3.8")) { gemini += 40; signals.Add("Gemini 3.8 identifier"); }
        if (BoldPseudoHeading().Matches(markdown).Count >= 2 && !markdown.Contains("\n# ") && !markdown.Contains("\n## "))
        { gemini += 25; signals.Add("bold-line pseudo-headings"); }
        if (SourcesHeading().IsMatch(markdown) && NumberedSourceUrl().IsMatch(markdown))
        { gemini += 25; signals.Add("trailing Sources block"); }

        if (TagRemnants().IsMatch(markdown)) { claude += 30; gemini += 20; signals.Add("AI tag remnants"); }
        if (markdown.Contains("Claude can make mistakes")) { claude += 50; signals.Add("Claude footer"); }
        if (TrailingOffer().IsMatch(markdown)) { claude += 15; signals.Add("trailing offer"); }

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

    // Always-on correctness repairs — NOT gated on the "Normalize AI quirks" toggle, because these
    // fix corruption, not style: they remove copy/paste garbage that is never real content (dead
    // citation pips, captured "Copy code" button text, leaked internal control tags) and rescue math
    // that would otherwise render as literal backslash soup (\( \)/\[ \] delimiters, browser-mangled
    // \begin…\end matrices, undelimited \frac/\sqrt). A user never WANTS any of these in their
    // export, and each is a no-op when its pattern doesn't match, so this is safe to run on any text.
    public (string Cleaned, List<string> Fixes) RepairArtifacts(string markdown, LlmClassification classification)
    {
        var fixes = new List<string>();

        // Pull fenced code blocks out to placeholders before running any repair, so nothing here
        // can rewrite a legitimate code sample that happens to contain one of these patterns — a
        // "Copy code" line, a <thinking> tag in an XML example, a literal \begin{bmatrix}. The
        // math-recovery passes below also protect inline code / existing $-math internally; this
        // adds the fenced-block guard the artifact removals would otherwise lack (they run
        // unconditionally now, so a false positive inside code would be real corruption).
        var text = ProtectFencedCode(markdown, out var fencedBlocks);

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
        Apply(TagRemnants(), "", "Removed internal tag remnants");

        // ChatGPT emits \( \) / \[ \]; Markdig's math extension wants $ / $$.
        Apply(LatexBlock(), "\n$$$$\n${1}\n$$$$\n", "Converted LaTeX display math to $$");
        Apply(LatexInline(), "$$${1}$$", "Converted LaTeX inline math to $");

        // Format single-line $$\begin{...}...\end{...}$$ environments onto their own display lines
        // so Markdig parses them as <div class="math"> (display-mode) rather than inline <span class="math">,
        // preventing KaTeX inline syntax errors (which rendered matrices as red text).
        text = SingleLineDisplayEnv().Replace(text, "\n$$$$\n${1}\n$$$$\n");

        // Fenced code, inline code, and existing $-math are protected from the recovery passes.
        // Both passes used to run the same four fence/math scans internally — compute the protected
        // ranges ONCE here and share them, recomputing only if matrix recovery rewrote the text
        // (Regex.Replace returns the same string instance when nothing matched, so a reference
        // compare is a cheap, safe staleness check).
        var protectedRanges = BuildProtectedRanges(text);

        // Rebuild browser-copied \begin…\end environments (matrices, aligned blocks) into clean,
        // $$-delimited LaTeX BEFORE WrapBareLatexMath, so the $$ it produces is protected from the
        // bare-command wrapper below.
        var beforeMatrix = text;
        text = RecoverMatrixEnvironments(text, fixes, protectedRanges);
        if (!ReferenceEquals(text, beforeMatrix)) protectedRanges = BuildProtectedRanges(text);

        text = WrapBareLatexMath(text, fixes, protectedRanges);

        text = RestoreFencedCode(text, fencedBlocks).Trim();
        classification.AppliedFixes.AddRange(fixes);
        classification.HasMath = classification.HasMath || DollarMath().IsMatch(text);
        return (text, fixes);
    }

    // Swap fenced code blocks for inert " MKCODEn " placeholders (which match none of the repair
    // patterns) so repairs skip them, then swap them back verbatim. The token is space-padded and
    // numeric so nothing here rewrites it; a real document containing the literal string is a
    // non-issue (worst case a repair would skip a spurious spot, never corrupt content).
    [GeneratedRegex(" MKCODE(\\d+) ")] private static partial Regex CodePlaceholder();

    private static string ProtectFencedCode(string text, out List<string> blocks)
    {
        var list = new List<string>();
        var result = FencedCode().Replace(text, m =>
        {
            list.Add(m.Value);
            return $" MKCODE{list.Count - 1} ";
        });
        blocks = list;
        return result;
    }

    private static string RestoreFencedCode(string text, List<string> blocks) =>
        blocks.Count == 0 ? text : CodePlaceholder().Replace(text, m => blocks[int.Parse(m.Groups[1].Value)]);

    // Opt-in stylistic cleanup, gated on the "Normalize AI quirks" toggle — these are preferences,
    // not corrections: stripping vendor boilerplate footers, reshaping bold pseudo-headings into
    // real headings, and tightening blank-line spacing. A user could legitimately want any of them
    // left alone. Assumes RepairArtifacts has already run.
    public (string Cleaned, List<string> Fixes) NormalizeStyle(string markdown, LlmClassification classification,
        IReadOnlyList<TextCleanupRule>? customRules = null)
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

        Apply(DisclaimerFooters(), "", "Removed assistant disclaimer footer");

        // Gemini loves "**Heading:**" lines instead of real headings — promote them so the
        // document gets a navigable structure (and the TOC has something to index).
        Apply(BoldPseudoHeading(), "### $1", "Promoted bold pseudo-headings");

        Apply(ExcessBlankLines(), "\n\n", "Collapsed excess blank lines");

        // User-authored cleanup rules on top of the built-in quirks fixes (Settings -> AI
        // Normalization): strip buzzwords, replace phrases, regex rewrites.
        if (customRules is not null)
        {
            foreach (var rule in customRules)
            {
                if (string.IsNullOrWhiteSpace(rule.Find)) continue;
                try
                {
                    int count;
                    if (rule.IsRegex)
                    {
                        var rx = new Regex(rule.Find, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        count = rx.Matches(text).Count;
                        if (count > 0) text = rx.Replace(text, rule.Replace ?? "");
                    }
                    else
                    {
                        count = 0;
                        int idx = 0;
                        while ((idx = text.IndexOf(rule.Find, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            count++;
                            text = text.Remove(idx, rule.Find.Length).Insert(idx, rule.Replace ?? "");
                            idx += (rule.Replace ?? "").Length;
                        }
                    }
                    if (count > 0) fixes.Add("Custom: " + rule.Find + " (" + count + ")");
                }
                catch (ArgumentException)
                {
                    fixes.Add("Custom rule skipped (bad regex): " + rule.Find);
                }
            }
        }

        text = text.Trim();
        classification.AppliedFixes.AddRange(fixes);
        return (text, fixes);
    }

    // Convenience: always-on repairs followed by stylistic cleanup, in one call — for callers that
    // have already decided the stylistic toggle is on. Callers that want only the unconditional
    // corrections call RepairArtifacts directly and skip NormalizeStyle.
    public (string Cleaned, List<string> Fixes) Normalize(string markdown, LlmClassification classification)
    {
        var (repaired, repairFixes) = RepairArtifacts(markdown, classification);
        var (styled, styleFixes) = NormalizeStyle(repaired, classification);
        return (styled, repairFixes.Concat(styleFixes).ToList());
    }

    // The four "protected" spans (fenced code, inline code, $-math, $$-blocks) that the math
    // recovery passes must not rewrite. Computed once in RepairArtifacts and shared by both
    // RecoverMatrixEnvironments and WrapBareLatexMath (these four scans used to run twice).
    private static List<(int Start, int End)> BuildProtectedRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        void Collect(Regex rx) { foreach (Match m in rx.Matches(text)) ranges.Add((m.Index, m.Index + m.Length)); }
        Collect(FencedCode());
        Collect(InlineCode());
        Collect(DollarMath());
        Collect(DisplayMathBlock());
        return ranges;
    }

    // Recovers a LaTeX \begin…\end environment (matrix / aligned system) that arrived as mangled
    // plain text from a browser copy of the RENDERED math — see MatrixEnvArtifact for the exact
    // shape. Rebuilds a clean, $$-delimited environment: re-injects the dropped environment name
    // (defaulting to bmatrix, matching the bracket the flattened reading was wrapped in), restores
    // \\ row separators, and drops the glued-on flattened-glyph digit runs. Scoped to bodies
    // containing '&' and skipped inside code / existing $-math, so it only fires on genuine copied
    // matrices, never on prose or a code sample that happens to show \begin{…}.
    private string RecoverMatrixEnvironments(string text, List<string> fixes, List<(int Start, int End)> protectedRanges)
    {
        bool IsProtected(int index) => protectedRanges.Any(r => index >= r.Start && index < r.End);

        int count = 0;
        var result = MatrixEnvArtifact().Replace(text, m =>
        {
            if (IsProtected(m.Index)) return m.Value;

            var body = m.Groups["body"].Value;
            if (!body.Contains('&')) return m.Value; // not a matrix/aligned env — leave untouched

            var envName = EnvName(m.Groups["env"].Value) ?? EnvName(m.Groups["env2"].Value) ?? "bmatrix";

            // Collapse mangled row breaks (\, \ , \\) -> \\, then tidy internal whitespace so the
            // rebuilt source reads cleanly. Cell contents (e.g. a real \frac) are left intact.
            var cleanBody = MangledRowBreak().Replace(body, @"\\");
            cleanBody = HorizontalWhitespace().Replace(cleanBody, " ").Trim();

            count++;
            return $"\n\n$$\\begin{{{envName}}} {cleanBody} \\end{{{envName}}}$$\n\n";
        });

        if (count > 0) fixes.Add($"Recovered browser-copied matrix/LaTeX environments ({count})");
        return result;
    }

    // "{bmatrix}" -> "bmatrix"; null/empty -> null.
    private static string? EnvName(string braced) =>
        string.IsNullOrEmpty(braced) ? null : braced.Trim('{', '}');

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
    private static string WrapBareLatexMath(string text, List<string> fixes, List<(int Start, int End)> protectedRanges)
    {
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
