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
    // Reasoning and Chain-of-Thought blocks emitted by frontier models:
    // DeepSeek R1: <think>...</think>
    // Gemini 2.0 / 3.7 / 3.8: <thought>...</thought>, :::thought
    // Claude 3.7: <thinking>...</thinking>
    private static readonly Regex FencedCodeRegex = new(
        @"```.*?```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ReasoningBlock = new(
        @"<(?:think|thought|thinking|reasoning)\b[^>]*>([\s\S]*?)(</(?:think|thought|thinking|reasoning)>|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReasoningDirective = new(
        @":::(?:thought|thinking|reasoning)\b([\s\S]*?)(:::|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Gemini's code-block wrapper often labels Mermaid diagrams as "code snippet" or "code"
    // instead of "mermaid". Matches fences starting with "code snippet", "code", "text", or bare fences
    // that contain a valid Mermaid diagram header.
    private static readonly Regex CodeSnippetMermaidFence = new(
        @"```(?:code[ \t]*snippet|code|text)?\r?\n(\s*(?:graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|journey|gitGraph|mindmap|timeline|quadrantChart|requirementDiagram|C4Context|xychart-beta|sankey-beta|block-beta|packet-beta|kanban|architecture-beta)\b[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MermaidBlockFinder = new(
        @"```mermaid\r?\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Perplexity & Gemini web-search grounding citation badges: [cite: ...] and bare [1]-style pips.
    // The lookahead keeps real markdown links ("[source](url)") intact.
    private static readonly Regex CitationPip = new(@"\[(?:\d+|sources?)\](?!\()", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GeminiCiteMarker = new(@"\[cite:\s*[^\]]+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Prompt echo: some chat exports open with the user's own prompt echoed back under a
    // "User:" / "Human:" / "Prompt:" label (plain, bold, or block-quoted). Only the FIRST line
    // is eligible — the same label mid-document is legitimate content (dialogue, docs).
    private static readonly Regex PromptEchoHeader = new(@"\A\s*(?:>\s*)?(?:\*\*)?(?:user|human|prompt)(?:\*\*)?\s*:[^\n]*\n?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Normalize(string markdown, string? providerId, bool foldReasoning = false)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;

        // A code fence captured inside a blockquote ("> ```python") never parses as a fence —
        // un-quote it so the block renders as real code, for every provider.
        markdown = Regex.Replace(markdown, @"^>\s*(```[\w-]*)$", "$1", RegexOptions.Multiline);

        // Protect code fences before running reasoning tag extraction so legitimate code is never modified
        var protectedFences = new System.Collections.Generic.List<string>();
        markdown = FencedCodeRegex.Replace(markdown, m =>
        {
            protectedFences.Add(m.Value);
            return $" MS_PROTECTED_FENCE_{protectedFences.Count - 1}_ ";
        });

        // Content-detected artifact: reasoning blocks (DeepSeek <think>, Gemini <thought>, Claude <thinking>)
        if (foldReasoning)
        {
            // Transform into Word-native collapsible details sections
            markdown = ReasoningBlock.Replace(markdown, m =>
            {
                var body = m.Groups[1].Value.Trim();
                return string.IsNullOrWhiteSpace(body) ? "" : $"\n<details>\n<summary>Reasoning Process</summary>\n\n{body}\n\n</details>\n";
            });
            markdown = ReasoningDirective.Replace(markdown, m =>
            {
                var body = m.Groups[1].Value.Trim();
                return string.IsNullOrWhiteSpace(body) ? "" : $"\n<details>\n<summary>Reasoning Process</summary>\n\n{body}\n\n</details>\n";
            });
        }
        else
        {
            markdown = ReasoningBlock.Replace(markdown, string.Empty);
            markdown = ReasoningDirective.Replace(markdown, string.Empty);
        }

        // Restore fences before diagram detection & deduplication
        if (protectedFences.Count > 0)
        {
            markdown = Regex.Replace(markdown, @" MS_PROTECTED_FENCE_(\d+)_ ", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx < protectedFences.Count ? protectedFences[idx] : m.Value;
            });
            protectedFences.Clear();
        }

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
            "gemini" or "bard" or "gemini-3.8" or "gemini-3-8" or "gemini38" or "gemini_3_8" or "gemini-pro" or "gemini-flash" or "gemini-exp" => NormalizeGeminiQuirks(markdown),
            _ => markdown
        };
    }

    private static string NormalizeGeminiQuirks(string md)
    {
        md = PromptEchoHeader.Replace(md, string.Empty);
        md = GeminiCiteMarker.Replace(md, string.Empty);
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
