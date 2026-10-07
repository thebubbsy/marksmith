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

    /// <summary>The gallery miniature: the family's real shapes as SVG markup (no text).</summary>
    public string ThumbnailSvg => HtmlPreviewRenderer.RenderThumbnailSvg(Family);

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
        return sb.ToString();
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
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedNode is not null;
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
    }

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
        // A preload starts a new design: Ctrl+Z must not bring back the previous one.
        _undoStack.Clear();
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
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

    public void Select(StudioNodeViewModel? node) => SelectedNode = node;

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
        if (editing != null) editing.IsEditing = true;
        OnPropertyChanged(nameof(HasSelection));
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
        InsertIntoDocumentCommand.NotifyCanExecuteChanged();
    }

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

    private void PushUndo()
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
        if (_undoStack.Count == 0) return;
        _redoStack.Add(MarkdownText ?? "");
        string target = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        ApplyHistory(target);
    }

    [RelayCommand]
    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(MarkdownText ?? "");
        string target = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        ApplyHistory(target);
    }

    /// <summary>Adds a child under the given node (or the selection; or a new root when the
    /// tree is empty). The new node drops straight into inline rename.</summary>
    [RelayCommand]
    public void AddChild(StudioNodeViewModel? target = null)
    {
        var parent = target ?? SelectedNode;
        PushUndo();
        var node = new StudioNodeViewModel { Text = "New Node", Parent = parent, Depth = (parent?.Depth ?? -1) + 1 };
        if (parent == null) RootNodes.Add(node);
        else parent.Children.Add(node);
        Select(node);
        BeginRename(node);
        SyncTreeToMarkdown();
    }

    [RelayCommand]
    public void AddSibling(StudioNodeViewModel? target = null)
    {
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
    }

    [RelayCommand]
    public void DeleteSelected(StudioNodeViewModel? target = null)
    {
        var node = target ?? SelectedNode;
        if (node == null) return;
        PushUndo();
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        siblings.Remove(node);

        StudioNodeViewModel? next = null;
        if (siblings.Count > 0) next = idx < siblings.Count ? siblings[idx] : siblings[^1];
        else next = node.Parent;
        Select(next);
        SyncTreeToMarkdown();
    }

    [RelayCommand]
    public void MoveUp()
    {
        var node = SelectedNode;
        if (node == null) return;
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        if (idx <= 0) return;
        PushUndo();
        siblings.Move(idx, idx - 1);
        Select(node);
        SyncTreeToMarkdown();
    }

    [RelayCommand]
    public void MoveDown()
    {
        var node = SelectedNode;
        if (node == null) return;
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        if (idx < 0 || idx >= siblings.Count - 1) return;
        PushUndo();
        siblings.Move(idx, idx + 1);
        Select(node);
        SyncTreeToMarkdown();
    }

    /// <summary>Outdents: moves the node up one level, becoming a sibling of its parent.</summary>
    [RelayCommand]
    public void Promote()
    {
        var node = SelectedNode;
        if (node == null || node.Parent == null) return;
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
    }

    /// <summary>Indents: moves the node under its previous sibling.</summary>
    [RelayCommand]
    public void Demote()
    {
        var node = SelectedNode;
        if (node == null) return;
        var siblings = node.Parent == null ? RootNodes : node.Parent!.Children;
        int idx = siblings.IndexOf(node);
        if (idx <= 0) return;
        PushUndo();
        var prev = siblings[idx - 1];
        siblings.RemoveAt(idx);
        prev.Children.Add(node);
        node.Parent = prev;
        Select(node);
        SyncTreeToMarkdown();
    }

    [RelayCommand]
    public void BeginRename(StudioNodeViewModel? target = null)
    {
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
            PushUndo();
            SyncTreeToMarkdown();
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

            PreviewHtml = HtmlPreviewRenderer.RenderHtml(ast, alias, title);
            PreviewTitle = title;
            PreviewHint = SingleShapeHint(ast, alias, title);
            PreviewHtmlChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview error: {ex.Message}";
        }
    }

    private bool CanInsert => !IsOutlineEmpty;

    [RelayCommand(CanExecute = nameof(CanInsert))]
    public void InsertIntoDocument()
    {
        var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
        string alias = SelectedLayout?.Alias ?? MarkSmith.Core.Glox.SmartArtLayoutSuggester.Suggest(ast) ?? "list";
        var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias);
        if (pkg == null)
        {
            StatusMessage = "Insert error: the selected layout is not available.";
            return;
        }

        string inner = (MarkdownText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(inner))
        {
            StatusMessage = "Insert error: build a hierarchy first.";
            return;
        }

        var block = new StringBuilder();
        // Leading/trailing blank lines: the preview + DOCX block extractors require the marker to
        // sit on its own paragraph, so the block stays valid wherever the caret is.
        block.AppendLine();
        block.AppendLine($":::smartart type=\"{alias}\"");
        block.AppendLine(inner);
        block.AppendLine(":::");
        InsertToDocumentRequested?.Invoke(this, block.ToString());
        // Built-in packages have an empty title ("✓ Added  to the document"); use the gallery's name.
        string name = SelectedLayout?.DisplayName ?? (string.IsNullOrWhiteSpace(pkg.Title) ? StudioLayoutItem.Humanize(alias) : pkg.Title);
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
}
