using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// ISS-005: provider-specific Markdown quirk normalization, keyed off the definitive source id the
// browser extension reports (ground truth — see LlmSourceService.ParseSourceId). Complements the
// content-heuristic repairs in LlmSourceService: this handles quirks that only make sense when the
// originating provider is KNOWN (DeepSeek's escaped table pipes, Perplexity's [n] citation pips),
// plus a provider-agnostic fix for quoted code fences ("> ```lang") that arrive block-quoted.
// Unknown / null provider ids still get the generic fence fix; every rule is a no-op when its
// pattern is absent, so this is safe to run on any ingested text.
public static class ProviderDialectNormalizer
{
    // DeepSeek R1 exposes its chain-of-thought in <think>...</think> blocks (sometimes left
    // unclosed at the end of the stream). The reasoning is scaffolding, never part of the
    // answer — and the marker is unmistakable, so this runs content-detected for EVERY
    // provider id (including unknown/null), not just "deepseek".
    private static readonly Regex ThinkBlock = new(@"<think>[\s\S]*?(</think>|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Perplexity's web-search citation badges: bare [1]-style pips and [source] markers.
    // The lookahead keeps real markdown links ("[source](url)") intact.
    private static readonly Regex CitationPip = new(@"\[(?:\d+|sources?)\](?!\()", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Prompt echo: some chat exports open with the user's own prompt echoed back under a
    // "User:" / "Human:" / "Prompt:" label (plain, bold, or block-quoted). Only the FIRST line
    // is eligible — the same label mid-document is legitimate content (dialogue, docs).
    private static readonly Regex PromptEchoHeader = new(@"\A\s*(?:>\s*)?(?:\*\*)?(?:user|human|prompt)(?:\*\*)?\s*:[^\n]*\n?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Normalize(string markdown, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;

        // A code fence captured inside a blockquote ("> ```python") never parses as a fence —
        // un-quote it so the block renders as real code, for every provider.
        markdown = Regex.Replace(markdown, @"^>\s*(```[\w-]*)$", "$1", RegexOptions.Multiline);

        // Content-detected artifact: reasoning blocks are stripped regardless of source id.
        markdown = ThinkBlock.Replace(markdown, string.Empty);

        return providerId?.ToLowerInvariant() switch
        {
            "chatgpt" or "openai" => NormalizeChatGPTQuirks(markdown),
            "claude" or "anthropic" => NormalizeClaudeQuirks(markdown),
            "deepseek" => NormalizeDeepSeekQuirks(markdown),
            "perplexity" => NormalizePerplexityQuirks(markdown),
            _ => markdown
        };
    }

    // ChatGPT exports display math as \[ ... \] (single backslash). The verbatim pattern must be
    // @"\[" — a double backslash (@"\\[") never matches the decoded export and silently no-ops.
    private static string NormalizeChatGPTQuirks(string md) =>
        md.Replace(@"\[", "$$").Replace(@"\]", "$$");

    private static string NormalizeClaudeQuirks(string md) =>
        Regex.Replace(md, @"</?antArtifact[^>]*>", string.Empty);

    private static string NormalizeDeepSeekQuirks(string md)
    {
        md = PromptEchoHeader.Replace(md, string.Empty);
        return md.Replace(@"\|", "|");
    }

    private static string NormalizePerplexityQuirks(string md)
    {
        md = PromptEchoHeader.Replace(md, string.Empty);
        return CitationPip.Replace(md, string.Empty);
    }
}
