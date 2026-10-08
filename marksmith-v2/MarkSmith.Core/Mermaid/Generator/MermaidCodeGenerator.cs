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

    // Every Mermaid statement is one line, so a raw line break inside a label splits the
    // statement and corrupts the diagram. The Studio's label boxes are WinUI TextBoxes, which
    // store a typed line break as a bare '\r' — so all three break forms must be handled.
    private static readonly System.Text.RegularExpressions.Regex LineBreak =
        new(@"\r\n|\r|\n", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BrTag =
        new(@"<br\s*/?>", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A label with its line breaks written as Mermaid's <c>&lt;br/&gt;</c>, for the
    /// places Mermaid renders it (node, edge, participant, message, note and state text).</summary>
    public static string ToBreakTags(string? text) => LineBreak.Replace(text ?? string.Empty, "<br/>");

    /// <summary>The reverse of <see cref="ToBreakTags"/>: <c>&lt;br/&gt;</c> variants become
    /// '\n' so the canvas shows a real multi-line label.</summary>
    public static string FromBreakTags(string? text) => BrTag.Replace(text ?? string.Empty, "\n");

    /// <summary>A label's lines, whichever line-break form it uses.</summary>
    public static string[] Lines(string? text) => LineBreak.Split(text ?? string.Empty);

    /// <summary>A label joined onto one line with spaces, for the places Mermaid has no line
    /// break syntax (titles, section and task names, ER and class text, mindmap nodes).</summary>
    public static string OneLine(string? text) =>
        string.Join(" ", LineBreak.Split(text ?? string.Empty).Select(s => s.Trim()).Where(s => s.Length > 0));

    private static void GenerateFlowchart(FlowchartDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine($"flowchart {ast.Direction}");
        if (!string.IsNullOrEmpty(ast.Title))
        {
            sb.AppendLine($"{indent}title {OneLine(ast.Title)}");
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

        // Styling after the nodes and edges it refers to (linkStyle counts edges in order).
        foreach (var line in ast.StyleLines)
            sb.AppendLine($"{indent}{line.Trim()}");
    }

    private static void GenerateSubgraph(FlowSubgraph sg, StringBuilder sb, string indent, int level, FlowchartDiagramAst ast, HashSet<string> emittedNodes)
    {
        string curIndent = string.Concat(Enumerable.Repeat(indent, level));
        sb.AppendLine($"{curIndent}subgraph {sg.Id} [\"{OneLine(sg.Title)}\"]");

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
        // Multi-line labels (from <br/> in the original source, or line breaks typed in the
        // visual editor) must be re-escaped on output — see LineBreak.
        text = ToBreakTags(text);
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
        string labelStr = hasLabel ? $" \"{ToBreakTags(edge.Label)}\" " : string.Empty;

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
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {OneLine(ast.Title)}");

        // Boxed participants are written together inside their box, where the first of them
        // was declared; Mermaid requires a box's participants to be declared inside it.
        var boxOf = new Dictionary<string, SequenceBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ast.Boxes)
            foreach (var id in b.ParticipantIds)
                boxOf.TryAdd(id, b);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ast.Participants)
        {
            if (written.Contains(p.Id) || p.CreatedInline) continue;
            if (boxOf.TryGetValue(p.Id, out var box))
            {
                sb.AppendLine($"{indent}box {OneLine(box.Header)}".TrimEnd());
                // A member introduced later with `create participant` is declared by that line.
                foreach (var member in ast.Participants.Where(q => !q.CreatedInline && boxOf.TryGetValue(q.Id, out var qb) && ReferenceEquals(qb, box)))
                {
                    sb.AppendLine($"{indent}{indent}{FormatParticipant(member)}");
                    written.Add(member.Id);
                }
                sb.AppendLine($"{indent}end");
                continue;
            }
            sb.AppendLine($"{indent}{FormatParticipant(p)}");
            written.Add(p.Id);
        }

        if (ast.Statements.Count > 0)
        {
            GenerateSequenceStatements(ast.Statements, sb, indent);
            return;
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
            sb.AppendLine($"{indent}Note {placement} {targets}: {ToBreakTags(note.Text)}");
        }

        foreach (var block in ast.Blocks)
        {
            sb.AppendLine($"{indent}{block.BlockType.ToString().ToLowerInvariant()} {OneLine(block.HeaderText)}".TrimEnd());
            foreach (var msg in block.Messages)
            {
                sb.AppendLine($"{indent}{indent}{FormatSequenceMessage(msg)}");
            }
            foreach (var elseBr in block.ElseBranches)
            {
                sb.AppendLine($"{indent}else {OneLine(elseBr.Condition)}".TrimEnd());
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

    /// <summary>Writes the body in its original order, indenting inside blocks. Dividers sit at
    /// their block's own depth, as Mermaid's docs write them.</summary>
    private static void GenerateSequenceStatements(IReadOnlyList<SequenceStatement> statements, StringBuilder sb, string indent)
    {
        int depth = 1;
        string Pad(int d) => string.Concat(Enumerable.Repeat(indent, Math.Max(1, d)));
        foreach (var st in statements)
        {
            switch (st.Kind)
            {
                case SequenceStatementKind.Message when st.Message is not null:
                    sb.AppendLine($"{Pad(depth)}{FormatSequenceMessage(st.Message)}");
                    break;
                case SequenceStatementKind.Note when st.Note is not null:
                    sb.AppendLine($"{Pad(depth)}{FormatSequenceNote(st.Note)}");
                    break;
                case SequenceStatementKind.Activate:
                    sb.AppendLine($"{Pad(depth)}activate {st.ParticipantId}");
                    break;
                case SequenceStatementKind.Deactivate:
                    sb.AppendLine($"{Pad(depth)}deactivate {st.ParticipantId}");
                    break;
                case SequenceStatementKind.BlockStart:
                    string keyword = string.IsNullOrEmpty(st.Keyword) ? st.BlockType.ToString().ToLowerInvariant() : st.Keyword;
                    sb.AppendLine($"{Pad(depth)}{keyword} {OneLine(st.Text)}".TrimEnd());
                    depth++;
                    break;
                case SequenceStatementKind.BlockDivider:
                    sb.AppendLine($"{Pad(depth - 1)}{(string.IsNullOrEmpty(st.Keyword) ? "else" : st.Keyword)} {OneLine(st.Text)}".TrimEnd());
                    break;
                case SequenceStatementKind.BlockEnd:
                    if (depth > 1) depth--;
                    sb.AppendLine($"{Pad(depth)}end");
                    break;
                case SequenceStatementKind.Raw when !string.IsNullOrWhiteSpace(st.Text):
                    sb.AppendLine($"{Pad(depth)}{st.Text.Trim()}");
                    break;
            }
        }
    }

    private static string FormatParticipant(SequenceParticipant p)
    {
        string keyword = p.Type == SequenceParticipantType.Actor ? "actor" : "participant";
        return p.Alias != p.Id && !string.IsNullOrEmpty(p.Alias)
            ? $"{keyword} {p.Id} as {ToBreakTags(p.Alias)}"
            : $"{keyword} {p.Id}";
    }

    private static string FormatSequenceNote(SequenceNote note)
    {
        string placement = note.Placement switch
        {
            NotePlacement.LeftOf => "left of",
            NotePlacement.RightOf => "right of",
            _ => "over"
        };
        return $"Note {placement} {string.Join(",", note.TargetParticipantIds)}: {ToBreakTags(note.Text)}";
    }

    private static string FormatSequenceMessage(SequenceMessage msg)
    {
        string arrow = msg.MessageType switch
        {
            SequenceMessageType.DashedArrow => "-->>",
            SequenceMessageType.SolidOpen => "->",
            SequenceMessageType.DashedOpen => "-->",
            SequenceMessageType.CrossArrow => "-x",
            SequenceMessageType.DashedCross => "--x",
            SequenceMessageType.PointArrow => "-)",
            SequenceMessageType.DashedPoint => "--)",
            _ => "->>"
        };

        string act = msg.ActivateTarget ? "+" : (msg.DeactivateTarget ? "-" : string.Empty);
        return $"{msg.FromId}{arrow}{act}{msg.ToId}: {ToBreakTags(msg.Text)}";
    }

    private static void GenerateClass(ClassDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("classDiagram");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {OneLine(ast.Title)}");

        foreach (var kvp in ast.Classes)
        {
            var cls = kvp.Value;
            sb.AppendLine($"{indent}class {cls.Name} {{");

            if (!string.IsNullOrEmpty(cls.Annotation))
            {
                sb.AppendLine($"{indent}{indent}{cls.Annotation}");
            }

            // (A whole-line Trim() here used to strip the indent off every member.)
            foreach (var attr in cls.Attributes)
                sb.AppendLine($"{indent}{indent}{FormatClassMember(attr)}");

            foreach (var m in cls.Methods)
                sb.AppendLine($"{indent}{indent}{FormatClassMember(m)}");

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
            string label = !string.IsNullOrEmpty(rel.Label) ? $" : {OneLine(rel.Label)}" : string.Empty;

            sb.AppendLine($"{indent}{rel.FromClass} {fromCard}{op}{toCard} {rel.ToClass}{label}");
        }

        foreach (var note in ast.NoteLines)
            sb.AppendLine($"{indent}{note.Trim()}");
    }

    private static string FormatVisibility(ClassVisibility vis) => vis switch
    {
        ClassVisibility.Public => "+",
        ClassVisibility.Private => "-",
        ClassVisibility.Protected => "#",
        ClassVisibility.Internal => "~",
        _ => string.Empty
    };

    /// <summary>One class member as Mermaid writes it inside a class body: <c>+String name</c>,
    /// <c>-save(int id) bool$</c>. Diagram Studio shows members in its boxes the same way.</summary>
    public static string FormatClassMember(ClassMember m)
    {
        string vis = FormatVisibility(m.Visibility);
        string flags = (m.IsStatic ? "$" : string.Empty) + (m.IsAbstract ? "*" : string.Empty);
        if (m.IsMethod)
        {
            string returnStr = !string.IsNullOrEmpty(m.Type) ? $" {m.Type}" : string.Empty;
            return $"{vis}{m.Name}({string.Join(", ", m.Parameters)}){returnStr}{flags}".Trim();
        }
        // "+String name"; with no type just "+name". (The old Replace(vis + " ", vis) removed
        // every space when there was no visibility, joining "String name" into "Stringname".)
        string typed = string.IsNullOrEmpty(m.Type) ? m.Name : $"{m.Type} {m.Name}";
        return $"{vis}{typed}{flags}".Trim();
    }

    private static void GenerateState(StateDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine(ast.IsV2 ? "stateDiagram-v2" : "stateDiagram");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {OneLine(ast.Title)}");

        // A note about a state inside a composite is written inside that composite: at the top
        // level Mermaid would read its state as a new, empty top-level one.
        var notesIn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var topNotes = new List<string>();
        foreach (var note in ast.Notes)
        {
            var owner = StateDiagramAst.NoteTarget(note) is { } target ? CompositeOwning(ast.States.Values, target) : null;
            if (owner is null) topNotes.Add(note);
            else (notesIn.TryGetValue(owner, out var list) ? list : notesIn[owner] = new List<string>()).Add(note);
        }

        foreach (var kvp in ast.States)
        {
            GenerateStateNode(kvp.Value, sb, indent, 1, notesIn);
        }

        foreach (var trans in ast.Transitions)
        {
            string evt = !string.IsNullOrEmpty(trans.EventLabel) ? $" : {OneLine(trans.EventLabel)}" : string.Empty;
            sb.AppendLine($"{indent}{trans.FromId} --> {trans.ToId}{evt}");
        }

        foreach (var note in topNotes) WriteStateNote(note, sb, indent, indent);
    }

    // The composite whose own states include `id` (searched depth first), or null at the top level.
    private static string? CompositeOwning(IEnumerable<StateNode> states, string id, string? owner = null)
    {
        foreach (var s in states)
        {
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return owner;
            if (CompositeOwning(s.SubStates, id, s.Id) is { } found) return found;
        }
        return null;
    }

    private static void WriteStateNote(string note, StringBuilder sb, string at, string indent)
    {
        var noteLines = note.Split('\n');
        for (int i = 0; i < noteLines.Length; i++)
        {
            bool body = noteLines.Length > 1 && i > 0 && i < noteLines.Length - 1;
            sb.AppendLine($"{at}{(body ? indent : string.Empty)}{noteLines[i].Trim()}");
        }
    }

    private static void GenerateStateNode(StateNode node, StringBuilder sb, string indent, int level,
        Dictionary<string, List<string>>? notesIn = null)
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
                GenerateStateNode(sub, sb, indent, level + 1, notesIn);
            }
            foreach (var trans in node.SubTransitions)
            {
                string evt = !string.IsNullOrEmpty(trans.EventLabel) ? $" : {OneLine(trans.EventLabel)}" : string.Empty;
                sb.AppendLine($"{curIndent}{indent}{trans.FromId} --> {trans.ToId}{evt}");
            }
            if (notesIn is not null && notesIn.TryGetValue(node.Id, out var notes))
                foreach (var note in notes) WriteStateNote(note, sb, curIndent + indent, indent);
            sb.AppendLine($"{curIndent}}}");
        }
        else if (!string.IsNullOrEmpty(node.Label) && node.Label != node.Id && node.Type == StateNodeType.Normal)
        {
            sb.AppendLine($"{curIndent}state \"{ToBreakTags(node.Label)}\" as {node.Id}");
        }
    }

    private static void GenerateGantt(GanttChartAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("gantt");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {OneLine(ast.Title)}");
        sb.AppendLine($"{indent}dateFormat {ast.DateFormat}");
        sb.AppendLine($"{indent}axisFormat {ast.AxisFormat}");

        foreach (var sec in ast.Sections)
        {
            sb.AppendLine($"{indent}section {OneLine(sec.Name)}");
            foreach (var task in sec.Tasks)
            {
                var flags = new List<string>();
                if (task.Status.HasFlag(GanttTaskStatus.Active)) flags.Add("active");
                if (task.Status.HasFlag(GanttTaskStatus.Done)) flags.Add("done");
                if (task.Status.HasFlag(GanttTaskStatus.Crit)) flags.Add("crit");
                if (task.IsMilestone) flags.Add("milestone");

                string flagsStr = flags.Count > 0 ? string.Join(", ", flags) + ", " : string.Empty;
                string startStr = !string.IsNullOrEmpty(task.StartDate) ? $"{task.StartDate}, " : string.Empty;

                sb.AppendLine($"{indent}{indent}{OneLine(task.Name)} :{flagsStr}{task.Id}, {startStr}{task.DurationOrEndDate}");
            }
        }
    }

    private static void GenerateEr(ErDiagramAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("erDiagram");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {OneLine(ast.Title)}");

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
                    string cmt = !string.IsNullOrEmpty(attr.Comment) ? $" \"{OneLine(attr.Comment)}\"" : string.Empty;
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
            string label = !string.IsNullOrEmpty(rel.RelationshipName) ? $" : \"{OneLine(rel.RelationshipName)}\"" : string.Empty;
            sb.AppendLine($"{indent}{rel.Entity1} {c1}{lineStyle}{c2} {rel.Entity2}{label}");
        }
    }

    private static string FormatErCardinality(ErCardinality card, bool isLeft)
    {
        return card switch
        {
            ErCardinality.ExactlyOne => "||",
            ErCardinality.ZeroOrOne => isLeft ? "|o" : "o|",
            // Mermaid's right-hand "many" ends open with "{": o{ and |{ ("o}" / "|}" there is
            // not Mermaid, and was read back as a different relationship).
            ErCardinality.ZeroOrMore => isLeft ? "}o" : "o{",
            ErCardinality.OneOrMore => isLeft ? "}|" : "|{",
            _ => "||"
        };
    }

    private static void GenerateMindmap(MindmapAst ast, StringBuilder sb, string indent)
    {
        sb.AppendLine("mindmap");
        if (!string.IsNullOrEmpty(ast.Title)) sb.AppendLine($"{indent}title {OneLine(ast.Title)}");

        if (ast.Root != null)
        {
            GenerateMindmapNode(ast.Root, sb, indent, 1);
        }
    }

    private static void GenerateMindmapNode(MindmapNode node, StringBuilder sb, string indent, int level)
    {
        string curIndent = string.Concat(Enumerable.Repeat(indent, level));
        string text = OneLine(node.Text);
        string textStr = node.Shape switch
        {
            MindmapNodeShape.Square => $"[{text}]",
            MindmapNodeShape.Rounded => $"({text})",
            MindmapNodeShape.Circle => strokeCircle(text),
            MindmapNodeShape.Cloud => $"){text}(",
            MindmapNodeShape.Bang => $")){text}((" ,
            _ => text
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
