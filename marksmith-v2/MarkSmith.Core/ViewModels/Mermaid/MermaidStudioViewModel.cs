using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Mermaid.Parser;
using MarkSmith.Mermaid.Sync;

namespace MarkSmith.ViewModels.Mermaid;

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
    [NotifyPropertyChangedFor(nameof(ShowsDirectionPicker))]
    [NotifyPropertyChangedFor(nameof(ShowsRoutingPicker))]
    private MermaidDiagramType _selectedDiagramType = MermaidDiagramType.Flowchart;

    /// <summary>Layout direction only means something for a flowchart (`flowchart TD|LR|…`).</summary>
    public bool ShowsDirectionPicker => SelectedDiagramType == MermaidDiagramType.Flowchart;

    /// <summary>Sequence messages always run straight across between lifelines, and a Gantt chart has
    /// no connectors, so the routing choice is hidden there rather than offered and ignored.</summary>
    public bool ShowsRoutingPicker => SelectedDiagramType is not (MermaidDiagramType.Sequence or MermaidDiagramType.Gantt);

    [ObservableProperty]
    private DiagramNodeViewModel? _selectedNode;

    [ObservableProperty]
    private DiagramConnectorViewModel? _selectedConnector;

    // The canvas highlights the selected connector from its own IsSelected flag.
    partial void OnSelectedConnectorChanged(DiagramConnectorViewModel? oldValue, DiagramConnectorViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

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

    // ---- Connector routing + style presets (QODER task 5) ----
    // The routing mode applied to every connector on the canvas. Changing it re-geometries all
    // existing connectors and is persisted into the generated mermaid via an `%%{init}%%`
    // `flowchart.curve` directive so the choice survives a save/reload round trip.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectorRoutingIndex))]
    private ConnectorRoutingMode _connectorRouting = ConnectorRoutingMode.Orthogonal;

    // Name of the active style preset (empty = studio default colors). When set, every node's
    // fill/border and every connector's line are recolored, and the palette is persisted into the
    // generated mermaid via `%%{init}%%` themeVariables.
    [ObservableProperty]
    private string _activePalette = string.Empty;

    // int-backed view of ConnectorRouting for the toolbar ComboBox, whose display order
    // (0 = Elbow/Orthogonal, 1 = Straight, 2 = Curved/Bezier) differs from the enum's order.
    public int ConnectorRoutingIndex
    {
        get => ConnectorRouting switch
        {
            ConnectorRoutingMode.Straight => 1,
            ConnectorRoutingMode.Bezier => 2,
            _ => 0
        };
        set => ConnectorRouting = value switch
        {
            1 => ConnectorRoutingMode.Straight,
            2 => ConnectorRoutingMode.Bezier,
            _ => ConnectorRoutingMode.Orthogonal
        };
    }

    // ---- Flowchart direction (Top-Down / Left-Right / …) ----
    // The layout direction emitted as `flowchart TD|LR|…`. Restored from the loaded AST so an
    // authored `graph LR` round-trips unchanged; the toolbar Direction picker lets the user flip
    // it and the change flows into GenerateMermaidCode (and thus the dirty baseline / sync).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlowchartDirectionIndex))]
    private FlowDirection _flowchartDirection = FlowDirection.TD;

    // int-backed view of FlowchartDirection for the toolbar ComboBox (display order:
    // 0 = Top-Down, 1 = Left-Right, 2 = Bottom-Up, 3 = Right-Left). TB (a Mermaid synonym of TD)
    // displays as Top-Down but is preserved verbatim unless the user actively picks a new option.
    public int FlowchartDirectionIndex
    {
        get => FlowchartDirection switch
        {
            FlowDirection.LR => 1,
            FlowDirection.BT => 2,
            FlowDirection.RL => 3,
            _ => 0 // TD and TB both read as Top-Down
        };
        set => FlowchartDirection = value switch
        {
            1 => FlowDirection.LR,
            2 => FlowDirection.BT,
            3 => FlowDirection.RL,
            _ => FlowDirection.TD
        };
    }

    // Changing the direction dropdown immediately re-flows the canvas so the user sees
    // the new orientation in the designer (not just in the exported code). One undo step.
    partial void OnFlowchartDirectionChanged(FlowDirection value)
    {
        if (SelectedDiagramType != MermaidDiagramType.Flowchart) return;

        SnapshotForUndo();
        foreach (var n in Nodes) n.HasCustomPosition = false;
        ApplyAutoLayout(force: true);
    }

    // Canonical code snapshot of the last load/save point, used to detect unsaved edits. We compare
    // generated-code-to-generated-code (not the raw source text) so harmless formatting differences
    // between the authored fence and the generator's output don't register as false "dirty" state.
    private string _savedCode = string.Empty;

    public MermaidDiagramAst? CurrentAst { get; private set; }

    // True when the canvas has drifted from the last load/sync — drives the "unsaved changes"
    // prompt on window close.
    public bool HasUnsavedChanges => !string.Equals(GenerateMermaidCode(), _savedCode, StringComparison.Ordinal);

    // ---- Undo / Redo (memento snapshots of canonical mermaid code) ----
    // The whole canvas round-trips through GenerateMermaidCode()/parse, so a single string is a
    // complete, faithful snapshot. Snapshot BEFORE each mutation; undo/redo swap the current state
    // with the stack top. Capped so a long editing session can't grow without bound.
    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private const int MaxUndoDepth = 100;

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    // Call BEFORE a mutation so the pre-change state is what gets restored.
    public void SnapshotForUndo()
    {
        _undoStack.Add(GenerateMermaidCode());
        if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        CanUndo = _undoStack.Count > 0;
        CanRedo = false;
    }

    [RelayCommand]
    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Add(GenerateMermaidCode());
        var prev = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreFromCode(prev);
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
        StatusText = "Undid last change.";
    }

    [RelayCommand]
    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(GenerateMermaidCode());
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreFromCode(next);
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
        StatusText = "Redid change.";
    }

    private void RestoreFromCode(string code)
    {
        var parseResult = MermaidParser.Parse(code);
        if (parseResult.Ast is null) return;
        CurrentAst = parseResult.Ast;
        SelectedDiagramType = parseResult.Ast.DiagramType;
        AstToCanvas(parseResult.Ast);
        // Deliberately do NOT touch _savedCode: undo/redo must not reset the unsaved-changes baseline.
        SelectedNodes.Clear();
        SelectedNode = null;
        SelectedConnector = null;
    }

    public MermaidStudioViewModel()
    {
        InitializePalette();
        // A sequence diagram's rows depend on every message and participant, so any add/remove
        // re-lays the whole conversation (cheap: one pass over the messages).
        Connectors.CollectionChanged += (_, e) =>
        {
            if (!IsSequence || _suspendSequenceLayout) return;
            // A message drawn (or pasted) on the canvas takes a slot at the end of the script, so
            // from then on it reorders like any loaded message.
            if (e.NewItems is { } added)
                foreach (DiagramConnectorViewModel c in added)
                    if (!_sequenceMessageOf.ContainsKey(c))
                    {
                        var st = SequenceStatement.ForMessage(new SequenceMessage { FromId = c.SourceNodeId, ToId = c.TargetNodeId });
                        _sequenceScript.Add(st);
                        _sequenceMessageOf[c] = st;
                    }
            LayoutSequence();
        };
        Nodes.CollectionChanged += (_, _) => { if (IsSequence && !_suspendSequenceLayout) LayoutSequence(); };
    }

    private bool IsSequence => SelectedDiagramType == MermaidDiagramType.Sequence;
    private bool _suspendSequenceLayout;

    /// <summary>The loaded sequence diagram's body as written (messages, notes, blocks,
    /// activations), and which statement each loaded message connector came from.</summary>
    private List<SequenceStatement> _sequenceScript = new();
    private bool _sequenceAutoNumber;
    private List<SequenceBox> _sequenceBoxes = new();
    private List<FlowSubgraph> _flowSubgraphs = new();
    private List<string> _flowStyleLines = new();
    /// <summary>The loaded flowchart's edges in source order: linkStyle's numbers point at them.</summary>
    private List<DiagramConnectorViewModel> _flowEdgesAtLoad = new();
    private readonly Dictionary<DiagramConnectorViewModel, SequenceStatement> _sequenceMessageOf = new(ReferenceEqualityComparer.Instance);

    public void InitializePalette()
    {
        PaletteItems.Clear();

        // 1. Flowchart Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Rectangle Box", ShapeType = "Rectangle", DefaultText = "Process", IconGlyph = "\uE8A5" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Rounded Rectangle", ShapeType = "RoundedRectangle", DefaultText = "Start/End", IconGlyph = "\uE739" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Stadium (Pill)", ShapeType = "Stadium", DefaultText = "Terminal", IconGlyph = "\uE739" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Subroutine", ShapeType = "Subroutine", DefaultText = "Subroutine", IconGlyph = "\uE8F1" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Database", ShapeType = "CylindricalDatabase", DefaultText = "Data Store", IconGlyph = "\uEDA2" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Circle", ShapeType = "Circle", DefaultText = "Node", IconGlyph = "\uEA3A" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Decision Rhombus", ShapeType = "Rhombus", DefaultText = "Decision?", IconGlyph = "\uF178" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Hexagon", ShapeType = "Hexagon", DefaultText = "Prepare", IconGlyph = "\uF0E2" });

        // 2. Sequence Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Sequence", DisplayName = "Actor", ShapeType = "Actor", DefaultText = "User", IconGlyph = "\uE77B" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Sequence", DisplayName = "Participant Box", ShapeType = "Participant", DefaultText = "Service", IconGlyph = "\uE8A5" });

        // 3. Class Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Class", DisplayName = "Class Box", ShapeType = "ClassBox", DefaultText = "Customer", IconGlyph = "\uE8A5" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Class", DisplayName = "Interface", ShapeType = "Interface", DefaultText = "IService", IconGlyph = "\uE943" });

        // 4. State Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "State", DisplayName = "State Node", ShapeType = "NormalState", DefaultText = "Idle", IconGlyph = "\uE739" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "State", DisplayName = "Choice State", ShapeType = "ChoiceState", DefaultText = "Choice", IconGlyph = "\uF178" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "State", DisplayName = "Start Point", ShapeType = "Start", DefaultText = "Start", IconGlyph = "\uEA3B" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "State", DisplayName = "End Point", ShapeType = "End", DefaultText = "End", IconGlyph = "\uECCB" });

        // 5. Gantt Primitives
        PaletteItems.Add(new MermaidPaletteItem { Category = "Gantt", DisplayName = "Task Bar", ShapeType = "TaskBar", DefaultText = "Design Phase", IconGlyph = "\uE787" });
        PaletteItems.Add(new MermaidPaletteItem { Category = "Gantt", DisplayName = "Milestone", ShapeType = "Milestone", DefaultText = "Release 1.0", IconGlyph = "\uE7C1" });

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

    /// <summary>A whole new diagram replaced the canvas (template, document block, file): the
    /// view fits it into the viewport. Not raised for edits, undo or live code sync.</summary>
    public event EventHandler? DiagramLoaded;

    public void LoadFromMermaidCode(string code)
    {
        RawMermaidCode = code;
        var parseResult = MermaidParser.Parse(code);

        if (parseResult.Ast != null)
        {
            CurrentAst = parseResult.Ast;
            SelectedDiagramType = parseResult.Ast.DiagramType;
            AstToCanvas(parseResult.Ast);
            // Re-apply any authored routing/palette BEFORE taking the dirty baseline so the
            // generated baseline (which embeds the matching `%%{init}%%`) lines up exactly.
            RestoreStudioStateFromDirectives();
            _savedCode = GenerateMermaidCode(); // baseline for unsaved-change detection
            StatusText = $"Loaded {SelectedDiagramType} diagram ({Nodes.Count} nodes, {Connectors.Count} edges).";
            DiagramLoaded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            StatusText = "Could not parse Mermaid diagram. Creating default canvas.";
            LoadDefaultSample();
        }
    }

    // Live code -> canvas sync for the Studio's Code pane (mermaid.live-style bidirectional
    // editing). Re-parses the authored text and rebuilds the canvas, but PRESERVES the on-canvas
    // position/size of any node that survives the re-parse so typing in the code pane doesn't
    // re-layout the whole diagram. New nodes fall back to the auto-layout position.
    // Returns true when the code parsed and the canvas was rebuilt; false on a syntax error
    // (canvas left untouched) so the Code pane can surface live pass/fail feedback.
    public bool SyncCanvasFromCode(string code)
    {
        var parseResult = MermaidParser.Parse(code);
        if (parseResult.Ast is null)
        {
            StatusText = "Mermaid syntax error — canvas left unchanged.";
            return false;
        }

        // Snapshot geometry by id before the rebuild.
        var geometry = Nodes.ToDictionary(
            n => n.Id,
            n => (n.X, n.Y, n.Width, n.Height),
            StringComparer.OrdinalIgnoreCase);

        RawMermaidCode = code;
        CurrentAst = parseResult.Ast;
        SelectedDiagramType = parseResult.Ast.DiagramType;
        AstToCanvas(parseResult.Ast);

        foreach (var n in Nodes)
        {
            if (geometry.TryGetValue(n.Id, out var g))
            {
                n.X = g.X;
                n.Y = g.Y;
                n.Width = g.Width;
                n.Height = g.Height;
                n.HasCustomPosition = true;
            }
        }

        UpdateAllConnectors();
        StatusText = $"Canvas synced from code ({Nodes.Count} nodes, {Connectors.Count} edges).";
        return true;
    }

    private void LoadDefaultSample()
    {
        SelectedDiagramType = MermaidDiagramType.Flowchart;
        FlowchartDirection = FlowDirection.TD; // the sample is laid out top-down
        Nodes.Clear();
        Connectors.Clear();
        ClearSequenceDecorations();
        _sequenceScript = new List<SequenceStatement>();
        _sequenceMessageOf.Clear();
        _sequenceAutoNumber = false;
        _sequenceBoxes = new List<SequenceBox>();
        _flowSubgraphs = new List<FlowSubgraph>();
        _flowStyleLines = new List<string>();
        _flowEdgesAtLoad = new List<DiagramConnectorViewModel>();

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

        _savedCode = GenerateMermaidCode(); // baseline for unsaved-change detection
        StatusText = "Sample flowchart initialized.";
    }

    public void AstToCanvas(MermaidDiagramAst ast)
    {
        _suspendSequenceLayout = true;
        try { AstToCanvasCore(ast); }
        finally { _suspendSequenceLayout = false; }
    }

    private void AstToCanvasCore(MermaidDiagramAst ast)
    {
        Nodes.Clear();
        Connectors.Clear();
        ClearSequenceDecorations();
        _sequenceScript = new List<SequenceStatement>();
        _sequenceMessageOf.Clear();
        _sequenceAutoNumber = false;
        _sequenceBoxes = new List<SequenceBox>();
        _flowSubgraphs = new List<FlowSubgraph>();
        _flowStyleLines = new List<string>();
        _flowEdgesAtLoad = new List<DiagramConnectorViewModel>();

        switch (ast)
        {
            case FlowchartDiagramAst flowchart:
                // Restore the authored layout direction so `graph LR` etc. round-trips and the
                // Direction picker reflects the loaded diagram.
                FlowchartDirection = flowchart.Direction;
                // Not drawn on the canvas (yet), but never lost on save: see CanvasToAst.
                _flowSubgraphs = flowchart.Subgraphs.ToList();
                _flowStyleLines = flowchart.StyleLines.ToList();
                foreach (var kvp in flowchart.Nodes)
                {
                    var fn = kvp.Value;
                    // Display <br/> (and variants) as real line breaks on the canvas; the
                    // code generator re-escapes \n back to <br/> on sync, so the round-trip
                    // is lossless and multi-line labels render properly in both worlds.
                    string label = string.IsNullOrEmpty(fn.Text) ? fn.Id : fn.Text;
                    label = MermaidCodeGenerator.FromBreakTags(label);
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = fn.Id,
                        LabelText = label,
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
                        Label = edge.Label is null ? null : MermaidCodeGenerator.FromBreakTags(edge.Label),
                        LineStyle = edge.LineStyle.ToString(),
                        StartHead = edge.StartHead.ToString(),
                        EndHead = edge.EndHead.ToString()
                    });
                }
                _flowEdgesAtLoad = Connectors.ToList();
                break;

            case SequenceDiagramAst seq:
                foreach (var p in seq.Participants)
                {
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = p.Id,
                        LabelText = string.IsNullOrEmpty(p.Alias) ? p.Id : MermaidCodeGenerator.FromBreakTags(p.Alias),
                        Shape = p.Type.ToString(),
                        Category = "Sequence"
                    });
                }
                // Every message becomes a row, including the ones inside loop/alt/opt blocks (they
                // used to be left off the canvas). The written script is kept so notes, blocks and
                // activations go back exactly where they were (see CanvasToAst).
                _sequenceAutoNumber = seq.AutoNumber;
                _sequenceBoxes = seq.Boxes.ToList();
                _sequenceScript = seq.Statements.Count > 0
                    ? seq.Statements.ToList()
                    : seq.Messages.Select(SequenceStatement.ForMessage).ToList();
                foreach (var st in _sequenceScript)
                {
                    if (st.Kind != SequenceStatementKind.Message || st.Message is not { } msg) continue;
                    var conn = new DiagramConnectorViewModel
                    {
                        SourceNodeId = msg.FromId,
                        SourceAnchor = "Right",
                        TargetNodeId = msg.ToId,
                        TargetAnchor = "Left",
                        Label = MermaidCodeGenerator.FromBreakTags(msg.Text),
                        LineStyle = msg.MessageType.ToString()
                    };
                    _sequenceMessageOf[conn] = st;
                    Connectors.Add(conn);
                }
                break;

            case ClassDiagramAst cls:
                foreach (var kvp in cls.Classes)
                {
                    var c = kvp.Value;
                    // The box shows the class as Mermaid writes it: name, annotation, then every
                    // attribute and method in Mermaid's own member syntax, so saving reads it back
                    // with the real parser (methods and visibility used to be lost here).
                    var lines = new List<string> { c.Name };
                    if (!string.IsNullOrEmpty(c.Annotation)) lines.Add(c.Annotation);
                    lines.AddRange(c.Attributes.Concat(c.Methods).Select(MermaidCodeGenerator.FormatClassMember));
                    string label = string.Join("\n", lines);
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
                // Mermaid writes both the start and the end point as [*], but draws two: a dot that
                // transitions leave and a bullseye they arrive at. One shared node used to pull
                // the first and last states together and tangle every diagram.
                bool hasStart = st.Transitions.Any(t => t.FromId == StatePseudoId);
                bool hasEnd = st.Transitions.Any(t => t.ToId == StatePseudoId);
                foreach (var kvp in st.States)
                {
                    var s = kvp.Value;
                    if (s.Id == StatePseudoId)
                    {
                        if (hasStart || !hasEnd) Nodes.Add(PseudoStateNode(StatePseudoId, "Start"));
                        if (hasEnd) Nodes.Add(PseudoStateNode(StateEndNodeId, "End"));
                        continue;
                    }
                    Nodes.Add(new DiagramNodeViewModel
                    {
                        Id = s.Id,
                        LabelText = string.IsNullOrEmpty(s.Label) ? s.Id : MermaidCodeGenerator.FromBreakTags(s.Label),
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
                        TargetNodeId = tr.ToId == StatePseudoId ? StateEndNodeId : tr.ToId,
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
                        Label = rel.RelationshipName,
                        // Display only (ER export keeps the parsed cardinalities): identifying
                        // relationships are solid, non-identifying dashed, and ER links have no arrow.
                        LineStyle = rel.IsIdentifying ? "Solid" : "Dashed",
                        EndHead = "None"
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

        // Size every node to its label first; a saved size below still wins.
        foreach (var node in Nodes) node.GrowToFitLabel();

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

    /// <summary>Mermaid's id for a state diagram's start and end points.</summary>
    public const string StatePseudoId = "[*]";

    /// <summary>The canvas id of the end point when a diagram has both (written back as [*]).</summary>
    public const string StateEndNodeId = "[*]end";

    // [*] is a UML start dot / end bullseye, not a box with "[*]" written in it.
    private static DiagramNodeViewModel PseudoStateNode(string id, string shape) => new()
    {
        Id = id,
        LabelText = shape == "Start" ? "Start" : "End",
        Shape = shape,
        Category = "State",
        Width = 28,
        Height = 28,
    };

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

        // The Auto Layout toolbar button passes force=true: it is an explicit user request to
        // re-arrange the whole diagram, so it must override any node the user has dragged or that
        // was loaded with a saved position (HasCustomPosition). The default (force=false) path is
        // used on load, where saved/custom positions are deliberately respected.
        if (force)
        {
            foreach (var n in Nodes) n.HasCustomPosition = false;
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
            // Participants across the top in declaration order, each column wide enough for its
            // header and for the longest message label crossing to its neighbour (Mermaid does the
            // same). Header boxes keep their label-fitted width; they used to be forced to 140x50,
            // which clipped longer names.
            const double startY = 100;
            double centerX = 0;
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (!n.HasCustomPosition)
                {
                    n.Width = Math.Max(n.Width, 120);
                    n.Height = string.Equals(n.Shape, "Actor", StringComparison.OrdinalIgnoreCase) ? 64 : 50;
                }

                if (i == 0)
                {
                    centerX = 120 + n.Width / 2;
                }
                else
                {
                    var prev = Nodes[i - 1];
                    double widest = Connectors
                        .Where(c => (Same(c.SourceNodeId, prev.Id) && Same(c.TargetNodeId, n.Id)) ||
                                    (Same(c.SourceNodeId, n.Id) && Same(c.TargetNodeId, prev.Id)))
                        .Select(c => MarkSmith.Core.Mermaid.Routing.SequenceLayout.LabelWidth(c.Label))
                        .DefaultIfEmpty(0).Max();
                    centerX += MarkSmith.Core.Mermaid.Routing.SequenceLayout.ColumnSpacing(prev.Width, n.Width, widest);
                }

                if (!n.HasCustomPosition)
                {
                    n.X = centerX - n.Width / 2;
                    n.Y = startY;
                }
            }

            static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
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
            // Ranks and in-layer order: cycles broken, crossings reduced (Core LayeredLayout).
            var layered = MarkSmith.Core.Mermaid.Routing.LayeredLayout.Compute(
                Nodes.Select(n => n.Id).ToList(),
                Connectors.Select(c => (c.SourceNodeId, c.TargetNodeId)).ToList());
            var byId = Nodes.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var layers = new Dictionary<int, List<DiagramNodeViewModel>>();
            for (int r = 0; r < layered.Layers.Count; r++)
            {
                var list = layered.Layers[r].Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (list.Count > 0) layers[r] = list;
            }

            bool isFlowchart = SelectedDiagramType == MermaidDiagramType.Flowchart;
            bool vertical = isFlowchart && FlowchartDirection is FlowDirection.TD or FlowDirection.BT;
            bool reversePrimary = isFlowchart && FlowchartDirection is FlowDirection.BT or FlowDirection.RL;

            // Size-aware layers: each rank is as deep as its biggest node and each node as wide as
            // itself, plus a gap with room for an edge label and its markers. (Fixed 200/160 px
            // steps used to butt grown class boxes together so labels covered the UML markers.)
            // With default 140x60 nodes the steps come out the same as before.
            double primaryGap = vertical ? 100 : 80;
            double crossGap = vertical ? 60 : 100;
            double primaryOrigin = 100;
            double crossCenter = vertical ? 500 : 350;
            double PrimarySize(DiagramNodeViewModel n) => vertical ? n.Height : n.Width;
            double CrossSize(DiagramNodeViewModel n) => vertical ? n.Width : n.Height;

            var orderedRanks = layers.Keys.OrderBy(k => k).ToList();
            if (reversePrimary) orderedRanks.Reverse();
            var depth = layers.ToDictionary(l => l.Key, l => l.Value.Max(PrimarySize));
            var rankStart = new Dictionary<int, double>();
            double cursor = primaryOrigin;
            foreach (var rank in orderedRanks)
            {
                rankStart[rank] = cursor;
                cursor += depth[rank] + primaryGap;
            }

            foreach (var kvp in layers.OrderBy(l => l.Key))
            {
                int rank = kvp.Key;
                var layerNodes = kvp.Value;
                double totalSpan = layerNodes.Sum(CrossSize) + (layerNodes.Count - 1) * crossGap;
                double crossPos = Math.Max(primaryOrigin, crossCenter - totalSpan / 2);

                foreach (var node in layerNodes)
                {
                    if (!node.HasCustomPosition)
                    {
                        // Centre each node within its rank so mixed sizes line up on one axis.
                        double primaryPos = rankStart[rank] + (depth[rank] - PrimarySize(node)) / 2;
                        if (vertical)
                        {
                            node.X = crossPos;
                            node.Y = primaryPos;
                        }
                        else
                        {
                            node.X = primaryPos;
                            node.Y = crossPos;
                        }
                    }
                    crossPos += CrossSize(node) + crossGap;
                }
            }
        }

        UpdateAllConnectors();
    }

    public void UpdateConnectedConnectors(DiagramNodeViewModel node)
    {
        if (IsSequence) { LayoutSequence(); return; }
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
        if (IsSequence) { LayoutSequence(); return; }
        foreach (var node in Nodes) node.LifelineLength = 0;
        ClearSequenceDecorations();
        foreach (var conn in Connectors)
        {
            conn.SequenceNumber = null;
            UpdateConnectorGeometry(conn);
        }
    }

    /// <summary>A loaded subgraph with only the nodes still on the canvas (each tagged with it),
    /// or null when nothing is left in it or its nested subgraphs.</summary>
    private static FlowSubgraph? PruneSubgraph(FlowSubgraph sg, FlowchartDiagramAst ast)
    {
        var kept = new FlowSubgraph { Id = sg.Id, Title = sg.Title };
        foreach (var id in sg.NodeIds)
            if (ast.Nodes.TryGetValue(id, out var node))
            {
                kept.NodeIds.Add(node.Id);
                node.SubgraphId ??= sg.Id;
            }
        foreach (var nested in sg.NestedSubgraphs)
            if (PruneSubgraph(nested, ast) is { } k) kept.NestedSubgraphs.Add(k);
        return kept.NodeIds.Count > 0 || kept.NestedSubgraphs.Count > 0 ? kept : null;
    }

    /// <summary>
    /// A kept style/click/class/linkStyle line as it should be written for what is on the canvas
    /// now, or null to drop it. Mermaid creates any node a "style X" or "class X" names, so a line
    /// for a deleted node would bring it back as a stray box; a linkStyle past the last edge is an
    /// error.
    /// </summary>
    private int LinkIndexNow(int loadedIndex) =>
        loadedIndex >= 0 && loadedIndex < _flowEdgesAtLoad.Count ? Connectors.IndexOf(_flowEdgesAtLoad[loadedIndex]) : -1;

    private static string? StyleLineForCanvas(string line, FlowchartDiagramAst ast, Func<int, int> linkIndexNow)
    {
        var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return line;
        switch (parts[0].ToLowerInvariant())
        {
            case "style":
            case "click":
                return ast.Nodes.ContainsKey(parts[1]) ? line : null;
            case "class":
                var ids = parts[1].Split(',').Select(i => i.Trim()).Where(ast.Nodes.ContainsKey).ToList();
                if (ids.Count == 0) return null;
                return parts.Length > 2 ? $"{parts[0]} {string.Join(",", ids)} {parts[2]}" : $"{parts[0]} {string.Join(",", ids)}";
            case "linkstyle":
                if (parts[1].Equals("default", StringComparison.OrdinalIgnoreCase)) return line;
                // Numbers follow their edges: renumbered after a delete or reorder, dropped with
                // a deleted edge (a stale number would style some other line).
                var now = parts[1].Split(',')
                    .Select(i => int.TryParse(i.Trim(), out var n) ? linkIndexNow(n) : -1)
                    .Where(n => n >= 0).Distinct().OrderBy(n => n).ToList();
                if (now.Count == 0) return null;
                return parts.Length > 2 ? $"{parts[0]} {string.Join(",", now)} {parts[2]}" : $"{parts[0]} {string.Join(",", now)}";
            default:
                return line;
        }
    }

    /// <summary>
    /// Moves the selected sequence message one row up (-1) or down (+1). It swaps places with its
    /// neighbour in the script, so it can move into or out of a loop/alt block. Returns false at
    /// either end or when nothing movable is selected.
    /// </summary>
    public bool MoveSelectedMessage(int direction)
    {
        if (!IsSequence || SelectedConnector is not { } conn) return false;
        var (_, rows) = BuildSequenceScriptWithRows();
        int row = rows.IndexOf(conn);
        int target = row + Math.Sign(direction);
        if (row < 0 || direction == 0 || target < 0 || target >= rows.Count)
        {
            StatusText = row < 0 ? StatusText : direction < 0 ? "Already the first message." : "Already the last message.";
            return false;
        }

        SnapshotForUndo();
        // Rows fill the script's message slots in Connectors order, so swapping the two connectors
        // in the collection swaps their rows.
        int a = Connectors.IndexOf(conn), b = Connectors.IndexOf(rows[target]);
        Connectors.Move(a, b);
        if (Math.Abs(a - b) > 1) Connectors.Move(a < b ? b - 1 : b + 1, a);
        StatusText = $"Moved \"{(string.IsNullOrEmpty(conn.Label) ? "message" : conn.Label)}\" {(direction < 0 ? "up" : "down")}.";
        return true;
    }

    /// <summary>
    /// The sequence body to write back: the loaded script with every message slot refilled from
    /// the canvas in row order (so an edited, deleted or reordered message lands where the script
    /// had one), notes and activations whose participant was deleted left out, and messages drawn
    /// since loading appended at the end. Blocks keep their place even if emptied.
    /// </summary>
    private List<SequenceStatement> BuildSequenceScript() => BuildSequenceScriptWithRows().Script;

    /// <summary><see cref="BuildSequenceScript"/>, plus the connector behind each message
    /// statement in script order (row i of the layout is drawn by connector i).</summary>
    private (List<SequenceStatement> Script, List<DiagramConnectorViewModel> Rows) BuildSequenceScriptWithRows()
    {
        var rows = new List<DiagramConnectorViewModel>();
        var ids = new HashSet<string>(Nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        var loaded = new Queue<DiagramConnectorViewModel>(Connectors.Where(c => _sequenceMessageOf.ContainsKey(c)));
        var surviving = new HashSet<SequenceStatement>(loaded.Select(c => _sequenceMessageOf[c]), ReferenceEqualityComparer.Instance);
        var script = new List<SequenceStatement>();

        foreach (var st in _sequenceScript)
        {
            switch (st.Kind)
            {
                case SequenceStatementKind.Message:
                    if (surviving.Contains(st) && loaded.Count > 0)
                    {
                        var c = loaded.Dequeue();
                        script.Add(SequenceStatement.ForMessage(MessageFrom(c, _sequenceMessageOf[c].Message)));
                        rows.Add(c);
                    }
                    break;
                case SequenceStatementKind.Note:
                    if (st.Note is { } note && note.TargetParticipantIds.All(ids.Contains)) script.Add(st);
                    break;
                case SequenceStatementKind.Activate:
                case SequenceStatementKind.Deactivate:
                    if (ids.Contains(st.ParticipantId)) script.Add(st);
                    break;
                case SequenceStatementKind.Raw when SequenceCreateLine.Target(st.Text) is { } who:
                    // create/destroy for a deleted participant would bring it back.
                    if (ids.Contains(who)) script.Add(st);
                    break;
                default:
                    script.Add(st);
                    break;
            }
        }

        foreach (var c in Connectors.Where(c => !_sequenceMessageOf.ContainsKey(c)))
        {
            script.Add(SequenceStatement.ForMessage(MessageFrom(c, null)));
            rows.Add(c);
        }
        return (script, rows);

        static SequenceMessage MessageFrom(DiagramConnectorViewModel c, SequenceMessage? original)
        {
            Enum.TryParse<SequenceMessageType>(c.LineStyle, true, out var msgType);
            return new SequenceMessage
            {
                FromId = c.SourceNodeId,
                ToId = c.TargetNodeId,
                Text = c.Label ?? string.Empty,
                MessageType = msgType,
                // +/- activation shorthand travels with its message.
                ActivateTarget = original?.ActivateTarget ?? false,
                DeactivateTarget = original?.DeactivateTarget ?? false
            };
        }
    }

    /// <summary>
    /// Draws the sequence diagram: a lifeline under every participant and each message on its own
    /// row, in order, lifeline to lifeline (Core <see cref="MarkSmith.Core.Mermaid.Routing.SequenceLayout"/>).
    /// </summary>
    public void LayoutSequence()
    {
        var boxes = Nodes.Select(n => new MarkSmith.Core.Mermaid.Routing.SequenceParticipantBox(n.Id, n.X, n.Y, n.Width, n.Height)).ToList();
        var (script, rowConnectors) = BuildSequenceScriptWithRows();
        var drawing = MarkSmith.Core.Mermaid.Routing.SequenceLayout.Layout(boxes, script, _sequenceAutoNumber, _sequenceBoxes);

        for (int i = 0; i < rowConnectors.Count && i < drawing.Routes.Count; i++)
        {
            if (drawing.Routes[i] is { } route)
                rowConnectors[i].SetRoute(route.Points, route.LabelX, route.LabelY);
            rowConnectors[i].SequenceNumber = drawing.Routes[i]?.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var node in Nodes)
            node.LifelineLength = drawing.LifelineLengths.TryGetValue(node.Id, out var len) ? len : 0;

        Replace(SequenceBoxes, drawing.Boxes);
        Replace(SequenceFrames, drawing.Frames);
        Replace(SequenceNotes, drawing.Notes);
        Replace(SequenceActivations, drawing.Activations);

        static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
        {
            if (target.SequenceEqual(items)) return; // records compare by value: nothing moved
            target.Clear();
            foreach (var item in items) target.Add(item);
        }
    }

    /// <summary>Sequence block frames (loop/alt/opt/par/critical/break, and rect bands), outermost
    /// first. Empty for other diagram types.</summary>
    public ObservableCollection<MarkSmith.Core.Mermaid.Routing.SequenceFrame> SequenceFrames { get; } = new();
    /// <summary>Participant boxes (<c>box … end</c>), drawn behind headers and lifelines.</summary>
    public ObservableCollection<MarkSmith.Core.Mermaid.Routing.SequenceGroupBox> SequenceBoxes { get; } = new();
    /// <summary>Sequence notes (left of / right of / over).</summary>
    public ObservableCollection<MarkSmith.Core.Mermaid.Routing.SequenceNoteBox> SequenceNotes { get; } = new();
    /// <summary>Activation bars on sequence lifelines.</summary>
    public ObservableCollection<MarkSmith.Core.Mermaid.Routing.SequenceActivationBar> SequenceActivations { get; } = new();

    private void ClearSequenceDecorations()
    {
        if (SequenceBoxes.Count > 0) SequenceBoxes.Clear();
        if (SequenceFrames.Count > 0) SequenceFrames.Clear();
        if (SequenceNotes.Count > 0) SequenceNotes.Clear();
        if (SequenceActivations.Count > 0) SequenceActivations.Clear();
    }

    /// <summary>
    /// Everything the canvas draws, for Fit and the minimap: node boxes plus sequence lifelines and
    /// self-call loops that hang below or beside them. Null when the canvas is empty.
    /// </summary>
    public Rect? GetContentBounds()
    {
        if (Nodes.Count == 0) return null;
        double minX = Nodes.Min(n => n.X), minY = Nodes.Min(n => n.Y);
        double maxX = Nodes.Max(n => n.X + n.Width), maxY = Nodes.Max(n => n.LifelineBottom);
        foreach (var c in Connectors.Where(c => !string.IsNullOrEmpty(c.PathData)))
        {
            minX = Math.Min(minX, Math.Min(c.SourceX, c.TargetX));
            maxX = Math.Max(maxX, Math.Max(c.SourceX, c.TargetX));
            maxY = Math.Max(maxY, Math.Max(c.SourceY, c.TargetY));
        }
        if (IsSequence)
        {
            // Self-call loops reach right of their lifeline, and their labels further still.
            foreach (var c in Connectors.Where(c => c.SourceNodeId.Equals(c.TargetNodeId, StringComparison.OrdinalIgnoreCase)))
                maxX = Math.Max(maxX, c.MidpointX + MarkSmith.Core.Mermaid.Routing.SequenceLayout.LabelWidth(c.Label) / 2);
            // Notes and block frames can sit outside the outermost lifelines.
            foreach (var r in SequenceFrames.Select(f => (f.X, f.Y, f.Width, f.Height))
                         .Concat(SequenceBoxes.Select(b => (b.X, b.Y, b.Width, b.Height)))
                         .Concat(SequenceNotes.Select(n => (n.X, n.Y, n.Width, n.Height))))
            {
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);
            }
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
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

        if (IsSequence) { LayoutSequence(); return; }

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
        SnapshotForUndo();
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
        AddConnectorCore(sourceNode.Id, sourceAnchor, newNode.Id, targetAnchor);
        SelectNode(newNode, false);
        StatusText = $"Quick-added node '{newNode.LabelText}' ({direction}).";
        return newNode;
    }

    public void UpdateConnectorGeometry(DiagramConnectorViewModel conn)
    {
        // Sequence messages are laid out as a whole (row order matters); a connector not yet in
        // the list is drawn by the re-layout that adding it triggers.
        if (IsSequence) { LayoutSequence(); return; }
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

    public DiagramNodeViewModel AddNodeFromPalette(MermaidPaletteItem item, double x, double y)
    {
        SnapshotForUndo();
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
        if (node.IsPseudoState) node.Width = node.Height = 28;

        Nodes.Add(node);
        SelectNode(node, false);
        StatusText = $"Added {node.Shape} node '{node.LabelText}' at ({node.X:F0}, {node.Y:F0}).";
        return node;
    }

    public void AddConnector(string sourceId, string sourceAnchor, string targetId, string targetAnchor)
    {
        SnapshotForUndo();
        AddConnectorCore(sourceId, sourceAnchor, targetId, targetAnchor);
    }

    // Snapshot-free core so composite operations (e.g. QuickAddNode = add node + add connector) can
    // record a single undo entry instead of one per sub-step.
    private void AddConnectorCore(string sourceId, string sourceAnchor, string targetId, string targetAnchor)
    {
        var conn = new DiagramConnectorViewModel
        {
            SourceNodeId = sourceId,
            SourceAnchor = sourceAnchor,
            TargetNodeId = targetId,
            TargetAnchor = targetAnchor,
            // Each grammar keeps its style in a different field (see ConnectorAppearance): a new
            // sequence message is a solid arrow, a class link an association, an ER link has no head.
            LineStyle = SelectedDiagramType == MermaidDiagramType.Sequence ? "SolidArrow" : "Solid",
            EndHead = SelectedDiagramType switch
            {
                MermaidDiagramType.Class => "Association",
                MermaidDiagramType.Er => "None",
                _ => "Normal",
            },
            RoutingMode = ConnectorRouting // new connectors follow the active toolbar routing
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
            SnapshotForUndo();
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
            SnapshotForUndo();
            var connToDelete = SelectedConnector;
            Connectors.Remove(connToDelete);
            SelectedConnector = null;
            StatusText = "Deleted connector.";
        }
    }

    // ============================================================================================
    // World-class editing suite — selection, nudge, duplicate, copy/paste, align & distribute.
    // These back the keyboard shortcuts, the right-click context menu and the Align toolbar, and
    // bring the Studio to parity with dedicated diagrammers (draw.io / Whimsical / mermaid.live).
    // ============================================================================================

    #region Selection helpers

    [RelayCommand]
    public void SelectAll()
    {
        SelectedNodes.Clear();
        foreach (var n in Nodes)
        {
            n.IsSelected = true;
            SelectedNodes.Add(n);
        }
        SelectedNode = SelectedNodes.LastOrDefault();
        SelectedConnector = null;
        StatusText = $"Selected {SelectedNodes.Count} node(s).";
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var n in Nodes) n.IsSelected = false;
        SelectedNodes.Clear();
        SelectedNode = null;
        SelectedConnector = null;
    }

    #endregion

    #region Nudge (arrow keys)

    // Arrow-key nudge. A single press moves 1px (fine); holding Shift moves a full grid step
    // (coarse). Each press is its own undo step so a user can walk a node back.
    public void NudgeSelected(double deltaX, double deltaY, bool coarse)
    {
        // In a sequence diagram ↑/↓ on a selected message moves it a row (its position is its
        // order; nudging it by pixels means nothing).
        if (IsSequence && SelectedNodes.Count == 0 && SelectedConnector is not null && deltaX == 0 && deltaY != 0)
        {
            MoveSelectedMessage(deltaY < 0 ? -1 : 1);
            return;
        }
        if (SelectedNodes.Count == 0) return;
        double step = coarse ? Math.Max(GridSnapSize, 10) : 1;
        SnapshotForUndo();
        MoveSelectedNodes(deltaX * step, deltaY * step);
    }

    #endregion

    #region Duplicate + Copy / Paste (in-app clipboard)

    // Immutable snapshots so the clipboard survives later edits to the originals.
    private sealed record NodeSnapshot(string LabelText, string Category, string Shape, double X, double Y,
        double Width, double Height, string FillColor, string StrokeColor, double StrokeWidth);

    private sealed record ConnectorSnapshot(string SourceNodeId, string SourceAnchor, string TargetNodeId,
        string TargetAnchor, string LineStyle, string StartHead, string EndHead, string? Label,
        ConnectorRoutingMode RoutingMode, string StrokeColor, double StrokeWidth);

    private readonly List<NodeSnapshot> _clipboardNodes = new();
    private readonly List<ConnectorSnapshot> _clipboardConnectors = new();

    [RelayCommand]
    public void CopySelected()
    {
        if (SelectedNodes.Count == 0) { StatusText = "Nothing selected to copy."; return; }

        _clipboardNodes.Clear();
        _clipboardConnectors.Clear();

        var ids = new HashSet<string>(SelectedNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var n in SelectedNodes)
            _clipboardNodes.Add(new NodeSnapshot(n.LabelText, n.Category, n.Shape, n.X, n.Y, n.Width, n.Height, n.FillColor, n.StrokeColor, n.StrokeWidth));

        // Only carry connectors whose BOTH endpoints are in the copied set — dangling edges would
        // reference nodes that don't exist at the paste site.
        foreach (var c in Connectors)
            if (ids.Contains(c.SourceNodeId) && ids.Contains(c.TargetNodeId))
                _clipboardConnectors.Add(new ConnectorSnapshot(c.SourceNodeId, c.SourceAnchor, c.TargetNodeId, c.TargetAnchor, c.LineStyle, c.StartHead, c.EndHead, c.Label, c.RoutingMode, c.StrokeColor, c.StrokeWidth));

        StatusText = $"Copied {_clipboardNodes.Count} node(s) and {_clipboardConnectors.Count} connector(s).";
    }

    [RelayCommand]
    public void PasteClipboard()
    {
        if (_clipboardNodes.Count == 0) { StatusText = "Clipboard is empty."; return; }
        PasteSnapshots(_clipboardNodes, _clipboardConnectors, offset: 30);
    }

    [RelayCommand]
    public void DuplicateSelected()
    {
        if (SelectedNodes.Count == 0) { StatusText = "Nothing selected to duplicate."; return; }

        var ids = new HashSet<string>(SelectedNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        var nodes = SelectedNodes
            .Select(n => new NodeSnapshot(n.LabelText, n.Category, n.Shape, n.X, n.Y, n.Width, n.Height, n.FillColor, n.StrokeColor, n.StrokeWidth))
            .ToList();
        var conns = Connectors
            .Where(c => ids.Contains(c.SourceNodeId) && ids.Contains(c.TargetNodeId))
            .Select(c => new ConnectorSnapshot(c.SourceNodeId, c.SourceAnchor, c.TargetNodeId, c.TargetAnchor, c.LineStyle, c.StartHead, c.EndHead, c.Label, c.RoutingMode, c.StrokeColor, c.StrokeWidth))
            .ToList();

        PasteSnapshots(nodes, conns, offset: 30);
    }

    /// <summary>
    /// Alt+drag duplicate: creates an exact copy of a single node at the same position (no offset,
    /// so it sits directly under the pointer ready to be dragged), selects the copy, and returns it.
    /// One undo step. Connectors are NOT copied — this is a quick "stamp another one" gesture.
    /// </summary>
    public DiagramNodeViewModel DuplicateSingleNodeForDrag(DiagramNodeViewModel source)
    {
        SnapshotForUndo();
        var node = new DiagramNodeViewModel
        {
            Id = GenerateUniqueId(),
            LabelText = source.LabelText,
            Category = source.Category,
            Shape = source.Shape,
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            FillColor = source.FillColor,
            StrokeColor = source.StrokeColor,
            StrokeWidth = source.StrokeWidth,
            HasCustomPosition = true
        };
        Nodes.Add(node);
        SelectNode(node, false);
        StatusText = $"Duplicated '{source.LabelText}' — drag to place.";
        return node;
    }

    // Materializes snapshots as brand-new nodes (fresh IDs), remaps any carried connectors onto
    // the new IDs, offsets the geometry so the copy doesn't sit exactly on top of the original, and
    // selects the result. One undo step for the whole operation.
    private void PasteSnapshots(List<NodeSnapshot> nodes, List<ConnectorSnapshot> conns, double offset)
    {
        SnapshotForUndo();

        var oldToNew = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<DiagramNodeViewModel>();

        foreach (var snap in nodes)
        {
            var newId = GenerateUniqueId();
            var node = new DiagramNodeViewModel
            {
                Id = newId,
                LabelText = snap.LabelText,
                Category = snap.Category,
                Shape = snap.Shape,
                X = snap.X + offset,
                Y = snap.Y + offset,
                Width = snap.Width,
                Height = snap.Height,
                FillColor = snap.FillColor,
                StrokeColor = snap.StrokeColor,
                StrokeWidth = snap.StrokeWidth,
                HasCustomPosition = true
            };
            Nodes.Add(node);
            created.Add(node);
        }

        // Map original IDs -> new IDs. Snapshots are positional, so zip against the ORIGINAL
        // selected order captured at copy time via the connector endpoints' first appearance.
        // Rebuild the old-id list from the source snapshots isn't stored, so derive the mapping by
        // matching each connector endpoint to the snapshot that owned it. Simpler and robust: the
        // caller passes connectors whose endpoints are original IDs; we map by re-deriving the
        // original node list order is not available here, so instead we map old->new using the
        // original IDs captured alongside snapshots. To keep this airtight we rebuild the map from
        // the nodes we just created paired with the snapshot order of the CURRENT selection.
        // (See CopySelected/DuplicateSelected: snapshots are taken in SelectedNodes order, so we
        // re-derive original IDs the same way.)
        var originals = SelectedNodes.Count == nodes.Count
            ? SelectedNodes.Select(n => n.Id).ToList()
            : new List<string>();
        if (originals.Count == nodes.Count)
        {
            for (int i = 0; i < originals.Count; i++) oldToNew[originals[i]] = created[i].Id;
        }

        foreach (var c in conns)
        {
            if (!oldToNew.TryGetValue(c.SourceNodeId, out var src) || !oldToNew.TryGetValue(c.TargetNodeId, out var tgt))
                continue; // endpoint wasn't duplicated — skip the edge rather than dangle it
            var conn = new DiagramConnectorViewModel
            {
                SourceNodeId = src,
                SourceAnchor = c.SourceAnchor,
                TargetNodeId = tgt,
                TargetAnchor = c.TargetAnchor,
                LineStyle = c.LineStyle,
                StartHead = c.StartHead,
                EndHead = c.EndHead,
                Label = c.Label,
                RoutingMode = c.RoutingMode,
                StrokeColor = c.StrokeColor,
                StrokeWidth = c.StrokeWidth
            };
            UpdateConnectorGeometry(conn);
            Connectors.Add(conn);
        }

        // Select the newly pasted set.
        ClearSelection();
        foreach (var n in created)
        {
            n.IsSelected = true;
            SelectedNodes.Add(n);
        }
        SelectedNode = created.LastOrDefault();
        StatusText = $"Pasted {created.Count} node(s).";
    }

    // Collision-free node id: node_N where N is the first free integer.
    private string GenerateUniqueId()
    {
        int counter = Nodes.Count + 1;
        string id = $"node_{counter}";
        while (Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            counter++;
            id = $"node_{counter}";
        }
        return id;
    }

    #endregion

    #region Align & Distribute (multi-select)

    // All alignment ops act on the current multi-selection and are a single undo step. They no-op
    // (with a status hint) when fewer than two nodes are selected — aligning one node is meaningless.
    private bool TryBeginAlign(out List<DiagramNodeViewModel> sel)
    {
        sel = SelectedNodes.ToList();
        if (sel.Count < 2) { StatusText = "Select at least two nodes to align."; return false; }
        SnapshotForUndo();
        return true;
    }

    [RelayCommand]
    public void AlignLeft()
    {
        if (!TryBeginAlign(out var sel)) return;
        double minX = sel.Min(n => n.X);
        foreach (var n in sel) n.X = minX;
        FinishAlign("Aligned left.");
    }

    [RelayCommand]
    public void AlignHorizontalCenter()
    {
        if (!TryBeginAlign(out var sel)) return;
        double cx = sel.Average(n => n.X + n.Width / 2);
        foreach (var n in sel) n.X = cx - n.Width / 2;
        FinishAlign("Aligned horizontal centers.");
    }

    [RelayCommand]
    public void AlignRight()
    {
        if (!TryBeginAlign(out var sel)) return;
        double maxRight = sel.Max(n => n.X + n.Width);
        foreach (var n in sel) n.X = maxRight - n.Width;
        FinishAlign("Aligned right.");
    }

    [RelayCommand]
    public void AlignTop()
    {
        if (!TryBeginAlign(out var sel)) return;
        double minY = sel.Min(n => n.Y);
        foreach (var n in sel) n.Y = minY;
        FinishAlign("Aligned top.");
    }

    [RelayCommand]
    public void AlignVerticalMiddle()
    {
        if (!TryBeginAlign(out var sel)) return;
        double cy = sel.Average(n => n.Y + n.Height / 2);
        foreach (var n in sel) n.Y = cy - n.Height / 2;
        FinishAlign("Aligned vertical middles.");
    }

    [RelayCommand]
    public void AlignBottom()
    {
        if (!TryBeginAlign(out var sel)) return;
        double maxBottom = sel.Max(n => n.Y + n.Height);
        foreach (var n in sel) n.Y = maxBottom - n.Height;
        FinishAlign("Aligned bottom.");
    }

    [RelayCommand]
    public void DistributeHorizontally()
    {
        if (!TryBeginAlign(out var sel)) return;
        var ordered = sel.OrderBy(n => n.X).ToList();
        double firstCenter = ordered[0].X + ordered[0].Width / 2;
        double lastCenter = ordered[^1].X + ordered[^1].Width / 2;
        double step = (lastCenter - firstCenter) / (ordered.Count - 1);
        for (int i = 1; i < ordered.Count - 1; i++)
            ordered[i].X = firstCenter + step * i - ordered[i].Width / 2;
        FinishAlign("Distributed horizontally.");
    }

    [RelayCommand]
    public void DistributeVertically()
    {
        if (!TryBeginAlign(out var sel)) return;
        var ordered = sel.OrderBy(n => n.Y).ToList();
        double firstCenter = ordered[0].Y + ordered[0].Height / 2;
        double lastCenter = ordered[^1].Y + ordered[^1].Height / 2;
        double step = (lastCenter - firstCenter) / (ordered.Count - 1);
        for (int i = 1; i < ordered.Count - 1; i++)
            ordered[i].Y = firstCenter + step * i - ordered[i].Height / 2;
        FinishAlign("Distributed vertically.");
    }

    private void FinishAlign(string message)
    {
        foreach (var n in SelectedNodes) UpdateConnectedConnectors(n);
        StatusText = message;
    }

    #endregion

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
                var flowchart = new FlowchartDiagramAst { Direction = FlowchartDirection };
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
                    if (!Enum.TryParse<FlowArrowHead>(c.StartHead, true, out var startHead)) startHead = FlowArrowHead.None;
                    flowchart.Edges.Add(new FlowEdge
                    {
                        FromId = c.SourceNodeId,
                        ToId = c.TargetNodeId,
                        Label = c.Label,
                        LineStyle = lineStyle,
                        StartHead = startHead,
                        EndHead = endHead
                    });
                }
                foreach (var sg in _flowSubgraphs)
                    if (PruneSubgraph(sg, flowchart) is { } kept) flowchart.Subgraphs.Add(kept);
                foreach (var line in _flowStyleLines)
                    if (StyleLineForCanvas(line, flowchart, LinkIndexNow) is { } kept) flowchart.StyleLines.Add(kept);
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
                        Type = string.Equals(n.Shape, "Actor", StringComparison.OrdinalIgnoreCase) ? SequenceParticipantType.Actor : SequenceParticipantType.Participant,
                        CreatedInline = _sequenceScript.Any(st => st.Kind == SequenceStatementKind.Raw && SequenceCreateLine.IsCreate(st.Text)
                                                              && string.Equals(SequenceCreateLine.Target(st.Text), n.Id, StringComparison.OrdinalIgnoreCase))
                    });
                }
                // Boxes keep their surviving participants; one left empty is dropped.
                foreach (var box in _sequenceBoxes)
                {
                    var kept = new SequenceBox { Header = box.Header };
                    kept.ParticipantIds.AddRange(box.ParticipantIds.Where(id => Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase))));
                    if (kept.ParticipantIds.Count > 0) seq.Boxes.Add(kept);
                }
                seq.Statements.AddRange(BuildSequenceScript());
                seq.RebuildIndexes();
                seq.AutoNumber = existingSeq?.AutoNumber ?? _sequenceAutoNumber;
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
                            ClassDiagramParser.ParseClassMember(line, classNode);
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
                            ClassDiagramParser.ParseClassMember(line, classNode);
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
                // Notes go with their class ("note for X"); a free-standing note always stays.
                if (existingCls is not null)
                    foreach (var note in existingCls.NoteLines)
                    {
                        var target = System.Text.RegularExpressions.Regex.Match(note, @"^note\s+for\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!target.Success || cls.Classes.ContainsKey(target.Groups[1].Value)) cls.NoteLines.Add(note);
                    }
                return cls;

            case MermaidDiagramType.State:
                var state = new StateDiagramAst();
                // Start and end points (however many the canvas has) are all [*] in Mermaid.
                var pseudoIds = new HashSet<string>(Nodes.Where(n => n.IsPseudoState).Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
                string MermaidStateId(string id) => pseudoIds.Contains(id) ? StatePseudoId : id;
                foreach (var n in Nodes)
                {
                    if (n.IsPseudoState) continue;
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

                    var stateNode = new StateNode { Id = n.Id, Label = cleanLabel, Type = type };
                    // A composite state is one box on the canvas; what's inside it isn't drawn yet,
                    // but it is kept (it used to be emptied by every save).
                    if (type == StateNodeType.Composite && CurrentAst is StateDiagramAst loadedState
                        && loadedState.States.TryGetValue(n.Id, out var loadedComposite))
                    {
                        stateNode.SubStates.AddRange(loadedComposite.SubStates);
                        stateNode.SubTransitions.AddRange(loadedComposite.SubTransitions);
                    }
                    state.States[n.Id] = stateNode;
                }
                foreach (var c in Connectors)
                {
                    state.Transitions.Add(new StateTransition
                    {
                        FromId = MermaidStateId(c.SourceNodeId),
                        ToId = MermaidStateId(c.TargetNodeId),
                        EventLabel = c.Label
                    });
                }
                if (pseudoIds.Count > 0 && !state.States.ContainsKey(StatePseudoId))
                    state.States[StatePseudoId] = new StateNode { Id = StatePseudoId, Label = StatePseudoId, Type = StateNodeType.Start };
                // Notes go with the state they're attached to, wherever it sits.
                if (CurrentAst is StateDiagramAst loadedNotes)
                    foreach (var note in loadedNotes.Notes)
                        if (StateDiagramAst.NoteTarget(note) is not { } target || HasState(state.States.Values, target))
                            state.Notes.Add(note);
                return state;

                static bool HasState(IEnumerable<StateNode> nodes, string id) =>
                    nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || HasState(n.SubStates, id));

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

    // ---- Style presets (QODER task 5) ----
    // A preset recolors node fills, node borders and connector lines in one click and is persisted
    // into the generated mermaid code as `%%{init}%%` themeVariables so the look survives a reload.
    public sealed record DiagramPalette(string Name, string Fill, string Border, string Line, string Text);

    public static readonly IReadOnlyList<DiagramPalette> StylePresets = new[]
    {
        new DiagramPalette("Catppuccin Slate",  "#313244", "#585B70", "#89B4FA", "#CDD6F4"),
        new DiagramPalette("Nord Ocean",        "#2E3440", "#4C566A", "#88C0D0", "#ECEFF4"),
        new DiagramPalette("Emerald Corporate", "#062E23", "#10B981", "#34D399", "#ECFDF5"),
        new DiagramPalette("Monochrome Print",  "#FFFFFF", "#1F2937", "#111827", "#111827"),
    };

    partial void OnConnectorRoutingChanged(ConnectorRoutingMode value)
    {
        foreach (var c in Connectors) c.RoutingMode = value;
        UpdateAllConnectors();
        StatusText = $"Connector routing set to {value}.";
    }

    partial void OnActivePaletteChanged(string value)
    {
        var preset = StylePresets.FirstOrDefault(p => p.Name == value);
        if (preset is null) return; // empty/unknown -> keep current colors
        foreach (var n in Nodes)
        {
            n.FillColor = preset.Fill;
            n.StrokeColor = preset.Border;
        }
        foreach (var c in Connectors)
        {
            c.StrokeColor = preset.Line;
        }
        StatusText = $"Applied '{value}' palette.";
    }

    public string GenerateMermaidCode()
    {
        var ast = CanvasToAst();
        ApplyStudioDirectives(ast);
        return MermaidCodeGenerator.Generate(ast);
    }

    // Persists the Studio's routing + palette choices into the generated mermaid as an
    // `%%{init}%%` directive (and carries across any non-init directives from the loaded AST so
    // they aren't dropped by the canvas round trip). Emitted ONLY when the state differs from the
    // studio defaults, so untouched documents still round-trip byte-identically.
    private void ApplyStudioDirectives(MermaidDiagramAst ast)
    {
        if (CurrentAst is not null)
        {
            foreach (var dir in CurrentAst.Directives)
            {
                if (dir.StartsWith("%%{init", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ast.Directives.Contains(dir)) ast.Directives.Add(dir);
            }
        }

        var init = BuildInitDirective();
        if (init is not null) ast.Directives.Add(init);
    }

    private string? BuildInitDirective()
    {
        bool hasPalette = !string.IsNullOrEmpty(ActivePalette);
        bool hasCurve = ConnectorRouting != ConnectorRoutingMode.Orthogonal;
        if (!hasPalette && !hasCurve) return null;

        var parts = new List<string>();
        if (hasPalette)
        {
            var preset = StylePresets.First(p => p.Name == ActivePalette);
            parts.Add("'theme':'base'");
            parts.Add("'themeVariables':{'primaryColor':'" + preset.Fill +
                      "','primaryBorderColor':'" + preset.Border +
                      "','lineColor':'" + preset.Line +
                      "','primaryTextColor':'" + preset.Text + "'}");
        }
        if (hasCurve)
        {
            string curve = ConnectorRouting switch
            {
                ConnectorRoutingMode.Straight => "linear",
                ConnectorRoutingMode.Bezier => "basis",
                _ => "stepAfter"
            };
            parts.Add("'flowchart':{'curve':'" + curve + "'}");
        }
        return "%%{init: {" + string.Join(",", parts) + "}}%%";
    }

    // Re-applies the routing + palette encoded in a loaded diagram's `%%{init}%%` directive so the
    // canvas reflects the authored look (and the dirty baseline generated afterwards matches it).
    private void RestoreStudioStateFromDirectives()
    {
        var routing = ConnectorRoutingMode.Orthogonal;
        var palette = string.Empty;

        foreach (var dir in CurrentAst?.Directives ?? Enumerable.Empty<string>())
        {
            if (!dir.StartsWith("%%{init", StringComparison.OrdinalIgnoreCase)) continue;

            var curveMatch = System.Text.RegularExpressions.Regex.Match(dir, "'curve'\\s*:\\s*'(\\w+)'");
            if (curveMatch.Success)
            {
                routing = curveMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "linear" => ConnectorRoutingMode.Straight,
                    "basis" => ConnectorRoutingMode.Bezier,
                    _ => ConnectorRoutingMode.Orthogonal
                };
            }

            var colorMatch = System.Text.RegularExpressions.Regex.Match(dir, "'primaryColor'\\s*:\\s*'(#[0-9A-Fa-f]{3,8})'");
            if (colorMatch.Success)
            {
                var match = StylePresets.FirstOrDefault(p => p.Fill.Equals(colorMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                if (match is not null) palette = match.Name;
            }
        }

        ConnectorRouting = routing;
        ActivePalette = palette;
    }

    public string SyncToMarkdown(string markdown)
    {
        string newMermaidCode = GenerateMermaidCode();
        _savedCode = newMermaidCode; // syncing counts as saving — reset the dirty baseline

        // ISS-018: the canvas AST can't represent style/classDef/linkStyle directives or
        // per-subgraph directions — carry them across from the fence being replaced so the
        // canvas → AST → code round trip doesn't strip them. The dirty baseline above stays
        // pure generator output so HasUnsavedChanges keeps comparing like with like.
        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        if (ActiveBlockIndex >= 0 && ActiveBlockIndex < blocks.Count)
            newMermaidCode = Services.MermaidPreservationNormalizer.Preserve(newMermaidCode, blocks[ActiveBlockIndex].Code);

        RawMermaidCode = newMermaidCode;
        if (blocks.Count > 0)
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
