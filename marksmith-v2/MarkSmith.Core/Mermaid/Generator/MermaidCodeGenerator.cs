namespace MarkSmith.Mermaid.Generator;

using System.Text;
using MarkSmith.Mermaid.Ast;

public static class MermaidCodeGenerator
{
    public static string Generate(MermaidDiagramAst ast, GeneratorOptions? options = null)
    {
        options ??= new GeneratorOptions();
        var sb = new StringBuilder();
        string indent = new(' ', options.IndentSpaces);

        // Append comments and directives first if any
        foreach (var comment in ast.Comments)
        {
            if (comment.TrimStart().StartsWith("%%"))
                sb.AppendLine(comment);
            else
                sb.AppendLine($"%% {comment}");
        }
        foreach (var dir in ast.Directives)
        {
            sb.AppendLine(dir);
        }

        switch (ast)
        {
            case FlowchartDiagramAst flowchart:
                GenerateFlowchart(flowchart, sb, indent);
                break;
            case SequenceDiagramAst sequence:
                GenerateSequence(sequence, sb, indent);
                break;
            case ClassDiagramAst classDiag:
                GenerateClass(classDiag, sb, indent);
                break;
            case StateDiagramAst stateDiag:
                GenerateState(stateDiag, sb, indent);
                break;
            case GanttChartAst gantt:
                GenerateGantt(gantt, sb, indent);
                break;
            case ErDiagramAst er:
                GenerateEr(er, sb, indent);
                break;
            case MindmapAst mindmap:
                GenerateMindmap(mindmap, sb, indent);
                break;
            default:
                throw new NotSupportedException($"Diagram type '{ast.DiagramType}' not supported.");
        }

        return sb.ToString().TrimEnd();
    }

    private static void GenerateFlowchart(FlowchartDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine($"flowchart {ast.Direction}");
        if (!string.IsNullOrEmpty(ast.Title))
        {
            sb.AppendLine($"{indent}title {ast.Title}");
        }

        var emittedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Subgraphs
        foreach (var sg in ast.Subgraphs)
        {
            GenerateSubgraph(sg, sb, indent, 1, ast, emittedNodes);
        }

        // Standalone nodes not in subgraphs
        foreach (var kvp in ast.Nodes)
        {
            if (!emittedNodes.Contains(kvp.Key) && kvp.Value.SubgraphId == null)
            {
                sb.AppendLine($"{indent}{FormatNode(kvp.Value)}");
                emittedNodes.Add(kvp.Key);
            }
        }

        // Edges
        foreach (var edge in ast.Edges)
        {
            string fromStr = ast.Nodes.TryGetValue(edge.FromId, out var fn) ? FormatNode(fn) : edge.FromId;
            string toStr = ast.Nodes.TryGetValue(edge.ToId, out var tn) ? FormatNode(tn) : edge.ToId;

            // Avoid repeating full shape format if already emitted
            string fromOutput = emittedNodes.Contains(edge.FromId) ? edge.FromId : fromStr;
            string toOutput = emittedNodes.Contains(edge.ToId) ? edge.ToId : toStr;

            emittedNodes.Add(edge.FromId);
            emittedNodes.Add(edge.ToId);

            string op = FormatEdgeOperator(edge);
            sb.AppendLine($"{indent}{fromOutput} {op} {toOutput}");
        }
    }

    private static void GenerateSubgraph(FlowSubgraph sg, StringBuilder sb, string indent, int level, FlowchartDiagramAst ast, HashSet<string> emittedNodes)
    {
        string curIndent = string.Concat(Enumerable.Repeat(indent, level));
        sb.AppendLine($"{curIndent}subgraph {sg.Id} [\"{sg.Title}\"]");

        foreach (var nodeId in sg.NodeIds)
        {
            if (ast.Nodes.TryGetValue(nodeId, out var node))
            {
                sb.AppendLine($"{curIndent}{indent}{FormatNode(node)}");
                emittedNodes.Add(nodeId);
            }
        }

        foreach (var nested in sg.NestedSubgraphs)
        {
            GenerateSubgraph(nested, sb, indent, level + 1, ast, emittedNodes);
        }

        sb.AppendLine($"{curIndent}end");
    }

    private static string FormatNode(FlowNode node)
    {
        string text = string.IsNullOrEmpty(node.Text) ? node.Id : node.Text;
        // Multi-line labels (from <br/> in the original source, or newlines typed in the
        // visual editor's inline TextBox) must be re-escaped on output: a raw newline inside
        // a node statement splits it across lines and corrupts the entire diagram.
        text = text.Replace("\r\n", "\n").Replace("\n", "<br/>");
        bool needsQuotes = text.Contains(" ") || text.Contains(":") || text.Contains("-") || text.Contains("<br/>");
        string labelStr = needsQuotes ? $"\"{text}\"" : text;

        return node.Shape switch
        {
            FlowNodeShape.RoundedRectangle => $"{node.Id}({labelStr})",
            FlowNodeShape.Stadium => $"{node.Id}([{labelStr}])",
            FlowNodeShape.Subroutine => $"{node.Id}[[{labelStr}]]",
            FlowNodeShape.CylindricalDatabase => $"{node.Id}[({labelStr})]",
            FlowNodeShape.Circle => $"{node.Id}(({labelStr}))",
            FlowNodeShape.Asymmetric => $"{node.Id}>{labelStr}]",
            FlowNodeShape.RhombusDiamond => $"{node.Id}{{{labelStr}}}",
            FlowNodeShape.Hexagon => $"{node.Id}{{{{{labelStr}}}}}",
            FlowNodeShape.Parallelogram => $"{node.Id}[/{labelStr}/]",
            FlowNodeShape.Trapezoid => $"{node.Id}[/{labelStr}\\]",
            _ => $"{node.Id}[{labelStr}]"
        };
    }

    private static string FormatEdgeOperator(FlowEdge edge)
    {
        if (edge.StartHead == FlowArrowHead.Cross && edge.EndHead == FlowArrowHead.Cross) return "x--x";
        if (edge.StartHead == FlowArrowHead.Circle && edge.EndHead == FlowArrowHead.Circle) return "o--o";
        if (edge.StartHead == FlowArrowHead.Normal && edge.EndHead == FlowArrowHead.Normal) return "<-->";

        bool hasLabel = !string.IsNullOrEmpty(edge.Label);
        string labelStr = hasLabel ? $" \"{edge.Label!.Replace("\r\n", "\n").Replace("\n", "<br/>")}\" " : string.Empty;

        return edge.LineStyle switch
        {
            FlowLineStyle.Thick => edge.EndHead == FlowArrowHead.Normal
                ? (hasLabel ? $"=={labelStr}==>" : "==>")
                : (hasLabel ? $"=={labelStr}===" : "==="),
            FlowLineStyle.Dashed => edge.EndHead == FlowArrowHead.Normal
                ? (hasLabel ? $"-.{labelStr}.->" : "-.->")
                : (hasLabel ? $"-.{labelStr}.-" : "-.-"),
            _ => edge.EndHead == FlowArrowHead.Normal
                ? (hasLabel ? $"--{labelStr}-->" : "-->")
                : (hasLabel ? $"--{labelStr}---" : "---")
        };
    }

    private static void GenerateSequence(SequenceDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("sequenceDiagram");
        if (ast.AutoNumber) sb.AppendLine($"{indent}autonumber");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {ast.Title}");

        foreach (var p in ast.Participants)
        {
            string keyword = p.Type == SequenceParticipantType.Actor ? "actor" : "participant";
            if (p.Alias != p.Id && !string.IsNullOrEmpty(p.Alias))
            {
                sb.AppendLine($"{indent}{keyword} {p.Id} as {p.Alias}");
            }
            else
            {
                sb.AppendLine($"{indent}{keyword} {p.Id}");
            }
        }

        foreach (var note in ast.Notes)
        {
            string targets = string.Join(",", note.TargetParticipantIds);
            string placement = note.Placement switch
            {
                NotePlacement.LeftOf => "left of",
                NotePlacement.RightOf => "right of",
                _ => "over"
            };
            sb.AppendLine($"{indent}Note {placement} {targets}: {note.Text}");
        }

        foreach (var block in ast.Blocks)
        {
            sb.AppendLine($"{indent}{block.BlockType.ToString().ToLowerInvariant()} {block.HeaderText}".TrimEnd());
            foreach (var msg in block.Messages)
            {
                sb.AppendLine($"{indent}{indent}{FormatSequenceMessage(msg)}");
            }
            foreach (var elseBr in block.ElseBranches)
            {
                sb.AppendLine($"{indent}else {elseBr.Condition}".TrimEnd());
                foreach (var msg in elseBr.Messages)
                {
                    sb.AppendLine($"{indent}{indent}{FormatSequenceMessage(msg)}");
                }
            }
            sb.AppendLine($"{indent}end");
        }

        foreach (var msg in ast.Messages)
        {
            sb.AppendLine($"{indent}{FormatSequenceMessage(msg)}");
        }
    }

    private static string FormatSequenceMessage(SequenceMessage msg)
    {
        string arrow = msg.MessageType switch
        {
            SequenceMessageType.DashedArrow => "-->>",
            SequenceMessageType.SolidOpen => "->",
            SequenceMessageType.DashedOpen => "-->",
            SequenceMessageType.CrossArrow => "-x",
            SequenceMessageType.PointArrow => "-\\",
            _ => "->>"
        };

        string act = msg.ActivateTarget ? "+" : (msg.DeactivateTarget ? "-" : string.Empty);
        return $"{msg.FromId}{arrow}{act}{msg.ToId}: {msg.Text}";
    }

    private static void GenerateClass(ClassDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("classDiagram");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {ast.Title}");

        foreach (var kvp in ast.Classes)
        {
            var cls = kvp.Value;
            sb.AppendLine($"{indent}class {cls.Name} {{");

            if (!string.IsNullOrEmpty(cls.Annotation))
            {
                sb.AppendLine($"{indent}{indent}{cls.Annotation}");
            }

            foreach (var attr in cls.Attributes)
            {
                string vis = FormatVisibility(attr.Visibility);
                string staticFlag = attr.IsStatic ? "$" : string.Empty;
                string abstractFlag = attr.IsAbstract ? "*" : string.Empty;
                sb.AppendLine($"{indent}{indent}{vis}{attr.Type} {attr.Name}{staticFlag}{abstractFlag}".Trim());
            }

            foreach (var m in cls.Methods)
            {
                string vis = FormatVisibility(m.Visibility);
                string paramsStr = string.Join(", ", m.Parameters);
                string returnStr = !string.IsNullOrEmpty(m.Type) ? $" {m.Type}" : string.Empty;
                string staticFlag = m.IsStatic ? "$" : string.Empty;
                string abstractFlag = m.IsAbstract ? "*" : string.Empty;
                sb.AppendLine($"{indent}{indent}{vis}{m.Name}({paramsStr}){returnStr}{staticFlag}{abstractFlag}".Trim());
            }

            sb.AppendLine($"{indent}}}");
        }

        foreach (var rel in ast.Relationships)
        {
            string op = rel.RelationshipType switch
            {
                ClassRelationshipType.Inheritance => "<|--",
                ClassRelationshipType.Realization => "<|..",
                ClassRelationshipType.Dependency => "..>",
                ClassRelationshipType.Aggregation => "o--",
                ClassRelationshipType.Composition => "*--",
                _ => "-->"
            };

            string fromCard = !string.IsNullOrEmpty(rel.FromCardinality) ? $"\"{rel.FromCardinality}\" " : string.Empty;
            string toCard = !string.IsNullOrEmpty(rel.ToCardinality) ? $" \"{rel.ToCardinality}\"" : string.Empty;
            string label = !string.IsNullOrEmpty(rel.Label) ? $" : {rel.Label}" : string.Empty;

            sb.AppendLine($"{indent}{rel.FromClass} {fromCard}{op}{toCard} {rel.ToClass}{label}");
        }
    }

    private static string FormatVisibility(ClassVisibility vis) => vis switch
    {
        ClassVisibility.Public => "+",
        ClassVisibility.Private => "-",
        ClassVisibility.Protected => "#",
        ClassVisibility.Internal => "~",
        _ => string.Empty
    };

    private static void GenerateState(StateDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine(ast.IsV2 ? "stateDiagram-v2" : "stateDiagram");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {ast.Title}");

        foreach (var kvp in ast.States)
        {
            GenerateStateNode(kvp.Value, sb, indent, 1);
        }

        foreach (var trans in ast.Transitions)
        {
            string evt = !string.IsNullOrEmpty(trans.EventLabel) ? $" : {trans.EventLabel}" : string.Empty;
            sb.AppendLine($"{indent}{trans.FromId} --> {trans.ToId}{evt}");
        }
    }

    private static void GenerateStateNode(StateNode node, StringBuilder sb, string indent, int level)
    {
        string curIndent = string.Concat(Enumerable.Repeat(indent, level));

        if (node.Type == StateNodeType.Choice)
        {
            sb.AppendLine($"{curIndent}state {node.Id} <<choice>>");
        }
        else if (node.Type == StateNodeType.Fork)
        {
            sb.AppendLine($"{curIndent}state {node.Id} <<fork>>");
        }
        else if (node.Type == StateNodeType.Join)
        {
            sb.AppendLine($"{curIndent}state {node.Id} <<join>>");
        }
        else if (node.Type == StateNodeType.Composite)
        {
            sb.AppendLine($"{curIndent}state {node.Id} {{");
            foreach (var sub in node.SubStates)
            {
                GenerateStateNode(sub, sb, indent, level + 1);
            }
            foreach (var trans in node.SubTransitions)
            {
                string evt = !string.IsNullOrEmpty(trans.EventLabel) ? $" : {trans.EventLabel}" : string.Empty;
                sb.AppendLine($"{curIndent}{indent}{trans.FromId} --> {trans.ToId}{evt}");
            }
            sb.AppendLine($"{curIndent}}}");
        }
        else if (!string.IsNullOrEmpty(node.Label) && node.Label != node.Id && node.Type == StateNodeType.Normal)
        {
            sb.AppendLine($"{curIndent}state \"{node.Label}\" as {node.Id}");
        }
    }

    private static void GenerateGantt(GanttChartAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("gantt");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {ast.Title}");
        sb.AppendLine($"{indent}dateFormat {ast.DateFormat}");
        sb.AppendLine($"{indent}axisFormat {ast.AxisFormat}");

        foreach (var sec in ast.Sections)
        {
            sb.AppendLine($"{indent}section {sec.Name}");
            foreach (var task in sec.Tasks)
            {
                var flags = new List<string>();
                if (task.Status.HasFlag(GanttTaskStatus.Active)) flags.Add("active");
                if (task.Status.HasFlag(GanttTaskStatus.Done)) flags.Add("done");
                if (task.Status.HasFlag(GanttTaskStatus.Crit)) flags.Add("crit");
                if (task.IsMilestone) flags.Add("milestone");

                string flagsStr = flags.Count > 0 ? string.Join(", ", flags) + ", " : string.Empty;
                string startStr = !string.IsNullOrEmpty(task.StartDate) ? $"{task.StartDate}, " : string.Empty;

                sb.AppendLine($"{indent}{indent}{task.Name} :{flagsStr}{task.Id}, {startStr}{task.DurationOrEndDate}");
            }
        }
    }

    private static void GenerateEr(ErDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("erDiagram");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {ast.Title}");

        foreach (var kvp in ast.Entities)
        {
            var entity = kvp.Value;
            if (entity.Attributes.Count > 0)
            {
                sb.AppendLine($"{indent}{entity.Name} {{");
                foreach (var attr in entity.Attributes)
                {
                    string pk = attr.IsPrimaryKey ? " PK" : string.Empty;
                    string fk = attr.IsForeignKey ? " FK" : string.Empty;
                    string cmt = !string.IsNullOrEmpty(attr.Comment) ? $" \"{attr.Comment}\"" : string.Empty;
                    sb.AppendLine($"{indent}{indent}{attr.Type} {attr.Name}{pk}{fk}{cmt}");
                }
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                sb.AppendLine($"{indent}{entity.Name}");
            }
        }

        foreach (var rel in ast.Relationships)
        {
            string c1 = FormatErCardinality(rel.Cardinality1, true);
            string c2 = FormatErCardinality(rel.Cardinality2, false);
            string lineStyle = rel.IsIdentifying ? "--" : "..";
            string label = !string.IsNullOrEmpty(rel.RelationshipName) ? $" : \"{rel.RelationshipName}\"" : string.Empty;
            sb.AppendLine($"{indent}{rel.Entity1} {c1}{lineStyle}{c2} {rel.Entity2}{label}");
        }
    }

    private static string FormatErCardinality(ErCardinality card, bool isLeft)
    {
        return card switch
        {
            ErCardinality.ExactlyOne => "||",
            ErCardinality.ZeroOrOne => isLeft ? "|o" : "o|",
            ErCardinality.ZeroOrMore => isLeft ? "}o" : "o}",
            ErCardinality.OneOrMore => isLeft ? "}|" : "|}",
            _ => "||"
        };
    }

    private static void GenerateMindmap(MindmapAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("mindmap");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {ast.Title}");

        if (ast.Root != null)
        {
            GenerateMindmapNode(ast.Root, sb, indent, 1);
        }
    }

    private static void GenerateMindmapNode(MindmapNode node, StringBuilder sb, string indent, int level)
    {
        string curIndent = string.Concat(Enumerable.Repeat(indent, level));
        string textStr = node.Shape switch
        {
            MindmapNodeShape.Square => $"[{node.Text}]",
            MindmapNodeShape.Rounded => $"({node.Text})",
            MindmapNodeShape.Circle => strokeCircle(node.Text),
            MindmapNodeShape.Cloud => $"){node.Text}(",
            MindmapNodeShape.Bang => $")){node.Text}((" ,
            _ => node.Text
        };

        string iconStr = !string.IsNullOrEmpty(node.Icon) ? $" {node.Icon}" : string.Empty;
        sb.AppendLine($"{curIndent}{textStr}{iconStr}");

        foreach (var child in node.Children)
        {
            GenerateMindmapNode(child, sb, indent, level + 1);
        }
    }

    private static string strokeCircle(string txt) => strokeWrap("((", txt, "))");
    private static string strokeWrap(string l, string t, string r) => $"{l}{t}{r}";
}
