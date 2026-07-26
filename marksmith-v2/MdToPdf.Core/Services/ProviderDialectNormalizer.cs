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
    public static string Normalize(string markdown, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;

        // A code fence captured inside a blockquote ("> ```python") never parses as a fence —
        // un-quote it so the block renders as real code, for every provider.
        markdown = Regex.Replace(markdown, @"^>\s*(```[\w-]*)$", "$1", RegexOptions.Multiline);

        return providerId?.ToLowerInvariant() switch
        {
            "chatgpt" or "openai" => NormalizeChatGPTQuirks(markdown),
            "claude" or "anthropic" => NormalizeClaudeQuirks(markdown),
            "deepseek" => NormalizeDeepSeekQuirks(markdown),
            "perplexity" => NormalizePerplexityQuirks(markdown),
            _ => markdown
        };
    }

    private static string NormalizeChatGPTQuirks(string md) =>
        md.Replace(@"\\[", "$$").Replace(@"\\]", "$$");

    private static string NormalizeClaudeQuirks(string md) =>
        Regex.Replace(md, @"</?antArtifact[^>]*>", string.Empty);

    private static string NormalizeDeepSeekQuirks(string md) =>
        md.Replace(@"\|", "|");

    private static string NormalizePerplexityQuirks(string md) =>
        Regex.Replace(md, @"\[\d+\]", string.Empty);
}
