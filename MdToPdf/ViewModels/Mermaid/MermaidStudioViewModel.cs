using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using MdToPdf.Mermaid.Parser;
using MdToPdf.Mermaid.Sync;

namespace MdToPdf.ViewModels.Mermaid;

public partial class MermaidStudioViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<DiagramNodeViewModel> _nodes = new();

    [ObservableProperty]
    private ObservableCollection<DiagramConnectorViewModel> _connectors = new();

    [ObservableProperty]
    private ObservableCollection<DiagramNodeViewModel> _selectedNodes = new();

    [ObservableProperty]
    private ObservableCollection<MermaidPaletteItem> _paletteItems = new();

    [ObservableProperty]
    private MermaidDiagramType _selectedDiagramType = MermaidDiagramType.Flowchart;

    [ObservableProperty]
    private FlowDirection _selectedFlowDirection = FlowDirection.TD;

    public List<FlowDirection> AvailableDirections { get; } = new()
    {
        FlowDirection.TD,
        FlowDirection.LR,
        FlowDirection.BT,
        FlowDirection.RL
    };

    partial void OnSelectedFlowDirectionChanged(FlowDirection value)
    {
        if (CurrentAst is FlowchartDiagramAst fc)
        {
            fc.Direction = value;
        }
        ApplyAutoLayout(force: true);
        StatusText = $"Flowchart direction set to {value}.";
    }

    [ObservableProperty]
    private DiagramNodeViewModel? _selectedNode;

    [ObservableProperty]
    private DiagramConnectorViewModel? _selectedConnector;

    [ObservableProperty]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private bool _isGridSnapEnabled = true;

    [ObservableProperty]
    private double _gridSnapSize = 10.0;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _activeBlockIndex = 0;

    [ObservableProperty]
    private int _totalBlockCount = 0;

    [ObservableProperty]
    private string _rawMermaidCode = string.Empty;

    public MermaidDiagramAst? CurrentAst { get; private set; }

    public MermaidStudioViewModel()
    {
        InitializePalette();
    }

    public void InitializePalette()
    {
        PaletteItems.Clear();

        // 1. Flowchart Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Rectangle Box", ShapeType = "Rectangle", DefaultText = "Process", IconGlyph = "\uE8A5" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Rounded Rectangle", ShapeType = "RoundedRectangle", DefaultText = "Start/End", IconGlyph = "\uE739" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Stadium (Pill)", ShapeType = "Stadium", DefaultText = "Terminal", IconGlyph = "\uE91B" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Subroutine", ShapeType = "Subroutine", DefaultText = "Subroutine", IconGlyph = "\uE8B9" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Database", ShapeType = "CylindricalDatabase", DefaultText = "Data Store", IconGlyph = "\uEAF5" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Circle", ShapeType = "Circle", DefaultText = "Node", IconGlyph = "\uEA3A" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Decision Rhombus", ShapeType = "Rhombus", DefaultText = "Decision?", IconGlyph = "\uE803" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Hexagon", ShapeType = "Hexagon", DefaultText = "Prepare", IconGlyph = "\uF0E2" });

        // 2. Sequence Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Sequence", DisplayName = "Actor", ShapeType = "Actor", DefaultText = "User", IconGlyph = "\uE77B" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Sequence", DisplayName = "Participant Box", ShapeType = "Participant", DefaultText = "Service", IconGlyph = "\uE8A5" });

        // 3. Class Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Class", DisplayName = "Class Box", ShapeType = "ClassBox", DefaultText = "Customer", IconGlyph = "\uE8A5" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Class", DisplayName = "Interface", ShapeType = "Interface", DefaultText = "IService", IconGlyph = "\uE896" });

        // 4. State Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "State", DisplayName = "State Node", ShapeType = "NormalState", DefaultText = "Idle", IconGlyph = "\uE739" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "State", DisplayName = "Choice State", ShapeType = "ChoiceState", DefaultText = "Choice", IconGlyph = "\uE803" });

        // 5. Gantt Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Gantt", DisplayName = "Task Bar", ShapeType = "TaskBar", DefaultText = "Design Phase", IconGlyph = "\uE91B" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Gantt", DisplayName = "Milestone", ShapeType = "Milestone", DefaultText = "Release 1.0", IconGlyph = "\uE735" });

        // 6. ER Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "ER", DisplayName = "Entity Box", ShapeType = "Entity", DefaultText = "ORDER", IconGlyph = "\uE8A5" });

        // 7. Mindmap Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Mindmap", DisplayName = "Central Theme", ShapeType = "RootNode", DefaultText = "Core Subject", IconGlyph = "\uEA3A" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Mindmap", DisplayName = "Branch Topic", ShapeType = "BranchNode", DefaultText = "Subtopic", IconGlyph = "\uE739" });
    }

    public void LoadFromMarkdown(string markdown, int blockIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            LoadDefaultSample();
            return;
        }

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        TotalBlockCount = blocks.Count;

        if (blocks.Count == 0)
        {
            LoadDefaultSample();
            return;
        }

        ActiveBlockIndex = Math.Clamp(blockIndex, 0, blocks.Count - 1);
        string code = blocks[ActiveBlockIndex].Code;
        LoadFromMermaidCode(code);
    }

    public void LoadFromMermaidCode(string code)
    {
        RawMermaidCode = code;
        var parseResult = MermaidParser.Parse(code);

        if (parseResult.Ast != null)
        {
            CurrentAst = parseResult.Ast;
            SelectedDiagramType = parseResult.Ast.DiagramType;
            AstToCanvas(parseResult.Ast);
            StatusText = $"Loaded {SelectedDiagramType} diagram ({Nodes.Count} nodes, {Connectors.Count} edges).";
        }
        else
        {
            StatusText = "Could not parse Mermaid diagram. Creating default canvas.";
            LoadDefaultSample();
        }
    }

    private void LoadDefaultSample()
    {
        SelectedDiagramType = MermaidDiagramType.Flowchart;
        Nodes.Clear();
        Connectors.Clear();

        var nodeA = new DiagramNodeViewModel { Id = "A", LabelText = "Start Process", Shape = "RoundedRectangle", X = 200, Y = 150, Width = 150, Height = 60, HasCustomPosition = true };
        var nodeB = new DiagramNodeViewModel { Id = "B", LabelText = "Check Conditions", Shape = "Rhombus", X = 200, Y = 280, Width = 160, Height = 80, HasCustomPosition = true };
        var nodeC = new DiagramNodeViewModel { Id = "C", LabelText = "Success Action", Shape = "Rectangle", X = 450, Y = 290, Width = 150, Height = 60, HasCustomPosition = true };

        Nodes.Add(nodeA);
        Nodes.Add(nodeB);
        Nodes.Add(nodeC);

        var conn1 = new DiagramConnectorViewModel { SourceNodeId = "A", SourceAnchor = "Bottom", TargetNodeId = "B", TargetAnchor = "Top", Label = "Initialize" };
        conn1.UpdateGeometry(nodeA.AnchorBottom, nodeB.AnchorTop);
        Connectors.Add(conn1);

        var conn2 = new DiagramConnectorViewModel { SourceNodeId = "B", SourceAnchor = "Right", TargetNodeId = "C", TargetAnchor = "Left", Label = "Valid" };
        conn2.UpdateGeometry(nodeB.AnchorRight, nodeC.AnchorLeft);
        Connectors.Add(conn2);

        StatusText = "Sample flowchart initialized.";
    }

    public void AstToCanvas(MermaidDiagramAst ast)
    {
        Nodes.Clear();
        Connectors.Clear();

        switch (ast)
        {
            case FlowchartDiagramAst flowchart:
                SelectedFlowDirection = flowchart.Direction;
                foreach (var kvp in flowchart.Nodes)
                {
                    var fn = kvp.Value;
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = fn.Id,
                        LabelText = string.IsNullOrEmpty(fn.Text) ? fn.Id : fn.Text,
                        Shape = fn.Shape.ToString(),
                        Category = "Flowchart"
                    });
                }
                foreach (var edge in flowchart.Edges)
                {
                    Connectors.Add(new DiagramConnectorViewModel
                    {
                        SourceNodeId = edge.FromId,
                        SourceAnchor = "Bottom",
                        TargetNodeId = edge.ToId,
                        TargetAnchor = "Top",
                        Label = edge.Label,
                        LineStyle = edge.LineStyle.ToString(),
                        EndHead = edge.EndHead.ToString()
                    });
                }
                break;

            case SequenceDiagramAst seq:
                foreach (var p in seq.Participants)
                {
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = p.Id,
                        LabelText = string.IsNullOrEmpty(p.Alias) ? p.Id : p.Alias,
                        Shape = p.Type.ToString(),
                        Category = "Sequence"
                    });
                }
                foreach (var msg in seq.Messages)
                {
                    Connectors.Add(new DiagramConnectorViewModel
                    {
                        SourceNodeId = msg.FromId,
                        SourceAnchor = "Right",
                        TargetNodeId = msg.ToId,
                        TargetAnchor = "Left",
                        Label = msg.Text,
                        LineStyle = msg.MessageType.ToString()
                    });
                }
                break;

            case ClassDiagramAst cls:
                foreach (var kvp in cls.Classes)
                {
                    var c = kvp.Value;
                    string label = c.Name;
                    if (c.Attributes.Count > 0)
                        label += "\n" + string.Join("\n", c.Attributes.Select(a => $"+{a.Name}: {a.Type}"));
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = c.Name,
                        LabelText = label,
                        Shape = "ClassBox",
                        Category = "Class"
                    });
                }
                foreach (var rel in cls.Relationships)
                {
                    Connectors.Add(new DiagramConnectorViewModel
                    {
                        SourceNodeId = rel.FromClass,
                        SourceAnchor = "Bottom",
                        TargetNodeId = rel.ToClass,
                        TargetAnchor = "Top",
                        Label = rel.Label,
                        EndHead = rel.RelationshipType.ToString()
                    });
                }
                break;

            case StateDiagramAst st:
                foreach (var kvp in st.States)
                {
                    var s = kvp.Value;
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = s.Id,
                        LabelText = string.IsNullOrEmpty(s.Label) ? s.Id : s.Label,
                        Shape = s.Type.ToString(),
                        Category = "State"
                    });
                }
                foreach (var tr in st.Transitions)
                {
                    Connectors.Add(new DiagramConnectorViewModel
                    {
                        SourceNodeId = tr.FromId,
                        SourceAnchor = "Right",
                        TargetNodeId = tr.ToId,
                        TargetAnchor = "Left",
                        Label = tr.EventLabel
                    });
                }
                break;

            case GanttChartAst gantt:
                int taskIdx = 0;
                foreach (var sec in gantt.Sections)
                {
                    foreach (var task in sec.Tasks)
                    {
                        Nodes.Add(new DiagramNodeViewModel
                        {
                            Id = string.IsNullOrEmpty(task.Id) ? $"task_{++taskIdx}" : task.Id,
                            LabelText = task.Name,
                            Shape = task.IsMilestone ? "Milestone" : "TaskBar",
                            Category = "Gantt"
                        });
                    }
                }
                break;

            case ErDiagramAst er:
                foreach (var kvp in er.Entities)
                {
                    var ent = kvp.Value;
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = ent.Name,
                        LabelText = ent.Name,
                        Shape = "Entity",
                        Category = "ER"
                    });
                }
                foreach (var rel in er.Relationships)
                {
                    Connectors.Add(new DiagramConnectorViewModel
                    {
                        SourceNodeId = rel.Entity1,
                        SourceAnchor = "Right",
                        TargetNodeId = rel.Entity2,
                        TargetAnchor = "Left",
                        Label = rel.RelationshipName
                    });
                }
                break;

            case MindmapAst mindmap:
                if (mindmap.Root != null)
                {
                    TraverseMindmap(mindmap.Root, null);
                }
                break;

        }

        var positions = MermaidMetadataService.ExtractPositions(ast.Comments);
        foreach (var node in Nodes)
        {
            if (positions.TryGetValue(node.Id, out var pos))
            {
                node.X = pos.X;
                node.Y = pos.Y;
                if (pos.Width.HasValue && pos.Width.Value > 0) node.Width = pos.Width.Value;
                if (pos.Height.HasValue && pos.Height.Value > 0) node.Height = pos.Height.Value;
                node.HasCustomPosition = true;
            }
        }

        ApplyAutoLayout();
    }

    private void TraverseMindmap(MindmapNode node, string? parentId)
    {
        Nodes.Add(new DiagramNodeViewModel
        {
            Id = node.Id,
            LabelText = node.Text,
            Shape = node.Shape.ToString(),
            Category = "Mindmap"
        });

        if (parentId != null)
        {
            Connectors.Add(new DiagramConnectorViewModel
            {
                SourceNodeId = parentId,
                SourceAnchor = "Bottom",
                TargetNodeId = node.Id,
                TargetAnchor = "Top"
            });
        }

        foreach (var child in node.Children)
        {
            TraverseMindmap(child, node.Id);
        }
    }

    public void ApplyAutoLayout(bool force = false)
    {
        if (Nodes.Count == 0) return;

        if (force)
        {
            foreach (var n in Nodes)
            {
                n.HasCustomPosition = false;
            }
        }

        if (SelectedDiagramType == MermaidDiagramType.Mindmap)
        {
            var hasParent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var childrenMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in Connectors)
            {
                if (!childrenMap.TryGetValue(c.SourceNodeId, out var list))
                {
                    list = new List<string>();
                    childrenMap[c.SourceNodeId] = list;
                }
                list.Add(c.TargetNodeId);
                hasParent.Add(c.TargetNodeId);
            }

            var rootVM = Nodes.FirstOrDefault(n => !hasParent.Contains(n.Id)) ?? Nodes[0];
            double centerX = 500;
            double centerY = 350;

            if (!rootVM.HasCustomPosition)
            {
                rootVM.X = centerX;
                rootVM.Y = centerY;
            }

            if (childrenMap.TryGetValue(rootVM.Id, out var level1Ids))
            {
                int n1 = level1Ids.Count;
                double r1 = 220;
                for (int i = 0; i < n1; i++)
                {
                    double angle = (2 * Math.PI * i) / n1;
                    var childVM = Nodes.FirstOrDefault(n => n.Id.Equals(level1Ids[i], StringComparison.OrdinalIgnoreCase));
                    if (childVM != null)
                    {
                        if (!childVM.HasCustomPosition)
                        {
                            childVM.X = centerX + r1 * Math.Cos(angle);
                            childVM.Y = centerY + r1 * Math.Sin(angle);
                        }

                        if (childrenMap.TryGetValue(childVM.Id, out var level2Ids))
                        {
                            int n2 = level2Ids.Count;
                            double r2 = 360;
                            double arc = Math.PI / 3;
                            double startArc = angle - arc / 2;
                            for (int j = 0; j < n2; j++)
                            {
                                double subAngle = n2 > 1 ? startArc + (arc * j) / (n2 - 1) : angle;
                                var grandChildVM = Nodes.FirstOrDefault(n => n.Id.Equals(level2Ids[j], StringComparison.OrdinalIgnoreCase));
                                if (grandChildVM != null && !grandChildVM.HasCustomPosition)
                                {
                                    grandChildVM.X = centerX + r2 * Math.Cos(subAngle);
                                    grandChildVM.Y = centerY + r2 * Math.Sin(subAngle);
                                }
                            }
                        }
                    }
                }
            }

            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootVM.Id };
            foreach (var kvp in childrenMap)
            {
                placed.Add(kvp.Key);
                foreach (var childId in kvp.Value) placed.Add(childId);
            }
            int extraIndex = 0;
            foreach (var node in Nodes)
            {
                if (!placed.Contains(node.Id) && !node.HasCustomPosition)
                {
                    node.X = 100 + (extraIndex % 4) * 180;
                    node.Y = 600 + (extraIndex / 4) * 100;
                    extraIndex++;
                }
            }
        }
        else if (SelectedDiagramType == MermaidDiagramType.Sequence)
        {
            double startX = 120;
            double startY = 100;
            double spacingX = 220;

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (!Nodes[i].HasCustomPosition)
                {
                    Nodes[i].X = startX + i * spacingX;
                    Nodes[i].Y = startY;
                    Nodes[i].Width = 140;
                    Nodes[i].Height = 50;
                }
            }
        }
        else if (SelectedDiagramType == MermaidDiagramType.Gantt)
        {
            double startX = 120;
            double startY = 100;
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (!Nodes[i].HasCustomPosition)
                {
                    Nodes[i].X = startX;
                    Nodes[i].Y = startY + i * 70;
                    Nodes[i].Width = 300;
                    Nodes[i].Height = 45;
                }
            }
        }
        else
        {
            var inDegree = Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);

            foreach (var c in Connectors)
            {
                if (inDegree.ContainsKey(c.TargetNodeId))
                {
                    inDegree[c.TargetNodeId]++;
                }
            }

            var ranks = Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();

            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0) queue.Enqueue(kvp.Key);
            }

            if (queue.Count == 0 && Nodes.Count > 0) queue.Enqueue(Nodes[0].Id);

            int ops = 0;
            while (queue.Count > 0 && ops < 10000)
            {
                ops++;
                var u = queue.Dequeue();
                int currentRank = ranks[u];
                
                // Prevent infinite loop if the diagram contains cycles
                if (currentRank > Nodes.Count + 5)
                {
                    continue;
                }
                
                var outgoing = Connectors.Where(c => c.SourceNodeId.Equals(u, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var edge in outgoing)
                {
                    if (ranks.ContainsKey(edge.TargetNodeId))
                    {
                        if (ranks[edge.TargetNodeId] < currentRank + 1)
                        {
                            ranks[edge.TargetNodeId] = currentRank + 1;
                            queue.Enqueue(edge.TargetNodeId);
                        }
                    }
                }
            }

            var layers = new Dictionary<int, List<DiagramNodeViewModel>>();
            foreach (var n in Nodes)
            {
                int r = ranks[n.Id];
                if (!layers.TryGetValue(r, out var list))
                {
                    list = new List<DiagramNodeViewModel>();
                    layers[r] = list;
                }
                list.Add(n);
            }

            double startX = 100;
            double startY = 100;
            double layerSpacingY = 160;
            double nodeSpacingX = 200;
            double layerSpacingX = 220;
            double nodeSpacingY = 120;

            int maxRank = layers.Keys.Count > 0 ? layers.Keys.Max() : 0;

            foreach (var kvp in layers.OrderBy(l => l.Key))
            {
                int rank = kvp.Key;
                var layerNodes = kvp.Value;

                switch (SelectedFlowDirection)
                {
                    case FlowDirection.LR:
                        double layerXLR = startX + rank * layerSpacingX;
                        double totalHeightLR = (layerNodes.Count - 1) * nodeSpacingY;
                        double startYLR = Math.Max(100, 350 - totalHeightLR / 2);
                        for (int j = 0; j < layerNodes.Count; j++)
                        {
                            if (!layerNodes[j].HasCustomPosition)
                            {
                                layerNodes[j].X = layerXLR;
                                layerNodes[j].Y = startYLR + j * nodeSpacingY;
                            }
                        }
                        break;

                    case FlowDirection.RL:
                        double layerXRL = startX + (maxRank - rank) * layerSpacingX;
                        double totalHeightRL = (layerNodes.Count - 1) * nodeSpacingY;
                        double startYRL = Math.Max(100, 350 - totalHeightRL / 2);
                        for (int j = 0; j < layerNodes.Count; j++)
                        {
                            if (!layerNodes[j].HasCustomPosition)
                            {
                                layerNodes[j].X = layerXRL;
                                layerNodes[j].Y = startYRL + j * nodeSpacingY;
                            }
                        }
                        break;

                    case FlowDirection.BT:
                        double layerYBT = startY + (maxRank - rank) * layerSpacingY;
                        double totalWidthBT = (layerNodes.Count - 1) * nodeSpacingX;
                        double startXBT = Math.Max(100, 500 - totalWidthBT / 2);
                        for (int j = 0; j < layerNodes.Count; j++)
                        {
                            if (!layerNodes[j].HasCustomPosition)
                            {
                                layerNodes[j].X = startXBT + j * nodeSpacingX;
                                layerNodes[j].Y = layerYBT;
                            }
                        }
                        break;

                    case FlowDirection.TD:
                    case FlowDirection.TB:
                    default:
                        double layerY = startY + rank * layerSpacingY;
                        double totalWidth = (layerNodes.Count - 1) * nodeSpacingX;
                        double startXTD = Math.Max(100, 500 - totalWidth / 2);

                        for (int j = 0; j < layerNodes.Count; j++)
                        {
                            if (!layerNodes[j].HasCustomPosition)
                            {
                                layerNodes[j].X = startXTD + j * nodeSpacingX;
                                layerNodes[j].Y = layerY;
                            }
                        }
                        break;
                }
            }
        }

        UpdateAllConnectors();
    }

    public void UpdateConnectedConnectors(DiagramNodeViewModel node)
    {
        foreach (var conn in Connectors)
        {
            if (conn.SourceNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase) ||
                conn.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            {
                UpdateConnectorGeometry(conn);
            }
        }
    }

    public void UpdateAllConnectors()
    {
        foreach (var conn in Connectors)
        {
            UpdateConnectorGeometry(conn);
        }
    }

    public void SelectNode(DiagramNodeViewModel node, bool isMultiSelect = false)
    {
        if (!isMultiSelect)
        {
            foreach (var n in Nodes) n.IsSelected = false;
            SelectedNodes.Clear();
            node.IsSelected = true;
            SelectedNodes.Add(node);
            SelectedNode = node;
            SelectedConnector = null;
        }
        else
        {
            if (SelectedNodes.Contains(node))
            {
                node.IsSelected = false;
                SelectedNodes.Remove(node);
                SelectedNode = SelectedNodes.LastOrDefault();
            }
            else
            {
                node.IsSelected = true;
                SelectedNodes.Add(node);
                SelectedNode = node;
                SelectedConnector = null;
            }
        }
    }

    public void SelectNodesInRect(Rect bounds, bool isAdditive = false)
    {
        if (!isAdditive)
        {
            foreach (var n in Nodes) n.IsSelected = false;
            SelectedNodes.Clear();
        }

        foreach (var node in Nodes)
        {
            var nodeRect = new Rect(node.X, node.Y, node.Width, node.Height);
            bool intersects = bounds.IntersectsWith(nodeRect);

            if (intersects)
            {
                if (!node.IsSelected)
                {
                    node.IsSelected = true;
                    if (!SelectedNodes.Contains(node))
                    {
                        SelectedNodes.Add(node);
                    }
                }
            }
        }

        SelectedNode = SelectedNodes.FirstOrDefault();
        if (SelectedNodes.Count > 0) SelectedConnector = null;
    }

    public void MoveSelectedNodes(double deltaX, double deltaY)
    {
        var selectedIds = new HashSet<string>(SelectedNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var node in SelectedNodes)
        {
            node.X = Math.Max(10, node.X + deltaX);
            node.Y = Math.Max(10, node.Y + deltaY);
        }

        foreach (var conn in Connectors)
        {
            bool srcMoved = selectedIds.Contains(conn.SourceNodeId);
            bool tgtMoved = selectedIds.Contains(conn.TargetNodeId);

            if (srcMoved && tgtMoved)
            {
                // Both source and target nodes are moving together: translate route points directly
                conn.TranslateGeometry(deltaX, deltaY);
            }
            else if (srcMoved || tgtMoved)
            {
                UpdateConnectorGeometry(conn);
            }
        }
    }

    public DiagramNodeViewModel QuickAddNode(DiagramNodeViewModel sourceNode, string direction)
    {
        int counter = Nodes.Count + 1;
        string id = $"node_{counter}";
        while (Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            counter++;
            id = $"node_{counter}";
        }

        double dirX = 0;
        double dirY = 0;
        double advanceStep = 160;
        double newX = sourceNode.X;
        double newY = sourceNode.Y;
        string sourceAnchor = "Right";
        string targetAnchor = "Left";

        switch (direction?.ToLowerInvariant())
        {
            case "top":
            case "up":
                dirY = -1;
                advanceStep = 120;
                newY = Math.Max(20, sourceNode.Y - advanceStep);
                sourceAnchor = "Top";
                targetAnchor = "Bottom";
                break;
            case "right":
                dirX = 1;
                advanceStep = sourceNode.Width > 0 ? sourceNode.Width + 80 : 160;
                newX = sourceNode.X + advanceStep;
                sourceAnchor = "Right";
                targetAnchor = "Left";
                break;
            case "bottom":
            case "down":
                dirY = 1;
                advanceStep = sourceNode.Height > 0 ? sourceNode.Height + 60 : 120;
                newY = sourceNode.Y + advanceStep;
                sourceAnchor = "Bottom";
                targetAnchor = "Top";
                break;
            case "left":
                dirX = -1;
                advanceStep = 160;
                newX = Math.Max(20, sourceNode.X - advanceStep);
                sourceAnchor = "Left";
                targetAnchor = "Right";
                break;
            default:
                dirX = 1;
                advanceStep = sourceNode.Width > 0 ? sourceNode.Width + 80 : 160;
                newX = sourceNode.X + advanceStep;
                sourceAnchor = "Right";
                targetAnchor = "Left";
                break;
        }

        double nodeWidth = sourceNode.Width > 0 ? sourceNode.Width : 140;
        double nodeHeight = sourceNode.Height > 0 ? sourceNode.Height : 60;

        // Collision-avoidance check to iteratively advance spawn coordinates along direction vector if target space intersects an existing node
        int attempts = 0;
        while (attempts < 50)
        {
            var candidateRect = new Rect(newX, newY, nodeWidth, nodeHeight);
            bool hasCollision = Nodes.Any(n => candidateRect.IntersectsWith(new Rect(n.X, n.Y, n.Width, n.Height)));
            if (!hasCollision)
            {
                break;
            }

            if (dirX > 0) newX += advanceStep;
            else if (dirX < 0) newX = Math.Max(20, newX - advanceStep);
            else if (dirY > 0) newY += advanceStep;
            else if (dirY < 0) newY = Math.Max(20, newY - advanceStep);

            attempts++;
        }

        var newNode = new DiagramNodeViewModel
        {
            Id = id,
            LabelText = "New Node",
            Category = sourceNode.Category,
            Shape = sourceNode.Shape,
            X = newX,
            Y = newY,
            Width = nodeWidth,
            Height = nodeHeight,
            HasCustomPosition = true
        };

        Nodes.Add(newNode);
        AddConnector(sourceNode.Id, sourceAnchor, newNode.Id, targetAnchor);
        SelectNode(newNode, false);
        StatusText = $"Quick-added node '{newNode.LabelText}' ({direction}).";
        return newNode;
    }

    public void UpdateConnectorGeometry(DiagramConnectorViewModel conn)
    {
        var srcNode = Nodes.FirstOrDefault(n => n.Id.Equals(conn.SourceNodeId, StringComparison.OrdinalIgnoreCase));
        var tgtNode = Nodes.FirstOrDefault(n => n.Id.Equals(conn.TargetNodeId, StringComparison.OrdinalIgnoreCase));

        if (srcNode != null && tgtNode != null)
        {
            string srcAnchor = conn.SourceAnchor;
            string tgtAnchor = conn.TargetAnchor;

            double dx = tgtNode.X - srcNode.X;
            double dy = tgtNode.Y - srcNode.Y;

            if (Math.Abs(dy) > Math.Abs(dx))
            {
                if (dy > 0) { srcAnchor = "Bottom"; tgtAnchor = "Top"; }
                else { srcAnchor = "Top"; tgtAnchor = "Bottom"; }
            }
            else
            {
                if (dx > 0) { srcAnchor = "Right"; tgtAnchor = "Left"; }
                else { srcAnchor = "Left"; tgtAnchor = "Right"; }
            }

            Point p1 = srcNode.GetAnchorPoint(srcAnchor);
            Point p2 = tgtNode.GetAnchorPoint(tgtAnchor);
            conn.SourceAnchor = srcAnchor;
            conn.TargetAnchor = tgtAnchor;

            var srcBounds = new Rect(srcNode.X, srcNode.Y, srcNode.Width, srcNode.Height);
            var tgtBounds = new Rect(tgtNode.X, tgtNode.Y, tgtNode.Width, tgtNode.Height);
            var obstacleBounds = Nodes
                .Where(n => !n.Id.Equals(srcNode.Id, StringComparison.OrdinalIgnoreCase) && !n.Id.Equals(tgtNode.Id, StringComparison.OrdinalIgnoreCase))
                .Select(n => new Rect(n.X, n.Y, n.Width, n.Height));

            conn.UpdateGeometry(p1, p2, srcBounds, tgtBounds, obstacleBounds);
        }
    }

    public void AddNodeFromPalette(MermaidPaletteItem item, double x, double y)
    {
        int counter = Nodes.Count + 1;
        string id = $"node_{counter}";
        while (Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            counter++;
            id = $"node_{counter}";
        }

        double snapX = IsGridSnapEnabled ? Math.Round(x / GridSnapSize) * GridSnapSize : x;
        double snapY = IsGridSnapEnabled ? Math.Round(y / GridSnapSize) * GridSnapSize : y;

        var node = new DiagramNodeViewModel
        {
            Id = id,
            LabelText = item.DefaultText,
            Category = item.Category,
            Shape = item.ShapeType,
            X = Math.Max(20, snapX),
            Y = Math.Max(20, snapY),
            Width = item.ShapeType == "TaskBar" ? 260 : 140,
            Height = 60
        };

        Nodes.Add(node);
        SelectNode(node, false);
        StatusText = $"Added {node.Shape} node '{node.LabelText}' at ({node.X:F0}, {node.Y:F0}).";
    }

    public void AddConnector(string sourceId, string sourceAnchor, string targetId, string targetAnchor)
    {
        var conn = new DiagramConnectorViewModel
        {
            SourceNodeId = sourceId,
            SourceAnchor = sourceAnchor,
            TargetNodeId = targetId,
            TargetAnchor = targetAnchor,
            LineStyle = "Solid",
            EndHead = "Normal"
        };
        UpdateConnectorGeometry(conn);
        Connectors.Add(conn);
        SelectedConnector = conn;
        StatusText = $"Connected {sourceId} -> {targetId}.";
    }

    [RelayCommand]
    public void DeleteSelected()
    {
        if (SelectedNodes.Count > 0 || SelectedNode != null)
        {
            var nodesToDelete = SelectedNodes.Count > 0 ? SelectedNodes.ToList() : new List<DiagramNodeViewModel> { SelectedNode! };
            foreach (var nodeToDelete in nodesToDelete)
            {
                Nodes.Remove(nodeToDelete);
                var connected = Connectors.Where(c => c.SourceNodeId == nodeToDelete.Id || c.TargetNodeId == nodeToDelete.Id).ToList();
                foreach (var c in connected) Connectors.Remove(c);
            }

            SelectedNodes.Clear();
            SelectedNode = null;
            StatusText = $"Deleted {nodesToDelete.Count} node(s).";
        }
        else if (SelectedConnector != null)
        {
            var connToDelete = SelectedConnector;
            Connectors.Remove(connToDelete);
            SelectedConnector = null;
            StatusText = "Deleted connector.";
        }
    }

    public MermaidDiagramAst CanvasToAst()
    {
        var ast = BuildRawAstFromCanvas();

        if (CurrentAst != null && CurrentAst.Comments.Count > 0)
        {
            foreach (var c in CurrentAst.Comments)
            {
                if (!ast.Comments.Contains(c))
                {
                    ast.Comments.Add(c);
                }
            }
        }

        var positions = Nodes.Select(n => new NodePositionMetadata
        {
            Id = n.Id,
            X = n.X,
            Y = n.Y,
            Width = n.Width,
            Height = n.Height
        });

        MermaidMetadataService.InjectPositions(ast, positions);
        return ast;
    }

    private MermaidDiagramAst BuildRawAstFromCanvas()
    {
        switch (SelectedDiagramType)
        {
            case MermaidDiagramType.Flowchart:
                var flowchart = new FlowchartDiagramAst { Direction = SelectedFlowDirection };
                foreach (var n in Nodes)
                {
                    FlowNodeShape shape;
                    if (string.Equals(n.Shape, "Rhombus", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(n.Shape, "Diamond", StringComparison.OrdinalIgnoreCase))
                    {
                        shape = FlowNodeShape.RhombusDiamond;
                    }
                    else if (!Enum.TryParse<FlowNodeShape>(n.Shape, true, out shape))
                    {
                        shape = FlowNodeShape.Rectangle;
                    }

                    flowchart.Nodes[n.Id] = new FlowNode
                    {
                        Id = n.Id,
                        Text = n.LabelText,
                        Shape = shape
                    };
                }
                foreach (var c in Connectors)
                {
                    Enum.TryParse<FlowLineStyle>(c.LineStyle, true, out var lineStyle);
                    Enum.TryParse<FlowArrowHead>(c.EndHead, true, out var endHead);
                    flowchart.Edges.Add(new FlowEdge
                    {
                        FromId = c.SourceNodeId,
                        ToId = c.TargetNodeId,
                        Label = c.Label,
                        LineStyle = lineStyle,
                        EndHead = endHead
                    });
                }
                return flowchart;

            case MermaidDiagramType.Sequence:
                var seq = new SequenceDiagramAst();
                var existingSeq = CurrentAst as SequenceDiagramAst;
                foreach (var n in Nodes)
                {
                    seq.Participants.Add(new SequenceParticipant
                    {
                        Id = n.Id,
                        Alias = n.LabelText,
                        Type = string.Equals(n.Shape, "Actor", StringComparison.OrdinalIgnoreCase) ? SequenceParticipantType.Actor : SequenceParticipantType.Participant
                    });
                }
                foreach (var c in Connectors)
                {
                    Enum.TryParse<SequenceMessageType>(c.LineStyle, true, out var msgType);
                    seq.Messages.Add(new SequenceMessage
                    {
                        FromId = c.SourceNodeId,
                        ToId = c.TargetNodeId,
                        Text = c.Label ?? string.Empty,
                        MessageType = msgType
                    });
                }
                if (existingSeq != null)
                {
                    foreach (var block in existingSeq.Blocks) seq.Blocks.Add(block);
                    foreach (var note in existingSeq.Notes) seq.Notes.Add(note);
                    seq.AutoNumber = existingSeq.AutoNumber;
                }
                return seq;

            case MermaidDiagramType.Class:
                var cls = new ClassDiagramAst();
                var existingCls = CurrentAst as ClassDiagramAst;
                foreach (var n in Nodes)
                {
                    var classNode = new ClassNode { Name = n.Id };
                    var lines = n.LabelText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 1)
                    {
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var line = lines[i].Trim();
                            if (i == 0 && (line.Equals(n.Id, StringComparison.OrdinalIgnoreCase) || (!line.StartsWith("+") && !line.StartsWith("-") && !line.StartsWith("#") && !line.StartsWith("~"))))
                            {
                                if (line.StartsWith("<<") && line.EndsWith(">>")) classNode.Annotation = line;
                                else classNode.Name = line;
                                continue;
                            }
                            if (line.StartsWith("<<") && line.EndsWith(">>"))
                            {
                                classNode.Annotation = line;
                                continue;
                            }
                            ParseClassMember(line, classNode);
                        }
                    }
                    else if (existingCls != null && existingCls.Classes.TryGetValue(n.Id, out var exClass))
                    {
                        classNode.Name = exClass.Name;
                        classNode.Annotation = exClass.Annotation;
                        foreach (var attr in exClass.Attributes) classNode.Attributes.Add(attr);
                        foreach (var m in exClass.Methods) classNode.Methods.Add(m);
                    }
                    else
                    {
                        var line = n.LabelText.Trim();
                        if (line.Contains("+") || line.Contains("-") || line.Contains("#") || line.Contains("~"))
                        {
                            ParseClassMember(line, classNode);
                        }
                    }
                    cls.Classes[n.Id] = classNode;
                }
                foreach (var c in Connectors)
                {
                    ClassRelationshipType relType = ClassRelationshipType.Association;
                    if (Enum.TryParse<ClassRelationshipType>(c.EndHead, true, out var parsedRel))
                    {
                        relType = parsedRel;
                    }
                    cls.Relationships.Add(new ClassRelationship
                    {
                        FromClass = c.SourceNodeId,
                        ToClass = c.TargetNodeId,
                        Label = c.Label,
                        RelationshipType = relType
                    });
                }
                return cls;

            case MermaidDiagramType.State:
                var state = new StateDiagramAst();
                foreach (var n in Nodes)
                {
                    StateNodeType type = StateNodeType.Normal;
                    if (string.Equals(n.Shape, "ChoiceState", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(n.Shape, "Choice", StringComparison.OrdinalIgnoreCase) ||
                        n.LabelText.Contains("<<choice>>", StringComparison.OrdinalIgnoreCase))
                    {
                        type = StateNodeType.Choice;
                    }
                    else if (string.Equals(n.Shape, "Fork", StringComparison.OrdinalIgnoreCase) || n.LabelText.Contains("<<fork>>", StringComparison.OrdinalIgnoreCase))
                    {
                        type = StateNodeType.Fork;
                    }
                    else if (string.Equals(n.Shape, "Join", StringComparison.OrdinalIgnoreCase) || n.LabelText.Contains("<<join>>", StringComparison.OrdinalIgnoreCase))
                    {
                        type = StateNodeType.Join;
                    }
                    else if (string.Equals(n.Shape, "Start", StringComparison.OrdinalIgnoreCase) || n.Id == "[*]")
                    {
                        type = StateNodeType.Start;
                    }
                    else if (string.Equals(n.Shape, "End", StringComparison.OrdinalIgnoreCase))
                    {
                        type = StateNodeType.End;
                    }
                    else if (CurrentAst is StateDiagramAst existingState && existingState.States.TryGetValue(n.Id, out var exNode))
                    {
                        type = exNode.Type;
                    }

                    string cleanLabel = n.LabelText.Replace("<<choice>>", "").Replace("<<fork>>", "").Replace("<<join>>", "").Trim();
                    if (string.IsNullOrEmpty(cleanLabel)) cleanLabel = n.Id;

                    state.States[n.Id] = new StateNode { Id = n.Id, Label = cleanLabel, Type = type };
                }
                foreach (var c in Connectors)
                {
                    state.Transitions.Add(new StateTransition
                    {
                        FromId = c.SourceNodeId,
                        ToId = c.TargetNodeId,
                        EventLabel = c.Label
                    });
                }
                return state;

            case MermaidDiagramType.Gantt:
                var gantt = new GanttChartAst();
                var existingGantt = CurrentAst as GanttChartAst;
                var sectionMap = new Dictionary<string, GanttSection>(StringComparer.OrdinalIgnoreCase);

                foreach (var n in Nodes)
                {
                    bool isMilestone = string.Equals(n.Shape, "Milestone", StringComparison.OrdinalIgnoreCase) || n.LabelText.Contains(":milestone");

                    GanttTask task;
                    string secName = "Tasks";

                    GanttTask? exTask = null;
                    if (existingGantt != null)
                    {
                        foreach (var sec in existingGantt.Sections)
                        {
                            var match = sec.Tasks.FirstOrDefault(t => t.Id.Equals(n.Id, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                exTask = match;
                                secName = sec.Name;
                                break;
                            }
                        }
                    }

                    if (exTask != null)
                    {
                        task = new GanttTask
                        {
                            Id = n.Id,
                            Name = n.LabelText,
                            Status = exTask.Status,
                            IsMilestone = isMilestone || exTask.IsMilestone,
                            StartDate = exTask.StartDate,
                            DurationOrEndDate = exTask.DurationOrEndDate,
                            AfterTaskId = exTask.AfterTaskId
                        };
                    }
                    else
                    {
                        task = new GanttTask
                        {
                            Id = n.Id,
                            Name = n.LabelText,
                            IsMilestone = isMilestone,
                            DurationOrEndDate = isMilestone ? "0d" : "5d"
                        };
                    }

                    if (!sectionMap.TryGetValue(secName, out var section))
                    {
                        section = new GanttSection { Name = secName };
                        sectionMap[secName] = section;
                        gantt.Sections.Add(section);
                    }
                    section.Tasks.Add(task);
                }
                if (gantt.Sections.Count == 0)
                {
                    gantt.Sections.Add(new GanttSection { Name = "Tasks" });
                }
                return gantt;

            case MermaidDiagramType.Er:
                var er = new ErDiagramAst();
                var existingEr = CurrentAst as ErDiagramAst;

                foreach (var n in Nodes)
                {
                    var entity = new ErEntity { Name = n.Id };
                    var lines = n.LabelText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 1)
                    {
                        for (int i = 1; i < lines.Length; i++)
                        {
                            var line = lines[i].Trim();
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                bool isPk = line.Contains("PK", StringComparison.OrdinalIgnoreCase);
                                bool isFk = line.Contains("FK", StringComparison.OrdinalIgnoreCase);
                                string? comment = null;
                                if (line.Contains("\""))
                                {
                                    int q1 = line.IndexOf('"');
                                    int q2 = line.LastIndexOf('"');
                                    if (q2 > q1) comment = line.Substring(q1 + 1, q2 - q1 - 1);
                                }
                                entity.Attributes.Add(new ErAttribute
                                {
                                    Type = parts[0],
                                    Name = parts[1],
                                    IsPrimaryKey = isPk,
                                    IsForeignKey = isFk,
                                    Comment = comment
                                });
                            }
                        }
                    }
                    else if (existingEr != null && existingEr.Entities.TryGetValue(n.Id, out var exEnt))
                    {
                        foreach (var attr in exEnt.Attributes)
                        {
                            entity.Attributes.Add(new ErAttribute
                            {
                                Type = attr.Type,
                                Name = attr.Name,
                                IsPrimaryKey = attr.IsPrimaryKey,
                                IsForeignKey = attr.IsForeignKey,
                                Comment = attr.Comment
                            });
                        }
                    }

                    er.Entities[n.Id] = entity;
                }

                foreach (var c in Connectors)
                {
                    ErRelationship rel;
                    ErRelationship? exRel = existingEr?.Relationships.FirstOrDefault(r =>
                        r.Entity1.Equals(c.SourceNodeId, StringComparison.OrdinalIgnoreCase) &&
                        r.Entity2.Equals(c.TargetNodeId, StringComparison.OrdinalIgnoreCase));

                    if (exRel != null)
                    {
                        rel = new ErRelationship
                        {
                            Entity1 = c.SourceNodeId,
                            Entity2 = c.TargetNodeId,
                            Cardinality1 = exRel.Cardinality1,
                            Cardinality2 = exRel.Cardinality2,
                            IsIdentifying = exRel.IsIdentifying,
                            RelationshipName = c.Label ?? exRel.RelationshipName
                        };
                    }
                    else
                    {
                        rel = new ErRelationship
                        {
                            Entity1 = c.SourceNodeId,
                            Entity2 = c.TargetNodeId,
                            Cardinality1 = ErCardinality.ExactlyOne,
                            Cardinality2 = ErCardinality.ZeroOrMore,
                            IsIdentifying = true,
                            RelationshipName = c.Label ?? string.Empty
                        };
                    }
                    er.Relationships.Add(rel);
                }
                return er;

            case MermaidDiagramType.Mindmap:
                var mindmap = new MindmapAst();
                if (Nodes.Count > 0)
                {
                    var nodeMap = new Dictionary<string, MindmapNode>(StringComparer.OrdinalIgnoreCase);
                    foreach (var n in Nodes)
                    {
                        Enum.TryParse<MindmapNodeShape>(n.Shape, true, out var shape);
                        nodeMap[n.Id] = new MindmapNode
                        {
                            Id = n.Id,
                            Text = n.LabelText,
                            Shape = shape
                        };
                    }

                    var childrenMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var hasParent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var c in Connectors)
                    {
                        if (nodeMap.ContainsKey(c.SourceNodeId) && nodeMap.ContainsKey(c.TargetNodeId))
                        {
                            if (!childrenMap.TryGetValue(c.SourceNodeId, out var list))
                            {
                                list = new List<string>();
                                childrenMap[c.SourceNodeId] = list;
                            }
                            list.Add(c.TargetNodeId);
                            hasParent.Add(c.TargetNodeId);
                        }
                    }

                    var rootVm = Nodes.FirstOrDefault(n => !hasParent.Contains(n.Id)) ?? Nodes[0];
                    if (nodeMap.TryGetValue(rootVm.Id, out var rootNode))
                    {
                        mindmap.Root = rootNode;
                        BuildMindmapTree(mindmap.Root, nodeMap, childrenMap, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                }
                return mindmap;

            default:
                return new FlowchartDiagramAst();
        }
    }

    private static void BuildMindmapTree(MindmapNode parent, Dictionary<string, MindmapNode> nodeMap, Dictionary<string, List<string>> childrenMap, HashSet<string> visited)
    {
        visited.Add(parent.Id);
        if (childrenMap.TryGetValue(parent.Id, out var childIds))
        {
            foreach (var childId in childIds)
            {
                if (!visited.Contains(childId) && nodeMap.TryGetValue(childId, out var childNode))
                {
                    parent.Children.Add(childNode);
                    BuildMindmapTree(childNode, nodeMap, childrenMap, visited);
                }
            }
        }
    }

    private static void ParseClassMember(string line, ClassNode classNode)
    {
        ClassVisibility vis = ClassVisibility.Public;
        if (line.StartsWith("+")) { vis = ClassVisibility.Public; line = line[1..].Trim(); }
        else if (line.StartsWith("-")) { vis = ClassVisibility.Private; line = line[1..].Trim(); }
        else if (line.StartsWith("#")) { vis = ClassVisibility.Protected; line = line[1..].Trim(); }
        else if (line.StartsWith("~")) { vis = ClassVisibility.Internal; line = line[1..].Trim(); }

        if (line.Contains("("))
        {
            int openParen = line.IndexOf('(');
            int closeParen = line.LastIndexOf(')');
            string beforeParen = openParen >= 0 ? line[..openParen].Trim() : line;
            string insideParen = (openParen >= 0 && closeParen > openParen) ? line.Substring(openParen + 1, closeParen - openParen - 1).Trim() : string.Empty;
            string afterParen = closeParen >= 0 && closeParen < line.Length - 1 ? line[(closeParen + 1)..].Trim() : string.Empty;

            string methodName = beforeParen;
            string returnType = afterParen;

            if (beforeParen.Contains(" "))
            {
                var parts = beforeParen.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    returnType = parts[0];
                    methodName = parts[1];
                }
            }

            var method = new ClassMember
            {
                Name = methodName,
                Type = returnType,
                Visibility = vis,
                IsMethod = true
            };

            if (!string.IsNullOrEmpty(insideParen))
            {
                var paramList = insideParen.Split(',');
                foreach (var p in paramList) method.Parameters.Add(p.Trim());
            }

            classNode.Methods.Add(method);
        }
        else
        {
            string type = "String";
            string name = line;
            if (line.Contains(":"))
            {
                var parts = line.Split(':');
                name = parts[0].Trim();
                type = parts[1].Trim();
            }
            else if (line.Contains(" "))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    type = parts[0].Trim();
                    name = parts[1].Trim();
                }
            }

            classNode.Attributes.Add(new ClassMember
            {
                Name = name,
                Type = type,
                Visibility = vis,
                IsMethod = false
            });
        }
    }

    public string GenerateMermaidCode()
    {
        var ast = CanvasToAst();
        return MermaidCodeGenerator.Generate(ast);
    }

    public string SyncToMarkdown(string markdown)
    {
        string newMermaidCode = GenerateMermaidCode();
        RawMermaidCode = newMermaidCode;
        if (MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown).Count > 0)
        {
            return MermaidMarkdownSyncService.ReplaceMermaidBlock(markdown, ActiveBlockIndex, newMermaidCode);
        }
        else
        {
            // Append mermaid fence if document has none
            return markdown.TrimEnd() + "\n\n```mermaid\n" + newMermaidCode + "\n```\n";
        }
    }
}
