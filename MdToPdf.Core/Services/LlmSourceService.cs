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

        // Gemini loves "**Heading:**" lines instead of real headings — promote them so the
        // document gets a navigable structure (and the TOC has something to index).
        Apply(BoldPseudoHeading(), "### $1", "Promoted bold pseudo-headings");

        Apply(ExcessBlankLines(), "\n\n", "Collapsed excess blank lines");

        classification.AppliedFixes = fixes;
        classification.HasMath = classification.HasMath || DollarMath().IsMatch(text);
        return (text.Trim(), fixes);
    }
}
