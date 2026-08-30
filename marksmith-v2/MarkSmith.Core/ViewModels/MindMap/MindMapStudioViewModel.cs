using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

        // Undo works on whole-document snapshots. The maps this feature is built for are hundreds
        // of nodes, not hundreds of thousands, so a snapshot is a few hundred KB and the simplicity
        // is worth far more than a command-diff scheme nobody can reason about.
        private const int MaxUndoDepth = 60;
        private readonly Stack<UndoEntry> _undo = new();
        private readonly Stack<UndoEntry> _redo = new();
        private bool _suppressUndoCapture;

        public MindMapDocument Document { get; private set; } = new();

        [ObservableProperty]
        private string _title = "Document Galaxy";

        [ObservableProperty]
        private double _zoomLevel = 1.0;

        public string ZoomLevelText => $"{(int)Math.Round(ZoomLevel * 100)}%";
        public string NodesCountText => $"{Nodes.Count} {(Nodes.Count == 1 ? "Node" : "Nodes")}";
        public string LinksCountText => $"{Links.Count} {(Links.Count == 1 ? "Cross-Link" : "Cross-Links")}";

        partial void OnZoomLevelChanged(double value)
        {
            OnPropertyChanged(nameof(ZoomLevelText));
        }

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
        private string? _selectedTagFilter;

        [ObservableProperty]
        private bool _isDirty;

        /// <summary>True while the on-screen map is the generated first-run tour. Drives the
        /// "this is a demo" banner and the Clear-the-tour action.</summary>
        [ObservableProperty]
        private bool _isTutorialActive;

        /// <summary>Dim everything that is not connected to the selection, so a single document's
        /// constellation stands out of a dense vault.</summary>
        [ObservableProperty]
        private bool _isFocusModeEnabled;

        [ObservableProperty]
        private string _insightsSummary = "";

        [ObservableProperty]
        private int _searchMatchCount;

        public bool HasSelectedNode => SelectedNode != null;
        public bool HasSelectedLink => SelectedLink != null;

        /// <summary>Reads the selected edge back as a sentence — "A → B" — because a selected line
        /// on a canvas gives no clue which two documents it actually joins.</summary>
        public string SelectedLinkDescription
        {
            get
            {
                var link = SelectedLink;
                if (link == null) return "";
                string from = Nodes.FirstOrDefault(n => n.Id == link.SourceNodeId)?.Title ?? "?";
                string to = Nodes.FirstOrDefault(n => n.Id == link.TargetNodeId)?.Title ?? "?";
                string arrow = link.Direction switch
                {
                    MindMapLinkDirection.Bidirectional => "↔",
                    MindMapLinkDirection.TargetToSource => "←",
                    MindMapLinkDirection.None => "—",
                    _ => "→"
                };
                return $"{from}  {arrow}  {to}";
            }
        }
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool HasTags => DistinctTags.Count > 0;
        public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

        public ObservableCollection<string> DistinctTags { get; } = new();

        partial void OnSearchQueryChanged(string value)
        {
            _searchCursor = -1;
            OnPropertyChanged(nameof(HasSearchQuery));
            ApplyFilterAndSearch();
        }

        partial void OnSelectedTagFilterChanged(string? value) => ApplyFilterAndSearch();
        partial void OnIsFocusModeEnabledChanged(bool value) => ApplyFilterAndSearch();

        partial void OnSelectedNodeChanged(MindMapNodeViewModel? oldValue, MindMapNodeViewModel? newValue)
        {
            if (oldValue != null) oldValue.IsSelected = false;
            if (newValue != null)
            {
                newValue.IsSelected = true;
                // A node and a link can't both be "the selection" — Delete used to remove whichever
                // of the two had been set most recently, which was rarely the one highlighted.
                if (SelectedLink != null) SelectedLink = null;
            }
            OnPropertyChanged(nameof(HasSelectedNode));
            ApplyFilterAndSearch();
        }

        partial void OnSelectedLinkChanged(MindMapLinkViewModel? oldValue, MindMapLinkViewModel? newValue)
        {
            if (oldValue != null) oldValue.IsSelected = false;
            if (newValue != null)
            {
                newValue.IsSelected = true;
                if (SelectedNode != null) SelectedNode = null;
            }
            OnPropertyChanged(nameof(HasSelectedLink));
            OnPropertyChanged(nameof(SelectedLinkDescription));
        }

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

        public IReadOnlyList<MindMapNodeType> AvailableNodeTypes { get; } =
            Enum.GetValues<MindMapNodeType>().ToArray();

        public event EventHandler<string>? OpenDocumentRequested;
        public event EventHandler? CanvasRedrawRequested;

        public MindMapStudioViewModel()
        {
            Nodes.CollectionChanged += OnNodeCollectionChanged;
            Links.CollectionChanged += OnLinkCollectionChanged;
            LoadTutorialGalaxy();
        }

        private void OnNodeCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // These read off Nodes/Links rather than a backing field, so nothing raised change
            // notification for them and the status bar counters sat frozen at their startup values
            // for the life of the window.
            OnPropertyChanged(nameof(NodesCountText));
        }

        private void OnLinkCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(LinksCountText));
        }

        // ---- Loading & persistence ----

        /// <summary>
        /// Opens the user's saved galaxy, falling back to the guided tour only on a genuine first
        /// run. Nothing used to call Load at all: the studio built the sample map in its
        /// constructor every single time, so a saved library was written to disk and then never
        /// read back, and the demo content was all anyone ever saw.
        /// </summary>
        public async Task InitializeAsync(string? filePath = null)
        {
            string path = filePath ?? MindMapStorageService.GetDefaultLibraryStoragePath();
            MindMapLoadResult result;
            try
            {
                result = await _storageService.LoadWithReportAsync(path);
            }
            catch (Exception ex)
            {
                StatusMessage = $"⚠ Could not open your galaxy ({ex.Message}). Showing the guided tour instead.";
                LoadTutorialGalaxy();
                return;
            }

            LoadDocument(result.Document);
            _undo.Clear();
            _redo.Clear();
            RaiseUndoState();
            IsDirty = false;

            if (result.LoadError != null)
            {
                StatusMessage = "⚠ " + result.LoadError;
            }
            else if (result.IsFirstRun)
            {
                StatusMessage = "👋 Welcome — this is a guided tour. Open ① to see why this replaces folders, then import your own vault.";
            }
            else
            {
                string repairs = result.Repairs.Summarize();
                StatusMessage = $"Loaded '{Document.Title}' — {InsightsSummary}" + (repairs.Length > 0 ? $" · {repairs}" : "");
            }
        }

        public void LoadDocument(MindMapDocument doc)
        {
            MindMapGraph.Normalize(doc);

            Document = doc;
            Title = doc.Title;
            ZoomLevel = doc.ZoomLevel > 0 ? doc.ZoomLevel : 1.0;
            ViewportOffsetX = doc.ViewportOffsetX;
            ViewportOffsetY = doc.ViewportOffsetY;
            IsTutorialActive = doc.IsTutorial;

            RebuildViewModels();

            SelectedLink = null;
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? Nodes.FirstOrDefault();
            RefreshInsights();
            StatusMessage = $"Loaded galaxy with {Nodes.Count} nodes and {Links.Count} cross-links.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RebuildViewModels()
        {
            Nodes.Clear();
            foreach (var n in Document.Nodes) Nodes.Add(new MindMapNodeViewModel(n));

            Links.Clear();
            foreach (var l in Document.Links) Links.Add(new MindMapLinkViewModel(l));

            RefreshDistinctTags();
            RefreshConnectionCounts();
            ApplyFilterAndSearch();
        }

        /// <summary>Recomputes each node's edge count so the canvas can weight hubs. Cheap enough
        /// to run on every structural change, and it is the only thing that keeps a card's
        /// "connections" badge honest after a link is added or deleted.</summary>
        public void RefreshConnectionCounts()
        {
            var counts = new Dictionary<string, int>(Nodes.Count, StringComparer.Ordinal);
            foreach (var n in Nodes) counts[n.Id] = 0;

            void Bump(string? id)
            {
                if (id != null && counts.ContainsKey(id)) counts[id]++;
            }

            foreach (var n in Nodes)
            {
                if (n.ParentId != null && counts.ContainsKey(n.ParentId))
                {
                    Bump(n.Id);
                    Bump(n.ParentId);
                }
            }
            foreach (var l in Links)
            {
                Bump(l.SourceNodeId);
                Bump(l.TargetNodeId);
            }

            foreach (var n in Nodes) n.ConnectionCount = counts[n.Id];
        }

        public void LoadTutorialGalaxy()
        {
            LoadDocument(MindMapStorageService.CreateTutorialGalaxy());
            IsDirty = false;
        }

        /// <summary>Kept for compatibility with callers that expect the old name.</summary>
        public void LoadDefaultGalaxy() => LoadTutorialGalaxy();

        /// <summary>
        /// Removes exactly the generated tour nodes and leaves anything the user added, so
        /// "clear the tour" after experimenting does not throw away their first real node.
        /// </summary>
        [RelayCommand]
        public void ClearTutorial()
        {
            if (!Document.Nodes.Any(n => n.IsTutorial))
            {
                StatusMessage = "Nothing to clear — this galaxy is all yours.";
                return;
            }

            PushUndo("Clear guided tour");
            SyncAllToModel();

            var doomed = Document.Nodes.Where(n => n.IsTutorial).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            Document.Nodes.RemoveAll(n => doomed.Contains(n.Id));
            Document.Links.RemoveAll(l => doomed.Contains(l.SourceNodeId) || doomed.Contains(l.TargetNodeId));

            if (Document.Nodes.Count == 0)
            {
                var fresh = new MindMapNode
                {
                    Title = "My Vault",
                    NodeType = MindMapNodeType.Project,
                    Width = 220,
                    Height = 62,
                    ColorHex = "#FF7C4D",
                    Icon = "🌌",
                    MarkdownContent = "# My Vault\n\nImport a folder, or start adding documents."
                };
                Document.Nodes.Add(fresh);
                Document.RootNodeId = fresh.Id;
            }

            Document.IsTutorial = false;
            IsTutorialActive = false;
            MindMapGraph.Normalize(Document);
            RebuildViewModels();
            SelectedNode = Nodes.FirstOrDefault();
            MarkDirty("Cleared the guided tour — the galaxy is yours now.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public async Task SaveAsync(string? filePath = null)
        {
            SyncAllToModel();

            // Saving is what turns the tour into "the user's map"; keeping the flag would make the
            // studio offer to clear their own content on the next launch.
            Document.IsTutorial = false;
            IsTutorialActive = false;

            string path = filePath ?? MindMapStorageService.GetDefaultLibraryStoragePath();
            try
            {
                await _storageService.SaveAsync(Document, path);
                IsDirty = false;
                StatusMessage = $"✓ Saved galaxy to {Path.GetFileName(path)} · {InsightsSummary}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"⚠ Could not save to {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        // ---- Undo / redo ----

        private sealed record UndoEntry(MindMapDocument Snapshot, string Label, string? SelectedNodeId);

        /// <summary>Captures the current document before a mutation. Call this first in any command
        /// that changes structure.</summary>
        public void PushUndo(string label)
        {
            if (_suppressUndoCapture) return;

            SyncAllToModel();
            _undo.Push(new UndoEntry(MindMapGraph.DeepCopy(Document), label, SelectedNode?.Id));
            if (_undo.Count > MaxUndoDepth)
            {
                var kept = _undo.ToArray().Take(MaxUndoDepth).Reverse().ToArray();
                _undo.Clear();
                foreach (var e in kept) _undo.Push(e);
            }
            _redo.Clear();
            RaiseUndoState();
        }

        [RelayCommand]
        public void Undo()
        {
            if (_undo.Count == 0)
            {
                StatusMessage = "Nothing to undo.";
                return;
            }

            SyncAllToModel();
            var entry = _undo.Pop();
            _redo.Push(new UndoEntry(MindMapGraph.DeepCopy(Document), entry.Label, SelectedNode?.Id));
            RestoreSnapshot(entry);
            StatusMessage = $"↩ Undid: {entry.Label}";
        }

        [RelayCommand]
        public void Redo()
        {
            if (_redo.Count == 0)
            {
                StatusMessage = "Nothing to redo.";
                return;
            }

            SyncAllToModel();
            var entry = _redo.Pop();
            _undo.Push(new UndoEntry(MindMapGraph.DeepCopy(Document), entry.Label, SelectedNode?.Id));
            RestoreSnapshot(entry);
            StatusMessage = $"↪ Redid: {entry.Label}";
        }

        private void RestoreSnapshot(UndoEntry entry)
        {
            _suppressUndoCapture = true;
            try
            {
                Document = entry.Snapshot;
                Title = Document.Title;
                IsTutorialActive = Document.IsTutorial;
                RebuildViewModels();
                SelectedLink = null;
                SelectedNode = Nodes.FirstOrDefault(n => n.Id == entry.SelectedNodeId) ?? Nodes.FirstOrDefault();
                RefreshInsights();
                IsDirty = true;
            }
            finally
            {
                _suppressUndoCapture = false;
            }
            RaiseUndoState();
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseUndoState()
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        private void MarkDirty(string? status = null)
        {
            IsDirty = true;
            if (status != null) StatusMessage = status;
            RefreshInsights();
        }

        // ---- Tags, search & focus ----

        public void RefreshDistinctTags()
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in Nodes)
            {
                foreach (var t in n.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim());
                }
            }

            DistinctTags.Clear();
            foreach (var tag in tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                DistinctTags.Add(tag);
            }
            OnPropertyChanged(nameof(HasTags));
        }

        private int _searchCursor = -1;

        /// <summary>
        /// Recomputes dimming. Three independent reasons a node can be de-emphasised — it misses
        /// the search, it misses the tag filter, or focus mode is on and it is not in the
        /// selection's neighbourhood — are resolved here in one place so they compose instead of
        /// fighting each other.
        /// </summary>
        public void ApplyFilterAndSearch()
        {
            var q = SearchQuery?.Trim();
            var tag = SelectedTagFilter;
            bool hasSearch = !string.IsNullOrEmpty(q);
            bool hasTag = !string.IsNullOrEmpty(tag);

            HashSet<string>? focusSet = null;
            if (IsFocusModeEnabled && SelectedNode != null)
            {
                focusSet = MindMapGraph.NeighborsOf(Document, SelectedNode.Id);
                focusSet.Add(SelectedNode.Id);
            }

            int matches = 0;
            foreach (var n in Nodes)
            {
                bool matchesSearch = !hasSearch || NodeMatches(n, q!);
                bool matchesTag = !hasTag || n.Tags.Any(t => MindMapGraph.TagEquals(t, tag));
                bool inFocus = focusSet == null || focusSet.Contains(n.Id);

                bool visible = matchesSearch && matchesTag && inFocus;
                if (hasSearch && matchesSearch && matchesTag) matches++;

                n.IsDimmed = (hasSearch || hasTag || focusSet != null) && !visible;
                n.IsHighlighted = (hasSearch || hasTag) && visible;
                n.IsNeighbor = focusSet != null && inFocus && n.Id != SelectedNode?.Id;
            }

            SearchMatchCount = hasSearch ? matches : 0;
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Search reaches the title, the notes, the tags AND the file path — looking up a
        /// document by its filename is the single most common thing to want, and the old title-only
        /// match could not do it.</summary>
        private static bool NodeMatches(MindMapNodeViewModel n, string query) =>
            n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (n.MarkdownContent?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (n.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || n.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || t.TrimStart('#').Contains(query, StringComparison.OrdinalIgnoreCase));

        /// <summary>Cycles through search hits instead of always snapping back to the first one.</summary>
        public MindMapNodeViewModel? FocusNextMatch(bool forward = true)
        {
            var q = SearchQuery?.Trim();
            if (string.IsNullOrEmpty(q)) return null;

            var matches = Nodes.Where(n => NodeMatches(n, q)).ToList();
            if (matches.Count == 0)
            {
                StatusMessage = $"No node matches '{q}'.";
                return null;
            }

            _searchCursor = forward
                ? (_searchCursor + 1) % matches.Count
                : (_searchCursor - 1 + matches.Count) % matches.Count;

            var target = matches[_searchCursor];
            SelectedNode = target;
            CenterOn(target);
            StatusMessage = $"Match {_searchCursor + 1} of {matches.Count} for '{q}' — {target.Title}";
            return target;
        }

        /// <summary>Puts a node in the middle of the viewport. Centring on the node's centre (not
        /// its top-left corner) is what makes it land under the camera rather than up and left of it.</summary>
        public void CenterOn(MindMapNodeViewModel node)
        {
            ViewportOffsetX = -(node.X + node.Width / 2.0);
            ViewportOffsetY = -(node.Y + node.Height / 2.0);
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void ToggleFocusMode()
        {
            IsFocusModeEnabled = !IsFocusModeEnabled;
            StatusMessage = IsFocusModeEnabled
                ? "🔦 Focus mode on — showing only what the selected document connects to."
                : "Focus mode off — showing the whole galaxy.";
        }

        public void RefreshInsights()
        {
            SyncAllToModel();
            InsightsSummary = MindMapGraph.Analyze(Document).HeadlineSummary();
        }

        public MindMapInsights GetInsights()
        {
            SyncAllToModel();
            return MindMapGraph.Analyze(Document);
        }

        // ---- Structural editing ----

        [RelayCommand]
        public void AddChildNode()
        {
            var parent = SelectedNode ?? Nodes.FirstOrDefault();
            if (parent == null)
            {
                AddRootNode();
                return;
            }

            PushUndo("Add child node");

            var childModel = new MindMapNode
            {
                Title = "New Sub-Project / Document",
                NodeType = MindMapNodeType.Document,
                X = parent.X + parent.Width + 200,
                Y = NextFreeChildY(parent),
                Width = 190,
                Height = 56,
                ColorHex = NextPaletteColor(),
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
            MarkDirty($"Added child node under '{parent.Title}'.");
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
            if (parent == null)
            {
                AddChildNode();
                return;
            }

            PushUndo("Add sibling node");

            var siblingModel = new MindMapNode
            {
                Title = "New Linked Project",
                NodeType = MindMapNodeType.Document,
                X = sel.X,
                Y = sel.Y + sel.Height + 24,
                Width = sel.Width,
                Height = 56,
                ColorHex = NextPaletteColor(),
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
            MarkDirty($"Added sibling node under '{parent.Title}'.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void AddRootNode()
        {
            PushUndo("Add node");

            var model = new MindMapNode
            {
                Title = "New Document Node",
                NodeType = MindMapNodeType.Document,
                X = -ViewportOffsetX - 95,
                Y = -ViewportOffsetY - 28,
                Width = 190,
                Height = 56,
                ColorHex = NextPaletteColor(),
                Icon = "📄",
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd"),
                ModifiedDate = DateTime.Now.ToString("yyyy-MM-dd")
            };

            Document.Nodes.Add(model);
            if (string.IsNullOrEmpty(Document.RootNodeId)) Document.RootNodeId = model.Id;

            var vm = new MindMapNodeViewModel(model);
            Nodes.Add(vm);
            SelectedNode = vm;
            MarkDirty("Added a free-floating node — link it to give it meaning.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void DuplicateSelectedNode()
        {
            var source = SelectedNode;
            if (source == null) return;

            PushUndo("Duplicate node");
            source.SyncToModel();

            var clone = source.Model.Clone();
            if (clone.ParentId != null)
            {
                // Clone() deliberately leaves this to the caller: without it the copy claims a
                // parent that has never heard of it, and the connector to it is never drawn.
                var parent = Document.Nodes.FirstOrDefault(n => n.Id == clone.ParentId);
                if (parent != null) parent.ChildIds.Add(clone.Id);
                else clone.ParentId = null;
            }

            Document.Nodes.Add(clone);
            var vm = new MindMapNodeViewModel(clone);
            Nodes.Add(vm);
            SelectedNode = vm;
            RefreshDistinctTags();
            MarkDirty($"Duplicated '{source.Title}'.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void DeleteSelection()
        {
            if (SelectedNode != null)
            {
                DeleteNode(SelectedNode);
            }
            else if (SelectedLink != null)
            {
                PushUndo("Delete link");
                var l = SelectedLink;
                Links.Remove(l);
                Document.Links.Remove(l.Model);
                SelectedLink = null;
                RefreshConnectionCounts();
                MarkDirty("Removed cross-link.");
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Deletes a node and adopts its children into its parent, rather than leaving them
        /// pointing at an id that no longer exists — orphans like that were still drawn but had no
        /// connector to anything, so they looked like a rendering glitch.
        /// </summary>
        public void DeleteNode(MindMapNodeViewModel target)
        {
            if (target == null) return;

            if (target.Id == Document.RootNodeId && Nodes.Count > 1)
            {
                StatusMessage = "Cannot delete the root project node — delete its children first, or pick a different root.";
                return;
            }

            PushUndo($"Delete '{target.Title}'");
            SyncAllToModel();

            var attachedLinks = Links.Where(l => l.SourceNodeId == target.Id || l.TargetNodeId == target.Id).ToList();
            foreach (var l in attachedLinks)
            {
                Links.Remove(l);
                Document.Links.Remove(l.Model);
            }

            var parentModel = target.ParentId != null
                ? Document.Nodes.FirstOrDefault(n => n.Id == target.ParentId)
                : null;

            foreach (var childId in target.Model.ChildIds.ToList())
            {
                var childModel = Document.Nodes.FirstOrDefault(n => n.Id == childId);
                if (childModel == null) continue;
                childModel.ParentId = parentModel?.Id;
                parentModel?.ChildIds.Add(childId);

                var childVm = Nodes.FirstOrDefault(n => n.Id == childId);
                if (childVm != null) childVm.ParentId = childModel.ParentId;
            }

            parentModel?.ChildIds.Remove(target.Id);

            Nodes.Remove(target);
            Document.Nodes.Remove(target.Model);

            MindMapGraph.Normalize(Document);
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == parentModel?.Id) ?? Nodes.FirstOrDefault();
            RefreshDistinctTags();
            RefreshConnectionCounts();
            int adopted = target.Model.ChildIds.Count;
            MarkDirty(adopted > 0
                ? $"Removed '{target.Title}' — its {adopted} child node(s) moved up a level."
                : $"Removed node '{target.Title}'.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        public MindMapLinkViewModel? ConnectNodes(string sourceId, string targetId, string? label = null)
        {
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId) || sourceId == targetId)
            {
                StatusMessage = "A node cannot be linked to itself.";
                return null;
            }

            var src = Nodes.FirstOrDefault(n => n.Id == sourceId);
            var tgt = Nodes.FirstOrDefault(n => n.Id == targetId);
            if (src == null || tgt == null)
            {
                StatusMessage = "Could not connect — one of those nodes is no longer in the galaxy.";
                return null;
            }

            var existing = Links.FirstOrDefault(l => (l.SourceNodeId == sourceId && l.TargetNodeId == targetId) ||
                                                     (l.SourceNodeId == targetId && l.TargetNodeId == sourceId));
            if (existing != null)
            {
                // Re-linking two nodes with a new reason is a rename, not an error — silently
                // refusing left the user with the linker's guessed label and no way to change it.
                PushUndo("Relabel link");
                if (!string.IsNullOrWhiteSpace(label)) existing.Label = label;
                existing.Kind = MindMapLinkKind.Manual;
                existing.SyncToModel();
                SelectedLink = existing;
                MarkDirty($"These are already connected — updated the relationship to '{existing.Label}'.");
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
                return existing;
            }

            PushUndo("Connect nodes");

            var linkModel = new MindMapLink
            {
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                Label = string.IsNullOrWhiteSpace(label) ? "linked project" : label.Trim(),
                ColorHex = src.ColorHex,
                Style = MindMapLinkStyle.CurvedBezier,
                Direction = MindMapLinkDirection.SourceToTarget,
                Kind = MindMapLinkKind.Manual
            };

            Document.Links.Add(linkModel);
            var linkVm = new MindMapLinkViewModel(linkModel);
            Links.Add(linkVm);
            SelectedLink = linkVm;
            RefreshConnectionCounts();
            MarkDirty($"Connected '{src.Title}' → '{tgt.Title}' as '{linkModel.Label}'.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            return linkVm;
        }

        /// <summary>Re-hangs a node under a new parent — the drag-and-drop reparent gesture.</summary>
        public bool ReparentNode(string nodeId, string? newParentId)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;
            if (newParentId == nodeId) return false;

            // Re-hanging a node under one of its own descendants would detach that whole branch
            // from the map into a self-referential ring.
            if (newParentId != null && IsDescendant(newParentId, nodeId))
            {
                StatusMessage = "Cannot move a node inside its own branch.";
                return false;
            }

            PushUndo("Move node");
            SyncAllToModel();

            var oldParent = Document.Nodes.FirstOrDefault(n => n.Id == node.ParentId);
            oldParent?.ChildIds.Remove(nodeId);

            var newParent = newParentId == null ? null : Document.Nodes.FirstOrDefault(n => n.Id == newParentId);
            node.Model.ParentId = newParent?.Id;
            node.ParentId = newParent?.Id;
            newParent?.ChildIds.Add(nodeId);

            MindMapGraph.Normalize(Document);
            MarkDirty(newParent != null
                ? $"Moved '{node.Title}' under '{newParent.Title}'."
                : $"'{node.Title}' is now a free-floating node.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private bool IsDescendant(string candidateId, string ancestorId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = candidateId;
            while (cursor != null && seen.Add(cursor))
            {
                if (cursor == ancestorId) return true;
                cursor = Document.Nodes.FirstOrDefault(n => n.Id == cursor)?.ParentId;
            }
            return false;
        }

        [RelayCommand]
        public void ApplyLayout(string layoutName)
        {
            PushUndo($"{layoutName} layout");
            SyncAllToModel();

            var layoutType = (layoutName ?? "").ToLowerInvariant() switch
            {
                "radial" or "galaxy" => MindMapLayoutType.RadialGalaxy,
                "force" or "physics" => MindMapLayoutType.ForceDirected,
                "vertical" or "hierarchy" => MindMapLayoutType.VerticalHierarchy,
                "clusters" or "constellation" => MindMapLayoutType.ConstellationClusters,
                _ => MindMapLayoutType.HorizontalTree
            };

            _layoutEngine.ApplyLayout(Document, layoutType);

            var byId = Document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            foreach (var vm in Nodes)
            {
                if (byId.TryGetValue(vm.Id, out var m))
                {
                    vm.X = m.X;
                    vm.Y = m.Y;
                }
            }

            MarkDirty($"Applied {layoutType} layout.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public async Task ImportDirectoryAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                StatusMessage = $"⚠ '{directoryPath}' is not a folder I can read.";
                return;
            }

            StatusMessage = $"Scanning and auto-linking '{Path.GetFileName(directoryPath)}'…";
            try
            {
                var doc = await _autoLinker.BuildGalaxyFromDirectoryAsync(directoryPath);
                _layoutEngine.ApplyLayout(doc, MindMapLayoutType.HorizontalTree);

                PushUndo("Import vault");
                LoadDocument(doc);
                MarkDirty($"✓ Imported '{doc.Title}' — {InsightsSummary}. Press 💾 Save to keep it.");
            }
            catch (Exception ex)
            {
                StatusMessage = $"⚠ Import failed: {ex.Message}";
            }
        }

        /// <summary>Re-scans the folder this map was built from, keeping the current one on failure.</summary>
        [RelayCommand]
        public async Task RescanSourceDirectoryAsync()
        {
            if (string.IsNullOrWhiteSpace(Document.SourceDirectory))
            {
                StatusMessage = "This galaxy wasn't imported from a folder — use 📂 Import Vault first.";
                return;
            }
            await ImportDirectoryAsync(Document.SourceDirectory);
        }

        [RelayCommand]
        public void ExportToDocx(string outputFilePath)
        {
            // Go-live licensing: the galaxy DOCX export is another entrance to DOCX generation —
            // it honors the same paywall and spends trial exports the same way (the MindMap
            // exporter builds the package directly, bypassing DocxExportService's chokepoint).
            if (!AppServices.License.CanExportDocx)
            {
                StatusMessage = "⚠ DOCX export is a MarkSmith Pro feature — start the 3-export trial or upgrade in Settings.";
                return;
            }
            SyncAllToModel();
            try
            {
                _docxExporter.ExportToDocx(Document, outputFilePath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"⚠ DOCX export failed: {ex.Message}";
                return;
            }
            if (AppServices.License.State.Edition == Models.Edition.Trial)
                AppServices.License.ConsumeDocxExport();
            StatusMessage = $"✓ Exported editable Word Document Galaxy to: {outputFilePath}";
        }

        /// <summary>Mermaid text for pasting straight into a Markdown document — the flowchart form
        /// keeps the cross-links, which is the whole point of the map.</summary>
        public string ExportToMermaid(bool asFlowchart = true)
        {
            SyncAllToModel();
            string body = asFlowchart
                ? MindMapStorageService.ExportToMermaidFlowchart(Document)
                : MindMapStorageService.ExportToMermaid(Document);
            return "```mermaid\n" + body.TrimEnd() + "\n```\n";
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
            else if (!string.IsNullOrEmpty(target.FilePath))
            {
                StatusMessage = $"⚠ '{target.FilePath}' is no longer on disk — the node still remembers it.";
                ShowPreviewCard(target);
            }
            else
            {
                ShowPreviewCard(target);
            }
        }

        public void ShowPreviewCard(MindMapNodeViewModel node)
        {
            if (node == null) return;
            PreviewTitle = node.Title;
            PreviewFilePath = node.FilePath ?? "Standalone project note";
            PreviewMarkdown = string.IsNullOrWhiteSpace(node.MarkdownContent)
                ? $"# {node.Title}\n\n*No notes attached yet.*\n\nSelect this node and type into **Markdown Summary / Notes** in the inspector to give it a memory."
                : node.MarkdownContent!;
            IsPreviewCardVisible = true;
        }

        public void HidePreviewCard()
        {
            IsPreviewCardVisible = false;
        }

        public void RecolorSelectedNode(string hex)
        {
            if (SelectedNode == null) return;
            PushUndo("Recolour node");
            SelectedNode.ColorHex = MindMapGraph.NormalizeHex(hex, SelectedNode.ColorHex);
            SelectedNode.SyncToModel();
            MarkDirty();
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        private string NextPaletteColor() => PaletteColors[Nodes.Count % PaletteColors.Count];

        /// <summary>Stacks a new child below its parent's existing children instead of on top of
        /// the first one — the old formula used the parent's child count without accounting for
        /// where those children actually sit.</summary>
        private double NextFreeChildY(MindMapNodeViewModel parent)
        {
            var siblings = Nodes.Where(n => n.ParentId == parent.Id).ToList();
            if (siblings.Count == 0) return parent.Y;
            return siblings.Max(s => s.Y + s.Height) + 24;
        }

        public void SyncAllToModel()
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
