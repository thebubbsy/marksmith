using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf.Services;

// ISS-018: non-destructive preservation of the mermaid constructs the bespoke AST cannot
// represent — `style` / `classDef` / `linkStyle` directives and per-subgraph `direction` —
// so a parse → AST → re-serialize ("reflow") round trip no longer strips node styling or
// flattens "Left-to-Right Process" subgraphs back to top-down. Applied ONLY on the
// re-serialization paths (MermaidMarkdownSyncService.SyncAstToMarkdown and the Mermaid Studio
// sync), never at ingest: untouched documents render exactly as authored.
public static class MermaidPreservationNormalizer
{
    // A subgraph header NOT already followed by a `direction` line (spec ISS-018 lock pass).
    private static readonly Regex SubgraphHeaderNoDirection = new(
        @"(subgraph\s+[""]?[^""\r\n]+[""]?)\s*\r?\n(?!\s*direction)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // style / classDef / linkStyle directives (re-anchored after re-serialization).
    private static readonly Regex StyleDirective = new(
        @"\b(style|classDef|linkStyle)\s+([^\r\n]+)", RegexOptions.Compiled);

    private static readonly Regex StyleLine = new(
        @"^[ \t]*(style|classDef|linkStyle)\b[^\r\n]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex DirectionLine = new(
        @"^[ \t]*direction[ \t]+(TD|TB|LR|BT|RL)[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // Spec pass: locks subgraph orientation (an explicit `direction LR` for any subgraph that
    // lost / never had one) and re-anchors style directives so stray re-serialization whitespace
    // can't detach the payload from the keyword.
    public static string PreserveMermaidStylesAndSubgraphs(string mermaidSource)
    {
        if (string.IsNullOrWhiteSpace(mermaidSource)) return mermaidSource;

        // 1. Lock subgraph directions.
        var normalized = SubgraphHeaderNoDirection.Replace(mermaidSource, "$1\n    direction LR");

        // 2. Ensure style / classDef / linkStyle statements remain intact post-reflow.
        normalized = StyleDirective.Replace(normalized, m =>
        {
            var directive = m.Groups[1].Value;
            var payload = m.Groups[2].Value;
            return $"{directive} {payload.Trim()}";
        });

        return normalized;
    }

    // Full round-trip preservation: carry the original fence's style directives and subgraph
    // directions across to the regenerated code, then run the spec lock pass (which only touches
    // subgraphs that still have no direction — recovered ones keep their true orientation).
    public static string Preserve(string generated, string? originalSource)
    {
        if (string.IsNullOrWhiteSpace(generated)) return generated;
        if (!string.IsNullOrWhiteSpace(originalSource))
        {
            generated = CarryOverStyleDirectives(generated, originalSource);
            generated = CarryOverSubgraphDirections(generated, originalSource);
        }
        return PreserveMermaidStylesAndSubgraphs(generated);
    }

    // Re-appends style/classDef/linkStyle lines from the authored fence that the AST round trip
    // dropped (the AST has no storage for them), skipping anything already present.
    private static string CarryOverStyleDirectives(string generated, string original)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in StyleDirective.Matches(generated))
            existing.Add(NormalizeDirective(m));

        var carried = new List<string>();
        foreach (Match m in StyleLine.Matches(original))
        {
            var line = m.Value.Trim();
            var normalized = StyleDirective.Replace(line, NormalizeDirective, 1);
            if (existing.Add(normalized)) carried.Add(line);
        }

        return carried.Count == 0
            ? generated
            : generated.TrimEnd() + "\n" + string.Join("\n", carried);
    }

    private static string NormalizeDirective(Match m) =>
        $"{m.Groups[1].Value.ToLowerInvariant()} {m.Groups[2].Value.Trim()}";

    // Recovers each authored subgraph's `direction` (keyed by id, falling back to title) and
    // re-inserts it after the matching regenerated subgraph header.
    private static string CarryOverSubgraphDirections(string generated, string original)
    {
        var authored = MapAuthoredDirections(original);
        if (authored.Count == 0) return generated;

        var lines = generated.Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]).Append('\n');

            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase)) continue;

            var next = i + 1 < lines.Length ? lines[i + 1].TrimStart() : string.Empty;
            if (next.StartsWith("direction", StringComparison.OrdinalIgnoreCase)) continue;

            if (TryLookupDirection(authored, trimmed, out var direction))
            {
                var indent = new string(' ', lines[i].Length - trimmed.Length + 4);
                sb.Append(indent).Append("direction ").Append(direction).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static Dictionary<string, string> MapAuthoredDirections(string source)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase)) continue;

            // The direction (if any) is the next non-blank line.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0) continue;
                var dm = DirectionLine.Match(lines[j]);
                if (!dm.Success) break; // some other statement — no authored direction

                foreach (var key in SubgraphKeys(trimmed))
                    map.TryAdd(key, dm.Groups[1].Value.ToUpperInvariant());
                break;
            }
        }
        return map;
    }

    private static bool TryLookupDirection(
        Dictionary<string, string> authored, string headerLine, out string direction)
    {
        foreach (var key in SubgraphKeys(headerLine))
        {
            if (authored.TryGetValue(key, out direction!)) return true;
        }
        direction = string.Empty;
        return false;
    }

    // Lookup keys for a subgraph header: its id (first bare token) and its title (quoted string
    // or [bracketed] label), so `subgraph SG1 ["Data"]` matches the authored `subgraph SG1`.
    private static IEnumerable<string> SubgraphKeys(string headerLine)
    {
        var rest = headerLine.Trim();
        if (rest.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase))
            rest = rest["subgraph".Length..].Trim();

        var first = rest.Split(' ', 2)[0].Trim();
        if (first.Length > 0 && !first.StartsWith('"'))
            yield return first.TrimEnd('[', ']').Trim();

        var quoted = Regex.Match(rest, "\"([^\"]+)\"");
        if (quoted.Success) yield return quoted.Groups[1].Value;

        var bracketed = Regex.Match(rest, @"\[([^\]]+)\]");
        if (bracketed.Success)
        {
            var title = bracketed.Groups[1].Value.Trim().Trim('"');
            if (title.Length > 0) yield return title;
        }
    }
}
