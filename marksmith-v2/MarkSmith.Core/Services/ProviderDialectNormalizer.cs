using System.Text.RegularExpressions;

namespace MarkSmith.Services;

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

    // Claude sometimes uses <antThinking>...</antThinking> for chain-of-thought logic.
    // Like <think>, these blocks should be removed from the final markdown.
    private static readonly Regex AntThinkingBlock = new(@"<antThinking>[\s\S]*?(</antThinking>|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Gemini's code-block wrapper often labels Mermaid diagrams as "code snippet" or "code"
    // instead of "mermaid". Matches fences starting with "code snippet", "code", "text", or bare fences
    // that contain a valid Mermaid diagram header.
    private static readonly Regex CodeSnippetMermaidFence = new(
        @"```(?:code[ \t]*snippet|code|text)?\r?\n(\s*(?:graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|journey|gitGraph|mindmap|timeline|quadrantChart|requirementDiagram|C4Context|xychart-beta|sankey-beta|block-beta|packet-beta|kanban|architecture-beta)\b[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MermaidBlockFinder = new(
        @"```mermaid\r?\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        // Content-detected artifact: normalize Gemini "code snippet" blocks to ```mermaid when body is a diagram
        markdown = CodeSnippetMermaidFence.Replace(markdown, "```mermaid\n$1```");

        // Deduplicate duplicate Mermaid blocks (when both raw code block and rendered widget were captured)
        markdown = DeduplicateMermaidBlocks(markdown);

        return providerId?.ToLowerInvariant() switch
        {
            "chatgpt" or "openai" => NormalizeChatGPTQuirks(markdown),
            "claude" or "anthropic" => NormalizeClaudeQuirks(markdown),
            "deepseek" => NormalizeDeepSeekQuirks(markdown),
            "perplexity" => NormalizePerplexityQuirks(markdown),
            "gemini" or "bard" => NormalizeGeminiQuirks(markdown),
            _ => markdown
        };
    }

    private static string NormalizeGeminiQuirks(string md)
    {
        md = PromptEchoHeader.Replace(md, string.Empty);
        return md;
    }

    private static string DeduplicateMermaidBlocks(string md)
    {
        if (!md.Contains("```mermaid", System.StringComparison.OrdinalIgnoreCase)) return md;

        var matches = MermaidBlockFinder.Matches(md);
        if (matches.Count < 2) return md;

        var seenDiagrams = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder(md.Length);
        int lastPos = 0;

        foreach (Match m in matches)
        {
            var bodyKey = m.Groups[1].Value.Trim().Replace("\r\n", "\n");
            if (seenDiagrams.Contains(bodyKey))
            {
                // Skip duplicate block and any immediately preceding blank lines or whitespace
                sb.Append(md.Substring(lastPos, m.Index - lastPos).TrimEnd(' ', '\t', '\r', '\n'));
                lastPos = m.Index + m.Length;
            }
            else
            {
                seenDiagrams.Add(bodyKey);
            }
        }

        if (lastPos < md.Length)
        {
            sb.Append(md.Substring(lastPos));
        }

        return sb.ToString();
    }

    // ChatGPT exports display math as \[ ... \] (single backslash). The verbatim pattern must be
    // @"\[" — a double backslash (@"\\[") never matches the decoded export and silently no-ops.
    private static string NormalizeChatGPTQuirks(string md) =>
        md.Replace(@"\[", "$$").Replace(@"\]", "$$");

    private static string NormalizeClaudeQuirks(string md)
    {
        md = AntThinkingBlock.Replace(md, string.Empty);
        return Regex.Replace(md, @"</?antArtifact[^>]*>", string.Empty);
    }

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
