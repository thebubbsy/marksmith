namespace MarkSmith.Mermaid.Parser;

using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;

public static class FlowchartParser
{
    private static readonly Regex SubgraphHeaderRegex = new(@"^subgraph\s+([^\s\[\]""]+)(?:\s+\[?""?([^""\]]+)""?\]?)?", RegexOptions.IgnoreCase);
    private static readonly Regex DirectivesRegex = new(@"^(?:accTitle:|title)\s*(.+)$", RegexOptions.IgnoreCase);

    public static FlowchartDiagramAst Parse(string code)
    {
        // Normalize multiline pipes created by AIs to single-line pipes
        code = Regex.Replace(code, @"\|\s*\r?\n\s*([^|]*?)\s*\r?\n\s*\|", "|$1|");

        var ast = new FlowchartDiagramAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        Stack<FlowSubgraph> currentSubgraphs = new();

        foreach (var line in lines)
        {
            if (line.StartsWith("%%"))
            {
                if (line.StartsWith("%%{"))
                    ast.Directives.Add(line);
                else
                    ast.Comments.Add(line.Substring(2).Trim());
                continue;
            }

            var dirMatch = DirectivesRegex.Match(line);
            if (dirMatch.Success)
            {
                ast.Title = dirMatch.Groups[1].Value.Trim();
                continue;
            }

            string lower = line.ToLowerInvariant();
            if (lower.StartsWith("flowchart") || lower.StartsWith("graph"))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && Enum.TryParse<FlowDirection>(parts[1], true, out var dir))
                {
                    ast.Direction = dir;
                }
                continue;
            }

            if (lower.StartsWith("subgraph"))
            {
                var sgMatch = SubgraphHeaderRegex.Match(line);
                if (sgMatch.Success)
                {
                    var sgId = sgMatch.Groups[1].Value.Trim();
                    var sgTitle = sgMatch.Groups[2].Success ? sgMatch.Groups[2].Value.Trim() : sgId;
                    var sg = new FlowSubgraph { Id = sgId, Title = sgTitle };
                    if (currentSubgraphs.Count > 0)
                    {
                        currentSubgraphs.Peek().NestedSubgraphs.Add(sg);
                    }
                    else
                    {
                        ast.Subgraphs.Add(sg);
                    }
                    currentSubgraphs.Push(sg);
                }
                continue;
            }

            if (lower == "end")
            {
                if (currentSubgraphs.Count > 0)
                {
                    currentSubgraphs.Pop();
                }
                continue;
            }

            // Parse edge or standalone node line
            ParseLine(line, ast, currentSubgraphs.Count > 0 ? currentSubgraphs.Peek() : null);
        }

        return ast;
    }

    private static readonly Regex EdgeRegex = new(@"^(.*?)\s*(--\s*\|[^|]+\|(?:-->|---|->)|==\s*\|[^|]+\|(?:==>|===|=>)|-\.-\s*\|[^|]+\|(?:-\.->|-.-|->|-->)|--\s*""[^""]+""\s*(?:-->|---|->)|==\s*""[^""]+""\s*(?:==>|===|=>)|-\.\s*""[^""]+""\s*(?:\.->|\.-)|-\.-\s*[^""=\->|]+\s*-\.->|--\s*[^""=\->|]+\s*(?:-->|---|->)|==\s*[^""=\->|]+\s*(?:==>|===|=>)|-\.\s*[^""=\->|]+\s*(?:\.->|\.-)|(?:-->|==>|-.->|<-->|---|===|-.-|->|=>)\s*\|[^|]+\||<-->|x--x|o--o|==>|===|-.->|-.-|-->|---|==)\s*(.*)$");

    private static void ParseLine(string line, FlowchartDiagramAst ast, FlowSubgraph? currentSubgraph)
    {
        var edgeMatch = EdgeRegex.Match(line);

        if (!edgeMatch.Success)
        {
            ParseOrCreateNode(line, ast, currentSubgraph);
            return;
        }

        // Chained edges share one line (A -->|Yes| B -->|No| C): walk the operators left to
        // right, re-matching the remainder each pass so every segment becomes its own edge
        // instead of "B -->|No| C" being swallowed as a single node id.
        FlowNode? fromNode = null;
        while (edgeMatch.Success)
        {
            string leftPart = edgeMatch.Groups[1].Value.Trim();
            string opPart = edgeMatch.Groups[2].Value.Trim();
            string rightPart = edgeMatch.Groups[3].Value.Trim();

            fromNode ??= ParseOrCreateNode(leftPart, ast, currentSubgraph);

            // If the remainder holds another edge operator, this edge's target is only the
            // remainder's left segment and the loop continues from that node. A match whose
            // left segment has unclosed brackets is an arrow inside a node label
            // (B[Go --> Stop]) — not a chain.
            var nextMatch = EdgeRegex.Match(rightPart);
            if (nextMatch.Success && HasUnbalancedBrackets(nextMatch.Groups[1].Value))
                nextMatch = System.Text.RegularExpressions.Match.Empty;
            string toText = nextMatch.Success ? nextMatch.Groups[1].Value.Trim() : rightPart;
            var toNode = ParseOrCreateNode(toText, ast, currentSubgraph);

            var (edgeLabel, style, startHead, endHead) = ParseEdgeOperator(opPart);

            ast.Edges.Add(new FlowEdge
            {
                FromId = fromNode.Id,
                ToId = toNode.Id,
                Label = edgeLabel,
                LineStyle = style,
                StartHead = startHead,
                EndHead = endHead
            });

            fromNode = toNode;
            edgeMatch = nextMatch;
        }
    }

    // True when the text opens more ( [ { or " than it closes — i.e. we're inside a node label.
    private static bool HasUnbalancedBrackets(string text)
    {
        int round = 0, square = 0, curly = 0, quotes = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '(': round++; break;
                case ')': round--; break;
                case '[': square++; break;
                case ']': square--; break;
                case '{': curly++; break;
                case '}': curly--; break;
                case '"': quotes++; break;
            }
        }
        return round != 0 || square != 0 || curly != 0 || quotes % 2 != 0;
    }

    private static (string? Label, FlowLineStyle Style, FlowArrowHead StartHead, FlowArrowHead EndHead) ParseEdgeOperator(string opPart)
    {
        string? edgeLabel = null;
        FlowLineStyle style = FlowLineStyle.Solid;
        FlowArrowHead startHead = FlowArrowHead.None;
        FlowArrowHead endHead = FlowArrowHead.Normal;

        // Extract label and style from opPart
        if (opPart.Contains("|"))
        {
            var lblMatch = Regex.Match(opPart, @"\|([^|]+)\|");
            if (lblMatch.Success)
            {
                edgeLabel = lblMatch.Groups[1].Value.Trim();
            }
        }
        else if (opPart.Contains("\""))
        {
            var lblMatch = Regex.Match(opPart, @"""([^""]+)""");
            if (lblMatch.Success)
            {
                edgeLabel = lblMatch.Groups[1].Value.Trim();
            }
        }
        else
        {
            var lblMatch = Regex.Match(opPart, @"(?:--|==|-\.-|-\.)\s*([^=\->]+?)\s*(?:--+>?|==?>?|-?\.->|\.-)");
            if (lblMatch.Success)
            {
                string matchedLbl = lblMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(matchedLbl))
                {
                    edgeLabel = matchedLbl;
                }
            }
        }

        // Arrowhead / line-style detection inspects the bare edge syntax, so drop any label
        // that trails the arrow first — "-->|Yes|" ends in "|", not ">", and would otherwise
        // lose its arrowhead.
        var opCore = Regex.Replace(opPart, @"\|[^|]*\|\s*$", string.Empty).TrimEnd();

        if (opCore.Contains("=="))
        {
            style = FlowLineStyle.Thick;
            if (!opCore.EndsWith(">")) endHead = FlowArrowHead.None;
        }
        else if (opCore.Contains("-.-") || opCore.Contains("-."))
        {
            style = FlowLineStyle.Dashed;
            if (!opCore.EndsWith(">")) endHead = FlowArrowHead.None;
        }
        else if (opCore.StartsWith("x") && opCore.EndsWith("x"))
        {
            startHead = FlowArrowHead.Cross;
            endHead = FlowArrowHead.Cross;
        }
        else if (opCore.StartsWith("o") && opCore.EndsWith("o"))
        {
            startHead = FlowArrowHead.Circle;
            endHead = FlowArrowHead.Circle;
        }
        else if (opCore.StartsWith("<"))
        {
            startHead = FlowArrowHead.Normal;
            endHead = FlowArrowHead.Normal;
        }
        else if (!opCore.EndsWith(">"))
        {
            endHead = FlowArrowHead.None;
        }

        return (edgeLabel, style, startHead, endHead);
    }

    public static FlowNode ParseOrCreateNode(string text, FlowchartDiagramAst ast, FlowSubgraph? currentSubgraph)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return new FlowNode { Id = "Unknown" };

        string id;
        string label = string.Empty;
        FlowNodeShape shape = FlowNodeShape.Rectangle;

        // Try matching node shapes
        if (TryExtractShape(text, "([", "])", out id, out label)) shape = FlowNodeShape.Stadium;
        else if (TryExtractShape(text, "[[", "]]", out id, out label)) shape = FlowNodeShape.Subroutine;
        else if (TryExtractShape(text, "[(", ")]", out id, out label)) shape = FlowNodeShape.CylindricalDatabase;
        else if (TryExtractShape(text, "((", "))", out id, out label)) shape = FlowNodeShape.Circle;
        else if (TryExtractShape(text, "{{", "}}", out id, out label)) shape = FlowNodeShape.Hexagon;
        else if (TryExtractShape(text, "[/", "/]", out id, out label) || TryExtractShape(text, "[\\", "\\]", out id, out label)) shape = FlowNodeShape.Parallelogram;
        else if (TryExtractShape(text, "[/", "\\]", out id, out label) || TryExtractShape(text, "[\\", "/]", out id, out label)) shape = FlowNodeShape.Trapezoid;
        else if (TryExtractShape(text, "[", "]", out id, out label)) shape = FlowNodeShape.Rectangle;
        else if (TryExtractShape(text, "(", ")", out id, out label)) shape = FlowNodeShape.RoundedRectangle;
        else if (TryExtractShape(text, ">", "]", out id, out label)) shape = FlowNodeShape.Asymmetric;
        else if (TryExtractShape(text, "{", "}", out id, out label)) shape = FlowNodeShape.RhombusDiamond;
        else
        {
            id = text;
            label = text;
            shape = FlowNodeShape.Rectangle;
        }

        id = id.Trim();
        label = label.Trim().Trim('"');

        if (!ast.Nodes.TryGetValue(id, out var node))
        {
            node = new FlowNode
            {
                Id = id,
                Text = string.IsNullOrEmpty(label) ? id : label,
                Shape = shape,
                SubgraphId = currentSubgraph?.Id
            };
            ast.Nodes[id] = node;
            if (currentSubgraph != null && !currentSubgraph.NodeIds.Contains(id))
            {
                currentSubgraph.NodeIds.Add(id);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(label) && label != id)
            {
                node.Text = label;
                node.Shape = shape;
            }
            if (currentSubgraph != null && node.SubgraphId == null)
            {
                node.SubgraphId = currentSubgraph.Id;
                if (!currentSubgraph.NodeIds.Contains(id))
                {
                    currentSubgraph.NodeIds.Add(id);
                }
            }
        }

        return node;
    }

    private static bool TryExtractShape(string input, string startDelim, string endDelim, out string id, out string label)
    {
        id = string.Empty;
        label = string.Empty;

        if (!input.EndsWith(endDelim, StringComparison.Ordinal))
            return false;

        int startIdx = input.IndexOf(startDelim, StringComparison.Ordinal);
        if (startIdx <= 0) return false;

        if (startDelim == ">" && (input.Contains("[") || input.Contains("(")))
            return false;

        int endIdx = input.Length - endDelim.Length;
        if (endIdx <= startIdx) return false;

        id = input.Substring(0, startIdx).Trim();

        // Validate node ID: must not be empty and cannot contain quotes, parens, brackets, operators or whitespace
        if (id.Length == 0 || id.Any(c => c == '"' || c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}' || c == '>' || c == '<' || c == '=' || char.IsWhiteSpace(c)))
            return false;

        label = input.Substring(startIdx + startDelim.Length, endIdx - (startIdx + startDelim.Length)).Trim();
        return true;
    }
}
