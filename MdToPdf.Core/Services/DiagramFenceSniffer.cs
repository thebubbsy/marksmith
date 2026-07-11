using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// Labels UNLABELLED fenced code blocks with the diagram language their content clearly is, so the
// downstream diagram-plugin path (preview + DOCX) can pick them up. AI assistants routinely emit a
// bare ``` fence for a diagram — e.g. ChatGPT returns Graphviz as ``` … digraph { … } ``` with no
// `dot` info string — and the plugin detector, which keys off the fence language, then never sees
// it and it renders as plain code.
//
// Only rewrites when the content matches a specific, low-false-positive signature (a Graphviz
// `digraph{`/`graph{`, a PlantUML `@start…`, a Vega/Vega-Lite `$schema`), and never touches a fence
// that already declares a language or whose content Mermaid owns (`graph TD` with no braces is
// Mermaid, not Graphviz — the brace requirement keeps them apart). Runs before Markdig in both the
// HTML/PDF and DOCX pipelines, like the other normalizers, and skips nothing else in the document.
public static class DiagramFenceSniffer
{
    private static readonly (Regex Signature, string Language)[] Signatures =
    {
        // Graphviz DOT: a graph/digraph header with a brace. The brace is what separates it from
        // Mermaid's `graph TD` / `graph LR` (no braces), which must stay Mermaid.
        (new Regex(@"^\s*(strict\s+)?(di)?graph\b[^\n{]*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline), "dot"),
        // PlantUML: any @start… opener (uml/mindmap/gantt/salt/json/yaml/wbs/…).
        (new Regex(@"^\s*@start[a-z]+", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline), "plantuml"),
        // Vega / Vega-Lite: a JSON doc whose $schema points at a vega spec.
        (new Regex("\"\\$schema\"\\s*:\\s*\"[^\"]*vega", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vega-lite"),
    };

    private static readonly Regex FenceOpen = new(@"^(\s*)(`{3,}|~{3,})[ \t]*$", RegexOptions.Compiled);

    public static string Apply(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || (!markdown.Contains("```", StringComparison.Ordinal) && !markdown.Contains("~~~", StringComparison.Ordinal)))
            return markdown;

        var lines = markdown.Split('\n');
        var changed = false;

        for (int i = 0; i < lines.Length; i++)
        {
            // An opener with NO info string (a language already present means it's already routed).
            var open = FenceOpen.Match(lines[i]);
            if (!open.Success) continue;

            var indent = open.Groups[1].Value;
            var marker = open.Groups[2].Value[..3];

            // Collect the fence body up to the matching closing marker.
            int close = -1;
            var body = new System.Text.StringBuilder();
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].TrimStart().StartsWith(marker, StringComparison.Ordinal)) { close = j; break; }
                body.Append(lines[j]).Append('\n');
            }
            if (close < 0) break; // unterminated fence — leave the rest of the document alone

            var content = body.ToString();
            foreach (var (signature, language) in Signatures)
            {
                if (signature.IsMatch(content))
                {
                    lines[i] = $"{indent}{open.Groups[2].Value}{language}";
                    changed = true;
                    break;
                }
            }

            i = close; // resume scanning after this fence (never re-enter its body)
        }

        return changed ? string.Join("\n", lines) : markdown;
    }
}
