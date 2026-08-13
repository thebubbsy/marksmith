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
    private ObservableCollection<StudioLayoutItem> _layouts = new();

    [ObservableProperty]
    private StudioLayoutItem? _selectedLayout;

    [ObservableProperty]
    private ObservableCollection<StudioNodeViewModel> _rootNodes = new();

    /// <summary>Depth-first flatten of the tree — the rows the outline editor binds to.</summary>
    [ObservableProperty]
    private ObservableCollection<StudioNodeViewModel> _outlineRows = new();

    [ObservableProperty]
    private string _statusMessage = "Ready";

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
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public SmartArtDesignStudioViewModel()
    {
        LoadLayouts();
        RebuildTree();
        UpdatePreview();
    }

    partial void OnMarkdownTextChanged(string value) { RebuildTree(); UpdatePreview(); }

    /// <summary>Preloads pasted content into the studio with the suggested layout family
    /// pre-selected (the family→layout mapping is a small reviewable table, not 176 detectors —
    /// the user still picks the exact layout from the 176-layout gallery).</summary>
    public void Preload(string markdown, string layoutAlias)
    {
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
                Category = GuessCategory(alias, pkg)
            });
        }
        FilterLayouts();
    }

    private void FilterLayouts()
    {
        Layouts.Clear();
        string q = (SearchQuery ?? "").Trim().ToLowerInvariant();
        foreach (var item in _allLayouts)
        {
            if (!string.IsNullOrEmpty(q) && !item.Name.ToLower().Contains(q) && !item.Alias.ToLower().Contains(q))
                continue;
            Layouts.Add(item);
        }
        if (SelectedLayout == null || !Layouts.Contains(SelectedLayout))
            SelectedLayout = Layouts.FirstOrDefault();
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
            string title = SelectedLayout?.Name
                ?? MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.TryResolve(alias)?.Title
                ?? "SmartArt Layout";

            PreviewHtml = HtmlPreviewRenderer.RenderHtml(ast, alias, title);
            StatusMessage = $"Preview: {title} ({alias})";
            PreviewHtmlChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview error: {ex.Message}";
        }
    }

    [RelayCommand]
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
        StatusMessage = $"✓ Added {pkg.Title} to the document — preview & export it there.";
    }

    private static string Tail(string urn)
    {
        int idx = urn.LastIndexOf('/');
        return idx >= 0 ? urn[(idx + 1)..] : urn;
    }

    private static string GuessCategory(string hint, GloxPackage pkg)
    {
        string combined = $"{hint} {pkg.Category} {pkg.UniqueId}".ToLowerInvariant();
        if (combined.Contains("hier") || combined.Contains("org")) return "Hierarchy";
        if (combined.Contains("process") || combined.Contains("workflow")) return "Process";
        if (combined.Contains("cycle")) return "Cycle";
        if (combined.Contains("matrix") || combined.Contains("grid")) return "Matrix";
        if (combined.Contains("pyramid")) return "Pyramid";
        if (combined.Contains("venn")) return "Venn";
        if (combined.Contains("picture")) return "Picture List";
        if (combined.Contains("relationship")) return "Relationship";
        return "List";
    }
}
