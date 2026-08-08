using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Core.AST;
using MarkSmith.Core.Generator;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using MarkSmith.Core.Solver;

namespace MarkSmith.ViewModels.SmartArtStudio;

public class StudioLayoutItem
{
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public partial class StudioNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty]
    private string _text = "Node";

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private int _level;

    [ObservableProperty]
    private bool _isSelected;

    public string ParentId { get; set; } = string.Empty;
    public ObservableCollection<StudioNodeViewModel> Children { get; } = new();
}

public partial class StudioConnectorViewModel : ObservableObject
{
    [ObservableProperty]
    private double _x1;

    [ObservableProperty]
    private double _y1;

    [ObservableProperty]
    private double _x2;

    [ObservableProperty]
    private double _y2;
}

/// <summary>
/// SmartArt Design Studio — canvas-first authoring of the canonical AST with a live
/// Word-fidelity pipeline. Brand-new UI; reuses the engine (catalog/solver/generator).
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

    [ObservableProperty]
    private ObservableCollection<StudioNodeViewModel> _displayNodes = new();

    [ObservableProperty]
    private ObservableCollection<StudioConnectorViewModel> _connectors = new();

    [ObservableProperty]
    private string _statusMessage = "Ready";

    private readonly List<StudioLayoutItem> _allLayouts = new();

    public event EventHandler? PreviewHtmlChanged;

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
                       string.Equals(l.Alias, layoutAlias, StringComparison.OrdinalIgnoreCase))
                   ?? Layouts.FirstOrDefault();
        if (item is not null) SelectedLayout = item; // triggers UpdatePreview
    }
    partial void OnSelectedLayoutChanged(StudioLayoutItem? value) => UpdatePreview();
    partial void OnSearchQueryChanged(string value) => FilterLayouts();

    private void LoadLayouts()
    {
        _allLayouts.Clear();
        var catalog = SmartArtLayoutCatalog.Shared;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] aliases = { "hierarchy", "orgchart", "process", "cycle", "matrix",
                             "pyramid", "venn", "picturelist", "relationship", "list" };
        foreach (var alias in aliases)
        {
            var pkg = catalog.TryResolve(alias);
            if (pkg == null || !seen.Add(pkg.UniqueId)) continue;
            _allLayouts.Add(new StudioLayoutItem
            {
                Name = string.IsNullOrWhiteSpace(pkg.Title) ? Tail(pkg.UniqueId) : pkg.Title,
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

    // ---- structure canvas ----

    public void RebuildTree()
    {
        RootNodes.Clear();
        DisplayNodes.Clear();
        Connectors.Clear();

        var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
        var dataNodes = ast.Root.Children.Count > 0 ? ast.Root.Children : new List<AstNode> { ast.Root };

        foreach (var n in dataNodes)
        {
            var root = ToViewModel(n, "root");
            RootNodes.Add(root);
        }

        LayoutTree();
        FlattenInto(RootNodes);
    }

    private StudioNodeViewModel ToViewModel(AstNode node, string parentId)
    {
        var vm = new StudioNodeViewModel
        {
            Text = node.Text,
            ParentId = parentId
        };
        foreach (var child in node.Children)
        {
            vm.Children.Add(ToViewModel(child, vm.Id));
        }
        return vm;
    }

    private void LayoutTree()
    {
        foreach (var root in RootNodes)
        {
            Position(root, 0, 0);
        }
    }

    private double Position(StudioNodeViewModel node, int level, double startX)
    {
        node.Level = level;
        node.Y = 30 + level * 110;
        if (node.Children.Count == 0)
        {
            node.X = startX;
            return startX + 170;
        }
        double cursor = startX;
        foreach (var child in node.Children)
        {
            cursor = Position(child, level + 1, cursor);
        }
        node.X = (node.Children[0].X + node.Children[^1].X) / 2;
        return cursor;
    }

    private void FlattenInto(IEnumerable<StudioNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            DisplayNodes.Add(node);
            foreach (var child in node.Children)
            {
                Connectors.Add(new StudioConnectorViewModel
                {
                    X1 = node.X + 65, Y1 = node.Y + 40,
                    X2 = child.X + 65, Y2 = child.Y
                });
            }
            FlattenInto(node.Children);
        }
    }

    [RelayCommand]
    public void AddRootNode()
    {
        var node = new StudioNodeViewModel { Text = "New Node", X = 30, Y = 30 };
        RootNodes.Add(node);
        SyncTreeToMarkdown();
    }

    [RelayCommand]
    public void DeleteSelectedNode()
    {
        var selected = DisplayNodes.FirstOrDefault(n => n.IsSelected);
        if (selected == null) return;
        if (selected.ParentId == "root")
        {
            var root = RootNodes.FirstOrDefault(r => r.Id == selected.Id);
            if (root != null) RootNodes.Remove(root);
        }
        else
        {
            var parent = DisplayNodes.FirstOrDefault(n => n.Id == selected.ParentId);
            parent?.Children.Remove(selected);
        }
        SyncTreeToMarkdown();
    }

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
        sb.AppendLine($"{new string(' ', depth * 2)}- {node.Text}");
        foreach (var child in node.Children)
        {
            AppendMarkdown(sb, child, depth + 1);
        }
    }

    // ---- preview ----

    public void UpdatePreview()
    {
        try
        {
            var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
            string alias = SelectedLayout?.Alias ?? "hierarchy";
            string title = SelectedLayout?.Name ?? "Hierarchy Layout";

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
    public void ExportDocx()
    {
        try
        {
            var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
            string alias = SelectedLayout?.Alias ?? "hierarchy";
            var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias) ?? SmartArtLayoutCatalog.Shared.TryResolve("default");
            if (pkg == null) { StatusMessage = "Export error: no layout."; return; }

            var solved = new ConstraintSolver().Solve(ast, pkg);
            var genRes = new OpenXmlDiagramGenerator().Generate(solved, pkg);
            string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"SmartArt_{alias}_{DateTime.Now:HHmmss}.docx");
            DocxPackageWriter.WriteDocx(outPath, genRes);
            StatusMessage = $"✓ Exported native SmartArt ({pkg.UniqueId}) → {outPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
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
