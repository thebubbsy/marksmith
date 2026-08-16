using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Core.Composer;
using MarkSmith.Models.MindMap;
using MarkSmith.Services.MindMap;

namespace MarkSmith.ViewModels.MindMap
{
    public sealed partial class MindMapStudioViewModel : ObservableObject
    {
        private readonly MindMapStorageService _storageService = new();
        private readonly MindMapAutoLinker _autoLinker = new();
        private readonly MindMapLayoutEngine _layoutEngine = new();
        private readonly MindMapDocxExporter _docxExporter = new();

        public MindMapDocument Document { get; private set; } = new();

        [ObservableProperty]
        private string _title = "Document Galaxy";

        [ObservableProperty]
        private double _zoomLevel = 1.0;

        [ObservableProperty]
        private double _viewportOffsetX = 0;

        [ObservableProperty]
        private double _viewportOffsetY = 0;

        [ObservableProperty]
        private MindMapNodeViewModel? _selectedNode;

        [ObservableProperty]
        private MindMapLinkViewModel? _selectedLink;

        [ObservableProperty]
        private string _statusMessage = "Ready — select a document node, link projects, or auto-layout your galaxy.";

        [ObservableProperty]
        private string _searchQuery = "";

        [ObservableProperty]
        private bool _isPreviewCardVisible;

        [ObservableProperty]
        private string _previewTitle = "";

        [ObservableProperty]
        private string _previewMarkdown = "";

        [ObservableProperty]
        private string _previewFilePath = "";

        [ObservableProperty]
        private string _selectedThemeName = "Midnight Galaxy";

        public ObservableCollection<MindMapNodeViewModel> Nodes { get; } = new();
        public ObservableCollection<MindMapLinkViewModel> Links { get; } = new();

        public IReadOnlyList<string> AvailableThemes { get; } = new[]
        {
            "Midnight Galaxy",
            "Clean White",
            "Nordic Slate",
            "Obsidian Dark",
            "Cyberpunk Neon"
        };

        public IReadOnlyList<string> PaletteColors { get; } = new[]
        {
            "#FF7C4D", // Orange
            "#22D3EE", // Cyan
            "#34D399", // Green
            "#3B82F6", // Blue
            "#A855F7", // Purple
            "#EC4899", // Rose
            "#FBBF24", // Yellow
            "#E11D48"  // Crimson
        };

        public event EventHandler<string>? OpenDocumentRequested;
        public event EventHandler? CanvasRedrawRequested;

        public MindMapStudioViewModel()
        {
            LoadDefaultGalaxy();
        }

        public void LoadDocument(MindMapDocument doc)
        {
            Document = doc;
            Title = doc.Title;
            ZoomLevel = doc.ZoomLevel > 0 ? doc.ZoomLevel : 1.0;
            ViewportOffsetX = doc.ViewportOffsetX;
            ViewportOffsetY = doc.ViewportOffsetY;

            Nodes.Clear();
            foreach (var n in doc.Nodes)
            {
                Nodes.Add(new MindMapNodeViewModel(n));
            }

            Links.Clear();
            foreach (var l in doc.Links)
            {
                Links.Add(new MindMapLinkViewModel(l));
            }

            SelectedNode = Nodes.FirstOrDefault();
            StatusMessage = $"Loaded galaxy with {Nodes.Count} nodes and {Links.Count} cross-links.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        public void LoadDefaultGalaxy()
        {
            var defaultDoc = MindMapStorageService.CreateDefaultGalaxy();
            LoadDocument(defaultDoc);
        }

        [RelayCommand]
        public void AddChildNode()
        {
            var parent = SelectedNode ?? Nodes.FirstOrDefault();
            if (parent == null) return;

            string[] colors = PaletteColors.ToArray();
            int colorIdx = Nodes.Count % colors.Length;

            var childModel = new MindMapNode
            {
                Title = "New Sub-Project / Document",
                NodeType = MindMapNodeType.Document,
                X = parent.X + parent.Width + 180,
                Y = parent.Y + (parent.Model.ChildIds.Count * 70),
                Width = 180,
                Height = 56,
                ColorHex = colors[colorIdx],
                Icon = "📄",
                Progress = 0,
                ParentId = parent.Id,
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd"),
                ModifiedDate = DateTime.Now.ToString("yyyy-MM-dd")
            };

            parent.Model.ChildIds.Add(childModel.Id);
            Document.Nodes.Add(childModel);

            var childVm = new MindMapNodeViewModel(childModel);
            Nodes.Add(childVm);
            SelectedNode = childVm;
            StatusMessage = $"Added child node under '{parent.Title}'.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void AddSiblingNode()
        {
            var sel = SelectedNode;
            if (sel == null || string.IsNullOrEmpty(sel.ParentId))
            {
                AddChildNode();
                return;
            }

            var parent = Nodes.FirstOrDefault(n => n.Id == sel.ParentId);
            if (parent == null) return;

            string[] colors = PaletteColors.ToArray();
            int colorIdx = Nodes.Count % colors.Length;

            var siblingModel = new MindMapNode
            {
                Title = "New Linked Project",
                NodeType = MindMapNodeType.Document,
                X = sel.X,
                Y = sel.Y + sel.Height + 24,
                Width = 180,
                Height = 56,
                ColorHex = colors[colorIdx],
                Icon = "📄",
                Progress = 0,
                ParentId = parent.Id,
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd"),
                ModifiedDate = DateTime.Now.ToString("yyyy-MM-dd")
            };

            parent.Model.ChildIds.Add(siblingModel.Id);
            Document.Nodes.Add(siblingModel);

            var siblingVm = new MindMapNodeViewModel(siblingModel);
            Nodes.Add(siblingVm);
            SelectedNode = siblingVm;
            StatusMessage = $"Added sibling node under '{parent.Title}'.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void DeleteSelection()
        {
            if (SelectedNode != null)
            {
                var target = SelectedNode;
                if (target.Id == Document.RootNodeId && Nodes.Count > 1)
                {
                    StatusMessage = "Cannot delete root project node.";
                    return;
                }

                // Remove links attached to this node
                var attachedLinks = Links.Where(l => l.SourceNodeId == target.Id || l.TargetNodeId == target.Id).ToList();
                foreach (var l in attachedLinks)
                {
                    Links.Remove(l);
                    Document.Links.Remove(l.Model);
                }

                // Remove from parent's children
                if (!string.IsNullOrEmpty(target.ParentId))
                {
                    var parent = Nodes.FirstOrDefault(n => n.Id == target.ParentId);
                    parent?.Model.ChildIds.Remove(target.Id);
                }

                Nodes.Remove(target);
                Document.Nodes.Remove(target.Model);
                SelectedNode = Nodes.FirstOrDefault();
                StatusMessage = $"Removed node '{target.Title}'.";
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (SelectedLink != null)
            {
                var l = SelectedLink;
                Links.Remove(l);
                Document.Links.Remove(l.Model);
                SelectedLink = null;
                StatusMessage = "Removed cross-link.";
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ConnectNodes(string sourceId, string targetId, string? label = null)
        {
            if (sourceId == targetId) return;

            var existing = Links.FirstOrDefault(l => (l.SourceNodeId == sourceId && l.TargetNodeId == targetId) ||
                                                     (l.SourceNodeId == targetId && l.TargetNodeId == sourceId));
            if (existing != null)
            {
                StatusMessage = "Connection already exists between these nodes.";
                return;
            }

            var src = Nodes.FirstOrDefault(n => n.Id == sourceId);
            var linkModel = new MindMapLink
            {
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                Label = label ?? "linked project",
                ColorHex = src?.ColorHex ?? "#7C4DFF",
                Style = MindMapLinkStyle.CurvedBezier,
                Direction = MindMapLinkDirection.SourceToTarget
            };

            Document.Links.Add(linkModel);
            var linkVm = new MindMapLinkViewModel(linkModel);
            Links.Add(linkVm);
            SelectedLink = linkVm;
            StatusMessage = $"Connected nodes with relationship '{linkModel.Label}'.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void ApplyLayout(string layoutName)
        {
            SyncAllToModel();

            var layoutType = layoutName.ToLowerInvariant() switch
            {
                "radial" or "galaxy" => MindMapLayoutType.RadialGalaxy,
                "force" or "physics" => MindMapLayoutType.ForceDirected,
                "vertical" or "hierarchy" => MindMapLayoutType.VerticalHierarchy,
                _ => MindMapLayoutType.HorizontalTree
            };

            _layoutEngine.ApplyLayout(Document, layoutType);

            // Re-sync coordinates to ViewModels
            foreach (var vm in Nodes)
            {
                var m = Document.Nodes.FirstOrDefault(n => n.Id == vm.Id);
                if (m != null)
                {
                    vm.X = m.X;
                    vm.Y = m.Y;
                }
            }

            StatusMessage = $"Applied {layoutName} layout.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public async Task ImportDirectoryAsync(string directoryPath)
        {
            StatusMessage = $"Scanning and auto-linking directory '{directoryPath}'…";
            var doc = await _autoLinker.BuildGalaxyFromDirectoryAsync(directoryPath);
            _layoutEngine.ApplyLayout(doc, MindMapLayoutType.HorizontalTree);
            LoadDocument(doc);
        }

        [RelayCommand]
        public void ExportToDocx(string outputFilePath)
        {
            SyncAllToModel();
            _docxExporter.ExportToDocx(Document, outputFilePath);
            StatusMessage = $"✓ Exported editable Word Document Galaxy to: {outputFilePath}";
        }

        [RelayCommand]
        public async Task SaveAsync(string? filePath = null)
        {
            SyncAllToModel();
            string path = filePath ?? MindMapStorageService.GetDefaultLibraryStoragePath();
            await _storageService.SaveAsync(Document, path);
            StatusMessage = $"✓ Saved galaxy to {Path.GetFileName(path)}.";
        }

        [RelayCommand]
        public void OpenLinkedDocument(MindMapNodeViewModel? node)
        {
            var target = node ?? SelectedNode;
            if (target == null) return;

            if (!string.IsNullOrEmpty(target.FilePath) && File.Exists(target.FilePath))
            {
                OpenDocumentRequested?.Invoke(this, target.FilePath);
                StatusMessage = $"Opening '{Path.GetFileName(target.FilePath)}' in MarkSmith…";
            }
            else if (!string.IsNullOrEmpty(target.MarkdownContent))
            {
                ShowPreviewCard(target);
            }
            else
            {
                StatusMessage = $"Node '{target.Title}' has no associated file on disk.";
            }
        }

        public void ShowPreviewCard(MindMapNodeViewModel node)
        {
            PreviewTitle = node.Title;
            PreviewFilePath = node.FilePath ?? "Standalone Project Note";
            PreviewMarkdown = node.MarkdownContent ?? $"# {node.Title}\n\n*No markdown notes attached.*";
            IsPreviewCardVisible = true;
        }

        public void HidePreviewCard()
        {
            IsPreviewCardVisible = false;
        }

        public void RecolorSelectedNode(string hex)
        {
            if (SelectedNode != null)
            {
                SelectedNode.ColorHex = hex;
                SelectedNode.SyncToModel();
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SyncAllToModel()
        {
            Document.Title = Title;
            Document.ZoomLevel = ZoomLevel;
            Document.ViewportOffsetX = ViewportOffsetX;
            Document.ViewportOffsetY = ViewportOffsetY;

            foreach (var n in Nodes) n.SyncToModel();
            foreach (var l in Links) l.SyncToModel();
        }
    }
}
