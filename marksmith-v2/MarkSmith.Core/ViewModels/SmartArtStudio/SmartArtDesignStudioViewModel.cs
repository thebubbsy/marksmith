using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;

namespace MarkSmith.ViewModels.SmartArtStudio;

public class StudioLayoutItem
{
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// What the gallery shows. Most packages have no title, so Name is just the alias
    /// ("AlternatingCircleProcess", "arrow1"); split those into words ("Alternating Circle
    /// Process", "Arrow 1") rather than printing the same identifier twice.
    /// </summary>
    public string DisplayName => !string.Equals(Name, Alias, StringComparison.Ordinal) ? Name
        : WordNames.TryGetValue(Alias, out var word) ? word : Humanize(ExpandPrefix(Alias));

    /// <summary>The drawing the preview (and the gallery miniature) uses for this layout.</summary>
    public SmartArtPreviewFamily Family { get; set; }

    /// <summary>The gallery miniature: this layout's own drawing as SVG markup (no text).</summary>
    public string ThumbnailSvg => HtmlPreviewRenderer.RenderThumbnailSvg(Alias);

    /// <summary>The names Word's SmartArt gallery shows for its built-in layouts, whose packages
    /// carry an empty title. Only the layouts whose Word name is certain are listed; the rest fall
    /// back to <see cref="ExpandPrefix"/> + <see cref="Humanize"/> ("hList7" → "Horizontal List 7")
    /// rather than risk a wrong name.</summary>
    internal static readonly IReadOnlyDictionary<string, string> WordNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = "Basic Block List",
        ["vList2"] = "Vertical Bullet List",
        ["list1"] = "Stacked List",
        ["hList1"] = "Horizontal Bullet List",
        ["process1"] = "Basic Process",
        ["process2"] = "Vertical Process",
        ["chevron1"] = "Basic Chevron Process",
        ["chevron2"] = "Vertical Chevron List",
        ["hChevron3"] = "Closed Chevron Process",
        ["hProcess3"] = "Continuous Arrow Process",
        ["hProcess9"] = "Continuous Block Process",
        ["hProcess11"] = "Basic Timeline",
        ["arrow2"] = "Upward Arrow",
        ["equation1"] = "Equation",
        ["equation2"] = "Vertical Equation",
        ["funnel1"] = "Funnel",
        ["gear1"] = "Gear",
        ["cycle1"] = "Text Cycle",
        ["cycle2"] = "Basic Cycle",
        ["cycle3"] = "Continuous Cycle",
        ["cycle4"] = "Cycle Matrix",
        ["cycle5"] = "Block Cycle",
        ["cycle6"] = "Nondirectional Cycle",
        ["cycle7"] = "Multidirectional Cycle",
        ["cycle8"] = "Segmented Cycle",
        ["chart3"] = "Basic Pie",
        ["orgChart1"] = "Organization Chart",
        ["hierarchy1"] = "Hierarchy",
        ["hierarchy2"] = "Horizontal Hierarchy",
        ["hierarchy3"] = "Hierarchy List",
        ["hierarchy4"] = "Table Hierarchy",
        ["hierarchy5"] = "Horizontal Labeled Hierarchy",
        ["hierarchy6"] = "Labeled Hierarchy",
        ["radial1"] = "Basic Radial",
        ["matrix1"] = "Titled Matrix",
        ["matrix2"] = "Grid Matrix",
        ["matrix3"] = "Basic Matrix",
        ["pyramid1"] = "Basic Pyramid",
        ["pyramid2"] = "Pyramid List",
        ["pyramid3"] = "Inverted Pyramid",
        ["pyramid4"] = "Segmented Pyramid",
        ["venn1"] = "Basic Venn",
        ["venn2"] = "Stacked Venn",
        ["venn3"] = "Linear Venn",
        ["target1"] = "Basic Target",
        ["target2"] = "Nested Target",
        ["target3"] = "Target List",
        ["balance1"] = "Balance",
        ["arrow1"] = "Diverging Arrows",
        ["arrow3"] = "Counterbalance Arrows",
        ["arrow4"] = "Opposing Arrows",
        ["arrow5"] = "Converging Arrows",
        ["arrow6"] = "Arrow Ribbon",
        ["bList2"] = "Bending Picture Accent List",
        ["bProcess2"] = "Circular Bending Process",
        ["bProcess4"] = "Repeating Bending Process",
        ["hList3"] = "Table List",
        ["hList6"] = "Trapezoid List",
        ["hProcess4"] = "Alternating Flow",
        ["hProcess6"] = "Process Arrows",
        ["hProcess7"] = "Detailed Process",
        ["lProcess2"] = "Grouped List",
        ["lProcess3"] = "Chevron List",
        ["pList1"] = "Picture Caption List",
        ["pList2"] = "Horizontal Picture List",
        ["process3"] = "Accent Process",
        ["radial3"] = "Radial Venn",
        ["radial4"] = "Converging Radial",
        ["radial5"] = "Diverging Radial",
        ["vList4"] = "Vertical Picture List",
        ["vList6"] = "Vertical Arrow List",
        ["PlusandMinus"] = "Plus and Minus",
        ["NameandTitleOrganizationalChart"] = "Name and Title Organization Chart",
        ["RandomtoResultProcess"] = "Random to Result Process",
        ["SubStepProcess"] = "Sub-Step Process",
        ["HorizontalMultiLevelHierarchy"] = "Horizontal Multi-Level Hierarchy",
        ["BendingPictureSemiTransparentText"] = "Bending Picture Semi-Transparent Text",
    };

    /// <summary>Office's short layout ids abbreviate their orientation: h = horizontal, v = vertical,
    /// b = bending, p = picture, l = list.</summary>
    internal static string ExpandPrefix(string id)
    {
        if (id.Length < 2 || !char.IsLower(id[0]) || !char.IsUpper(id[1])) return id;
        string? word = id[0] switch
        {
            'h' => "Horizontal",
            'v' => "Vertical",
            'b' => "Bending",
            'p' => "Picture",
            'l' => "List",
            _ => null,
        };
        return word is null ? id : word + id[1..];
    }

    /// <summary>Tooltip: the token used after <c>type=</c> in a <c>:::smartart</c> block.</summary>
    public string AliasHint => $"Markdown name: {Alias}";

    internal static string Humanize(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        var sb = new System.Text.StringBuilder(id.Length + 8);
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (c is '_' or '-') { sb.Append(' '); continue; }
            if (i > 0)
            {
                var prev = id[i - 1];
                var wordBreak =
                    (char.IsUpper(c) && char.IsLower(prev)) ||
                    (char.IsDigit(c) && char.IsLetter(prev)) ||
                    (char.IsLetter(c) && char.IsDigit(prev)) ||
                    // "SWOTMatrix" -> "SWOT Matrix": an upper followed by a lower ends an acronym.
                    (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < id.Length && char.IsLower(id[i + 1]));
                if (wordBreak && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : c);
        }
        // Title case keeps the small words small: "Meet The Team" -> "Meet the Team".
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "(?<=. )(The|And|To|Of)(?= )", m => m.Value.ToLowerInvariant());
    }
}

/// <summary>One node of the hierarchy the user is designing. The outline editor drives this
/// tree; every mutation flows back to Markdown (the canonical form the preview and DOCX
/// export consume) via SyncTreeToMarkdown.</summary>
public partial class StudioNodeViewModel : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _text = "New Node";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private int _depth;

    public StudioNodeViewModel? Parent { get; set; }
    public ObservableCollection<StudioNodeViewModel> Children { get; } = new();
}

/// <summary>
/// SmartArt Design Studio — outline-first authoring of the hierarchy. This is the DESIGN
/// surface: select a node and add children/siblings, rename inline (F2 / double-click),
/// delete, reorder, promote/demote — every operation is undoable (Ctrl+Z/Y) and lands in
/// the Markdown, which the live preview renders and the DOCX export turns into native Word
/// SmartArt. The backend (catalog/solver/generator) is untouched by this UI.
/// </summary>
public partial class SmartArtDesignStudioViewModel : ObservableObject
{
    [ObservableProperty]
    private string _markdownText = "- Executive Board\n  - CEO\n    - Engineering Team\n    - Product Team\n  - CFO\n  - CMO";

    [ObservableProperty]
    private string _previewHtml = "";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private ObservableCollection<string> _categories = new()
    {
        "All", "Hierarchy", "Process", "Cycle", "Matrix", "Pyramid", "Venn", "Relationship", "Picture List", "List"
    };

    [ObservableProperty]
    private string _selectedPalette = "Office Blue";

    partial void OnSelectedPaletteChanged(string value) => UpdatePreview();

    [ObservableProperty]
    private ObservableCollection<string> _palettes = new()
    {
        "Office Blue", "Emerald Forest", "Sunset Warmth", "Ocean Cyan", "Purple Modern", "Monochrome Dark"
    };

    [ObservableProperty]
    private ObservableCollection<StudioLayoutItem> _layouts = new();

    [ObservableProperty]
    private StudioLayoutItem? _selectedLayout;

    [ObservableProperty]
    private ObservableCollection<StudioNodeViewModel> _rootNodes = new();

    /// <summary>Depth-first flatten of the tree — the rows the outline editor binds to.</summary>
    [ObservableProperty]
    private ObservableCollection<StudioNodeViewModel> _outlineRows = new();

    [ObservableProperty]
    private string _statusMessage = "Pick a layout, then shape the outline — the preview follows every edit.";

    /// <summary>Name of the layout the preview is showing (the preview pane header).</summary>
    [ObservableProperty]
    private string _previewTitle = "";

    /// <summary>Shown above the preview when the layout will draw a single shape because the
    /// outline has one top-level item (empty when there's nothing to say).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewHint))]
    private string _previewHint = "";

    public bool HasPreviewHint => !string.IsNullOrEmpty(PreviewHint);

    /// <summary>Lists, processes, cycles… draw one shape per top-level item and put sub-items inside
    /// it as bullets (as Word does), so the default org-chart outline — one root — becomes a single
    /// box. Say so, instead of leaving the user wondering where their shapes went.</summary>
    internal static string SingleShapeHint(MarkSmith.Core.AST.CanonicalAst ast, string alias, string title)
    {
        var top = ast.Root.Children.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList();
        if (top.Count != 1 || top[0].Children.Count < 2) return "";
        var family = HtmlPreviewRenderer.ResolveFamily(alias);
        if (family is SmartArtPreviewFamily.Hierarchy or SmartArtPreviewFamily.HorizontalHierarchy
            or SmartArtPreviewFamily.BlockHierarchy or SmartArtPreviewFamily.Pyramid
            or SmartArtPreviewFamily.InvertedPyramid or SmartArtPreviewFamily.Radial or SmartArtPreviewFamily.Matrix)
            return "";
        return $"{title} draws one shape per top-level item, with sub-items as its bullet text. " +
               $"Promote (‹) the items under “{top[0].Text}” to give each its own shape.";
    }

    private readonly List<StudioLayoutItem> _allLayouts = new();

    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private const int MaxUndo = 50;
    private string _editSnapshot = "";
    private StudioNodeViewModel? _editingNode;

    public event EventHandler? PreviewHtmlChanged;

    /// <summary>Raised when the user asks to add the designed diagram to the ACTIVE document.
    /// Carries the complete <c>:::smartart type="…"</c> markdown block (nested bullets = the
    /// hierarchy), which the main preview renders and the DOCX export turns into native Word
    /// SmartArt. The studio itself never writes a document — the document flow owns output.</summary>
    public event EventHandler<string>? InsertToDocumentRequested;

    private StudioNodeViewModel? _selectedNode;

    public StudioNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            _selectedNode = value;
            foreach (var row in OutlineRows)
            {
                row.IsSelected = ReferenceEquals(row, value);
            }
            OnPropertyChanged();
            NotifySelectionCapabilities();
        }
    }

    public bool HasSelection => SelectedNode is not null;

    public bool CanMoveUp
    {
        get
        {
            if (SelectedNode is not { } node) return false;
            var siblings = node.Parent == null ? RootNodes : node.Parent.Children;
            return siblings.IndexOf(node) > 0;
        }
    }

    public bool CanMoveDown
    {
        get
        {
            if (SelectedNode is not { } node) return false;
            var siblings = node.Parent == null ? RootNodes : node.Parent.Children;
            int idx = siblings.IndexOf(node);
            return idx >= 0 && idx < siblings.Count - 1;
        }
    }

    public bool CanPromote => SelectedNode?.Parent is not null;

    public bool CanDemote
    {
        get
        {
            if (SelectedNode is not { } node) return false;
            var siblings = node.Parent == null ? RootNodes : node.Parent.Children;
            return siblings.IndexOf(node) > 0;
        }
    }

    private void NotifySelectionCapabilities()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(CanPromote));
        OnPropertyChanged(nameof(CanDemote));
    }

    /// <summary>True when every node has been deleted — drives the outline's empty state.</summary>
    public bool IsOutlineEmpty => OutlineRows.Count == 0;
    /// <summary>True when the gallery search filters out every layout.</summary>
    public bool HasNoLayoutMatches => Layouts.Count == 0;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public SmartArtDesignStudioViewModel()
    {
        LoadLayouts();
        SelectSuggestedLayout();
        RebuildTree();
        UpdatePreview();
        MarkKept();
    }

    // ---- work that hasn't gone anywhere ----
    // The outline and layout as the studio opened with them or last inserted them. Closing the
    // window used to drop an edited graphic without a word; the window asks while they differ.

    private (string Text, string? Layout) _kept;

    /// <summary>True when the outline or layout changed since the studio opened or last inserted,
    /// so closing it would lose the edit.</summary>
    public bool HasUnkeptWork => !IsOutlineEmpty && _kept != KeptState();

    /// <summary>Record the outline and layout as they stand as safe to close on.</summary>
    public void MarkKept() => _kept = KeptState();

    private (string, string?) KeptState() => ((MarkdownText ?? "").Trim(), SelectedLayout?.Alias);

    /// <summary>Open on the layout that suits the starting outline (an org chart for the sample
    /// hierarchy) instead of whichever layout sorts first alphabetically ("Accented Picture").</summary>
    private void SelectSuggestedLayout()
    {
        try
        {
            var suggested = SmartArtLayoutSuggester.Suggest(MarkdownAstParser.Parse(MarkdownText ?? ""));
            var pkg = suggested is null ? null : SmartArtLayoutCatalog.Shared.TryResolve(suggested);
            if (pkg is null) return;
            var match = Layouts.FirstOrDefault(l => string.Equals(l.Alias, Tail(pkg.UniqueId), StringComparison.OrdinalIgnoreCase));
            if (match is not null) SelectedLayout = match;
        }
        catch { /* keep the first layout */ }
    }

    partial void OnMarkdownTextChanged(string value) { RebuildTree(); UpdatePreview(); }

    /// <summary>Preloads pasted content into the studio with the suggested layout family
    /// pre-selected (the family→layout mapping is a small reviewable table, not 176 detectors —
    /// the user still picks the exact layout from the 176-layout gallery).</summary>
    public void Preload(string markdown, string layoutAlias)
    {
        // A preload starts a new design, so Ctrl+Z doesn't bring back the previous one, unless
        // that one was never inserted: the studio is reused, and sending a second selection to it
        // used to wipe an unfinished graphic for good. Then the old outline is one Ctrl+Z away.
        bool replacingUnkept = HasUnkeptWork;
        if (replacingUnkept)
        {
            PushUndo();
        }
        else
        {
            _undoStack.Clear();
            _redoStack.Clear();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
        MarkdownText = markdown; // triggers RebuildTree + UpdatePreview
        var item = _allLayouts.FirstOrDefault(l =>
                       string.Equals(l.Alias, layoutAlias, StringComparison.OrdinalIgnoreCase));
        if (item is null && !string.IsNullOrWhiteSpace(layoutAlias))
        {
            // The suggestion flow passes family aliases ("hierarchy"), but gallery items carry the
            // authoritative URN tail ("orgChart1") — bridge them through the catalog.
            var pkg = SmartArtLayoutCatalog.Shared.TryResolve(layoutAlias);
            if (pkg != null)
            {
                string tail = Tail(pkg.UniqueId);
                item = _allLayouts.FirstOrDefault(l =>
                    string.Equals(l.Alias, tail, StringComparison.OrdinalIgnoreCase));
            }
        }
        item ??= Layouts.FirstOrDefault();
        if (item is not null) SelectedLayout = item; // triggers UpdatePreview
        MarkKept(); // the preloaded outline is the document's own; nothing to lose yet
        if (replacingUnkept)
            StatusMessage = "Opened the new selection. Your previous outline wasn't inserted: Ctrl+Z brings it back.";
    }

    partial void OnSelectedLayoutChanged(StudioLayoutItem? value) => UpdatePreview();
    partial void OnSearchQueryChanged(string value) => FilterLayouts();
    partial void OnSelectedCategoryChanged(string value) => FilterLayouts();

    /// <summary>"176 layouts" / "12 of 176 layouts" under the gallery search.</summary>
    public string LayoutCountText => Layouts.Count == _allLayouts.Count
        ? $"{_allLayouts.Count} layouts"
        : $"{Layouts.Count} of {_allLayouts.Count} layouts";

    private void LoadLayouts()
    {
        _allLayouts.Clear();
        var catalog = SmartArtLayoutCatalog.Shared;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The full 176-layout native Office corpus: every embedded package is a gallery entry
        // (searchable), with the friendly alias families pinned at the top.
        foreach (var pkg in catalog.All.OrderBy(p => Tail(p.UniqueId), StringComparer.OrdinalIgnoreCase))
        {
            string alias = Tail(pkg.UniqueId);
            if (string.IsNullOrWhiteSpace(alias) || !seen.Add(pkg.UniqueId)) continue;
            _allLayouts.Add(new StudioLayoutItem
            {
                Name = string.IsNullOrWhiteSpace(pkg.Title) ? alias : pkg.Title,
                Alias = alias,
                Category = GuessCategory(alias, pkg),
                Family = HtmlPreviewRenderer.ResolveFamily(pkg.UniqueId),
            });
        }
        // Alphabetical by the name the row shows ("Basic Block List" sat under "d" for "default").
        _allLayouts.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName, b.DisplayName));
        FilterLayouts();
    }

    private void FilterLayouts()
    {
        Layouts.Clear();
        string q = (SearchQuery ?? "").Trim().ToLowerInvariant();
        string cat = SelectedCategory ?? "All";
        foreach (var item in _allLayouts)
        {
            if (!string.IsNullOrEmpty(q) && !item.Name.ToLower().Contains(q) && !item.Alias.ToLower().Contains(q)
                && !item.DisplayName.ToLower().Contains(q))
                continue;
            if (cat != "All" && !string.Equals(item.Category, cat, StringComparison.Ordinal))
                continue;
            Layouts.Add(item);
        }
        if (SelectedLayout == null || !Layouts.Contains(SelectedLayout))
            SelectedLayout = Layouts.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoLayoutMatches));
        OnPropertyChanged(nameof(LayoutCountText));
    }

    // ------------------------------------------------------------------ tree model

    public void Select(StudioNodeViewModel? node)
    {
        if (_editingNode != null && !ReferenceEquals(_editingNode, node))
        {
            CommitIfEditing(ref node);
        }
        SelectedNode = node;
    }

    private void CommitIfEditing(ref StudioNodeViewModel? target)
    {
        if (_editingNode == null) return;
        var targetPath = target != null ? PathOf(target) : null;
        CommitRename();
        if (targetPath is { Count: > 0 })
        {
            target = NodeAtPath(targetPath) ?? target;
        }
    }

    public void RebuildTree()
    {
        // Selection + inline-edit survive the rebuild via their POSITION PATH (root index +
        // child indices) — the markdown determines the tree deterministically, so a path
        // captured before the rebuild lands on the same node after it (node Ids are not stable
        // across rebuilds; the tree instances are recreated from the parsed AST).
        var selectedPath = PathOf(SelectedNode);
        var editingPath = PathOf(_editingNode);
        RootNodes.Clear();

        var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
        // The top-level bullets ARE ast.Root.Children — never synthesize a phantom root node
        // when the content has no list items (empty/deleted hierarchy or plain prose in the
        // Markdown box would otherwise show a bogus "Document Root" row).
        foreach (var n in ast.Root.Children)
        {
            RootNodes.Add(ToNode(n, null, 0));
        }

        RebuildOutline();
        SelectedNode = NodeAtPath(selectedPath);
        var editing = NodeAtPath(editingPath);
        _editingNode = editing;
        if (editing != null) editing.IsEditing = true;
        NotifySelectionCapabilities();
    }

    private List<int> PathOf(StudioNodeViewModel? node)
    {
        var path = new Stack<int>();
        var cur = node;
        while (cur != null && cur.Parent != null)
        {
            path.Push(cur.Parent.Children.IndexOf(cur));
            cur = cur.Parent;
        }
        if (cur != null)
        {
            path.Push(RootNodes.IndexOf(cur));
        }
        return path.ToList();
    }

    private StudioNodeViewModel? NodeAtPath(List<int> path)
    {
        if (path == null || path.Count == 0) return null;
        if (path[0] < 0 || path[0] >= RootNodes.Count) return null;
        var node = RootNodes[path[0]];
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] < 0 || path[i] >= node.Children.Count) return null;
            node = node.Children[path[i]];
        }
        return node;
    }

    private static StudioNodeViewModel ToNode(AstNode node, StudioNodeViewModel? parent, int depth)
    {
        var vm = new StudioNodeViewModel
        {
            Text = node.Text,
            Parent = parent,
            Depth = depth
        };
        foreach (var child in node.Children)
        {
            vm.Children.Add(ToNode(child, vm, depth + 1));
        }
        return vm;
    }

    private void RebuildOutline()
    {
        OutlineRows.Clear();
        void Walk(StudioNodeViewModel node)
        {
            OutlineRows.Add(node);
            foreach (var c in node.Children)
            {
                Walk(c);
            }
        }
        foreach (var root in RootNodes)
        {
            Walk(root);
        }
        OnPropertyChanged(nameof(IsOutlineEmpty));
        OnPropertyChanged(nameof(OutlineSummaryText));
        InsertIntoDocumentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Concise summary of the current outline item count and selected layout.</summary>
    public string OutlineSummaryText => OutlineRows.Count == 1
        ? $"1 item · {(string.IsNullOrWhiteSpace(PreviewTitle) ? (SelectedLayout?.DisplayName ?? "SmartArt") : PreviewTitle)}"
        : $"{OutlineRows.Count} items · {(string.IsNullOrWhiteSpace(PreviewTitle) ? (SelectedLayout?.DisplayName ?? "SmartArt") : PreviewTitle)}";

    /// <summary>Writes the designed tree back to Markdown (the canonical form). MarkdownText
    /// change re-runs RebuildTree + UpdatePreview, keeping everything in sync.</summary>
    public void SyncTreeToMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var root in RootNodes)
        {
            AppendMarkdown(sb, root, 0);
        }
        string generated = sb.ToString().TrimEnd();
        if (!string.Equals(MarkdownText, generated, StringComparison.Ordinal))
        {
            MarkdownText = generated;
        }
        else
        {
            RebuildTree();
            UpdatePreview();
        }
    }

    private static void AppendMarkdown(StringBuilder sb, StudioNodeViewModel node, int depth)
    {
        // LF consistently (the rest of the markdown pipeline normalizes to LF at render time).
        sb.Append(new string(' ', depth * 2)).Append("- ").Append(node.Text).Append('\n');
        foreach (var child in node.Children)
        {
            AppendMarkdown(sb, child, depth + 1);
        }
    }

    // ------------------------------------------------------------------ design operations

    public void PushUndo()
    {
        _undoStack.Add(MarkdownText ?? "");
        if (_undoStack.Count > MaxUndo) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void ApplyHistory(string markdown)
    {
        MarkdownText = markdown; // OnMarkdownTextChanged rebuilds the tree + preview
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    public void Undo()
    {
        if (_editingNode != null) CommitRename();
        if (_undoStack.Count == 0) return;
        _redoStack.Add(MarkdownText ?? "");
        string target = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        ApplyHistory(target);
        StatusMessage = "Undid outline edit · Ctrl+Y to redo";
    }

    [RelayCommand]
    public void Redo()
    {
        if (_editingNode != null) CommitRename();
        if (_redoStack.Count == 0) return;
        _undoStack.Add(MarkdownText ?? "");
        string target = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        ApplyHistory(target);
        StatusMessage = "Redid outline edit · Ctrl+Z to undo";
    }

    /// <summary>Adds a child under the given node (or the selection; or a new root when the
    /// tree is empty). The new node drops straight into inline rename.</summary>
    [RelayCommand]
    public void AddChild(StudioNodeViewModel? target = null)
    {
        CommitIfEditing(ref target);
        var parent = target ?? SelectedNode;
        PushUndo();
        var node = new StudioNodeViewModel { Text = "New Node", Parent = parent, Depth = (parent?.Depth ?? -1) + 1 };
        if (parent == null) RootNodes.Add(node);
        else parent.Children.Add(node);
        Select(node);
        BeginRename(node);
        SyncTreeToMarkdown();
        StatusMessage = parent == null ? "Added first item · Ctrl+Z to undo" : $"Added child under '{parent.Text}' · Ctrl+Z to undo";
    }

    [RelayCommand]
    public void AddSibling(StudioNodeViewModel? target = null)
    {
        CommitIfEditing(ref target);
        var node = target ?? SelectedNode;
        if (node == null) { AddChild(null); return; }
        PushUndo();
        var sibling = new StudioNodeViewModel { Text = "New Node", Parent = node.Parent, Depth = node.Depth };
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        siblings.Insert(idx + 1, sibling);
        Select(sibling);
        BeginRename(sibling);
        SyncTreeToMarkdown();
        StatusMessage = $"Added sibling after '{node.Text}' · Ctrl+Z to undo";
    }

    /// <summary>Duplicates the target (or selected) node and all of its children as the next sibling.</summary>
    [RelayCommand]
    public void DuplicateSelected(StudioNodeViewModel? target = null)
    {
        CommitIfEditing(ref target);
        var node = target ?? SelectedNode;
        if (node == null) return;
        PushUndo();
        var clone = CloneSubtree(node, node.Parent, node.Depth);
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        siblings.Insert(idx + 1, clone);
        Select(clone);
        SyncTreeToMarkdown();
        StatusMessage = $"Duplicated '{node.Text}' · Ctrl+Z to undo";
    }

    private static StudioNodeViewModel CloneSubtree(StudioNodeViewModel source, StudioNodeViewModel? parent, int depth)
    {
        var copy = new StudioNodeViewModel
        {
            Text = source.Text,
            Parent = parent,
            Depth = depth
        };
        foreach (var child in source.Children)
        {
            copy.Children.Add(CloneSubtree(child, copy, depth + 1));
        }
        return copy;
    }

    [RelayCommand]
    public void DeleteSelected(StudioNodeViewModel? target = null)
    {
        CommitIfEditing(ref target);
        var node = target ?? SelectedNode;
        if (node == null) return;
        string name = node.Text;
        PushUndo();
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        siblings.Remove(node);

        StudioNodeViewModel? next = null;
        if (siblings.Count > 0) next = idx < siblings.Count ? siblings[idx] : siblings[^1];
        else next = node.Parent;
        Select(next);
        SyncTreeToMarkdown();
        StatusMessage = $"Deleted '{name}' · Ctrl+Z to undo";
    }

    [RelayCommand]
    public void MoveUp()
    {
        if (_editingNode != null) CommitRename();
        var node = SelectedNode;
        if (node == null) return;
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        if (idx <= 0)
        {
            StatusMessage = $"'{node.Text}' is already the first sibling";
            return;
        }
        PushUndo();
        siblings.Move(idx, idx - 1);
        Select(node);
        SyncTreeToMarkdown();
        StatusMessage = $"Moved '{node.Text}' up";
    }

    [RelayCommand]
    public void MoveDown()
    {
        if (_editingNode != null) CommitRename();
        var node = SelectedNode;
        if (node == null) return;
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        if (idx < 0 || idx >= siblings.Count - 1)
        {
            StatusMessage = $"'{node.Text}' is already the last sibling";
            return;
        }
        PushUndo();
        siblings.Move(idx, idx + 1);
        Select(node);
        SyncTreeToMarkdown();
        StatusMessage = $"Moved '{node.Text}' down";
    }

    /// <summary>Outdents: moves the node up one level, becoming a sibling of its parent.</summary>
    [RelayCommand]
    public void Promote()
    {
        if (_editingNode != null) CommitRename();
        var node = SelectedNode;
        if (node == null) return;
        if (node.Parent == null)
        {
            StatusMessage = $"'{node.Text}' is already at the top level";
            return;
        }
        PushUndo();
        var oldParent = node.Parent;
        var grandparent = oldParent.Parent;
        var oldSiblings = oldParent.Children;
        int idx = oldSiblings.IndexOf(node);
        oldSiblings.RemoveAt(idx);

        var parentSiblings = grandparent == null ? RootNodes : grandparent.Children;
        int parentIdx = parentSiblings.IndexOf(oldParent);
        node.Parent = grandparent;
        parentSiblings.Insert(parentIdx + 1, node);
        Select(node);
        SyncTreeToMarkdown();
        StatusMessage = $"Promoted '{node.Text}' out one level";
    }

    /// <summary>Indents: moves the node under its previous sibling.</summary>
    [RelayCommand]
    public void Demote()
    {
        if (_editingNode != null) CommitRename();
        var node = SelectedNode;
        if (node == null) return;
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        if (idx <= 0)
        {
            StatusMessage = $"'{node.Text}' has no previous sibling to nest under";
            return;
        }
        PushUndo();
        var prev = siblings[idx - 1];
        siblings.RemoveAt(idx);
        prev.Children.Add(node);
        node.Parent = prev;
        Select(node);
        SyncTreeToMarkdown();
        StatusMessage = $"Demoted '{node.Text}' in one level";
    }

    /// <summary>Keys the outline answers to while it has keyboard focus.</summary>
    public enum OutlineKey { Up, Down, Home, End, Tab, Enter, Insert, Delete, F2, Left, Right }

    /// <summary>
    /// The outline's keyboard model, so the whole tree can be built without the mouse (it used to
    /// answer only Delete and F2):
    /// ↑/↓ and Home/End move the selection; Alt+↑/↓ move the item among its siblings;
    /// Tab / Shift+Tab (or Alt+Right / Alt+Left) indent and outdent; Enter adds a sibling and Insert
    /// a child (both open for typing); Delete deletes; F2 renames. Returns whether the key did
    /// something, so the window lets an unused key (Tab with nothing selected) carry on to focus navigation.
    /// </summary>
    public bool HandleOutlineKey(OutlineKey key, bool shift = false, bool alt = false)
    {
        if (OutlineRows.Count == 0)
        {
            if (key is OutlineKey.Enter or OutlineKey.Insert) { AddChild(null); return true; }
            return false;
        }
        var node = SelectedNode;
        int row = node is null ? -1 : OutlineRows.IndexOf(node);
        switch (key)
        {
            case OutlineKey.Up when alt:
                if (node is null) return false;
                MoveUp();
                return true;
            case OutlineKey.Down when alt:
                if (node is null) return false;
                MoveDown();
                return true;
            case OutlineKey.Left when alt:
                if (node is null) return false;
                Promote();
                return true;
            case OutlineKey.Right when alt:
                if (node is null) return false;
                Demote();
                return true;
            case OutlineKey.Up:
                Select(OutlineRows[row <= 0 ? 0 : row - 1]);
                if (SelectedNode is { } upNode)
                    StatusMessage = $"{upNode.Text} · level {upNode.Depth + 1}";
                return true;
            case OutlineKey.Down:
                Select(OutlineRows[row < 0 ? 0 : Math.Min(OutlineRows.Count - 1, row + 1)]);
                if (SelectedNode is { } downNode)
                    StatusMessage = $"{downNode.Text} · level {downNode.Depth + 1}";
                return true;
            case OutlineKey.Home:
                Select(OutlineRows[0]);
                if (SelectedNode is { } homeNode)
                    StatusMessage = $"{homeNode.Text} · level {homeNode.Depth + 1}";
                return true;
            case OutlineKey.End:
                Select(OutlineRows[^1]);
                if (SelectedNode is { } endNode)
                    StatusMessage = $"{endNode.Text} · level {endNode.Depth + 1}";
                return true;
            case OutlineKey.Tab:
                if (node is null) return false;
                if (shift) Promote(); else Demote();
                return true;
            case OutlineKey.Enter:
                if (node is null) return false;
                AddSibling(node);
                return true;
            case OutlineKey.Insert:
                if (node is null) return false;
                AddChild(node);
                return true;
            case OutlineKey.Delete:
                if (node is null) return false;
                DeleteSelected(node);
                return true;
            case OutlineKey.F2:
                if (node is null) return false;
                BeginRename(node);
                return true;
        }
        return false;
    }

    [RelayCommand]
    public void BeginRename(StudioNodeViewModel? target = null)
    {
        CommitIfEditing(ref target);
        var node = target ?? SelectedNode;
        if (node == null) return;
        _editSnapshot = node.Text;
        Select(node);
        _editingNode = node;
        node.IsEditing = true;
    }

    /// <summary>Ends the inline rename (Enter / focus loss). The two-way text binding already
    /// applied the new name; this pushes it into the Markdown — and onto the undo stack, so
    /// Ctrl+Z reverts the rename itself, not the previous design operation.</summary>
    public void CommitRename()
    {
        var editing = _editingNode;
        string before = _editSnapshot;
        bool wasEditing = editing != null;
        foreach (var row in OutlineRows) row.IsEditing = false;
        _editingNode = null;
        if (wasEditing && editing != null &&
            !string.Equals(before, editing.Text, StringComparison.Ordinal))
        {
            string renamedTo = editing.Text;
            PushUndo();
            SyncTreeToMarkdown();
            StatusMessage = $"Renamed to '{renamedTo}' · Ctrl+Z to undo";
        }
    }

    /// <summary>Aborts the inline rename (Esc), restoring the pre-edit text.</summary>
    public void CancelRename()
    {
        foreach (var row in OutlineRows)
        {
            if (!row.IsEditing) continue;
            row.Text = _editSnapshot;
            row.IsEditing = false;
        }
        _editingNode = null;
    }

    // ------------------------------------------------------------------ preview + insert

    public void UpdatePreview()
    {
        try
        {
            var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
            string alias = SelectedLayout?.Alias ?? MarkSmith.Core.Glox.SmartArtLayoutSuggester.Suggest(ast) ?? "list";
            string title = SelectedLayout?.DisplayName
                ?? MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.TryResolve(alias)?.Title
                ?? StudioLayoutItem.Humanize(alias);

            PreviewHtml = HtmlPreviewRenderer.RenderHtml(ast, alias, title, SelectedPalette);
            PreviewTitle = title;
            PreviewHint = SingleShapeHint(ast, alias, title);
            OnPropertyChanged(nameof(OutlineSummaryText));
            PreviewHtmlChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview error: {ex.Message}";
        }
    }

    private bool CanInsert => !IsOutlineEmpty;

    public bool TryBuildSmartArtBlock(out string block, out string layoutDisplayName, out string errorMessage)
    {
        if (_editingNode != null) CommitRename();
        block = string.Empty;
        layoutDisplayName = string.Empty;
        errorMessage = string.Empty;

        var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
        string alias = SelectedLayout?.Alias ?? MarkSmith.Core.Glox.SmartArtLayoutSuggester.Suggest(ast) ?? "list";
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias);
        if (pkg == null)
        {
            errorMessage = "Insert error: the selected layout is not available.";
            return false;
        }

        string inner = (MarkdownText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(inner))
        {
            errorMessage = "Insert error: build a hierarchy first.";
            return false;
        }

        var sb = new StringBuilder();
        // Leading/trailing blank lines: the preview + DOCX block extractors require the marker to
        // sit on its own paragraph, so the block stays valid wherever the caret is.
        sb.AppendLine();
        sb.AppendLine($":::smartart type=\"{alias}\"");
        sb.AppendLine(inner);
        sb.AppendLine(":::");
        block = sb.ToString();
        layoutDisplayName = SelectedLayout?.DisplayName
            ?? (string.IsNullOrWhiteSpace(pkg.Title) ? StudioLayoutItem.Humanize(alias) : pkg.Title);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanInsert))]
    public void InsertIntoDocument()
    {
        if (!TryBuildSmartArtBlock(out string block, out string name, out string err))
        {
            StatusMessage = err;
            return;
        }

        InsertToDocumentRequested?.Invoke(this, block);
        MarkKept();
        // Built-in packages have an empty title ("✓ Added  to the document"); use the gallery's name.
        StatusMessage = $"✓ Added {name} to the document — preview & export it there.";
    }

    private static string Tail(string urn)
    {
        int idx = urn.LastIndexOf('/');
        return idx >= 0 ? urn[(idx + 1)..] : urn;
    }

    /// <summary>The gallery category follows the drawing the preview uses, so a row's label, its icon
    /// and the preview always agree (a keyword guess filed "Picture Grid" under Matrix).</summary>
    private static string GuessCategory(string hint, GloxPackage pkg) =>
        HtmlPreviewRenderer.ResolveFamily(pkg.UniqueId) switch
        {
            SmartArtPreviewFamily.Hierarchy or SmartArtPreviewFamily.HorizontalHierarchy
                or SmartArtPreviewFamily.BlockHierarchy or SmartArtPreviewFamily.HierarchyList => "Hierarchy",
            SmartArtPreviewFamily.Process or SmartArtPreviewFamily.Chevron or SmartArtPreviewFamily.VerticalProcess
                or SmartArtPreviewFamily.BendingProcess or SmartArtPreviewFamily.StepsUp or SmartArtPreviewFamily.StepsDown
                or SmartArtPreviewFamily.Timeline or SmartArtPreviewFamily.Equation => "Process",
            SmartArtPreviewFamily.Cycle => "Cycle",
            SmartArtPreviewFamily.Matrix => "Matrix",
            SmartArtPreviewFamily.Pyramid or SmartArtPreviewFamily.InvertedPyramid => "Pyramid",
            SmartArtPreviewFamily.Venn or SmartArtPreviewFamily.LinearVenn => "Venn",
            SmartArtPreviewFamily.Radial or SmartArtPreviewFamily.Balance or SmartArtPreviewFamily.Target => "Relationship",
            SmartArtPreviewFamily.Pictures => "Picture List",
            _ => "List",
        };

    /// <summary>
    /// Computes the word count and character count across an outline item collection.
    /// </summary>
    public static (int WordCount, int CharCount) ComputeOutlineMetrics(IEnumerable<string> items)
    {
        if (items is null) return (0, 0);
        int words = 0;
        int chars = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item)) continue;
            chars += item.Length;
            var parts = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            words += parts.Length;
        }
        return (words, chars);
    }

    public static (int WordCount, int CharCount, int NodeCount) ComputeOutlineMetrics(IEnumerable<StudioNodeViewModel> nodes)
    {
        if (nodes is null) return (0, 0, 0);
        var list = nodes.ToList();
        var (words, chars) = ComputeOutlineMetrics(list.Select(n => n.Text ?? ""));
        return (words, chars, list.Count);
    }

    /// <summary>
    /// Reverses the line order of an outline markdown string, preserving trailing CRLF if present.
    /// </summary>
    public static string ReverseOutline(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown ?? "";
        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var lines = normalized.Split('\n');
        var list = new List<string>(lines);
        list.Reverse();
        var result = string.Join("\n", list);
        return hasCr ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>
    /// Sorts the lines of an outline markdown string alphabetically (A-Z or Z-A).
    /// </summary>
    public static string SortOutlineAlphabetically(string markdown, bool ascending = true)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown ?? "";
        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var lines = normalized.Split('\n');
        var list = new List<string>(lines);
        if (ascending)
        {
            list.Sort((a, b) => string.Compare(a.TrimStart('-', '*', ' '), b.TrimStart('-', '*', ' '), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            list.Sort((a, b) => string.Compare(b.TrimStart('-', '*', ' '), a.TrimStart('-', '*', ' '), StringComparison.OrdinalIgnoreCase));
        }
        var result = string.Join("\n", list);
        return hasCr ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>
    /// Trims trailing whitespace from every line of an outline markdown string,
    /// preserving indentation levels and original line break format (CRLF vs LF).
    /// </summary>
    public static string TrimTrailingWhitespace(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown ?? "";
        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }
        var result = string.Join("\n", lines);
        return hasCr ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>
    /// Standardises bullet list markers (e.g. converting '*', '+', or custom markers to '-')
    /// while strictly preserving indentation whitespace, tabs, and line ending format (CRLF vs LF).
    /// </summary>
    public static string StandardiseBullets(string markdown, char targetBullet = '-')
    {
        if (string.IsNullOrEmpty(markdown)) return markdown ?? "";
        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int leadWs = 0;
            while (leadWs < line.Length && (line[leadWs] == ' ' || line[leadWs] == '\t'))
            {
                leadWs++;
            }

            if (leadWs < line.Length)
            {
                var rest = line.Substring(leadWs);
                if (rest.StartsWith("* ") || rest.StartsWith("+ ") || rest.StartsWith("- "))
                {
                    lines[i] = line.Substring(0, leadWs) + targetBullet + " " + rest.Substring(2);
                }
            }
        }
        var result = string.Join("\n", lines);
        return hasCr ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>
    /// Normalizes outline bullet indentation to a uniform number of spaces per depth level (default 2),
    /// converting mixed tabs or irregular space widths while strictly preserving line endings (CRLF vs LF).
    /// </summary>
    public static string NormalizeIndentation(string markdown, int spacesPerLevel = 2)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown ?? string.Empty;
        if (spacesPerLevel < 1) spacesPerLevel = 2;
        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < line.Length && (line[leadWs] == ' ' || line[leadWs] == '\t'))
            {
                if (line[leadWs] == '\t') spaceCount += spacesPerLevel;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < line.Length)
            {
                var rest = line.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ "))
                {
                    int depth = spaceCount / spacesPerLevel;
                    lines[i] = new string(' ', depth * spacesPerLevel) + rest;
                }
            }
        }
        var result = string.Join("\n", lines);
        return hasCr ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>
    /// Analyzes bullet outline text to determine total nodes, root nodes, leaf nodes, maximum depth, and formats a summary.
    /// </summary>
    public static (int TotalNodes, int RootNodes, int LeafNodes, int MaxDepth, string FormattedSummary) AnalyzeHierarchyMetrics(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return (0, 0, 0, 0, "No nodes in outline");

        var normalized = markdown.Replace("\r", "");
        var rawLines = normalized.Split('\n');
        var depths = new List<int>();

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    int depth = (spaceCount / 2) + 1; // 1-based depth
                    depths.Add(depth);
                }
            }
        }

        if (depths.Count == 0)
            return (0, 0, 0, 0, "No nodes in outline");

        int total = depths.Count;
        int roots = depths.Count(d => d == 1);
        int maxDepth = depths.Max();
        int leaves = 0;

        for (int i = 0; i < depths.Count; i++)
        {
            if (i == depths.Count - 1 || depths[i + 1] <= depths[i])
            {
                leaves++;
            }
        }

        var rootLabel = roots == 1 ? "1 root" : $"{roots} roots";
        var leafLabel = leaves == 1 ? "1 leaf" : $"{leaves} leaves";
        var summary = $"Hierarchy: {total} nodes · {rootLabel} · {leafLabel} · max depth {maxDepth}";
        return (total, roots, leaves, maxDepth, summary);
    }

    /// <summary>
    /// Filters a markdown bullet outline to only include items up to the specified maximum depth (1 = top-level roots only).
    /// Preserves original line endings (CRLF or LF).
    /// </summary>
    public static string FormatOutlineAtMaxDepth(string markdown, int maxDepth = 1)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        if (maxDepth < 1) maxDepth = 1;

        bool hasCr = markdown.Contains('\r');
        var normalized = markdown.Replace("\r", "");
        var rawLines = normalized.Split('\n');
        var resultLines = new List<string>();

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    int depth = (spaceCount / 2) + 1;
                    if (depth <= maxDepth)
                    {
                        resultLines.Add(rawLine);
                    }
                }
            }
        }

        var result = string.Join("\n", resultLines);
        return hasCr ? result.Replace("\n", "\r\n") : result;
    }

    public record OutlineLineLengthAudit(int TotalLines, int LongestLineLength, string LongestLineText, int ExceedingCount, int Threshold);

    /// <summary>
    /// Audits markdown outline lines for character lengths against a recommended threshold (default 40 chars).
    /// </summary>
    public static OutlineLineLengthAudit AuditOutlineLineLengths(string? markdown, int maxRecommendedLength = 40)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new OutlineLineLengthAudit(0, 0, string.Empty, 0, maxRecommendedLength);

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int totalLines = 0;
        int longestLen = 0;
        string longestText = string.Empty;
        int exceedingCount = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            totalLines++;

            var content = trimmed;
            if (content.StartsWith("- ") || content.StartsWith("* ") || content.StartsWith("+ "))
                content = content.Substring(2).TrimStart();
            else if (char.IsDigit(content[0]))
            {
                int dotIdx = content.IndexOf('.');
                if (dotIdx > 0 && dotIdx < 4 && dotIdx + 1 < content.Length && content[dotIdx + 1] == ' ')
                    content = content.Substring(dotIdx + 2).TrimStart();
            }

            if (content.Length > longestLen)
            {
                longestLen = content.Length;
                longestText = content;
            }

            if (content.Length > maxRecommendedLength)
            {
                exceedingCount++;
            }
        }

        return new OutlineLineLengthAudit(totalLines, longestLen, longestText, exceedingCount, maxRecommendedLength);
    }

    /// <summary>
    /// Formats the outline line length audit into a clear single-line summary string.
    /// </summary>
    public static string FormatOutlineLineLengthAuditSummary(string? markdown, int maxRecommendedLength = 40)
    {
        var audit = AuditOutlineLineLengths(markdown, maxRecommendedLength);
        if (audit.TotalLines == 0) return "Outline is empty (0 lines)";

        var warning = audit.ExceedingCount > 0
            ? $" · ⚠️ {audit.ExceedingCount} lines exceed {audit.Threshold} chars"
            : $" · All lines within {audit.Threshold}-char limit";

        var preview = audit.LongestLineLength > 0
            ? $" (Longest: {audit.LongestLineLength} chars: \"{TruncateString(audit.LongestLineText, 25)}\")"
            : string.Empty;

        return $"{audit.TotalLines} lines{warning}{preview}";
    }

    private static string TruncateString(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max - 3) + "...";

    /// <summary>
    /// Computes item counts per depth level (e.g. L1, L2, L3) and formats an executive hierarchy breakdown summary.
    /// </summary>
    public static string FormatOutlineHierarchyBreakdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "Outline is empty (0 items)";
        var lines = markdown.Replace("\r", "").Split('\n');
        var depthCounts = new Dictionary<int, int>();
        int totalItems = 0;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    int depth = (spaceCount / 2) + 1;
                    depthCounts[depth] = depthCounts.GetValueOrDefault(depth, 0) + 1;
                    totalItems++;
                }
            }
        }

        if (totalItems == 0) return "Outline has 0 structured items";

        int maxDepth = depthCounts.Keys.Max();
        var breakdown = string.Join(", ", depthCounts.OrderBy(k => k.Key).Select(k => $"L{k.Key}: {k.Value}"));
        return $"Outline: {totalItems} items across {depthCounts.Count} level{(depthCounts.Count == 1 ? "" : "s")} ({breakdown}) · Max Depth: {maxDepth}";
    }

    /// <summary>
    /// Audits the outline hierarchy structure for branching factor, parent nodes, and leaf node distribution.
    /// </summary>
    public static (int TotalNodes, int BranchCount, int LeafCount, double AvgBranching, int MaxBranching) AuditOutlineBranching(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return (0, 0, 0, 0.0, 0);

        var lines = markdown.Replace("\r", "").Split('\n');
        var depths = new List<int>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    int depth = (spaceCount / 2) + 1;
                    depths.Add(depth);
                }
            }
        }

        if (depths.Count == 0) return (0, 0, 0, 0.0, 0);

        int branchCount = 0;
        int leafCount = 0;
        var directChildCounts = new List<int>();

        for (int i = 0; i < depths.Count; i++)
        {
            int d = depths[i];
            bool hasChild = (i + 1 < depths.Count && depths[i + 1] > d);
            if (hasChild)
            {
                branchCount++;
                int directChildren = 0;
                for (int j = i + 1; j < depths.Count; j++)
                {
                    if (depths[j] <= d) break;
                    if (depths[j] == d + 1) directChildren++;
                }
                directChildCounts.Add(directChildren);
            }
            else
            {
                leafCount++;
            }
        }

        int maxBranch = directChildCounts.Count > 0 ? directChildCounts.Max() : 0;
        double avgBranch = directChildCounts.Count > 0 ? (double)directChildCounts.Sum() / directChildCounts.Count : 0.0;

        return (depths.Count, branchCount, leafCount, avgBranch, maxBranch);
    }

    /// <summary>
    /// Formats a human-readable summary of outline branching, leaf nodes, and hierarchy balance.
    /// </summary>
    public static string FormatOutlineBranchingSummary(string? markdown)
    {
        var (total, branches, leaves, avgBranch, maxBranch) = AuditOutlineBranching(markdown);
        if (total == 0) return "Outline branching: Empty outline (0 nodes)";

        if (branches == 0)
        {
            return $"Outline branching: {total} nodes (All {leaves} are flat root items, 0 branches)";
        }

        return $"Outline branching: {total} nodes ({branches} branch{(branches == 1 ? "" : "es")}, {leaves} leaf/leaves) · Avg: {avgBranch:0.#} children/branch (Max: {maxBranch})";
    }

    /// <summary>
    /// Audits the outline hierarchy subtree balance across root-level nodes.
    /// Returns total roots, sizes of each root subtree (root + its descendants), standard deviation of subtree sizes, and balance state.
    /// </summary>
    public static (int RootCount, List<int> SubtreeSizes, double StdDev, bool IsBalanced) AuditOutlineBalance(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return (0, new List<int>(), 0.0, true);

        var lines = markdown.Replace("\r", "").Split('\n');
        var depths = new List<int>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    int depth = (spaceCount / 2) + 1;
                    depths.Add(depth);
                }
            }
        }

        if (depths.Count == 0) return (0, new List<int>(), 0.0, true);

        var rootIndices = new List<int>();
        for (int i = 0; i < depths.Count; i++)
        {
            if (depths[i] == 1) rootIndices.Add(i);
        }

        if (rootIndices.Count == 0)
        {
            rootIndices.Add(0);
        }

        var subtreeSizes = new List<int>();
        for (int r = 0; r < rootIndices.Count; r++)
        {
            int start = rootIndices[r];
            int nextStart = (r + 1 < rootIndices.Count) ? rootIndices[r + 1] : depths.Count;
            int size = nextStart - start;
            subtreeSizes.Add(size);
        }

        if (subtreeSizes.Count <= 1)
        {
            return (subtreeSizes.Count, subtreeSizes, 0.0, true);
        }

        double mean = (double)subtreeSizes.Sum() / subtreeSizes.Count;
        double sumSquares = subtreeSizes.Sum(s => Math.Pow(s - mean, 2));
        double stdDev = Math.Sqrt(sumSquares / subtreeSizes.Count);
        bool isBalanced = stdDev <= 1.5;

        return (subtreeSizes.Count, subtreeSizes, stdDev, isBalanced);
    }

    /// <summary>
    /// Formats a human-readable summary of outline root subtree balance.
    /// </summary>
    public static string FormatOutlineBalanceSummary(string? markdown)
    {
        var (roots, sizes, stdDev, isBalanced) = AuditOutlineBalance(markdown);
        if (roots == 0) return "Outline balance: Empty outline (0 nodes)";

        if (roots == 1)
        {
            return $"Outline balance: 1 root tree ({sizes[0]} total nodes)";
        }

        string sizeList = string.Join(", ", sizes);
        string status = isBalanced ? "Balanced" : "Skewed";
        return $"Outline balance: {roots} root trees ({status}: subtree sizes [{sizeList}] · σ={stdDev:0.#})";
    }

    /// <summary>
    /// Audits the outline hierarchy depth distribution across all nodes.
    /// Returns total nodes, max depth, average depth, distribution per level, and whether indentation steps are uniform (no skipped levels).
    /// </summary>
    public static (int TotalNodes, int MaxDepth, double AvgDepth, Dictionary<int, int> LevelCounts, bool IsStepUniform) AuditOutlineDepths(string? markdown)
    {
        var levelCounts = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (0, 0, 0.0, levelCounts, true);
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        var depths = new List<int>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    int depth = (spaceCount / 2) + 1;
                    depths.Add(depth);
                    levelCounts[depth] = levelCounts.TryGetValue(depth, out int c) ? c + 1 : 1;
                }
            }
        }

        if (depths.Count == 0)
        {
            return (0, 0, 0.0, levelCounts, true);
        }

        int maxDepth = depths.Max();
        double avgDepth = (double)depths.Sum() / depths.Count;

        bool isStepUniform = true;
        int prevDepth = 1;
        for (int i = 0; i < depths.Count; i++)
        {
            int d = depths[i];
            if (d > prevDepth + 1)
            {
                isStepUniform = false;
                break;
            }
            prevDepth = d;
        }

        return (depths.Count, maxDepth, avgDepth, levelCounts, isStepUniform);
    }

    /// <summary>
    /// Formats a human-readable summary of outline depth distribution and indentation uniformity.
    /// </summary>
    public static string FormatOutlineDepthDistributionSummary(string? markdown)
    {
        var (total, maxDepth, avgDepth, levelCounts, isStepUniform) = AuditOutlineDepths(markdown);
        if (total == 0) return "Outline depth: Empty outline (0 nodes)";

        if (maxDepth == 1)
        {
            return $"Outline depth: {total} nodes (All flat at Level 1 · Uniform)";
        }

        var levelsList = levelCounts
            .OrderBy(kv => kv.Key)
            .Select(kv => $"L{kv.Key}: {kv.Value}")
            .ToList();
        string levelsStr = string.Join(", ", levelsList);

        string uniformityStr = isStepUniform ? "Uniform step hierarchy" : "Non-uniform indentation (contains skipped levels)";
        return $"Outline depth: {total} nodes (Max: L{maxDepth}, Avg: {avgDepth:0.#}) · [{levelsStr}] · {uniformityStr}";
    }

    /// <summary>
    /// Reverses the top-level blocks of an outline markdown string while preserving all child sub-bullets
    /// under their respective parents in original order.
    /// </summary>
    public static (string ReversedMarkdown, int BlockCount, int TotalLineCount) ReverseOutlineHierarchically(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return (markdown ?? "", 0, 0);

        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var rawLines = normalized.Split('\n');
        var blocks = new List<List<string>>();
        List<string>? currentBlock = null;
        int totalValidLines = 0;

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (currentBlock != null) currentBlock.Add(rawLine);
                continue;
            }

            int leadWs = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t')) leadWs++;

            bool isTopLevel = leadWs == 0 || currentBlock == null;
            if (isTopLevel)
            {
                currentBlock = new List<string> { rawLine };
                blocks.Add(currentBlock);
            }
            else
            {
                currentBlock.Add(rawLine);
            }
            totalValidLines++;
        }

        if (blocks.Count == 0) return (markdown, 0, 0);

        blocks.Reverse();
        var flattened = new List<string>();
        foreach (var b in blocks)
        {
            flattened.AddRange(b);
        }

        var result = string.Join("\n", flattened);
        return (hasCr ? result.Replace("\n", "\r\n") : result, blocks.Count, totalValidLines);
    }

    /// <summary>
    /// Formats a human-readable summary of hierarchical outline reversal.
    /// </summary>
    public static string FormatHierarchyReverseSummary(int blockCount, int totalLineCount)
    {
        if (blockCount == 0) return "Reversed outline: 0 blocks (Empty outline)";
        string blockLabel = blockCount == 1 ? "1 top-level block" : $"{blockCount} top-level blocks";
        string lineLabel = totalLineCount == 1 ? "1 line" : $"{totalLineCount} lines";
        return $"Reversed {blockLabel} ({lineLabel} · Child subtrees preserved)";
    }

    /// <summary>
    /// Removes duplicate sibling bullets within the same hierarchical parent scope of an outline markdown string,
    /// preserving the first occurrence of each unique sibling.
    /// </summary>
    public static (string DeduplicatedMarkdown, int RemovedDuplicates, int SurvivingNodes) DeduplicateOutlineNodes(string? markdown, bool ignoreCase = true)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return (markdown ?? "", 0, 0);

        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var rawLines = normalized.Split('\n');
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Map each depth level to the set of sibling labels seen under the current parent at (depth - 1)
        var seenByLevel = new Dictionary<int, HashSet<string>>();
        var outputLines = new List<string>();
        int removedCount = 0;
        int survivingCount = 0;
        int skipSubtreeAboveIndent = int.MaxValue;

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (skipSubtreeAboveIndent == int.MaxValue)
                {
                    outputLines.Add(rawLine);
                }
                continue;
            }

            int indent = 0;
            while (indent < rawLine.Length && (rawLine[indent] == ' ' || rawLine[indent] == '\t'))
            {
                indent += rawLine[indent] == '\t' ? 2 : 1;
            }

            if (indent > skipSubtreeAboveIndent)
            {
                removedCount++;
                continue;
            }
            skipSubtreeAboveIndent = int.MaxValue;

            // Clear deeper sibling scopes whenever we step back to an outer or equal indentation level
            var deeperLevels = seenByLevel.Keys.Where(k => k > indent).ToList();
            foreach (var dl in deeperLevels)
            {
                seenByLevel.Remove(dl);
            }

            string trimmed = rawLine.Trim();
            string cleanLabel = trimmed;
            if (cleanLabel.StartsWith("- ") || cleanLabel.StartsWith("* ") || cleanLabel.StartsWith("+ "))
            {
                cleanLabel = cleanLabel[2..].Trim();
            }

            if (!seenByLevel.TryGetValue(indent, out var siblingsAtIndent))
            {
                siblingsAtIndent = new HashSet<string>(comparer);
                seenByLevel[indent] = siblingsAtIndent;
            }

            if (!siblingsAtIndent.Add(cleanLabel))
            {
                removedCount++;
                skipSubtreeAboveIndent = indent;
                continue;
            }

            outputLines.Add(rawLine);
            survivingCount++;
        }

        var result = string.Join("\n", outputLines);
        return (hasCr ? result.Replace("\n", "\r\n") : result, removedCount, survivingCount);
    }

    /// <summary>
    /// Formats a human-readable summary of outline sibling deduplication.
    /// </summary>
    public static string FormatOutlineDeduplicationSummary(int removedDuplicates, int survivingNodes)
    {
        if (removedDuplicates == 0 && survivingNodes == 0)
            return "Outline deduplication: Empty outline (0 nodes)";

        string survivingStr = survivingNodes == 1 ? "1 unique node" : $"{survivingNodes} unique nodes";
        if (removedDuplicates <= 0)
            return $"Outline deduplication: 0 duplicates found ({survivingStr} verified)";

        string dupStr = removedDuplicates == 1 ? "1 duplicate sibling node" : $"{removedDuplicates} duplicate sibling nodes";
        return $"Deduplicated outline: Removed {dupStr} ({survivingStr} retained)";
    }

    /// <summary>
    /// Splits overly long outline bullet items (exceeding <paramref name="maxChars"/> characters) into a concise
    /// parent heading bullet and an indented child sub-bullet (preferring natural clause separators such as ": ", " — ",
    /// or " - ", or falling back to a word boundary near <paramref name="maxChars"/>).
    /// </summary>
    public static (string SplitMarkdown, int SplitCount, int ResultingNodeCount) SplitLongOutlineItems(
        string? markdown,
        int maxChars = 45)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return (markdown ?? string.Empty, 0, 0);

        int limit = Math.Max(12, maxChars);
        bool hasCr = markdown.Contains("\r\n", StringComparison.Ordinal);
        string normalized = hasCr ? markdown.Replace("\r\n", "\n") : markdown;
        var rawLines = normalized.Split('\n');

        var outputLines = new List<string>(rawLines.Length);
        int splitCount = 0;
        int resultingNodes = 0;
        string[] delimiters = [": ", " — ", " – ", " - ", "; "];

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                outputLines.Add(rawLine);
                continue;
            }

            int indentLen = 0;
            while (indentLen < rawLine.Length && (rawLine[indentLen] == ' ' || rawLine[indentLen] == '\t'))
            {
                indentLen++;
            }

            string indentPrefix = rawLine[..indentLen];
            string trimmed = rawLine[indentLen..].TrimEnd();
            string bulletMarker = "- ";
            string cleanLabel = trimmed;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                bulletMarker = trimmed[..2];
                cleanLabel = trimmed[2..].Trim();
            }

            if (cleanLabel.Length <= limit)
            {
                outputLines.Add(rawLine);
                resultingNodes++;
                continue;
            }

            string? parentPart = null;
            string? childPart = null;

            foreach (var delim in delimiters)
            {
                int idx = cleanLabel.IndexOf(delim, StringComparison.Ordinal);
                if (idx >= 3 && idx + delim.Length < cleanLabel.Length)
                {
                    parentPart = cleanLabel[..idx].Trim();
                    childPart = cleanLabel[(idx + delim.Length)..].Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(parentPart) || string.IsNullOrEmpty(childPart))
            {
                int breakIdx = cleanLabel.LastIndexOf(' ', Math.Min(limit, cleanLabel.Length - 1));
                if (breakIdx < 4)
                {
                    breakIdx = cleanLabel.IndexOf(' ', Math.Min(limit, cleanLabel.Length - 1));
                }

                if (breakIdx > 0 && breakIdx < cleanLabel.Length - 1)
                {
                    parentPart = cleanLabel[..breakIdx].Trim();
                    childPart = cleanLabel[(breakIdx + 1)..].Trim();
                }
            }

            if (!string.IsNullOrEmpty(parentPart) && !string.IsNullOrEmpty(childPart))
            {
                outputLines.Add($"{indentPrefix}{bulletMarker}{parentPart}");
                outputLines.Add($"{indentPrefix}  {bulletMarker}{childPart}");
                splitCount++;
                resultingNodes += 2;
            }
            else
            {
                outputLines.Add(rawLine);
                resultingNodes++;
            }
        }

        string joined = string.Join("\n", outputLines);
        return (hasCr ? joined.Replace("\n", "\r\n") : joined, splitCount, resultingNodes);
    }

    /// <summary>
    /// Formats a human-readable summary of long outline bullet splitting.
    /// </summary>
    public static string FormatLongItemSplitSummary(int splitCount, int resultingNodeCount, int maxChars = 45)
    {
        int limit = Math.Max(12, maxChars);
        if (resultingNodeCount <= 0 && splitCount <= 0)
            return $"Split long outline items (>{limit} chars): Empty outline (0 nodes)";

        string nodeStr = resultingNodeCount == 1 ? "1 node" : $"{resultingNodeCount} nodes";
        if (splitCount <= 0)
            return $"Split long outline items (>{limit} chars): 0 long bullets found ({nodeStr} within limit)";

        string splitStr = splitCount == 1 ? "1 long bullet split" : $"{splitCount} long bullets split";
        return $"Split long outline items (>{limit} chars): {splitStr} into parent + sub-bullet ({nodeStr} total)";
    }

    private static readonly System.Text.RegularExpressions.Regex ExistingHierarchicalNumberPrefixRegex =
        new(@"^\d+(?:\.\d+)*[\.\)]\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Applies (or strips when <paramref name="stripExistingOnly"/> is <c>true</c>) hierarchical outline numbering
    /// (e.g., <c>1.</c>, <c>1.1.</c>, <c>1.2.</c>, <c>2.</c>) across all outline bullets according to indentation depth.
    /// </summary>
    public static (string NumberedMarkdown, int NumberedNodes, int MaxDepth) ApplyHierarchicalNumbering(
        string? markdown,
        bool stripExistingOnly = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return (markdown ?? string.Empty, 0, 0);

        bool hasCr = markdown.Contains("\r\n", StringComparison.Ordinal);
        string normalized = hasCr ? markdown.Replace("\r\n", "\n") : markdown;
        var rawLines = normalized.Split('\n');

        var outputLines = new List<string>(rawLines.Length);
        var counters = new List<int>();
        int numberedNodes = 0;
        int maxDepth = 0;

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                outputLines.Add(rawLine);
                continue;
            }

            int indentSpaces = 0;
            int rawIndentChars = 0;
            while (rawIndentChars < rawLine.Length && (rawLine[rawIndentChars] == ' ' || rawLine[rawIndentChars] == '\t'))
            {
                indentSpaces += rawLine[rawIndentChars] == '\t' ? 2 : 1;
                rawIndentChars++;
            }

            string indentPrefix = rawLine[..rawIndentChars];
            string trimmed = rawLine[rawIndentChars..].TrimEnd();
            string bulletMarker = "- ";
            string cleanLabel = trimmed;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                bulletMarker = trimmed[..2];
                cleanLabel = trimmed[2..].Trim();
            }

            var match = ExistingHierarchicalNumberPrefixRegex.Match(cleanLabel);
            if (match.Success)
            {
                cleanLabel = cleanLabel[match.Length..].TrimStart();
            }

            int depth = indentSpaces / 2;
            if (depth + 1 > maxDepth) maxDepth = depth + 1;

            if (stripExistingOnly)
            {
                outputLines.Add($"{indentPrefix}{bulletMarker}{cleanLabel}");
                numberedNodes++;
                continue;
            }

            while (counters.Count <= depth)
            {
                counters.Add(0);
            }
            while (counters.Count > depth + 1)
            {
                counters.RemoveAt(counters.Count - 1);
            }

            counters[depth]++;
            for (int d = 0; d < depth; d++)
            {
                if (counters[d] == 0) counters[d] = 1;
            }

            string numberPrefix = string.Join(".", counters) + ".";
            outputLines.Add($"{indentPrefix}{bulletMarker}{numberPrefix} {cleanLabel}");
            numberedNodes++;
        }

        string joined = string.Join("\n", outputLines);
        return (hasCr ? joined.Replace("\n", "\r\n") : joined, numberedNodes, maxDepth);
    }

    /// <summary>
    /// Formats a human-readable summary of hierarchical outline auto-numbering.
    /// </summary>
    public static string FormatOutlineNumberingSummary(int numberedNodes, int maxDepth, bool stripped = false)
    {
        if (numberedNodes <= 0)
            return "Outline numbering: Empty outline (0 nodes)";

        string nodeStr = numberedNodes == 1 ? "1 node" : $"{numberedNodes} nodes";
        string depthStr = maxDepth == 1 ? "1 level" : $"{maxDepth} levels";
        if (stripped)
            return $"Outline numbering: Stripped numeric prefixes from {nodeStr} across {depthStr}";

        return $"Outline numbering: Applied hierarchical numbering (1., 1.1.) to {nodeStr} across {depthStr}";
    }

    /// <summary>
    /// Represents a scored SmartArt layout family recommendation for an outline structure.
    /// </summary>
    public sealed record LayoutFamilyRecommendation(
        string Family,
        int MatchScorePercent,
        string Rationale,
        IReadOnlyList<string> RecommendedLayouts);

    /// <summary>
    /// Analyzes an outline's hierarchy structure (total nodes, root count, max depth, balance)
    /// and scores the best-fitting SmartArt layout families (Process, Hierarchy, Matrix, Pyramid, Cycle, Comparison/Relationship, List).
    /// </summary>
    public static List<LayoutFamilyRecommendation> RecommendLayoutFamiliesForOutline(string? markdown)
    {
        var recommendations = new List<LayoutFamilyRecommendation>();
        if (string.IsNullOrWhiteSpace(markdown)) return recommendations;

        var (totalNodes, maxDepth, avgDepth, levelCounts, isStepUniform) = AuditOutlineDepths(markdown);
        if (totalNodes == 0) return recommendations;

        var (rootCount, subtreeSizes, stdDev, isBalanced) = AuditOutlineBalance(markdown);
        var (branchTotal, branchCount, leafCount, avgBranch, maxBranch) = AuditOutlineBranching(markdown);

        // 1. Hierarchy: Best when 1 root tree with 2+ levels and high branching
        if (rootCount == 1 && maxDepth >= 2)
        {
            int score = maxDepth >= 3 ? 95 : 85;
            recommendations.Add(new LayoutFamilyRecommendation(
                "Hierarchy",
                score,
                $"Single root hierarchy spanning {maxDepth} levels with {leafCount} leaf items",
                new[] { "Horizontal Hierarchy", "Hierarchy Tree", "Grouped List" }));
        }

        // 2. Matrix: Exactly 4 roots or 4 balanced categories
        if (rootCount == 4 && isBalanced)
        {
            recommendations.Add(new LayoutFamilyRecommendation(
                "Matrix",
                92,
                "4 balanced root quadrants suitable for 2×2 quadrant mapping",
                new[] { "Grid Matrix", "Titled Matrix", "Cycle Matrix" }));
        }
        else if (rootCount == 4)
        {
            recommendations.Add(new LayoutFamilyRecommendation(
                "Matrix",
                80,
                "4 root categories adaptable for 2×2 grid layout",
                new[] { "Grid Matrix", "Titled Matrix" }));
        }

        // 3. Comparison / Relationship: Exactly 2 balanced root trees
        if (rootCount == 2 && isBalanced)
        {
            recommendations.Add(new LayoutFamilyRecommendation(
                "Relationship & Comparison",
                90,
                "2 balanced root trees ideal for side-by-side comparison or opposing relationships",
                new[] { "Balance", "Opposing Arrows", "Converging Arrows" }));
        }

        // 4. Process: Flat or shallow linear flow of 3–7 items
        if (maxDepth == 1 && totalNodes >= 3 && totalNodes <= 8)
        {
            recommendations.Add(new LayoutFamilyRecommendation(
                "Process",
                90,
                $"Linear sequence of {totalNodes} sequential steps with uniform depth",
                new[] { "Basic Process", "Chevron Process", "Step Down Process" }));
        }
        else if (maxDepth == 2 && rootCount >= 3 && rootCount <= 6)
        {
            recommendations.Add(new LayoutFamilyRecommendation(
                "Process",
                82,
                $"{rootCount} major phases with indented detail bullet points",
                new[] { "Detailed Process", "Grouped Process", "Process List" }));
        }

        // 5. Cycle: 3–6 items, especially with looping terminology or flat sequential flow
        bool hasCycleKeywords = markdown.Contains("cycle", StringComparison.OrdinalIgnoreCase) ||
                                markdown.Contains("repeat", StringComparison.OrdinalIgnoreCase) ||
                                markdown.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                                markdown.Contains("feedback", StringComparison.OrdinalIgnoreCase) ||
                                markdown.Contains("iterate", StringComparison.OrdinalIgnoreCase);
        if (maxDepth == 1 && totalNodes >= 3 && totalNodes <= 6)
        {
            int cycleScore = hasCycleKeywords ? 94 : 75;
            recommendations.Add(new LayoutFamilyRecommendation(
                "Cycle",
                cycleScore,
                $"Circular flow of {totalNodes} contiguous stages{(hasCycleKeywords ? " with continuous loop semantics" : "")}",
                new[] { "Basic Cycle", "Block Cycle", "Radial Cycle" }));
        }

        // 6. Pyramid: 3–5 levels with decreasing node counts per level
        if (maxDepth >= 3 && maxDepth <= 5 && levelCounts.Count >= 3)
        {
            bool isDecreasing = true;
            for (int lvl = 1; lvl < maxDepth; lvl++)
            {
                if (levelCounts.TryGetValue(lvl, out int cur) && levelCounts.TryGetValue(lvl + 1, out int next))
                {
                    if (next >= cur) { isDecreasing = false; break; }
                }
            }
            if (isDecreasing)
            {
                recommendations.Add(new LayoutFamilyRecommendation(
                    "Pyramid",
                    88,
                    $"Layered structure of {maxDepth} tiers with tapering proportions",
                    new[] { "Basic Pyramid", "Inverted Pyramid", "Segmented Pyramid" }));
            }
        }

        // 7. List / Block List: Fallback or flat listing
        if (maxDepth == 1 || recommendations.Count == 0)
        {
            int listScore = maxDepth == 1 ? 80 : 65;
            recommendations.Add(new LayoutFamilyRecommendation(
                "List",
                listScore,
                $"Structured bullet catalog of {totalNodes} items",
                new[] { "Vertical Block List", "Horizontal Bullet List", "Table Hierarchy" }));
        }

        return recommendations
            .OrderByDescending(r => r.MatchScorePercent)
            .ThenBy(r => r.Family)
            .ToList();
    }

    /// <summary>
    /// Formats a human-readable summary of SmartArt layout family recommendations for an outline.
    /// </summary>
    public static string FormatLayoutRecommendationSummary(string? markdown)
    {
        var recs = RecommendLayoutFamiliesForOutline(markdown);
        if (recs.Count == 0)
            return "SmartArt layout recommendations: Empty outline (0 nodes)";

        var top = recs[0];
        string suggestedList = string.Join(", ", top.RecommendedLayouts.Take(2));
        var otherRecs = recs.Skip(1).Take(2).Select(r => $"{r.Family} ({r.MatchScorePercent}%)");
        string alternatives = otherRecs.Any() ? $" · Alt: {string.Join(", ", otherRecs)}" : "";

        return $"SmartArt layout recommendation: #1 {top.Family} ({top.MatchScorePercent}% match · {top.Rationale}) → Suggested: {suggestedList}{alternatives}";
    }

    /// <summary>
    /// Structures a flat bullet list into a 2-level hierarchy by splitting bullet items on delimiters
    /// (': ', ' — ', or ' - ') and grouping items sharing identical category prefixes under parent nodes.
    /// </summary>
    public static (string StructuredMarkdown, int ParentsCreated, int ItemsStructured, string Summary)
        StructureFlatOutlineByDelimiter(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (markdown ?? "", 0, 0, FormatDelimiterStructuringSummary(0, 0, 0));
        }

        bool hasCr = markdown.Contains("\r");
        var normalized = markdown.Replace("\r", "");
        var lines = normalized.Split('\n');

        var delimiters = new[] { ": ", " — ", " - " };
        var outputLines = new List<string>();
        string? currentParentPrefix = null;
        int parentsCreated = 0;
        int itemsStructured = 0;
        int totalNodes = 0;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                outputLines.Add(rawLine);
                continue;
            }

            int leadWs = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t')) leadWs++;

            var trimmed = rawLine.Substring(leadWs);
            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ");

            if (!isBullet || leadWs > 0)
            {
                // Already indented or not a bullet
                outputLines.Add(rawLine);
                currentParentPrefix = null;
                continue;
            }

            totalNodes++;
            string bulletMarker = trimmed.Substring(0, 2); // e.g. "- "
            string content = trimmed.Substring(2).Trim();

            // Check for delimiter
            string? matchedDelim = null;
            int delimIndex = -1;
            foreach (var d in delimiters)
            {
                int idx = content.IndexOf(d, StringComparison.Ordinal);
                if (idx > 0 && (delimIndex < 0 || idx < delimIndex))
                {
                    delimIndex = idx;
                    matchedDelim = d;
                }
            }

            if (matchedDelim != null && delimIndex > 0)
            {
                string prefix = content.Substring(0, delimIndex).Trim();
                string suffix = content.Substring(delimIndex + matchedDelim.Length).Trim();

                if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(suffix))
                {
                    itemsStructured++;
                    if (!string.Equals(currentParentPrefix, prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        currentParentPrefix = prefix;
                        parentsCreated++;
                        outputLines.Add($"{bulletMarker}{prefix}");
                    }
                    outputLines.Add($"  {bulletMarker}{suffix}");
                    continue;
                }
            }

            // No delimiter found
            outputLines.Add(rawLine);
            currentParentPrefix = null;
        }

        var result = string.Join("\n", outputLines);
        var finalMarkdown = hasCr ? result.Replace("\n", "\r\n") : result;
        var summary = FormatDelimiterStructuringSummary(parentsCreated, itemsStructured, totalNodes);
        return (finalMarkdown, parentsCreated, itemsStructured, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of flat-to-hierarchy outline structuring by delimiter.
    /// </summary>
    public static string FormatDelimiterStructuringSummary(int parentsCreated, int itemsStructured, int totalNodes)
    {
        if (totalNodes <= 0)
            return "Structure by delimiter: 0 nodes (Empty outline)";

        if (itemsStructured <= 0)
            return $"Structure by delimiter: 0 delimited items found across {totalNodes} nodes (No ': ', ' — ', or ' - ' delimiters)";

        string parentLabel = parentsCreated == 1 ? "1 parent group" : $"{parentsCreated} parent groups";
        string itemLabel = itemsStructured == 1 ? "1 item" : $"{itemsStructured} items";
        return $"Structure by delimiter ({parentLabel} created): Converted {itemLabel} across {totalNodes} nodes into hierarchical subtrees";
    }

    /// <summary>
    /// Clamps or flattens outlines whose depth exceeds <paramref name="maxDepth"/> (e.g. 2 or 3) to prevent
    /// rendering overflow in horizontal/process SmartArt layouts.
    /// Supports clamping indentation level or merging sub-bullets into the parent bullet with a separator.
    /// </summary>
    public (string Markdown, int AdjustedCount, string Summary) BalanceOutlineNestingDepth(
        int maxDepth = 2,
        bool mergeIntoParent = false)
    {
        var (result, adjusted, total, summary) = BalanceOutlineNestingDepth(MarkdownText, maxDepth, mergeIntoParent);
        if (adjusted > 0)
        {
            MarkdownText = result;
        }
        return (result, adjusted, summary);
    }

    /// <summary>
    /// Clamps or flattens markdown outline lines whose depth exceeds <paramref name="maxDepth"/>.
    /// </summary>
    public static (string Markdown, int AdjustedCount, int TotalBullets, string Summary) BalanceOutlineNestingDepth(
        string? markdown,
        int maxDepth = 2,
        bool mergeIntoParent = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (markdown ?? string.Empty, 0, 0, FormatDepthBalancingSummary(0, 0, maxDepth, mergeIntoParent));
        }

        bool hasCr = markdown.Contains("\r");
        var lines = markdown.Replace("\r", "").Split('\n');
        var outputLines = new List<string>();
        int totalBullets = 0;
        int adjustedCount = 0;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                outputLines.Add(rawLine);
                continue;
            }

            int leadWs = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t')) leadWs++;

            var trimmed = rawLine.Substring(leadWs);
            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ");

            if (!isBullet)
            {
                outputLines.Add(rawLine);
                continue;
            }

            totalBullets++;
            string bulletMarker = trimmed.Substring(0, 2);
            string content = trimmed.Substring(2).Trim();

            // Calculate logical depth (1 = root, 2 = 2 spaces, 3 = 4 spaces, etc.)
            int depth = 1 + (leadWs >= 2 ? (leadWs / 2) : 0);

            if (depth > maxDepth)
            {
                adjustedCount++;
                if (mergeIntoParent && outputLines.Count > 0 && !string.IsNullOrWhiteSpace(outputLines[^1]))
                {
                    // Merge into previous line
                    outputLines[^1] = outputLines[^1] + " — " + content;
                }
                else
                {
                    // Clamp to maxDepth
                    int clampedSpaces = Math.Max(0, (maxDepth - 1) * 2);
                    string indent = new string(' ', clampedSpaces);
                    outputLines.Add($"{indent}{bulletMarker}{content}");
                }
            }
            else
            {
                outputLines.Add(rawLine);
            }
        }

        var joined = string.Join("\n", outputLines);
        var finalMarkdown = hasCr ? joined.Replace("\n", "\r\n") : joined;
        var summary = FormatDepthBalancingSummary(adjustedCount, totalBullets, maxDepth, mergeIntoParent);
        return (finalMarkdown, adjustedCount, totalBullets, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of outline depth balancing.
    /// </summary>
    public static string FormatDepthBalancingSummary(int adjustedCount, int totalBullets, int maxDepth, bool mergeIntoParent = false)
    {
        if (totalBullets <= 0)
            return "Balance outline depth: 0 bullets (Empty outline)";

        if (adjustedCount <= 0)
            return $"Balance outline depth: All {totalBullets} bullets within max depth {maxDepth} (0 adjustments needed)";

        string modeStr = mergeIntoParent ? "merged into parent bullets" : "clamped to max depth";
        string bulletLabel = adjustedCount == 1 ? "1 bullet" : $"{adjustedCount} bullets";
        return $"Balance outline depth (Max depth {maxDepth}): {bulletLabel} {modeStr} across {totalBullets} bullets to prevent layout overflow";
    }

    /// <summary>
    /// Audits the active SmartArt outline for token density, average word count per node,
    /// variance, and identifies verbose or terse nodes that risk visual imbalance or layout overflow.
    /// </summary>
    public (int TotalBullets, int TotalWords, double MeanWords, int MinWords, int MaxWords, int VerboseCount, string Rating, string Summary)
        AuditOutlineTokenDensity()
    {
        var result = AuditOutlineTokenDensity(MarkdownText);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Audits markdown outline text for token density, word count distribution, and layout readability balance.
    /// </summary>
    public static (int TotalBullets, int TotalWords, double MeanWords, int MinWords, int MaxWords, int VerboseCount, string Rating, string Summary)
        AuditOutlineTokenDensity(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (0, 0, 0.0, 0, 0, 0, "Empty", "Outline token density: 0 bullets (Empty outline)");
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        var bulletWordCounts = new List<int>();
        bool inFence = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || string.IsNullOrWhiteSpace(trimmed)) continue;

            // Check if bullet or numbered item
            int leadWs = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t')) leadWs++;
            var stripped = rawLine.Substring(leadWs);

            bool isBullet = stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ ") ||
                            System.Text.RegularExpressions.Regex.IsMatch(stripped, @"^\d+[\.\)]\s+");

            if (!isBullet) continue;

            // Extract content without bullet marker
            string content;
            if (stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ "))
            {
                content = stripped.Substring(2).Trim();
            }
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(stripped, @"^\d+[\.\)]\s+");
                content = stripped.Substring(match.Length).Trim();
            }

            var words = content.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            bulletWordCounts.Add(words.Length);
        }

        if (bulletWordCounts.Count == 0)
        {
            return (0, 0, 0.0, 0, 0, 0, "Empty", "Outline token density: 0 bullets (No outline bullet items detected)");
        }

        int totalBullets = bulletWordCounts.Count;
        int totalWords = bulletWordCounts.Sum();
        double meanWords = Math.Round((double)totalWords / totalBullets, 1);
        int minWords = bulletWordCounts.Min();
        int maxWords = bulletWordCounts.Max();
        int verboseCount = bulletWordCounts.Count(w => w > 12);
        int terseCount = bulletWordCounts.Count(w => w <= 1);

        string rating;
        if (maxWords <= 10 && meanWords >= 2.0 && meanWords <= 7.0 && verboseCount == 0)
            rating = "Optimal";
        else if (maxWords <= 16 && verboseCount <= 1)
            rating = "Balanced";
        else if (maxWords <= 25)
            rating = "Moderate Variance";
        else
            rating = "Overflow Risk";

        string summary = FormatTokenDensitySummary(totalBullets, totalWords, meanWords, minWords, maxWords, verboseCount, terseCount, rating);
        return (totalBullets, totalWords, meanWords, minWords, maxWords, verboseCount, rating, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of outline token density and readability balance.
    /// </summary>
    public static string FormatTokenDensitySummary(
        int totalBullets, int totalWords, double meanWords, int minWords, int maxWords, int verboseCount, int terseCount, string rating)
    {
        if (totalBullets <= 0)
            return "Outline token density: 0 bullets (Empty outline)";

        string bulletLabel = totalBullets == 1 ? "1 bullet" : $"{totalBullets} bullets";
        string wordLabel = totalWords == 1 ? "1 word" : $"{totalWords} words";

        string advice = "";
        if (verboseCount > 0)
        {
            advice = $" · Flagged: {verboseCount} verbose node{(verboseCount == 1 ? "" : "s")} (>12w, risk overflow)";
        }
        else if (rating == "Optimal")
        {
            advice = " · Ideal concise diagram phrasing";
        }

        return $"Outline token density (Rating: {rating} · {bulletLabel} · Total {wordLabel}): Mean {meanWords:0.#}w/node (Range: {minWords}-{maxWords}w){advice}";
    }

    /// <summary>
    /// Audits the aspect ratio and container fit of the active outline against standard slide/viewport dimensions.
    /// </summary>
    public LayoutAspectRatioFit AuditLayoutAspectRatio()
    {
        int count = OutlineRows.Count > 0 ? OutlineRows.Count : 0;
        return AuditLayoutAspectRatio(SelectedCategory, count, MarkdownText);
    }

    /// <summary>
    /// Analyzes layout category and node count to calculate optimal aspect ratio, dimensions, and fit quality.
    /// </summary>
    public static LayoutAspectRatioFit AuditLayoutAspectRatio(
        string? category,
        int nodeCount,
        string? markdownText = null)
    {
        string cat = string.IsNullOrWhiteSpace(category) ? "Process" : category.Trim();
        if (cat.Equals("All", StringComparison.OrdinalIgnoreCase)) cat = "Process";

        int count = nodeCount;
        if (count <= 0 && !string.IsNullOrWhiteSpace(markdownText))
        {
            var lines = markdownText.Replace("\r", "").Split('\n');
            count = lines.Count(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* ") || l.TrimStart().StartsWith("+ "));
        }
        if (count <= 0) count = 1;

        double estRatio;
        string recRatioName;
        double recRatioVal;
        int optW;
        int optH;
        string fitQuality;

        switch (cat.ToLowerInvariant())
        {
            case "process":
            case "timeline":
                estRatio = Math.Round(Math.Max(1.5, Math.Min(3.0, 1.2 + (count * 0.25))), 2);
                recRatioName = count > 6 ? "2:1 Ultra-Wide" : "16:9 Widescreen";
                recRatioVal = count > 6 ? 2.0 : (16.0 / 9.0);
                optW = count > 6 ? 1000 : 960;
                optH = count > 6 ? 500 : 540;
                fitQuality = count > 8 ? "Crowded (Consider splitting or clamping depth)" : "Optimal Fit";
                break;

            case "cycle":
            case "radial":
                estRatio = 1.0;
                recRatioName = "1:1 Square";
                recRatioVal = 1.0;
                optW = 600;
                optH = 600;
                fitQuality = count > 7 ? "Dense (Text overlap risk in radial cycle)" : count < 3 ? "Sparse (<3 nodes in cycle)" : "Optimal Fit";
                break;

            case "matrix":
                estRatio = 1.0;
                recRatioName = "1:1 Square";
                recRatioVal = 1.0;
                optW = 600;
                optH = 600;
                fitQuality = count == 4 ? "Perfect 2×2 Quadrant" : count > 4 ? "Overloaded for 2×2 Matrix" : "Underpopulated";
                break;

            case "pyramid":
                estRatio = 1.25;
                recRatioName = "4:3 Standard";
                recRatioVal = 4.0 / 3.0;
                optW = 800;
                optH = 600;
                fitQuality = count > 6 ? "Crowded (>6 tiers)" : "Optimal Fit";
                break;

            case "hierarchy":
            case "tree":
                estRatio = count > 6 ? 1.78 : 1.33;
                recRatioName = count > 6 ? "16:9 Widescreen" : "4:3 Standard";
                recRatioVal = count > 6 ? (16.0 / 9.0) : (4.0 / 3.0);
                optW = count > 6 ? 960 : 800;
                optH = count > 6 ? 540 : 600;
                fitQuality = count > 12 ? "Complex Hierarchy (Recommend zoom / pan)" : "Optimal Fit";
                break;

            case "list":
            case "picture list":
                estRatio = 0.71;
                recRatioName = "A4 Portrait (1:√2)";
                recRatioVal = 1.0 / 1.414;
                optW = 600;
                optH = 848;
                fitQuality = count > 10 ? "Long Vertical List (Scroll required)" : "Optimal Fit";
                break;

            default:
                estRatio = 1.78;
                recRatioName = "16:9 Widescreen";
                recRatioVal = 16.0 / 9.0;
                optW = 960;
                optH = 540;
                fitQuality = "Optimal Fit";
                break;
        }

        string summary = FormatLayoutAspectRatioSummary(cat, count, estRatio, recRatioName, optW, optH, fitQuality);
        return new LayoutAspectRatioFit(cat, count, estRatio, recRatioName, recRatioVal, optW, optH, fitQuality, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of layout aspect ratio fit metrics.
    /// </summary>
    public static string FormatLayoutAspectRatioSummary(
        string category, int nodeCount, double estRatio, string recRatioName, int optW, int optH, string fitQuality)
    {
        string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
        return $"Layout aspect ratio audit ({category} · {nodeLabel}): Target {recRatioName} ({optW}×{optH}px · Est. Ratio {estRatio:0.##}:1) · Quality: {fitQuality}";
    }

    /// <summary>
    /// Formats a human-readable summary of layout aspect ratio fit from category and count.
    /// </summary>
    public static string FormatLayoutAspectRatioSummary(string? category, int nodeCount, string? markdownText = null)
    {
        return AuditLayoutAspectRatio(category, nodeCount, markdownText).Summary;
    }

    /// <summary>
    /// Audits outline item label lengths against category-specific character budgets to detect text truncation risks.
    /// </summary>
    public NodeCharacterBudgetAudit AuditNodeCharacterBudgets() => AuditNodeCharacterBudgets(SelectedCategory, MarkdownText);

    /// <summary>
    /// Audits outline item label lengths against category-specific character budgets to detect text truncation risks.
    /// </summary>
    public static NodeCharacterBudgetAudit AuditNodeCharacterBudgets(string? category, string? markdownText)
    {
        string cat = string.IsNullOrWhiteSpace(category) ? "Process" : category.Trim();
        if (cat.Equals("All", StringComparison.OrdinalIgnoreCase)) cat = "Process";

        int l1Budget;
        int l2Budget;

        switch (cat.ToLowerInvariant())
        {
            case "process":
            case "timeline":
                l1Budget = 25;
                l2Budget = 18;
                break;
            case "cycle":
            case "radial":
                l1Budget = 20;
                l2Budget = 15;
                break;
            case "matrix":
                l1Budget = 28;
                l2Budget = 20;
                break;
            case "pyramid":
                l1Budget = 24;
                l2Budget = 16;
                break;
            case "hierarchy":
            case "tree":
                l1Budget = 30;
                l2Budget = 22;
                break;
            case "list":
            case "picture list":
                l1Budget = 45;
                l2Budget = 35;
                break;
            default:
                l1Budget = 30;
                l2Budget = 20;
                break;
        }

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new NodeCharacterBudgetAudit(
                cat, 0, 0, 0, l1Budget, l2Budget, "None", Array.Empty<string>(),
                $"Node character budget audit ({cat}): 0 nodes (Empty outline)");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var nodes = new List<(string Text, int Depth)>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    string content = rest.Substring(2).Trim();
                    int depth = (spaceCount / 2) + 1;
                    nodes.Add((content, depth));
                }
            }
        }

        if (nodes.Count == 0)
        {
            return new NodeCharacterBudgetAudit(
                cat, 0, 0, 0, l1Budget, l2Budget, "None", Array.Empty<string>(),
                $"Node character budget audit ({cat}): 0 nodes (Empty outline)");
        }

        var offending = new List<string>();
        int maxChar = 0;

        foreach (var (text, depth) in nodes)
        {
            int len = text.Length;
            if (len > maxChar) maxChar = len;
            int budget = depth == 1 ? l1Budget : l2Budget;

            if (len > budget)
            {
                offending.Add($"L{depth} '{text}' ({len}/{budget}c)");
            }
        }

        int overCount = offending.Count;
        string risk;
        if (overCount == 0)
            risk = "Minimal (Labels fit within bounds)";
        else if (overCount <= 2 && maxChar <= (l1Budget + 8))
            risk = "Low (Minor font squeeze)";
        else if (overCount <= 4)
            risk = "Moderate (Text wrapping likely)";
        else
            risk = "Severe (Text truncation / illegible micro-font risk)";

        string summary = FormatCharacterBudgetSummary(cat, nodes.Count, overCount, maxChar, l1Budget, l2Budget, risk, offending);
        return new NodeCharacterBudgetAudit(cat, nodes.Count, overCount, maxChar, l1Budget, l2Budget, risk, offending, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of node character count budgets and truncation risk.
    /// </summary>
    public static string FormatCharacterBudgetSummary(
        string category, int totalNodes, int overBudgetCount, int maxChars, int l1Budget, int l2Budget, string risk, IReadOnlyList<string>? offending = null)
    {
        if (totalNodes <= 0) return $"Node character budget audit ({category}): 0 nodes (Empty outline)";
        string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        string budgetLabel = $"Budget: L1≤{l1Budget}c, L2≤{l2Budget}c";

        if (overBudgetCount == 0)
        {
            return $"Node character budget audit ({category} · {nodeLabel}): 0 over budget ({budgetLabel}) · Risk: {risk} · Max: {maxChars}c";
        }

        string overLabel = overBudgetCount == 1 ? "1 node over budget" : $"{overBudgetCount} nodes over budget";
        string preview = "";
        if (offending != null && offending.Count > 0)
        {
            preview = $" [{string.Join(", ", offending.Take(3))}{(offending.Count > 3 ? ", …" : "")}]";
        }

        return $"Node character budget audit ({category} · {nodeLabel}): {overLabel} ({budgetLabel}) · Risk: {risk} · Max: {maxChars}c{preview}";
    }

    /// <summary>
    /// Recommends Fluent UI icon glyphs based on outline bullet semantic keywords.
    /// </summary>
    public OutlineIconRecommendationAudit RecommendIconsForOutline() => RecommendIconsForOutline(MarkdownText);

    /// <summary>
    /// Recommends Fluent UI icon glyphs based on outline bullet semantic keywords.
    /// </summary>
    public static OutlineIconRecommendationAudit RecommendIconsForOutline(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new OutlineIconRecommendationAudit(
                0, 0, 0.0, Array.Empty<NodeIconAffinity>(),
                "Outline icon recommendations: 0 nodes (Empty outline)");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var nodes = new List<(string Text, int Depth)>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                if (rawLine[leadWs] == '\t') spaceCount += 2;
                else spaceCount += 1;
                leadWs++;
            }

            if (leadWs < rawLine.Length)
            {
                var rest = rawLine.Substring(leadWs);
                if (rest.StartsWith("- ") || rest.StartsWith("* ") || rest.StartsWith("+ ") ||
                    (rest.Length > 2 && char.IsDigit(rest[0]) && rest[1] == '.' && rest[2] == ' '))
                {
                    string content = rest.Substring(2).Trim();
                    int depth = (spaceCount / 2) + 1;
                    nodes.Add((content, depth));
                }
            }
        }

        if (nodes.Count == 0)
        {
            return new OutlineIconRecommendationAudit(
                0, 0, 0.0, Array.Empty<NodeIconAffinity>(),
                "Outline icon recommendations: 0 nodes (Empty outline)");
        }

        var affinities = new List<NodeIconAffinity>();
        int matched = 0;

        foreach (var (text, depth) in nodes)
        {
            var (glyph, name, kw) = MatchKeywordToIcon(text);
            if (!string.IsNullOrEmpty(kw)) matched++;
            affinities.Add(new NodeIconAffinity(text, depth, glyph, name, string.IsNullOrEmpty(kw) ? "default" : kw));
        }

        double coverage = Math.Round((double)matched / nodes.Count * 100.0, 1);
        string nodeLabel = nodes.Count == 1 ? "1 node" : $"{nodes.Count} nodes";
        string summary = FormatIconRecommendationsSummary(nodes.Count, matched, coverage, affinities);

        return new OutlineIconRecommendationAudit(
            nodes.Count,
            matched,
            coverage,
            affinities,
            summary);
    }

    private static (string Glyph, string Name, string MatchedKeyword) MatchKeywordToIcon(string text)
    {
        var lower = text.ToLowerInvariant();
        var wordSet = System.Text.RegularExpressions.Regex.Matches(lower, @"\b[a-z0-9]+\b")
            .Select(m => m.Value)
            .ToHashSet();

        // 1. Security / Authentication
        foreach (var k in new[] { "security", "protect", "auth", "lock", "shield", "safe" })
            if (wordSet.Contains(k)) return ("\uE72E", "Lock", k);

        // 2. Cloud Infrastructure
        foreach (var k in new[] { "cloud", "aws", "azure", "serverless" })
            if (wordSet.Contains(k)) return ("\uE753", "Cloud", k);

        // 3. Database / Storage
        foreach (var k in new[] { "database", "sql", "storage", "data", "store" })
            if (wordSet.Contains(k)) return ("\uE81E", "Database", k);

        // 4. Analytics / Reporting
        foreach (var k in new[] { "analytics", "telemetry", "metric", "metrics", "report", "reporting", "stats", "chart" })
            if (wordSet.Contains(k)) return ("\uE9D9", "Analytics", k);

        // 5. User / Account
        foreach (var k in new[] { "user", "users", "author", "account", "profile", "person", "team" })
            if (wordSet.Contains(k)) return ("\uE77B", "Contact", k);

        // 6. Settings / Config
        foreach (var k in new[] { "settings", "config", "configuration", "setup", "options", "admin" })
            if (wordSet.Contains(k)) return ("\uE713", "Setting", k);

        // 7. Network / API
        foreach (var k in new[] { "network", "api", "connect", "endpoint", "link" })
            if (wordSet.Contains(k)) return ("\uE968", "Network", k);

        // 8. Workflow / Process
        foreach (var k in new[] { "workflow", "process", "pipeline", "task", "flow" })
            if (wordSet.Contains(k)) return ("\uE809", "Flow", k);

        // 9. Warnings / Alerts / Risk
        foreach (var k in new[] { "warning", "risk", "conflict", "alert", "error" })
            if (wordSet.Contains(k)) return ("\uE7BA", "Warning", k);

        // 10. Success / Verification
        foreach (var k in new[] { "success", "done", "complete", "pass", "verified", "verify" })
            if (wordSet.Contains(k)) return ("\uE73E", "CheckMark", k);

        // 11. Performance / Speed
        foreach (var k in new[] { "performance", "speed", "fast", "velocity", "pace" })
            if (wordSet.Contains(k)) return ("\uE9F9", "SpeedHigh", k);

        // 12. Testing / Inspection
        foreach (var k in new[] { "test", "tests", "audit", "check", "inspect" })
            if (wordSet.Contains(k)) return ("\uE7B3", "Inspect", k);

        // 13. Time / Calendar / Schedule
        foreach (var k in new[] { "schedule", "calendar", "timeline", "clock", "date", "time" })
            if (wordSet.Contains(k)) return ("\uE787", "Calendar", k);

        // 14. Document / Specs
        foreach (var k in new[] { "document", "doc", "file", "text", "write", "spec" })
            if (wordSet.Contains(k)) return ("\uE8A5", "Document", k);

        return ("\uE7C3", "Page", "");
    }

    /// <summary>
    /// Formats a human-readable summary of outline icon recommendations.
    /// </summary>
    public static string FormatIconRecommendationsSummary(
        int totalNodes, int matched, double coverage, IReadOnlyList<NodeIconAffinity>? affinities = null)
    {
        if (totalNodes <= 0) return "Outline icon recommendations: 0 nodes (Empty outline)";
        string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";

        string preview = "";
        if (affinities != null && affinities.Count > 0)
        {
            var sample = affinities.Take(3).Select(a => $"'{a.NodeText}' → {a.IconName}");
            preview = $" [{string.Join(", ", sample)}{(affinities.Count > 3 ? ", …" : "")}]";
        }

        return $"Outline icon recommendations ({nodeLabel}): {matched}/{totalNodes} matched ({coverage}%) · Icons: Lock, Cloud, Database, Flow, etc.{preview}";
    }

    /// <summary>
    /// Formats a human-readable summary of outline icon recommendations from markdown text.
    /// </summary>
    public static string FormatIconRecommendationsSummary(string? markdownText)
    {
        return RecommendIconsForOutline(markdownText).Summary;
    }

    /// <summary>
    /// Audits the active SmartArt outline for hierarchy branching factor, tree balance, and nesting depth distribution.
    /// </summary>
    public HierarchyBranchingAudit AuditHierarchyBranchingFactor()
    {
        var result = AuditHierarchyBranchingFactor(MarkdownText);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Audits markdown outline text for hierarchy branching factor, max depth, child distribution, and layout fit.
    /// </summary>
    public static HierarchyBranchingAudit AuditHierarchyBranchingFactor(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            string emptySummary = "Outline hierarchy branching audit: 0 items (Empty outline)";
            return new HierarchyBranchingAudit(
                0, 0, 0, 0.0,
                new Dictionary<int, int>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Flat Outline (0 Levels)",
                "None",
                emptySummary);
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        bool inFence = false;
        var items = new List<(int Depth, string Text)>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || string.IsNullOrWhiteSpace(trimmed)) continue;

            int leadWs = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t')) leadWs++;
            var stripped = rawLine.Substring(leadWs);

            bool isBullet = stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ ") ||
                            System.Text.RegularExpressions.Regex.IsMatch(stripped, @"^\d+[\.\)]\s+");

            if (!isBullet) continue;

            int depth = Math.Max(0, leadWs / 2); // 2 spaces per level
            string content;
            if (stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ "))
            {
                content = stripped.Substring(2).Trim();
            }
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(stripped, @"^\d+[\.\)]\s+(.*)$");
                content = match.Success ? match.Groups[1].Value.Trim() : stripped;
            }

            items.Add((depth, content));
        }

        if (items.Count == 0)
        {
            string emptySummary = "Outline hierarchy branching audit: 0 items (No bullet nodes found)";
            return new HierarchyBranchingAudit(
                0, 0, 0, 0.0,
                new Dictionary<int, int>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Flat Outline (0 Levels)",
                "None",
                emptySummary);
        }

        int maxDepth = items.Max(i => i.Depth) + 1; // 1-based max depth level
        var levelCounts = new Dictionary<int, int>();
        for (int d = 1; d <= maxDepth; d++) levelCounts[d] = 0;

        foreach (var it in items)
        {
            int lvl = it.Depth + 1;
            levelCounts[lvl] = levelCounts.TryGetValue(lvl, out int c) ? c + 1 : 1;
        }

        // Compute parent-child branching
        var stack = new List<(int Depth, string Text, int ChildCount)>();
        var parentChildCounts = new List<int>();
        var overBranchingNodes = new List<string>();
        var deepBranches = new List<string>();

        for (int i = 0; i < items.Count; i++)
        {
            var cur = items[i];
            if (cur.Depth >= 3)
            {
                deepBranches.Add($"Level {cur.Depth + 1}: {cur.Text}");
            }

            while (stack.Count > 0 && stack[^1].Depth >= cur.Depth)
            {
                var popped = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (popped.ChildCount > 0)
                {
                    parentChildCounts.Add(popped.ChildCount);
                    if (popped.ChildCount > 6)
                    {
                        overBranchingNodes.Add($"'{popped.Text}' ({popped.ChildCount} children)");
                    }
                }
            }

            if (stack.Count > 0)
            {
                var parent = stack[^1];
                stack[^1] = (parent.Depth, parent.Text, parent.ChildCount + 1);
            }

            stack.Add((cur.Depth, cur.Text, 0));
        }

        while (stack.Count > 0)
        {
            var popped = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (popped.ChildCount > 0)
            {
                parentChildCounts.Add(popped.ChildCount);
                if (popped.ChildCount > 6)
                {
                    overBranchingNodes.Add($"'{popped.Text}' ({popped.ChildCount} children)");
                }
            }
        }

        int maxBranching = parentChildCounts.Count > 0 ? parentChildCounts.Max() : 0;
        double avgBranching = parentChildCounts.Count > 0 ? Math.Round(parentChildCounts.Average(), 1) : 0.0;

        string treeBalance = maxDepth switch
        {
            1 => "Flat Outline (1 Level)",
            _ when overBranchingNodes.Count > 0 => "Wide / Over-branching",
            _ when deepBranches.Count > 0 => "Deep / Nested",
            _ when maxBranching <= 4 && maxDepth <= 3 => "Well Balanced",
            _ => "Moderate Balance"
        };

        string recommendedCategory = maxDepth switch
        {
            1 when items.Count <= 5 => "Process / Sequential List",
            1 => "Grid / Matrix List",
            _ when maxDepth >= 2 && maxBranching <= 4 => "Hierarchy / Organization Chart",
            _ => "Multi-Level List / Table"
        };

        string summary = FormatHierarchyBranchingSummary(
            items.Count, maxDepth, maxBranching, avgBranching, treeBalance, recommendedCategory);

        return new HierarchyBranchingAudit(
            items.Count,
            maxDepth,
            maxBranching,
            avgBranching,
            levelCounts,
            overBranchingNodes,
            deepBranches,
            treeBalance,
            recommendedCategory,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of hierarchy branching and depth distribution.
    /// </summary>
    public static string FormatHierarchyBranchingSummary(
        int totalItems, int maxDepth, int maxBranching, double avgBranching, string treeBalance, string recommendedCategory)
    {
        if (totalItems <= 0) return "Outline hierarchy branching audit: 0 items";
        string itemLabel = totalItems == 1 ? "1 item" : $"{totalItems} items";
        string branchStr = maxDepth > 1 ? $" · Max branch: {maxBranching} (avg {avgBranching:0.#})" : "";
        return $"Hierarchy branching audit ({itemLabel} across {maxDepth} levels{branchStr} · {treeBalance}): Recommended: {recommendedCategory}";
    }

    /// <summary>
    /// Audits the container density and slide fit of the active outline against standard slide dimensions.
    /// </summary>
    public ContainerDensityFitAudit AuditContainerDensityFit()
    {
        int count = OutlineRows.Count > 0 ? OutlineRows.Count : 0;
        var result = AuditContainerDensityFit(SelectedCategory, count, 960.0);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Audits node count against container slide width to evaluate visual density, item fit, and overflow/truncation risks.
    /// </summary>
    public static ContainerDensityFitAudit AuditContainerDensityFit(
        string? category = null,
        int? nodeCount = null,
        double containerWidth = 960.0)
    {
        string cat = string.IsNullOrWhiteSpace(category) ? "Process / Steps" : category.Trim();
        int count = nodeCount ?? 0;

        if (count == 0)
        {
            string emptySummary = $"Container density audit ('{cat}', 0 items): Insufficient items (Empty outline)";
            return new ContainerDensityFitAudit(
                cat, 0, containerWidth, 0.0, "Empty", "Add items to outline", emptySummary);
        }

        // Available horizontal slide space calculation: 80px outer margins, 16px inter-node gaps
        double outerMargins = 80.0;
        double gap = 16.0;
        double availableWidth = Math.Max(100.0, containerWidth - outerMargins - ((count - 1) * gap));
        double nodeWidth = Math.Round(availableWidth / count, 1);

        string densityRating;
        string recommendation;

        if (nodeWidth >= 160.0)
        {
            densityRating = "Spacious Fit";
            recommendation = "Optimal typography legibility and spacing.";
        }
        else if (nodeWidth >= 110.0)
        {
            densityRating = "Optimal Density";
            recommendation = "Balanced layout with clear visual hierarchy.";
        }
        else if (nodeWidth >= 75.0)
        {
            densityRating = "Dense / Compact";
            recommendation = "Minor truncation risk; consider shortening labels or expanding canvas.";
        }
        else
        {
            densityRating = "Overcrowded (High Overflow Risk)";
            recommendation = $"Step count ({count}) exceeds horizontal slide capacity; switch to Multi-row Grid or split across slides.";
        }

        string summary = FormatContainerDensitySummary(cat, count, containerWidth, nodeWidth, densityRating, recommendation);

        return new ContainerDensityFitAudit(
            cat, count, containerWidth, nodeWidth, densityRating, recommendation, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of container density and slide fit metrics.
    /// </summary>
    public static string FormatContainerDensitySummary(
        string category, int nodeCount, double containerWidth, double nodeWidth, string densityRating, string? recommendation = null)
    {
        if (nodeCount <= 0) return $"Container density audit ('{category}', 0 items): Insufficient items";
        string itemLabel = nodeCount == 1 ? "1 item" : $"{nodeCount} items";
        string recStr = !string.IsNullOrWhiteSpace(recommendation) ? $" · {recommendation}" : "";
        return $"Container density audit ('{category}', {itemLabel} in {containerWidth:0}px slide): {nodeWidth:0.#}px/item · {densityRating}{recStr}";
    }

    /// <summary>
    /// Audits sibling text length variance and visual symmetry balance of the active outline.
    /// </summary>
    public SiblingTextVarianceAudit AuditSiblingTextLengthVariance() => AuditSiblingTextLengthVariance(MarkdownText);

    /// <summary>
    /// Audits sibling text length variance and visual symmetry balance from markdown outline text.
    /// </summary>
    public static SiblingTextVarianceAudit AuditSiblingTextLengthVariance(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new SiblingTextVarianceAudit(
                0, 0, 0.0, 0.0, "None",
                Array.Empty<SiblingGroupVariance>(), "Empty Outline",
                "Sibling text variance audit: 0 nodes (Empty outline)");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var parsed = new List<(string Text, int Depth)>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            int leadWs = 0;
            int spaceCount = 0;
            while (leadWs < rawLine.Length && (rawLine[leadWs] == ' ' || rawLine[leadWs] == '\t'))
            {
                spaceCount += rawLine[leadWs] == '\t' ? 4 : 1;
                leadWs++;
            }
            int depth = Math.Min(6, (spaceCount / 2) + 1);
            string trimmed = rawLine.Substring(leadWs).Trim();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                trimmed = trimmed.Substring(2).Trim();
            }
            else if (trimmed.Length >= 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.')
            {
                trimmed = trimmed.Substring(2).Trim();
            }
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                parsed.Add((trimmed, depth));
            }
        }

        if (parsed.Count == 0)
        {
            return new SiblingTextVarianceAudit(
                0, 0, 0.0, 0.0, "None",
                Array.Empty<SiblingGroupVariance>(), "Empty Outline",
                "Sibling text variance audit: 0 nodes (Empty outline)");
        }

        var parentAtDepth = new Dictionary<int, string>();
        parentAtDepth[0] = "Root";

        var groups = new Dictionary<(int Depth, string Parent), List<string>>();

        foreach (var (text, depth) in parsed)
        {
            string parent = parentAtDepth.TryGetValue(depth - 1, out var p) ? p : "Root";
            parentAtDepth[depth] = text;

            var key = (depth, parent);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string>();
                groups[key] = list;
            }
            list.Add(text);
        }

        var siblingVariances = new List<SiblingGroupVariance>();

        foreach (var kvp in groups)
        {
            var siblings = kvp.Value;
            if (siblings.Count < 2) continue;

            int n = siblings.Count;
            double mean = siblings.Average(s => s.Length);
            double sumSqDiff = siblings.Sum(s => Math.Pow(s.Length - mean, 2));
            double stdDev = Math.Sqrt(sumSqDiff / n);
            double cv = mean > 0 ? (stdDev / mean) : 0.0;

            siblingVariances.Add(new SiblingGroupVariance(
                kvp.Key.Parent,
                kvp.Key.Depth,
                n,
                Math.Round(mean, 1),
                Math.Round(stdDev, 1),
                Math.Round(cv, 2),
                siblings));
        }

        if (siblingVariances.Count == 0)
        {
            string singleSummary = $"Sibling text variance audit ({parsed.Count} items): No multi-sibling groups found (Linear hierarchy)";
            return new SiblingTextVarianceAudit(
                parsed.Count, 0, 0.0, 0.0, "None",
                Array.Empty<SiblingGroupVariance>(), "Linear Hierarchy", singleSummary);
        }

        double avgCv = Math.Round(siblingVariances.Average(g => g.CoefficientOfVariation), 2);
        double maxCv = Math.Round(siblingVariances.Max(g => g.CoefficientOfVariation), 2);
        var worstGroup = siblingVariances.OrderByDescending(g => g.CoefficientOfVariation).First();

        string symmetryCategory;
        if (avgCv <= 0.25) symmetryCategory = "Harmonious Symmetry (<25% CV)";
        else if (avgCv <= 0.45) symmetryCategory = "Balanced Variance (25–45% CV)";
        else if (avgCv <= 0.65) symmetryCategory = "Moderate Variance (45–65% CV)";
        else symmetryCategory = "Highly Asymmetric (>65% CV)";

        string summary = FormatSiblingTextVarianceSummary(
            parsed.Count, siblingVariances.Count, avgCv, maxCv, worstGroup.ParentTitle, symmetryCategory);

        return new SiblingTextVarianceAudit(
            parsed.Count,
            siblingVariances.Count,
            avgCv,
            maxCv,
            worstGroup.ParentTitle,
            siblingVariances,
            symmetryCategory,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of sibling text length variance and visual symmetry balance.
    /// </summary>
    public static string FormatSiblingTextVarianceSummary(
        int totalNodes, int groupCount, double avgCv, double maxCv, string worstParent, string symmetryCategory)
    {
        if (totalNodes <= 0) return "Sibling text variance audit: 0 nodes (Empty outline)";
        string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        string groupLabel = groupCount == 1 ? "1 sibling group" : $"{groupCount} sibling groups";

        string worstStr = groupCount > 0 && maxCv > 0.0
            ? $" · Worst: '{worstParent}' ({maxCv * 100:0}% CV)"
            : "";

        return $"Sibling text variance audit ({nodeLabel}, {groupLabel} · {symmetryCategory}): avg CV {avgCv * 100:0}%{worstStr}";
    }

    /// <summary>
    /// Audits linear flow monotonicity, step ordering, and sequential progression across outline steps.
    /// Evaluates whether steps progress monotonically (e.g. numbered steps 1, 2, 3...) or have order inversions/regressions.
    /// </summary>
    public LinearFlowMonotonicityAudit AuditLinearFlowMonotonicity() => AuditLinearFlowMonotonicity(MarkdownText, SelectedCategory);

    /// <summary>
    /// Audits linear flow monotonicity and directional flow vector from markdown outline text.
    /// </summary>
    public static LinearFlowMonotonicityAudit AuditLinearFlowMonotonicity(string? markdownText, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new LinearFlowMonotonicityAudit(
                0, 0, 0, 0, "None", "Empty Outline", "Process / Steps",
                "Linear flow monotonicity audit: 0 steps (Empty outline)");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var stepTitles = new List<string>();
        var stepNumbers = new List<int>();

        var stepNumRegex = new System.Text.RegularExpressions.Regex(@"(?:step|phase|stage|part|\b)\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            string trimmed = rawLine.TrimStart();
            int indent = rawLine.Length - trimmed.Length;
            if (indent >= 2) continue;

            string text = trimmed.TrimStart('-', '*', '+', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ')
                                 .Trim();
            if (string.IsNullOrEmpty(text)) continue;

            stepTitles.Add(text);

            var match = stepNumRegex.Match(trimmed);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int num))
            {
                stepNumbers.Add(num);
            }
            else
            {
                stepNumbers.Add(-1);
            }
        }

        if (stepTitles.Count == 0)
        {
            return new LinearFlowMonotonicityAudit(
                0, 0, 0, 0, "None", "Empty Outline", "Process / Steps",
                "Linear flow monotonicity audit: 0 steps (Empty outline)");
        }

        int explicitNumberedCount = stepNumbers.Count(n => n >= 0);
        int outOfOrderInversions = 0;
        int lastNum = -1;

        if (explicitNumberedCount >= 2)
        {
            foreach (int num in stepNumbers)
            {
                if (num < 0) continue;
                if (lastNum >= 0 && num <= lastNum)
                {
                    outOfOrderInversions++;
                }
                lastNum = num;
            }
        }

        string flowCategory;
        if (stepTitles.Count == 1)
        {
            flowCategory = "Single Step (Trivial Flow)";
        }
        else if (explicitNumberedCount >= 2 && outOfOrderInversions > 0)
        {
            flowCategory = "Non-Monotonic (Out-of-Order Steps)";
        }
        else if (explicitNumberedCount >= 2)
        {
            flowCategory = "Strict Monotonic Progression";
        }
        else
        {
            flowCategory = "Unnumbered Sequential Flow";
        }

        string recommendedCategory;
        if (stepTitles.Count <= 2) recommendedCategory = "Hierarchy / Tree";
        else if (stepTitles.Count <= 5) recommendedCategory = "Process / Steps";
        else if (stepTitles.Count <= 8) recommendedCategory = "Timeline";
        else recommendedCategory = "List / Clusters";

        string summary = FormatLinearFlowSummary(
            stepTitles.Count, explicitNumberedCount, outOfOrderInversions, flowCategory, recommendedCategory);

        return new LinearFlowMonotonicityAudit(
            stepTitles.Count,
            explicitNumberedCount,
            outOfOrderInversions,
            stepTitles.Count,
            stepTitles[0],
            flowCategory,
            recommendedCategory,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of linear flow monotonicity and step progression metrics.
    /// </summary>
    public static string FormatLinearFlowSummary(
        int stepCount, int numberedCount, int inversionCount, string flowCategory, string recommendedLayout)
    {
        if (stepCount == 0) return "Linear flow monotonicity audit: 0 steps (Empty outline)";
        string stepLabel = stepCount == 1 ? "1 step" : $"{stepCount} steps";

        string invNote = inversionCount > 0 ? $" · ⚠ {inversionCount} out-of-order inversions" : "";
        string numNote = numberedCount >= 2 ? $" ({numberedCount} numbered)" : "";

        return $"Linear flow audit ({stepLabel}{numNote} · {flowCategory}): Recommended layout '{recommendedLayout}'{invNote}";
    }
}

/// <summary>
/// Represents the result of an outline hierarchy branching and tree balance audit.
/// </summary>
public sealed record HierarchyBranchingAudit(
    int TotalItems,
    int MaxDepth,
    int MaxBranchingFactor,
    double AverageBranchingFactor,
    IReadOnlyDictionary<int, int> LevelCounts,
    IReadOnlyList<string> OverBranchingNodes,
    IReadOnlyList<string> DeepBranches,
    string TreeBalance,
    string RecommendedCategory,
    string Summary);


/// <summary>
/// Represents a recommended icon affinity for an outline node.
/// </summary>
public sealed record NodeIconAffinity(
    string NodeText,
    int Depth,
    string IconGlyph,
    string IconName,
    string MatchedKeyword);

/// <summary>
/// Represents the result of an outline icon recommendation audit.
/// </summary>
public sealed record OutlineIconRecommendationAudit(
    int TotalNodes,
    int MatchedNodes,
    double CoveragePercentage,
    IReadOnlyList<NodeIconAffinity> Recommendations,
    string Summary);

/// <summary>
/// Represents the result of an outline node character count budget and truncation risk audit.
/// </summary>
public sealed record NodeCharacterBudgetAudit(
    string Category,
    int TotalNodes,
    int OverBudgetCount,
    int MaxCharacterCount,
    int Level1Budget,
    int Level2Budget,
    string TruncationRiskLevel,
    IReadOnlyList<string> OffendingNodes,
    string Summary);

/// <summary>
/// Represents the result of a SmartArt layout aspect ratio and container fit analysis.
/// </summary>
public sealed record LayoutAspectRatioFit(
    string Category,
    int NodeCount,
    double EstimatedAspectRatio,
    string RecommendedRatioName,
    double RecommendedRatioValue,
    int OptimalWidth,
    int OptimalHeight,
    string FitQuality,
    string Summary);

/// <summary>
/// Represents the result of a SmartArt container density and slide width overflow risk audit.
/// </summary>
public sealed record ContainerDensityFitAudit(
    string Category,
    int NodeCount,
    double ContainerWidth,
    double NodeWidth,
    string DensityRating,
    string Recommendation,
    string Summary);

/// <summary>
/// Represents length variance metrics for a group of sibling nodes.
/// </summary>
public sealed record SiblingGroupVariance(
    string ParentTitle,
    int Depth,
    int SiblingCount,
    double MeanCharCount,
    double StdDevCharCount,
    double CoefficientOfVariation,
    IReadOnlyList<string> SiblingTexts);

/// <summary>
/// Represents the result of a sibling text length variance and visual symmetry balance audit.
/// </summary>
public sealed record SiblingTextVarianceAudit(
    int TotalNodes,
    int SiblingGroupCount,
    double AverageCoefficientOfVariation,
    double MaxCoefficientOfVariation,
    string WorstGroupParentTitle,
    IReadOnlyList<SiblingGroupVariance> SiblingGroups,
    string SymmetryCategory,
    string Summary);

/// <summary>
/// Represents the result of an audit of linear flow monotonicity, step ordering, and layout fit across outline items.
/// </summary>
public sealed record LinearFlowMonotonicityAudit(
    int TotalSteps,
    int ExplicitlyNumberedSteps,
    int InversionCount,
    int SequentialStepCount,
    string LeadingStepTitle,
    string FlowCategory,
    string RecommendedLayoutCategory,
    string Summary);

