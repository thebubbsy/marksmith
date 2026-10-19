using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Audits cyclic flow continuity, loop closure, and circular layout fit across outline items.
    /// </summary>
    public CyclicFlowContinuityAudit AuditCyclicFlowContinuity() => AuditCyclicFlowContinuity(MarkdownText, SelectedCategory);

    /// <summary>
    /// Audits cyclic flow continuity and circular loop closure across outline items.
    /// </summary>
    public static CyclicFlowContinuityAudit AuditCyclicFlowContinuity(string? markdownText, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new CyclicFlowContinuityAudit(
                0, false, 0, 0.0, "None", "Empty Outline", "Process / Steps",
                "Cyclic flow continuity audit: 0 steps (Empty outline)");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var stepTitles = new List<string>();
        int branchCount = 0;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            string trimmed = rawLine.TrimStart();
            int indent = rawLine.Length - trimmed.Length;
            if (indent >= 2)
            {
                branchCount++;
                continue;
            }

            string text = trimmed.TrimStart('-', '*', '+', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ')
                                 .Trim();
            if (string.IsNullOrEmpty(text)) continue;

            stepTitles.Add(text);
        }

        if (stepTitles.Count == 0)
        {
            return new CyclicFlowContinuityAudit(
                0, false, 0, 0.0, "None", "Empty Outline", "Process / Steps",
                "Cyclic flow continuity audit: 0 steps (Empty outline)");
        }

        if (stepTitles.Count < 3)
        {
            string insufficientSummary = $"Cyclic flow continuity audit: {stepTitles.Count} steps (Minimum 3 steps required for circular cycle layout) · Process / Steps";
            return new CyclicFlowContinuityAudit(
                stepTitles.Count, false, branchCount, 0.2, "None",
                "Insufficient Steps for Cycle (Minimum 3)", "Process / Steps", insufficientSummary);
        }

        bool hasClosure = false;
        string closureMechanism = "None";

        string lastStep = stepTitles[^1].ToLowerInvariant();
        string firstStep = stepTitles[0].ToLowerInvariant();

        if (lastStep.Contains("repeat") || lastStep.Contains("restart") || lastStep.Contains("loop") ||
            lastStep.Contains("iterate") || lastStep.Contains("cycle") || lastStep.Contains("return") ||
            lastStep.Contains("feedback") || lastStep.Contains("continuous") || lastStep.Contains("revisit"))
        {
            hasClosure = true;
            closureMechanism = "Tail Keyword Loopback";
        }
        else if (firstStep.Contains("cycle") || firstStep.Contains("loop") || firstStep.Contains("pdca") ||
                 firstStep.Contains("ooda") || firstStep.Contains("sprint"))
        {
            hasClosure = true;
            closureMechanism = "Header Cycle Motif";
        }
        else if (!string.IsNullOrEmpty(category) && category.Contains("Cycle", StringComparison.OrdinalIgnoreCase))
        {
            hasClosure = true;
            closureMechanism = "Explicit Cycle Layout Category";
        }

        double score = 0.5;
        if (hasClosure) score += 0.3;
        if (stepTitles.Count >= 3 && stepTitles.Count <= 7) score += 0.2;
        if (branchCount > 3) score -= 0.2;
        score = Math.Round(Math.Clamp(score, 0.0, 1.0), 2);

        string flowCategory;
        if (hasClosure)
        {
            flowCategory = "Closed Circular Feedback Loop";
        }
        else if (branchCount > 4)
        {
            flowCategory = "Branched Tree (Unsuitable for Pure Cycle)";
        }
        else
        {
            flowCategory = "Candidate Circular Pipeline (Open Loop)";
        }

        string recommendedCategory = (hasClosure || score >= 0.70) ? "Cycle / Circular" : "Process / Steps";
        string summary = FormatCyclicFlowSummary(
            stepTitles.Count, hasClosure, branchCount, score, flowCategory, recommendedCategory);

        return new CyclicFlowContinuityAudit(
            stepTitles.Count,
            hasClosure,
            branchCount,
            score,
            closureMechanism,
            flowCategory,
            recommendedCategory,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of cyclic flow continuity metrics.
    /// </summary>
    public static string FormatCyclicFlowSummary(
        int stepCount, bool hasClosure, int branchCount, double score, string category, string recommendedLayout)
    {
        if (stepCount == 0) return "Cyclic flow continuity audit: 0 steps (Empty outline)";
        string stepLabel = stepCount == 1 ? "1 step" : $"{stepCount} steps";
        string closureNote = hasClosure ? " · Closed Loop (↺)" : " · Open Loop (→)";
        string branchNote = branchCount > 0 ? $" · {branchCount} branches" : " · Flat";

        return $"Cyclic flow audit ({stepLabel}{closureNote}{branchNote} · {category}): Score {score:0.00} · Recommended '{recommendedLayout}'";
    }

    /// <summary>
    /// Audits the outline for 2x2 matrix quadrant balance (SWOT, Eisenhower, BCG, Risk matrix),
    /// evaluating item distribution uniformity and character weight symmetry across 4 quadrants.
    /// </summary>
    public MatrixQuadrantBalanceAudit AuditMatrixQuadrantBalance()
    {
        return AuditMatrixQuadrantBalance(MarkdownText);
    }

    /// <summary>
    /// Audits markdown outline text for 2x2 matrix quadrant balance and distribution uniformity.
    /// </summary>
    public static MatrixQuadrantBalanceAudit AuditMatrixQuadrantBalance(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            string emptySummary = "Matrix quadrant audit: 0 quadrants (Empty outline)";
            return new MatrixQuadrantBalanceAudit(
                0, 0, 0.0, 0.0, 0.0, Array.Empty<MatrixQuadrantInfo>(), "Empty Outline", emptySummary);
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        var quadrants = new List<(string Title, int Items, int Chars)>();
        string currentQuadTitle = "";
        int currentQuadItems = 0;
        int currentQuadChars = 0;
        bool inFence = false;

        void CommitCurrentQuadrant()
        {
            if (!string.IsNullOrEmpty(currentQuadTitle))
            {
                quadrants.Add((currentQuadTitle, Math.Max(1, currentQuadItems), currentQuadChars));
            }
        }

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
            while (leadWs < rawLine.Length && char.IsWhiteSpace(rawLine[leadWs])) leadWs++;

            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") ||
                            char.IsDigit(trimmed[0]) && trimmed.Length > 2 && trimmed[1] == '.' && trimmed[2] == ' ';

            if (!isBullet) continue;

            string content = trimmed.Length > 2 ? trimmed[2..].Trim() : trimmed;
            int depth = 1 + (leadWs >= 2 ? (leadWs / 2) : 0);

            if (depth == 1)
            {
                CommitCurrentQuadrant();
                currentQuadTitle = content;
                currentQuadItems = 0;
                currentQuadChars = content.Length;
            }
            else
            {
                currentQuadItems++;
                currentQuadChars += content.Length;
            }
        }
        CommitCurrentQuadrant();

        if (quadrants.Count == 0)
        {
            string noQuadSummary = "Matrix quadrant audit: 0 quadrants (No top-level bullets)";
            return new MatrixQuadrantBalanceAudit(
                0, 0, 0.0, 0.0, 0.0, Array.Empty<MatrixQuadrantInfo>(), "No Quadrants", noQuadSummary);
        }

        var quadInfos = new List<MatrixQuadrantInfo>();
        for (int i = 0; i < quadrants.Count; i++)
        {
            var q = quadrants[i];
            quadInfos.Add(new MatrixQuadrantInfo(i + 1, q.Title, q.Items, q.Chars));
        }

        int totalQuadrants = quadInfos.Count;
        int totalItems = quadInfos.Sum(q => q.ItemCount);
        double meanItems = (double)totalItems / totalQuadrants;

        double sumSquares = quadInfos.Sum(q => Math.Pow(q.ItemCount - meanItems, 2));
        double stdDev = Math.Sqrt(sumSquares / totalQuadrants);
        double cv = meanItems > 0 ? stdDev / meanItems : 0.0;
        double balanceScore = Math.Round(Math.Clamp((1.0 - cv) * 100.0, 0.0, 100.0), 1);

        string category;
        if (totalQuadrants == 4 && cv <= 0.15)
        {
            category = "Perfect 2x2 Quad Symmetry";
        }
        else if (totalQuadrants == 4 && cv <= 0.40)
        {
            category = "Balanced 2x2 Matrix";
        }
        else if (totalQuadrants == 4)
        {
            category = "Asymmetric 2x2 Matrix";
        }
        else if (totalQuadrants < 4)
        {
            category = $"Incomplete Matrix ({totalQuadrants}/4 Quadrants)";
        }
        else
        {
            category = $"Overcrowded Matrix ({totalQuadrants} Quadrants)";
        }

        string summary = FormatMatrixQuadrantSummary(
            totalQuadrants, totalItems, meanItems, cv, balanceScore, category);

        return new MatrixQuadrantBalanceAudit(
            totalQuadrants, totalItems, Math.Round(meanItems, 1), Math.Round(cv, 2),
            balanceScore, quadInfos, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of 2x2 matrix quadrant balance metrics.
    /// </summary>
    public static string FormatMatrixQuadrantSummary(
        int totalQuadrants, int totalItems, double meanItems, double cv, double balanceScore, string category)
    {
        if (totalQuadrants == 0) return "Matrix quadrant audit: 0 quadrants (Empty outline)";
        string quadLabel = totalQuadrants == 4 ? "4 quadrants (2x2)" : $"{totalQuadrants} quadrants";
        return $"Matrix quadrant audit ({quadLabel}, {totalItems} items · {category}): Avg {meanItems:0.#} items/quad, balance score {balanceScore:0.#}%";
    }

    /// <summary>
    /// Normalizes 2x2 matrix outline top-level quadrant headers (SWOT, Eisenhower, BCG, or Generic 2x2),
    /// canonicalizing quadrant titles while preserving all child bullets and hierarchy.
    /// </summary>
    public MatrixQuadrantNormalizationResult NormalizeMatrixQuadrantHeaders()
    {
        var result = NormalizeMatrixQuadrantHeaders(MarkdownText);
        if (result.NormalizedHeadersCount > 0)
        {
            MarkdownText = result.NormalizedMarkdown;
        }
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Normalizes markdown outline text by canonicalizing 2x2 matrix top-level quadrant headers.
    /// </summary>
    public static MatrixQuadrantNormalizationResult NormalizeMatrixQuadrantHeaders(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MatrixQuadrantNormalizationResult(
                markdown ?? string.Empty, "None", 0, "Normalize matrix headers: 0 headers (Empty outline)");
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        var topLevelLineIndices = new List<int>();
        var topLevelTitles = new List<string>();
        bool inFence = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || string.IsNullOrWhiteSpace(trimmed)) continue;

            int leadWs = 0;
            while (leadWs < raw.Length && char.IsWhiteSpace(raw[leadWs])) leadWs++;

            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") ||
                            char.IsDigit(trimmed[0]) && trimmed.Length > 2 && trimmed[1] == '.' && trimmed[2] == ' ';

            if (!isBullet) continue;

            int depth = 1 + (leadWs >= 2 ? (leadWs / 2) : 0);
            if (depth == 1)
            {
                topLevelLineIndices.Add(i);
                string content = trimmed.Length > 2 ? trimmed[2..].Trim() : trimmed;
                topLevelTitles.Add(content);
            }
        }

        if (topLevelTitles.Count != 4)
        {
            string non4Summary = $"Normalize matrix headers ({topLevelTitles.Count} top-level headers): Requires exactly 4 quadrant headers (Found {topLevelTitles.Count})";
            return new MatrixQuadrantNormalizationResult(markdown, "Non-4-Quadrant", 0, non4Summary);
        }

        string combined = string.Join(" ", topLevelTitles).ToLowerInvariant();
        string archetype;
        string[] canonical;

        if (combined.Contains("strength") || combined.Contains("weakness") || combined.Contains("threat") || combined.Contains("opportunit") ||
            (topLevelTitles[0].Equals("S", StringComparison.OrdinalIgnoreCase) && topLevelTitles[1].Equals("W", StringComparison.OrdinalIgnoreCase)))
        {
            archetype = "SWOT Matrix";
            canonical = new[] { "Strengths", "Weaknesses", "Opportunities", "Threats" };
        }
        else if (combined.Contains("urgent") || combined.Contains("importan") || combined.Contains("eisenhower"))
        {
            archetype = "Eisenhower Priority Matrix";
            canonical = new[]
            {
                "Urgent & Important (Do First)",
                "Important & Not Urgent (Schedule)",
                "Urgent & Not Important (Delegate)",
                "Not Urgent & Not Important (Eliminate)"
            };
        }
        else if (combined.Contains("star") || combined.Contains("cow") || combined.Contains("dog") || combined.Contains("question"))
        {
            archetype = "BCG Growth-Share Matrix";
            canonical = new[]
            {
                "Stars (High Growth, High Share)",
                "Question Marks (High Growth, Low Share)",
                "Cash Cows (Low Growth, High Share)",
                "Dogs (Low Growth, Low Share)"
            };
        }
        else
        {
            archetype = "2x2 Quadrant Matrix";
            canonical = new[]
            {
                "Quadrant I (Top-Right)",
                "Quadrant II (Top-Left)",
                "Quadrant III (Bottom-Left)",
                "Quadrant IV (Bottom-Right)"
            };
        }

        int normalizedCount = 0;
        for (int q = 0; q < 4; q++)
        {
            int lineIdx = topLevelLineIndices[q];
            string rawLine = lines[lineIdx];
            int leadWs = 0;
            while (leadWs < rawLine.Length && char.IsWhiteSpace(rawLine[leadWs])) leadWs++;
            string indent = rawLine[..leadWs];
            string trimmed = rawLine.Trim();
            string bulletPrefix = trimmed.StartsWith("- ") ? "- " :
                                 trimmed.StartsWith("* ") ? "* " :
                                 trimmed.StartsWith("+ ") ? "+ " : "- ";

            string newTitle = canonical[q];
            if (!topLevelTitles[q].Equals(newTitle, StringComparison.Ordinal))
            {
                lines[lineIdx] = $"{indent}{bulletPrefix}{newTitle}";
                normalizedCount++;
            }
        }

        string normalizedMd = string.Join("\n", lines);
        string summary = $"Normalize matrix headers ({archetype}): {normalizedCount}/4 headers canonicalized [{string.Join(", ", canonical)}]";

        return new MatrixQuadrantNormalizationResult(normalizedMd, archetype, normalizedCount, summary);
    }

    /// <summary>
    /// Audits hierarchical pyramid and funnel stage area proportionality, volume progression,
    /// and monotonic taper across outline items.
    /// </summary>
    public PyramidProportionalityAudit AuditPyramidStageProportionality()
    {
        var result = AuditPyramidStageProportionality(MarkdownText);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Audits markdown outline text for hierarchical pyramid / funnel stage area proportionality and volume progression.
    /// </summary>
    public static PyramidProportionalityAudit AuditPyramidStageProportionality(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            string emptySummary = "Pyramid proportionality audit: 0 stages · Outline is empty";
            return new PyramidProportionalityAudit(
                0, Array.Empty<PyramidStageInfo>(), 1.0, true, 0.0, "Empty Outline", emptySummary);
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        var stageInfos = new List<PyramidStageInfo>();
        bool inFence = false;

        string currentStageTitle = "";
        int currentStageCharCount = 0;
        int currentStageChildBullets = 0;
        int stageIndex = 0;

        void CommitCurrentStage()
        {
            if (stageIndex > 0)
            {
                stageInfos.Add(new PyramidStageInfo(
                    stageIndex, currentStageTitle, currentStageCharCount, currentStageChildBullets, 0.0));
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || string.IsNullOrWhiteSpace(trimmed)) continue;

            int leadWs = 0;
            while (leadWs < raw.Length && char.IsWhiteSpace(raw[leadWs])) leadWs++;

            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") ||
                            (char.IsDigit(trimmed[0]) && trimmed.Length > 2 && trimmed[1] == '.' && trimmed[2] == ' ');

            if (!isBullet)
            {
                if (stageIndex > 0) currentStageCharCount += trimmed.Length;
                continue;
            }

            int depth = 1 + (leadWs >= 2 ? (leadWs / 2) : 0);
            string content = trimmed.Length > 2 ? trimmed[2..].Trim() : trimmed;

            if (depth == 1)
            {
                CommitCurrentStage();
                stageIndex++;
                currentStageTitle = content;
                currentStageCharCount = content.Length;
                currentStageChildBullets = 0;
            }
            else
            {
                if (stageIndex > 0)
                {
                    currentStageChildBullets++;
                    currentStageCharCount += content.Length;
                }
            }
        }
        CommitCurrentStage();

        if (stageInfos.Count == 0)
        {
            string noStagesSummary = "Pyramid proportionality audit: 0 stages · No outline bullets found";
            return new PyramidProportionalityAudit(
                0, Array.Empty<PyramidStageInfo>(), 1.0, true, 0.0, "No Stages Found", noStagesSummary);
        }

        int totalChars = stageInfos.Sum(s => s.CharacterCount);
        var calibratedStages = stageInfos.Select(s => new PyramidStageInfo(
            s.StageIndex, s.StageTitle, s.CharacterCount, s.ChildBulletCount,
            totalChars > 0 ? Math.Round((s.CharacterCount / (double)totalChars) * 100.0, 1) : Math.Round(100.0 / stageInfos.Count, 1)
        )).ToList();

        if (calibratedStages.Count == 1)
        {
            string singleSummary = $"Pyramid proportionality audit (1 stage · Single Tier): '{calibratedStages[0].StageTitle}'";
            return new PyramidProportionalityAudit(
                1, calibratedStages, 1.0, true, 50.0, "Single Stage", singleSummary);
        }

        // Evaluate monotonicity
        bool isDecreasing = true;
        bool isIncreasing = true;
        double sumStepRatio = 0.0;
        int stepCount = 0;

        for (int i = 1; i < calibratedStages.Count; i++)
        {
            double prevVol = Math.Max(1.0, calibratedStages[i - 1].CharacterCount);
            double curVol = Math.Max(1.0, calibratedStages[i].CharacterCount);

            if (curVol > prevVol) isDecreasing = false;
            if (curVol < prevVol) isIncreasing = false;

            double ratio = curVol / prevVol;
            sumStepRatio += ratio;
            stepCount++;
        }

        double avgTaperRatio = stepCount > 0 ? Math.Round(sumStepRatio / stepCount, 2) : 1.0;
        bool isMonotonic = isDecreasing || isIncreasing;

        string category;
        double proportionalityScore;

        if (isDecreasing && avgTaperRatio <= 0.85)
        {
            category = "Monotonically Tapered (Top-to-Bottom / Inverted Funnel)";
            proportionalityScore = Math.Round(88.0 + (calibratedStages.Count >= 3 && calibratedStages.Count <= 6 ? 10.0 : 0.0), 1);
        }
        else if (isIncreasing && avgTaperRatio >= 1.15)
        {
            category = "Monotonically Tapered (Bottom-to-Top / Upward Pyramid)";
            proportionalityScore = Math.Round(88.0 + (calibratedStages.Count >= 3 && calibratedStages.Count <= 6 ? 10.0 : 0.0), 1);
        }
        else if (Math.Abs(avgTaperRatio - 1.0) <= 0.20)
        {
            category = "Uniform Slices (Balanced Hierarchy)";
            proportionalityScore = 85.0;
        }
        else
        {
            category = "Irregular Stage Volume Fluctuation";
            proportionalityScore = 65.0;
        }

        string stageLabel = calibratedStages.Count == 1 ? "1 stage" : $"{calibratedStages.Count} stages";
        string summary = $"Pyramid proportionality audit ({stageLabel} · {category}): Taper ratio {avgTaperRatio:0.00}x (Score {proportionalityScore:0.#}%)";

        return new PyramidProportionalityAudit(
            calibratedStages.Count, calibratedStages, avgTaperRatio, isMonotonic,
            proportionalityScore, category, summary);
    }

    /// <summary>
    /// Audits the active SmartArt outline for Venn diagram and Euler set overlap completeness,
    /// disjoint partitioning, and missing intersection regions.
    /// </summary>
    public VennDiagramCompletenessAudit AuditVennDiagramCompleteness()
    {
        var result = AuditVennDiagramCompleteness(MarkdownText);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Audits markdown outline text for Venn diagram set overlap completeness and intersection partitions.
    /// </summary>
    public static VennDiagramCompletenessAudit AuditVennDiagramCompleteness(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            string emptySummary = "Venn completeness audit: 0 sets · Outline is empty";
            return new VennDiagramCompletenessAudit(
                0, 0, 0, 0, 0.0, Array.Empty<string>(),
                Array.Empty<VennPartitionInfo>(), "Empty Outline", emptySummary);
        }

        var lines = markdown.Replace("\r", "").Split('\n');
        var partitions = new List<VennPartitionInfo>();
        var currentItems = new List<string>();
        string currentTitle = "";
        bool currentIsIntersection = false;
        bool inFence = false;

        void CommitCurrent()
        {
            if (!string.IsNullOrWhiteSpace(currentTitle))
            {
                string pType = currentIsIntersection ? "Intersection / Overlap" : "Primary Set";
                partitions.Add(new VennPartitionInfo(
                    currentTitle, pType, currentItems.Count, currentItems.ToList(), currentIsIntersection));
                currentItems.Clear();
                currentTitle = "";
            }
        }

        var intersectionKeywords = new[] { "overlap", "intersection", "both", "shared", "common", "∩", "all three", "triple", "center", "core", "universal" };

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

            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") ||
                            System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s+");

            if (isBullet)
            {
                int depth = 1 + (leadWs >= 2 ? (leadWs / 2) : 0);
                string content = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^[-*+\d\.\)]+\s*", "").Trim();

                if (depth == 1)
                {
                    CommitCurrent();
                    currentTitle = content;
                    string lower = content.ToLowerInvariant();
                    currentIsIntersection = intersectionKeywords.Any(k => lower.Contains(k)) ||
                                            (lower.Contains("&") && !lower.Contains("&&")) ||
                                            (lower.Contains(" and ") && (lower.Contains("set") || lower.Contains("group")));
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        currentItems.Add(content);
                    }
                }
            }
        }
        CommitCurrent();

        if (partitions.Count == 0)
        {
            string noPartitionsSummary = "Venn completeness audit: 0 sets · No outline bullets found";
            return new VennDiagramCompletenessAudit(
                0, 0, 0, 0, 0.0, Array.Empty<string>(),
                Array.Empty<VennPartitionInfo>(), "No Sets Found", noPartitionsSummary);
        }

        var primarySets = partitions.Where(p => !p.IsIntersection).ToList();
        var intersections = partitions.Where(p => p.IsIntersection).ToList();

        int primaryCount = primarySets.Count;
        int expectedRegions;
        var missingRegions = new List<string>();

        if (primaryCount == 2)
        {
            expectedRegions = 3; // Set A, Set B, A∩B
            if (intersections.Count == 0)
            {
                missingRegions.Add($"Overlap between '{primarySets[0].PartitionName}' and '{primarySets[1].PartitionName}'");
            }
        }
        else if (primaryCount == 3)
        {
            expectedRegions = 7; // A, B, C, AB, BC, AC, ABC
            int expectedIntersections = 4;
            int missingCount = Math.Max(0, expectedIntersections - intersections.Count);
            if (missingCount > 0)
            {
                if (!intersections.Any(i => i.PartitionName.ToLowerInvariant().Contains("all") || i.PartitionName.ToLowerInvariant().Contains("three") || i.PartitionName.ToLowerInvariant().Contains("triple")))
                {
                    missingRegions.Add("Triple Central Overlap (All Three Sets)");
                }
                if (missingRegions.Count < missingCount)
                {
                    missingRegions.Add($"{missingCount} Pairwise/Triple Intersection Overlaps");
                }
            }
        }
        else if (primaryCount <= 1)
        {
            expectedRegions = 1;
        }
        else
        {
            expectedRegions = (int)Math.Min(15, Math.Pow(2, primaryCount) - 1);
            if (intersections.Count == 0) missingRegions.Add("Intersection Overlaps");
        }

        int definedRegions = partitions.Count;
        int missingRegionsCount = Math.Max(0, expectedRegions - definedRegions);
        double completeness = expectedRegions > 0
            ? Math.Round(Math.Clamp((double)definedRegions / expectedRegions * 100.0, 0.0, 100.0), 1)
            : 0.0;

        string category;
        if (primaryCount <= 1)
        {
            category = "Single Set / Non-Venn Outline";
        }
        else if (primaryCount == 2)
        {
            category = intersections.Count >= 1 ? "Complete 2-Set Venn (3/3 Partitions)" : "Partial 2-Set Venn (Missing Overlap)";
        }
        else if (primaryCount == 3)
        {
            if (definedRegions >= 7) category = "Complete 3-Set Venn (7/7 Partitions)";
            else if (intersections.Count > 0) category = $"Partial 3-Set Venn ({definedRegions}/7 Partitions)";
            else category = "Disjoint 3-Set Outline (Zero Overlaps)";
        }
        else
        {
            category = $"Multi-Set Euler Diagram ({primaryCount} Sets)";
        }

        string summary = FormatVennSummary(
            primaryCount, definedRegions, expectedRegions, missingRegionsCount, completeness, category);

        return new VennDiagramCompletenessAudit(
            primaryCount, expectedRegions, definedRegions, missingRegionsCount, completeness,
            missingRegions, partitions, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of Venn diagram set overlap completeness.
    /// </summary>
    public static string FormatVennSummary(
        int primaryCount, int definedRegions, int expectedRegions, int missingCount,
        double completeness, string category)
    {
        if (primaryCount == 0) return "Venn completeness audit: 0 sets · Outline is empty";
        string setsLabel = primaryCount == 1 ? "1 set" : $"{primaryCount} sets";
        string missingNote = missingCount > 0 ? $" · ⚠ {missingCount} missing regions" : " · Complete";
        return $"Venn completeness audit ({setsLabel} · {category}): {definedRegions}/{expectedRegions} regions ({completeness:0.#}% complete){missingNote}";
    }

    /// <summary>
    /// Audits timeline and roadmap milestone outlines for chronological date monotonicity,
    /// ascending sequence consistency, date format uniformity, and backwards date inversions.
    /// </summary>
    public TimelineChronologyAudit AuditTimelineMilestoneChronology()
    {
        var result = AuditTimelineMilestoneChronology(MarkdownText);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Static overload auditing timeline milestone chronology from markdown outline text.
    /// </summary>
    public static TimelineChronologyAudit AuditTimelineMilestoneChronology(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new TimelineChronologyAudit(
                0, 0, 0, 100.0, true, "None",
                Array.Empty<TimelineMilestoneInfo>(),
                Array.Empty<string>(),
                "Empty Timeline Outline",
                "Timeline chronology audit: 0 milestones · Outline is empty");
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var milestones = new List<TimelineMilestoneInfo>();
        var formatCounts = new Dictionary<string, int>();

        var isoDateRegex = new System.Text.RegularExpressions.Regex(@"\b(20\d\d|19\d\d)-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])\b");
        var yearMonthRegex = new System.Text.RegularExpressions.Regex(@"\b(20\d\d|19\d\d)-(0[1-9]|1[0-2])\b");
        var quarterRegex = new System.Text.RegularExpressions.Regex(@"\bQ([1-4])[\s\-_/]+(20\d\d|19\d\d)\b|\b(20\d\d|19\d\d)[\s\-_/]+Q([1-4])\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var monthYearRegex = new System.Text.RegularExpressions.Regex(@"\b(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)[\s,]+(20\d\d|19\d\d)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var yearRegex = new System.Text.RegularExpressions.Regex(@"\b(20\d\d|19\d\d)\b");
        var phaseRegex = new System.Text.RegularExpressions.Regex(@"\b(Sprint|Phase|Milestone|Stage|Step|Wave)\s+(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int index = 1;
        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            bool isItem = line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ ") ||
                          System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+[\.\)]\s+") ||
                          line.StartsWith("#");
            if (!isItem) continue;

            string clean = System.Text.RegularExpressions.Regex.Replace(line, @"^([#\-\*\+\d\.\)]+)\s*", "");
            clean = clean.Trim();
            if (clean.Length == 0) continue;

            string rawDateStr = "";
            DateTime? parsedDate = null;
            string formatCategory = "Undated";

            var isoM = isoDateRegex.Match(clean);
            if (isoM.Success)
            {
                rawDateStr = isoM.Value;
                if (DateTime.TryParse(rawDateStr, out var dt))
                {
                    parsedDate = dt;
                    formatCategory = "ISO Date (YYYY-MM-DD)";
                }
            }
            else
            {
                var qM = quarterRegex.Match(clean);
                if (qM.Success)
                {
                    rawDateStr = qM.Value;
                    int qNum = qM.Groups[1].Success ? int.Parse(qM.Groups[1].Value) : int.Parse(qM.Groups[4].Value);
                    int year = qM.Groups[2].Success ? int.Parse(qM.Groups[2].Value) : int.Parse(qM.Groups[3].Value);
                    int month = (qNum - 1) * 3 + 1;
                    parsedDate = new DateTime(year, month, 1);
                    formatCategory = "Quarter (Q# YYYY)";
                }
                else
                {
                    var myM = monthYearRegex.Match(clean);
                    if (myM.Success)
                    {
                        rawDateStr = myM.Value;
                        if (DateTime.TryParse(rawDateStr, out var dt))
                        {
                            parsedDate = dt;
                            formatCategory = "Month-Year (Mon YYYY)";
                        }
                    }
                    else
                    {
                        var ymM = yearMonthRegex.Match(clean);
                        if (ymM.Success)
                        {
                            rawDateStr = ymM.Value;
                            if (DateTime.TryParse(rawDateStr + "-01", out var dt))
                            {
                                parsedDate = dt;
                                formatCategory = "Year-Month (YYYY-MM)";
                            }
                        }
                        else
                        {
                            var yM = yearRegex.Match(clean);
                            if (yM.Success)
                            {
                                rawDateStr = yM.Value;
                                int year = int.Parse(yM.Value);
                                parsedDate = new DateTime(year, 1, 1);
                                formatCategory = "Year Only (YYYY)";
                            }
                            else
                            {
                                var pM = phaseRegex.Match(clean);
                                if (pM.Success)
                                {
                                    rawDateStr = pM.Value;
                                    int phaseNum = int.Parse(pM.Groups[2].Value);
                                    parsedDate = new DateTime(2000, 1, 1).AddMonths(phaseNum);
                                    formatCategory = $"{pM.Groups[1].Value} Numbering";
                                }
                            }
                        }
                    }
                }
            }

            if (formatCategory != "Undated")
            {
                formatCounts[formatCategory] = formatCounts.TryGetValue(formatCategory, out int fc) ? fc + 1 : 1;
            }

            milestones.Add(new TimelineMilestoneInfo(
                index++,
                line,
                clean,
                rawDateStr,
                parsedDate.HasValue,
                parsedDate,
                formatCategory,
                true));
        }

        if (milestones.Count == 0)
        {
            return new TimelineChronologyAudit(
                0, 0, 0, 100.0, true, "None",
                Array.Empty<TimelineMilestoneInfo>(),
                Array.Empty<string>(),
                "No Milestones Found",
                "Timeline chronology audit: 0 milestones · Outline contains no milestones");
        }

        int datedCount = milestones.Count(m => m.HasDate);
        int inversions = 0;
        var inversionNotes = new List<string>();
        var updatedMilestones = new List<TimelineMilestoneInfo>();

        DateTime? prevDate = null;
        string prevTitle = "";

        for (int i = 0; i < milestones.Count; i++)
        {
            var m = milestones[i];
            bool ordered = true;

            if (m.HasDate && m.ParsedDate.HasValue)
            {
                if (prevDate.HasValue && m.ParsedDate.Value < prevDate.Value)
                {
                    ordered = false;
                    inversions++;
                    inversionNotes.Add($"Step #{m.Index} '{m.CleanTitle}' ({m.DateString}) occurs before Step '{prevTitle}' ({prevDate.Value:yyyy-MM-dd})");
                }
                prevDate = m.ParsedDate.Value;
                prevTitle = m.CleanTitle;
            }

            updatedMilestones.Add(m with { IsChronologicallyOrdered = ordered });
        }

        bool isUniformFormat = formatCounts.Count <= 1;
        string dominantFormat = formatCounts.Count > 0
            ? formatCounts.OrderByDescending(kv => kv.Value).First().Key
            : "Undated";

        double monotonicityScore = datedCount >= 2
            ? Math.Round(Math.Clamp((1.0 - ((double)inversions / (datedCount - 1))) * 100.0, 0.0, 100.0), 1)
            : 100.0;

        string category;
        if (datedCount == 0)
        {
            category = "Undated Sequential Steps";
        }
        else if (inversions > 0)
        {
            category = $"Chronological Inversion Detected ({inversions} Out-of-Order Steps)";
        }
        else if (!isUniformFormat)
        {
            category = "Chronologically Ordered with Mixed Date Formats";
        }
        else
        {
            category = "Strict Chronological Progression (100% Monotonic)";
        }

        string summary = FormatTimelineChronologySummary(
            milestones.Count, datedCount, inversions, monotonicityScore, isUniformFormat, dominantFormat, category, inversionNotes);

        return new TimelineChronologyAudit(
            milestones.Count,
            datedCount,
            inversions,
            monotonicityScore,
            isUniformFormat,
            dominantFormat,
            updatedMilestones,
            inversionNotes,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of timeline milestone chronology and date format metrics.
    /// </summary>
    public static string FormatTimelineChronologySummary(
        int totalMilestones, int datedCount, int inversions, double monotonicityScore,
        bool isUniformFormat, string dominantFormat, string category, IReadOnlyList<string> inversionsList)
    {
        if (totalMilestones == 0) return "Timeline chronology audit: 0 milestones · Outline is empty";

        string milestoneLabel = totalMilestones == 1 ? "1 milestone" : $"{totalMilestones} milestones";
        string datedLabel = datedCount > 0 ? $"{datedCount}/{totalMilestones} dated" : "0 dated";
        string formatStr = datedCount > 0 ? $" · {dominantFormat}" : "";
        string inversionStr = inversions > 0 ? $" · ⚠ {inversions} backwards date jumps" : "";

        return $"Timeline chronology audit ({milestoneLabel} [{datedLabel}] · {category}): {monotonicityScore:0.#}% chronological score{formatStr}{inversionStr}";
    }

    /// <summary>
    /// Audits cyclic process diagrams for loop convergence, recurrence closure, terminal state detection,
    /// and word count balance across stages.
    /// </summary>
    public CyclicProcessConvergenceAudit AuditCyclicProcessConvergence(string? markdown = null)
    {
        var result = AuditCyclicProcessConvergence(markdown ?? MarkdownText, SelectedCategory);
        StatusMessage = result.Summary;
        return result;
    }

    /// <summary>
    /// Static overload auditing cyclic process convergence from markdown text and layout preset.
    /// </summary>
    public static CyclicProcessConvergenceAudit AuditCyclicProcessConvergence(string? markdown, string? layoutPreset = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            string emptySummary = "Cyclic process audit: 0 stages · Outline is empty";
            return new CyclicProcessConvergenceAudit(
                0, false, 0, 0.0, 100.0, Array.Empty<CyclicStageInfo>(), "None", "Empty Outline", emptySummary);
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var stageList = new List<(string Title, int Words, int Children)>();
        bool inFence = false;

        string currentTitle = "";
        int currentWords = 0;
        int currentChildren = 0;

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

            bool isTopBullet = (leadWs < 2) && (stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ ") ||
                               System.Text.RegularExpressions.Regex.IsMatch(stripped, @"^\d+[\.\)]\s+"));

            bool isChildBullet = (leadWs >= 2) && (stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ ") ||
                                 System.Text.RegularExpressions.Regex.IsMatch(stripped, @"^\d+[\.\)]\s+"));

            if (isTopBullet)
            {
                if (!string.IsNullOrEmpty(currentTitle))
                {
                    stageList.Add((currentTitle, currentWords, currentChildren));
                }
                string cleanText = stripped;
                if (stripped.StartsWith("- ") || stripped.StartsWith("* ") || stripped.StartsWith("+ "))
                    cleanText = stripped.Substring(2).Trim();
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(stripped, @"^\d+[\.\)]\s+");
                    cleanText = stripped.Substring(m.Length).Trim();
                }
                currentTitle = cleanText;
                currentWords = cleanText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                currentChildren = 0;
            }
            else if (isChildBullet)
            {
                currentChildren++;
            }
        }

        if (!string.IsNullOrEmpty(currentTitle))
        {
            stageList.Add((currentTitle, currentWords, currentChildren));
        }

        if (stageList.Count == 0)
        {
            string noStagesSummary = "Cyclic process audit: 0 stages · No outline bullets detected";
            return new CyclicProcessConvergenceAudit(
                0, false, 0, 0.0, 100.0, Array.Empty<CyclicStageInfo>(), "None", "No Stages Detected", noStagesSummary);
        }

        if (stageList.Count == 1)
        {
            var s = stageList[0];
            string singleSummary = $"Cyclic process audit (1 stage '{s.Title}'): Incomplete loop · Minimum 2 stages required for cyclic recurrence";
            var singleInfo = new CyclicStageInfo(1, s.Title, s.Words, s.Children, s.Title, false);
            return new CyclicProcessConvergenceAudit(
                1, false, 0, 0.0, 50.0, new[] { singleInfo }, "Single Stage", "Incomplete Loop (1 Stage)", singleSummary);
        }

        // Loop closure detection
        string preset = layoutPreset ?? "";
        bool isCyclicPreset = preset.Contains("cycle", StringComparison.OrdinalIgnoreCase) ||
                              preset.Contains("radial", StringComparison.OrdinalIgnoreCase) ||
                              preset.Contains("gear", StringComparison.OrdinalIgnoreCase) ||
                              preset.Contains("circle", StringComparison.OrdinalIgnoreCase);

        bool hasExplicitLoopKeyword = false;
        string firstStageTitle = stageList[0].Title.ToLowerInvariant();
        var lastStage = stageList[^1];
        string lastText = lastStage.Title.ToLowerInvariant();

        string[] loopKeywords = { "repeat", "loop", "cycle", "recurrence", "continuous", "feedback", "iterate", "return" };
        if (loopKeywords.Any(k => lastText.Contains(k)) || lastText.Contains(firstStageTitle))
        {
            hasExplicitLoopKeyword = true;
        }

        bool hasRecurrenceClosure = isCyclicPreset || hasExplicitLoopKeyword;
        string closureMechanism = isCyclicPreset
            ? (hasExplicitLoopKeyword ? "Preset Geometry & Explicit Recurrence Phrase" : "Layout Preset Geometric Closure")
            : (hasExplicitLoopKeyword ? "Explicit Recurrence Keyword / Stage Ref" : "None (Linear)");

        string[] terminalKeywords = { "terminate", "exit", "end", "stop", "final", "deliver", "archive" };
        int terminalSinkCount = 0;
        var stages = new List<CyclicStageInfo>();

        for (int i = 0; i < stageList.Count; i++)
        {
            var cur = stageList[i];
            string nextTitle = (i < stageList.Count - 1) ? stageList[i + 1].Title : stageList[0].Title;
            bool isTerminal = false;

            if (terminalKeywords.Any(k => cur.Title.ToLowerInvariant().Contains(k)) && !hasExplicitLoopKeyword)
            {
                isTerminal = true;
                terminalSinkCount++;
            }

            stages.Add(new CyclicStageInfo(i + 1, cur.Title, cur.Words, cur.Children, nextTitle, isTerminal));
        }

        double meanWords = stageList.Average(s => s.Words);
        double variance = stageList.Count > 1
            ? stageList.Select(s => (s.Words - meanWords) * (s.Words - meanWords)).Average()
            : 0.0;
        double stdDev = Math.Round(Math.Sqrt(variance), 1);

        double convergenceScore = 100.0;
        if (!hasRecurrenceClosure) convergenceScore -= 40.0;
        convergenceScore -= (terminalSinkCount * 15.0);
        convergenceScore -= (stdDev * 2.0);
        convergenceScore = Math.Round(Math.Clamp(convergenceScore, 0.0, 100.0), 1);

        string category;
        if (hasRecurrenceClosure && terminalSinkCount == 0 && convergenceScore >= 80.0)
        {
            category = "Seamless Closed-Loop Convergence";
        }
        else if (hasRecurrenceClosure && terminalSinkCount > 0)
        {
            category = "Cyclic Loop with Terminal Sink Branches";
        }
        else if (hasRecurrenceClosure)
        {
            category = "Balanced Cyclic Recurrence";
        }
        else
        {
            category = "Open-Ended Linear Sequence (Missing Recurrence Closure)";
        }

        string summary = FormatCyclicProcessSummary(
            stageList.Count, hasRecurrenceClosure, terminalSinkCount, stdDev, convergenceScore, closureMechanism, category);

        return new CyclicProcessConvergenceAudit(
            stageList.Count,
            hasRecurrenceClosure,
            terminalSinkCount,
            stdDev,
            convergenceScore,
            stages,
            closureMechanism,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of cyclic process convergence metrics.
    /// </summary>
    public static string FormatCyclicProcessSummary(
        int totalStages, bool hasClosure, int terminalSinks, double wordVariance,
        double convergenceScore, string mechanism, string category)
    {
        if (totalStages == 0) return "Cyclic process audit: 0 stages · Outline is empty";

        string stageLabel = totalStages == 1 ? "1 stage" : $"{totalStages} stages";
        string closureStr = hasClosure ? $"Closed loop ({mechanism})" : "Open-ended sequence";
        string sinkStr = terminalSinks > 0 ? $" · ⚠ {terminalSinks} terminal sink{(terminalSinks == 1 ? "" : "s")}" : "";

        return $"Cyclic process audit ({stageLabel} · {closureStr} · {category}): {convergenceScore:0.#}% convergence score (Word balance variance {wordVariance:0.#}w){sinkStr}";
    }

    /// <summary>
    /// Audits organizational hierarchy tree outlines for span of control, branching factor standard deviation,
    /// manager-to-report ratios, and identifies flat vs tall hierarchy bottlenecks.
    /// </summary>
    public HierarchySpanOfControlAudit AuditHierarchySpanOfControl(string? markdown = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Hierarchy span of control audit: 0 nodes · Outline is empty";
            StatusMessage = emptySummary;
            return new HierarchySpanOfControlAudit(
                0, 0, 0, 0, 0.0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<HierarchyNodeSpanInfo>(), "Empty Outline", emptySummary);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var parsedNodes = new List<(int Level, string Title)>();

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("<!--") || trimmed.StartsWith("```") || trimmed.StartsWith(":::")) continue;

            // Calculate indent level
            int leadingSpaces = 0;
            while (leadingSpaces < rawLine.Length && (rawLine[leadingSpaces] == ' ' || rawLine[leadingSpaces] == '\t'))
            {
                leadingSpaces += (rawLine[leadingSpaces] == '\t' ? 4 : 1);
            }
            int level = leadingSpaces / 2;

            // Strip bullet markers
            string clean = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^([-*+]|\d+\.)\s*", "").Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;

            parsedNodes.Add((level, clean));
        }

        if (parsedNodes.Count == 0)
        {
            string emptySummary = "Hierarchy span of control audit: 0 nodes · Outline contains no hierarchical list items";
            StatusMessage = emptySummary;
            return new HierarchySpanOfControlAudit(
                0, 0, 0, 0, 0.0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<HierarchyNodeSpanInfo>(), "Empty Outline", emptySummary);
        }

        // Build tree children using indent stack
        int nTotal = parsedNodes.Count;
        var childrenMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < nTotal; i++) childrenMap[i] = new List<int>();

        var stack = new Stack<(int Level, int Index)>();
        for (int i = 0; i < nTotal; i++)
        {
            int lvl = parsedNodes[i].Level;
            while (stack.Count > 0 && stack.Peek().Level >= lvl)
            {
                stack.Pop();
            }
            if (stack.Count > 0)
            {
                childrenMap[stack.Peek().Index].Add(i);
            }
            stack.Push((lvl, i));
        }

        // Count descendant subtrees
        int CountDescendants(int idx)
        {
            int count = childrenMap[idx].Count;
            foreach (var ch in childrenMap[idx])
            {
                count += CountDescendants(ch);
            }
            return count;
        }

        var nodeSpanInfos = new List<HierarchyNodeSpanInfo>();
        var managerSpans = new List<double>();
        int overloadedCount = 0;
        int underutilizedCount = 0;
        int maxDepth = parsedNodes.Max(n => n.Level);

        for (int i = 0; i < nTotal; i++)
        {
            var (lvl, title) = parsedNodes[i];
            int directReports = childrenMap[i].Count;
            int subtreeCount = CountDescendants(i);
            bool isManager = directReports > 0;
            bool isOverloaded = directReports > 8;
            bool isUnderutilized = isManager && directReports < 3;

            if (isManager)
            {
                managerSpans.Add(directReports);
                if (isOverloaded) overloadedCount++;
                if (isUnderutilized) underutilizedCount++;
            }

            nodeSpanInfos.Add(new HierarchyNodeSpanInfo(
                lvl, title, directReports, subtreeCount, isManager, isOverloaded, isUnderutilized));
        }

        int managerCount = managerSpans.Count;
        int icCount = nTotal - managerCount;
        double meanSpan = managerCount > 0 ? Math.Round(managerSpans.Average(), 1) : 0.0;
        double variance = managerCount > 1
            ? managerSpans.Select(s => (s - meanSpan) * (s - meanSpan)).Average()
            : 0.0;
        double stdDev = Math.Round(Math.Sqrt(variance), 1);

        double managerRatio = icCount > 0
            ? Math.Round((double)managerCount / icCount, 2)
            : (managerCount > 0 ? 1.0 : 0.0);

        double balanceScore;
        string category;

        if (managerCount == 0)
        {
            balanceScore = 100.0;
            category = nTotal == 1 ? "Solo Individual Contributor" : "Flat Non-Hierarchical List";
        }
        else
        {
            balanceScore = Math.Round(Math.Clamp(100.0 - (overloadedCount * 18.0) - (underutilizedCount * 10.0) - (stdDev * 3.0), 0.0, 100.0), 1);
            if (overloadedCount > 0)
            {
                category = "Manager Bottlenecks (High Span of Control)";
            }
            else if (underutilizedCount > 0)
            {
                category = "Micro-Management Inefficiencies (Low Span)";
            }
            else if (balanceScore >= 80.0)
            {
                category = "Optimal Organizational Hierarchy Balance";
            }
            else
            {
                category = "Moderate Hierarchy Distribution";
            }
        }

        string summary = FormatHierarchySpanSummary(
            nTotal, managerCount, icCount, maxDepth, meanSpan, stdDev, managerRatio,
            overloadedCount, underutilizedCount, balanceScore, category);

        StatusMessage = summary;

        return new HierarchySpanOfControlAudit(
            nTotal,
            managerCount,
            icCount,
            maxDepth,
            meanSpan,
            stdDev,
            managerRatio,
            overloadedCount,
            underutilizedCount,
            balanceScore,
            nodeSpanInfos,
            category,
            summary);
    }

    /// <summary>
    /// Static overload auditing organizational hierarchy span of control from raw markdown outline text.
    /// </summary>
    public static HierarchySpanOfControlAudit AuditHierarchySpanOfControl(string? markdown, bool isStatic)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditHierarchySpanOfControl(markdown);
    }

    /// <summary>
    /// Formats a human-readable summary of hierarchy span of control and manager-to-report ratios.
    /// </summary>
    public static string FormatHierarchySpanSummary(
        int totalNodes, int managerCount, int icCount, int maxDepth, double meanSpan, double stdDev,
        double ratio, int overloaded, int underutilized, double balanceScore, string category)
    {
        if (totalNodes == 0) return "Hierarchy span of control audit: 0 nodes · Outline is empty";

        string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        string mgrLabel = managerCount == 1 ? "1 manager" : $"{managerCount} managers";
        string icLabel = icCount == 1 ? "1 IC" : $"{icCount} ICs";

        if (managerCount == 0)
        {
            return $"Hierarchy span of control audit ({nodeLabel} · {category}): All items at root depth 0";
        }

        string issues = "";
        if (overloaded > 0) issues += $" · ⚠ {overloaded} overloaded manager{(overloaded == 1 ? "" : "s")} (>8 reports)";
        if (underutilized > 0) issues += $" · ⚠ {underutilized} low-span manager{(underutilized == 1 ? "" : "s")} (<3 reports)";

        return $"Hierarchy span of control audit ({nodeLabel} · {mgrLabel}, {icLabel} · Depth {maxDepth} · {category}): Span: ~{meanSpan:0.#} reports/mgr (Std dev {stdDev:0.#}) · Ratio: {ratio:0.##} mgr/IC · {balanceScore:0.#}% balance{issues}";
    }

    /// <summary>
    /// Audits SmartArt layout typography scaling, label font-fit, multi-line text wrapping,
    /// and text clipping hazards across diagram node boundaries.
    /// </summary>
    public SmartArtTypographyFontFitAudit AuditLayoutTypographyAndFontFit(
        string? markdown = null, double canvasWidth = 800.0, double canvasHeight = 500.0)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Typography font-fit audit: 0 nodes · Outline is empty";
            StatusMessage = emptySummary;
            return new SmartArtTypographyFontFitAudit(
                0, canvasWidth, canvasHeight, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<SmartArtNodeTypographyInfo>(), "Empty Outline", emptySummary);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var parsedNodes = new List<string>();

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("<!--") || trimmed.StartsWith("```") || trimmed.StartsWith(":::")) continue;

            string clean = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^([-*+]|\d+\.)\s*", "").Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;

            parsedNodes.Add(clean);
        }

        if (parsedNodes.Count == 0)
        {
            string emptySummary = "Typography font-fit audit: 0 nodes · Outline contains no items";
            StatusMessage = emptySummary;
            return new SmartArtTypographyFontFitAudit(
                0, canvasWidth, canvasHeight, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<SmartArtNodeTypographyInfo>(), "Empty Outline", emptySummary);
        }

        int nodeCount = parsedNodes.Count;
        double estimatedNodeWidth = Math.Max(120.0, (canvasWidth - 80.0) / Math.Max(1, Math.Min(nodeCount, 5)));
        double estimatedNodeHeight = Math.Max(60.0, (canvasHeight - 60.0) / Math.Max(1, (nodeCount + 4) / 5));

        var nodeInfos = new List<SmartArtNodeTypographyInfo>();
        int clippingRiskCount = 0;
        double sumFontSize = 0.0;
        double minFont = double.MaxValue;
        double maxFont = double.MinValue;

        for (int i = 0; i < parsedNodes.Count; i++)
        {
            string title = parsedNodes[i];
            int charCount = title.Length;
            var words = title.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int wordCount = words.Length;

            double availW = Math.Max(60.0, estimatedNodeWidth - 24.0);
            double availH = Math.Max(40.0, estimatedNodeHeight - 20.0);

            double optimalFontSize = 14.0;
            bool clippingRisk = false;
            bool requiresHyphenation = words.Any(w => w.Length > 16);

            for (double fs = 18.0; fs >= 8.0; fs -= 0.5)
            {
                double charW = fs * 0.55;
                double lineH = fs * 1.35;
                int maxCharsPerLine = Math.Max(4, (int)(availW / charW));
                int estLines = Math.Max(1, (int)Math.Ceiling((double)charCount / maxCharsPerLine));
                double reqHeight = estLines * lineH;

                if (reqHeight <= availH && !words.Any(w => w.Length * charW > availW))
                {
                    optimalFontSize = fs;
                    break;
                }
                optimalFontSize = fs;
            }

            if (optimalFontSize <= 8.5 || charCount > 80 || requiresHyphenation)
            {
                clippingRisk = true;
                clippingRiskCount++;
            }

            int approxLines = Math.Max(1, (int)Math.Ceiling((double)charCount / Math.Max(5, (int)(availW / (optimalFontSize * 0.55)))));

            if (optimalFontSize < minFont) minFont = optimalFontSize;
            if (optimalFontSize > maxFont) maxFont = optimalFontSize;
            sumFontSize += optimalFontSize;

            nodeInfos.Add(new SmartArtNodeTypographyInfo(
                i + 1, title, charCount, wordCount, approxLines, Math.Round(optimalFontSize, 1), clippingRisk, requiresHyphenation));
        }

        double meanFont = Math.Round(sumFontSize / nodeCount, 1);
        if (minFont == double.MaxValue) minFont = 14.0;
        if (maxFont == double.MinValue) maxFont = 14.0;

        double legibilityScore = Math.Round(Math.Clamp(100.0 - (clippingRiskCount * 18.0) - (Math.Max(0.0, 12.0 - meanFont) * 5.0), 0.0, 100.0), 1);

        string category;
        if (clippingRiskCount == 0 && meanFont >= 13.0)
        {
            category = "Optimal Typographic Legibility";
        }
        else if (clippingRiskCount == 0)
        {
            category = "Readable (Compact Typography)";
        }
        else if (clippingRiskCount <= 2)
        {
            category = "Minor Text Overflow Risks";
        }
        else
        {
            category = "Severe Text Clipping & Compression Hazards";
        }

        string summary = FormatTypographyFontFitSummary(
            nodeCount, minFont, maxFont, meanFont, clippingRiskCount, legibilityScore, category);

        StatusMessage = summary;

        return new SmartArtTypographyFontFitAudit(
            nodeCount, canvasWidth, canvasHeight, minFont, maxFont, meanFont,
            clippingRiskCount, legibilityScore, nodeInfos, category, summary);
    }

    public static SmartArtTypographyFontFitAudit AuditLayoutTypographyAndFontFit(string? markdown, bool isStatic)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditLayoutTypographyAndFontFit(markdown);
    }

    public static string FormatTypographyFontFitSummary(
        int totalNodes, double minFont, double maxFont, double meanFont, int clippingCount,
        double legibilityScore, string category)
    {
        if (totalNodes == 0) return "Typography font-fit audit: 0 nodes · Outline is empty";

        string nodeDesc = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        string fontDesc = Math.Abs(minFont - maxFont) < 0.1
            ? $"{meanFont:0.#}pt"
            : $"{minFont:0.#}–{maxFont:0.#}pt (~{meanFont:0.#}pt avg)";
        string clipDesc = clippingCount > 0 ? $" · ⚠ {clippingCount} clipping risk{(clippingCount == 1 ? "" : "s")}" : " · 0 clipping risks";

        return $"Typography font-fit audit ({nodeDesc} [{fontDesc}] · {category} · {legibilityScore:0.#}% legibility){clipDesc}";
    }

    /// <summary>
    /// Audits SmartArt color hierarchy, depth gradient progression, and chromatic harmony across diagram nesting levels.
    /// Evaluates monotonic perceived luminance progression and WCAG contrast fidelity between adjacent parent/child tiers.
    /// </summary>
    public SmartArtColorHierarchyAudit AuditColorHierarchyAndGradients(string? markdown = null, IReadOnlyList<string>? paletteHexes = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "SmartArt color hierarchy audit: 0 nodes · Outline is empty";
            StatusMessage = emptySummary;
            return new SmartArtColorHierarchyAudit(
                0, 0, 1.0, 0, 100.0, true, "None",
                Array.Empty<SmartArtDepthLevelMetrics>(), Array.Empty<string>(), "Empty Outline", emptySummary);
        }

        var defaultPalette = new[] { "#005FB8", "#2E75D3", "#6095E6", "#94B6F0", "#C7D8F9" };
        var palette = (paletteHexes != null && paletteHexes.Count > 0) ? paletteHexes : defaultPalette;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var depthCounts = new Dictionary<int, int>();
        int totalNodes = 0;

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) continue;

            int indent = 0;
            while (indent < rawLine.Length && (rawLine[indent] == ' ' || rawLine[indent] == '\t'))
            {
                indent += (rawLine[indent] == '\t') ? 4 : 1;
            }

            int depth = indent / 2;
            depthCounts[depth] = depthCounts.TryGetValue(depth, out int count) ? count + 1 : 1;
            totalNodes++;
        }

        if (totalNodes == 0)
        {
            string emptySummary = "SmartArt color hierarchy audit: 0 nodes · Outline is empty";
            StatusMessage = emptySummary;
            return new SmartArtColorHierarchyAudit(
                0, 0, 1.0, 0, 100.0, true, "None",
                Array.Empty<SmartArtDepthLevelMetrics>(), Array.Empty<string>(), "Empty Outline", emptySummary);
        }

        int maxDepth = depthCounts.Keys.Max();
        var depthLevels = new List<SmartArtDepthLevelMetrics>();
        var issues = new List<string>();

        double prevLuminance = -1.0;
        int contrastDeficits = 0;
        double totalContrast = 0.0;
        int contrastEvaluations = 0;
        bool isMonotonic = true;
        int lastDirection = 0; // 1 = ascending, -1 = descending

        for (int d = 0; d <= maxDepth; d++)
        {
            int count = depthCounts.TryGetValue(d, out int c) ? c : 0;
            string hex = palette[d % palette.Count];
            double lum = CalculatePerceivedLuminance(hex);

            double contrastWithPrev;
            if (prevLuminance < 0.0)
            {
                // Root tier vs white canvas background (L=1.0)
                contrastWithPrev = Math.Round((1.0 + 0.05) / (lum + 0.05), 2);
            }
            else
            {
                double high = Math.Max(prevLuminance, lum);
                double low = Math.Min(prevLuminance, lum);
                contrastWithPrev = Math.Round((high + 0.05) / (low + 0.05), 2);

                int dir = lum > prevLuminance ? 1 : (lum < prevLuminance ? -1 : 0);
                if (lastDirection != 0 && dir != 0 && dir != lastDirection)
                {
                    isMonotonic = false;
                }
                if (dir != 0) lastDirection = dir;
            }

            totalContrast += contrastWithPrev;
            contrastEvaluations++;

            bool meetsAa = contrastWithPrev >= 3.0;
            bool meetsAaa = contrastWithPrev >= 4.5;

            if (d > 0 && contrastWithPrev < 1.25)
            {
                contrastDeficits++;
                issues.Add($"Tier {d} ({hex}) has low contrast ratio ({contrastWithPrev:0.##}:1) with Tier {d - 1}");
            }

            depthLevels.Add(new SmartArtDepthLevelMetrics(
                d, count, hex, Math.Round(lum, 4), contrastWithPrev, meetsAa, meetsAaa));

            prevLuminance = lum;
        }

        double meanContrast = contrastEvaluations > 0 ? Math.Round(totalContrast / contrastEvaluations, 2) : 1.0;
        string gradientDir = lastDirection > 0 ? "Ascending Luminance (Dark-to-Light)" : (lastDirection < 0 ? "Descending Luminance (Light-to-Dark)" : "Uniform Luminance");

        double harmonyScore = 100.0;
        harmonyScore -= contrastDeficits * 20.0;
        if (!isMonotonic && maxDepth >= 2) harmonyScore -= 15.0;
        harmonyScore = Math.Round(Math.Clamp(harmonyScore, 0.0, 100.0), 1);

        string category;
        if (contrastDeficits == 0 && isMonotonic)
            category = "Optimal Monotonic Depth Gradient (100%)";
        else if (harmonyScore >= 80.0)
            category = "Harmonious Depth Gradient (≥80%)";
        else if (harmonyScore >= 50.0)
            category = "Moderate Depth Contrast Fluctuations";
        else
            category = "Low Contrast / Inverted Depth Gradient";

        string summary = FormatColorHierarchySummary(
            totalNodes, maxDepth, depthLevels.Count, meanContrast, contrastDeficits,
            harmonyScore, isMonotonic, gradientDir, category);

        StatusMessage = summary;

        return new SmartArtColorHierarchyAudit(
            totalNodes, maxDepth, meanContrast, contrastDeficits, harmonyScore,
            isMonotonic, gradientDir, depthLevels, issues, category, summary);
    }

    public static SmartArtColorHierarchyAudit AuditColorHierarchyAndGradients(string? markdown, bool isStatic)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditColorHierarchyAndGradients(markdown);
    }

    public static string FormatColorHierarchySummary(
        int totalNodes, int maxDepth, int depthTiers, double meanContrast, int contrastDeficits,
        double harmonyScore, bool isMonotonic, string gradientDir, string category)
    {
        if (totalNodes == 0) return "SmartArt color hierarchy audit: 0 nodes · Outline is empty";

        string nodeDesc = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        string tierDesc = depthTiers == 1 ? "1 depth tier" : $"{depthTiers} depth tiers (max depth {maxDepth})";
        string deficitDesc = contrastDeficits > 0 ? $" · ⚠ {contrastDeficits} contrast deficit{(contrastDeficits == 1 ? "" : "s")}" : "";

        return $"SmartArt color hierarchy audit ({nodeDesc} [{tierDesc}] · {category} · {harmonyScore:0.#}% harmony): ~{meanContrast:0.#}:1 avg contrast · {gradientDir}{deficitDesc}";
    }

    private static double CalculatePerceivedLuminance(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0.5;
        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 3)
        {
            clean = $"{clean[0]}{clean[0]}{clean[1]}{clean[1]}{clean[2]}{clean[2]}";
        }
        if (clean.Length >= 6 &&
            int.TryParse(clean.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int r) &&
            int.TryParse(clean.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g) &&
            int.TryParse(clean.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            double rLin = LinearizeChannel(r / 255.0);
            double gLin = LinearizeChannel(g / 255.0);
            double bLin = LinearizeChannel(b / 255.0);
            return 0.2126 * rLin + 0.7152 * gLin + 0.0722 * bLin;
        }
        return 0.5;
    }

    private static double LinearizeChannel(double c)
    {
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Audits SmartArt chevron and linear sequential process diagrams for step pacing rhythm,
    /// text density variance, arrowhead aspect ratio consistency, and outlier steps.
    /// </summary>
    public LinearFlowChevronRhythmAudit AuditLinearFlowChevronRhythm(string? markdown = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Linear flow rhythm audit: 0 steps · Outline is empty";
            StatusMessage = emptySummary;
            return new LinearFlowChevronRhythmAudit(
                0, 0, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<LinearFlowStepInfo>(),
                "Empty Outline",
                emptySummary);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var stepList = new List<(string Title, int Words, int Chars)>();

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("<!--") || trimmed.StartsWith("```") || trimmed.StartsWith(":::")) continue;

            int leadingSpaces = 0;
            while (leadingSpaces < rawLine.Length && (rawLine[leadingSpaces] == ' ' || rawLine[leadingSpaces] == '\t'))
            {
                leadingSpaces += (rawLine[leadingSpaces] == '\t' ? 4 : 1);
            }
            if (leadingSpaces > 4) continue;

            string clean = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^([-*+]|\d+\.)\s*", "").Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;

            int words = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            stepList.Add((clean, words, clean.Length));
        }

        if (stepList.Count == 0)
        {
            string emptySummary = "Linear flow rhythm audit: 0 steps · Outline contains no process steps";
            StatusMessage = emptySummary;
            return new LinearFlowChevronRhythmAudit(
                0, 0, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<LinearFlowStepInfo>(),
                "Empty Outline",
                emptySummary);
        }

        int totalWords = stepList.Sum(s => s.Words);
        double meanWords = Math.Round((double)totalWords / stepList.Count, 1);

        double variance = stepList.Count > 1
            ? stepList.Select(s => (s.Words - meanWords) * (s.Words - meanWords)).Average()
            : 0.0;
        double stdDev = Math.Round(Math.Sqrt(variance), 1);
        double cv = Math.Round(stdDev / Math.Max(1.0, meanWords), 2);

        var steps = new List<LinearFlowStepInfo>();
        int outlierCount = 0;

        for (int i = 0; i < stepList.Count; i++)
        {
            var cur = stepList[i];
            double ratio = Math.Round((double)cur.Words / Math.Max(1.0, meanWords), 2);
            bool isOutlier = (ratio > 2.5) || (ratio < 0.25 && meanWords > 8.0);
            if (isOutlier) outlierCount++;

            steps.Add(new LinearFlowStepInfo(
                i + 1,
                cur.Title,
                cur.Words,
                cur.Chars,
                ratio,
                isOutlier));
        }

        double score = 100.0;
        score -= (cv * 35.0);
        score -= (outlierCount * 12.0);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category;
        if (cv < 0.35 && outlierCount == 0 && score >= 85.0)
        {
            category = "Harmonic Step Pacing & Balanced Rhythm";
        }
        else if (score >= 70.0 && outlierCount == 0)
        {
            category = "Consistent Linear Progression";
        }
        else if (outlierCount > 0)
        {
            category = "Pacing Imbalance / Wordy Steps Detected";
        }
        else
        {
            category = "Ragged Step Distribution";
        }

        string summary = FormatLinearFlowSummary(
            stepList.Count, totalWords, meanWords, cv, outlierCount, score, category);

        StatusMessage = summary;

        return new LinearFlowChevronRhythmAudit(
            stepList.Count,
            totalWords,
            meanWords,
            stdDev,
            cv,
            outlierCount,
            score,
            steps,
            category,
            summary);
    }

    public static LinearFlowChevronRhythmAudit AuditLinearFlowChevronRhythm(string? markdown, bool isStatic)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditLinearFlowChevronRhythm(markdown);
    }

    public static string FormatLinearFlowSummary(
        int totalSteps, int totalWords, double meanWords, double cv, int outliers,
        double score, string category)
    {
        if (totalSteps == 0) return "Linear flow rhythm audit: 0 steps · Outline is empty";

        string stepLabel = totalSteps == 1 ? "1 step" : $"{totalSteps} steps";
        string outlierAlert = outliers > 0 ? $" · ⚠ {outliers} pacing outlier{(outliers == 1 ? "" : "s")}" : " · Uniform step rhythm";

        return $"Linear flow rhythm audit ({stepLabel} [{totalWords} words, avg {meanWords:0.#}w/step] · {category} · {score:0.#}% score): Variation CV={cv:0.##}{outlierAlert}";
    }

    /// <summary>
    /// Audits SmartArt radial cycle and hub-and-spoke outline structures for harmonic angular sector spacing (360° / N),
    /// spoke orbital pacing, word count variance, and text weight balance.
    /// </summary>
    public SmartArtRadialCycleAudit AuditRadialCycleOrbitalBalance(string? markdown = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Radial cycle orbital balance audit: 0 spokes · Outline is empty";
            return new SmartArtRadialCycleAudit(
                HubTitle: "Empty Outline",
                TotalSpokes: 0,
                AngularSectorStepDegrees: 0.0,
                WordCountStdDev: 0.0,
                SpokeBalanceScore: 100.0,
                ImbalancedSpokeCount: 0,
                Spokes: Array.Empty<RadialSpokeInfo>(),
                Category: "Empty Outline",
                Summary: emptySummary);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        string hubTitle = "Central Hub";
        var spokes = new List<(string Title, int Words, int SubBullets)>();

        string? currentSpoke = null;
        int currentSpokeWords = 0;
        int currentSpokeSubBullets = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") || Regex.IsMatch(trimmed, @"^\d+\.\s");
            int indent = rawLine.TakeWhile(c => c == ' ' || c == '\t').Sum(c => c == '\t' ? 4 : 1);

            string content = Regex.Replace(trimmed, @"^[-*+#\d.]+\s*", "").Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            int wordCount = content.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

            if (trimmed.StartsWith("# ") || (indent == 0 && !isBullet && spokes.Count == 0 && currentSpoke == null))
            {
                hubTitle = content;
                continue;
            }

            if ((isBullet && indent <= 2) || (indent == 0 && isBullet))
            {
                if (currentSpoke != null)
                {
                    spokes.Add((currentSpoke, currentSpokeWords, currentSpokeSubBullets));
                }
                currentSpoke = content;
                currentSpokeWords = wordCount;
                currentSpokeSubBullets = 0;
            }
            else
            {
                if (currentSpoke != null)
                {
                    currentSpokeWords += wordCount;
                    currentSpokeSubBullets++;
                }
                else
                {
                    currentSpoke = content;
                    currentSpokeWords = wordCount;
                    currentSpokeSubBullets = 0;
                }
            }
        }

        if (currentSpoke != null)
        {
            spokes.Add((currentSpoke, currentSpokeWords, currentSpokeSubBullets));
        }

        int totalSpokes = spokes.Count;
        if (totalSpokes == 0)
        {
            string emptySummary = "Radial cycle orbital balance audit: 0 spokes · Outline contains no orbital nodes";
            return new SmartArtRadialCycleAudit(
                HubTitle: hubTitle,
                TotalSpokes: 0,
                AngularSectorStepDegrees: 0.0,
                WordCountStdDev: 0.0,
                SpokeBalanceScore: 100.0,
                ImbalancedSpokeCount: 0,
                Spokes: Array.Empty<RadialSpokeInfo>(),
                Category: "No Spokes Found",
                Summary: emptySummary);
        }

        double angleStep = totalSpokes > 0 ? (360.0 / totalSpokes) : 0.0;
        double meanWords = spokes.Average(s => s.Words);
        double variance = spokes.Select(s => Math.Pow(s.Words - meanWords, 2)).Average();
        double stdDev = Math.Sqrt(variance);
        double cv = meanWords > 0 ? (stdDev / meanWords) : 0.0;

        var evaluatedSpokes = new List<RadialSpokeInfo>();
        int imbalancedCount = 0;

        for (int i = 0; i < totalSpokes; i++)
        {
            var sp = spokes[i];
            double angle = i * angleStep;
            double weightRatio = meanWords > 0 ? (sp.Words / meanWords) : 1.0;
            bool isImbalanced = weightRatio > 2.0 || weightRatio < 0.35;
            if (isImbalanced) imbalancedCount++;

            evaluatedSpokes.Add(new RadialSpokeInfo(
                SpokeIndex: i + 1,
                Title: sp.Title,
                AngleDegrees: Math.Round(angle, 1),
                WordCount: sp.Words,
                SubBulletCount: sp.SubBullets,
                WeightRatio: Math.Round(weightRatio, 2),
                IsImbalanced: isImbalanced));
        }

        double score;
        if (totalSpokes < 3)
        {
            score = 65.0;
        }
        else if (totalSpokes > 10)
        {
            score = Math.Max(50.0, 95.0 - (totalSpokes - 10) * 8.0);
        }
        else
        {
            score = Math.Clamp(100.0 - (cv * 35.0) - (imbalancedCount * 12.0), 0.0, 100.0);
        }

        string category = score switch
        {
            >= 90.0 when totalSpokes >= 3 && totalSpokes <= 8 => "Harmonic Radial Orbital Cycle",
            >= 75.0 => "Balanced Hub-and-Spoke Distribution",
            >= 60.0 => totalSpokes < 3 ? "Degenerate Radial Cycle (< 3 Spokes)" : "Lopsided Spoke Text Weight",
            _ => totalSpokes > 10 ? "Crowded Orbital Ring (> 10 Spokes)" : "Severe Radial Weight Imbalance"
        };

        string summary = FormatRadialCycleSummary(
            hubTitle, totalSpokes, angleStep, stdDev, score, category, imbalancedCount);

        return new SmartArtRadialCycleAudit(
            HubTitle: hubTitle,
            TotalSpokes: totalSpokes,
            AngularSectorStepDegrees: Math.Round(angleStep, 1),
            WordCountStdDev: Math.Round(stdDev, 1),
            SpokeBalanceScore: Math.Round(score, 1),
            ImbalancedSpokeCount: imbalancedCount,
            Spokes: evaluatedSpokes,
            Category: category,
            Summary: summary);
    }

    public static SmartArtRadialCycleAudit AuditRadialCycleOrbitalBalance(string? markdown, bool isStatic)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditRadialCycleOrbitalBalance(markdown);
    }

    public static string FormatRadialCycleSummary(
        string hubTitle, int totalSpokes, double angleStep, double stdDev,
        double score, string category, int imbalancedCount)
    {
        if (totalSpokes == 0) return "Radial cycle orbital balance audit: 0 spokes · Outline is empty";

        string spokeLabel = totalSpokes == 1 ? "1 spoke" : $"{totalSpokes} spokes";
        string imbalanceAlert = imbalancedCount > 0 ? $" · ⚠ {imbalancedCount} lopsided spoke{(imbalancedCount == 1 ? "" : "s")}" : " · Harmonic angular pacing";

        return $"Radial cycle orbital balance audit (Hub: \"{hubTitle}\" · {spokeLabel} [{angleStep:0.#}° step] · {category} · {score:0.#}% score): Text std dev ±{stdDev:0.#}w{imbalanceAlert}";
    }

    /// <summary>
    /// Audits SmartArt pyramid and funnel diagrams for layer volume, height tapering,
    /// proportional mass balance, and apex overloading ("Inverted Mass Traps").
    /// </summary>
    public SmartArtPyramidTaperingAudit AuditPyramidTaperingAndMassBalance(string? markdown = null, bool isInverted = false)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Pyramid tapering audit: 0 tiers · Outline is empty";
            StatusMessage = emptySummary;
            return new SmartArtPyramidTaperingAudit(
                0, 0, isInverted, 0, 100.0, 1.0,
                Array.Empty<PyramidTierInfo>(), "Empty Outline", emptySummary);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var parsedTiers = new List<(string Title, int Words, int Chars, int SubBullets)>();

        string? currentTitle = null;
        int currentWords = 0;
        int currentChars = 0;
        int currentSubBullets = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::")) continue;

            int indent = rawLine.TakeWhile(c => c == ' ' || c == '\t').Sum(c => c == '\t' ? 4 : 1);
            bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") || Regex.IsMatch(trimmed, @"^\d+\.\s");
            string clean = Regex.Replace(trimmed, @"^[-*+#\d.]+\s*", "").Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;

            int words = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

            if ((isBullet && indent <= 2) || (indent == 0 && isBullet) || trimmed.StartsWith("# "))
            {
                if (currentTitle != null)
                {
                    parsedTiers.Add((currentTitle, currentWords, currentChars, currentSubBullets));
                }
                currentTitle = clean;
                currentWords = words;
                currentChars = clean.Length;
                currentSubBullets = 0;
            }
            else
            {
                if (currentTitle != null)
                {
                    currentWords += words;
                    currentChars += clean.Length;
                    currentSubBullets++;
                }
                else
                {
                    currentTitle = clean;
                    currentWords = words;
                    currentChars = clean.Length;
                    currentSubBullets = 0;
                }
            }
        }

        if (currentTitle != null)
        {
            parsedTiers.Add((currentTitle, currentWords, currentChars, currentSubBullets));
        }

        int totalTiers = parsedTiers.Count;
        if (totalTiers == 0)
        {
            string emptySummary = "Pyramid tapering audit: 0 tiers · No tiers found in outline";
            StatusMessage = emptySummary;
            return new SmartArtPyramidTaperingAudit(
                0, 0, isInverted, 0, 100.0, 1.0,
                Array.Empty<PyramidTierInfo>(), "Empty Outline", emptySummary);
        }

        int totalWords = Math.Max(1, parsedTiers.Sum(t => t.Words));
        double nSq = totalTiers * totalTiers;
        var evaluatedTiers = new List<PyramidTierInfo>();
        int trapCount = 0;
        double sumDensity = 0.0;

        for (int i = 0; i < totalTiers; i++)
        {
            var tier = parsedTiers[i];
            int tierRank = i + 1; // 1-based

            // Standard pyramid: Tier 1 is apex (smallest), Tier N is base (largest)
            // Inverted pyramid: Tier 1 is base (largest), Tier N is apex (smallest)
            double areaFraction = isInverted
                ? (2.0 * (totalTiers - tierRank + 1) - 1.0) / nSq
                : (2.0 * tierRank - 1.0) / nSq;

            double expectedWords = Math.Max(1.0, totalWords * areaFraction);
            double densityRatio = Math.Round(tier.Words / expectedWords, 2);
            sumDensity += densityRatio;

            bool isApexTier = isInverted ? (tierRank == totalTiers) : (tierRank == 1);
            bool isBaseTier = isInverted ? (tierRank == 1) : (tierRank == totalTiers);

            bool isTrap = false;
            if (isApexTier && totalTiers >= 2)
            {
                var oppositeTier = isInverted ? parsedTiers[0] : parsedTiers[^1];
                if ((densityRatio >= 2.0 && tier.Words >= 5) || (tier.Words > oppositeTier.Words && tier.Words >= 6))
                {
                    isTrap = true;
                }
            }
            else if (areaFraction <= 0.25 && densityRatio >= 2.0)
            {
                isTrap = true;
            }

            if (isTrap) trapCount++;

            string grade;
            if (isTrap)
            {
                grade = "Inverted Mass Trap (Apex Overloaded)";
            }
            else if (isBaseTier && densityRatio < 0.35 && tier.Words < 4)
            {
                grade = "Base Starved (Excess Gutter)";
            }
            else
            {
                grade = "Balanced Taper";
            }

            evaluatedTiers.Add(new PyramidTierInfo(
                TierIndex: tierRank,
                Title: tier.Title,
                WordCount: tier.Words,
                CharacterCount: tier.Chars,
                SubBulletCount: tier.SubBullets,
                GeometricRelativeArea: Math.Round(areaFraction * 100.0, 1),
                TextDensityRatio: densityRatio,
                HasInvertedMassTrap: isTrap,
                TaperGrade: grade));
        }

        double meanDensity = Math.Round(sumDensity / totalTiers, 2);
        double score = 100.0 - (trapCount * 28.0);
        if (totalTiers == 1) score = 60.0;
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 90.0 when trapCount == 0 => "Harmonic Proportional Mass Taper",
            >= 75.0 when trapCount == 0 => "Balanced Pyramid Stacking",
            >= 50.0 => "Moderate Text Mass Inversion",
            _ => "Severe Inverted Mass Trap"
        };

        string summary = FormatPyramidTaperingSummary(
            totalTiers, totalWords, isInverted, trapCount, score, category);

        StatusMessage = summary;

        return new SmartArtPyramidTaperingAudit(
            TotalTiers: totalTiers,
            TotalWords: totalWords,
            IsInverted: isInverted,
            InvertedMassTrapCount: trapCount,
            TaperSmoothnessScore: score,
            MeanDensityRatio: meanDensity,
            Tiers: evaluatedTiers,
            Category: category,
            Summary: summary);
    }

    public static SmartArtPyramidTaperingAudit AuditPyramidTaperingAndMassBalance(string? markdown, bool isInverted = false, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditPyramidTaperingAndMassBalance(markdown, isInverted);
    }

    public static string FormatPyramidTaperingSummary(
        int totalTiers, int totalWords, bool isInverted, int trapCount,
        double score, string category)
    {
        if (totalTiers == 0) return "Pyramid tapering audit: 0 tiers · Outline is empty";

        string tierLabel = totalTiers == 1 ? "1 tier" : $"{totalTiers} tiers";
        string modeLabel = isInverted ? "Inverted/Funnel" : "Standard Apex-Up";
        string trapAlert = trapCount > 0 ? $" · ⚠ {trapCount} inverted mass trap{(trapCount == 1 ? "" : "s")}" : " · Proportional mass aligned";

        return $"Pyramid & funnel tapering audit ({tierLabel} [{totalWords} words · {modeLabel}] · {category} · {score:0.#}% score){trapAlert}";
    }

    /// <summary>
    /// Audits SmartArt 2x2 matrix diagrams for 4-quadrant balance, axis label alignment,
    /// item distribution, word count symmetry, and empty/overloaded quadrant traps.
    /// </summary>
    public SmartArtMatrixQuadrantAudit AuditMatrixQuadrantSymmetry(string? markdown = null, string? layoutName = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.Alias ?? "matrix";

        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Matrix quadrant audit: 0 quadrants · Outline is empty";
            return new SmartArtMatrixQuadrantAudit(
                0, 0, 0, 0.0, 0.0, 0.0, 0.0, 100.0,
                Array.Empty<MatrixQuadrantItem>(),
                Array.Empty<string>(),
                "Empty Matrix", emptySummary);
        }

        var lines = text.Replace("\r", "").Split('\n');
        var parsedBuckets = new List<(string Header, List<string> Items)>();
        (string Header, List<string> Items)? currentBucket = null;

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            int leadingSpaces = rawLine.TakeWhile(c => c == ' ').Count();
            bool isRootBullet = (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")) && leadingSpaces <= 1;
            bool isHeading = trimmed.StartsWith("#");

            if (isRootBullet || isHeading)
            {
                if (currentBucket != null)
                {
                    parsedBuckets.Add(currentBucket.Value);
                }
                string headerText = trimmed.TrimStart('#', '-', '*', '+', ' ');
                currentBucket = (headerText, new List<string>());
            }
            else
            {
                string itemText = trimmed.TrimStart('-', '*', '+', ' ');
                if (currentBucket != null)
                {
                    currentBucket.Value.Items.Add(itemText);
                }
                else
                {
                    currentBucket = (itemText, new List<string>());
                }
            }
        }

        if (currentBucket != null)
        {
            parsedBuckets.Add(currentBucket.Value);
        }

        if (parsedBuckets.Count == 0)
        {
            string emptySummary = "Matrix quadrant audit: 0 quadrants · No outline items found";
            return new SmartArtMatrixQuadrantAudit(
                0, 0, 0, 0.0, 0.0, 0.0, 0.0, 100.0,
                Array.Empty<MatrixQuadrantItem>(),
                Array.Empty<string>(),
                "Empty Matrix", emptySummary);
        }

        string[] defaultQuadrantNames = new[]
        {
            "Quadrant 1 (Top-Left)",
            "Quadrant 2 (Top-Right)",
            "Quadrant 3 (Bottom-Left)",
            "Quadrant 4 (Bottom-Right)"
        };

        var quadrantItems = new List<MatrixQuadrantItem>();
        var balanceAlerts = new List<string>();
        int totalItemsAcrossAll = 0;
        int totalWordsAcrossAll = 0;

        var evaluatedBuckets = new (string Header, List<string> Items)[4];
        for (int i = 0; i < 4; i++)
        {
            if (i < parsedBuckets.Count)
            {
                evaluatedBuckets[i] = parsedBuckets[i];
                int itemsCount = parsedBuckets[i].Items.Count;
                int words = CountWords(parsedBuckets[i].Header) + parsedBuckets[i].Items.Sum(CountWords);
                totalItemsAcrossAll += itemsCount;
                totalWordsAcrossAll += words;
            }
            else
            {
                evaluatedBuckets[i] = (string.Empty, new List<string>());
            }
        }

        if (parsedBuckets.Count > 4)
        {
            balanceAlerts.Add($"Outline contains {parsedBuckets.Count} root nodes ({parsedBuckets.Count - 4} overflow beyond standard 2x2 grid)");
        }

        int emptyQuadrantsCount = 0;
        int overloadedQuadrantsCount = 0;

        for (int i = 0; i < 4; i++)
        {
            var (header, items) = evaluatedBuckets[i];
            string qName = defaultQuadrantNames[i];
            bool isEmpty = string.IsNullOrWhiteSpace(header) && items.Count == 0;
            if (isEmpty) emptyQuadrantsCount++;

            int itemCount = items.Count;
            int words = CountWords(header) + items.Sum(CountWords);
            int chars = header.Length + items.Sum(it => it.Length);
            double wordPct = totalWordsAcrossAll > 0 ? (double)words / totalWordsAcrossAll * 100.0 : 0.0;

            bool isOverloaded = (totalWordsAcrossAll > 10 && wordPct > 50.0) ||
                                (totalItemsAcrossAll >= 4 && itemCount > totalItemsAcrossAll * 0.50);
            if (isOverloaded)
            {
                overloadedQuadrantsCount++;
                balanceAlerts.Add($"{qName} '{header}' is overloaded ({wordPct:0.#}% of text volume)");
            }

            if (isEmpty && parsedBuckets.Count > 0)
            {
                balanceAlerts.Add($"{qName} is empty (lacks content)");
            }

            quadrantItems.Add(new MatrixQuadrantItem(
                QuadrantIndex: i + 1,
                QuadrantName: qName,
                Header: header,
                ItemCount: itemCount,
                WordCount: words,
                CharacterCount: chars,
                WordPercentage: Math.Round(wordPct, 1),
                IsEmpty: isEmpty,
                IsOverloaded: isOverloaded));
        }

        double meanItems = (double)totalItemsAcrossAll / 4.0;
        double meanWords = (double)totalWordsAcrossAll / 4.0;

        double sumItemSqDiff = quadrantItems.Sum(q => Math.Pow(q.ItemCount - meanItems, 2));
        double itemStdDev = Math.Round(Math.Sqrt(sumItemSqDiff / 4.0), 2);

        double sumWordSqDiff = quadrantItems.Sum(q => Math.Pow(q.WordCount - meanWords, 2));
        double wordStdDev = Math.Round(Math.Sqrt(sumWordSqDiff / 4.0), 2);

        double penalty = (emptyQuadrantsCount * 22.0) + (overloadedQuadrantsCount * 18.0);
        if (meanWords > 0)
        {
            double cvWords = wordStdDev / meanWords;
            penalty += Math.Min(30.0, cvWords * 15.0);
        }
        if (parsedBuckets.Count > 4)
        {
            penalty += (parsedBuckets.Count - 4) * 8.0;
        }

        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 when emptyQuadrantsCount == 0 && overloadedQuadrantsCount == 0 => "Harmonic 4-Quadrant Symmetry",
            >= 80.0 => "Balanced 2x2 Matrix with Minor Pacing Variance",
            >= 60.0 => "Asymmetric Quadrant Distribution (Unequal Density)",
            _ => "Critical Matrix Imbalance (Empty or Overloaded Quadrants)"
        };

        string summary = FormatMatrixQuadrantSummary(
            quadrantItems.Count(q => !q.IsEmpty), totalItemsAcrossAll, totalWordsAcrossAll,
            emptyQuadrantsCount, overloadedQuadrantsCount, score, category);

        return new SmartArtMatrixQuadrantAudit(
            TotalQuadrants: 4,
            TotalItems: totalItemsAcrossAll,
            TotalWords: totalWordsAcrossAll,
            MeanItemsPerQuadrant: Math.Round(meanItems, 1),
            MeanWordsPerQuadrant: Math.Round(meanWords, 1),
            ItemStandardDeviation: itemStdDev,
            WordStandardDeviation: wordStdDev,
            SymmetryBalanceScore: score,
            Quadrants: quadrantItems,
            BalanceAlerts: balanceAlerts,
            SymmetryCategory: category,
            Summary: summary);
    }

    public static SmartArtMatrixQuadrantAudit AuditMatrixQuadrantSymmetry(string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditMatrixQuadrantSymmetry(markdown, layoutName);
    }

    public static string FormatMatrixQuadrantSummary(
        int populatedQuadrants, int totalItems, int totalWords, int emptyCount, int overloadedCount,
        double score, string category)
    {
        if (populatedQuadrants == 0 && totalWords == 0) return "Matrix quadrant audit: 0 quadrants · Outline is empty";

        string quadLabel = $"{populatedQuadrants}/4 quadrants populated";
        var alerts = new List<string>();
        if (emptyCount > 0) alerts.Add($"⚠ {emptyCount} empty");
        if (overloadedCount > 0) alerts.Add($"⚠ {overloadedCount} overloaded");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 4-quadrant balance aligned";

        return $"2x2 Matrix symmetry audit ({quadLabel} [{totalItems} items, {totalWords} words] · {category} · {score:0.#}% symmetry){alertSuffix}";
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Audits SmartArt Venn diagram circular overlap chord geometry, 2-set and 3-set lens intersection areas,
    /// item density balance, and empty intersection traps.
    /// </summary>
    public SmartArtVennOverlapAudit AuditVennDiagramOverlapAndSetBalance(string? markdown = null, string? layoutName = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new SmartArtVennOverlapAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<VennSetRegionInfo>(),
                Array.Empty<string>(),
                "Empty Outline",
                "Venn diagram overlap audit: 0 items · Outline contains no set data");
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var sections = new List<(string Header, List<string> Items)>();
        string currentHeader = "";
        var currentItems = new List<string>();

        foreach (var line in lines)
        {
            int indent = line.TakeWhile(char.IsWhiteSpace).Count();
            string trimmed = line.Trim().TrimStart('-', '*', '+', '#').Trim();

            if (indent == 0 || (sections.Count == 0 && string.IsNullOrEmpty(currentHeader)))
            {
                if (!string.IsNullOrEmpty(currentHeader))
                {
                    sections.Add((currentHeader, currentItems));
                    currentItems = new List<string>();
                }
                currentHeader = trimmed;
            }
            else
            {
                currentItems.Add(trimmed);
            }
        }
        if (!string.IsNullOrEmpty(currentHeader))
        {
            sections.Add((currentHeader, currentItems));
        }

        static bool IsIntersectionHeader(string h)
        {
            string lower = h.ToLowerInvariant();
            return lower.Contains('&') || lower.Contains("both") || lower.Contains("intersection") ||
                   lower.Contains("all") || lower.Contains("shared") || lower.Contains("overlap");
        }

        var primarySets = sections.Where(s => !IsIntersectionHeader(s.Header)).ToList();
        var intersectionSections = sections.Where(s => IsIntersectionHeader(s.Header)).ToList();

        int primarySetCount = primarySets.Count >= 3 ? 3 : 2;
        int totalExpectedRegions = primarySetCount == 3 ? 7 : 3;

        double lensOverlapFraction = primarySetCount == 3 ? 0.08 : 0.285;

        var regionInfos = new List<VennSetRegionInfo>();
        var balanceAlerts = new List<string>();
        int populatedRegions = 0;
        int emptyTrapCount = 0;
        int overloadedCount = 0;
        int totalItemsCount = 0;

        foreach (var sec in sections)
        {
            bool isInter = IsIntersectionHeader(sec.Header);
            int itemCount = sec.Items.Count > 0 ? sec.Items.Count : 1;
            totalItemsCount += itemCount;

            double geomArea = isInter ? lensOverlapFraction : (1.0 - lensOverlapFraction);
            double density = Math.Round((double)itemCount / Math.Max(0.1, geomArea), 1);

            bool isEmptyTrap = isInter && (sec.Items.Count == 0 || (sec.Items.Count == 1 && string.IsNullOrWhiteSpace(sec.Items[0])));
            if (isEmptyTrap)
            {
                emptyTrapCount++;
                balanceAlerts.Add($"⚠ Empty intersection trap: '{sec.Header}' has no shared items");
            }
            else
            {
                populatedRegions++;
            }

            if (isInter && density > 15.0)
            {
                overloadedCount++;
                balanceAlerts.Add($"⚠ Overloaded intersection: '{sec.Header}' density {density} exceeds geometric lens capacity");
            }

            regionInfos.Add(new VennSetRegionInfo(
                Guid.NewGuid().ToString("N")[..6],
                sec.Header,
                isInter ? "Intersection Lens" : "Primary Set",
                itemCount,
                sec.Items,
                Math.Round(geomArea, 3),
                density,
                isEmptyTrap));
        }

        if (intersectionSections.Count == 0 && primarySets.Count >= 2)
        {
            emptyTrapCount++;
            balanceAlerts.Add("⚠ Missing intersection region: Sets are disjoint or overlap is unlabelled");
        }

        double baseScore = 100.0;
        if (emptyTrapCount > 0) baseScore -= (emptyTrapCount * 30.0);
        if (overloadedCount > 0) baseScore -= (overloadedCount * 15.0);
        double vennScore = Math.Round(Math.Clamp(baseScore, 0.0, 100.0), 1);

        string category;
        if (emptyTrapCount > 0) category = "Disjoint / Empty Intersection Traps";
        else if (overloadedCount > 0) category = "Overloaded Intersection Density";
        else if (primarySetCount == 3) category = "Balanced 3-Set Triadic Overlap";
        else category = "Balanced 2-Set Dual Overlap";

        string summary = FormatVennDiagramSummary(
            primarySetCount, sections.Count, totalItemsCount, emptyTrapCount,
            lensOverlapFraction, vennScore, category);

        return new SmartArtVennOverlapAudit(
            primarySetCount,
            totalExpectedRegions,
            populatedRegions,
            emptyTrapCount,
            totalItemsCount,
            lensOverlapFraction,
            vennScore,
            regionInfos,
            balanceAlerts,
            category,
            summary);
    }

    public static SmartArtVennOverlapAudit AuditVennDiagramOverlapAndSetBalance(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditVennDiagramOverlapAndSetBalance(markdown, layoutName);
    }

    public static string FormatVennDiagramSummary(
        int setCount, int definedRegions, int totalItems, int emptyTraps,
        double lensOverlapFraction, double score, string category)
    {
        if (definedRegions == 0) return "Venn diagram overlap audit: 0 regions · Outline is empty";

        string setLabel = $"{setCount}-set Venn ({definedRegions} regions, {totalItems} items)";
        string chordLabel = $"lens ~{lensOverlapFraction * 100.0:0.#}% area";

        var alerts = new List<string>();
        if (emptyTraps > 0) alerts.Add($"⚠ {emptyTraps} empty trap");
        if (alerts.Count == 0) alerts.Add("✓ set inclusion balanced");

        return $"Venn overlap & set inclusion audit ({setLabel} · {chordLabel} · {category} · {score:0.#}% balance) · {string.Join(", ", alerts)}";
    }

    public SmartArtProcessSequenceAudit AuditProcessSequencePacing(string? markdown = null, string? layoutName = null)
    {
        string text = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.Name ?? "Linear Process";

        if (string.IsNullOrWhiteSpace(text))
        {
            string emptySummary = "Process sequence pacing audit: 0 steps · Outline is empty";
            return new SmartArtProcessSequenceAudit(
                0, 0, 0.0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<ProcessSequenceStepInfo>(),
                Array.Empty<string>(),
                "Empty Outline", emptySummary);
        }

        var lines = text.Replace("\r", "").Split('\n');
        var stepLines = new List<(int Index, string Title, string Content)>();

        int currentIndex = 0;
        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith(":::")) continue;

            string itemText = trimmed;
            if (itemText.StartsWith("- ") || itemText.StartsWith("* ") || itemText.StartsWith("+ "))
            {
                itemText = itemText[2..].Trim();
            }
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(itemText, @"^\d+[\.\)]\s*(.*)$");
                if (match.Success)
                {
                    itemText = match.Groups[1].Value.Trim();
                }
                else if (itemText.StartsWith("#"))
                {
                    itemText = itemText.TrimStart('#').Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(itemText)) continue;

            currentIndex++;
            string title = itemText;
            string content = "";
            int colonIdx = itemText.IndexOf(':');
            if (colonIdx > 0 && colonIdx < itemText.Length - 1)
            {
                title = itemText[..colonIdx].Trim();
                content = itemText[(colonIdx + 1)..].Trim();
            }

            stepLines.Add((currentIndex, title, content));
        }

        int totalSteps = stepLines.Count;
        if (totalSteps == 0)
        {
            string emptySummary = "Process sequence pacing audit: 0 steps · No steps detected";
            return new SmartArtProcessSequenceAudit(
                0, 0, 0.0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<ProcessSequenceStepInfo>(),
                Array.Empty<string>(),
                "No Steps Found", emptySummary);
        }

        const double CanvasWidth = 800.0;
        const double ChevronHeight = 160.0;
        double stepWidth = Math.Max(40.0, (CanvasWidth - Math.Max(0, totalSteps - 1) * 12.0) / totalSteps);
        double estAspectRatio = Math.Round(stepWidth / ChevronHeight, 2);

        var stepWordCounts = new List<int>();

        foreach (var step in stepLines)
        {
            string full = string.IsNullOrWhiteSpace(step.Content) ? step.Title : $"{step.Title} {step.Content}";
            var words = full.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            stepWordCounts.Add(words.Length);
        }

        double meanWords = Math.Round(stepWordCounts.Average(), 1);
        double variance = stepWordCounts.Average(w => (w - meanWords) * (w - meanWords));
        double stdDev = Math.Round(Math.Sqrt(variance), 1);

        int overloadedCount = 0;
        int tooBriefCount = 0;
        var alerts = new List<string>();
        var stepInfos = new List<ProcessSequenceStepInfo>();

        for (int i = 0; i < stepLines.Count; i++)
        {
            var step = stepLines[i];
            int words = stepWordCounts[i];
            bool isOverloaded = (words >= 30) || (meanWords >= 4 && words > meanWords * 2.5);
            bool isTooBrief = (words <= 1 && meanWords >= 5);

            if (isOverloaded)
            {
                overloadedCount++;
                alerts.Add($"Step {step.Index} (\"{step.Title}\") is overloaded ({words} words)");
            }
            if (isTooBrief)
            {
                tooBriefCount++;
                alerts.Add($"Step {step.Index} (\"{step.Title}\") is too brief ({words} words)");
            }

            stepInfos.Add(new ProcessSequenceStepInfo(
                step.Index,
                step.Title,
                words,
                step.Title.Length + step.Content.Length,
                Math.Round(stepWidth, 1),
                estAspectRatio,
                isOverloaded,
                isTooBrief));
        }

        double stepCountFactor = totalSteps switch
        {
            >= 3 and <= 6 => 100.0,
            2 => 85.0,
            7 => 85.0,
            8 => 70.0,
            1 => 50.0,
            _ => Math.Max(30.0, 100.0 - (totalSteps - 6) * 12.0)
        };

        double aspectFactor = (estAspectRatio >= 1.5 && estAspectRatio <= 3.2) ? 100.0 :
                              (estAspectRatio >= 1.0 && estAspectRatio <= 4.0) ? 85.0 : 65.0;

        double varianceFactor = meanWords > 0
            ? Math.Clamp(100.0 - ((stdDev / meanWords) * 45.0), 30.0, 100.0)
            : 100.0;

        double pacingScore = Math.Round((stepCountFactor * 0.40) + (aspectFactor * 0.25) + (varianceFactor * 0.35) - (overloadedCount * 10.0), 1);
        pacingScore = Math.Clamp(pacingScore, 10.0, 100.0);

        string category;
        if (pacingScore >= 90.0 && overloadedCount == 0) category = "Optimal Linear Flow";
        else if (totalSteps > 7) category = "Crowded Process (>7 steps)";
        else if (overloadedCount > 0) category = "Uneven Text Density";
        else if (totalSteps < 3) category = "Trivial Process (<3 steps)";
        else category = "Moderate Process Pacing";

        int totalWords = stepWordCounts.Sum();
        string summary = FormatProcessSequenceSummary(
            totalSteps, totalWords, meanWords, stdDev, estAspectRatio,
            overloadedCount, tooBriefCount, pacingScore, category);

        return new SmartArtProcessSequenceAudit(
            totalSteps,
            totalWords,
            meanWords,
            stdDev,
            estAspectRatio,
            overloadedCount,
            tooBriefCount,
            pacingScore,
            stepInfos,
            alerts,
            category,
            summary);
    }

    public static SmartArtProcessSequenceAudit AuditProcessSequencePacing(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditProcessSequencePacing(markdown, layoutName);
    }

    public static string FormatProcessSequenceSummary(
        int totalSteps, int totalWords, double meanWords, double stdDev,
        double estAspectRatio, int overloadedCount, int tooBriefCount, double score, string category)
    {
        if (totalSteps == 0) return "Process sequence pacing audit: 0 steps · Outline is empty";

        string stepLabel = totalSteps == 1 ? "1 step" : $"{totalSteps} steps";
        string aspectLabel = $"aspect {estAspectRatio:0.##}:1";

        var alerts = new List<string>();
        if (overloadedCount > 0) alerts.Add($"⚠ {overloadedCount} overloaded step");
        if (tooBriefCount > 0) alerts.Add($"ℹ {tooBriefCount} brief step");
        if (alerts.Count == 0) alerts.Add("✓ uniform pacing");

        return $"Process sequence pacing audit ({stepLabel}, {totalWords} words · {meanWords:0.#} w/step, {aspectLabel} · {category} · {score:0.#}% balance) · {string.Join(", ", alerts)}";
    }

    /// <summary>
    /// Audits SmartArt hierarchy tree depth-to-breadth aspect ratio, branching factor uniformity,
    /// single-child bottleneck chains, and wide root fan layout ergonomics.
    /// </summary>
    public SmartArtHierarchyBranchingAudit AuditHierarchyAspectAndBranching(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtHierarchyBranchingAudit(
                0, 0, 0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<HierarchyLevelInfo>(),
                Array.Empty<string>(),
                "Empty Hierarchy",
                "Hierarchy tree balance audit: 0 nodes · Outline contains no items");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var parsedNodes = new List<(int Level, string Text, int ParentIndex)>();
        var parentStack = new List<(int Level, int NodeIndex)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent += line[indent] == '\t' ? 4 : 1;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
                continue;

            int level = indent / 2;
            string nodeText = trimmed;

            if (trimmed.StartsWith('#'))
            {
                int hashCount = trimmed.TakeWhile(c => c == '#').Count();
                level = Math.Max(0, hashCount - 1);
                nodeText = trimmed.TrimStart('#').Trim();
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                nodeText = trimmed.Substring(2).Trim();
            }
            else
            {
                var numMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
                if (numMatch.Success)
                {
                    nodeText = numMatch.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(nodeText)) continue;

            while (parentStack.Count > 0 && parentStack[parentStack.Count - 1].Level >= level)
            {
                parentStack.RemoveAt(parentStack.Count - 1);
            }

            int parentIdx = parentStack.Count > 0 ? parentStack[parentStack.Count - 1].NodeIndex : -1;
            int currentNodeIdx = parsedNodes.Count;
            parsedNodes.Add((level, nodeText, parentIdx));
            parentStack.Add((level, currentNodeIdx));
        }

        if (parsedNodes.Count == 0)
        {
            return new SmartArtHierarchyBranchingAudit(
                0, 0, 0, 0.0, 0.0, 0, 0, 100.0,
                Array.Empty<HierarchyLevelInfo>(),
                Array.Empty<string>(),
                "Empty Hierarchy",
                "Hierarchy tree balance audit: 0 nodes · Outline contains no items");
        }

        var levelGroups = parsedNodes.GroupBy(n => n.Level).OrderBy(g => g.Key).ToList();
        var levelDistribution = levelGroups.Select(g => new HierarchyLevelInfo(
            Level: g.Key,
            NodeCount: g.Count(),
            AvgTextLength: Math.Round(g.Average(n => n.Text.Length), 1))).ToList();

        int maxDepth = levelGroups.Max(g => g.Key);
        int maxBreadth = levelGroups.Max(g => g.Count());
        double ratio = Math.Round((double)maxDepth / Math.Max(1, maxBreadth), 2);

        var childCounts = new Dictionary<int, int>();
        for (int i = 0; i < parsedNodes.Count; i++)
        {
            int p = parsedNodes[i].ParentIndex;
            if (p >= 0)
            {
                childCounts[p] = childCounts.GetValueOrDefault(p, 0) + 1;
            }
        }

        int singleChildChains = childCounts.Values.Count(c => c == 1);
        int wideFanNodes = childCounts.Values.Count(c => c > 8);
        double avgBranching = childCounts.Count > 0 ? Math.Round((double)childCounts.Values.Sum() / childCounts.Count, 1) : 0.0;

        var alerts = new List<string>();
        if (wideFanNodes > 0) alerts.Add($"🚨 {wideFanNodes} wide fan (>8 children)");
        if (singleChildChains >= 2) alerts.Add($"⚠ {singleChildChains} linear single-child chains");
        if (maxDepth >= 5) alerts.Add($"⚠ Deep nesting ({maxDepth} levels)");

        double penalty = (wideFanNodes * 15.0) + (singleChildChains * 8.0) + (Math.Max(0, maxDepth - 4) * 10.0);
        if (ratio > 2.0) penalty += 15.0;
        if (maxBreadth > 12) penalty += 10.0;

        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category = score switch
        {
            >= 90.0 when wideFanNodes == 0 && singleChildChains == 0 => "Balanced Organizational Hierarchy",
            >= 75.0 => "Structured Tree with Minor Branching Asymmetry",
            >= 60.0 => "Wide Fan or Deep Chain Variance",
            _ => "Severe Hierarchy Imbalance"
        };

        string summary = FormatHierarchyBranchingSummary(
            parsedNodes.Count, maxDepth, maxBreadth, ratio, avgBranching,
            singleChildChains, wideFanNodes, score, category);

        return new SmartArtHierarchyBranchingAudit(
            TotalNodes: parsedNodes.Count,
            MaxDepth: maxDepth,
            MaxBreadth: maxBreadth,
            DepthToBreadthRatio: ratio,
            AvgBranchingFactor: avgBranching,
            SingleChildChains: singleChildChains,
            WideFanNodes: wideFanNodes,
            HierarchyBalanceScore: score,
            LevelDistribution: levelDistribution,
            StructuralAlerts: alerts,
            Category: category,
            Summary: summary);
    }

    public static SmartArtHierarchyBranchingAudit AuditHierarchyAspectAndBranching(string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditHierarchyAspectAndBranching(markdown, layoutName);
    }

    public static string FormatHierarchyBranchingSummary(
        int totalNodes, int maxDepth, int maxBreadth, double ratio, double avgBranching,
        int singleChains, int wideFans, double score, string category)
    {
        if (totalNodes == 0) return "Hierarchy tree balance audit: 0 nodes · Outline contains no items";

        string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        string dimLabel = $"{maxDepth} depth x {maxBreadth} breadth (ratio {ratio:0.##})";

        var alerts = new List<string>();
        if (wideFans > 0) alerts.Add($"🚨 {wideFans} wide fan");
        if (singleChains > 0) alerts.Add($"⚠ {singleChains} single-child");
        if (avgBranching > 0) alerts.Add($"avg {avgBranching:0.#} children/branch");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Balanced tree geometry";

        return $"Hierarchy tree balance audit ({nodeLabel} [{dimLabel}] · {category} · {score:0.#}% balance){alertSuffix}";
    }

    /// <summary>
    /// Audits SmartArt timeline milestone chronology, date monotonicity, milestone duration intervals,
    /// and out-of-order temporal event hazards across timeline and roadmap outlines.
    /// </summary>
    public SmartArtTimelineChronologyAudit AuditTimelineMilestoneChronologyAndSpacing(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtTimelineChronologyAudit(
                0, 0, 0, 0.0, 100.0,
                Array.Empty<TimelineMilestoneItem>(),
                Array.Empty<string>(),
                "Empty Timeline",
                "Timeline milestone chronology audit: 0 milestones · Outline is empty");
        }

        var lines = input.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var items = new List<TimelineMilestoneItem>();
        int index = 1;

        // Parse milestone items from outline
        foreach (var line in lines)
        {
            string clean = line.TrimStart('-', '*', '+', '#').Trim();
            if (clean.Length > 2 && char.IsDigit(clean[0]) && clean[1] == '.')
            {
                clean = clean.Substring(2).Trim();
            }

            if (string.IsNullOrWhiteSpace(clean)) continue;

            // Attempt date parsing
            double epoch = 0.0;
            bool hasDate = false;

            // Regex 1: ISO Date yyyy-MM-dd
            var mIso = System.Text.RegularExpressions.Regex.Match(clean, @"\b(?<y>19\d\d|20\d\d|21\d\d)[-/](?<m>\d{1,2})[-/](?<d>\d{1,2})\b");
            if (mIso.Success)
            {
                int y = int.Parse(mIso.Groups["y"].Value);
                int m = int.Parse(mIso.Groups["m"].Value);
                int d = int.Parse(mIso.Groups["d"].Value);
                epoch = (y - 2000) * 365.25 + m * 30.5 + d;
                hasDate = true;
            }
            else
            {
                // Regex 2: Quarters Q1 2026 or 2026 Q1
                var mQ = System.Text.RegularExpressions.Regex.Match(clean, @"\b(?:Q(?<q>[1-4])\s*(?<y>19\d\d|20\d\d|21\d\d)|(?<y>19\d\d|20\d\d|21\d\d)\s*Q(?<q>[1-4]))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mQ.Success)
                {
                    int y = int.Parse(mQ.Groups["y"].Value);
                    int q = int.Parse(mQ.Groups["q"].Value);
                    epoch = (y - 2000) * 365.25 + (q - 1) * 91.25;
                    hasDate = true;
                }
                else
                {
                    // Regex 3: Year-Month yyyy-MM
                    var mYm = System.Text.RegularExpressions.Regex.Match(clean, @"\b(?<y>19\d\d|20\d\d|21\d\d)[-/](?<m>\d{1,2})\b");
                    if (mYm.Success)
                    {
                        int y = int.Parse(mYm.Groups["y"].Value);
                        int m = int.Parse(mYm.Groups["m"].Value);
                        epoch = (y - 2000) * 365.25 + m * 30.5;
                        hasDate = true;
                    }
                    else
                    {
                        // Regex 4: Standalone Year 2026
                        var mY = System.Text.RegularExpressions.Regex.Match(clean, @"\b(?<y>19\d\d|20\d\d|21\d\d)\b");
                        if (mY.Success)
                        {
                            int y = int.Parse(mY.Groups["y"].Value);
                            epoch = (y - 2000) * 365.25;
                            hasDate = true;
                        }
                        else
                        {
                            // Regex 5: Sequential Phase / Sprint / Stage / Milestone
                            var mPhase = System.Text.RegularExpressions.Regex.Match(clean, @"\b(?:Phase|Sprint|Stage|Milestone|M)\s*(?<num>\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (mPhase.Success)
                            {
                                int p = int.Parse(mPhase.Groups["num"].Value);
                                epoch = 10000 + p * 30.0;
                                hasDate = true;
                            }
                        }
                    }
                }
            }

            items.Add(new TimelineMilestoneItem(
                index++,
                line,
                clean,
                epoch,
                hasDate,
                false,
                null));
        }

        if (items.Count == 0)
        {
            return new SmartArtTimelineChronologyAudit(
                0, 0, 0, 0.0, 100.0,
                Array.Empty<TimelineMilestoneItem>(),
                Array.Empty<string>(),
                "Empty Timeline",
                "Timeline milestone chronology audit: 0 milestones · Outline is empty");
        }

        // Check for chronological inversions
        int datedCount = items.Count(i => i.HasExplicitDate);
        int inversions = 0;
        var alerts = new List<string>();
        var updatedItems = new List<TimelineMilestoneItem>();

        double prevEpoch = double.MinValue;
        var intervals = new List<double>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool isOutOfOrder = false;
            string? alert = null;

            if (item.HasExplicitDate)
            {
                if (prevEpoch != double.MinValue)
                {
                    if (item.EpochScore < prevEpoch)
                    {
                        inversions++;
                        isOutOfOrder = true;
                        alert = $"Out of chronological order (earlier than preceding milestone)";
                        alerts.Add($"Milestone {item.Index} ('{item.ParsedLabel}') is out of order");
                    }
                    else
                    {
                        intervals.Add(item.EpochScore - prevEpoch);
                    }
                }
                prevEpoch = item.EpochScore;
            }

            updatedItems.Add(new TimelineMilestoneItem(
                item.Index,
                item.RawText,
                item.ParsedLabel,
                item.EpochScore,
                item.HasExplicitDate,
                isOutOfOrder,
                alert));
        }

        double meanInterval = intervals.Count > 0 ? Math.Round(intervals.Average(), 1) : 0.0;
        double penalty = inversions * 25.0;
        if (datedCount > 0 && datedCount < items.Count / 2) penalty += 15.0;
        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 when inversions == 0 => "Strict Monotonic Chronological Progression",
            >= 80.0 => "Chronologically Ordered Timeline with Undated Milestones",
            >= 60.0 => "Chronology Anomalies Detected (Out-of-Order Events)",
            _ => "Severely Inverted / Non-Chronological Milestones"
        };

        string summary = FormatTimelineChronologyAndSpacingSummary(
            items.Count, datedCount, inversions, meanInterval, score, category);

        return new SmartArtTimelineChronologyAudit(
            items.Count,
            datedCount,
            inversions,
            meanInterval,
            score,
            updatedItems,
            alerts,
            category,
            summary);
    }

    public static SmartArtTimelineChronologyAudit AuditTimelineMilestoneChronologyAndSpacing(string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditTimelineMilestoneChronologyAndSpacing(markdown, layoutName);
    }

    public static string FormatTimelineChronologyAndSpacingSummary(
        int totalMilestones, int datedMilestones, int inversions, double meanIntervalDays, double score, string category)
    {
        if (totalMilestones == 0) return "Timeline milestone chronology audit: 0 milestones · Outline is empty";

        string mLabel = totalMilestones == 1 ? "1 milestone" : $"{totalMilestones} milestones";
        string dateLabel = $"{datedMilestones}/{totalMilestones} dated";

        var alerts = new List<string>();
        if (inversions > 0) alerts.Add($"⚠ {inversions} chronological inversions");
        if (meanIntervalDays > 0) alerts.Add($"~{meanIntervalDays:0.#} days avg interval");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Monotonic timeline order";

        return $"Timeline milestone chronology audit ({mLabel} [{dateLabel}] · {category} · {score:0.#}% chronology){alertSuffix}";
    }

    /// <summary>
    /// Audits SmartArt segmented process cycle interval uniformity, radial angular chord balance (360/N),
    /// cognitive step count limits (3-7 optimal), and text brevity symmetry across cyclic process layouts.
    /// </summary>
    public SmartArtCycleIntervalAudit AuditCycleIntervalAndRadialChords(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtCycleIntervalAudit(
                0, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<CycleSegmentItem>(),
                "Empty Cycle",
                "Cycle process interval audit: 0 steps · Outline contains no steps");
        }

        var lines = input.Replace("\r", "").Split('\n');
        var stepRegex = new System.Text.RegularExpressions.Regex(@"^(\s*)(?:[-*+]|\d+\.)\s+(.*)$");

        var rawSteps = new List<string>();
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::")) continue;

            var match = stepRegex.Match(rawLine);
            if (match.Success)
            {
                int indent = match.Groups[1].Value.Length;
                if (indent == 0) // top-level cycle segment step
                {
                    rawSteps.Add(match.Groups[2].Value.Trim());
                }
            }
        }

        if (rawSteps.Count == 0)
        {
            return new SmartArtCycleIntervalAudit(
                0, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<CycleSegmentItem>(),
                "Empty Cycle",
                "Cycle process interval audit: 0 steps · No top-level cycle segments found");
        }

        int totalSteps = rawSteps.Count;
        double angularChord = Math.Round(360.0 / totalSteps, 1);

        var segments = new List<CycleSegmentItem>();
        var wordCounts = new List<int>();
        int overcrowdedCount = 0;

        for (int i = 0; i < rawSteps.Count; i++)
        {
            string title = rawSteps[i];
            var words = title.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int wc = words.Length;
            wordCounts.Add(wc);

            bool isBrevityOptimal = wc is >= 1 and <= 12;
            if (!isBrevityOptimal) overcrowdedCount++;

            segments.Add(new CycleSegmentItem(
                i + 1,
                title,
                wc,
                angularChord,
                isBrevityOptimal));
        }

        double meanWords = Math.Round(wordCounts.Average(), 1);
        double sumSq = wordCounts.Sum(w => Math.Pow(w - meanWords, 2));
        double wordStdDev = wordCounts.Count > 1
            ? Math.Round(Math.Sqrt(sumSq / (wordCounts.Count - 1)), 2)
            : 0.0;

        double penalty = 0.0;
        if (totalSteps < 3)
        {
            penalty += (3 - totalSteps) * 25.0; // Under-constrained cycle
        }
        else if (totalSteps > 7)
        {
            penalty += (totalSteps - 7) * 10.0; // Overcrowded cycle
        }

        penalty += Math.Min(30.0, wordStdDev * 3.0);
        if (overcrowdedCount > 0)
        {
            penalty += overcrowdedCount * 5.0;
        }

        double radialHarmonyScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (totalSteps < 3)
            category = "Degenerate Cycle (<3 Steps)";
        else if (totalSteps > 7)
            category = "Overcrowded Radial (>7 Steps)";
        else if (radialHarmonyScore >= 90.0)
            category = "Harmonic Radial Cycle (Optimal 3-7 Steps & Uniform Chords)";
        else
            category = "Unbalanced Cycle Pacing & Segment Word Density";

        string summary = FormatCycleIntervalSummary(
            totalSteps, angularChord, meanWords, wordStdDev, overcrowdedCount,
            radialHarmonyScore, category);

        return new SmartArtCycleIntervalAudit(
            totalSteps,
            angularChord,
            meanWords,
            wordStdDev,
            overcrowdedCount,
            radialHarmonyScore,
            segments,
            category,
            summary);
    }

    public static SmartArtCycleIntervalAudit AuditCycleIntervalAndRadialChords(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditCycleIntervalAndRadialChords(markdown, layoutName);
    }

    public static string FormatCycleIntervalSummary(
        int totalSteps, double angularChord, double meanWords, double wordStdDev, int overcrowdedCount,
        double score, string category)
    {
        if (totalSteps == 0) return "Cycle process interval audit: 0 steps · Outline contains no steps";

        string stepLabel = totalSteps == 1 ? "1 step" : $"{totalSteps} steps";
        var alerts = new List<string>();
        if (totalSteps < 3) alerts.Add("⚠ under-constrained (<3 steps)");
        if (totalSteps > 7) alerts.Add($"⚠ {totalSteps} steps (>7 overcrowded)");
        if (overcrowdedCount > 0) alerts.Add($"⚠ {overcrowdedCount} verbose steps (>12w)");
        alerts.Add($"~{meanWords:0.#}w/step (σ={wordStdDev:0.#})");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Uniform chord distribution";

        return $"Cycle process interval audit ({stepLabel} [{angularChord:0.#}° chord] · {category} · {score:0.#}% harmony){alertSuffix}";
    }

    public SmartArtQuadrantGridAudit AuditQuadrantGridBalance(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtQuadrantGridAudit(
                0, 0, 0, 4, 0.0, 100.0, 100.0,
                Array.Empty<QuadrantCellItem>(),
                "Empty Matrix",
                "2x2 matrix quadrant grid audit: 0 items · Outline contains no quadrant content");
        }

        var lines = input.Replace("\r", "").Split('\n');
        var parsedItems = new List<string>();

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) continue;

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                parsedItems.Add(trimmed[2..].Trim());
            }
            else if (trimmed.StartsWith("#"))
            {
                int hIdx = 0;
                while (hIdx < trimmed.Length && trimmed[hIdx] == '#') hIdx++;
                parsedItems.Add(trimmed[hIdx..].Trim());
            }
            else
            {
                parsedItems.Add(trimmed);
            }
        }

        if (parsedItems.Count == 0)
        {
            return new SmartArtQuadrantGridAudit(
                0, 0, 0, 4, 0.0, 100.0, 100.0,
                Array.Empty<QuadrantCellItem>(),
                "Empty Matrix",
                "2x2 matrix quadrant grid audit: 0 items · Outline contains no quadrant content");
        }

        string[] quadrantNames = { "Q1 Top-Left", "Q2 Top-Right", "Q3 Bottom-Left", "Q4 Bottom-Right" };
        var cellItems = new List<QuadrantCellItem>();

        int totalWords = 0;
        int populatedCount = 0;
        int emptyCount = 0;

        int[] qWords = new int[4];
        int[] qItemCounts = new int[4];

        for (int q = 0; q < 4; q++)
        {
            if (q < parsedItems.Count)
            {
                string text = parsedItems[q];
                int words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                qWords[q] = words;
                qItemCounts[q] = 1;
                totalWords += words;
                populatedCount++;

                bool isSparse = words < 2;
                bool isOvercrowded = words > 20;

                cellItems.Add(new QuadrantCellItem(
                    q + 1, quadrantNames[q], text, 1, words, isSparse, isOvercrowded));
            }
            else
            {
                emptyCount++;
                cellItems.Add(new QuadrantCellItem(
                    q + 1, quadrantNames[q], "(Empty Quadrant)", 0, 0, true, false));
            }
        }

        if (parsedItems.Count > 4)
        {
            for (int i = 4; i < parsedItems.Count; i++)
            {
                int qTarget = i % 4;
                string text = parsedItems[i];
                int words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                qWords[qTarget] += words;
                qItemCounts[qTarget]++;
                totalWords += words;
            }
        }

        double meanWords = Math.Round((double)totalWords / 4.0, 1);

        double sym1 = (qWords[0] == 0 && qWords[3] == 0) ? 1.0 :
            1.0 - (Math.Abs(qWords[0] - qWords[3]) / (double)Math.Max(1, qWords[0] + qWords[3]));
        double sym2 = (qWords[1] == 0 && qWords[2] == 0) ? 1.0 :
            1.0 - (Math.Abs(qWords[1] - qWords[2]) / (double)Math.Max(1, qWords[1] + qWords[2]));

        double diagonalSymmetryPct = Math.Round(Math.Clamp((sym1 + sym2) / 2.0 * 100.0, 0.0, 100.0), 1);

        double emptyPenalty = emptyCount * 20.0;
        double symmetryBonus = (diagonalSymmetryPct / 100.0) * 30.0;
        double balanceScore = Math.Round(Math.Clamp(70.0 - emptyPenalty + symmetryBonus, 10.0, 100.0), 1);

        string category;
        if (emptyCount == 0 && diagonalSymmetryPct >= 85.0)
            category = "Equilateral 2x2 Matrix Grid (High Diagonal Balance)";
        else if (emptyCount == 0)
            category = "Fully Populated 4-Quadrant Grid (Moderate Asymmetry)";
        else if (emptyCount == 1)
            category = "Asymmetric 3-Quadrant Grid (1 Empty Quadrant)";
        else
            category = $"Sparse Matrix ({emptyCount} Empty Quadrants Detected)";

        string summary = FormatQuadrantBalanceSummary(
            parsedItems.Count, totalWords, qWords[0], qWords[1], qWords[2], qWords[3],
            diagonalSymmetryPct, balanceScore, category);

        return new SmartArtQuadrantGridAudit(
            parsedItems.Count,
            totalWords,
            populatedCount,
            emptyCount,
            meanWords,
            diagonalSymmetryPct,
            balanceScore,
            cellItems,
            category,
            summary);
    }

    public static SmartArtQuadrantGridAudit AuditQuadrantGridBalance(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditQuadrantGridBalance(markdown, layoutName);
    }

    public static string FormatQuadrantBalanceSummary(
        int totalItems, int totalWords, int q1Words, int q2Words, int q3Words, int q4Words,
        double diagonalSymmetry, double score, string category)
    {
        if (totalItems == 0) return "2x2 matrix quadrant grid audit: 0 items · Outline contains no quadrant content";

        string itemLabel = totalItems == 1 ? "1 item" : $"{totalItems} items";
        return $"2x2 matrix quadrant grid audit ({itemLabel} [{totalWords} words] · {category} · {score:0.#}% score): " +
               $"Q1:{q1Words}w, Q2:{q2Words}w, Q3:{q3Words}w, Q4:{q4Words}w · {diagonalSymmetry:0.#}% diagonal symmetry";
    }

    public SmartArtFunnelPyramidAudit AuditFunnelPyramidProgression(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtFunnelPyramidAudit(
                0, 0, 0, 0.0, 0.0, true, 100.0,
                Array.Empty<FunnelPyramidStageItem>(),
                "Empty Funnel/Pyramid",
                "Funnel/Pyramid progression audit: 0 stages · Outline contains no funnel/pyramid content");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var stageData = new List<(string Title, List<string> Subitems)>();

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) continue;

            int leadingSpaces = rawLine.Length - rawLine.TrimStart().Length;
            bool isSubitem = leadingSpaces >= 2 && stageData.Count > 0;

            string clean = trimmed.TrimStart('-', '*', '+', '#', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');

            if (isSubitem)
            {
                stageData[^1].Subitems.Add(clean);
            }
            else
            {
                stageData.Add((clean, new List<string>()));
            }
        }

        if (stageData.Count == 0)
        {
            return new SmartArtFunnelPyramidAudit(
                0, 0, 0, 0.0, 0.0, true, 100.0,
                Array.Empty<FunnelPyramidStageItem>(),
                "Empty Funnel/Pyramid",
                "Funnel/Pyramid progression audit: 0 stages · Outline contains no funnel/pyramid content");
        }

        var stageWordCounts = new List<int>();
        var stageCharCounts = new List<int>();

        foreach (var stage in stageData)
        {
            string combinedText = stage.Title + " " + string.Join(" ", stage.Subitems);
            int words = combinedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            int chars = combinedText.Length;
            stageWordCounts.Add(words);
            stageCharCounts.Add(chars);
        }

        int totalStages = stageData.Count;
        int totalWords = stageWordCounts.Sum();
        double meanWords = stageWordCounts.Average();
        double variance = stageWordCounts.Select(w => (w - meanWords) * (w - meanWords)).Average();
        double stdDev = Math.Round(Math.Sqrt(variance), 1);

        int maxChars = stageCharCounts.Max();
        var stageItems = new List<FunnelPyramidStageItem>();
        int inversionCount = 0;

        for (int i = 0; i < totalStages; i++)
        {
            int w = stageWordCounts[i];
            int c = stageCharCounts[i];
            double relRatio = maxChars > 0 ? Math.Round((double)c / maxChars, 2) : 1.0;

            bool hasInversion = false;
            if (i > 0)
            {
                int prevW = stageWordCounts[i - 1];
                if (prevW > 0 && w > 0)
                {
                    double ratio = (double)w / prevW;
                    if (ratio > 2.5 || ratio < 0.4)
                    {
                        hasInversion = true;
                        inversionCount++;
                    }
                }
            }

            string status = hasInversion
                ? $"Abrupt Volume Inversion ({w}w vs adjacent {stageWordCounts[Math.Max(0, i - 1)]}w)"
                : $"Paced ({w} words)";

            stageItems.Add(new FunnelPyramidStageItem(
                i + 1, stageData[i].Title, w, c, stageData[i].Subitems.Count, relRatio, hasInversion, status));
        }

        double penalty = (inversionCount * 20.0);
        if (totalStages < 3 || totalStages > 7) penalty += 15.0;
        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (totalStages < 3)
            category = "Under-Utilized Stage Count (<3 stages)";
        else if (totalStages > 7)
            category = $"Overcrowded Stage Sprawl ({totalStages} stages)";
        else if (inversionCount > 0)
            category = $"Abrupt Volume Inversion ({inversionCount} stage jumps)";
        else
            category = "Smooth Monotonic Stage Taper";

        string summary = FormatFunnelPyramidSummary(
            totalStages, totalWords, inversionCount, meanWords, stdDev, score, category);

        return new SmartArtFunnelPyramidAudit(
            totalStages, totalWords, inversionCount, Math.Round(meanWords, 1),
            stdDev, inversionCount == 0, score, stageItems, category, summary);
    }

    public static SmartArtFunnelPyramidAudit AuditFunnelPyramidProgression(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditFunnelPyramidProgression(markdown, layoutName);
    }

    public static string FormatFunnelPyramidSummary(
        int totalStages, int totalWords, int inversions, double meanWords, double stdDev, double score, string category)
    {
        if (totalStages == 0) return "Funnel/Pyramid progression audit: 0 stages · Outline contains no funnel/pyramid content";

        string stageLabel = totalStages == 1 ? "1 stage" : $"{totalStages} stages";
        var alerts = new List<string>();
        if (inversions > 0) alerts.Add($"⚠ {inversions} volume {(inversions == 1 ? "inversion" : "inversions")}");
        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% smooth monotonic taper";

        return $"Funnel/Pyramid progression audit ({stageLabel} [{totalWords} words] · {category} · {score:0.#}% score): " +
               $"avg {meanWords:0.#}w (±{stdDev:0.#}w){alertSuffix}";
    }

    public SmartArtVennRadialAudit AuditVennRadialIntersectionBalance(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtVennRadialAudit(
                0, 0, 0, 0.0, 0.0, 100.0,
                Array.Empty<VennRadialSetItem>(),
                "Empty Venn/Radial Diagram",
                "Venn/Radial intersection balance audit: 0 sets · Outline contains no set data");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var setItems = new List<VennRadialSetItem>();
        int setIdx = 0;

        string currentTitle = "";
        int currentWords = 0;
        int currentChars = 0;
        int currentSubitems = 0;
        bool isCurrentIntersection = false;

        void CommitCurrent()
        {
            if (string.IsNullOrWhiteSpace(currentTitle)) return;
            setIdx++;
            string status = isCurrentIntersection
                ? $"Intersection ({currentWords} words, {currentSubitems} subitems)"
                : $"Primary Set ({currentWords} words, {currentSubitems} subitems)";

            setItems.Add(new VennRadialSetItem(
                setIdx, currentTitle, currentWords, currentChars, currentSubitems, isCurrentIntersection, status));

            currentTitle = "";
            currentWords = 0;
            currentChars = 0;
            currentSubitems = 0;
            isCurrentIntersection = false;
        }

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("```") || trimmed.StartsWith(":::")) continue;

            int indent = rawLine.TakeWhile(char.IsWhiteSpace).Count();

            // Detect top-level bullet: * Item or - Item or 1. Item
            if (indent < 2 && (trimmed.StartsWith("* ") || trimmed.StartsWith("- ") || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s+")))
            {
                CommitCurrent();
                string title = trimmed.Substring(trimmed.IndexOf(' ') + 1).Trim();
                currentTitle = title;
                int words = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                currentWords += words;
                currentChars += title.Length;

                if (title.Contains("∩", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("intersection", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("shared", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("overlap", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("both", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("common", StringComparison.OrdinalIgnoreCase))
                {
                    isCurrentIntersection = true;
                }
            }
            else if (indent >= 2 && (trimmed.StartsWith("* ") || trimmed.StartsWith("- ")))
            {
                // Sub-bullet
                string subText = trimmed.Substring(2).Trim();
                int words = subText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                currentWords += words;
                currentChars += subText.Length;
                currentSubitems++;

                if (subText.Contains("∩", StringComparison.OrdinalIgnoreCase) ||
                    subText.Contains("intersection", StringComparison.OrdinalIgnoreCase) ||
                    subText.Contains("shared", StringComparison.OrdinalIgnoreCase))
                {
                    isCurrentIntersection = true;
                }
            }
        }
        CommitCurrent();

        if (setItems.Count == 0)
        {
            return new SmartArtVennRadialAudit(
                0, 0, 0, 0.0, 0.0, 100.0,
                Array.Empty<VennRadialSetItem>(),
                "Empty Venn/Radial Diagram",
                "Venn/Radial intersection balance audit: 0 sets · Outline contains no set items");
        }

        int primarySetCount = setItems.Count(s => !s.IsIntersection);
        int intersectionCount = setItems.Count(s => s.IsIntersection);
        int totalWords = setItems.Sum(s => s.WordCount);

        var primarySets = setItems.Where(s => !s.IsIntersection).ToList();
        double meanWords = primarySets.Count > 0 ? primarySets.Average(s => s.WordCount) : 0.0;
        double variance = primarySets.Count > 1
            ? primarySets.Sum(s => Math.Pow(s.WordCount - meanWords, 2)) / (primarySets.Count - 1)
            : 0.0;
        double stdDev = Math.Round(Math.Sqrt(variance), 1);
        double cv = meanWords > 0 ? stdDev / meanWords : 0.0;

        // Balance scoring:
        double baseScore = 100.0 * Math.Max(0.0, 1.0 - Math.Min(1.0, cv));
        if (primarySetCount >= 2 && intersectionCount == 0 && (layoutName != null && layoutName.Contains("venn", StringComparison.OrdinalIgnoreCase)))
        {
            baseScore -= 25.0; // Penalty for missing intersection in a Venn layout
        }
        double balanceScore = Math.Round(Math.Clamp(baseScore, 10.0, 100.0), 1);

        string category;
        if (primarySetCount <= 1)
        {
            category = "Single Set / Non-Venn Outline";
        }
        else if (balanceScore >= 85.0)
        {
            category = intersectionCount > 0 ? "Harmonious Venn/Radial Overlap Balance" : "Balanced Radial Hub Distribution";
        }
        else if (balanceScore >= 60.0)
        {
            category = "Moderate Overlap Symmetry";
        }
        else
        {
            category = "Skewed / Asymmetric Set Word Volume";
        }

        string summary = FormatVennRadialBalanceSummary(
            primarySetCount, intersectionCount, totalWords, meanWords, stdDev, balanceScore, category);

        return new SmartArtVennRadialAudit(
            primarySetCount, intersectionCount, totalWords, meanWords, stdDev, balanceScore, setItems, category, summary);
    }

    public static SmartArtVennRadialAudit AuditVennRadialIntersectionBalance(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditVennRadialIntersectionBalance(markdown, layoutName);
    }

    public static string FormatVennRadialBalanceSummary(
        int totalSets, int intersections, int totalWords, double meanWords, double stdDev, double score, string category)
    {
        if (totalSets == 0)
            return "Venn/Radial intersection balance audit: 0 sets · Outline contains no set data";

        string setLabel = totalSets == 1 ? "1 primary set" : $"{totalSets} primary sets";
        var alerts = new List<string>();
        if (intersections > 0) alerts.Add($"{intersections} {(intersections == 1 ? "intersection" : "intersections")}");
        else alerts.Add("0 intersections");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
        return $"Venn/Radial intersection balance audit ({setLabel} [{totalWords} words] · {category} · {score:0.#}% score): " +
               $"avg {meanWords:0.#}w (±{stdDev:0.#}w){details}";
    }

    /// <summary>
    /// Audits SmartArt list items for text volume density, word count standard deviation, line counts, and balance.
    /// </summary>
    public SmartArtListTextDensityAudit AuditListTextDensityBalance(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtListTextDensityAudit(
                0, 0, 0, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<ListItemDensityInfo>(),
                "Empty Outline",
                "List item text density balance audit: 0 list items · Outline contains no items");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var items = new List<ListItemDensityInfo>();
        int itemIndex = 0;

        string currentTitle = "";
        int currentWords = 0;
        int currentChars = 0;
        int currentLines = 0;
        int currentSubitems = 0;

        void CommitCurrent()
        {
            if (string.IsNullOrWhiteSpace(currentTitle)) return;
            itemIndex++;
            items.Add(new ListItemDensityInfo(
                itemIndex,
                currentTitle,
                currentWords,
                currentChars,
                Math.Max(1, currentLines),
                currentSubitems,
                false,
                "Normal"));
            currentTitle = "";
            currentWords = 0;
            currentChars = 0;
            currentLines = 0;
            currentSubitems = 0;
        }

        foreach (var rawLine in lines)
        {
            string line = rawLine;
            if (string.IsNullOrWhiteSpace(line)) continue;

            string trimmed = line.Trim();
            int leadingSpaces = line.Length - line.TrimStart().Length;
            bool isTopLevel = leadingSpaces < 2 &&
                (trimmed.StartsWith("* ") || trimmed.StartsWith("- ") || trimmed.StartsWith("+ ") ||
                 System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s") ||
                 trimmed.StartsWith("#"));

            if (isTopLevel)
            {
                CommitCurrent();
                string title = trimmed;
                if (title.StartsWith("#"))
                {
                    title = title.TrimStart('#').Trim();
                }
                else if (title.StartsWith("* ") || title.StartsWith("- ") || title.StartsWith("+ "))
                {
                    title = title[2..].Trim();
                }
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(title, @"^\d+[\.\)]\s*(.*)$");
                    if (m.Success) title = m.Groups[1].Value.Trim();
                }

                currentTitle = title;
                int words = string.IsNullOrWhiteSpace(title) ? 0 : title.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                currentWords = words;
                currentChars = title.Length;
                currentLines = 1;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(currentTitle))
                {
                    currentTitle = trimmed;
                    int words = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    currentWords = words;
                    currentChars = trimmed.Length;
                    currentLines = 1;
                }
                else
                {
                    currentSubitems++;
                    currentLines++;
                    int words = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    currentWords += words;
                    currentChars += trimmed.Length;
                }
            }
        }
        CommitCurrent();

        if (items.Count == 0)
        {
            return new SmartArtListTextDensityAudit(
                0, 0, 0, 0.0, 0.0, 0.0, 0, 100.0,
                Array.Empty<ListItemDensityInfo>(),
                "Empty Outline",
                "List item text density balance audit: 0 list items · Outline contains no items");
        }

        int totalWords = items.Sum(i => i.WordCount);
        int totalChars = items.Sum(i => i.CharacterCount);
        double meanWords = Math.Round((double)totalWords / items.Count, 1);
        double meanLines = Math.Round((double)items.Sum(i => i.LineCount) / items.Count, 1);

        double sumSquares = items.Sum(i => Math.Pow(i.WordCount - meanWords, 2));
        double stdDev = Math.Round(Math.Sqrt(sumSquares / items.Count), 1);

        var finalItems = new List<ListItemDensityInfo>();
        int outlierCount = 0;
        foreach (var it in items)
        {
            bool isOutlier = (meanWords >= 4.0 && it.WordCount > 2.2 * meanWords) ||
                             (meanWords >= 8.0 && it.WordCount < 0.3 * meanWords);
            if (isOutlier) outlierCount++;

            string status = isOutlier
                ? (it.WordCount > meanWords ? "⚠ Verbose / Density Outlier" : "⚠ Sparse / Underweight Item")
                : "✓ Balanced Density";

            finalItems.Add(it with { IsDensityOutlier = isOutlier, Status = status });
        }

        double cv = meanWords > 0.0 ? (stdDev / meanWords) : 0.0;
        double penalty = (cv * 35.0) + (outlierCount * 15.0);
        double score = Math.Round(Math.Clamp(100.0 - penalty, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 90.0 => "Balanced Harmonious List Items",
            >= 75.0 => "Good Readability Balance",
            >= 50.0 => "Moderate Density Variance / Length Skew",
            _ => "Severe Text Density Asymmetry / Outlier Items"
        };

        string summary = FormatListTextDensitySummary(
            items.Count, totalWords, totalChars, meanWords, stdDev, meanLines, outlierCount, score, category);

        return new SmartArtListTextDensityAudit(
            items.Count, totalWords, totalChars, meanWords, stdDev, meanLines, outlierCount, score,
            finalItems, category, summary);
    }

    public static SmartArtListTextDensityAudit AuditListTextDensityBalance(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditListTextDensityBalance(markdown, layoutName);
    }

    public static string FormatListTextDensitySummary(
        int totalItems, int totalWords, int totalChars, double meanWords, double stdDev,
        double meanLines, int outliers, double score, string category)
    {
        if (totalItems == 0)
            return "List item text density balance audit: 0 list items · Outline contains no items";

        string itemStr = totalItems == 1 ? "1 list item" : $"{totalItems} list items";
        var alerts = new List<string>();
        if (outliers > 0) alerts.Add($"⚠ {outliers} density {(outliers == 1 ? "outlier" : "outliers")}");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% harmonious density";
        return $"List item text density balance audit ({itemStr} [{totalWords} words] · {category} · {score:0.#}% score): " +
               $"avg {meanWords:0.#}w (±{stdDev:0.#}w), avg {meanLines:0.#} lines{details}";
    }

    /// <summary>
    /// Audits SmartArt process and workflow steps for sequential progression, transition cues (Next, Then, Finally),
    /// title length variance, and transition harmony.
    /// </summary>
    public SmartArtProcessStepFlowAudit AuditProcessStepFlowAndTransitions(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtProcessStepFlowAudit(
                0, 0, 0, 0, 0.0, 0.0, 100.0,
                Array.Empty<ProcessStepTransitionItem>(), "Empty Outline",
                FormatProcessStepFlowSummary(0, 0, 0, 0, 0.0, 0.0, 100.0, "Empty Outline"));
        }

        var lines = input.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var rawSteps = new List<(string Title, List<string> Sublines)>();
        (string Title, List<string> Sublines)? currentStep = null;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::")) continue;

            bool isTopItem = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") ||
                             System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s+");

            if (isTopItem)
            {
                if (currentStep != null) rawSteps.Add(currentStep.Value);
                string cleanTitle = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^([-*+]|\d+[\.\)])\s+", "").Trim();
                currentStep = (cleanTitle, new List<string>());
            }
            else
            {
                if (currentStep != null)
                {
                    currentStep.Value.Sublines.Add(trimmed.TrimStart('-', '*', '+', ' '));
                }
                else
                {
                    currentStep = (trimmed, new List<string>());
                }
            }
        }
        if (currentStep != null) rawSteps.Add(currentStep.Value);

        if (rawSteps.Count == 0)
        {
            return new SmartArtProcessStepFlowAudit(
                0, 0, 0, 0, 0.0, 0.0, 100.0,
                Array.Empty<ProcessStepTransitionItem>(), "No Steps Detected",
                FormatProcessStepFlowSummary(0, 0, 0, 0, 0.0, 0.0, 100.0, "No Steps Detected"));
        }

        string[] transitionCues = new[] { "First", "Next", "Then", "After", "Subsequently", "Following", "Finally", "Lastly", "Step", "Phase", "Stage" };

        var items = new List<ProcessStepTransitionItem>();
        int cueCount = 0;
        int numCount = 0;
        var titleLengths = new List<int>();

        for (int i = 0; i < rawSteps.Count; i++)
        {
            var s = rawSteps[i];
            var titleWords = s.Title.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int titleWc = titleWords.Length;
            titleLengths.Add(titleWc);

            int descWc = s.Sublines.Sum(sub => sub.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length);

            bool hasCue = false;
            string matchedCue = "";
            foreach (var cue in transitionCues)
            {
                if (s.Title.StartsWith(cue, StringComparison.OrdinalIgnoreCase))
                {
                    hasCue = true;
                    matchedCue = cue;
                    break;
                }
            }
            if (hasCue) cueCount++;

            bool hasSeqNum = System.Text.RegularExpressions.Regex.IsMatch(s.Title, @"^(Step\s+\d+|Phase\s+\d+|\d+[\.:\)])", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                             (i + 1).ToString() == s.Title.Split(' ', '.')[0];
            if (hasSeqNum) numCount++;

            string directionality = i == 0 ? "Initial / Entry" :
                                    i == rawSteps.Count - 1 ? "Terminal / Exit" :
                                    "Forward Transition";

            string status = hasCue ? $"Transition Cue ({matchedCue})" :
                            hasSeqNum ? "Sequential Step" :
                            "Standard Step";

            items.Add(new ProcessStepTransitionItem(
                i + 1, s.Title, titleWc, descWc, hasCue, matchedCue, hasSeqNum, directionality, status));
        }

        double avgTitleWc = titleLengths.Average();
        double variance = titleLengths.Sum(l => Math.Pow(l - avgTitleWc, 2)) / titleLengths.Count;
        double stdDev = Math.Round(Math.Sqrt(variance), 2);

        int outlierSteps = items.Count(item => Math.Abs(item.TitleWordCount - avgTitleWc) > Math.Max(3.0, 1.8 * stdDev));

        double lengthConsistency = Math.Clamp(100.0 - (stdDev * 10.0) - (outlierSteps * 8.0), 0.0, 100.0);
        double flowSignaling = rawSteps.Count > 1
            ? Math.Clamp(50.0 + ((cueCount + numCount) * 25.0 / rawSteps.Count), 50.0, 100.0)
            : 100.0;
        double harmonyScore = Math.Round((lengthConsistency * 0.6) + (flowSignaling * 0.4), 1);

        string category = harmonyScore switch
        {
            >= 90.0 => "Seamless Forward Flow",
            >= 75.0 => "Balanced Sequential Process",
            >= 60.0 => "Moderate Step Length Variance",
            >= 40.0 => "Irregular Transition Pacing",
            _ => "Asymmetric Flow Disparity"
        };

        string summary = FormatProcessStepFlowSummary(
            rawSteps.Count, cueCount, numCount, outlierSteps, Math.Round(avgTitleWc, 1),
            stdDev, harmonyScore, category);

        return new SmartArtProcessStepFlowAudit(
            rawSteps.Count, cueCount, numCount, outlierSteps, Math.Round(avgTitleWc, 1),
            stdDev, harmonyScore, items, category, summary);
    }

    public static SmartArtProcessStepFlowAudit AuditProcessStepFlowAndTransitions(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditProcessStepFlowAndTransitions(markdown, layoutName);
    }

    public static string FormatProcessStepFlowSummary(
        int totalSteps, int cueCount, int numCount, int outliers, double avgTitleWc,
        double stdDev, double score, string category)
    {
        if (totalSteps == 0)
            return "Process step flow & transition audit: 0 steps · Outline contains no steps";

        string stepStr = totalSteps == 1 ? "1 step" : $"{totalSteps} steps";
        var cues = new List<string>();
        if (numCount > 0) cues.Add($"{numCount} numbered");
        if (cueCount > 0) cues.Add($"{cueCount} with transition cues");
        if (outliers > 0) cues.Add($"⚠ {outliers} title length {(outliers == 1 ? "outlier" : "outliers")}");

        string details = cues.Count > 0 ? $" · {string.Join(", ", cues)}" : " · sequential flow";
        return $"Process step flow & transition audit ({stepStr} · {category} · {score:0.#}% flow score): avg title {avgTitleWc:0.#}w (±{stdDev:0.#}w){details}";
    }

    /// <summary>
    /// Audits SmartArt outline hierarchy indentation levels, parent-child sibling balance,
    /// branching factor variance, and deep tree nesting ergonomics.
    /// </summary>
    public SmartArtHierarchyBalanceAudit AuditHierarchyIndentationAndChildBalance(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtHierarchyBalanceAudit(
                0, 0, 0.0, 0, 0, 0.0, 100.0,
                Array.Empty<HierarchyNodeBalanceItem>(),
                "Empty Outline",
                "Hierarchy tree balance audit: 0 nodes · Outline contains no items");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var parsedNodes = new List<(int LineNumber, int Level, string Text, int ParentIndex)>();
        var parentStack = new List<(int Level, int NodeIndex)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent += line[indent] == '\t' ? 4 : 1;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
                continue;

            int level = indent / 2;
            string nodeText = trimmed;

            if (trimmed.StartsWith('#'))
            {
                int hashCount = trimmed.TakeWhile(c => c == '#').Count();
                level = Math.Max(0, hashCount - 1);
                nodeText = trimmed.TrimStart('#').Trim();
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                nodeText = trimmed.Substring(2).Trim();
            }
            else
            {
                var numMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
                if (numMatch.Success)
                {
                    nodeText = numMatch.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(nodeText)) continue;

            while (parentStack.Count > 0 && parentStack[parentStack.Count - 1].Level >= level)
            {
                parentStack.RemoveAt(parentStack.Count - 1);
            }

            int parentIdx = parentStack.Count > 0 ? parentStack[parentStack.Count - 1].NodeIndex : -1;
            int currentNodeIdx = parsedNodes.Count;
            parsedNodes.Add((i + 1, level, nodeText, parentIdx));
            parentStack.Add((level, currentNodeIdx));
        }

        if (parsedNodes.Count == 0)
        {
            return new SmartArtHierarchyBalanceAudit(
                0, 0, 0.0, 0, 0, 0.0, 100.0,
                Array.Empty<HierarchyNodeBalanceItem>(),
                "Empty Outline",
                "Hierarchy tree balance audit: 0 nodes · Outline contains no items");
        }

        int totalNodes = parsedNodes.Count;
        int maxDepth = parsedNodes.Max(n => n.Level);

        var childCounts = new Dictionary<int, int>();
        for (int i = 0; i < parsedNodes.Count; i++)
        {
            int p = parsedNodes[i].ParentIndex;
            if (p >= 0)
            {
                childCounts[p] = childCounts.GetValueOrDefault(p, 0) + 1;
            }
        }

        var branchingFactors = childCounts.Values.ToList();
        double avgBranching = branchingFactors.Count > 0 ? Math.Round(branchingFactors.Average(), 2) : 0.0;
        double branchingVariance = branchingFactors.Count > 1
            ? Math.Round(branchingFactors.Select(b => Math.Pow(b - avgBranching, 2)).Average(), 2)
            : 0.0;

        int lopsidedParents = 0;
        int deeplyNestedCount = 0;
        var items = new List<HierarchyNodeBalanceItem>();

        for (int i = 0; i < parsedNodes.Count; i++)
        {
            var node = parsedNodes[i];
            int children = childCounts.GetValueOrDefault(i, 0);

            bool isLopsided = children > 6 || (children == 1 && avgBranching > 3.0);
            if (isLopsided) lopsidedParents++;

            bool isDeep = node.Level > 3;
            if (isDeep) deeplyNestedCount++;

            string status = isLopsided ? $"⚠ Lopsided branch ({children} children)" :
                            isDeep ? $"⚠ Deeply nested (level {node.Level})" :
                            children > 0 ? $"✓ {children} children" : "✓ Leaf item";

            items.Add(new HierarchyNodeBalanceItem(
                node.LineNumber, node.Text, node.Level, children, isLopsided, isDeep, status));
        }

        double penalty = (lopsidedParents * 12.0) + (deeplyNestedCount * 8.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Balanced Hierarchy",
            >= 75.0 => "Consistent Tree",
            >= 60.0 => "Moderate Depth Skew",
            >= 40.0 => "Lopsided Branching",
            _ => "Deeply Nested Outlier"
        };

        string summary = FormatHierarchyBalanceSummary(
            totalNodes, maxDepth, avgBranching, lopsidedParents, deeplyNestedCount,
            branchingVariance, score, category);

        StatusMessage = summary;

        return new SmartArtHierarchyBalanceAudit(
            totalNodes, maxDepth, avgBranching, lopsidedParents, deeplyNestedCount,
            branchingVariance, score, items, category, summary);
    }

    public static SmartArtHierarchyBalanceAudit AuditHierarchyIndentationAndChildBalance(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditHierarchyIndentationAndChildBalance(markdown, layoutName);
    }

    public static string FormatHierarchyBalanceSummary(
        int totalNodes, int maxDepth, double avgBranching, int lopsidedParents,
        int deeplyNestedCount, double branchingVariance, double score, string category)
    {
        if (totalNodes == 0)
            return "Hierarchy tree balance audit: 0 nodes · Outline contains no items";

        string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
        var alerts = new List<string>();
        alerts.Add($"max depth {maxDepth}");
        alerts.Add($"avg branch {avgBranching:0.#}");
        if (lopsidedParents > 0) alerts.Add($"⚠ {lopsidedParents} lopsided {(lopsidedParents == 1 ? "parent" : "parents")}");
        if (deeplyNestedCount > 0) alerts.Add($"⚠ {deeplyNestedCount} deep (level > 3)");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · balanced branches";
        return $"Hierarchy tree balance audit ({nodeStr} · {category} · {score:0.#}% balance){details}";
    }

    /// <summary>
    /// Audits SmartArt cycle and radial layouts for monotonic sequencing, minimum loop closure (N >= 3),
    /// step description word count uniformity, and sub-bullet balance.
    /// </summary>
    public SmartArtCycleSequenceAudit AuditCycleSequenceAndClosure(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.DisplayName ?? SelectedLayout?.Name ?? "Cycle";

        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtCycleSequenceAudit(
                0, false, false, 0.0, 0.0, 0, 100.0,
                Array.Empty<CycleSequenceStepItem>(),
                "Empty Outline", layout,
                $"SmartArt cycle sequence audit ({layout} · 0 steps): Outline is empty");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var rawSteps = new List<(string Title, int LineNumber, List<string> SubBullets)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent += line[indent] == '\t' ? 4 : 1;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
                continue;

            int level = indent / 2;
            string nodeText = trimmed;

            if (trimmed.StartsWith('#'))
            {
                int hashCount = trimmed.TakeWhile(c => c == '#').Count();
                level = Math.Max(0, hashCount - 1);
                nodeText = trimmed.TrimStart('#').Trim();
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                nodeText = trimmed.Substring(2).Trim();
            }
            else
            {
                var numMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
                if (numMatch.Success)
                {
                    nodeText = numMatch.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(nodeText)) continue;

            if (level == 0)
            {
                rawSteps.Add((nodeText, i + 1, new List<string>()));
            }
            else if (rawSteps.Count > 0)
            {
                rawSteps[rawSteps.Count - 1].SubBullets.Add(nodeText);
            }
            else
            {
                rawSteps.Add((nodeText, i + 1, new List<string>()));
            }
        }

        int totalSteps = rawSteps.Count;
        bool meetsMinSize = totalSteps >= 3;
        bool isClosedLoop = totalSteps >= 3;

        if (totalSteps == 0)
        {
            return new SmartArtCycleSequenceAudit(
                0, false, false, 0.0, 0.0, 0, 100.0,
                Array.Empty<CycleSequenceStepItem>(),
                "Empty Outline", layout,
                $"SmartArt cycle sequence audit ({layout} · 0 steps): Outline contains no valid steps");
        }

        var wordCounts = rawSteps.Select(s => s.Title.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length).ToList();
        double avgWords = Math.Round(wordCounts.Average(), 1);
        double wordVariance = wordCounts.Count > 1
            ? Math.Round(wordCounts.Select(w => Math.Pow(w - avgWords, 2)).Average(), 1)
            : 0.0;

        int totalSubBullets = rawSteps.Sum(s => s.SubBullets.Count);

        var items = new List<CycleSequenceStepItem>();
        for (int i = 0; i < rawSteps.Count; i++)
        {
            var step = rawSteps[i];
            int words = wordCounts[i];
            double deviation = Math.Round(words - avgWords, 1);
            int subCount = step.SubBullets.Count;

            string status = !meetsMinSize ? "⚠ Needs ≥3 steps to form closed loop" :
                            Math.Abs(deviation) > 6.0 ? $"⚠ Word count outlier ({words} words vs avg {avgWords:0.#})" :
                            subCount > 3 ? $"✓ Step with {subCount} details" : "✓ Balanced step";

            items.Add(new CycleSequenceStepItem(
                i + 1,
                step.Title,
                step.LineNumber,
                step.Title.Length,
                words,
                deviation,
                subCount,
                status));
        }

        double penalty = 0.0;
        if (!meetsMinSize)
        {
            penalty += (3 - totalSteps) * 20.0;
        }

        if (wordVariance > 10.0)
        {
            penalty += Math.Min(25.0, (wordVariance - 10.0) * 1.5);
        }

        if (totalSteps > 8)
        {
            penalty += (totalSteps - 8) * 5.0;
        }

        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Harmonious Cyclic Loop (≥90%)",
            >= 75.0 => "Balanced Sequence (75–89%)",
            >= 60.0 => "Moderate Cycle Asymmetry (60–74%)",
            >= 40.0 => "Unbalanced / Underpopulated Cycle (40–59%)",
            _ => "Incomplete Cycle (<40%)"
        };

        string summary = FormatCycleSequenceSummary(
            totalSteps, meetsMinSize, avgWords, wordVariance, totalSubBullets, score, category, layout);

        StatusMessage = summary;

        return new SmartArtCycleSequenceAudit(
            totalSteps, isClosedLoop, meetsMinSize, avgWords, wordVariance, totalSubBullets,
            score, items, category, layout, summary);
    }

    public static SmartArtCycleSequenceAudit AuditCycleSequenceAndClosure(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditCycleSequenceAndClosure(markdown, layoutName);
    }

    public static string FormatCycleSequenceSummary(
        int totalSteps, bool meetsMinSize, double avgWords, double wordVariance,
        int totalSubBullets, double score, string category, string layoutName)
    {
        if (totalSteps == 0)
            return $"SmartArt cycle sequence audit ({layoutName} · 0 steps): Outline is empty";

        string stepStr = totalSteps == 1 ? "1 step" : $"{totalSteps} steps";
        var alerts = new List<string>();
        if (!meetsMinSize) alerts.Add("⚠ incomplete loop (needs ≥3 steps)");
        else alerts.Add("closed cyclic loop");

        alerts.Add($"avg {avgWords:0.#} words/step (var {wordVariance:0.#})");
        if (totalSubBullets > 0) alerts.Add($"{totalSubBullets} sub-items");

        string details = string.Join(" · ", alerts);
        return $"Cycle sequence audit ({layoutName} · {stepStr} · {category} · {score:0.#}% balance): {details}";
    }

    /// <summary>
    /// Audits SmartArt matrix, grid, and quadrant layouts for structural balance, item distribution parity,
    /// and categorical symmetry across quadrant sectors (Q1..Q4).
    /// </summary>
    public SmartArtMatrixBalanceAudit AuditMatrixQuadrantBalance(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.DisplayName ?? SelectedLayout?.Name ?? "Quadrant Matrix";

        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtMatrixBalanceAudit(
                4, 0, 4, 0, 0, 0.0, 100.0,
                Array.Empty<QuadrantBalanceItem>(),
                "Empty Outline", layout,
                $"SmartArt matrix quadrant audit ({layout} · 0 items): Outline is empty");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var rawSteps = new List<(string Title, int LineNumber, List<string> SubBullets)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent += line[indent] == '\t' ? 4 : 1;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
                continue;

            int level = indent / 2;
            string nodeText = trimmed;

            if (trimmed.StartsWith('#'))
            {
                int hashCount = trimmed.TakeWhile(c => c == '#').Count();
                level = Math.Max(0, hashCount - 1);
                nodeText = trimmed.TrimStart('#').Trim();
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                nodeText = trimmed.Substring(2).Trim();
            }
            else
            {
                var numMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
                if (numMatch.Success)
                {
                    nodeText = numMatch.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(nodeText)) continue;

            if (level == 0)
            {
                rawSteps.Add((nodeText, i + 1, new List<string>()));
            }
            else if (rawSteps.Count > 0)
            {
                rawSteps[rawSteps.Count - 1].SubBullets.Add(nodeText);
            }
            else
            {
                rawSteps.Add((nodeText, i + 1, new List<string>()));
            }
        }

        string[] defaultQuadrantNames = new[] { "Quadrant 1 (Top-Left)", "Quadrant 2 (Top-Right)", "Quadrant 3 (Bottom-Left)", "Quadrant 4 (Bottom-Right)" };
        int totalQuadrants = 4;
        var quadrantItems = new List<QuadrantBalanceItem>();

        int totalItems = 0;
        int totalWords = 0;
        var itemCounts = new List<int>();

        for (int q = 0; q < totalQuadrants; q++)
        {
            if (q < rawSteps.Count)
            {
                var step = rawSteps[q];
                int itemsInQuadrant = 1 + step.SubBullets.Count;
                int words = step.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                foreach (var sb in step.SubBullets)
                {
                    words += sb.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                }

                totalItems += itemsInQuadrant;
                totalWords += words;
                itemCounts.Add(itemsInQuadrant);

                double meanWord = itemsInQuadrant > 0 ? (double)words / itemsInQuadrant : 0.0;
                string qName = !string.IsNullOrWhiteSpace(step.Title) ? step.Title : defaultQuadrantNames[q];

                quadrantItems.Add(new QuadrantBalanceItem(
                    q + 1, qName, itemsInQuadrant, words, Math.Round(meanWord, 1), false, false));
            }
            else
            {
                itemCounts.Add(0);
                quadrantItems.Add(new QuadrantBalanceItem(
                    q + 1, defaultQuadrantNames[q], 0, 0, 0.0, true, false));
            }
        }

        int populatedCount = quadrantItems.Count(qi => !qi.IsEmpty);
        int emptyCount = totalQuadrants - populatedCount;

        double avgItems = totalQuadrants > 0 ? (double)totalItems / totalQuadrants : 0.0;
        double varianceSum = 0;
        foreach (var c in itemCounts)
        {
            varianceSum += Math.Pow(c - avgItems, 2);
        }
        double itemVariance = Math.Round(varianceSum / totalQuadrants, 2);

        // Mark overloaded quadrants
        for (int i = 0; i < quadrantItems.Count; i++)
        {
            var qi = quadrantItems[i];
            if (!qi.IsEmpty && avgItems > 0 && qi.ItemCount >= avgItems * 1.8 && qi.ItemCount > 2)
            {
                quadrantItems[i] = qi with { IsOverloaded = true };
            }
        }

        double penalty = (emptyCount * 14.0) + (itemVariance * 5.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Harmonious Quadrant Balance (≥90%)",
            >= 75.0 => "Symmetric Matrix Grid (75–89%)",
            >= 50.0 => "Asymmetric Quadrant Distribution (50–74%)",
            _ => "Severely Warped / Skewed Matrix (<50%)"
        };

        string summary = FormatMatrixQuadrantSummary(
            totalQuadrants, populatedCount, emptyCount, totalItems, totalWords, itemVariance, score, category, layout);

        StatusMessage = summary;

        return new SmartArtMatrixBalanceAudit(
            totalQuadrants, populatedCount, emptyCount, totalItems, totalWords, itemVariance,
            score, quadrantItems, category, layout, summary);
    }

    public static SmartArtMatrixBalanceAudit AuditMatrixQuadrantBalance(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditMatrixQuadrantBalance(markdown, layoutName);
    }

    public static string FormatMatrixQuadrantSummary(
        int totalQuadrants, int populatedCount, int emptyCount, int totalItems, int totalWords,
        double itemVariance, double score, string category, string layoutName)
    {
        if (totalItems == 0)
            return $"SmartArt matrix quadrant audit ({layoutName} · 0 items): Outline is empty";

        var alerts = new List<string>();
        if (emptyCount > 0) alerts.Add($"⚠ {emptyCount} empty {(emptyCount == 1 ? "quadrant" : "quadrants")}");
        else alerts.Add("4/4 quadrants populated");

        alerts.Add($"{totalItems} total items ({totalWords} words)");
        alerts.Add($"item variance {itemVariance:0.##}");

        string details = string.Join(" · ", alerts);
        return $"Matrix quadrant balance audit ({layoutName} · {category} · {score:0.#}% symmetry): {details}";
    }

    /// <summary>
    /// Audits SmartArt outline for funnel and pyramid layout monotonic staging, volume progression, and stage concise sizing.
    /// </summary>
    public SmartArtFunnelPyramidProgressionAudit AuditFunnelPyramidVolumeProgression(string? markdown = null, string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.DisplayName ?? SelectedLayout?.Name ?? "Funnel / Pyramid";

        bool isFunnel = layout.Contains("funnel", StringComparison.OrdinalIgnoreCase);
        bool isPyramid = layout.Contains("pyramid", StringComparison.OrdinalIgnoreCase);
        if (!isFunnel && !isPyramid)
        {
            if (layout.Contains("inverted", StringComparison.OrdinalIgnoreCase)) isFunnel = true;
            else if (layout.Contains("stage", StringComparison.OrdinalIgnoreCase)) isFunnel = true;
            else isPyramid = true;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtFunnelPyramidProgressionAudit(
                0, isFunnel, isPyramid, true, 0, 100.0,
                Array.Empty<FunnelPyramidProgressionStageItem>(),
                "Empty Outline", layout,
                $"SmartArt funnel/pyramid progression audit ({layout} · 0 stages): Outline is empty");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var rawStages = new List<(string Title, int LineNumber, List<string> SubBullets)>();
        (string Title, int LineNumber, List<string> SubBullets)? currentStage = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) continue;

            int indent = line.TakeWhile(char.IsWhiteSpace).Count();
            string trimmed = line.TrimStart('-', '*', '+', '#', ' ', '\t');

            if (indent == 0 || (!line.StartsWith(" ") && !line.StartsWith("\t") && (line.StartsWith("-") || line.StartsWith("*") || (line.Length > 0 && char.IsDigit(line[0])))))
            {
                if (currentStage != null) rawStages.Add(currentStage.Value);
                currentStage = (trimmed, rawStages.Count + 1, new List<string>());
            }
            else
            {
                if (currentStage != null)
                {
                    currentStage.Value.SubBullets.Add(trimmed);
                }
                else
                {
                    currentStage = (trimmed, 1, new List<string>());
                }
            }
        }
        if (currentStage != null) rawStages.Add(currentStage.Value);

        int totalStages = rawStages.Count;
        if (totalStages == 0)
        {
            return new SmartArtFunnelPyramidProgressionAudit(
                0, isFunnel, isPyramid, true, 0, 100.0,
                Array.Empty<FunnelPyramidProgressionStageItem>(),
                "Empty Outline", layout,
                $"SmartArt funnel/pyramid progression audit ({layout} · 0 stages): Outline is empty");
        }

        var stageItems = new List<FunnelPyramidProgressionStageItem>();
        int wordViolations = 0;
        bool isMonotonic = true;

        for (int i = 0; i < totalStages; i++)
        {
            var stg = rawStages[i];
            int words = stg.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            foreach (var sub in stg.SubBullets)
            {
                words += sub.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            }

            if (words > 12) wordViolations++;

            double vol;
            if (isFunnel)
            {
                vol = Math.Round(100.0 - ((double)i / Math.Max(1, totalStages - 1)) * (100.0 - (100.0 / totalStages)), 1);
            }
            else
            {
                vol = Math.Round((100.0 / totalStages) + ((double)i / Math.Max(1, totalStages - 1)) * (100.0 - (100.0 / totalStages)), 1);
            }

            bool monotonicWithPrev = true;
            if (i > 0)
            {
                double prevVol = stageItems[i - 1].RelativeVolume;
                if (isFunnel && vol >= prevVol) { monotonicWithPrev = false; isMonotonic = false; }
                else if (isPyramid && vol <= prevVol) { monotonicWithPrev = false; isMonotonic = false; }
            }

            string status = words > 12
                ? $"⚠ Overly verbose ({words} words > 12)"
                : !monotonicWithPrev
                    ? "⚠ Volume progression non-monotonic"
                    : $"✓ Stage {i + 1} progression ({vol:0.#}% volume)";

            stageItems.Add(new FunnelPyramidProgressionStageItem(i + 1, stg.Title, words, vol, monotonicWithPrev, status));
        }

        double score = 100.0;
        if (totalStages < 3) score -= 15.0;
        else if (totalStages > 6) score -= 10.0;

        score -= (wordViolations * 8.0);
        if (!isMonotonic) score -= 20.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Stage Progression (≥90%)"
            : score >= 75.0
                ? "Moderate Volume Variance (75-89%)"
                : "Irregular Stage Hierarchy (<75%)";

        string summary = FormatFunnelPyramidSummary(
            totalStages, isFunnel, isPyramid, isMonotonic, wordViolations, score, category, layout);

        return new SmartArtFunnelPyramidProgressionAudit(
            totalStages, isFunnel, isPyramid, isMonotonic, wordViolations, score, stageItems, category, layout, summary);
    }

    public static SmartArtFunnelPyramidProgressionAudit AuditFunnelPyramidVolumeProgression(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditFunnelPyramidVolumeProgression(markdown, layoutName);
    }

    public static string FormatFunnelPyramidSummary(
        int totalStages, bool isFunnel, bool isPyramid, bool isMonotonic,
        int wordViolations, double score, string category, string layoutName)
    {
        if (totalStages == 0)
            return $"SmartArt funnel/pyramid progression audit ({layoutName} · 0 stages): Outline is empty";

        string kind = isFunnel ? "Funnel" : "Pyramid";
        string stageStr = totalStages == 1 ? "1 stage" : $"{totalStages} stages";
        var alerts = new List<string>();
        if (isMonotonic) alerts.Add("monotonic volume progression");
        else alerts.Add("⚠ non-monotonic staging order");

        if (wordViolations > 0) alerts.Add($"⚠ {wordViolations} verbose stage {(wordViolations == 1 ? "label" : "labels")}");
        else alerts.Add("concise stage labels (≤12w)");

        string details = string.Join(" · ", alerts);
        return $"SmartArt {kind} progression audit ({layoutName} · {stageStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits SmartArt timeline, roadmap, and horizontal process layouts for chronological monotonicity,
    /// step count bounds (3 <= N <= 8), and milestone pacing balance.
    /// </summary>
    public SmartArtTimelineIntervalAudit AuditTimelineChronologyAndIntervals(
        string? markdown = null,
        string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.DisplayName ?? SelectedLayout?.Name ?? "Timeline / Process";

        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtTimelineIntervalAudit(
                0, true, 0, 0, false, 100.0,
                Array.Empty<TimelineIntervalMilestoneItem>(),
                "Empty Outline", layout,
                $"SmartArt timeline chronology audit ({layout} · 0 milestones): Outline is empty");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var rawSteps = new List<string>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) continue;

            int indent = line.TakeWhile(char.IsWhiteSpace).Count();
            string trimmed = line.TrimStart('-', '*', '+', '#', ' ', '\t');

            if (indent == 0 || (!line.StartsWith(" ") && !line.StartsWith("\t") && (line.StartsWith("-") || line.StartsWith("*") || (line.Length > 0 && char.IsDigit(line[0])))))
            {
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    rawSteps.Add(trimmed);
                }
            }
        }

        int totalMilestones = rawSteps.Count;
        if (totalMilestones == 0)
        {
            return new SmartArtTimelineIntervalAudit(
                0, true, 0, 0, false, 100.0,
                Array.Empty<TimelineIntervalMilestoneItem>(),
                "Empty Outline", layout,
                $"SmartArt timeline chronology audit ({layout} · 0 milestones): Outline is empty");
        }

        var yearRegex = new Regex(@"\b(19\d\d|20\d\d)\b", RegexOptions.Compiled);
        var quarterRegex = new Regex(@"\bQ([1-4])\s*(?:'|\b)(?:20)?(\d\d)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var phaseRegex = new Regex(@"\b(?:Phase|Step|Stage|Sprint|Milestone|v|Version)\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var items = new List<TimelineIntervalMilestoneItem>();
        int wordViolations = 0;
        int invertedCount = 0;
        int? lastParsedSequenceValue = null;

        for (int i = 0; i < totalMilestones; i++)
        {
            string label = rawSteps[i];
            int words = label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 12) wordViolations++;

            string indicator = "Generic Step";
            int? parsedVal = null;

            var yMatch = yearRegex.Match(label);
            var qMatch = quarterRegex.Match(label);
            var pMatch = phaseRegex.Match(label);

            if (yMatch.Success)
            {
                int year = int.Parse(yMatch.Groups[1].Value);
                indicator = $"Year {year}";
                parsedVal = year;
            }
            else if (qMatch.Success)
            {
                int q = int.Parse(qMatch.Groups[1].Value);
                int y = qMatch.Groups[2].Success && !string.IsNullOrEmpty(qMatch.Groups[2].Value)
                    ? int.Parse(qMatch.Groups[2].Value) + 2000
                    : 2025;
                indicator = $"Q{q} {y}";
                parsedVal = (y * 10) + q;
            }
            else if (pMatch.Success)
            {
                int p = int.Parse(pMatch.Groups[1].Value);
                indicator = $"Phase {p}";
                parsedVal = p;
            }

            bool ordered = true;
            if (parsedVal.HasValue)
            {
                if (lastParsedSequenceValue.HasValue && parsedVal.Value < lastParsedSequenceValue.Value)
                {
                    ordered = false;
                    invertedCount++;
                }
                lastParsedSequenceValue = parsedVal.Value;
            }

            string status = words > 12
                ? $"⚠ Overly verbose ({words} words > 12)"
                : !ordered
                    ? $"⚠ Inverted sequence order ({indicator} appears after later milestone)"
                    : $"✓ Milestone {i + 1} ({indicator})";

            items.Add(new TimelineIntervalMilestoneItem(
                i + 1,
                label,
                indicator,
                parsedVal,
                words,
                ordered,
                status));
        }

        bool isOptimalBounds = totalMilestones >= 3 && totalMilestones <= 8;
        bool isMonotonic = invertedCount == 0;

        double score = 100.0;
        if (totalMilestones < 3) score -= 15.0;
        else if (totalMilestones > 8) score -= 12.0;

        score -= (invertedCount * 18.0);
        score -= (wordViolations * 8.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Milestone Pacing & Chronology (≥90%)"
            : score >= 75.0
                ? "Moderate Chronological Variance (75-89%)"
                : "Inverted Sequence & Temporal Clutter (<75%)";

        string summary = FormatTimelineChronologySummary(
            totalMilestones, isMonotonic, invertedCount, wordViolations, isOptimalBounds, score, category, layout);

        return new SmartArtTimelineIntervalAudit(
            totalMilestones, isMonotonic, invertedCount, wordViolations, isOptimalBounds, score, items, category, layout, summary);
    }

    public static SmartArtTimelineIntervalAudit AuditTimelineChronologyAndIntervals(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditTimelineChronologyAndIntervals(markdown, layoutName);
    }

    public static string FormatTimelineChronologySummary(
        int totalMilestones, bool isMonotonic, int invertedCount, int wordViolations,
        bool isOptimalBounds, double score, string category, string layoutName)
    {
        if (totalMilestones == 0)
            return $"SmartArt timeline chronology audit ({layoutName} · 0 milestones): Outline is empty";

        string milestoneStr = totalMilestones == 1 ? "1 milestone" : $"{totalMilestones} milestones";
        var alerts = new List<string>();

        if (isMonotonic) alerts.Add("strictly chronological");
        else alerts.Add($"⚠ {invertedCount} chronological {(invertedCount == 1 ? "inversion" : "inversions")}");

        if (wordViolations > 0) alerts.Add($"⚠ {wordViolations} verbose {(wordViolations == 1 ? "label" : "labels")}");
        else alerts.Add("concise milestone labels (≤12w)");

        if (!isOptimalBounds) alerts.Add(totalMilestones < 3 ? "sparse timeline (<3)" : "dense timeline (>8)");
        else alerts.Add("balanced bounds (3-8)");

        string details = string.Join(" · ", alerts);
        return $"SmartArt timeline chronology audit ({layoutName} · {milestoneStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits SmartArt radial cycle, diverging radial, and hub-and-spoke layouts for angular step separation,
    /// spoke count bounds (3 &lt;= N &lt;= 8), text length balance, and radial symmetry.
    /// </summary>
    public SmartArtRadialHubSpokeAudit AuditRadialHubSpokeSymmetry(
        string? markdown = null,
        string? layoutName = null)
    {
        string input = markdown ?? MarkdownText ?? string.Empty;
        string layout = layoutName ?? SelectedLayout?.DisplayName ?? SelectedLayout?.Name ?? "Radial / Hub-and-Spoke";

        if (string.IsNullOrWhiteSpace(input))
        {
            return new SmartArtRadialHubSpokeAudit(
                0, null, 0.0, false, 0, 0.0, 100.0,
                Array.Empty<RadialHubSpokeSectorItem>(),
                "Empty Outline", layout,
                $"SmartArt radial symmetry audit ({layout} · 0 spokes): Outline is empty");
        }

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var rawItems = new List<(string Label, int Indent)>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) continue;

            int indent = line.TakeWhile(char.IsWhiteSpace).Count();
            string trimmed = line.TrimStart('-', '*', '+', '#', ' ', '\t');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                rawItems.Add((trimmed, indent));
            }
        }

        if (rawItems.Count == 0)
        {
            return new SmartArtRadialHubSpokeAudit(
                0, null, 0.0, false, 0, 0.0, 100.0,
                Array.Empty<RadialHubSpokeSectorItem>(),
                "Empty Outline", layout,
                $"SmartArt radial symmetry audit ({layout} · 0 spokes): Outline is empty");
        }

        string? hubLabel = null;
        var spokeLabels = new List<string>();

        // Check if layout is hub-and-spoke or has a hierarchy
        bool isHubLayout = layout.Contains("Hub", StringComparison.OrdinalIgnoreCase) ||
                           layout.Contains("Diverging", StringComparison.OrdinalIgnoreCase);

        if (rawItems.Count > 1 && (isHubLayout || rawItems.Any(r => r.Indent > 0)))
        {
            // First item with min indent is Hub, rest (or indented items) are spokes
            hubLabel = rawItems[0].Label;
            for (int i = 1; i < rawItems.Count; i++)
            {
                spokeLabels.Add(rawItems[i].Label);
            }
        }
        else
        {
            // All items are radial cycle spokes/sectors
            foreach (var item in rawItems)
            {
                spokeLabels.Add(item.Label);
            }
        }

        int totalSpokes = spokeLabels.Count;
        if (totalSpokes == 0)
        {
            return new SmartArtRadialHubSpokeAudit(
                0, hubLabel, 0.0, false, 0, 0.0, 100.0,
                Array.Empty<RadialHubSpokeSectorItem>(),
                "Empty Outline", layout,
                $"SmartArt radial symmetry audit ({layout} · 0 spokes): No spokes found");
        }

        double angleStep = Math.Round(360.0 / totalSpokes, 1);
        bool isOptimalBounds = totalSpokes >= 3 && totalSpokes <= 8;

        var sectors = new List<RadialHubSpokeSectorItem>();
        int wordViolations = 0;
        var charLengths = new List<int>();

        for (int i = 0; i < totalSpokes; i++)
        {
            string label = spokeLabels[i];
            double idealAngle = Math.Round(i * (360.0 / totalSpokes), 1);
            int words = label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            int chars = label.Length;
            charLengths.Add(chars);

            bool isViolation = words > 12;
            if (isViolation) wordViolations++;

            string status;
            if (isViolation)
                status = $"⚠ Verbose spoke label ({words} words > 12 max recommended) at {idealAngle:0.#}°";
            else
                status = $"✓ Balanced sector at {idealAngle:0.#}° ({words} words · {chars} chars)";

            sectors.Add(new RadialHubSpokeSectorItem(
                i + 1,
                label,
                idealAngle,
                words,
                chars,
                isViolation,
                status));
        }

        double avgLength = charLengths.Average();
        double variance = charLengths.Count > 1
            ? charLengths.Select(c => Math.Pow(c - avgLength, 2)).Average()
            : 0.0;
        variance = Math.Round(variance, 1);

        double score = 100.0;
        if (totalSpokes < 3) score -= 30.0;
        else if (totalSpokes > 8) score -= ((totalSpokes - 8) * 8.0);

        score -= (wordViolations * 5.0);
        if (variance > 400.0) score -= 15.0;
        else if (variance > 100.0) score -= 8.0;

        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Radial Symmetry & Balance (≥90%)"
            : score >= 75.0
                ? "Moderate Radial Disparity (75-89%)"
                : "Severe Angular Crowding / Asymmetry (<75%)";

        string summary = FormatRadialHubSpokeSummary(
            totalSpokes, hubLabel, angleStep, isOptimalBounds, wordViolations,
            variance, score, category, layout);

        return new SmartArtRadialHubSpokeAudit(
            totalSpokes, hubLabel, angleStep, isOptimalBounds, wordViolations,
            variance, score, sectors, category, layout, summary);
    }

    public static SmartArtRadialHubSpokeAudit AuditRadialHubSpokeSymmetry(
        string? markdown, string? layoutName = null, bool isStatic = true)
    {
        var vm = new SmartArtDesignStudioViewModel();
        return vm.AuditRadialHubSpokeSymmetry(markdown, layoutName);
    }

    public static string FormatRadialHubSpokeSummary(
        int totalSpokes, string? hubLabel, double angleStep, bool isOptimalBounds,
        int wordViolations, double textVariance, double score, string category, string layoutName)
    {
        if (totalSpokes == 0)
            return $"SmartArt radial symmetry audit ({layoutName} · 0 spokes): Outline is empty";

        string spokeStr = totalSpokes == 1 ? "1 spoke" : $"{totalSpokes} spokes";
        var alerts = new List<string>();

        if (!string.IsNullOrEmpty(hubLabel)) alerts.Add($"hub: '{hubLabel}'");
        alerts.Add($"angular step {angleStep:0.#}°");

        if (isOptimalBounds) alerts.Add("optimal spoke bounds (3-8)");
        else alerts.Add(totalSpokes < 3 ? "sparse spoke count (<3)" : "crowded circle (>8)");

        if (wordViolations > 0) alerts.Add($"⚠ {wordViolations} verbose {(wordViolations == 1 ? "label" : "labels")}");
        else alerts.Add("concise labels");

        string details = string.Join(" · ", alerts);
        return $"SmartArt radial symmetry audit ({layoutName} · {spokeStr} · {category} · {score:0.#}% score): {details}";
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

/// <summary>
/// Represents the result of an audit of cyclic flow continuity, loop closure, and circular layout fit across outline items.
/// </summary>
public sealed record CyclicFlowContinuityAudit(
    int TotalSteps,
    bool HasLoopClosure,
    int IntermediateBranchCount,
    double ContinuityScore,
    string ClosureMechanism,
    string FlowContinuityCategory,
    string RecommendedLayoutCategory,
    string Summary);

/// <summary>
/// Represents item count and character metrics for an individual matrix quadrant.
/// </summary>
public sealed record MatrixQuadrantInfo(
    int QuadrantIndex,
    string QuadrantTitle,
    int ItemCount,
    int TotalCharCount);

/// <summary>
/// Represents the result of an audit of 2x2 matrix quadrant balance, uniformity, and distribution symmetry.
/// </summary>
public sealed record MatrixQuadrantBalanceAudit(
    int TotalQuadrants,
    int TotalItems,
    double AverageItemsPerQuadrant,
    double ItemsCoefficientOfVariation,
    double BalanceScore,
    IReadOnlyList<MatrixQuadrantInfo> Quadrants,
    string BalanceCategory,
    string Summary);

/// <summary>
/// Represents the result of a 2x2 matrix quadrant header normalization operation.
/// </summary>
public sealed record MatrixQuadrantNormalizationResult(
    string NormalizedMarkdown,
    string DetectedArchetype,
    int NormalizedHeadersCount,
    string Summary);

/// <summary>
/// Represents volume and bullet metrics for an individual pyramid or funnel stage.
/// </summary>
public sealed record PyramidStageInfo(
    int StageIndex,
    string StageTitle,
    int CharacterCount,
    int ChildBulletCount,
    double RelativeVolumePercentage);

/// <summary>
/// Represents the result of an audit of hierarchical pyramid and funnel stage area proportionality and volume progression.
/// </summary>
public sealed record PyramidProportionalityAudit(
    int StageCount,
    IReadOnlyList<PyramidStageInfo> Stages,
    double TaperProgressionRatio,
    bool IsMonotonicallyTapered,
    double ProportionalityScore,
    string ProportionalityCategory,
    string Summary);

/// <summary>
/// Represents parsed set info and item count for an individual Venn diagram partition.
/// </summary>
public sealed record VennPartitionInfo(
    string PartitionName,
    string PartitionType,
    int ItemCount,
    IReadOnlyList<string> Items,
    bool IsIntersection);

/// <summary>
/// Represents the result of an audit of Venn diagram set overlap completeness,
/// disjoint partitioning, and missing intersection regions.
/// </summary>
public sealed record VennDiagramCompletenessAudit(
    int PrimarySetCount,
    int ExpectedRegions,
    int DefinedRegions,
    int MissingRegionsCount,
    double CompletenessPercentage,
    IReadOnlyList<string> MissingPartitions,
    IReadOnlyList<VennPartitionInfo> Partitions,
    string CompletenessCategory,
    string Summary);

/// <summary>
/// Represents parsed date metrics and sequencing for an individual timeline milestone.
/// </summary>
public sealed record TimelineMilestoneInfo(
    int Index,
    string RawText,
    string CleanTitle,
    string DateString,
    bool HasDate,
    DateTime? ParsedDate,
    string DateFormat,
    bool IsChronologicallyOrdered);

/// <summary>
/// Represents the result of an audit of timeline milestone chronological monotonicity and date formats.
/// </summary>
public sealed record TimelineChronologyAudit(
    int TotalMilestones,
    int DatedMilestones,
    int ChronologicalInversions,
    double MonotonicityScore,
    bool IsUniformDateFormat,
    string DominantDateFormat,
    IReadOnlyList<TimelineMilestoneInfo> Milestones,
    IReadOnlyList<string> Inversions,
    string Category,
    string Summary);

/// <summary>
/// Represents stage sequencing and transition metrics for an individual stage in a cyclic process.
/// </summary>
public sealed record CyclicStageInfo(
    int StageIndex,
    string StageTitle,
    int WordCount,
    int ChildBulletCount,
    string NextStageTitle,
    bool IsTerminalSink);

/// <summary>
/// Represents the result of an audit of cyclic process loop convergence, terminal state detection,
/// and recurrence closure in SmartArt diagrams.
/// </summary>
public sealed record CyclicProcessConvergenceAudit(
    int TotalStages,
    bool HasRecurrenceClosure,
    int TerminalSinkCount,
    double StageWordCountVariance,
    double LoopConvergenceScore,
    IReadOnlyList<CyclicStageInfo> Stages,
    string ClosureMechanism,
    string ConvergenceCategory,
    string Summary);

/// <summary>
/// Represents span of control and hierarchy metrics for an individual node in an organizational hierarchy.
/// </summary>
public sealed record HierarchyNodeSpanInfo(
    int Level,
    string Title,
    int DirectReportsCount,
    int TotalSubtreeCount,
    bool IsManager,
    bool IsOverloaded,
    bool IsUnderutilized);

/// <summary>
/// Represents the result of an audit of organizational hierarchy span of control, branching factor variance,
/// and manager-to-report ratios.
/// </summary>
public sealed record HierarchySpanOfControlAudit(
    int TotalNodes,
    int ManagerCount,
    int IndividualContributorCount,
    int MaxDepth,
    double MeanSpanOfControl,
    double SpanStandardDeviation,
    double ManagerToReportRatio,
    int OverloadedManagerCount,
    int UnderutilizedManagerCount,
    double HierarchyBalanceScore,
    IReadOnlyList<HierarchyNodeSpanInfo> NodeSpans,
    string HierarchyCategory,
    string Summary);

/// <summary>
/// Represents typography scaling, font size fit, and text wrapping metrics for an individual SmartArt node.
/// </summary>
public sealed record SmartArtNodeTypographyInfo(
    int NodeIndex,
    string Title,
    int CharacterCount,
    int WordCount,
    int LineCount,
    double EstimatedOptimalFontSizePt,
    bool HasClippingRisk,
    bool RequiresHyphenation);

/// <summary>
/// Represents the result of an audit of SmartArt layout typography scaling, label font-fit,
/// and text clipping hazards across diagram node boundaries.
/// </summary>
public sealed record SmartArtTypographyFontFitAudit(
    int TotalNodes,
    double TargetCanvasWidth,
    double TargetCanvasHeight,
    double MinFontSizePt,
    double MaxFontSizePt,
    double MeanFontSizePt,
    int ClippingRiskCount,
    double TypographicLegibilityScore,
    IReadOnlyList<SmartArtNodeTypographyInfo> Nodes,
    string LegibilityCategory,
    string Summary);

/// <summary>
/// Represents calculated perceived luminance, color contrast ratio, and WCAG compliance for a specific outline depth level.
/// </summary>
public sealed record SmartArtDepthLevelMetrics(
    int DepthLevel,
    int NodeCount,
    string RepresentativeColorHex,
    double PerceivedLuminance,
    double ContrastRatioWithPreviousDepth,
    bool MeetsWcagAa,
    bool MeetsWcagAaa);

/// <summary>
/// Represents the result of an audit of SmartArt color hierarchy, depth gradient progression,
/// and chromatic harmony across diagram nesting levels.
/// </summary>
public sealed record SmartArtColorHierarchyAudit(
    int TotalNodes,
    int MaxDepth,
    double MeanContrastRatio,
    int ContrastDeficitCount,
    double ChromaticHarmonyScore,
    bool IsMonotonicProgression,
    string GradientDirection,
    IReadOnlyList<SmartArtDepthLevelMetrics> DepthLevels,
    IReadOnlyList<string> Issues,
    string Category,
    string Summary);

/// <summary>
/// Represents pacing, word count metrics, and relative rhythm for an individual sequential linear step.
/// </summary>
public sealed record LinearFlowStepInfo(
    int StepIndex,
    string Title,
    int WordCount,
    int CharacterCount,
    double RelativePacingRatio,
    bool IsPacingOutlier);

/// <summary>
/// Represents the result of an audit of SmartArt linear flow / chevron progression rhythm,
/// step text pacing variance, and arrowhead aspect ratio consistency.
/// </summary>
public sealed record LinearFlowChevronRhythmAudit(
    int TotalSteps,
    int TotalWords,
    double MeanWordsPerStep,
    double WordCountStdDev,
    double PacingVariationCoefficient,
    int PacingOutlierCount,
    double ProgressionRhythmScore,
    IReadOnlyList<LinearFlowStepInfo> Steps,
    string RhythmCategory,
    string Summary);

/// <summary>
/// Represents evaluated angular sector, text weight ratio, and balance metrics for an individual radial spoke.
/// </summary>
public sealed record RadialSpokeInfo(
    int SpokeIndex,
    string Title,
    double AngleDegrees,
    int WordCount,
    int SubBulletCount,
    double WeightRatio,
    bool IsImbalanced);

/// <summary>
/// Represents the result of an audit of SmartArt radial cycle and hub-and-spoke angular balance,
/// orbital pacing, and spoke text weight distribution.
/// </summary>
public sealed record SmartArtRadialCycleAudit(
    string HubTitle,
    int TotalSpokes,
    double AngularSectorStepDegrees,
    double WordCountStdDev,
    double SpokeBalanceScore,
    int ImbalancedSpokeCount,
    IReadOnlyList<RadialSpokeInfo> Spokes,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated text volume, geometric capacity, density ratio, and inverted mass trap detection for an individual pyramid or funnel tier.
/// </summary>
public sealed record PyramidTierInfo(
    int TierIndex,
    string Title,
    int WordCount,
    int CharacterCount,
    int SubBulletCount,
    double GeometricRelativeArea,
    double TextDensityRatio,
    bool HasInvertedMassTrap,
    string TaperGrade);

/// <summary>
/// Represents the result of an audit of SmartArt pyramid and funnel layer volume, height tapering,
/// proportional mass balance, and apex overloading.
/// </summary>
public sealed record SmartArtPyramidTaperingAudit(
    int TotalTiers,
    int TotalWords,
    bool IsInverted,
    int InvertedMassTrapCount,
    double TaperSmoothnessScore,
    double MeanDensityRatio,
    IReadOnlyList<PyramidTierInfo> Tiers,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated item count, word count, density, and overloading for an individual 2x2 matrix quadrant.
/// </summary>
public sealed record MatrixQuadrantItem(
    int QuadrantIndex,
    string QuadrantName,
    string Header,
    int ItemCount,
    int WordCount,
    int CharacterCount,
    double WordPercentage,
    bool IsEmpty,
    bool IsOverloaded);

/// <summary>
/// Represents the result of an audit of SmartArt 2x2 matrix diagrams for 4-quadrant balance,
/// item distribution, word count symmetry, and empty/overloaded quadrant traps.
/// </summary>
public sealed record SmartArtMatrixQuadrantAudit(
    int TotalQuadrants,
    int TotalItems,
    int TotalWords,
    double MeanItemsPerQuadrant,
    double MeanWordsPerQuadrant,
    double ItemStandardDeviation,
    double WordStandardDeviation,
    double SymmetryBalanceScore,
    IReadOnlyList<MatrixQuadrantItem> Quadrants,
    IReadOnlyList<string> BalanceAlerts,
    string SymmetryCategory,
    string Summary);

/// <summary>
/// Represents evaluated geometric lens area, relative chord dimension, and item density for a Venn diagram set region.
/// </summary>
public sealed record VennSetRegionInfo(
    string RegionId,
    string RegionLabel,
    string RegionType,
    int ItemCount,
    IReadOnlyList<string> Items,
    double GeometricRelativeArea,
    double ItemDensityRatio,
    bool IsEmptyTrap);

/// <summary>
/// Represents the result of an audit of SmartArt Venn diagram circular overlap chord geometry,
/// 2-set and 3-set lens intersection areas, item density balance, and empty intersection traps.
/// </summary>
public sealed record SmartArtVennOverlapAudit(
    int PrimarySetCount,
    int TotalRegions,
    int PopulatedRegions,
    int EmptyTrapRegions,
    int TotalItems,
    double LensOverlapChordAreaFraction,
    double VennBalanceScore,
    IReadOnlyList<VennSetRegionInfo> Regions,
    IReadOnlyList<string> BalanceAlerts,
    string Category,
    string Summary);




/// <summary>
/// Represents evaluated step metrics, estimated chevron aspect ratio, and word density for an individual process step.
/// </summary>
public sealed record ProcessSequenceStepInfo(
    int StepIndex,
    string StepTitle,
    int WordCount,
    int CharacterCount,
    double EstimatedStepWidth,
    double EstimatedAspectRatio,
    bool IsOverloaded,
    bool IsTooBrief);

/// <summary>
/// Represents the result of an audit of SmartArt chevron and process arrow sequence pacing,
/// step aspect ratios, word count variance, and overloaded step traps.
/// </summary>
public sealed record SmartArtProcessSequenceAudit(
    int TotalSteps,
    int TotalWords,
    double MeanWordsPerStep,
    double WordStandardDeviation,
    double EstimatedChevronAspectRatio,
    int OverloadedStepCount,
    int TooBriefStepCount,
    double PacingBalanceScore,
    IReadOnlyList<ProcessSequenceStepInfo> Steps,
    IReadOnlyList<string> PacingAlerts,
    string SequenceCategory,
    string Summary);

/// <summary>
/// Represents evaluated node count and average text length for an individual hierarchy depth level.
/// </summary>
public sealed record HierarchyLevelInfo(
    int Level,
    int NodeCount,
    double AvgTextLength);

/// <summary>
/// Represents the result of an audit of SmartArt hierarchy tree depth-to-breadth aspect ratio,
/// branching factor balance, single-child chains, and wide fan ergonomics.
/// </summary>
public sealed record SmartArtHierarchyBranchingAudit(
    int TotalNodes,
    int MaxDepth,
    int MaxBreadth,
    double DepthToBreadthRatio,
    double AvgBranchingFactor,
    int SingleChildChains,
    int WideFanNodes,
    double HierarchyBalanceScore,
    IReadOnlyList<HierarchyLevelInfo> LevelDistribution,
    IReadOnlyList<string> StructuralAlerts,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, parsed date label, epoch score, and chronology alerts for an individual timeline milestone.
/// </summary>
public sealed record TimelineMilestoneItem(
    int Index,
    string RawText,
    string ParsedLabel,
    double EpochScore,
    bool HasExplicitDate,
    bool IsOutOfOrder,
    string? ChronologyAlert);

/// <summary>
/// Represents the result of an audit of SmartArt timeline milestone chronology, date monotonicity,
/// milestone duration intervals, and out-of-order temporal event hazards.
/// </summary>
public sealed record SmartArtTimelineChronologyAudit(
    int TotalMilestones,
    int DatedMilestones,
    int ChronologyInversions,
    double MeanIntervalDays,
    double ChronologyMonotonicityScore,
    IReadOnlyList<TimelineMilestoneItem> Milestones,
    IReadOnlyList<string> ChronologyAlerts,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, word count, chord angle, and brevity for an individual cyclic process step.
/// </summary>
public sealed record CycleSegmentItem(
    int StepIndex,
    string StepTitle,
    int WordCount,
    double ChordDegrees,
    bool IsOptimalBrevity);

/// <summary>
/// Represents the result of an audit of SmartArt segmented process cycle interval uniformity, radial chord balance,
/// cognitive step limits, and text brevity symmetry across cyclic process layouts.
/// </summary>
public sealed record SmartArtCycleIntervalAudit(
    int TotalSteps,
    double AngularChordDegrees,
    double MeanWordsPerStep,
    double WordCountStdDev,
    int OvercrowdedStepsCount,
    double RadialHarmonyScore,
    IReadOnlyList<CycleSegmentItem> Segments,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, word count, and density alerts for an individual 2x2 matrix quadrant cell.
/// </summary>
public sealed record QuadrantCellItem(
    int QuadrantIndex,
    string QuadrantName,
    string Header,
    int ItemCount,
    int WordCount,
    bool IsSparse,
    bool IsOvercrowded);

/// <summary>
/// Represents the result of an audit of SmartArt 2x2 matrix quadrant grid balance, diagonal symmetry,
/// item distribution across Q1-Q4, and empty quadrant traps.
/// </summary>
public sealed record SmartArtQuadrantGridAudit(
    int TotalItems,
    int TotalWords,
    int PopulatedQuadrants,
    int EmptyQuadrants,
    double MeanWordsPerQuadrant,
    double DiagonalSymmetryPercentage,
    double QuadrantBalanceScore,
    IReadOnlyList<QuadrantCellItem> Quadrants,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, stage title, word count, character volume, and taper consistency for an individual funnel or pyramid stage.
/// </summary>
public sealed record FunnelPyramidStageItem(
    int StageIndex,
    string Title,
    int WordCount,
    int CharacterCount,
    int SubitemCount,
    double RelativeVolumeRatio,
    bool HasAbruptVolumeInversion,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt funnel/pyramid progression volume, stage ingestion taper,
/// word density variance, and monotonic narrowing/widening without abrupt volume inversions.
/// </summary>
public sealed record SmartArtFunnelPyramidAudit(
    int TotalStages,
    int TotalWords,
    int InversionCount,
    double MeanStageWords,
    double WordCountStdDev,
    bool IsMonotonicProgression,
    double TaperUniformityScore,
    IReadOnlyList<FunnelPyramidStageItem> Stages,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, word density, character volume, and intersection status for a Venn/Radial set item.
/// </summary>
public sealed record VennRadialSetItem(
    int SetIndex,
    string Title,
    int WordCount,
    int CharacterCount,
    int ChildItemCount,
    bool IsIntersection,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt radial and Venn diagram overlap area, intersection balance,
/// word density variance, and set symmetry across primary sets and intersection regions.
/// </summary>
public sealed record SmartArtVennRadialAudit(
    int TotalSets,
    int TotalIntersections,
    int TotalWords,
    double MeanSetWords,
    double WordCountStdDev,
    double BalanceScore,
    IReadOnlyList<VennRadialSetItem> Sets,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated text density, character volume, line count, and outlier status for an individual SmartArt list item.
/// </summary>
public sealed record ListItemDensityInfo(
    int ItemIndex,
    string Title,
    int WordCount,
    int CharacterCount,
    int LineCount,
    int SubitemCount,
    bool IsDensityOutlier,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt list item text volume density, word count standard deviation,
/// line counts, and balance across horizontal, vertical, and block list layouts.
/// </summary>
public sealed record SmartArtListTextDensityAudit(
    int TotalListItems,
    int TotalWords,
    int TotalCharacters,
    double MeanWordCount,
    double StdDevWordCount,
    double MeanLineCount,
    int OutlierCount,
    double DensityBalanceScore,
    IReadOnlyList<ListItemDensityInfo> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, sequential ordering, transition cues, and directionality for an individual SmartArt process step.
/// </summary>
public sealed record ProcessStepTransitionItem(
    int StepIndex,
    string StepTitle,
    int TitleWordCount,
    int DescriptionWordCount,
    bool HasTransitionCue,
    string TransitionCueWord,
    bool HasSequentialNumber,
    string Directionality,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt process step flow, monotonic sequence numbering,
/// transition cue phrases, and step description balance.
/// </summary>
public sealed record SmartArtProcessStepFlowAudit(
    int TotalSteps,
    int StepsWithTransitionCues,
    int SequentiallyNumberedSteps,
    int OutlierStepCount,
    double AverageTitleWordCount,
    double TitleWordVariance,
    double FlowHarmonyScore,
    IReadOnlyList<ProcessStepTransitionItem> Steps,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, indentation level, child count, and balance status for an individual hierarchy outline item.
/// </summary>
public sealed record HierarchyNodeBalanceItem(
    int LineNumber,
    string Text,
    int Level,
    int ChildCount,
    bool IsLopsidedParent,
    bool IsDeeplyNested,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt hierarchy level indentation depth, sibling child count balance,
/// branching factor variance, and tree ergonomics.
/// </summary>
public sealed record SmartArtHierarchyBalanceAudit(
    int TotalNodes,
    int MaxDepth,
    double AverageBranchingFactor,
    int LopsidedParentCount,
    int DeeplyNestedCount,
    double BranchingFactorVariance,
    double HierarchyBalanceScore,
    IReadOnlyList<HierarchyNodeBalanceItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, character length, word count, and sub-bullets for an individual SmartArt cycle step.
/// </summary>
public sealed record CycleSequenceStepItem(
    int StepIndex,
    string Title,
    int LineNumber,
    int CharacterLength,
    int WordCount,
    double LengthDeviationFromMean,
    int SubBulletCount,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt cycle layout monotonic sequencing, minimum loop closure (N >= 3),
/// step description word count uniformity, and sub-bullet balance.
/// </summary>
public sealed record SmartArtCycleSequenceAudit(
    int TotalSteps,
    bool IsClosedLoop,
    bool MeetsMinimumCycleSize,
    double AverageStepWordCount,
    double WordCountVariance,
    int SubBulletTotal,
    double BalanceScore,
    IReadOnlyList<CycleSequenceStepItem> Steps,
    string Category,
    string LayoutName,
    string Summary);

/// <summary>
/// Represents evaluated metrics and distribution parity for an individual quadrant or grid sector in a matrix layout.
/// </summary>
public sealed record QuadrantBalanceItem(
    int QuadrantIndex,
    string QuadrantName,
    int ItemCount,
    int TotalWords,
    double MeanWordCount,
    bool IsEmpty,
    bool IsOverloaded);

/// <summary>
/// Represents the result of an audit of SmartArt matrix and grid quadrant balance, item variance, and categorical symmetry.
/// </summary>
public sealed record SmartArtMatrixBalanceAudit(
    int TotalQuadrants,
    int PopulatedQuadrants,
    int EmptyQuadrants,
    int TotalItems,
    int TotalWords,
    double ItemVariance,
    double SymmetryScore,
    IReadOnlyList<QuadrantBalanceItem> Quadrants,
    string Category,
    string LayoutName,
    string Summary);

/// <summary>
/// Represents evaluated metrics, relative volume, word count, and monotonic ordering for an individual funnel or pyramid stage.
/// </summary>
public sealed record FunnelPyramidProgressionStageItem(
    int StageIndex,
    string StageLabel,
    int WordCount,
    double RelativeVolume,
    bool IsMonotonicWithPrevious,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt funnel/pyramid layout monotonic staging, volume progression, and stage concise sizing.
/// </summary>
public sealed record SmartArtFunnelPyramidProgressionAudit(
    int TotalStages,
    bool IsFunnelLayout,
    bool IsPyramidLayout,
    bool IsMonotonicallyOrdered,
    int WordLengthViolations,
    double VolumeProgressionScore,
    IReadOnlyList<FunnelPyramidProgressionStageItem> Stages,
    string Category,
    string LayoutName,
    string Summary);

/// <summary>
/// Represents evaluated metrics, milestone identifier, temporal date or phase, word count, and chronological sequence for an individual timeline step.
/// </summary>
public sealed record TimelineIntervalMilestoneItem(
    int StepIndex,
    string MilestoneLabel,
    string TemporalIndicator,
    int? ParsedYearOrPhase,
    int WordCount,
    bool IsChronologicallyOrdered,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt timeline and linear process layouts for chronological monotonicity,
/// step count bounds (3 <= N <= 8), and milestone pacing balance.
/// </summary>
public sealed record SmartArtTimelineIntervalAudit(
    int TotalMilestones,
    bool IsMonotonicallyOrdered,
    int InvertedOrderCount,
    int WordLengthViolations,
    bool IsWithinOptimalStepBounds,
    double ChronologyBalanceScore,
    IReadOnlyList<TimelineIntervalMilestoneItem> Milestones,
    string Category,
    string LayoutName,
    string Summary);

/// <summary>
/// Represents evaluated metrics, spoke index, ideal angle in degrees, and text metrics for an individual spoke in a radial or hub-and-spoke layout.
/// </summary>
public sealed record RadialHubSpokeSectorItem(
    int SectorIndex,
    string NodeLabel,
    double IdealAngleDegrees,
    int WordCount,
    int CharacterCount,
    bool IsWordLengthViolation,
    string Status);

/// <summary>
/// Represents the result of an audit of SmartArt radial cycle, diverging radial, and hub-and-spoke layouts for angular step separation,
/// spoke count bounds (3 <= N <= 8), text length balance, and radial symmetry.
/// </summary>
public sealed record SmartArtRadialHubSpokeAudit(
    int TotalSpokes,
    string? HubLabel,
    double AngularSeparationDegrees,
    bool IsWithinOptimalSpokeBounds,
    int WordLengthViolations,
    double TextLengthVariance,
    double RadialSymmetryScore,
    IReadOnlyList<RadialHubSpokeSectorItem> Sectors,
    string Category,
    string LayoutName,
    string Summary);

