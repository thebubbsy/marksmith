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
        public string NodesCountText => $"{Nodes.Count} {(Nodes.Count == 1 ? "node" : "nodes")}";
        public string LinksCountText => $"{Links.Count} {(Links.Count == 1 ? "link" : "links")}";

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
        private string _statusMessage = "Select a node to edit it, or drag the background to look around.";

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

        /// <summary>Names the step Undo would take back ("Undo: Rename node (Ctrl+Z)"), so the
        /// button says what it will do before you press it.</summary>
        public string UndoToolTip => _undo.Count > 0 ? $"Undo: {_undo.Peek().Label} (Ctrl+Z)" : "Nothing to undo (Ctrl+Z)";
        public string RedoToolTip => _redo.Count > 0 ? $"Redo: {_redo.Peek().Label} (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)";

        /// <summary>A node or a link is selected: what Delete acts on.</summary>
        public bool HasSelection => SelectedNode != null || SelectedLink != null;

        /// <summary>"3 matches" beside the search box. The bare count it showed read as a zoom
        /// level or a badge, not as a result.</summary>
        public string SearchMatchText => SearchMatchCount switch
        {
            0 => "No matches",
            1 => "1 match",
            var n => $"{n} matches"
        };

        partial void OnSearchMatchCountChanged(int value) => OnPropertyChanged(nameof(SearchMatchText));
        public bool HasTags => DistinctTags.Count > 0;
        /// <summary>True when the galaxy has no nodes at all — drives the canvas empty state.</summary>
        public bool IsGalaxyEmpty => Nodes.Count == 0;
        public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

        public ObservableCollection<string> DistinctTags { get; } = new();

        partial void OnSearchQueryChanged(string value)
        {
            _searchCursor = -1;
            OnPropertyChanged(nameof(HasSearchQuery));
            ApplyFilterAndSearch();
        }

        partial void OnSelectedTagFilterChanged(string? value) => ApplyFilterAndSearch();
        partial void OnIsFocusModeEnabledChanged(bool value)
        {
            // The toolbar toggle binds straight to this, so the status line is said here rather
            // than in the command.
            StatusMessage = !value
                ? "Focus is off. Showing the whole map."
                : SelectedNode != null
                    ? $"Focus is on. Showing '{SelectedNode.Title}' and what it connects to."
                    : "Focus is on. Select a node to see only what it connects to.";
            ApplyFilterAndSearch();
        }

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
            OnPropertyChanged(nameof(HasSelection));
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
            OnPropertyChanged(nameof(HasSelection));
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

        /// <summary>The node the preview card is showing. Hovering shows a card for any node, not
        /// just the selection, so the card's buttons must act on this one: "Open in editor" used to
        /// open the selected node while the card described a different one.</summary>
        [ObservableProperty]
        private MindMapNodeViewModel? _previewNode;

        /// <summary>True when the previewed node points at a file, so it has something to open
        /// and a version history to show.</summary>
        public bool PreviewHasFile => !string.IsNullOrWhiteSpace(PreviewNode?.FilePath);

        partial void OnPreviewNodeChanged(MindMapNodeViewModel? value) => OnPropertyChanged(nameof(PreviewHasFile));

        public const string DefaultThemeName = "Midnight Galaxy";

        [ObservableProperty]
        private string _selectedThemeName = DefaultThemeName;

        /// <summary>The theme is part of the map: it is saved with it, restored when it opens, and
        /// undone like any other change. It used to live only in the combo box, so every map
        /// reopened as Midnight Galaxy whatever had been chosen.</summary>
        partial void OnSelectedThemeNameChanging(string value)
        {
            if (_loadingDocument || value == SelectedThemeName) return;
            PushUndo("Change appearance");
        }

        partial void OnSelectedThemeNameChanged(string value)
        {
            if (_loadingDocument) return;
            Document.Theme ??= new MindMapTheme();
            Document.Theme.Name = value;
            MarkDirty($"Switched the map to {value}.");
        }

        /// <summary>Set while a document is being loaded or restored, so restoring its theme and
        /// fields isn't recorded as an edit.</summary>
        private bool _loadingDocument;

        /// <summary>The names the theme picker offers; anything else in a file falls back to the
        /// default rather than leaving the picker blank.</summary>
        private string ThemeNameFor(MindMapDocument doc) =>
            doc.Theme?.Name is { } name && AvailableThemes.Contains(name) ? name : DefaultThemeName;

        /// <summary>Raised when a command creates a node the user will want to name straight away.
        /// The window puts the caret in the inspector's title box, the way any outliner does.</summary>
        public event EventHandler<MindMapNodeViewModel>? NodeCreated;

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
            OnPropertyChanged(nameof(IsGalaxyEmpty));
            TrackCollectionChange(e, _trackedNodes);
        }

        private void OnLinkCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(LinksCountText));
            TrackCollectionChange(e, _trackedLinks);
        }

        // ---- Inspector edits ----
        //
        // The inspector binds straight to the node and link view models. Nothing used to notice
        // those edits: renaming a node left the old title on its card until something else
        // redrew the canvas, the window never showed "unsaved", closing threw the edit away, and
        // Undo skipped over it to whatever structural change came before.

        private static readonly HashSet<string> TrackedNodeProperties = new(StringComparer.Ordinal)
        {
            nameof(MindMapNodeViewModel.Title),
            nameof(MindMapNodeViewModel.FilePath),
            nameof(MindMapNodeViewModel.NodeType),
            nameof(MindMapNodeViewModel.Icon),
            nameof(MindMapNodeViewModel.Progress),
            nameof(MindMapNodeViewModel.MarkdownContent),
            nameof(MindMapNodeViewModel.TagsText),
        };

        private static readonly HashSet<string> TrackedLinkProperties = new(StringComparer.Ordinal)
        {
            nameof(MindMapLinkViewModel.Label),
        };

        private readonly HashSet<MindMapNodeViewModel> _trackedNodes = new();
        private readonly HashSet<MindMapLinkViewModel> _trackedLinks = new();
        private int _editTrackingSuspended;

        /// <summary>The field whose undo step is still open. Typing a title is one undo step, not
        /// one per keystroke; any other undoable change closes it.</summary>
        private string? _openEditKey;

        private void TrackCollectionChange<T>(NotifyCollectionChangedEventArgs e, HashSet<T> tracked)
            where T : ObservableObject
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // Clear() reports no old items, so the set is what knows what to unhook.
                foreach (var item in tracked) Unhook(item);
                tracked.Clear();
            }
            if (e.OldItems != null)
            {
                foreach (T item in e.OldItems)
                {
                    if (tracked.Remove(item)) Unhook(item);
                }
            }
            if (e.NewItems != null)
            {
                foreach (T item in e.NewItems)
                {
                    if (!tracked.Add(item)) continue;
                    item.PropertyChanging += OnTrackedPropertyChanging;
                    item.PropertyChanged += OnTrackedPropertyChanged;
                }
            }
        }

        private void Unhook(ObservableObject item)
        {
            item.PropertyChanging -= OnTrackedPropertyChanging;
            item.PropertyChanged -= OnTrackedPropertyChanged;
        }

        private static bool IsTracked(object? sender, string? property) => sender switch
        {
            MindMapNodeViewModel => property != null && TrackedNodeProperties.Contains(property),
            MindMapLinkViewModel => property != null && TrackedLinkProperties.Contains(property),
            _ => false
        };

        private static string EditKey(object sender, string property) => sender switch
        {
            MindMapNodeViewModel n => $"node:{n.Id}:{property}",
            MindMapLinkViewModel l => $"link:{l.Id}:{property}",
            _ => property
        };

        /// <summary>Snapshots the map *before* the value changes, which is what makes the edit
        /// undoable at all: by PropertyChanged the old value is gone.</summary>
        private void OnTrackedPropertyChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
        {
            if (_editTrackingSuspended > 0 || _suppressUndoCapture || sender == null || !IsTracked(sender, e.PropertyName)) return;

            string key = EditKey(sender, e.PropertyName!);
            if (key == _openEditKey) return;

            PushUndo(e.PropertyName switch
            {
                nameof(MindMapNodeViewModel.Title) => "Rename node",
                nameof(MindMapNodeViewModel.FilePath) => "Change linked file",
                nameof(MindMapNodeViewModel.NodeType) => "Change node type",
                nameof(MindMapNodeViewModel.Icon) => "Change icon",
                nameof(MindMapNodeViewModel.Progress) => "Change progress",
                nameof(MindMapNodeViewModel.MarkdownContent) => "Edit notes",
                nameof(MindMapNodeViewModel.TagsText) => "Edit tags",
                nameof(MindMapLinkViewModel.Label) => "Rename relationship",
                _ => "Edit"
            });
            _openEditKey = key;
        }

        private void OnTrackedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_editTrackingSuspended > 0 || _suppressUndoCapture || !IsTracked(sender, e.PropertyName)) return;

            if (sender is MindMapNodeViewModel node)
            {
                node.SyncToModel();
                switch (e.PropertyName)
                {
                    case nameof(MindMapNodeViewModel.Title):
                        OnPropertyChanged(nameof(SelectedLinkDescription));
                        if (PreviewNode == node) PreviewTitle = node.Title;
                        break;
                    case nameof(MindMapNodeViewModel.TagsText):
                        RefreshDistinctTags();
                        ApplyFilterAndSearch();
                        break;
                    case nameof(MindMapNodeViewModel.FilePath):
                        if (PreviewNode == node) ShowPreviewCard(node);
                        OnPropertyChanged(nameof(PreviewHasFile));
                        break;
                    case nameof(MindMapNodeViewModel.MarkdownContent):
                        if (PreviewNode == node && IsPreviewCardVisible) ShowPreviewCard(node);
                        break;
                }
                // Insights count linked files and words, so they follow these edits too.
                MarkDirty();
            }
            else if (sender is MindMapLinkViewModel link)
            {
                link.SyncToModel();
                IsDirty = true;
            }

            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Changes made by a command rather than typed into the inspector. The command
        /// records its own undo step, so these mustn't add a second one.</summary>
        private IDisposable SuspendEditTracking()
        {
            _editTrackingSuspended++;
            return new Resume(() => _editTrackingSuspended--);
        }

        private sealed class Resume(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose()
            {
                _onDispose?.Invoke();
                _onDispose = null;
            }
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
                StatusMessage = $"Couldn't open your map ({ex.Message}). Showing the guided tour instead.";
                LoadTutorialGalaxy();
                return;
            }

            LoadDocument(result.Document);
            _undo.Clear();
            _redo.Clear();
            RaiseUndoState();
            MarkClean();

            if (result.LoadError != null)
            {
                StatusMessage = result.LoadError;
            }
            else if (result.IsFirstRun)
            {
                StatusMessage = "This is a guided tour. Start with ① on the left, then import a folder to map your own documents.";
            }
            else
            {
                string repairs = result.Repairs.Summarize();
                StatusMessage = $"Opened '{Document.Title}'. {InsightsSummary}" + (repairs.Length > 0 ? $" {repairs}" : "");
            }

            await RefreshFileStatesAsync();
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
            RestoreTheme(doc);

            RebuildViewModels();

            SelectedLink = null;
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? Nodes.FirstOrDefault();
            HidePreviewCard();
            RefreshInsights();
            StatusMessage = $"Loaded {Plural(Nodes.Count, "node")} and {Plural(Links.Count, "link")}.";
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RestoreTheme(MindMapDocument doc)
        {
            _loadingDocument = true;
            try { SelectedThemeName = ThemeNameFor(doc); }
            finally { _loadingDocument = false; }
        }

        internal static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

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

        /// <summary>
        /// Probes every linked file once, off the UI thread, and pushes the result onto the node
        /// view models. Doing it here means the canvas never has to touch the disk while drawing.
        /// </summary>
        public async Task RefreshFileStatesAsync()
        {
            // Probe off the UI thread, but set the results back on the caller's: the inspector binds
            // to IsFileMissing, and a change notification raised from a pool thread is not one
            // WinUI can deliver.
            var nodes = Nodes.ToList();
            var missing = await Task.Run(() => nodes.Select(n => n.ProbeFileMissing()).ToArray());
            for (int i = 0; i < nodes.Count; i++) nodes[i].IsFileMissing = missing[i];
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
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
                    Title = "My vault",
                    NodeType = MindMapNodeType.Project,
                    Width = 220,
                    Height = 62,
                    ColorHex = "#FF7C4D",
                    Icon = "\uEC07",
                    MarkdownContent = "# My vault\n\nImport a folder, or start adding documents."
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
                MarkClean();
                StatusMessage = $"Saved the map to {Path.GetFileName(path)}.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save to {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        // ---- Undo / redo ----

        private sealed record UndoEntry(MindMapDocument Snapshot, string Label, string? SelectedNodeId)
        {
            /// <summary>Identifies the document state the entry restores. Undo and Redo pass it along
            /// when they move an entry between the stacks, so the saved state is recognised again.</summary>
            public long Id { get; init; } = System.Threading.Interlocked.Increment(ref _nextUndoId);
        }

        private static long _nextUndoId;

        /// <summary>The undo step on top of the stack when the map was last saved (0: none). Undoing
        /// back to it clears "Unsaved" again, the way an editor does, instead of a map that has
        /// been put back exactly as it was still claiming unsaved changes.</summary>
        private long _cleanUndoId;

        private long TopUndoId => _undo.Count > 0 ? _undo.Peek().Id : 0;

        private void MarkClean()
        {
            _openEditKey = null;
            _cleanUndoId = TopUndoId;
            IsDirty = false;
        }

        /// <summary>Captures the current document before a mutation. Call this first in any command
        /// that changes structure.</summary>
        public void PushUndo(string label)
        {
            if (_suppressUndoCapture) return;

            _openEditKey = null;
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
            _redo.Push(new UndoEntry(MindMapGraph.DeepCopy(Document), entry.Label, SelectedNode?.Id) { Id = entry.Id });
            RestoreSnapshot(entry);
            StatusMessage = $"Undid: {entry.Label}";
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
            _undo.Push(new UndoEntry(MindMapGraph.DeepCopy(Document), entry.Label, SelectedNode?.Id) { Id = entry.Id });
            RestoreSnapshot(entry);
            StatusMessage = $"Redid: {entry.Label}";
        }

        private void RestoreSnapshot(UndoEntry entry)
        {
            _openEditKey = null;
            _suppressUndoCapture = true;
            try
            {
                Document = entry.Snapshot;
                Title = Document.Title;
                IsTutorialActive = Document.IsTutorial;
                RestoreTheme(Document);
                RebuildViewModels();
                HidePreviewCard();
                SelectedLink = null;
                SelectedNode = Nodes.FirstOrDefault(n => n.Id == entry.SelectedNodeId) ?? Nodes.FirstOrDefault();
                RefreshInsights();
            }
            finally
            {
                _suppressUndoCapture = false;
            }
            IsDirty = TopUndoId != _cleanUndoId;
            RaiseUndoState();
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseUndoState()
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoToolTip));
            OnPropertyChanged(nameof(RedoToolTip));
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

        /// <summary>
        /// Finds the node standing for <paramref name="filePath"/>, selects it and puts it under the
        /// camera. This is the Galaxy half of the hub-and-spoke round trip: opening a document in
        /// the editor should light its star up, not leave the map parked wherever it last was.
        /// Returns null when no node points at that file.
        /// </summary>
        public MindMapNodeViewModel? RevealDocument(string? filePath)
        {
            var match = FindNodeForFile(filePath);
            if (match == null) return null;

            SelectedLink = null;
            SelectedNode = match;
            CenterOn(match);
            return match;
        }

        /// <summary>The node whose linked file is <paramref name="filePath"/>, or null.</summary>
        public MindMapNodeViewModel? FindNodeForFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            return Nodes.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.FilePath)
                                             && PathsEqual(n.FilePath!, filePath!));
        }

        /// <summary>
        /// The node for <paramref name="filePath"/>, adding one when the galaxy has never seen that
        /// file. Lets the editor push whatever is open into the map, instead of making the user go
        /// find the same file again from inside the Galaxy.
        /// </summary>
        public MindMapNodeViewModel EnsureDocumentNode(string filePath, string? title = null)
        {
            var existing = FindNodeForFile(filePath);
            if (existing != null) return existing;

            AddRootNodeCore(announce: false); // pushes undo, drops a node at the centre of the viewport and selects it
            var node = SelectedNode!;
            using (SuspendEditTracking())
            {
                node.FilePath = filePath;
                node.Title = !string.IsNullOrWhiteSpace(title)
                    ? title!
                    : Path.GetFileNameWithoutExtension(filePath);
            }
            node.SyncToModel();

            MarkDirty($"Added '{node.Title}' to the galaxy — link it to give it meaning.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            return node;
        }

        /// <summary>Compares two paths the way Windows does, tolerating a path that cannot be
        /// canonicalised (a node can hold a path that is no longer valid).</summary>
        private static bool PathsEqual(string a, string b)
        {
            static string Canonical(string p)
            {
                try { return Path.GetFullPath(p); }
                catch { return p; }
            }
            return string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
        }

        [RelayCommand]
        public void ToggleFocusMode()
        {
            IsFocusModeEnabled = !IsFocusModeEnabled;
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
                Title = NewNodeTitle,
                NodeType = MindMapNodeType.Document,
                X = parent.X + parent.Width + 200,
                Y = NextFreeChildY(parent),
                Width = 190,
                Height = 56,
                ColorHex = NextPaletteColor(),
                Icon = "\uE8A5",
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
            MarkDirty($"Added a node under '{parent.Title}'. Type to name it.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            NodeCreated?.Invoke(this, childVm);
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
                Title = NewNodeTitle,
                NodeType = MindMapNodeType.Document,
                X = sel.X,
                Y = sel.Y + sel.Height + 24,
                Width = sel.Width,
                Height = 56,
                ColorHex = NextPaletteColor(),
                Icon = "\uE8A5",
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
            MarkDirty($"Added a node beside '{sel.Title}'. Type to name it.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            NodeCreated?.Invoke(this, siblingVm);
        }

        [RelayCommand]
        public void AddRootNode() => AddRootNodeCore(announce: true);

        private void AddRootNodeCore(bool announce)
        {
            PushUndo("Add node");

            var model = new MindMapNode
            {
                Title = NewNodeTitle,
                NodeType = MindMapNodeType.Document,
                X = -ViewportOffsetX - 95,
                Y = -ViewportOffsetY - 28,
                Width = 190,
                Height = 56,
                ColorHex = NextPaletteColor(),
                Icon = "\uE8A5",
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd"),
                ModifiedDate = DateTime.Now.ToString("yyyy-MM-dd")
            };

            Document.Nodes.Add(model);
            if (string.IsNullOrEmpty(Document.RootNodeId)) Document.RootNodeId = model.Id;

            var vm = new MindMapNodeViewModel(model);
            Nodes.Add(vm);
            SelectedNode = vm;
            MarkDirty("Added a node. Type to name it, then link it to the documents it belongs with.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            if (announce) NodeCreated?.Invoke(this, vm);
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
                MarkDirty("Deleted the link.");
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
                StatusMessage = $"'{target.Title}' is the map's root. Delete or move the nodes under it first.";
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
                ? $"Deleted '{target.Title}'. {Plural(adopted, "node")} under it moved up a level."
                : $"Deleted '{target.Title}'.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        public MindMapLinkViewModel? ConnectNodes(string sourceId, string targetId, string? label = null)
        {
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId) || sourceId == targetId)
            {
                StatusMessage = "A node can't be linked to itself.";
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
                MarkDirty($"These were already linked. The relationship is now '{existing.DisplayLabel}'.");
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
                StatusMessage = "A node can't move under one of its own branches.";
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
                : $"'{node.Title}' no longer has a parent.");
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

            MarkDirty($"Arranged the map as {LayoutDisplayName(layoutType)}.");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public async Task ImportDirectoryAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                StatusMessage = $"'{directoryPath}' isn't a folder MarkSmith can read.";
                return;
            }

            StatusMessage = $"Reading '{Path.GetFileName(directoryPath)}' and linking what's in it…";
            try
            {
                var doc = await _autoLinker.BuildGalaxyFromDirectoryAsync(directoryPath);
                _layoutEngine.ApplyLayout(doc, MindMapLayoutType.HorizontalTree);

                PushUndo("Import vault");
                LoadDocument(doc);
                MarkDirty($"Mapped '{doc.Title}'. {InsightsSummary} Save to keep it.");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Couldn't map that folder: {ex.Message}";
            }
        }

        /// <summary>Re-scans the folder this map was built from, keeping the current one on failure.</summary>
        [RelayCommand]
        public async Task RescanSourceDirectoryAsync()
        {
            if (string.IsNullOrWhiteSpace(Document.SourceDirectory))
            {
                StatusMessage = "This map wasn't made from a folder. Use Import folder first.";
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
                StatusMessage = MarkSmith.Models.ProGate.StatusLine(MarkSmith.Models.FeatureId.DocxExport, AppServices.License.State);
                return;
            }
            SyncAllToModel();
            try
            {
                _docxExporter.ExportToDocx(Document, outputFilePath);
            }
            catch (Exception ex)
            {
                StatusMessage = MarkSmith.Services.ExportFailureMessage.Describe("Word document", ex, outputFilePath);
                return;
            }
            if (AppServices.License.State.Edition == Models.Edition.Trial)
                AppServices.License.ConsumeDocxExport();
            StatusMessage = $"Exported the map to {Path.GetFileName(outputFilePath)}.";
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
                StatusMessage = $"Opened '{Path.GetFileName(target.FilePath)}' in the editor.";
            }
            else if (!string.IsNullOrEmpty(target.FilePath))
            {
                StatusMessage = $"'{target.FileName}' is no longer on disk. Browse in the inspector to point the node at it again.";
                ShowPreviewCard(target);
            }
            else
            {
                StatusMessage = $"'{target.Title}' has no file attached. Browse in the inspector to attach one.";
                ShowPreviewCard(target);
            }
        }

        public void ShowPreviewCard(MindMapNodeViewModel node)
        {
            if (node == null) return;
            PreviewNode = node;
            PreviewTitle = node.Title;
            PreviewFilePath = string.IsNullOrWhiteSpace(node.FilePath) ? "No file attached" : node.FileName;
            PreviewMarkdown = string.IsNullOrWhiteSpace(node.MarkdownContent)
                ? "No notes yet. Select the node and write some under Notes in the inspector."
                : Excerpt(node.MarkdownContent!, PreviewCharacterBudget);
            IsPreviewCardVisible = true;
        }

        /// <summary>How much of a document the hover card shows. An imported node can hold 20k
        /// characters of source, and re-laying out that much text every time the pointer crosses a
        /// card was a visible stutter on its own.</summary>
        private const int PreviewCharacterBudget = 1500;

        private static string Excerpt(string text, int budget)
        {
            if (text.Length <= budget) return text;
            // Prefer a paragraph break so the card doesn't end mid-sentence.
            int cut = text.LastIndexOf("\n\n", budget, StringComparison.Ordinal);
            if (cut < budget / 2) cut = budget;
            return text[..cut].TrimEnd() + "\n\n… open the document to read the rest.";
        }

        public void HidePreviewCard()
        {
            IsPreviewCardVisible = false;
            PreviewNode = null;
        }

        public void RecolorSelectedNode(string hex)
        {
            if (SelectedNode == null) return;
            PushUndo("Recolour node");
            SelectedNode.ColorHex = MindMapGraph.NormalizeHex(hex, SelectedNode.ColorHex);
            SelectedNode.SyncToModel();
            MarkDirty($"Recoloured '{SelectedNode.Title}'.");
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

        /// <summary>What a node is called until the user names it. The window selects this text in
        /// the title box, so typing replaces it.</summary>
        public const string NewNodeTitle = "New document";

        /// <summary>The layout's name as the Auto-layout menu shows it, for the status line. It
        /// used to print the enum ("Applied HorizontalTree layout.").</summary>
        public static string LayoutDisplayName(MindMapLayoutType type) => type switch
        {
            MindMapLayoutType.RadialGalaxy => "a radial map",
            MindMapLayoutType.ForceDirected => "a force-directed web",
            MindMapLayoutType.VerticalHierarchy => "a top-down hierarchy",
            MindMapLayoutType.ConstellationClusters => "clusters",
            _ => "a left-to-right tree"
        };

        /// <summary>Flips a relationship's arrow. The canvas menu and the inspector button both
        /// come here; the menu's copy used to skip marking the map unsaved.</summary>
        public void ReverseLink(MindMapLinkViewModel? link)
        {
            if (link == null) return;
            PushUndo("Reverse link");
            link.ReverseDirection();
            link.SyncToModel();
            OnPropertyChanged(nameof(SelectedLinkDescription));
            MarkDirty(link.Direction switch
            {
                MindMapLinkDirection.Bidirectional => "The link now points both ways.",
                MindMapLinkDirection.None => "The link no longer has a direction.",
                _ => "Reversed the link."
            });
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Nodes <paramref name="node"/> can be moved under: never itself or anything in
        /// its own branch (the move would be refused), alphabetical so a long map is searchable.</summary>
        public IReadOnlyList<MindMapNodeViewModel> ReparentCandidates(MindMapNodeViewModel node) =>
            Nodes.Where(n => n.Id != node.Id && !IsDescendant(n.Id, node.Id))
                 .OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                 .ToList();

        /// <summary>Points a node at a file picked from disk, naming it after the file if it still
        /// has a placeholder title. One undo step for both changes.</summary>
        public void AttachFile(MindMapNodeViewModel node, string path)
        {
            PushUndo("Attach file");
            using (SuspendEditTracking())
            {
                node.FilePath = path;
                if (string.IsNullOrWhiteSpace(node.Title)
                    || node.Title == NewNodeTitle
                    || node.Title.StartsWith("New ", StringComparison.Ordinal))
                {
                    node.Title = Path.GetFileNameWithoutExtension(path);
                }
            }
            node.SyncToModel();
            OnPropertyChanged(nameof(PreviewHasFile));
            MarkDirty($"Attached '{Path.GetFileName(path)}' to '{node.Title}'.");
            _ = node.RefreshVersionHistoryAsync(AppServices.VersionHistory);
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Writes the map to another file without making that file "the" map: Save still
        /// goes to the library, and the unsaved marker stays until it does. The Export menu's old
        /// "Save map file" cleared the marker, so closing then lost the library's changes.</summary>
        public async Task SaveCopyAsync(string filePath)
        {
            SyncAllToModel();
            var copy = MindMapGraph.DeepCopy(Document);
            copy.IsTutorial = false;
            try
            {
                await _storageService.SaveAsync(copy, filePath);
                StatusMessage = $"Saved a copy to {Path.GetFileName(filePath)}.";
            }
            catch (Exception ex)
            {
                StatusMessage = MarkSmith.Services.ExportFailureMessage.Describe("Map copy", ex, filePath);
            }
        }

        public void SyncAllToModel()
        {
            Document.Title = Title;
            Document.ZoomLevel = ZoomLevel;
            Document.ViewportOffsetX = ViewportOffsetX;
            Document.ViewportOffsetY = ViewportOffsetY;
            Document.Theme ??= new MindMapTheme();
            Document.Theme.Name = SelectedThemeName;

            foreach (var n in Nodes) n.SyncToModel();
            foreach (var l in Links) l.SyncToModel();
        }
    }
}
