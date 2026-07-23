namespace MdToPdf.Mermaid.Parser;

using System.Text.RegularExpressions;
using MdToPdf.Mermaid.Ast;

public static class StateDiagramParser
{
    private static readonly Regex TransitionRegex = new(@"^([^\s:]+)\s*-->\s*([^\s:]+)(?:\s*:\s*(.*))?$", RegexOptions.IgnoreCase);
    private static readonly Regex StateChoiceRegex = new(@"^state\s+([^\s]+)\s+<<(choice|fork|join)>>$", RegexOptions.IgnoreCase);
    private static readonly Regex StateLabelAsRegex = new(@"^state\s+""([^""]+)""\s+as\s+([^\s]+)$", RegexOptions.IgnoreCase);
    private static readonly Regex StateLabelColonRegex = new(@"^([^\s:]+)\s*:\s*(.*)$", RegexOptions.IgnoreCase);

    public static StateDiagramAst Parse(string code)
    {
        var ast = new StateDiagramAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        Stack<StateNode> compositeStack = new();

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

            string lower = line.ToLowerInvariant();
            if (lower == "statediagram" || lower == "statediagram-v2")
            {
                ast.IsV2 = lower.EndsWith("-v2");
                continue;
            }

            if (lower.StartsWith("title "))
            {
                ast.Title = line.Substring(6).Trim();
                continue;
            }

            if (line.StartsWith("state ") && line.EndsWith("{"))
            {
                string stateId = line.Substring(6, line.Length - 7).Trim();
                var compState = GetOrCreateState(ast, stateId, compositeStack.Count > 0 ? compositeStack.Peek() : null);
                compState.Type = StateNodeType.Composite;
                compositeStack.Push(compState);
                continue;
            }

            if (line == "}")
            {
                if (compositeStack.Count > 0)
                {
                    compositeStack.Pop();
                }
                continue;
            }

            var choiceMatch = StateChoiceRegex.Match(line);
            if (choiceMatch.Success)
            {
                string id = choiceMatch.Groups[1].Value;
                string tag = choiceMatch.Groups[2].Value.ToLowerInvariant();
                var node = GetOrCreateState(ast, id, compositeStack.Count > 0 ? compositeStack.Peek() : null);
                node.Type = tag switch
                {
                    "choice" => StateNodeType.Choice,
                    "fork" => StateNodeType.Fork,
                    "join" => StateNodeType.Join,
                    _ => StateNodeType.Normal
                };
                continue;
            }

            var labelAsMatch = StateLabelAsRegex.Match(line);
            if (labelAsMatch.Success)
            {
                string label = labelAsMatch.Groups[1].Value;
                string id = labelAsMatch.Groups[2].Value;
                var node = GetOrCreateState(ast, id, compositeStack.Count > 0 ? compositeStack.Peek() : null);
                node.Label = label;
                continue;
            }

            var transMatch = TransitionRegex.Match(line);
            if (transMatch.Success)
            {
                string fromId = transMatch.Groups[1].Value;
                string toId = transMatch.Groups[2].Value;
                string? eventLbl = transMatch.Groups[3].Success ? transMatch.Groups[3].Value.Trim() : null;

                var fromNode = GetOrCreateState(ast, fromId, compositeStack.Count > 0 ? compositeStack.Peek() : null, isTarget: false);
                var toNode = GetOrCreateState(ast, toId, compositeStack.Count > 0 ? compositeStack.Peek() : null, isTarget: true);

                var transition = new StateTransition
                {
                    FromId = fromNode.Id,
                    ToId = toNode.Id,
                    EventLabel = eventLbl
                };

                if (compositeStack.Count > 0)
                {
                    compositeStack.Peek().SubTransitions.Add(transition);
                }
                else
                {
                    ast.Transitions.Add(transition);
                }
                continue;
            }

            var labelColonMatch = StateLabelColonRegex.Match(line);
            if (labelColonMatch.Success)
            {
                string id = labelColonMatch.Groups[1].Value;
                string label = labelColonMatch.Groups[2].Value.Trim();
                var node = GetOrCreateState(ast, id, compositeStack.Count > 0 ? compositeStack.Peek() : null);
                node.Label = label;
                continue;
            }

            if (line.StartsWith("state "))
            {
                string stateId = line.Substring(6).Trim();
                GetOrCreateState(ast, stateId, compositeStack.Count > 0 ? compositeStack.Peek() : null);
                continue;
            }
        }

        return ast;
    }

    private static StateNode GetOrCreateState(StateDiagramAst ast, string id, StateNode? parent, bool isTarget = false)
    {
        id = id.Trim();
        StateNodeType type = StateNodeType.Normal;

        if (id == "[*]")
        {
            type = isTarget ? StateNodeType.End : StateNodeType.Start;
        }

        if (parent != null)
        {
            var existingSub = parent.SubStates.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existingSub == null)
            {
                existingSub = new StateNode { Id = id, Label = id, Type = type };
                parent.SubStates.Add(existingSub);
            }
            else if (id == "[*]" && isTarget)
            {
                existingSub.Type = StateNodeType.End;
            }
            return existingSub;
        }

        if (!ast.States.TryGetValue(id, out var node))
        {
            node = new StateNode { Id = id, Label = id, Type = type };
            ast.States[id] = node;
        }
        else if (id == "[*]" && isTarget)
        {
            node.Type = StateNodeType.End;
        }

        return node;
    }
}
