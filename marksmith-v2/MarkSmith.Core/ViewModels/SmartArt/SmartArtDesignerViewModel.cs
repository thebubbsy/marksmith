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

namespace MarkSmith.ViewModels.SmartArt;

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

    [ObservableProperty]
    private ObservableCollection<SmartArtCanvasNodeViewModel> _childCanvasNodes = new();

    [ObservableProperty]
    private ObservableCollection<ConnectorLineViewModel> _connectors = new();

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

        RebuildCanvasGeometry();
    }

    /// <summary>Recomputes child positions + connector lines after any canvas change.</summary>
    public void RebuildCanvasGeometry()
    {
        ChildCanvasNodes.Clear();
        Connectors.Clear();

        foreach (var parent in CanvasNodes)
        {
            int n = parent.Children.Count;
            double startX = parent.X + parent.Width / 2 - ((n - 1) * 150.0) / 2;
            for (int j = 0; j < n; j++)
            {
                var child = parent.Children[j];
                child.X = startX + j * 150;
                child.Y = parent.Y + parent.Height + 70;
                child.Width = 120;
                child.Height = 50;
                ChildCanvasNodes.Add(child);
                Connectors.Add(new ConnectorLineViewModel
                {
                    X1 = parent.X + parent.Width / 2,
                    Y1 = parent.Y + parent.Height,
                    X2 = child.X + child.Width / 2,
                    Y2 = child.Y,
                    Color = parent.Color
                });
            }
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

        RebuildCanvasGeometry();
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
                PreviewHtml = RenderWordFidelitySnapshot(ast, alias, title);
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

    /// <summary>
    /// Word fidelity: actually compiles the diagram through the real embedded glox catalog
    /// into a native-SmartArt DOCX, then reports where it was written so the user can open
    /// it in Word. This is the honest fidelity path (Word itself is the only exact renderer).
    /// </summary>
    private string RenderWordFidelitySnapshot(CanonicalAst ast, string alias, string title)
    {
        try
        {
            var pkg = MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.TryResolve(alias)
                      ?? MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.TryResolve("default")
                      ?? throw new InvalidOperationException("No SmartArt layout resolved.");

            var solved = new MarkSmith.Core.Solver.ConstraintSolver().Solve(ast, pkg);
            var genRes = new OpenXmlDiagramGenerator().Generate(solved, pkg);

            string outPath = Path.Combine(Path.GetTempPath(),
                $"MarkSmith_fidelity_{alias}_{DateTime.Now:HHmmss}.docx");
            DocxPackageWriter.WriteDocx(outPath, genRes);

            StatusMessage = $"✓ Word-fidelity DOCX generated with native SmartArt ({pkg.UniqueId}) → {outPath}";

            return $@"<div class=""word-fidelity-container"" style=""width: 100%; max-width: 800px; height: 500px; background: #1a2733; border: 1px solid #2d4a63; border-radius: 8px; position: relative; overflow: hidden; font-family: system-ui, -apple-system, sans-serif; box-sizing: border-box; padding: 24px; color: #e8f1f8;"">
  <div style=""font-size: 15px; font-weight: bold; margin-bottom: 12px;"">✓ Word-Fidelity Snapshot Generated</div>
  <div style=""font-size: 13px; opacity: 0.85; margin-bottom: 8px;"">Layout: {WebUtility.HtmlEncode(title)} ({alias}) — native OOXML diagram, schema-valid.</div>
  <div style=""font-size: 13px; opacity: 0.85; margin-bottom: 8px;"">Embedded layout URN: <code style=""background: rgba(255,255,255,0.08); padding: 2px 6px; border-radius: 4px;"">{WebUtility.HtmlEncode(pkg.UniqueId)}</code></div>
  <div style=""font-size: 13px; opacity: 0.85; margin-bottom: 16px;"">DOCX written to:<br/><code style=""background: rgba(255,255,255,0.08); padding: 2px 6px; border-radius: 4px; word-break: break-all;"">{WebUtility.HtmlEncode(outPath)}</code></div>
  <div style=""font-size: 12px; opacity: 0.7;"">Open the file in Microsoft Word to preview the exact final rendering — Word is the only 100% accurate renderer.</div>
</div>";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Word-fidelity error: {ex.Message}";
            return $"<div style='color:#ef4444;background:#2b1420;padding:16px;border-radius:6px;font-family:monospace;'>Word-fidelity error: {WebUtility.HtmlEncode(ex.Message)}</div>";
        }
    }

    public void LoadLayouts()
    {
        _allLayouts.Clear();

        var catalog = MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prefer the friendly aliases so the gallery matches the resolver vocabulary.
        string[] aliases = { "hierarchy", "orgchart", "process", "cycle", "matrix",
                             "pyramid", "venn", "picturelist", "relationship", "list" };
        foreach (var alias in aliases)
        {
            var pkg = catalog.TryResolve(alias);
            if (pkg == null || !seen.Add(pkg.UniqueId)) continue;
            _allLayouts.Add(new GloxLayoutItem
            {
                Name = string.IsNullOrWhiteSpace(pkg.Title) ? TailOf(pkg.UniqueId) : pkg.Title,
                Alias = alias,
                Category = GuessCategory(alias, pkg)
            });
        }

        // Anything else registered in the catalog (imported .glox files, extra layouts).
        foreach (var pkg in catalog.All)
        {
            if (!seen.Add(pkg.UniqueId)) continue;
            _allLayouts.Add(new GloxLayoutItem
            {
                Name = string.IsNullOrWhiteSpace(pkg.Title) ? TailOf(pkg.UniqueId) : pkg.Title,
                Alias = TailOf(pkg.UniqueId),
                Category = GuessCategory(pkg.UniqueId, pkg)
            });
        }

        FilterLayouts();
    }

    private static string TailOf(string urn)
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

            // Load the real embedded .glox for this alias so Word renders the genuine
            // algorithm geometry (cycle, pyramid, matrix, ...) rather than collapsing to
            // basic blocks from an empty layout stub.
            var gloxPkg = MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.TryResolve(alias)
                ?? MarkSmith.Core.Glox.SmartArtLayoutCatalog.Shared.TryResolve("default");

            if (gloxPkg == null)
            {
                StatusMessage = "Export DOCX Error: No SmartArt layout resolved.";
                return;
            }

            var solver = new MarkSmith.Core.Solver.ConstraintSolver();
            var solved = solver.Solve(ast, gloxPkg);

            var generator = new OpenXmlDiagramGenerator();
            var genRes = generator.Generate(solved, gloxPkg);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outputPath = Path.Combine(desktop, $"Exported_{alias}_SmartArt.docx");

            DocxPackageWriter.WriteDocx(outputPath, genRes);
            StatusMessage = $"✓ Successfully exported native DOCX diagram ({gloxPkg.UniqueId}) to: {outputPath}";
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

/// <summary>A parent→child connector line drawn on the canvas.</summary>
public partial class ConnectorLineViewModel : ObservableObject
{
    [ObservableProperty]
    private double _x1;

    [ObservableProperty]
    private double _y1;

    [ObservableProperty]
    private double _x2;

    [ObservableProperty]
    private double _y2;

    [ObservableProperty]
    private string _color = "#0078d4";
}
