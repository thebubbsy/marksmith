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
using MarkSmith.Core.Glox.Builder;
using MarkSmith.Core.Glox.Packager;
using MarkSmith.Core.Preview;

namespace MdToPdf.ViewModels.SmartArt;

public partial class SmartArtDesignerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _markdownText = "- Executive Board\n  - CEO\n  - CTO\n    - Engineering Team\n  - CFO";

    [ObservableProperty]
    private string _jsonDefinitionText = "{\n  \"Title\": \"My Custom SmartArt Layout\",\n  \"Category\": \"hierarchy\",\n  \"Shape\": \"roundRect\"\n}";

    [ObservableProperty]
    private string _previewHtml = "";

    [ObservableProperty]
    private bool _isWordFidelityMode = false;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private int _selectedCategoryIndex = 0;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ObservableCollection<GloxLayoutItem> _availableLayouts = new();

    [ObservableProperty]
    private GloxLayoutItem? _selectedLayout;

    [ObservableProperty]
    private ObservableCollection<SmartArtPaletteItem> _paletteItems = new();

    [ObservableProperty]
    private ObservableCollection<SmartArtCanvasNodeViewModel> _canvasNodes = new();

    private readonly List<GloxLayoutItem> _allLayouts = new();

    public event EventHandler? PreviewHtmlChanged;

    public SmartArtDesignerViewModel()
    {
        LoadLayouts();
        LoadPaletteItems();
        LoadCanvasNodesFromMarkdown();
        UpdatePreviewHtml();
    }

    partial void OnMarkdownTextChanged(string value) => UpdatePreviewHtml();
    partial void OnSelectedLayoutChanged(GloxLayoutItem? value) => UpdatePreviewHtml();
    partial void OnIsWordFidelityModeChanged(bool value) => UpdatePreviewHtml();
    partial void OnSearchQueryChanged(string value) => FilterLayouts();
    partial void OnSelectedCategoryIndexChanged(int value) => FilterLayouts();

    public void SyncCanvasToMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var node in CanvasNodes)
        {
            sb.AppendLine($"- {node.Text}");
            foreach (var child in node.Children)
            {
                sb.AppendLine($"  - {child.Text}");
            }
        }

        string generatedMd = sb.ToString().TrimEnd();
        if (!string.Equals(MarkdownText, generatedMd, StringComparison.Ordinal))
        {
            MarkdownText = generatedMd;
        }
        else
        {
            UpdatePreviewHtml();
        }
    }

    private void LoadCanvasNodesFromMarkdown()
    {
        CanvasNodes.Clear();
        var ast = MarkdownAstParser.Parse(MarkdownText);
        double startX = 60, startY = 60;
        int i = 0;

        foreach (var node in ast.Root.Children)
        {
            var canvasNode = new SmartArtCanvasNodeViewModel
            {
                Text = node.Text,
                X = startX + (i * 160),
                Y = startY + ((i % 2) * 40),
                Width = 130,
                Height = 60,
                ShapeType = "roundRect",
                Color = "#0078d4"
            };

            foreach (var child in node.Children)
            {
                canvasNode.Children.Add(new SmartArtCanvasNodeViewModel
                {
                    Text = child.Text,
                    ShapeType = "roundRect",
                    Color = "#107c41"
                });
            }

            CanvasNodes.Add(canvasNode);
            i++;
        }
    }

    public void UpdatePreviewHtml()
    {
        try
        {
            var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
            string alias = SelectedLayout?.Alias ?? "hierarchy";
            string title = SelectedLayout?.Name ?? "Hierarchy Layout";

            if (IsWordFidelityMode)
            {
                PreviewHtml = WordFidelityPreviewEngine.RenderWordFidelitySnapshot(ast, alias, title);
            }
            else
            {
                PreviewHtml = HtmlPreviewRenderer.RenderHtml(ast, alias, title);
            }

            StatusMessage = $"Rendered {alias} layout successfully.";
            PreviewHtmlChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview Error: {ex.Message}";
            PreviewHtml = $"<div style='color:#ef4444;background:#2b1420;padding:16px;border-radius:6px;font-family:monospace;'>Diagnostic Error: {WebUtility.HtmlEncode(ex.Message)}<br/><pre>{WebUtility.HtmlEncode(ex.StackTrace)}</pre></div>";
            PreviewHtmlChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LoadPaletteItems()
    {
        PaletteItems.Clear();
        // Basic primitives
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Rectangle", ShapeType = "rect", Category = "Basic", DefaultText = "Process Step", Color = "#0078d4", Tooltip = "Standard rectangular container" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Rounded Rect", ShapeType = "roundRect", Category = "Basic", DefaultText = "Rounded Node", Color = "#107c41", Tooltip = "Rounded edge shape node" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Circle", ShapeType = "circle", Category = "Basic", DefaultText = "Cycle Node", Color = "#d13438", Tooltip = "Circular node primitive" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Decision Diamond", ShapeType = "diamond", Category = "Basic", DefaultText = "Decision", Color = "#ff8c00", Tooltip = "Decision branching node" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Hexagon", ShapeType = "hexagon", Category = "Basic", DefaultText = "Hex Block", Color = "#5c2d91", Tooltip = "Hexagonal cluster node" });

        // SmartArt primitives
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Hierarchy Node", ShapeType = "hierarchy", Category = "SmartArt", DefaultText = "Manager", Color = "#0078d4", Tooltip = "Tree hierarchy node" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Process Arrow", ShapeType = "process", Category = "SmartArt", DefaultText = "Step 1", Color = "#008272", Tooltip = "Linear process flow block" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Cycle Loop", ShapeType = "cycle", Category = "SmartArt", DefaultText = "Phase 1", Color = "#d13438", Tooltip = "Circular repeating cycle" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Matrix Quadrant", ShapeType = "matrix", Category = "SmartArt", DefaultText = "Q1 High", Color = "#5c2d91", Tooltip = "2x2 grid matrix quadrant" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Pyramid Tier", ShapeType = "pyramid", Category = "SmartArt", DefaultText = "Foundation", Color = "#ff8c00", Tooltip = "Pyramid layer block" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Venn Circle", ShapeType = "venn", Category = "SmartArt", DefaultText = "Overlapping Set", Color = "#0078d4", Tooltip = "Venn diagram intersection set" });

        // Special primitives
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Picture Node", ShapeType = "picture", Category = "Special", DefaultText = "Image Card", Color = "#107c41", Tooltip = "Node with embedded image asset" });
        PaletteItems.Add(new SmartArtPaletteItem { DisplayName = "Mosaic Grid Node", ShapeType = "mosaic", Category = "Special", DefaultText = "Mosaic Cell", Color = "#008272", Tooltip = "Raster mosaic element" });
    }

    private void LoadLayouts()
    {
        _allLayouts.Clear();
        _allLayouts.Add(new GloxLayoutItem { Name = "Hierarchy Layout", Alias = "hierarchy", Category = "Hierarchy" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Horizontal Org Chart", Alias = "orgchart", Category = "Hierarchy" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Basic Process", Alias = "process", Category = "Process" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Step Process", Alias = "step_process", Category = "Process" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Basic Cycle", Alias = "cycle", Category = "Cycle" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Multidirectional Cycle", Alias = "multi_cycle", Category = "Cycle" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Basic Matrix", Alias = "matrix", Category = "Matrix" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Grid Matrix", Alias = "grid_matrix", Category = "Matrix" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Basic Pyramid", Alias = "pyramid", Category = "Pyramid" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Basic Venn", Alias = "venn", Category = "Venn" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Linear Process Target", Alias = "target", Category = "Process" });
        _allLayouts.Add(new GloxLayoutItem { Name = "Picture Accent List", Alias = "picturelist", Category = "Picture List" });

        FilterLayouts();
    }

    private void FilterLayouts()
    {
        AvailableLayouts.Clear();
        string q = (SearchQuery ?? "").Trim().ToLower();

        string targetCategory = SelectedCategoryIndex switch
        {
            1 => "Hierarchy",
            2 => "Process",
            3 => "Cycle",
            4 => "Matrix",
            5 => "Pyramid",
            6 => "Venn",
            7 => "Picture List",
            _ => "All"
        };

        foreach (var item in _allLayouts)
        {
            if (targetCategory != "All" && !item.Category.Equals(targetCategory, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(q) && !item.Name.ToLower().Contains(q) && !item.Alias.ToLower().Contains(q))
                continue;

            AvailableLayouts.Add(item);
        }

        if (SelectedLayout == null || !AvailableLayouts.Contains(SelectedLayout))
        {
            SelectedLayout = AvailableLayouts.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void ExportGlox()
    {
        try
        {
            var def = JsonLayoutParser.Parse(JsonDefinitionText);
            var xml = GloxXmlSerializer.Serialize(def);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outputPath = Path.Combine(desktop, $"{def.Title.Replace(" ", "_")}.glox");
            GloxPackager.Package(xml, outputPath);
            StatusMessage = $"✓ Successfully exported GLOX package to: {outputPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export GLOX Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ExportDocx()
    {
        try
        {
            var ast = MarkdownAstParser.Parse(MarkdownText ?? "");
            string alias = SelectedLayout?.Alias ?? "hierarchy";
            string title = SelectedLayout?.Name ?? "Hierarchy Layout";

            var gloxPkg = new GloxPackage
            {
                UniqueId = $"urn:microsoft.com/office/officeart/2005/8/layout/{alias}",
                Title = title,
                Category = SelectedLayout?.Category ?? "hierarchy",
                LayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>"
            };

            var solver = new MarkSmith.Core.Solver.ConstraintSolver();
            var solved = solver.Solve(ast, gloxPkg);

            var generator = new OpenXmlDiagramGenerator();
            var genRes = generator.Generate(solved, gloxPkg);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outputPath = Path.Combine(desktop, $"Exported_{alias}_SmartArt.docx");

            DocxPackageWriter.WriteDocx(outputPath, genRes);
            StatusMessage = $"✓ Successfully exported native DOCX diagram to: {outputPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export DOCX Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RenderDocxFidelity()
    {
        IsWordFidelityMode = !IsWordFidelityMode;
    }
}
