using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
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

        /// <summary>Returns the root node of the mind map (a node with no incoming links, or the first node).</summary>
        public MindMapNodeViewModel? GetRootNode()
        {
            if (Nodes.Count == 0) return null;
            var targetNodeIds = new HashSet<string>(Links.Select(l => l.TargetNodeId));
            return Nodes.FirstOrDefault(n => !targetNodeIds.Contains(n.Id)) ?? Nodes[0];
        }

        [RelayCommand]
        public void AddRootNode() => AddRootNodeCore(announce: true);

        private void AddRootNodeCore(bool announce) =>
            AddNodeAt(-ViewportOffsetX - 95, -ViewportOffsetY - 28, announce);

        /// <summary>Places a new standalone node at <paramref name="x"/>, <paramref name="y"/> in
        /// world coordinates, selects it, and optionally raises <see cref="NodeCreated"/> so the
        /// inspector focuses its title field immediately.</summary>
        public MindMapNodeViewModel AddNodeAt(double x, double y, bool announce = true)
        {
            PushUndo("Add node");

            var model = new MindMapNode
            {
                Title = NewNodeTitle,
                NodeType = MindMapNodeType.Document,
                X = Math.Round(x, 1),
                Y = Math.Round(y, 1),
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
            return vm;
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
                "vertical" or "hierarchy" or "topdown" => MindMapLayoutType.VerticalHierarchy,
                "bottomup" or "inverted-hierarchy" => MindMapLayoutType.BottomUpHierarchy,
                "tree-rtl" or "rtl" or "right-to-left" => MindMapLayoutType.RightToLeftTree,
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

        /// <summary>
        /// Exports the selected subtree (or the first root node if none selected) as a standalone
        /// Markdown document with YAML frontmatter metadata and structured heading levels.
        /// Cycle protection ensures circular links do not cause infinite recursion.
        /// </summary>
        public string ExportSubtreeToMarkdown(MindMapNodeViewModel? rootNode = null)
        {
            SyncAllToModel();
            var target = rootNode ?? SelectedNode ?? Nodes.FirstOrDefault();
            if (target == null) return string.Empty;

            var sb = new StringBuilder();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int count = CountSubtreeNodes(target, visited);
            visited.Clear();

            sb.AppendLine("---");
            sb.AppendLine($"title: \"{target.Title.Replace("\"", "\\\"")}\"");
            sb.AppendLine("type: mindmap_subtree");
            sb.AppendLine($"root_id: \"{target.Id}\"");
            sb.AppendLine($"exported_at: \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
            sb.AppendLine($"node_count: {count}");
            sb.AppendLine("---");
            sb.AppendLine();

            AppendSubtreeMarkdown(sb, target, 1, visited);

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private int CountSubtreeNodes(MindMapNodeViewModel node, HashSet<string> visited)
        {
            if (!visited.Add(node.Id)) return 0;
            int count = 1;
            foreach (var child in GetSubtreeChildren(node))
            {
                count += CountSubtreeNodes(child, visited);
            }
            return count;
        }

        private List<MindMapNodeViewModel> GetSubtreeChildren(MindMapNodeViewModel parent)
        {
            var direct = Nodes.Where(n => string.Equals(n.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase));
            var linked = Links.Where(l => string.Equals(l.SourceNodeId, parent.Id, StringComparison.OrdinalIgnoreCase))
                              .Select(l => Nodes.FirstOrDefault(n => string.Equals(n.Id, l.TargetNodeId, StringComparison.OrdinalIgnoreCase)))
                              .Where(n => n != null)
                              .Select(n => n!);
            return direct.Concat(linked)
                         .Where(n => n != null && !string.Equals(n.Id, parent.Id, StringComparison.OrdinalIgnoreCase))
                         .DistinctBy(n => n.Id)
                         .ToList();
        }

        private void AppendSubtreeMarkdown(StringBuilder sb, MindMapNodeViewModel node, int depth, HashSet<string> visited)
        {
            if (!visited.Add(node.Id)) return;

            if (depth <= 6)
            {
                sb.Append(new string('#', depth));
                sb.Append(' ');
                sb.AppendLine(node.Title);
            }
            else
            {
                sb.Append(new string(' ', (depth - 7) * 2));
                sb.Append("- **");
                sb.Append(node.Title);
                sb.AppendLine("**");
            }

            if (!string.IsNullOrWhiteSpace(node.MarkdownContent))
            {
                sb.AppendLine();
                sb.AppendLine(node.MarkdownContent.Trim());
                sb.AppendLine();
            }

            foreach (var child in GetSubtreeChildren(node))
            {
                AppendSubtreeMarkdown(sb, child, depth + 1, visited);
            }
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
            MindMapLayoutType.BottomUpHierarchy => "a bottom-up hierarchy",
            MindMapLayoutType.RightToLeftTree => "a right-to-left tree",
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

        // ---- Keyboard canvas navigation & nudging ----

        /// <summary>Selects the document's root node (or first visible node when the root is dimmed).</summary>
        public MindMapNodeViewModel? SelectRootNode()
        {
            var root = Nodes.FirstOrDefault(n => n.Id == Document.RootNodeId && !n.IsDimmed)
                       ?? Nodes.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId) && !n.IsDimmed)
                       ?? Nodes.FirstOrDefault(n => !n.IsDimmed)
                       ?? Nodes.FirstOrDefault();
            if (root == null) return null;
            SelectedLink = null;
            SelectedNode = root;
            StatusMessage = $"Selected '{root.Title}' ({root.FormatBadge}).";
            return root;
        }

        /// <summary>Calculates the hierarchical depth of a node from the root node (0 = root).</summary>
        public int GetNodeDepth(MindMapNodeViewModel node)
        {
            if (node == null) return 0;
            int depth = 0;
            var current = node;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrEmpty(current.ParentId) && visited.Add(current.Id))
            {
                var parent = Nodes.FirstOrDefault(n => n.Id == current.ParentId);
                if (parent == null || parent == current) break;
                depth++;
                current = parent;
            }
            return depth;
        }

        /// <summary>Calculates the maximum depth across all nodes in the map.</summary>
        public int GetMaxHierarchyDepth()
        {
            if (Nodes.Count == 0) return 0;
            int max = 0;
            foreach (var n in Nodes)
            {
                int d = GetNodeDepth(n);
                if (d > max) max = d;
            }
            return max;
        }

        /// <summary>Selects the deepest hierarchy node in the map (the furthest leaf from the root).</summary>
        public MindMapNodeViewModel? SelectDeepestNode()
        {
            if (Nodes.Count == 0) return null;
            MindMapNodeViewModel? deepest = null;
            int maxDepth = -1;

            foreach (var n in Nodes)
            {
                if (n.IsDimmed) continue;
                int d = GetNodeDepth(n);
                if (d > maxDepth)
                {
                    maxDepth = d;
                    deepest = n;
                }
            }

            if (deepest == null)
            {
                deepest = Nodes.FirstOrDefault();
            }

            if (deepest != null)
            {
                SelectedLink = null;
                SelectedNode = deepest;
                StatusMessage = $"Selected deepest node '{deepest.Title}' (Depth {maxDepth}).";
            }
            return deepest;
        }

        /// <summary>Selects the next leaf node (a node with no children).</summary>
        public MindMapNodeViewModel? SelectNextLeafNode()
        {
            if (Nodes.Count == 0) return null;
            var parentIds = new HashSet<string>(Nodes.Where(n => !string.IsNullOrEmpty(n.ParentId)).Select(n => n.ParentId!), StringComparer.OrdinalIgnoreCase);
            var leaves = Nodes.Where(n => !parentIds.Contains(n.Id) && !n.IsDimmed).ToList();
            if (leaves.Count == 0) leaves = Nodes.Where(n => !parentIds.Contains(n.Id)).ToList();
            if (leaves.Count == 0) return SelectRootNode();

            int currentIndex = SelectedNode != null ? leaves.IndexOf(SelectedNode) : -1;
            int nextIndex = (currentIndex + 1) % leaves.Count;
            var target = leaves[nextIndex];

            SelectedLink = null;
            SelectedNode = target;
            StatusMessage = $"Selected leaf node '{target.Title}' ({nextIndex + 1} of {leaves.Count}).";
            return target;
        }

        /// <summary>Selects the next orphan / unlinked node (a node with no parent and no children/links, separate from root).</summary>
        public MindMapNodeViewModel? SelectNextOrphanNode()
        {
            if (Nodes.Count == 0) return null;
            var linkedNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in Links)
            {
                if (!string.IsNullOrEmpty(l.SourceNodeId)) linkedNodeIds.Add(l.SourceNodeId);
                if (!string.IsNullOrEmpty(l.TargetNodeId)) linkedNodeIds.Add(l.TargetNodeId);
            }
            foreach (var n in Nodes)
            {
                if (!string.IsNullOrEmpty(n.ParentId))
                {
                    linkedNodeIds.Add(n.ParentId);
                    linkedNodeIds.Add(n.Id);
                }
            }

            var orphans = Nodes.Where(n => !linkedNodeIds.Contains(n.Id) && n.Id != Document.RootNodeId).ToList();
            if (orphans.Count == 0)
            {
                StatusMessage = "No isolated / orphan nodes found in map.";
                return null;
            }

            int currentIndex = SelectedNode != null ? orphans.IndexOf(SelectedNode) : -1;
            int nextIndex = (currentIndex + 1) % orphans.Count;
            var target = orphans[nextIndex];

            SelectedLink = null;
            SelectedNode = target;
            StatusMessage = $"Selected orphan node '{target.Title}' ({nextIndex + 1} of {orphans.Count}).";
            return target;
        }

        /// <summary>Moves selection from the current node to the nearest non-dimmed node in the
        /// requested canvas direction <c>(dirX, dirY)</c>, favouring connected neighbours on ties.</summary>
        public MindMapNodeViewModel? NavigateDirection(double dirX, double dirY)
        {
            double len = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (len < 1e-6) return null;
            dirX /= len;
            dirY /= len;

            if (SelectedNode == null)
            {
                return SelectRootNode();
            }

            var from = SelectedNode;
            double fx = from.X + from.Width / 2.0;
            double fy = from.Y + from.Height / 2.0;

            MindMapNodeViewModel? best = null;
            double bestScore = double.MaxValue;

            foreach (var candidate in Nodes)
            {
                if (candidate.Id == from.Id || candidate.IsDimmed) continue;
                double cx = candidate.X + candidate.Width / 2.0;
                double cy = candidate.Y + candidate.Height / 2.0;
                double vx = cx - fx;
                double vy = cy - fy;

                double forward = vx * dirX + vy * dirY;
                if (forward <= 4.0) continue;

                double lateral = Math.Abs(-vx * dirY + vy * dirX);
                if (lateral > forward * 2.25) continue;

                double score = forward + lateral * 1.85;
                bool connected = candidate.ParentId == from.Id
                                 || from.ParentId == candidate.Id
                                 || Links.Any(l =>
                                     (l.SourceNodeId == from.Id && l.TargetNodeId == candidate.Id) ||
                                     (l.SourceNodeId == candidate.Id && l.TargetNodeId == from.Id));
                if (connected) score *= 0.85;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best != null)
            {
                SelectedLink = null;
                SelectedNode = best;
                StatusMessage = $"Selected '{best.Title}' ({best.FormatBadge}).";
            }
            return best;
        }

        /// <summary>Nudges the selected node by <c>(dx, dy)</c> pixels on the canvas, coalescing
        /// consecutive keyboard nudges of the same node into a single undo entry.</summary>
        public bool NudgeSelectedNode(double dx, double dy)
        {
            if (SelectedNode == null || (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)) return false;

            string editKey = $"nudge:{SelectedNode.Id}";
            if (_openEditKey != editKey)
            {
                PushUndo($"Move '{SelectedNode.Title}'");
                _openEditKey = editKey;
            }

            using (SuspendEditTracking())
            {
                SelectedNode.X = Math.Round(SelectedNode.X + dx);
                SelectedNode.Y = Math.Round(SelectedNode.Y + dy);
                SelectedNode.SyncToModel();
            }

            MarkDirty($"Moved '{SelectedNode.Title}' to ({SelectedNode.X:F0}, {SelectedNode.Y:F0}).");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // ---- Theme palette specification (shared with MindMapGalaxyWindow & contrast tests) ----

        public readonly record struct ThemePaletteSpec(
            string Bg,
            string Card,
            string Border,
            string Ink,
            string SubInk,
            string Edge,
            string ProgressFill,
            string ProgressInk,
            string VersionFill,
            string VersionInk,
            string MissingInk,
            string HighlightBorder);

        public static ThemePaletteSpec GetThemePalette(string? name) => name switch
        {
            "Clean White" => new(
                Bg: "#F8FAFC", Card: "#FFFFFF", Border: "#CBD5E1", Ink: "#111827", SubInk: "#475569", Edge: "#64748B",
                ProgressFill: "#059669", ProgressInk: "#047857", VersionFill: "#E0F2FE", VersionInk: "#0369A1",
                MissingInk: "#B91C1C", HighlightBorder: "#0284C7"),
            "Nordic Slate" => new(
                Bg: "#1E293B", Card: "#273549", Border: "#475569", Ink: "#E2E8F0", SubInk: "#94A3B8", Edge: "#64748B",
                ProgressFill: "#34D399", ProgressInk: "#34D399", VersionFill: "#1C2D42", VersionInk: "#7DD3FC",
                MissingInk: "#FCA5A5", HighlightBorder: "#22D3EE"),
            "Obsidian Dark" => new(
                Bg: "#0B0C10", Card: "#15171F", Border: "#2D3343", Ink: "#E8E8F0", SubInk: "#9494AD", Edge: "#4A5266",
                ProgressFill: "#34D399", ProgressInk: "#34D399", VersionFill: "#1C2D42", VersionInk: "#7DD3FC",
                MissingInk: "#FCA5A5", HighlightBorder: "#22D3EE"),
            "Cyberpunk Neon" => new(
                Bg: "#0A0118", Card: "#180A2E", Border: "#4C2882", Ink: "#F0E7FF", SubInk: "#C4B5FD", Edge: "#7C4DFF",
                ProgressFill: "#34D399", ProgressInk: "#34D399", VersionFill: "#1C2D42", VersionInk: "#7DD3FC",
                MissingInk: "#FCA5A5", HighlightBorder: "#22D3EE"),
            _ => new(
                Bg: "#12131C", Card: "#1C1C28", Border: "#2E3248", Ink: "#F1F1F8", SubInk: "#9A9AB0", Edge: "#5A6478",
                ProgressFill: "#34D399", ProgressInk: "#34D399", VersionFill: "#1C2D42", VersionInk: "#7DD3FC",
                MissingInk: "#FCA5A5", HighlightBorder: "#22D3EE")
        };

        /// <summary>
        /// Computes 4-directional adaptive routing between a parent node and child node.
        /// Determines whether child is primarily to the right, left, below, or above the parent,
        /// and connects start and end points to the respective cardinal faces with smooth cubic bezier or orthogonal control points.
        /// </summary>
        public static (double startX, double startY, double c1X, double c1Y, double c2X, double c2Y, double endX, double endY)
            ComputeHierarchyEdgeRouting(double srcX, double srcY, double srcW, double srcH,
                                        double tgtX, double tgtY, double tgtW, double tgtH,
                                        string routingStyle = "curved",
                                        double tension = 0.5)
        {
            double srcCenterX = srcX + (srcW / 2.0);
            double srcCenterY = srcY + (srcH / 2.0);
            double tgtCenterX = tgtX + (tgtW / 2.0);
            double tgtCenterY = tgtY + (tgtH / 2.0);
            double dx = tgtCenterX - srcCenterX;
            double dy = tgtCenterY - srcCenterY;

            double startX, startY, endX, endY, c1X, c1Y, c2X, c2Y;
            bool isStraight = string.Equals(routingStyle, "straight", StringComparison.OrdinalIgnoreCase);
            bool isOrthogonal = string.Equals(routingStyle, "orthogonal", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(routingStyle, "stepped", StringComparison.OrdinalIgnoreCase);

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                if (dx >= 0)
                {
                    // Rightwards (standard tree / LTR)
                    startX = srcX + srcW;
                    startY = srcCenterY;
                    endX = tgtX;
                    endY = tgtCenterY;
                    if (isStraight)
                    {
                        c1X = startX + (endX - startX) / 3.0;
                        c1Y = startY + (endY - startY) / 3.0;
                        c2X = startX + 2.0 * (endX - startX) / 3.0;
                        c2Y = startY + 2.0 * (endY - startY) / 3.0;
                    }
                    else if (isOrthogonal)
                    {
                        double midX = (startX + endX) / 2.0;
                        c1X = midX; c1Y = startY;
                        c2X = midX; c2Y = endY;
                    }
                    else
                    {
                        double ctrl = Math.Max(Math.Abs(endX - startX) * tension, 30);
                        c1X = startX + ctrl; c1Y = startY;
                        c2X = endX - ctrl; c2Y = endY;
                    }
                }
                else
                {
                    // Leftwards (RightToLeftTree / RTL)
                    startX = srcX;
                    startY = srcCenterY;
                    endX = tgtX + tgtW;
                    endY = tgtCenterY;
                    if (isStraight)
                    {
                        c1X = startX + (endX - startX) / 3.0;
                        c1Y = startY + (endY - startY) / 3.0;
                        c2X = startX + 2.0 * (endX - startX) / 3.0;
                        c2Y = startY + 2.0 * (endY - startY) / 3.0;
                    }
                    else if (isOrthogonal)
                    {
                        double midX = (startX + endX) / 2.0;
                        c1X = midX; c1Y = startY;
                        c2X = midX; c2Y = endY;
                    }
                    else
                    {
                        double ctrl = Math.Max(Math.Abs(endX - startX) * tension, 30);
                        c1X = startX - ctrl; c1Y = startY;
                        c2X = endX + ctrl; c2Y = endY;
                    }
                }
            }
            else
            {
                if (dy >= 0)
                {
                    // Downwards (VerticalHierarchy / top-down)
                    startX = srcCenterX;
                    startY = srcY + srcH;
                    endX = tgtCenterX;
                    endY = tgtY;
                    if (isStraight)
                    {
                        c1X = startX + (endX - startX) / 3.0;
                        c1Y = startY + (endY - startY) / 3.0;
                        c2X = startX + 2.0 * (endX - startX) / 3.0;
                        c2Y = startY + 2.0 * (endY - startY) / 3.0;
                    }
                    else if (isOrthogonal)
                    {
                        double midY = (startY + endY) / 2.0;
                        c1X = startX; c1Y = midY;
                        c2X = endX; c2Y = midY;
                    }
                    else
                    {
                        double ctrl = Math.Max(Math.Abs(endY - startY) * tension, 30);
                        c1X = startX; c1Y = startY + ctrl;
                        c2X = endX; c2Y = endY - ctrl;
                    }
                }
                else
                {
                    // Upwards (BottomUpHierarchy / inverted)
                    startX = srcCenterX;
                    startY = srcY;
                    endX = tgtCenterX;
                    endY = tgtY + tgtH;
                    if (isStraight)
                    {
                        c1X = startX + (endX - startX) / 3.0;
                        c1Y = startY + (endY - startY) / 3.0;
                        c2X = startX + 2.0 * (endX - startX) / 3.0;
                        c2Y = startY + 2.0 * (endY - startY) / 3.0;
                    }
                    else if (isOrthogonal)
                    {
                        double midY = (startY + endY) / 2.0;
                        c1X = startX; c1Y = midY;
                        c2X = endX; c2Y = midY;
                    }
                    else
                    {
                        double ctrl = Math.Max(Math.Abs(endY - startY) * tension, 30);
                        c1X = startX; c1Y = startY - ctrl;
                        c2X = endX; c2Y = endY + ctrl;
                    }
                }
            }

            return (startX, startY, c1X, c1Y, c2X, c2Y, endX, endY);
        }

        /// <summary>
        /// Stroke weight options for mind map connection edges.
        /// </summary>
        public enum ConnectionStrokeWeight
        {
            Thin,
            Normal,
            Thick
        }

        /// <summary>
        /// Returns the stroke thickness for a connection edge weight preset.
        /// </summary>
        public static double GetStrokeThickness(ConnectionStrokeWeight weight) =>
            weight switch
            {
                ConnectionStrokeWeight.Thin => 1.2,
                ConnectionStrokeWeight.Thick => 2.8,
                _ => 1.8
            };

        /// <summary>
        /// Returns the stroke thickness for a connection edge weight preset string.
        /// </summary>
        public static double GetStrokeThickness(string weightName) =>
            weightName?.ToLowerInvariant() switch
            {
                "thin" => 1.2,
                "thick" => 2.8,
                _ => 1.8
            };

        /// <summary>
        /// Returns the stroke dash array intervals for a connection line style (solid = empty, dashed = 4, 3, dotted = 1.5, 2).
        /// </summary>
        public static double[] GetDashArrayPattern(string styleName) =>
            styleName?.ToLowerInvariant() switch
            {
                "dashed" => new double[] { 4.0, 3.0 },
                "dotted" => new double[] { 1.5, 2.0 },
                _ => Array.Empty<double>()
            };

        /// <summary>
        /// Formats a human-readable summary of connection edge styling and arrowheads.
        /// </summary>
        public static string FormatConnectionStyleSummary(string? styleName, string? weightName, bool showArrows)
        {
            var style = string.IsNullOrWhiteSpace(styleName) ? "Curved" : styleName.Trim();
            var weight = string.IsNullOrWhiteSpace(weightName) ? "Normal" : weightName.Trim();
            var arrows = showArrows ? "Arrows On" : "Arrows Off";
            return $"{style} · {weight} · {arrows}";
        }

        /// <summary>
        /// Returns the toggled arrowhead visibility state and a human-readable confirmation label.
        /// </summary>
        public static (bool ShowArrows, string Label) ToggleArrowheads(bool current)
        {
            var next = !current;
            return (next, next ? "Arrowheads Visible" : "Arrowheads Hidden");
        }

        /// <summary>
        /// Cycles the connection routing style (Curved -> Straight -> Orthogonal -> Curved) and returns the next style name and label.
        /// </summary>
        public static (string NextRouting, string Label) CycleRoutingStyle(string? currentRouting)
        {
            var cur = currentRouting?.ToLowerInvariant();
            return cur switch
            {
                "curved" => ("Straight", "Straight Lines"),
                "straight" => ("Orthogonal", "Stepped (Orthogonal)"),
                _ => ("Curved", "Curved (Bezier)")
            };
        }

        /// <summary>
        /// Cycles the connection stroke weight (Thin -> Normal -> Thick -> Thin) and returns the next weight and label.
        /// </summary>
        public static (ConnectionStrokeWeight NextWeight, string Label) CycleStrokeWeight(ConnectionStrokeWeight current)
        {
            return current switch
            {
                ConnectionStrokeWeight.Thin => (ConnectionStrokeWeight.Normal, "Weight: Normal (1.8px)"),
                ConnectionStrokeWeight.Normal => (ConnectionStrokeWeight.Thick, "Weight: Thick (2.8px)"),
                _ => (ConnectionStrokeWeight.Thin, "Weight: Thin (1.2px)")
            };
        }

        /// <summary>
        /// Cardinal anchor attachment positions for node connections.
        /// </summary>
        public enum NodeAnchorPosition
        {
            Auto,
            Left,
            Right,
            Top,
            Bottom,
            Center
        }

        /// <summary>
        /// Computes the connection attachment point coordinate for a node bounding box and anchor position.
        /// </summary>
        public static (double X, double Y) CalculateNodeAnchorPoint(double nodeX, double nodeY, double nodeWidth, double nodeHeight, NodeAnchorPosition anchor)
        {
            var w = Math.Max(0, nodeWidth);
            var h = Math.Max(0, nodeHeight);
            return anchor switch
            {
                NodeAnchorPosition.Left => (nodeX, nodeY + (h / 2.0)),
                NodeAnchorPosition.Right => (nodeX + w, nodeY + (h / 2.0)),
                NodeAnchorPosition.Top => (nodeX + (w / 2.0), nodeY),
                NodeAnchorPosition.Bottom => (nodeX + (w / 2.0), nodeY + h),
                _ => (nodeX + (w / 2.0), nodeY + (h / 2.0))
            };
        }

        /// <summary>
        /// Cycles the connection anchor mode (Auto -> Left -> Right -> Top -> Bottom -> Center -> Auto) and returns the next mode and label.
        /// </summary>
        public static (NodeAnchorPosition NextAnchor, string Label) CycleAnchorPosition(NodeAnchorPosition current)
        {
            return current switch
            {
                NodeAnchorPosition.Auto => (NodeAnchorPosition.Left, "Anchor: Left edge"),
                NodeAnchorPosition.Left => (NodeAnchorPosition.Right, "Anchor: Right edge"),
                NodeAnchorPosition.Right => (NodeAnchorPosition.Top, "Anchor: Top edge"),
                NodeAnchorPosition.Top => (NodeAnchorPosition.Bottom, "Anchor: Bottom edge"),
                NodeAnchorPosition.Bottom => (NodeAnchorPosition.Center, "Anchor: Center point"),
                _ => (NodeAnchorPosition.Auto, "Anchor: Auto (closest edge)")
            };
        }

        /// <summary>
        /// Computes the connection attachment point coordinate for a node bounding box and anchor position with optional clearance padding.
        /// </summary>
        public static (double X, double Y) CalculateNodeAnchorPointWithPadding(double nodeX, double nodeY, double nodeWidth, double nodeHeight, NodeAnchorPosition anchor, double padding = 0.0)
        {
            var (bx, by) = CalculateNodeAnchorPoint(nodeX, nodeY, nodeWidth, nodeHeight, anchor);
            if (padding <= 0) return (bx, by);

            return anchor switch
            {
                NodeAnchorPosition.Left => (bx - padding, by),
                NodeAnchorPosition.Right => (bx + padding, by),
                NodeAnchorPosition.Top => (bx, by - padding),
                NodeAnchorPosition.Bottom => (bx, by + padding),
                _ => (bx, by)
            };
        }

        /// <summary>
        /// Cycles connection anchor clearance padding (0px -> 4px -> 8px -> 12px -> 0px) and returns the next value and display label.
        /// </summary>
        public static (double NextPadding, string Label) CycleAnchorPadding(double currentPadding)
        {
            return currentPadding switch
            {
                < 2.0 => (4.0, "Anchor padding: 4px"),
                < 6.0 => (8.0, "Anchor padding: 8px"),
                < 10.0 => (12.0, "Anchor padding: 12px"),
                _ => (0.0, "Anchor padding: 0px (Flush)")
            };
        }

        /// <summary>
        /// Formats an executive summary of the complete connection style configuration including routing, stroke weight, anchor mode, padding, and arrowheads.
        /// </summary>
        public static string FormatFullConnectionConfiguration(
            string? routingStyle,
            double strokeWeight,
            NodeAnchorPosition anchorMode,
            double anchorPadding,
            bool showArrows)
        {
            string routingName = (routingStyle ?? "").ToLowerInvariant() switch
            {
                "straight" => "Straight (Direct)",
                "orthogonal" or "stepped" => "Stepped (Orthogonal)",
                _ => "Curved (Bezier)"
            };

            string weightName = strokeWeight switch
            {
                < 1.5 => "Thin (1.2px)",
                < 2.2 => "Normal (1.8px)",
                _ => "Thick (2.8px)"
            };

            string anchorName = anchorMode switch
            {
                NodeAnchorPosition.Auto => "Auto",
                NodeAnchorPosition.Left => "Left Edge",
                NodeAnchorPosition.Right => "Right Edge",
                NodeAnchorPosition.Top => "Top Edge",
                NodeAnchorPosition.Bottom => "Bottom Edge",
                NodeAnchorPosition.Center => "Center",
                _ => anchorMode.ToString()
            };

            string padStr = anchorPadding <= 0.5 ? "Flush (0px)" : $"{anchorPadding:0}px";
            string arrowStr = showArrows ? "Arrows: On" : "Arrows: Off";

            return $"Connection: {routingName} · Weight: {weightName} · Anchor: {anchorName} ({padStr}) · {arrowStr}";
        }

        /// <summary>
        /// Cycles connection anchor snap angle (0° -> 45° -> 90° -> 0°) and returns the next angle and display label.
        /// </summary>
        public static (double NextSnapAngle, string Label) CycleAnchorSnapAngle(double currentSnapAngle)
        {
            return currentSnapAngle switch
            {
                < 22.5 => (45.0, "Snap angle: 45° (Diagonal Octant)"),
                < 67.5 => (90.0, "Snap angle: 90° (Cardinal Orthogonal)"),
                _ => (0.0, "Snap angle: 0° (Free / Direct)")
            };
        }

        /// <summary>
        /// Formats a connection anchor snap summary for display or export.
        /// </summary>
        public static string FormatAnchorSnapSummary(double snapAngle)
        {
            if (snapAngle < 22.5) return "Anchor snap: Free / Direct (0°)";
            if (snapAngle < 67.5) return "Anchor snap: Diagonal / Octant (45°)";
            return "Anchor snap: Cardinal / Orthogonal (90°)";
        }

        /// <summary>
        /// Toggles connection line label visibility (true -> false -> true) and returns the next state and confirmation label.
        /// </summary>
        public static (bool NextShowLabels, string Label) ToggleConnectionLabels(bool currentShowLabels)
        {
            var next = !currentShowLabels;
            return (next, next ? "Show connection labels" : "Hide connection labels");
        }

        /// <summary>
        /// Formats a connection labels display summary including total connection count.
        /// </summary>
        public static string FormatConnectionLabelsSummary(bool showLabels, int totalLinksCount)
        {
            var state = showLabels ? "Visible" : "Hidden";
            var linkText = totalLinksCount == 1 ? "1 connection" : $"{totalLinksCount} connections";
            return $"Connection labels: {state} ({linkText})";
        }

        /// <summary>
        /// Steps connection stroke thickness across discrete ergonomic presets (1.0px -> 1.8px -> 2.8px -> 4.0px -> 1.0px)
        /// and returns the next thickness and display label.
        /// </summary>
        public static (double NextThickness, string Label) StepStrokeThickness(double currentThickness)
        {
            if (currentThickness < 1.4) return (1.8, "Stroke thickness: 1.8px (Standard)");
            if (currentThickness < 2.3) return (2.8, "Stroke thickness: 2.8px (Bold)");
            if (currentThickness < 3.4) return (4.0, "Stroke thickness: 4.0px (Heavy)");
            return (1.0, "Stroke thickness: 1.0px (Hairline)");
        }

        /// <summary>
        /// Formats a connection stroke thickness summary for inspector and status readout.
        /// </summary>
        public static string FormatStrokeThicknessSummary(double thickness)
        {
            if (thickness <= 1.2) return $"Stroke thickness: {thickness:0.#}px (Hairline)";
            if (thickness <= 2.0) return $"Stroke thickness: {thickness:0.#}px (Standard)";
            if (thickness <= 3.0) return $"Stroke thickness: {thickness:0.#}px (Bold)";
            return $"Stroke thickness: {thickness:0.#}px (Heavy)";
        }

        /// <summary>
        /// Cycles connection link direction (SourceToTarget -> Bidirectional -> TargetToSource -> None -> SourceToTarget)
        /// and returns the next direction and display label.
        /// </summary>
        public static (MindMapLinkDirection NextDirection, string Label) CycleLinkDirection(MindMapLinkDirection current)
        {
            return current switch
            {
                MindMapLinkDirection.SourceToTarget => (MindMapLinkDirection.Bidirectional, "Arrow: Bidirectional (↔)"),
                MindMapLinkDirection.Bidirectional => (MindMapLinkDirection.TargetToSource, "Arrow: Target to source (←)"),
                MindMapLinkDirection.TargetToSource => (MindMapLinkDirection.None, "Arrow: Plain line (—)"),
                _ => (MindMapLinkDirection.SourceToTarget, "Arrow: Source to target (→)")
            };
        }

        /// <summary>
        /// Formats a connection link direction summary for inspector and status readout.
        /// </summary>
        public static string FormatLinkDirectionSummary(MindMapLinkDirection direction)
        {
            return direction switch
            {
                MindMapLinkDirection.SourceToTarget => "Arrow direction: Forward (Source → Target)",
                MindMapLinkDirection.TargetToSource => "Arrow direction: Reverse (Target → Source)",
                MindMapLinkDirection.Bidirectional => "Arrow direction: Mutual (Bidirectional ↔)",
                _ => "Arrow direction: Undirected (Plain line —)"
            };
        }

        /// <summary>
        /// Traverses the mind map graph hierarchy using BFS from root nodes (or unvisited component heads)
        /// and assigns palette colors by depth level. Returns the number of colorized nodes, maximum hierarchy depth,
        /// and count of distinct colors applied.
        /// </summary>
        public (int NodeCount, int MaxDepth, int DistinctColors) AutoColorizeByHierarchy(IReadOnlyList<string>? customPalette = null)
        {
            if (Nodes.Count == 0) return (0, 0, 0);

            var palette = customPalette != null && customPalette.Count > 0 ? customPalette : PaletteColors;
            PushUndo("Auto-colorize by hierarchy");

            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in Nodes)
            {
                inDegree[n.Id] = 0;
                adj[n.Id] = new List<string>();
            }

            foreach (var l in Links)
            {
                if (inDegree.ContainsKey(l.TargetNodeId)) inDegree[l.TargetNodeId]++;
                if (adj.ContainsKey(l.SourceNodeId)) adj[l.SourceNodeId].Add(l.TargetNodeId);
            }

            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();

            var roots = Nodes.Where(n => inDegree[n.Id] == 0).ToList();
            if (roots.Count == 0)
            {
                roots = Nodes.OrderByDescending(n => adj[n.Id].Count).Take(1).ToList();
            }

            foreach (var r in roots)
            {
                if (!depths.ContainsKey(r.Id))
                {
                    depths[r.Id] = 0;
                    queue.Enqueue(r.Id);
                }
            }

            while (queue.Count > 0)
            {
                var curId = queue.Dequeue();
                int curDepth = depths[curId];

                foreach (var childId in adj[curId])
                {
                    if (!depths.TryGetValue(childId, out int existingDepth) || curDepth + 1 < existingDepth)
                    {
                        depths[childId] = curDepth + 1;
                        queue.Enqueue(childId);
                    }
                }
            }

            foreach (var n in Nodes)
            {
                if (!depths.ContainsKey(n.Id))
                {
                    depths[n.Id] = 0;
                    queue.Enqueue(n.Id);
                    while (queue.Count > 0)
                    {
                        var curId = queue.Dequeue();
                        int curDepth = depths[curId];
                        foreach (var childId in adj[curId])
                        {
                            if (!depths.TryGetValue(childId, out int existingDepth) || curDepth + 1 < existingDepth)
                            {
                                depths[childId] = curDepth + 1;
                                queue.Enqueue(childId);
                            }
                        }
                    }
                }
            }

            var appliedColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int maxDepth = 0;

            foreach (var n in Nodes)
            {
                int depth = depths.TryGetValue(n.Id, out int d) ? d : 0;
                if (depth > maxDepth) maxDepth = depth;
                string color = palette[depth % palette.Count];
                n.ColorHex = MindMapGraph.NormalizeHex(color, n.ColorHex);
                appliedColors.Add(n.ColorHex);
            }

            IsDirty = true;
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            StatusMessage = FormatHierarchyColorSummary(Nodes.Count, maxDepth, appliedColors.Count);
            return (Nodes.Count, maxDepth, appliedColors.Count);
        }

        /// <summary>
        /// Formats a human-readable summary of hierarchy level colorization.
        /// </summary>
        public static string FormatHierarchyColorSummary(int nodeCount, int maxDepth, int distinctColors)
        {
            if (nodeCount == 0) return "Auto-colorize: 0 nodes (Empty map)";
            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            int levels = maxDepth + 1;
            string levelLabel = levels == 1 ? "1 level (Flat)" : $"{levels} levels (Depth 0–{maxDepth})";
            string colorLabel = distinctColors == 1 ? "1 color" : $"{distinctColors} distinct colors";
            return $"Auto-colorized {nodeLabel} across {levelLabel} ({colorLabel} applied)";
        }

        /// <summary>
        /// Identifies disconnected/orphaned nodes (nodes with 0 incoming and 0 outgoing links) and
        /// magnetically attaches them in a radial arc around the primary root hub node.
        /// </summary>
        public (int AttachedCount, string RootLabel, int TotalNodes) AttachOrphansToRoot(double orbitRadius = 260.0)
        {
            if (Nodes.Count == 0)
            {
                StatusMessage = FormatOrphanAttachmentSummary(0, "", 0);
                return (0, "", 0);
            }

            if (Nodes.Count == 1)
            {
                string singleLabel = string.IsNullOrWhiteSpace(Nodes[0].Title) ? "Root" : Nodes[0].Title;
                StatusMessage = FormatOrphanAttachmentSummary(0, singleLabel, 1);
                return (0, singleLabel, 1);
            }

            var degree = new Dictionary<string, int>(StringComparer.Ordinal);
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            var outDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in Nodes)
            {
                degree[n.Id] = 0;
                inDegree[n.Id] = 0;
                outDegree[n.Id] = 0;
            }

            foreach (var l in Links)
            {
                if (degree.ContainsKey(l.SourceNodeId))
                {
                    degree[l.SourceNodeId]++;
                    outDegree[l.SourceNodeId]++;
                }
                if (degree.ContainsKey(l.TargetNodeId))
                {
                    degree[l.TargetNodeId]++;
                    inDegree[l.TargetNodeId]++;
                }
            }

            // Pick primary root hub: prefer connected node with inDegree == 0 and highest outDegree, else SelectedNode, else first node
            var rootNode = Nodes
                .Where(n => degree[n.Id] > 0 && inDegree[n.Id] == 0)
                .OrderByDescending(n => outDegree[n.Id])
                .FirstOrDefault()
                ?? Nodes.Where(n => degree[n.Id] > 0).OrderByDescending(n => degree[n.Id]).FirstOrDefault()
                ?? SelectedNode
                ?? Nodes[0];

            string rootLabel = string.IsNullOrWhiteSpace(rootNode.Title) ? "Root" : rootNode.Title;
            var orphans = Nodes.Where(n => n.Id != rootNode.Id && degree[n.Id] == 0).ToList();

            if (orphans.Count == 0)
            {
                StatusMessage = FormatOrphanAttachmentSummary(0, rootLabel, Nodes.Count);
                return (0, rootLabel, Nodes.Count);
            }

            PushUndo("Attach orphaned nodes to root");
            double radius = Math.Max(120.0, orbitRadius);
            for (int i = 0; i < orphans.Count; i++)
            {
                var orphan = orphans[i];
                double angle = (2.0 * Math.PI * i) / orphans.Count - (Math.PI / 2.0);
                orphan.X = Math.Round(rootNode.X + radius * Math.Cos(angle), 1);
                orphan.Y = Math.Round(rootNode.Y + radius * Math.Sin(angle), 1);

                var link = new MindMapLink
                {
                    SourceNodeId = rootNode.Id,
                    TargetNodeId = orphan.Id,
                    Direction = MindMapLinkDirection.SourceToTarget
                };
                Links.Add( new MindMapLinkViewModel(link));
            }

            IsDirty = true;
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            StatusMessage = FormatOrphanAttachmentSummary(orphans.Count, rootLabel, Nodes.Count);
            return (orphans.Count, rootLabel, Nodes.Count);
        }

        /// <summary>
        /// Formats a human-readable summary of orphaned node clustering and root attachment.
        /// </summary>
        public static string FormatOrphanAttachmentSummary(int attachedCount, string? rootLabel, int totalNodes)
        {
            if (totalNodes <= 0) return "Orphan attachment: 0 nodes (Empty map)";
            string rLabel = string.IsNullOrWhiteSpace(rootLabel) ? "Root" : rootLabel;
            string totalStr = totalNodes == 1 ? "1 total node" : $"{totalNodes} total nodes";
            if (attachedCount <= 0)
            {
                return $"Orphan check: 0 orphaned nodes found ({totalStr} connected to '{rLabel}')";
            }

            string orphanStr = attachedCount == 1 ? "1 orphaned node" : $"{attachedCount} orphaned nodes";
            return $"Attached {orphanStr} to root '{rLabel}' ({totalStr} · 0 orphans remaining)";
        }

        /// <summary>
        /// Audits all nodes in the mind map that have a non-empty <see cref="MindMapNodeViewModel.FilePath"/>,
        /// verifying whether each linked file exists on disk.
        /// </summary>
        public (int TotalLinked, int ValidCount, int MissingCount, List<string> MissingNodeTitles) AuditLinkedFilesStatus(
            Func<string, bool>? fileExistsPredicate = null)
        {
            var missingTitles = new List<string>();
            if (Nodes.Count == 0)
            {
                StatusMessage = FormatLinkedFilesAuditSummary(0, 0, 0, missingTitles, 0);
                return (0, 0, 0, missingTitles);
            }

            var checkExists = fileExistsPredicate ?? System.IO.File.Exists;
            int totalLinked = 0;
            int validCount = 0;
            int missingCount = 0;

            foreach (var n in Nodes)
            {
                if (string.IsNullOrWhiteSpace(n.FilePath)) continue;
                totalLinked++;
                bool exists;
                try
                {
                    exists = checkExists(n.FilePath);
                }
                catch
                {
                    exists = false;
                }

                if (exists)
                {
                    validCount++;
                }
                else
                {
                    missingCount++;
                    string title = string.IsNullOrWhiteSpace(n.Title) ? "(Untitled)" : n.Title.Trim();
                    missingTitles.Add(title);
                }
            }

            StatusMessage = FormatLinkedFilesAuditSummary(totalLinked, validCount, missingCount, missingTitles, Nodes.Count);
            return (totalLinked, validCount, missingCount, missingTitles);
        }

        /// <summary>
        /// Formats a human-readable diagnostic summary of linked file health across mind map nodes.
        /// </summary>
        public static string FormatLinkedFilesAuditSummary(
            int totalLinked,
            int validCount,
            int missingCount,
            IEnumerable<string>? missingNodeTitles = null,
            int totalNodes = 0)
        {
            if (totalNodes <= 0 && totalLinked <= 0)
                return "Linked files audit: 0 linked files (Empty map)";

            if (totalLinked <= 0)
            {
                string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
                return $"Linked files audit: 0 linked files across {nodeStr}";
            }

            string linkedLabel = totalLinked == 1 ? "1 linked file" : $"{totalLinked} linked files";
            if (missingCount <= 0)
            {
                return $"Linked files audit ({linkedLabel}): 100% valid on disk ({validCount} verified · 0 broken links)";
            }

            double validPct = (double)validCount / totalLinked * 100.0;
            var titles = missingNodeTitles?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
            string sampleStr = titles.Count > 0
                ? $" ({string.Join(", ", titles.Take(3).Select(t => $"'{t}'"))}{(titles.Count > 3 ? $", +{titles.Count - 3} more" : "")})"
                : "";

            return $"Linked files audit ({linkedLabel}): {validCount} valid ({validPct:0.#}%), {missingCount} broken/missing{sampleStr}";
        }

        /// <summary>
        /// Recursively rolls up completion progress (<see cref="MindMapNodeViewModel.Progress"/>) from leaf nodes
        /// to parent/hub nodes across the mind map hierarchy.
        /// </summary>
        public (int UpdatedParentNodes, int LeafCount, int RootProgressPercent) RollupSubtreeProgress()
        {
            if (Nodes.Count == 0)
            {
                StatusMessage = FormatSubtreeProgressRollupSummary(0, 0, 0, 0);
                return (0, 0, 0);
            }

            var nodeById = Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var childrenMap = new Dictionary<string, List<MindMapNodeViewModel>>(StringComparer.Ordinal);
            foreach (var n in Nodes)
            {
                childrenMap[n.Id] = new List<MindMapNodeViewModel>();
            }

            foreach (var n in Nodes)
            {
                if (!string.IsNullOrWhiteSpace(n.ParentId) &&
                    nodeById.TryGetValue(n.ParentId, out var pNode) &&
                    !ReferenceEquals(pNode, n) &&
                    !childrenMap[pNode.Id].Contains(n))
                {
                    childrenMap[pNode.Id].Add(n);
                }
            }

            foreach (var l in Links)
            {
                if (nodeById.TryGetValue(l.SourceNodeId, out var src) &&
                    nodeById.TryGetValue(l.TargetNodeId, out var tgt) &&
                    !ReferenceEquals(src, tgt) &&
                    !childrenMap[src.Id].Contains(tgt))
                {
                    childrenMap[src.Id].Add(tgt);
                }
            }

            var computedProgress = new Dictionary<string, int>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            int leafCount = 0;

            int ComputeNodeProgress(MindMapNodeViewModel node)
            {
                if (computedProgress.TryGetValue(node.Id, out int cached))
                    return cached;

                if (!visiting.Add(node.Id))
                    return Math.Clamp(node.Progress, 0, 100);

                var kids = childrenMap[node.Id]
                    .Where(k => !visiting.Contains(k.Id))
                    .ToList();

                int result;
                if (kids.Count == 0)
                {
                    leafCount++;
                    result = Math.Clamp(node.Progress, 0, 100);
                }
                else
                {
                    double sum = 0;
                    foreach (var k in kids)
                    {
                        sum += ComputeNodeProgress(k);
                    }
                    result = Math.Clamp((int)Math.Round(sum / kids.Count, MidpointRounding.AwayFromZero), 0, 100);
                }

                visiting.Remove(node.Id);
                computedProgress[node.Id] = result;
                return result;
            }

            var rootNode = GetRootNode() ?? Nodes[0];
            ComputeNodeProgress(rootNode);
            foreach (var n in Nodes)
            {
                if (!computedProgress.ContainsKey(n.Id))
                {
                    ComputeNodeProgress(n);
                }
            }

            int updatedParents = 0;
            bool undoPushed = false;
            foreach (var n in Nodes)
            {
                if (childrenMap[n.Id].Count > 0 &&
                    computedProgress.TryGetValue(n.Id, out int targetProg) &&
                    n.Progress != targetProg)
                {
                    if (!undoPushed)
                    {
                        PushUndo("Roll up subtree progress");
                        undoPushed = true;
                    }
                    using (SuspendEditTracking())
                    {
                        n.Progress = targetProg;
                        n.SyncToModel();
                    }
                    updatedParents++;
                }
            }

            int rootProg = computedProgress.TryGetValue(rootNode.Id, out int rp) ? rp : rootNode.Progress;
            if (updatedParents > 0)
            {
                IsDirty = true;
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }

            StatusMessage = FormatSubtreeProgressRollupSummary(updatedParents, leafCount, rootProg, Nodes.Count);
            return (updatedParents, leafCount, rootProg);
        }

        /// <summary>
        /// Formats a human-readable summary of hierarchical subtree completion progress rollup.
        /// </summary>
        public static string FormatSubtreeProgressRollupSummary(
            int updatedParentNodes,
            int leafCount,
            int rootProgressPercent,
            int totalNodes = 0)
        {
            if (totalNodes <= 0 && leafCount <= 0 && updatedParentNodes <= 0)
                return "Subtree progress rollup: 0 nodes (Empty map)";

            string parentStr = updatedParentNodes == 1 ? "1 parent hub updated" : $"{updatedParentNodes} parent hubs updated";
            string leafStr = leafCount == 1 ? "1 leaf node" : $"{leafCount} leaf nodes";
            return $"Subtree progress rollup: {parentStr} from {leafStr} (Root completion: {Math.Clamp(rootProgressPercent, 0, 100)}%)";
        }

        /// <summary>
        /// Auto-colorizes nodes according to their primary tag category using a distinct palette.
        /// Returns total colorized nodes, distinct tag count, and distinct colors applied.
        /// </summary>
        public (int ColorizedNodes, int DistinctTags, int ColorsApplied) AutoColorizeByPrimaryTag(IReadOnlyList<string>? customPalette = null)
        {
            if (Nodes.Count == 0)
            {
                StatusMessage = "Auto-colorize by tag: 0 nodes (Empty map)";
                return (0, 0, 0);
            }

            var palette = customPalette != null && customPalette.Count > 0 ? customPalette : PaletteColors;

            // Collect all unique primary tags (normalized by trimming and stripping leading '#')
            var tagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var uniqueTags = new List<string>();

            foreach (var node in Nodes)
            {
                string? primaryTag = null;
                foreach (var t in node.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        var norm = t.Trim().TrimStart('#').ToLowerInvariant();
                        if (!string.IsNullOrEmpty(norm))
                        {
                            primaryTag = norm;
                            break;
                        }
                    }
                }

                if (primaryTag != null && !tagMap.ContainsKey(primaryTag))
                {
                    tagMap[primaryTag] = palette[uniqueTags.Count % palette.Count];
                    uniqueTags.Add(primaryTag);
                }
            }

            if (uniqueTags.Count == 0)
            {
                StatusMessage = "Auto-colorize by tag: No tagged nodes found in the map.";
                return (0, 0, 0);
            }

            PushUndo("Auto-colorize by tag");
            int colorizedCount = 0;
            var appliedColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (SuspendEditTracking())
            {
                foreach (var node in Nodes)
                {
                    string? primaryTag = null;
                    foreach (var t in node.Tags)
                    {
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            var norm = t.Trim().TrimStart('#').ToLowerInvariant();
                            if (!string.IsNullOrEmpty(norm))
                            {
                                primaryTag = norm;
                                break;
                            }
                        }
                    }

                    if (primaryTag != null && tagMap.TryGetValue(primaryTag, out var hex))
                    {
                        node.ColorHex = MindMapGraph.NormalizeHex(hex, node.ColorHex);
                        node.SyncToModel();
                        colorizedCount++;
                        appliedColors.Add(node.ColorHex);
                    }
                }
            }

            if (colorizedCount > 0)
            {
                IsDirty = true;
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }

            string nodeLabel = colorizedCount == 1 ? "1 node" : $"{colorizedCount} nodes";
            string tagLabel = uniqueTags.Count == 1 ? "1 tag category" : $"{uniqueTags.Count} tag categories";
            StatusMessage = $"Auto-colorized {nodeLabel} across {tagLabel} ({appliedColors.Count} palette colors applied).";
            return (colorizedCount, uniqueTags.Count, appliedColors.Count);
        }

        /// <summary>
        /// Audits tag co-occurrence across multi-tagged nodes in the galaxy.
        /// Returns total multi-tagged nodes, total tag instances, and pairwise co-occurrence frequencies sorted descending.
        /// </summary>
        public (int MultiTaggedNodes, int TotalTagInstances, List<(string TagA, string TagB, int CoOccurrenceCount)> PairwiseFrequencies)
            AuditTagCoOccurrence()
        {
            var pairs = new Dictionary<(string, string), int>();
            int multiTaggedNodes = 0;
            int totalTagInstances = 0;

            foreach (var node in Nodes)
            {
                var normTags = node.Tags
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim().TrimStart('#').ToLowerInvariant())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                totalTagInstances += normTags.Count;
                if (normTags.Count >= 2)
                {
                    multiTaggedNodes++;
                    for (int i = 0; i < normTags.Count; i++)
                    {
                        for (int j = i + 1; j < normTags.Count; j++)
                        {
                            var pairKey = (normTags[i], normTags[j]);
                            pairs[pairKey] = pairs.TryGetValue(pairKey, out int count) ? count + 1 : 1;
                        }
                    }
                }
            }

            var sortedPairs = pairs
                .Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value))
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Item1)
                .ThenBy(p => p.Item2)
                .ToList();

            return (multiTaggedNodes, totalTagInstances, sortedPairs);
        }

        /// <summary>
        /// Formats a human-readable summary of tag co-occurrence matrix and multi-tag clusters.
        /// </summary>
        public string FormatTagCoOccurrenceSummary()
        {
            var (multiTagged, totalInstances, pairs) = AuditTagCoOccurrence();
            return FormatTagCoOccurrenceSummary(multiTagged, totalInstances, pairs, Nodes.Count);
        }

        /// <summary>
        /// Formats a human-readable summary of tag co-occurrence counts and top pairwise clusters.
        /// </summary>
        public static string FormatTagCoOccurrenceSummary(
            int multiTaggedNodes,
            int totalTagInstances,
            IReadOnlyList<(string TagA, string TagB, int CoOccurrenceCount)>? pairs,
            int totalNodes = 0)
        {
            if (totalNodes <= 0 && multiTaggedNodes <= 0 && totalTagInstances <= 0)
                return "Tag co-occurrence audit: 0 nodes (Empty map)";

            if (totalTagInstances <= 0)
                return $"Tag co-occurrence audit: 0 tags across {totalNodes} nodes (No tags defined)";

            if (multiTaggedNodes <= 0 || pairs == null || pairs.Count == 0)
            {
                string tagLabel = totalTagInstances == 1 ? "1 tag instance" : $"{totalTagInstances} tag instances";
                return $"Tag co-occurrence audit: {tagLabel} across {totalNodes} nodes (0 multi-tagged nodes · No co-occurring pairs)";
            }

            string nodeLabel = multiTaggedNodes == 1 ? "1 multi-tagged node" : $"{multiTaggedNodes} multi-tagged nodes";
            var topPairs = pairs.Take(3).Select(p => $"'{p.TagA}' + '{p.TagB}' ({p.CoOccurrenceCount}×)");
            string topSummary = string.Join(", ", topPairs);
            if (pairs.Count > 3) topSummary += $", +{pairs.Count - 3} more pairs";

            return $"Tag co-occurrence audit ({nodeLabel} · {pairs.Count} unique pairs): Top co-occurrences: {topSummary}";
        }

        /// <summary>
        /// Centers the galaxy viewport camera on the collective center-of-mass of all nodes in the galaxy.
        /// Returns the computed center coordinates (CenterX, CenterY).
        /// </summary>
        public (double CenterX, double CenterY) CenterGalaxyOnCenterOfMass()
        {
            if (Nodes.Count == 0) return (0, 0);

            double cx = Nodes.Average(n => n.X + n.Width / 2.0);
            double cy = Nodes.Average(n => n.Y + n.Height / 2.0);

            ViewportOffsetX = -cx;
            ViewportOffsetY = -cy;
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);

            string nodeLabel = Nodes.Count == 1 ? "1 node" : $"{Nodes.Count} nodes";
            StatusMessage = $"✓ Centered galaxy viewport on center of mass ({cx:0.#}, {cy:0.#}) across {nodeLabel}.";
            return (cx, cy);
        }

        /// <summary>
        /// Audits spatial distribution of nodes across the four quadrants (NW, NE, SW, SE) relative to the
        /// collective center-of-mass, and measures drift from the root hub node.
        /// </summary>
        public (int NW, int NE, int SW, int SE, double CenterX, double CenterY, double DriftX, double DriftY)
            AuditSpatialQuadrantBalance()
        {
            if (Nodes.Count == 0) return (0, 0, 0, 0, 0, 0, 0, 0);

            double cx = Nodes.Average(n => n.X + n.Width / 2.0);
            double cy = Nodes.Average(n => n.Y + n.Height / 2.0);

            int nw = 0, ne = 0, sw = 0, se = 0;
            foreach (var node in Nodes)
            {
                double nx = node.X + node.Width / 2.0;
                double ny = node.Y + node.Height / 2.0;

                if (nx < cx && ny < cy) nw++;
                else if (nx >= cx && ny < cy) ne++;
                else if (nx < cx && ny >= cy) sw++;
                else se++;
            }

            var root = Nodes.FirstOrDefault(n => n.Id == Document.RootNodeId) ?? Nodes.FirstOrDefault();
            double driftX = root != null ? Math.Round((root.X + root.Width / 2.0) - cx, 1) : 0.0;
            double driftY = root != null ? Math.Round((root.Y + root.Height / 2.0) - cy, 1) : 0.0;

            return (nw, ne, sw, se, cx, cy, driftX, driftY);
        }

        /// <summary>
        /// Formats a human-readable summary of the galaxy's spatial quadrant balance and center-of-mass metrics.
        /// </summary>
        public string FormatSpatialQuadrantBalanceSummary()
        {
            var (nw, ne, sw, se, cx, cy, dx, dy) = AuditSpatialQuadrantBalance();
            return FormatSpatialQuadrantBalanceSummary(nw, ne, sw, se, cx, cy, dx, dy, Nodes.Count);
        }

        /// <summary>
        /// Formats a human-readable summary of quadrant distribution counts and drift.
        /// </summary>
        public static string FormatSpatialQuadrantBalanceSummary(
            int nw, int ne, int sw, int se, double cx, double cy, double driftX = 0, double driftY = 0, int totalNodes = 0)
        {
            if (totalNodes <= 0)
                return "Spatial quadrant balance: 0 nodes (Empty map)";

            double nwPct = (double)nw / totalNodes * 100.0;
            double nePct = (double)ne / totalNodes * 100.0;
            double swPct = (double)sw / totalNodes * 100.0;
            double sePct = (double)se / totalNodes * 100.0;

            string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string driftStr = (driftX != 0 || driftY != 0)
                ? $" · Root drift: (ΔX: {driftX:+0.#;-0.#;0}, ΔY: {driftY:+0.#;-0.#;0})"
                : "";

            return $"Spatial quadrant balance ({nodeLabel} · Center of Mass ({cx:0.#}, {cy:0.#})): NW: {nw} ({nwPct:0.#}%), NE: {ne} ({nePct:0.#}%), SW: {sw} ({swPct:0.#}%), SE: {se} ({sePct:0.#}%){driftStr}";
        }

        /// <summary>
        /// Normalizes text casing across selected nodes (or all nodes if single/no nodes are selected) using the specified casing mode.
        /// Preserves layout and links, records an atomic undo snapshot, and updates the canvas.
        /// </summary>
        public (int ModifiedCount, int TotalExamined, string Summary) NormalizeNodeTextCasing(NodeCasingMode mode = NodeCasingMode.TitleCase, bool onlySelected = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            List<MindMapNodeViewModel> targets;
            if (onlySelected && selected.Count > 0)
            {
                targets = selected;
            }
            else if (selected.Count > 1)
            {
                targets = selected;
            }
            else
            {
                targets = Nodes.ToList();
            }

            if (targets.Count == 0)
            {
                string emptySummary = FormatNodeCasingSummary(0, 0, mode);
                StatusMessage = emptySummary;
                return (0, 0, emptySummary);
            }

            int modified = 0;
            string? sample = null;
            bool undoPushed = false;

            foreach (var node in targets)
            {
                string current = node.Title ?? string.Empty;
                string transformed = TransformCasing(current, mode);

                if (!string.Equals(current, transformed, StringComparison.Ordinal))
                {
                    if (!undoPushed)
                    {
                        PushUndo($"Normalize node casing ({mode})");
                        undoPushed = true;
                    }

                    using (SuspendEditTracking())
                    {
                        node.Title = transformed;
                        node.SyncToModel();
                    }

                    modified++;
                    sample ??= transformed;
                }
            }

            if (modified > 0)
            {
                IsDirty = true;
                CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);
            }

            string summary = FormatNodeCasingSummary(modified, targets.Count, mode, sample);
            StatusMessage = modified > 0 ? $"✓ {summary} · Ctrl+Z to undo" : summary;
            return (modified, targets.Count, summary);
        }

        /// <summary>
        /// Transforms text casing according to the specified mode.
        /// </summary>
        public static string TransformCasing(string text, NodeCasingMode mode)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            return mode switch
            {
                NodeCasingMode.UpperCase => text.ToUpperInvariant(),
                NodeCasingMode.LowerCase => text.ToLowerInvariant(),
                NodeCasingMode.TitleCase => ConvertToTitleCase(text),
                NodeCasingMode.SentenceCase => ConvertToSentenceCase(text),
                _ => text
            };
        }

        private static string ConvertToTitleCase(string text)
        {
            var words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                var w = words[i];
                if (w.Length > 0)
                {
                    if (w.Length == 1)
                        words[i] = char.ToUpperInvariant(w[0]).ToString();
                    else
                        words[i] = char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant();
                }
            }
            return string.Join(" ", words);
        }

        private static string ConvertToSentenceCase(string text)
        {
            if (text.Length <= 1) return text.ToUpperInvariant();
            return char.ToUpperInvariant(text[0]) + text.Substring(1).ToLowerInvariant();
        }

        /// <summary>
        /// Formats a human-readable summary of the node text casing normalization operation.
        /// </summary>
        public static string FormatNodeCasingSummary(int modifiedCount, int totalExamined, NodeCasingMode mode, string? sampleTitle = null)
        {
            if (totalExamined <= 0)
                return "Normalize node text casing: 0 nodes (Empty map)";

            if (modifiedCount <= 0)
                return $"Normalize node text casing: 0 nodes modified across {totalExamined} nodes (Already matching {mode})";

            string nodeLabel = modifiedCount == 1 ? "1 node" : $"{modifiedCount} nodes";
            string sampleStr = !string.IsNullOrEmpty(sampleTitle) ? $" (e.g. '{sampleTitle}')" : "";
            return $"Normalize node text casing ({mode}): {nodeLabel} updated across {totalExamined} nodes{sampleStr}";
        }

        /// <summary>
        /// Audits mind map nodes to identify inactive/dormant nodes and subtrees
        /// (nodes with Progress == 100, tagged 'archived'/'done'/'dormant'/'deprecated', or with last modified date older than <paramref name="inactiveDaysThreshold"/>).
        /// </summary>
        public (int InactiveCount, int TotalNodes, int InactiveSubtreeRoots, double InactivePercentage, string Summary)
            AuditInactiveSubtrees(int inactiveDaysThreshold = 60, DateTime? referenceDate = null)
        {
            var nodes = Nodes.Select(n => n.Model).ToList();
            var links = Links.Select(l => l.Model).ToList();
            var result = AuditInactiveSubtrees(nodes, links, inactiveDaysThreshold, referenceDate);
            StatusMessage = result.Summary;
            return result;
        }

        /// <summary>
        /// Static pure-data auditor for inactive subtrees and dormant nodes.
        /// </summary>
        public static (int InactiveCount, int TotalNodes, int InactiveSubtreeRoots, double InactivePercentage, string Summary)
            AuditInactiveSubtrees(
                IReadOnlyList<MindMapNode>? nodes,
                IReadOnlyList<MindMapLink>? links = null,
                int inactiveDaysThreshold = 60,
                DateTime? referenceDate = null)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return (0, 0, 0, 0.0, "Inactive subtree audit: 0 nodes (Empty map)");
            }

            var refTime = referenceDate ?? DateTime.Now;
            var inactiveNodes = new HashSet<string>();
            var inactiveTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "archived", "done", "dormant", "deprecated", "completed" };

            foreach (var node in nodes)
            {
                bool isInactive = false;

                // 1. Tag match
                if (node.Tags != null && node.Tags.Any(t => inactiveTags.Contains(t.TrimStart('#').Trim())))
                {
                    isInactive = true;
                }

                // 2. 100% progress
                if (node.Progress >= 100)
                {
                    isInactive = true;
                }

                // 3. Stale date
                if (!isInactive && inactiveDaysThreshold > 0)
                {
                    string? dateStr = node.ModifiedDate ?? node.CreatedDate;
                    if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        if ((refTime - dt).TotalDays >= inactiveDaysThreshold)
                        {
                            isInactive = true;
                        }
                    }
                }

                if (isInactive)
                {
                    inactiveNodes.Add(node.Id);
                }
            }

            // Identify inactive subtree roots:
            // A node is an inactive subtree root if:
            // - It is inactive
            // - Parent is either absent or NOT inactive
            var childrenMap = new Dictionary<string, List<string>>();
            var parentMap = new Dictionary<string, string>();
            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.ParentId))
                {
                    parentMap[node.Id] = node.ParentId;
                    if (!childrenMap.ContainsKey(node.ParentId))
                        childrenMap[node.ParentId] = new List<string>();
                    childrenMap[node.ParentId].Add(node.Id);
                }
            }

            var inactiveRoots = new List<string>();
            foreach (var node in nodes)
            {
                if (inactiveNodes.Contains(node.Id))
                {
                    bool parentIsInactive = parentMap.TryGetValue(node.Id, out var pid) && inactiveNodes.Contains(pid);
                    if (!parentIsInactive)
                    {
                        inactiveRoots.Add(node.Title);
                    }
                }
            }

            int inactiveCount = inactiveNodes.Count;
            int total = nodes.Count;
            double pct = total > 0 ? Math.Round((double)inactiveCount / total * 100.0, 1) : 0.0;

            string summary = FormatInactiveSubtreesSummary(inactiveCount, total, inactiveRoots.Count, pct, inactiveDaysThreshold, inactiveRoots);
            return (inactiveCount, total, inactiveRoots.Count, pct, summary);
        }

        /// <summary>
        /// Formats a human-readable summary of inactive subtrees and dormant nodes.
        /// </summary>
        public static string FormatInactiveSubtreesSummary(
            int inactiveCount,
            int totalNodes,
            int inactiveSubtreeRoots,
            double inactivePercentage,
            int thresholdDays,
            IReadOnlyList<string>? sampleRoots = null)
        {
            if (totalNodes <= 0)
                return "Inactive subtree audit: 0 nodes (Empty map)";

            if (inactiveCount <= 0)
                return $"Inactive subtree audit: All {totalNodes} nodes are active and recently updated (0 inactive subtrees detected)";

            string nodeLabel = inactiveCount == 1 ? "1 dormant node" : $"{inactiveCount} dormant nodes";
            string rootLabel = inactiveSubtreeRoots == 1 ? "1 subtree root" : $"{inactiveSubtreeRoots} subtree roots";
            string samples = "";
            if (sampleRoots != null && sampleRoots.Count > 0)
            {
                var take = sampleRoots.Take(3).Select(s => $"'{s}'").ToList();
                samples = $" (e.g. {string.Join(", ", take)})";
            }

            return $"Inactive subtree audit ({inactivePercentage:0.#}% dormant · >{thresholdDays}d threshold): {nodeLabel} across {rootLabel}{samples} eligible for vault archiving";
        }

        /// <summary>
        /// Audits the topological depth, level-by-level breadth, and tree hierarchy balance of the active mind map.
        /// </summary>
        public TopologicalDepthAudit AuditTopologicalDepth()
        {
            return AuditTopologicalDepth(Nodes, Links);
        }

        /// <summary>
        /// Audits the topological depth, level hierarchy, and breadth distribution of nodes and links.
        /// </summary>
        public static TopologicalDepthAudit AuditTopologicalDepth(
            IReadOnlyList<MindMapNodeViewModel> nodes,
            IReadOnlyList<MindMapLinkViewModel>? links = null)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return new TopologicalDepthAudit(
                    0, 0, 0, 0.0, new Dictionary<int, int>(), 0, 0,
                    "Topological depth audit: 0 nodes (Empty map)");
            }

            // Build parent-child relationships from ParentId
            var childrenMap = new Dictionary<string, List<string>>();
            var inDegreeMap = new Dictionary<string, int>();
            foreach (var node in nodes)
            {
                childrenMap[node.Id] = new List<string>();
                inDegreeMap[node.Id] = 0;
            }

            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.ParentId) && childrenMap.ContainsKey(node.ParentId))
                {
                    childrenMap[node.ParentId].Add(node.Id);
                    inDegreeMap[node.Id]++;
                }
            }

            // Identify root nodes (in-degree == 0, or ParentId empty)
            var roots = nodes.Where(n => string.IsNullOrEmpty(n.ParentId) || inDegreeMap[n.Id] == 0).ToList();
            if (roots.Count == 0)
            {
                // If all have in-degree > 0 (cyclic), pick first node as root
                roots.Add(nodes[0]);
            }

            var nodeDepths = new Dictionary<string, int>();
            var queue = new Queue<(string NodeId, int Depth)>();
            var visited = new HashSet<string>();

            foreach (var root in roots)
            {
                queue.Enqueue((root.Id, 0));
                visited.Add(root.Id);
                nodeDepths[root.Id] = 0;
            }

            while (queue.Count > 0)
            {
                var (currId, depth) = queue.Dequeue();
                if (childrenMap.TryGetValue(currId, out var children))
                {
                    foreach (var childId in children)
                    {
                        if (visited.Add(childId))
                        {
                            nodeDepths[childId] = depth + 1;
                            queue.Enqueue((childId, depth + 1));
                        }
                    }
                }
            }

            // Any disconnected unvisited nodes are placed at depth 0
            foreach (var node in nodes)
            {
                if (!nodeDepths.ContainsKey(node.Id))
                {
                    nodeDepths[node.Id] = 0;
                }
            }

            var levelCounts = new SortedDictionary<int, int>();
            foreach (var d in nodeDepths.Values)
            {
                levelCounts[d] = levelCounts.GetValueOrDefault(d, 0) + 1;
            }

            int maxDepth = levelCounts.Keys.Count > 0 ? levelCounts.Keys.Max() : 0;
            double avgDepth = nodeDepths.Values.Count > 0 ? Math.Round(nodeDepths.Values.Average(), 2) : 0.0;

            int widestLevel = 0;
            int maxWidth = 0;
            foreach (var (lvl, count) in levelCounts)
            {
                if (count > maxWidth)
                {
                    maxWidth = count;
                    widestLevel = lvl;
                }
            }

            string summary = FormatTopologicalDepthSummary(nodes.Count, roots.Count, maxDepth, avgDepth, widestLevel, maxWidth, levelCounts);
            return new TopologicalDepthAudit(nodes.Count, roots.Count, maxDepth, avgDepth, levelCounts, widestLevel, maxWidth, summary);
        }

        /// <summary>
        /// Formats a human-readable summary of topological depth metrics.
        /// </summary>
        public static string FormatTopologicalDepthSummary(
            int totalNodes, int rootCount, int maxDepth, double avgDepth, int widestLevel, int maxWidth,
            IReadOnlyDictionary<int, int>? levelCounts = null)
        {
            if (totalNodes <= 0)
                return "Topological depth audit: 0 nodes (Empty map)";

            string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string rootLabel = rootCount == 1 ? "1 root" : $"{rootCount} roots";
            int totalLevels = maxDepth + 1;
            string levelLabel = totalLevels == 1 ? "1 level" : $"{totalLevels} levels (Depth 0–{maxDepth})";

            string breakdown = "";
            if (levelCounts != null && levelCounts.Count > 0)
            {
                var parts = levelCounts.Take(4).Select(kv => $"L{kv.Key}:{kv.Value}");
                breakdown = $" [{string.Join(" · ", parts)}{(levelCounts.Count > 4 ? " · …" : "")}]";
            }

            return $"Topological depth audit ({nodeLabel} across {levelLabel}): {rootLabel} · Avg depth: {avgDepth:0.##} · Widest: Level {widestLevel} ({maxWidth} nodes){breakdown}";
        }

        /// <summary>
        /// Formats a human-readable summary of topological depth from a collection of nodes.
        /// </summary>
        public static string FormatTopologicalDepthSummary(
            IReadOnlyList<MindMapNodeViewModel> nodes,
            IReadOnlyList<MindMapLinkViewModel>? links = null)
        {
            return AuditTopologicalDepth(nodes, links).Summary;
        }

        /// <summary>
        /// Audits orphaned (disconnected) nodes and calculates Euclidean proximity to nearest connected clusters.
        /// </summary>
        public OrphanedNodeAudit AuditOrphanedNodes() => AuditOrphanedNodes(Nodes.ToList(), Links.ToList());

        /// <summary>
        /// Audits orphaned (disconnected) nodes from a collection of nodes and links.
        /// </summary>
        public static OrphanedNodeAudit AuditOrphanedNodes(
            IReadOnlyList<MindMapNodeViewModel> nodes,
            IReadOnlyList<MindMapLinkViewModel>? links = null)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return new OrphanedNodeAudit(
                    0, 0, 0, Array.Empty<string>(), 0.0,
                    "Orphaned nodes audit: 0 nodes (Empty map)");
            }

            var lList = links ?? Array.Empty<MindMapLinkViewModel>();
            var connectedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in lList)
            {
                if (!string.IsNullOrEmpty(l.SourceNodeId)) connectedIds.Add(l.SourceNodeId);
                if (!string.IsNullOrEmpty(l.TargetNodeId)) connectedIds.Add(l.TargetNodeId);
            }

            var orphans = nodes.Where(n => !connectedIds.Contains(n.Id)).ToList();
            var connected = nodes.Where(n => connectedIds.Contains(n.Id)).ToList();

            if (orphans.Count == 0)
            {
                string allStr = nodes.Count == 1 ? "1 node" : $"{nodes.Count} nodes";
                return new OrphanedNodeAudit(
                    nodes.Count, 0, nodes.Count, Array.Empty<string>(), 0.0,
                    $"Orphaned nodes audit ({allStr}): 0 orphans · Fully connected cluster network");
            }

            double totalDist = 0.0;
            int distCount = 0;
            foreach (var orphan in orphans)
            {
                var targets = connected.Count > 0 ? connected : nodes.Where(n => n.Id != orphan.Id).ToList();
                if (targets.Count > 0)
                {
                    double minDist = targets.Min(t => Math.Sqrt(Math.Pow(orphan.X - t.X, 2) + Math.Pow(orphan.Y - t.Y, 2)));
                    totalDist += minDist;
                    distCount++;
                }
            }

            double avgDist = distCount > 0 ? Math.Round(totalDist / distCount, 1) : 0.0;
            var orphanTitles = orphans.Select(o => string.IsNullOrWhiteSpace(o.Title) ? "Untitled" : o.Title).ToList();

            string summary = FormatOrphanedNodeAuditSummary(nodes.Count, orphans.Count, connected.Count, avgDist, orphanTitles);
            return new OrphanedNodeAudit(nodes.Count, orphans.Count, connected.Count, orphanTitles, avgDist, summary);
        }

        /// <summary>
        /// Formats a human-readable summary of orphaned node and cluster proximity metrics.
        /// </summary>
        public static string FormatOrphanedNodeAuditSummary(
            int totalNodes, int orphanCount, int connectedCount, double avgDist, IReadOnlyList<string>? orphanTitles = null)
        {
            if (totalNodes <= 0) return "Orphaned nodes audit: 0 nodes (Empty map)";
            string totalStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            if (orphanCount <= 0) return $"Orphaned nodes audit ({totalStr}): 0 orphans · Fully connected cluster network";

            string orphanLabel = orphanCount == 1 ? "1 orphan" : $"{orphanCount} orphans";
            string preview = "";
            if (orphanTitles != null && orphanTitles.Count > 0)
            {
                var sample = string.Join(", ", orphanTitles.Take(3).Select(t => $"'{t}'"));
                preview = $" [{sample}{(orphanTitles.Count > 3 ? ", …" : "")}]";
            }

            return $"Orphaned nodes audit ({totalStr}): {orphanLabel} disconnected ({connectedCount} connected · Avg distance to nearest cluster: {avgDist:0.#}px){preview}";
        }

        /// <summary>
        /// Connects each orphaned node to its closest cluster member or connected neighbor by Euclidean distance.
        /// </summary>
        public (int ConnectedCount, double AvgDistance, string Summary) ConnectOrphanedNodesToClosestCluster()
        {
            if (Nodes.Count <= 1)
            {
                string emptySummary = "Connect orphans: requires at least 2 nodes";
                StatusMessage = emptySummary;
                return (0, 0.0, emptySummary);
            }

            var connectedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in Links)
            {
                if (!string.IsNullOrEmpty(l.SourceNodeId)) connectedIds.Add(l.SourceNodeId);
                if (!string.IsNullOrEmpty(l.TargetNodeId)) connectedIds.Add(l.TargetNodeId);
            }

            var orphans = Nodes.Where(n => !connectedIds.Contains(n.Id)).ToList();
            if (orphans.Count == 0)
            {
                string allStr = Nodes.Count == 1 ? "1 node" : $"{Nodes.Count} nodes";
                string noOrphanSummary = $"Connect orphans: 0 orphaned nodes found ({allStr} already connected)";
                StatusMessage = noOrphanSummary;
                return (0, 0.0, noOrphanSummary);
            }

            PushUndo("Connect orphaned nodes to closest cluster");
            var connected = Nodes.Where(n => connectedIds.Contains(n.Id)).ToList();
            double totalDist = 0.0;
            int connectedCount = 0;

            using (SuspendEditTracking())
            {
                foreach (var orphan in orphans)
                {
                    var candidates = connected.Count > 0 ? connected : Nodes.Where(n => n.Id != orphan.Id).ToList();
                    if (candidates.Count == 0) continue;

                    var closest = candidates
                        .OrderBy(c => Math.Pow(orphan.X - c.X, 2) + Math.Pow(orphan.Y - c.Y, 2))
                        .First();

                    double dist = Math.Sqrt(Math.Pow(orphan.X - closest.X, 2) + Math.Pow(orphan.Y - closest.Y, 2));
                    totalDist += dist;

                    var linkModel = new MindMapLink
                    {
                        SourceNodeId = closest.Id,
                        TargetNodeId = orphan.Id,
                        Direction = MindMapLinkDirection.SourceToTarget,
                        Kind = MindMapLinkKind.Manual
                    };

                    Document.Links.Add(linkModel);
                    var linkVm = new MindMapLinkViewModel(linkModel);
                    Links.Add(linkVm);

                    connected.Add(orphan);
                    connectedIds.Add(orphan.Id);
                    connectedCount++;
                }
            }

            double avgDist = connectedCount > 0 ? Math.Round(totalDist / connectedCount, 1) : 0.0;
            RefreshConnectionCounts();
            MarkDirty($"Connected {connectedCount} orphaned nodes to nearest clusters (avg distance: {avgDist:0.#}px).");
            CanvasRedrawRequested?.Invoke(this, EventArgs.Empty);

            string summary = $"✓ Connected {connectedCount} orphaned nodes to nearest clusters (avg distance: {avgDist:0.#}px) · Ctrl+Z to undo";
            StatusMessage = summary;
            return (connectedCount, avgDist, summary);
        }

        /// <summary>
        /// Audits 2D spatial convex hulls and bounding envelope metrics across tag clusters or all nodes in the galaxy.
        /// </summary>
        public ClusterConvexHullAudit AuditClusterConvexHulls()
        {
            var audit = AuditClusterConvexHulls(Nodes);
            StatusMessage = audit.Summary;
            return audit;
        }

        /// <summary>
        /// Computes 2D spatial convex hulls, perimeters, enclosed areas, centroids, and node densities across tag clusters.
        /// </summary>
        public static ClusterConvexHullAudit AuditClusterConvexHulls(IEnumerable<MindMapNodeViewModel>? nodes)
        {
            var list = nodes?.ToList() ?? new List<MindMapNodeViewModel>();
            if (list.Count == 0)
            {
                return new ClusterConvexHullAudit(
                    0, 0, "None", 0.0, Array.Empty<TagClusterHullInfo>(),
                    "Cluster convex hull audit: 0 clusters, 0 nodes (Empty map)");
            }

            // Group by primary tag (or "Untagged")
            var groups = list
                .GroupBy(n => n.Tags.Count > 0 && !string.IsNullOrWhiteSpace(n.Tags[0]) ? n.Tags[0].Trim().TrimStart('#') : "Untagged")
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .ToList();

            var clusterInfos = new List<TagClusterHullInfo>();

            foreach (var grp in groups)
            {
                var pts = grp.Select(n => (n.X, n.Y)).Distinct().ToList();
                double minX = grp.Min(n => n.X);
                double maxX = grp.Max(n => n.X);
                double minY = grp.Min(n => n.Y);
                double maxY = grp.Max(n => n.Y);
                double bboxW = Math.Round(maxX - minX, 1);
                double bboxH = Math.Round(maxY - minY, 1);

                double hullArea = 0.0;
                double hullPerimeter = 0.0;
                double centroidX = Math.Round(grp.Average(n => n.X), 1);
                double centroidY = Math.Round(grp.Average(n => n.Y), 1);

                if (pts.Count == 1)
                {
                    hullArea = 0.0;
                    hullPerimeter = 0.0;
                }
                else if (pts.Count == 2)
                {
                    double d = Math.Sqrt(Math.Pow(pts[1].X - pts[0].X, 2) + Math.Pow(pts[1].Y - pts[0].Y, 2));
                    hullPerimeter = Math.Round(2.0 * d, 1);
                    hullArea = 0.0;
                }
                else
                {
                    var hull = ComputeConvexHull2D(pts);
                    hullPerimeter = Math.Round(ComputePolygonPerimeter(hull), 1);
                    hullArea = Math.Round(ComputePolygonArea(hull), 1);

                    if (hullArea > 0.0)
                    {
                        var (cx, cy) = ComputePolygonCentroid(hull, hullArea);
                        centroidX = Math.Round(cx, 1);
                        centroidY = Math.Round(cy, 1);
                    }
                }

                double density = hullArea > 0.0 ? Math.Round((grp.Count() / hullArea) * 1000.0, 2) : 0.0;

                clusterInfos.Add(new TagClusterHullInfo(
                    grp.Key,
                    grp.Count(),
                    hullArea,
                    hullPerimeter,
                    centroidX,
                    centroidY,
                    bboxW,
                    bboxH,
                    density));
            }

            var largest = clusterInfos.OrderByDescending(c => c.HullArea).First();
            string clusterLabel = clusterInfos.Count == 1 ? "1 cluster" : $"{clusterInfos.Count} clusters";
            string nodeLabel = list.Count == 1 ? "1 node" : $"{list.Count} nodes";

            string summary = $"Cluster convex hull audit ({clusterLabel}, {nodeLabel}): Largest '{largest.Tag}' ({largest.HullArea:N0}px², {largest.Density:0.##} nodes/kpx² at centroid {largest.CentroidX:0.#},{largest.CentroidY:0.#})";

            return new ClusterConvexHullAudit(
                clusterInfos.Count,
                list.Count,
                largest.Tag,
                largest.HullArea,
                clusterInfos,
                summary);
        }

        private static List<(double X, double Y)> ComputeConvexHull2D(List<(double X, double Y)> points)
        {
            var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            var lower = new List<(double X, double Y)>();
            foreach (var p in sorted)
            {
                while (lower.Count >= 2 && CrossProduct(lower[^2], lower[^1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<(double X, double Y)>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var p = sorted[i];
                while (upper.Count >= 2 && CrossProduct(upper[^2], upper[^1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double CrossProduct((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private static double ComputePolygonPerimeter(List<(double X, double Y)> hull)
        {
            if (hull.Count < 2) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < hull.Count; i++)
            {
                var p1 = hull[i];
                var p2 = hull[(i + 1) % hull.Count];
                sum += Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
            }
            return sum;
        }

        private static double ComputePolygonArea(List<(double X, double Y)> hull)
        {
            if (hull.Count < 3) return 0.0;
            double area = 0.0;
            for (int i = 0; i < hull.Count; i++)
            {
                var p1 = hull[i];
                var p2 = hull[(i + 1) % hull.Count];
                area += (p1.X * p2.Y) - (p2.X * p1.Y);
            }
            return Math.Abs(area) * 0.5;
        }

        private static (double X, double Y) ComputePolygonCentroid(List<(double X, double Y)> hull, double area)
        {
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < hull.Count; i++)
            {
                var p1 = hull[i];
                var p2 = hull[(i + 1) % hull.Count];
                double factor = (p1.X * p2.Y) - (p2.X * p1.Y);
                cx += (p1.X + p2.X) * factor;
                cy += (p1.Y + p2.Y) * factor;
            }
            double denom = 6.0 * area;
            return (Math.Abs(denom) > 1e-6 ? cx / denom : 0.0, Math.Abs(denom) > 1e-6 ? cy / denom : 0.0);
        }

        /// <summary>
        /// Audits the 2D Minimum Spanning Tree (MST) spatial interconnect wireframe across canvas nodes
        /// using Kruskal's algorithm, identifying minimum routing length, trunk corridors, and edge spans.
        /// </summary>
        public MinimumSpanningTreeAudit AuditMinimumSpanningTree(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            List<MindMapNodeViewModel> nodeList;

            if (targetNodes != null)
            {
                nodeList = targetNodes.ToList();
            }
            else
            {
                var selected = Nodes.Where(n => n.IsSelected).ToList();
                if (selected.Count >= 2)
                {
                    nodeList = selected;
                    isSelection = true;
                }
                else
                {
                    nodeList = Nodes.ToList();
                }
            }

            int n = nodeList.Count;
            string scopeStr = isSelection ? $"Selection: {n} nodes" : $"Canvas: {n} nodes";

            if (n < 2)
            {
                string emptySummary = $"Minimum Spanning Tree (MST: 0 edges · 0px wire · {scopeStr}): Insufficient nodes for spatial wireframe";
                StatusMessage = emptySummary;
                return new MinimumSpanningTreeAudit(
                    0, n, 0.0, 0.0, 0.0, 0,
                    Array.Empty<MstEdgeInfo>(), Array.Empty<string>(), isSelection, emptySummary);
            }

            // Build all pairwise edges
            var allEdges = new List<(int U, int V, double Dist)>();
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double dx = nodeList[i].X - nodeList[j].X;
                    double dy = nodeList[i].Y - nodeList[j].Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    allEdges.Add((i, j, dist));
                }
            }

            allEdges.Sort((a, b) => a.Dist.CompareTo(b.Dist));

            // Disjoint Set Union (DSU) for Kruskal's
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x)
            {
                if (parent[x] == x) return x;
                return parent[x] = Find(parent[x]);
            }

            bool Union(int x, int y)
            {
                int rx = Find(x);
                int ry = Find(y);
                if (rx == ry) return false;
                parent[rx] = ry;
                return true;
            }

            var mstEdges = new List<MstEdgeInfo>();
            var degrees = new int[n];
            double totalLength = 0.0;
            double maxEdge = 0.0;

            foreach (var edge in allEdges)
            {
                if (Union(edge.U, edge.V))
                {
                    degrees[edge.U]++;
                    degrees[edge.V]++;
                    totalLength += edge.Dist;
                    if (edge.Dist > maxEdge) maxEdge = edge.Dist;

                    var uNode = nodeList[edge.U];
                    var vNode = nodeList[edge.V];
                    mstEdges.Add(new MstEdgeInfo(
                        uNode.Id,
                        string.IsNullOrWhiteSpace(uNode.Title) ? $"Node-{edge.U + 1}" : uNode.Title,
                        vNode.Id,
                        string.IsNullOrWhiteSpace(vNode.Title) ? $"Node-{edge.V + 1}" : vNode.Title,
                        Math.Round(edge.Dist, 1)));

                    if (mstEdges.Count == n - 1) break;
                }
            }

            double avgEdge = mstEdges.Count > 0 ? Math.Round(totalLength / mstEdges.Count, 1) : 0.0;
            totalLength = Math.Round(totalLength, 1);
            maxEdge = Math.Round(maxEdge, 1);

            // Identify trunk corridors: nodes with degree >= 3 in the MST
            var trunkNodes = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (degrees[i] >= 3)
                {
                    string title = string.IsNullOrWhiteSpace(nodeList[i].Title) ? $"Node-{i + 1}" : nodeList[i].Title;
                    trunkNodes.Add($"{title} (deg:{degrees[i]})");
                }
            }

            string summary = FormatMstSummary(mstEdges.Count, totalLength, trunkNodes.Count, avgEdge, maxEdge, scopeStr);
            StatusMessage = summary;

            return new MinimumSpanningTreeAudit(
                mstEdges.Count, n, totalLength, avgEdge, maxEdge, trunkNodes.Count,
                mstEdges, trunkNodes, isSelection, summary);
        }

        /// <summary>
        /// Formats a human-readable summary of Minimum Spanning Tree (MST) spatial wireframe metrics.
        /// </summary>
        public static string FormatMstSummary(int edgeCount, double totalLength, int trunkCount, double avgLength, double maxLength, string scope = "Full Canvas")
        {
            if (edgeCount == 0) return $"Minimum Spanning Tree (MST: 0 edges · 0px wire · {scope}): Insufficient nodes";
            string edgeLabel = edgeCount == 1 ? "1 edge" : $"{edgeCount} edges";
            string trunkLabel = trunkCount == 1 ? "1 trunk corridor" : $"{trunkCount} trunk corridors";
            return $"Minimum Spanning Tree (MST: {edgeLabel} · {totalLength:N0}px wire · {scope}): {trunkLabel}, avg {avgLength:0.#}px (Max: {maxLength:0.#}px)";
        }

        /// <summary>
        /// Audits the 2D Minimum Spanning Tree (MST) spatial wireframe across pure model nodes.
        /// </summary>
        public static MinimumSpanningTreeAudit AuditMinimumSpanningTree(IEnumerable<MindMapNode>? nodes)
        {
            var vms = (nodes ?? Array.Empty<MindMapNode>()).Select(n => new MindMapNodeViewModel(n)).ToList();
            var dummy = new MindMapStudioViewModel();
            return dummy.AuditMinimumSpanningTree(vms);
        }

        /// <summary>
        /// Audits node degree centrality, focal hubs, terminal leaf nodes, and average node connectivity across the mind map graph.
        /// </summary>
        public DegreeCentralityAudit AuditDegreeCentrality(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            List<MindMapNodeViewModel> nodeList;

            if (targetNodes != null)
            {
                nodeList = targetNodes.ToList();
                isSelection = true;
            }
            else
            {
                var selected = Nodes.Where(n => n.IsSelected).ToList();
                if (selected.Count >= 2)
                {
                    nodeList = selected;
                    isSelection = true;
                }
                else
                {
                    nodeList = Nodes.ToList();
                }
            }

            int n = nodeList.Count;
            string scopeStr = isSelection ? $"Selection: {n} nodes" : $"Canvas: {n} nodes";

            if (n == 0)
            {
                string emptySummary = $"Graph degree centrality (0 nodes, 0 links · {scopeStr}): Insufficient nodes";
                StatusMessage = emptySummary;
                return new DegreeCentralityAudit(
                    0, 0, 0.0, 0, "None", 0, 0,
                    Array.Empty<NodeDegreeInfo>(), isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(nodeList.Select(node => node.Id), StringComparer.Ordinal);
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            var outDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in nodeList)
            {
                inDegree[node.Id] = 0;
                outDegree[node.Id] = 0;
            }

            int linkCount = 0;
            foreach (var l in Links)
            {
                if (nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId))
                {
                    linkCount++;
                    outDegree[l.SourceNodeId]++;
                    inDegree[l.TargetNodeId]++;
                }
            }

            var degreeInfos = new List<NodeDegreeInfo>();
            int leafCount = 0;
            int orphanCount = 0;
            int totalDegreeSum = 0;
            int maxDegree = 0;
            string topHubTitle = "None";

            foreach (var node in nodeList)
            {
                int inD = inDegree[node.Id];
                int outD = outDegree[node.Id];
                int totalD = inD + outD;
                totalDegreeSum += totalD;

                if (totalD > maxDegree)
                {
                    maxDegree = totalD;
                    topHubTitle = string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title;
                }

                if (totalD == 0) orphanCount++;
                else if (totalD == 1) leafCount++;

                string role = totalD switch
                {
                    0 => "Isolated Orphan",
                    1 => "Terminal Leaf",
                    >= 4 => "Major Focal Hub",
                    >= 2 => "Connected Branch",
                    _ => "Node"
                };

                degreeInfos.Add(new NodeDegreeInfo(
                    node.Id,
                    string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title,
                    inD, outD, totalD, role));
            }

            double avgDegree = n > 0 ? Math.Round((double)totalDegreeSum / n, 1) : 0.0;
            string summary = FormatDegreeCentralitySummary(
                n, linkCount, avgDegree, maxDegree, topHubTitle, leafCount, orphanCount, scopeStr);
            StatusMessage = summary;

            return new DegreeCentralityAudit(
                n, linkCount, avgDegree, maxDegree, topHubTitle,
                leafCount, orphanCount, degreeInfos, isSelection, summary);
        }

        /// <summary>
        /// Formats a human-readable summary of graph degree centrality and node hub metrics.
        /// </summary>
        public static string FormatDegreeCentralitySummary(
            int nodeCount, int linkCount, double avgDegree, int maxDegree, string topHub, int leafCount, int orphanCount, string scope = "Full Canvas")
        {
            if (nodeCount == 0) return $"Graph degree centrality (0 nodes, 0 links · {scope}): Insufficient nodes";
            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string leafLabel = leafCount == 1 ? "1 leaf" : $"{leafCount} leaves";
            string orphanLabel = orphanCount == 1 ? "1 orphan" : $"{orphanCount} orphans";

            string hubStr = maxDegree > 0 ? $"Top hub '{topHub}' (deg:{maxDegree}), " : "";
            return $"Graph degree centrality ({nodeLabel}, {linkLabel} · {scope}): {hubStr}{leafLabel}, {orphanLabel} · avg deg {avgDegree:0.#} (Max: {maxDegree})";
        }

        /// <summary>
        /// Audits degree centrality across pure model nodes and links.
        /// </summary>
        public static DegreeCentralityAudit AuditDegreeCentrality(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditDegreeCentrality();
        }

        /// <summary>
        /// Audits leaf node termination depth, graph diameter/eccentricity from root, and peripheral leaf nodes across the mind map.
        /// </summary>
        public PeripheryEccentricityAudit AuditPeripheryEccentricity(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            List<MindMapNodeViewModel> nodeList;

            if (targetNodes != null)
            {
                nodeList = targetNodes.ToList();
                isSelection = true;
            }
            else
            {
                var selected = Nodes.Where(n => n.IsSelected).ToList();
                if (selected.Count >= 2)
                {
                    nodeList = selected;
                    isSelection = true;
                }
                else
                {
                    nodeList = Nodes.ToList();
                }
            }

            string scopeStr = isSelection ? "Selected Nodes" : "Full Canvas";

            if (nodeList.Count == 0)
            {
                string emptySummary = $"Periphery eccentricity (0 nodes · {scopeStr}): Insufficient nodes";
                StatusMessage = emptySummary;
                return new PeripheryEccentricityAudit(
                    0, 0, 0, 0.0, Array.Empty<string>(), 0, "No Nodes", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(nodeList.Select(n => n.Id), StringComparer.Ordinal);
            var activeLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var totalDegree = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var node in nodeList)
            {
                adj[node.Id] = new List<string>();
                totalDegree[node.Id] = 0;
            }

            foreach (var l in activeLinks)
            {
                if (adj.ContainsKey(l.SourceNodeId) && adj.ContainsKey(l.TargetNodeId))
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                    totalDegree[l.SourceNodeId]++;
                    totalDegree[l.TargetNodeId]++;
                }
            }

            var root = nodeList.FirstOrDefault(n => Document != null && n.Id == Document.RootNodeId)
                       ?? nodeList.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId) && totalDegree[n.Id] > 0)
                       ?? nodeList.OrderByDescending(n => totalDegree[n.Id]).FirstOrDefault()
                       ?? nodeList[0];

            var distances = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            distances[root.Id] = 0;
            queue.Enqueue(root.Id);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int d = distances[curr];
                foreach (var neighbor in adj[curr])
                {
                    if (!distances.ContainsKey(neighbor))
                    {
                        distances[neighbor] = d + 1;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            var leafNodes = nodeList.Where(n => (totalDegree[n.Id] <= 1 && n.Id != root.Id) || (nodeList.Count == 1)).ToList();
            if (leafNodes.Count == 0 && nodeList.Count > 1)
            {
                leafNodes = nodeList.Where(n => n.Id != root.Id).ToList();
            }

            int maxEccentricity = distances.Values.Count > 0 ? distances.Values.Max() : 0;
            var reachableLeaves = leafNodes.Where(n => distances.ContainsKey(n.Id)).ToList();
            double avgLeafDepth = reachableLeaves.Count > 0
                ? Math.Round(reachableLeaves.Average(n => distances[n.Id]), 1)
                : 0.0;

            var deepestLeaves = reachableLeaves
                .Where(n => distances[n.Id] == maxEccentricity)
                .Select(n => string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title)
                .Take(3)
                .ToList();

            int peripheralCount = distances.Values.Count(d => d >= Math.Max(1, maxEccentricity - 1));

            string category;
            if (maxEccentricity <= 1) category = "Compact / Star Hub";
            else if (maxEccentricity <= 3) category = "Balanced Tree";
            else if (maxEccentricity <= 5) category = "Deep Branching";
            else category = "Elongated Linear Chain";

            string summary = FormatPeripheryEccentricitySummary(
                nodeList.Count, leafNodes.Count, maxEccentricity, avgLeafDepth, category, deepestLeaves, scopeStr);
            StatusMessage = summary;

            return new PeripheryEccentricityAudit(
                nodeList.Count,
                leafNodes.Count,
                maxEccentricity,
                avgLeafDepth,
                deepestLeaves,
                peripheralCount,
                category,
                isSelection,
                summary);
        }

        /// <summary>
        /// Formats a human-readable summary of leaf node termination depth and periphery eccentricity metrics.
        /// </summary>
        public static string FormatPeripheryEccentricitySummary(
            int totalNodes, int leafCount, int maxEccentricity, double avgLeafDepth, string category,
            IReadOnlyList<string>? deepestLeaves = null, string scope = "Full Canvas")
        {
            if (totalNodes <= 0) return $"Periphery eccentricity (0 nodes · {scope}): Insufficient nodes";
            string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string leafLabel = leafCount == 1 ? "1 leaf" : $"{leafCount} leaves";

            string topStr = "";
            if (deepestLeaves != null && deepestLeaves.Count > 0)
            {
                topStr = $" (top: '{string.Join("', '", deepestLeaves)}')";
            }

            return $"Periphery eccentricity ({nodeLabel}, {leafLabel} · {scope}): Max depth {maxEccentricity}{topStr}, avg leaf depth {avgLeafDepth:0.#} · {category}";
        }

        /// <summary>
        /// Audits periphery eccentricity across pure model nodes and links.
        /// </summary>
        public static PeripheryEccentricityAudit AuditPeripheryEccentricity(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditPeripheryEccentricity();
        }

        /// <summary>
        /// Audits radial layout concentric ring distribution, polar angles from root, and angular spacing uniformity.
        /// Evaluates angular dispersion, detects clustered wedges vs balanced distribution across branches.
        /// </summary>
        public RadialAngularSpacingAudit AuditRadialAngularSpacing(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            List<MindMapNodeViewModel> nodeList;

            if (targetNodes != null)
            {
                nodeList = targetNodes.ToList();
                isSelection = true;
            }
            else
            {
                var selected = Nodes.Where(n => n.IsSelected).ToList();
                if (selected.Count >= 2)
                {
                    nodeList = selected;
                    isSelection = true;
                }
                else
                {
                    nodeList = Nodes.ToList();
                }
            }

            if (nodeList.Count < 2)
            {
                return new RadialAngularSpacingAudit(
                    nodeList.Count, 0, 0.0, 0.0, 0.0, 0.0,
                    Array.Empty<RadialNodePolarInfo>(), "Insufficient Nodes", isSelection,
                    $"Radial angular spacing ({nodeList.Count} nodes): Insufficient nodes to audit angular dispersion (minimum 2 nodes required)");
            }

            // Determine root or anchor center
            MindMapNodeViewModel? rootNode = null;
            if (!string.IsNullOrEmpty(Document.RootNodeId))
            {
                rootNode = nodeList.FirstOrDefault(n => n.Id == Document.RootNodeId);
            }
            rootNode ??= nodeList[0];

            double rootX = rootNode.X;
            double rootY = rootNode.Y;

            var polarList = new List<RadialNodePolarInfo>();
            var angles = new List<double>();

            foreach (var node in nodeList)
            {
                if (node.Id == rootNode.Id) continue;

                double dx = node.X - rootX;
                double dy = node.Y - rootY;
                double radius = Math.Round(Math.Sqrt(dx * dx + dy * dy), 1);

                // Compute angle in degrees [0, 360)
                double rad = Math.Atan2(dy, dx);
                double deg = rad * (180.0 / Math.PI);
                if (deg < 0) deg += 360.0;
                deg = Math.Round(deg, 1);

                int ringBand = radius < 250.0 ? 1 : (radius < 500.0 ? 2 : 3);
                polarList.Add(new RadialNodePolarInfo(node.Id, node.Title, radius, deg, ringBand));
                angles.Add(deg);
            }

            if (angles.Count == 0)
            {
                return new RadialAngularSpacingAudit(
                    nodeList.Count, 0, 0.0, 0.0, 0.0, 0.0,
                    polarList, "Concentric Overlap", isSelection,
                    $"Radial angular spacing ({nodeList.Count} nodes): All nodes coincident with root anchor");
            }

            angles.Sort();

            // Compute circular angular gaps
            var gaps = new List<double>();
            for (int i = 0; i < angles.Count - 1; i++)
            {
                gaps.Add(angles[i + 1] - angles[i]);
            }
            // Wrap around gap from last to first
            gaps.Add((360.0 - angles[angles.Count - 1]) + angles[0]);

            double minGap = Math.Round(gaps.Min(), 1);
            double maxGap = Math.Round(gaps.Max(), 1);
            double avgGap = Math.Round(360.0 / angles.Count, 1);

            // Standard deviation of gaps
            double sumSqDiff = gaps.Sum(g => Math.Pow(g - avgGap, 2));
            double stdDev = Math.Round(Math.Sqrt(sumSqDiff / gaps.Count), 1);

            double maxRadius = polarList.Max(p => p.Radius);

            string category;
            if (angles.Count < 3)
            {
                category = "Bipolar Pair";
            }
            else if (stdDev <= 15.0)
            {
                category = "Uniform Radial Dispersion (≤15° SD)";
            }
            else if (stdDev <= 35.0)
            {
                category = "Moderate Angular Balance (16–35° SD)";
            }
            else if (maxGap >= 180.0)
            {
                category = "Hemispheric / One-Sided Cluster (≥180° Gap)";
            }
            else
            {
                category = "Clustered Wedges (>35° SD)";
            }

            string scopeTag = isSelection ? "Selection " : "";
            string summary = $"Radial angular spacing ({scopeTag}{angles.Count} branches, root '{rootNode.Title}' · {category}): avg gap {avgGap:0.#}°, range {minGap:0.#}°–{maxGap:0.#}° (SD {stdDev:0.#}°, max R: {maxRadius:0.#}px)";

            return new RadialAngularSpacingAudit(
                nodeList.Count, angles.Count, avgGap, minGap, maxGap, stdDev,
                polarList, category, isSelection, summary);
        }

        /// <summary>
        /// Audits radial angular spacing across pure model nodes and links.
        /// </summary>
        public static RadialAngularSpacingAudit AuditRadialAngularSpacing(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditRadialAngularSpacing();
        }

        /// <summary>
        /// Audits semantic tag cluster cohesion, centroid dispersion, and density across nodes.
        /// </summary>
        public ClusterCohesionAudit AuditClusterCohesionAndDensity(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            var selectedList = Nodes.Where(n => n.IsSelected).ToList();
            var nodeList = (targetNodes ?? (selectedList.Count >= 2 ? selectedList : Nodes)).ToList();
            bool isSelection = targetNodes != null || selectedList.Count >= 2;
            string scopeTag = isSelection ? "selection · " : "";

            if (nodeList.Count < 2)
            {
                return new ClusterCohesionAudit(
                    nodeList.Count, 0, nodeList.Count, 0, 0.0, 0.0,
                    Array.Empty<ClusterMetricInfo>(), "Insufficient Nodes", isSelection,
                    $"Cluster cohesion ({nodeList.Count} nodes): Insufficient nodes to audit cluster cohesion (minimum 2 nodes required)");
            }

            // Group nodes by tags
            var clusters = new List<ClusterMetricInfo>();
            var taggedNodeIds = new HashSet<string>();
            var tagGroups = new Dictionary<string, List<MindMapNodeViewModel>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodeList)
            {
                if (node.Tags.Count == 0) continue;
                foreach (var tag in node.Tags)
                {
                    taggedNodeIds.Add(node.Id);
                    if (!tagGroups.TryGetValue(tag, out var group))
                    {
                        group = new List<MindMapNodeViewModel>();
                        tagGroups[tag] = group;
                    }
                    group.Add(node);
                }
            }

            int orphanCount = nodeList.Count - taggedNodeIds.Count;
            var linkList = Links.ToList();

            double totalWeightedRadius = 0.0;
            int totalClusteredNodes = 0;

            foreach (var kvp in tagGroups.OrderByDescending(g => g.Value.Count).ThenBy(g => g.Key))
            {
                string tag = kvp.Key;
                var groupNodes = kvp.Value;
                int count = groupNodes.Count;
                if (count < 2) continue; // single-node tags don't form clusters

                double sumX = groupNodes.Sum(n => n.X);
                double sumY = groupNodes.Sum(n => n.Y);
                double centX = Math.Round(sumX / count, 1);
                double centY = Math.Round(sumY / count, 1);

                double sumR = 0.0;
                foreach (var n in groupNodes)
                {
                    double dx = n.X - centX;
                    double dy = n.Y - centY;
                    sumR += Math.Sqrt(dx * dx + dy * dy);
                }
                double meanR = Math.Round(sumR / count, 1);

                var groupIds = new HashSet<string>(groupNodes.Select(n => n.Id));
                int internalEdges = linkList.Count(l => groupIds.Contains(l.SourceNodeId) && groupIds.Contains(l.TargetNodeId));
                double density = Math.Round((double)internalEdges / Math.Max(1.0, meanR * 0.01 + 1.0), 2);

                clusters.Add(new ClusterMetricInfo(tag, count, internalEdges, centX, centY, meanR, density));

                totalWeightedRadius += meanR * count;
                totalClusteredNodes += count;
            }

            double avgRadius = totalClusteredNodes > 0 ? Math.Round(totalWeightedRadius / totalClusteredNodes, 1) : 0.0;

            double cohesionScore = totalClusteredNodes > 0
                ? Math.Round(Math.Clamp(1.0 - (avgRadius / 800.0), 0.0, 1.0), 2)
                : 0.0;

            string category;
            if (clusters.Count == 0)
            {
                category = "No Multi-Node Clusters";
            }
            else if (cohesionScore >= 0.70)
            {
                category = "Tight Semantic Constellations";
            }
            else if (cohesionScore >= 0.40)
            {
                category = "Balanced Semantic Groupings";
            }
            else
            {
                category = "Dispersed Semantic Clouds";
            }

            string summary = $"Cluster cohesion ({scopeTag}{clusters.Count} clusters, {nodeList.Count} nodes · {category}): avg radius {avgRadius:0.#}px, cohesion {cohesionScore:0.00}, {orphanCount} untagged";

            return new ClusterCohesionAudit(
                nodeList.Count, totalClusteredNodes, orphanCount, clusters.Count,
                avgRadius, cohesionScore, clusters, category, isSelection, summary);
        }

        /// <summary>
        /// Audits cluster cohesion and density across pure model nodes and links.
        /// </summary>
        public static ClusterCohesionAudit AuditClusterCohesionAndDensity(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditClusterCohesionAndDensity();
        }

        /// <summary>
        /// Audits the mind map graph (or selected subgraph) for 2-colorability (bipartite property),
        /// detecting odd-length cycles that prevent two-tier partitioning (e.g. left/right or producer/consumer).
        /// </summary>
        public BipartitePartitionAudit AuditBipartitePartitioning()
        {
            var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
            bool isSelection = selectedNodes.Count > 1;
            var targetNodes = isSelection ? selectedNodes : Nodes.ToList();

            if (targetNodes.Count == 0)
            {
                string emptySummary = "Bipartite partition audit: 0 nodes · Map is empty";
                StatusMessage = emptySummary;
                return new BipartitePartitionAudit(0, 0, true, 0, 0, 1.0, "None", "Empty Graph", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.Ordinal);
            var relevantLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            // Build undirected adjacency list
            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in targetNodes) adj[n.Id] = new List<string>();

            foreach (var l in relevantLinks)
            {
                if (l.SourceNodeId == l.TargetNodeId)
                {
                    // Self-loop immediately makes graph non-bipartite
                    string selfLoopSummary = $"Bipartite partition audit ({targetNodes.Count} nodes · Non-Bipartite): Self-loop detected on '{targetNodes.FirstOrDefault(n => n.Id == l.SourceNodeId)?.Title ?? l.SourceNodeId}'";
                    StatusMessage = selfLoopSummary;
                    return new BipartitePartitionAudit(
                        targetNodes.Count, relevantLinks.Count, false, 0, 0, 0.0,
                        $"Self-loop on {l.SourceNodeId}", "Non-Bipartite (Self-Loop)", isSelection, selfLoopSummary);
                }
                if (!adj[l.SourceNodeId].Contains(l.TargetNodeId)) adj[l.SourceNodeId].Add(l.TargetNodeId);
                if (!adj[l.TargetNodeId].Contains(l.SourceNodeId)) adj[l.TargetNodeId].Add(l.SourceNodeId);
            }

            var colors = new Dictionary<string, int>(StringComparer.Ordinal);
            var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
            bool isBipartite = true;
            string oddCycleDescription = "None";

            foreach (var startNode in targetNodes)
            {
                if (colors.ContainsKey(startNode.Id)) continue;

                // BFS
                var queue = new Queue<string>();
                queue.Enqueue(startNode.Id);
                colors[startNode.Id] = 1;
                parents[startNode.Id] = null;

                while (queue.Count > 0 && isBipartite)
                {
                    var cur = queue.Dequeue();
                    int curColor = colors[cur];
                    int nextColor = curColor == 1 ? 2 : 1;

                    foreach (var neighbor in adj[cur])
                    {
                        if (!colors.TryGetValue(neighbor, out int nColor))
                        {
                            colors[neighbor] = nextColor;
                            parents[neighbor] = cur;
                            queue.Enqueue(neighbor);
                        }
                        else if (nColor == curColor)
                        {
                            // Odd cycle detected!
                            isBipartite = false;
                            string curTitle = targetNodes.FirstOrDefault(n => n.Id == cur)?.Title ?? cur;
                            string neighborTitle = targetNodes.FirstOrDefault(n => n.Id == neighbor)?.Title ?? neighbor;
                            oddCycleDescription = $"Odd cycle between '{curTitle}' and '{neighborTitle}'";
                            break;
                        }
                    }
                }
                if (!isBipartite) break;
            }

            int setACount = colors.Values.Count(c => c == 1);
            int setBCount = colors.Values.Count(c => c == 2);
            double balance = isBipartite && targetNodes.Count > 0
                ? Math.Round((double)Math.Min(setACount, setBCount) / Math.Max(1, Math.Max(setACount, setBCount)), 2)
                : 0.0;

            string category;
            if (!isBipartite)
            {
                category = "Non-Bipartite (Odd Cycle)";
            }
            else if (relevantLinks.Count == 0)
            {
                category = "Trivially Bipartite (No Edges)";
            }
            else if (balance >= 0.70)
            {
                category = "Balanced Bipartite Partition";
            }
            else
            {
                category = "Asymmetric Bipartite Partition";
            }

            string scopeTag = isSelection ? "Selection · " : "";
            string summary;
            if (isBipartite)
            {
                summary = $"Bipartite partition audit ({scopeTag}{targetNodes.Count} nodes, {relevantLinks.Count} links · {category}): Set A = {setACount}, Set B = {setBCount} (Balance {balance:0.00})";
            }
            else
            {
                summary = $"Bipartite partition audit ({scopeTag}{targetNodes.Count} nodes, {relevantLinks.Count} links · {category}): {oddCycleDescription}";
            }

            StatusMessage = summary;
            return new BipartitePartitionAudit(
                targetNodes.Count, relevantLinks.Count, isBipartite, setACount, setBCount, balance,
                oddCycleDescription, category, isSelection, summary);
        }

        /// <summary>
        /// Audits bipartite partitioning across pure model nodes and links.
        /// </summary>
        public static BipartitePartitionAudit AuditBipartitePartitioning(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditBipartitePartitioning();
        }

        /// <summary>
        /// Audits the mind map graph (or selected subgraph) for Directed Acyclic Graph (DAG) acyclic validity,
        /// detecting dependency feedback cycles and measuring topological sequence depth and entry/terminal tiers.
        /// </summary>
        public TopologicalOrderAudit AuditTopologicalOrderAndDepth()
        {
            var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
            bool isSelection = selectedNodes.Count > 1;
            var targetNodes = isSelection ? selectedNodes : Nodes.ToList();

            if (targetNodes.Count == 0)
            {
                string emptySummary = "Topological DAG audit: 0 nodes · Map is empty";
                StatusMessage = emptySummary;
                return new TopologicalOrderAudit(
                    0, 0, true, 0, 0, 0, Array.Empty<string>(), "None", "Empty Graph", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.Ordinal);
            var relevantLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            // Directed adjacency and in-degree tracking
            var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in targetNodes)
            {
                outgoing[n.Id] = new List<string>();
                inDegree[n.Id] = 0;
            }

            // Detect immediate self-loops
            foreach (var l in relevantLinks)
            {
                if (l.SourceNodeId == l.TargetNodeId)
                {
                    string selfNodeTitle = targetNodes.FirstOrDefault(n => n.Id == l.SourceNodeId)?.Title ?? l.SourceNodeId;
                    string selfSummary = $"Topological DAG audit ({targetNodes.Count} nodes · Cyclic Dependency Loop): Self-loop detected on '{selfNodeTitle}'";
                    StatusMessage = selfSummary;
                    return new TopologicalOrderAudit(
                        targetNodes.Count, relevantLinks.Count, false, 0, 0, 0,
                        Array.Empty<string>(), $"Self-loop on '{selfNodeTitle}'", "Cyclic Dependency Loop", isSelection, selfSummary);
                }

                outgoing[l.SourceNodeId].Add(l.TargetNodeId);
                inDegree[l.TargetNodeId]++;
            }

            // Kahn's Algorithm for Topological Sort & Longest Path Depth
            var inDegreeCopy = new Dictionary<string, int>(inDegree, StringComparer.Ordinal);
            var queue = new Queue<string>();
            var nodeDepth = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var n in targetNodes)
            {
                if (inDegreeCopy[n.Id] == 0)
                {
                    queue.Enqueue(n.Id);
                    nodeDepth[n.Id] = 0;
                }
            }

            int entryPointsCount = queue.Count;
            var orderedIds = new List<string>();

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                orderedIds.Add(cur);
                int curD = nodeDepth.TryGetValue(cur, out int d) ? d : 0;

                foreach (var next in outgoing[cur])
                {
                    int nextD = curD + 1;
                    if (!nodeDepth.TryGetValue(next, out int existingD) || nextD > existingD)
                    {
                        nodeDepth[next] = nextD;
                    }

                    inDegreeCopy[next]--;
                    if (inDegreeCopy[next] == 0)
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            bool isAcyclic = orderedIds.Count == targetNodes.Count;
            int maxSequenceDepth = nodeDepth.Count > 0 ? nodeDepth.Values.Max() : 0;
            int terminalSinksCount = targetNodes.Count(n => outgoing[n.Id].Count == 0);

            var titleMap = targetNodes.ToDictionary(n => n.Id, n => n.Title, StringComparer.Ordinal);
            var orderedTitles = orderedIds.Select(id => titleMap.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t) ? t : id).ToList();

            string cycleDesc = "None";
            if (!isAcyclic)
            {
                var cycleNodes = targetNodes.Where(n => inDegreeCopy[n.Id] > 0).ToList();
                var sampleCycleTitles = cycleNodes.Take(3).Select(n => string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title);
                cycleDesc = $"Cycle in: {string.Join(" ↔ ", sampleCycleTitles)}";
            }

            string category;
            if (!isAcyclic)
            {
                category = "Cyclic Dependency Loop";
            }
            else if (relevantLinks.Count == 0)
            {
                category = "Flat Independent Nodes";
            }
            else if (maxSequenceDepth == targetNodes.Count - 1 && targetNodes.Count > 1)
            {
                category = "Linear Sequential Pipeline";
            }
            else if (maxSequenceDepth >= 3)
            {
                category = $"Deep Layered DAG (Depth {maxSequenceDepth})";
            }
            else
            {
                category = $"Shallow Layered DAG (Depth {maxSequenceDepth})";
            }

            string scopeTag = isSelection ? "Selection · " : "";
            string summary;
            if (isAcyclic)
            {
                summary = $"Topological DAG audit ({scopeTag}{targetNodes.Count} nodes, {relevantLinks.Count} links · {category}): Max depth {maxSequenceDepth}, {entryPointsCount} entry points, {terminalSinksCount} sinks";
            }
            else
            {
                summary = $"Topological DAG audit ({scopeTag}{targetNodes.Count} nodes, {relevantLinks.Count} links · {category}): {cycleDesc}";
            }

            StatusMessage = summary;
            return new TopologicalOrderAudit(
                targetNodes.Count, relevantLinks.Count, isAcyclic, maxSequenceDepth,
                entryPointsCount, terminalSinksCount, orderedTitles, cycleDesc, category, isSelection, summary);
        }

        /// <summary>
        /// Audits topological DAG order and sequence depth across pure model nodes and links.
        /// </summary>
        public static TopologicalOrderAudit AuditTopologicalOrderAndDepth(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditTopologicalOrderAndDepth();
        }

        /// <summary>
        /// Audits mindmap graph for bridge links, articulation points (cut vertices), and structural vulnerability.
        /// </summary>
        public GraphVulnerabilityAudit AuditGraphBridgesAndArticulationPoints()
        {
            var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
            bool isSelection = selectedNodes.Count > 1;
            var targetNodes = isSelection ? selectedNodes : Nodes.ToList();

            if (targetNodes.Count == 0)
            {
                string emptySummary = "Graph vulnerability audit: 0 nodes · Map is empty";
                StatusMessage = emptySummary;
                return new GraphVulnerabilityAudit(
                    0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
                    0.0, "Empty Graph", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.Ordinal);
            var titleMap = targetNodes.ToDictionary(n => n.Id, n => !string.IsNullOrWhiteSpace(n.Title) ? n.Title : n.Id, StringComparer.Ordinal);
            var relevantLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            if (targetNodes.Count == 1)
            {
                string singleSummary = $"Graph vulnerability audit (1 node · Isolated Single Node): 0 bridges · 0 articulation points · '{titleMap[targetNodes[0].Id]}'";
                StatusMessage = singleSummary;
                return new GraphVulnerabilityAudit(
                    1, relevantLinks.Count, 1, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
                    0.0, "Single Node", isSelection, singleSummary);
            }

            // Build undirected adjacency graph
            var adj = new Dictionary<string, List<(string Neighbor, string LinkId)>>(StringComparer.Ordinal);
            foreach (var n in targetNodes) adj[n.Id] = new List<(string, string)>();

            foreach (var l in relevantLinks)
            {
                if (l.SourceNodeId == l.TargetNodeId) continue; // ignore self-loops
                adj[l.SourceNodeId].Add((l.TargetNodeId, l.Id));
                adj[l.TargetNodeId].Add((l.SourceNodeId, l.Id));
            }

            var disc = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new Dictionary<string, int>(StringComparer.Ordinal);
            var bridges = new List<string>();
            var articulationPoints = new HashSet<string>(StringComparer.Ordinal);
            int time = 0;
            int componentCount = 0;

            void Dfs(string u, string? parentLinkId)
            {
                disc[u] = low[u] = ++time;
                int children = 0;

                foreach (var (v, linkId) in adj[u])
                {
                    if (linkId == parentLinkId) continue;

                    if (disc.ContainsKey(v))
                    {
                        low[u] = Math.Min(low[u], disc[v]);
                    }
                    else
                    {
                        children++;
                        Dfs(v, linkId);
                        low[u] = Math.Min(low[u], low[v]);

                        if (low[v] > disc[u])
                        {
                            string uTitle = titleMap[u];
                            string vTitle = titleMap[v];
                            bridges.Add($"{uTitle} <-> {vTitle}");
                        }

                        if (parentLinkId != null && low[v] >= disc[u])
                        {
                            articulationPoints.Add(u);
                        }
                    }
                }

                if (parentLinkId == null && children > 1)
                {
                    articulationPoints.Add(u);
                }
            }

            foreach (var n in targetNodes)
            {
                if (!disc.ContainsKey(n.Id))
                {
                    componentCount++;
                    Dfs(n.Id, null);
                }
            }

            var artPointTitles = articulationPoints.Select(id => titleMap[id]).OrderBy(t => t).ToList();
            double fragilityIndex = relevantLinks.Count > 0
                ? Math.Round((bridges.Count / (double)relevantLinks.Count) * 100.0, 1)
                : 0.0;

            string category;
            if (componentCount > 1)
            {
                category = $"Disconnected Forest ({componentCount} components)";
            }
            else if (bridges.Count == 0 && artPointTitles.Count == 0)
            {
                category = "Robust Resilient Mesh (0 Single Points of Failure)";
            }
            else if (bridges.Count == relevantLinks.Count)
            {
                category = "Pure Tree Hierarchy (Max Fragility)";
            }
            else
            {
                category = $"Partially Redundant Mesh ({bridges.Count} Bridges)";
            }

            string bridgeDetail = bridges.Count > 0
                ? $" · {bridges.Count} bridge links [{string.Join(", ", bridges.Take(2))}]"
                : " · 0 bridge links";
            string artDetail = artPointTitles.Count > 0
                ? $" · {artPointTitles.Count} articulation points [{string.Join(", ", artPointTitles.Take(2))}]"
                : " · 0 articulation points";

            string scopeStr = isSelection ? "Selected Nodes · " : "";
            string summary = $"Graph vulnerability audit ({scopeStr}{targetNodes.Count} nodes, {relevantLinks.Count} links · {category}): Fragility: {fragilityIndex:0.#}%{bridgeDetail}{artDetail}";
            StatusMessage = summary;

            return new GraphVulnerabilityAudit(
                targetNodes.Count, relevantLinks.Count, componentCount, bridges.Count,
                artPointTitles.Count, bridges, artPointTitles, fragilityIndex, category, isSelection, summary);
        }

        /// <summary>
        /// Audits graph vulnerability across pure model nodes and links.
        /// </summary>
        public static GraphVulnerabilityAudit AuditGraphBridgesAndArticulationPoints(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditGraphBridgesAndArticulationPoints();
        }

        /// <summary>
        /// Audits the mindmap graph's actual network links using Kruskal's algorithm to identify the
        /// core minimal spanning backbone edges versus non-essential redundant chords in cyclic mindmaps.
        /// </summary>
        public NetworkSpanningBackboneAudit AuditNetworkSpanningBackboneAndRedundancy(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            var nodes = targetNodes?.ToList();
            if (nodes == null)
            {
                var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
                if (selectedNodes.Count > 1)
                {
                    nodes = selectedNodes;
                    isSelection = true;
                }
                else
                {
                    nodes = Nodes.ToList();
                }
            }

            string scopeStr = isSelection ? "Selected Nodes · " : "";

            if (nodes.Count == 0)
            {
                string emptySummary = $"Network spanning backbone ({scopeStr}0 nodes): Empty mindmap";
                StatusMessage = emptySummary;
                return new NetworkSpanningBackboneAudit(
                    0, 0, 0, 0, 0, 0.0, 0.0, 0.0,
                    Array.Empty<NetworkLinkRoleInfo>(),
                    "Empty MindMap", isSelection, emptySummary);
            }

            var nodeMap = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var relevantLinks = Links
                .Where(l => nodeMap.ContainsKey(l.SourceNodeId) && nodeMap.ContainsKey(l.TargetNodeId))
                .ToList();

            // Disjoint Set Union (Union-Find)
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in nodes)
            {
                parent[n.Id] = n.Id;
                rank[n.Id] = 0;
            }

            string Find(string i)
            {
                if (parent[i] == i) return i;
                return parent[i] = Find(parent[i]);
            }

            void Union(string rootX, string rootY)
            {
                if (rank[rootX] < rank[rootY]) parent[rootX] = rootY;
                else if (rank[rootX] > rank[rootY]) parent[rootY] = rootX;
                else { parent[rootY] = rootX; rank[rootX]++; }
            }

            // Calculate link lengths based on node coordinates
            var evaluatedLinks = relevantLinks.Select(l =>
            {
                var s = nodeMap[l.SourceNodeId];
                var t = nodeMap[l.TargetNodeId];
                double dx = t.X - s.X;
                double dy = t.Y - s.Y;
                double dist = Math.Round(Math.Sqrt(dx * dx + dy * dy), 1);
                string sTitle = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title;
                string tTitle = string.IsNullOrWhiteSpace(t.Title) ? t.Id : t.Title;
                return new { Link = l, SourceTitle = sTitle, TargetTitle = tTitle, Distance = dist };
            }).OrderBy(x => x.Distance).ToList();

            var linkRoles = new List<NetworkLinkRoleInfo>();
            int backboneCount = 0;
            int chordCount = 0;
            double totalLength = 0.0;
            double backboneLength = 0.0;

            foreach (var item in evaluatedLinks)
            {
                totalLength += item.Distance;
                string rootSrc = Find(item.Link.SourceNodeId);
                string rootTgt = Find(item.Link.TargetNodeId);

                bool isBackbone = false;
                bool isChord = false;

                if (rootSrc != rootTgt)
                {
                    Union(rootSrc, rootTgt);
                    isBackbone = true;
                    backboneCount++;
                    backboneLength += item.Distance;
                }
                else
                {
                    isChord = true;
                    chordCount++;
                }

                linkRoles.Add(new NetworkLinkRoleInfo(
                    item.Link.Id, item.SourceTitle, item.TargetTitle,
                    item.Distance, isBackbone, isChord));
            }

            totalLength = Math.Round(totalLength, 1);
            backboneLength = Math.Round(backboneLength, 1);

            int componentCount = nodes.Select(n => Find(n.Id)).Distinct(StringComparer.Ordinal).Count();
            double redundancyPct = evaluatedLinks.Count > 0
                ? Math.Round(((double)chordCount / evaluatedLinks.Count) * 100.0, 1)
                : 0.0;

            string category;
            if (nodes.Count <= 1)
            {
                category = "Degenerate / Single Node";
            }
            else if (componentCount > 1)
            {
                category = $"Disconnected Forest ({componentCount} components)";
            }
            else if (chordCount == 0)
            {
                category = "Optimal Tree (0% Redundancy)";
            }
            else if (redundancyPct <= 25.0)
            {
                category = "Sparsely Redundant Mesh (Low Chordality)";
            }
            else if (redundancyPct <= 50.0)
            {
                category = "Balanced Network Mesh (Moderate Redundancy)";
            }
            else
            {
                category = "Dense Multi-Ring Mesh (High Redundancy)";
            }

            string summary = FormatNetworkSpanningBackboneSummary(
                nodes.Count, evaluatedLinks.Count, componentCount, backboneCount, chordCount,
                totalLength, backboneLength, redundancyPct, category, isSelection);

            StatusMessage = summary;

            return new NetworkSpanningBackboneAudit(
                nodes.Count, evaluatedLinks.Count, componentCount, backboneCount, chordCount,
                totalLength, backboneLength, redundancyPct, linkRoles, category, isSelection, summary);
        }

        /// <summary>
        /// Audits network spanning backbone and redundant chords across pure model nodes and links.
        /// </summary>
        public static NetworkSpanningBackboneAudit AuditNetworkSpanningBackboneAndRedundancy(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditNetworkSpanningBackboneAndRedundancy();
        }

        /// <summary>
        /// Formats a human-readable summary of network spanning backbone and chord metrics.
        /// </summary>
        public static string FormatNetworkSpanningBackboneSummary(
            int nodeCount, int linkCount, int componentCount, int backboneCount, int chordCount,
            double totalLength, double backboneLength, double redundancyPct, string category, bool isSelection)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Network spanning backbone ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string compStr = componentCount > 1 ? $" · {componentCount} components" : "";
            string chordStr = chordCount > 0 ? $" · {chordCount} redundant chords ({redundancyPct:0.#}%)" : " · 0 redundant chords";
            string wireStr = linkCount > 0 ? $" · Wire: {backboneLength:N0}px backbone / {totalLength:N0}px total" : "";

            return $"Network backbone audit ({scopeStr}{nodeLabel}, {linkLabel}{compStr} · {category}): {backboneCount} backbone edges{chordStr}{wireStr}";
        }

        /// <summary>
        /// Audits mindmap graph modularity (Newman-Girvan Q) and detects community clusters using label propagation,
        /// identifying densely interconnected concept groupings and inter-cluster bridging nodes.
        /// </summary>
        public GraphModularityAudit AuditGraphModularityAndCommunities(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            var targetList = targetNodes?.ToList();
            if (targetList == null)
            {
                var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
                if (selectedNodes.Count > 1)
                {
                    targetList = selectedNodes;
                    isSelection = true;
                }
                else
                {
                    targetList = Nodes.ToList();
                }
            }
            string scopeStr = isSelection ? "Selected Nodes · " : "";

            if (targetList.Count == 0)
            {
                string emptySummary = $"Graph modularity ({scopeStr}0 nodes): Empty mindmap";
                StatusMessage = emptySummary;
                return new GraphModularityAudit(
                    0, 0, 0, 0.0, 0,
                    Array.Empty<MindMapCommunityInfo>(),
                    Array.Empty<string>(),
                    "Empty Mindmap", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetList.Select(n => n.Id));

            // Filter links where both endpoints are in targetList
            var validLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();
            int m = validLinks.Count;

            // Build adjacency list
            var adj = new Dictionary<string, List<string>>();
            foreach (var n in targetList) adj[n.Id] = new List<string>();
            foreach (var l in validLinks)
            {
                if (l.SourceNodeId != l.TargetNodeId)
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            // Community detection: Label propagation
            var community = new Dictionary<string, string>();
            foreach (var n in targetList) community[n.Id] = n.Id;

            if (m > 0)
            {
                bool changed = true;
                int maxIterations = 20;
                int iter = 0;
                var orderedNodeIds = targetList.Select(n => n.Id).ToList();

                while (changed && iter < maxIterations)
                {
                    changed = false;
                    iter++;

                    foreach (var u in orderedNodeIds)
                    {
                        var neighbors = adj[u];
                        if (neighbors.Count == 0) continue;

                        var freq = new Dictionary<string, int>();
                        foreach (var v in neighbors)
                        {
                            string c = community[v];
                            freq[c] = freq.TryGetValue(c, out int cnt) ? cnt + 1 : 1;
                        }

                        int maxCount = freq.Values.Max();
                        string bestLabel = freq.Where(kv => kv.Value == maxCount)
                                               .Select(kv => kv.Key)
                                               .OrderBy(s => s, StringComparer.Ordinal)
                                               .First();

                        if (community[u] != bestLabel)
                        {
                            community[u] = bestLabel;
                            changed = true;
                        }
                    }
                }
            }

            var groups = targetList.GroupBy(n => community[n.Id]).ToList();
            var communityInfos = new List<MindMapCommunityInfo>();
            int communityIdCounter = 1;

            double modularityQ = 0.0;
            double twoM = 2.0 * Math.Max(1, m);

            var bridgingNodes = new HashSet<string>();

            foreach (var grp in groups.OrderByDescending(g => g.Count()))
            {
                var cNodes = grp.ToList();
                var cNodeIds = new HashSet<string>(cNodes.Select(n => n.Id));
                string firstTitle = cNodes[0].Title;
                string cLabel = cNodes.Count == 1 ? $"Cluster '{firstTitle}'" : $"Cluster '{firstTitle}' (+{cNodes.Count - 1})";

                int internalEdges = 0;
                int externalEdges = 0;
                int degreeSum = 0;

                foreach (var n in cNodes)
                {
                    var neighbors = adj[n.Id];
                    degreeSum += neighbors.Count;
                    int nExt = 0;

                    foreach (var v in neighbors)
                    {
                        if (cNodeIds.Contains(v))
                        {
                            internalEdges++;
                        }
                        else
                        {
                            externalEdges++;
                            nExt++;
                        }
                    }

                    if (nExt > 0)
                    {
                        bridgingNodes.Add(n.Title);
                    }
                }

                internalEdges /= 2;

                if (m > 0)
                {
                    double e_c_frac = (double)internalEdges / m;
                    double d_c_frac = (double)degreeSum / twoM;
                    modularityQ += e_c_frac - (d_c_frac * d_c_frac);
                }

                double density = cNodes.Count > 1
                    ? (2.0 * internalEdges) / (cNodes.Count * (cNodes.Count - 1))
                    : 1.0;

                communityInfos.Add(new MindMapCommunityInfo(
                    communityIdCounter++,
                    cLabel,
                    cNodes.Count,
                    cNodes.Select(n => n.Title).ToList(),
                    internalEdges,
                    externalEdges,
                    Math.Round(density, 2)));
            }

            modularityQ = Math.Round(Math.Clamp(modularityQ, -0.5, 1.0), 3);

            string category;
            if (m == 0)
            {
                category = "Isolated Nodes / Disconnected";
            }
            else if (modularityQ >= 0.4)
            {
                category = "High Modularity (Strong Thematic Clusters)";
            }
            else if (modularityQ >= 0.2)
            {
                category = "Moderate Modularity (Balanced Clusters)";
            }
            else if (modularityQ > 0.0)
            {
                category = "Low Modularity (Tightly Interconnected Core)";
            }
            else
            {
                category = "Monolithic Network / Uniform Dispersion";
            }

            string summary = FormatCommunitySummary(
                targetList.Count, m, communityInfos.Count, modularityQ, bridgingNodes.Count,
                category, isSelection);

            StatusMessage = summary;

            return new GraphModularityAudit(
                targetList.Count,
                m,
                communityInfos.Count,
                modularityQ,
                bridgingNodes.Count,
                communityInfos,
                bridgingNodes.OrderBy(s => s).ToList(),
                category,
                isSelection,
                summary);
        }

        /// <summary>
        /// Audits mindmap graph modularity across pure model nodes and links.
        /// </summary>
        public static GraphModularityAudit AuditGraphModularityAndCommunities(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditGraphModularityAndCommunities();
        }

        /// <summary>
        /// Formats a human-readable summary of graph modularity and community detection.
        /// </summary>
        public static string FormatCommunitySummary(
            int nodeCount, int linkCount, int clusterCount, double modularityQ, int bridgingNodeCount,
            string category, bool isSelection)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Graph modularity ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string clusterLabel = clusterCount == 1 ? "1 community" : $"{clusterCount} communities";
            string bridgeStr = bridgingNodeCount > 0 ? $" · {bridgingNodeCount} bridging nodes" : "";

            return $"Graph modularity audit ({scopeStr}{nodeLabel}, {linkLabel} · {clusterLabel} · Q={modularityQ:0.###} · {category}): {clusterCount} clusters{bridgeStr}";
        }

        public static string FormatGraphModularitySummary(
            int nodeCount, int linkCount, int clusterCount, double modularityQ, int bridgingNodeCount,
            string category, bool isSelection)
            => FormatCommunitySummary(nodeCount, linkCount, clusterCount, modularityQ, bridgingNodeCount, category, isSelection);

        /// <summary>
        /// Audits the mindmap's geodesic graph diameter, radial eccentricity, peripheral nodes, central nodes,
        /// and identifies the longest critical semantic concept path.
        /// </summary>
        public GraphDiameterAudit AuditGraphDiameterAndEccentricity(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            var targetList = targetNodes?.ToList();
            if (targetList == null)
            {
                var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
                if (selectedNodes.Count > 1)
                {
                    targetList = selectedNodes;
                    isSelection = true;
                }
                else
                {
                    targetList = Nodes.ToList();
                }
            }

            if (targetList.Count == 0)
            {
                string emptySummary = "Graph diameter audit: 0 nodes (Empty mindmap)";
                StatusMessage = emptySummary;
                return new GraphDiameterAudit(0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<NodeEccentricityInfo>(), "Empty Mindmap", isSelection, emptySummary);
            }

            if (targetList.Count == 1)
            {
                var single = targetList[0];
                string singleSummary = $"Graph diameter audit: 1 node ('{single.Title}') · Diameter 0, Radius 0";
                StatusMessage = singleSummary;
                var singleInfo = new NodeEccentricityInfo(single.Id, single.Title, 0, true, true);
                return new GraphDiameterAudit(1, 0, 1, 0, 0, 1, 1, new[] { single.Title }, new[] { singleInfo }, "Single Node Hub", isSelection, singleSummary);
            }

            var targetIds = new HashSet<string>(targetList.Select(n => n.Id));
            var linksInScope = Links.Where(l => targetIds.Contains(l.SourceNodeId) && targetIds.Contains(l.TargetNodeId)).ToList();
            var nodesById = targetList.ToDictionary(n => n.Id);

            var adj = new Dictionary<string, List<string>>();
            foreach (var n in targetList) adj[n.Id] = new List<string>();
            foreach (var l in linksInScope)
            {
                if (l.SourceNodeId != l.TargetNodeId)
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            // Identify connected components
            var visitedComponents = new HashSet<string>();
            int componentCount = 0;
            foreach (var n in targetList)
            {
                if (!visitedComponents.Contains(n.Id))
                {
                    componentCount++;
                    var qComp = new Queue<string>();
                    visitedComponents.Add(n.Id);
                    qComp.Enqueue(n.Id);
                    while (qComp.Count > 0)
                    {
                        var curr = qComp.Dequeue();
                        foreach (var nbr in adj[curr])
                        {
                            if (visitedComponents.Add(nbr))
                                qComp.Enqueue(nbr);
                        }
                    }
                }
            }

            var eccentricities = new Dictionary<string, int>();
            int maxDistOverall = 0;
            string bestStartNode = targetList[0].Id;
            string bestEndNode = targetList[0].Id;
            Dictionary<string, string?> bestPrevMap = new();

            foreach (var sourceNode in targetList)
            {
                var dist = new Dictionary<string, int>();
                var prev = new Dictionary<string, string?>();
                var queue = new Queue<string>();

                dist[sourceNode.Id] = 0;
                prev[sourceNode.Id] = null;
                queue.Enqueue(sourceNode.Id);

                while (queue.Count > 0)
                {
                    var u = queue.Dequeue();
                    int d = dist[u];
                    foreach (var v in adj[u])
                    {
                        if (!dist.ContainsKey(v))
                        {
                            dist[v] = d + 1;
                            prev[v] = u;
                            queue.Enqueue(v);
                        }
                    }
                }

                int maxD = dist.Values.Max();
                eccentricities[sourceNode.Id] = maxD;

                if (maxD > maxDistOverall)
                {
                    maxDistOverall = maxD;
                    bestStartNode = sourceNode.Id;
                    bestEndNode = dist.OrderByDescending(kv => kv.Value).First().Key;
                    bestPrevMap = prev;
                }
            }

            int diameter = maxDistOverall;
            int radius = eccentricities.Values.Min();

            var nodeInfos = new List<NodeEccentricityInfo>();
            int peripheralCount = 0;
            int centralCount = 0;

            foreach (var n in targetList)
            {
                int ecc = eccentricities[n.Id];
                bool isPeripheral = (ecc == diameter && diameter > 0);
                bool isCentral = (ecc == radius);
                if (isPeripheral) peripheralCount++;
                if (isCentral) centralCount++;
                nodeInfos.Add(new NodeEccentricityInfo(n.Id, n.Title, ecc, isPeripheral, isCentral));
            }

            // Reconstruct longest concept path
            var longestPath = new List<string>();
            if (diameter > 0 && bestPrevMap.Count > 0)
            {
                string? curr = bestEndNode;
                while (curr != null)
                {
                    longestPath.Add(nodesById[curr].Title);
                    curr = bestPrevMap.TryGetValue(curr, out var p) ? p : null;
                }
                longestPath.Reverse();
            }
            else
            {
                longestPath.Add(targetList[0].Title);
            }

            string category;
            if (diameter == 0) category = "Trivial / Disconnected Nodes";
            else if (diameter == 1) category = "Direct Star Topology (Radius 1)";
            else if (diameter == 2) category = "Compact Hub-and-Spoke (Diameter 2)";
            else if (diameter <= 4) category = "Balanced Semantic Network (Diameter 3–4)";
            else category = "Extended Concept Chain (Deep Hierarchy)";

            string summary = FormatDiameterEccentricitySummary(
                targetList.Count, linksInScope.Count, componentCount, diameter, radius,
                peripheralCount, centralCount, longestPath, category, isSelection);

            StatusMessage = summary;

            return new GraphDiameterAudit(
                targetList.Count,
                linksInScope.Count,
                componentCount,
                diameter,
                radius,
                peripheralCount,
                centralCount,
                longestPath,
                nodeInfos,
                category,
                isSelection,
                summary);
        }

        /// <summary>
        /// Audits mindmap graph diameter across pure model nodes and links.
        /// </summary>
        public static GraphDiameterAudit AuditGraphDiameterAndEccentricity(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditGraphDiameterAndEccentricity();
        }

        /// <summary>
        /// Formats a human-readable summary of graph diameter, eccentricity, and longest concept path.
        /// </summary>
        public static string FormatDiameterEccentricitySummary(
            int nodeCount, int linkCount, int components, int diameter, int radius,
            int peripheralCount, int centralCount, IReadOnlyList<string> longestPath,
            string category, bool isSelection)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Graph diameter ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string compLabel = components == 1 ? "1 component" : $"{components} components";
            string pathStr = longestPath.Count > 0 ? string.Join(" → ", longestPath.Select(p => $"'{p}'")) : "None";

            return $"Graph diameter audit ({scopeStr}{nodeLabel}, {linkLabel} · {compLabel} · Diameter {diameter}, Radius {radius} · {category}): Longest concept path: {pathStr} ({diameter} hops) · {peripheralCount} peripheral, {centralCount} central";
        }

        /// <summary>
        /// Audits mindmap graph closeness centrality, harmonic mean distances for disconnected subgraphs,
        /// identifies optimal knowledge dissemination hubs, and scores information propagation efficiency.
        /// </summary>
        public ClosenessCentralityAudit AuditClosenessCentralityAndHarmonicDistance(IEnumerable<MindMapNodeViewModel>? targetNodes = null)
        {
            bool isSelection = false;
            var targetList = targetNodes?.ToList();
            if (targetList == null)
            {
                var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
                if (selectedNodes.Count > 1)
                {
                    targetList = selectedNodes;
                    isSelection = true;
                }
                else
                {
                    targetList = Nodes.ToList();
                }
            }
            else
            {
                isSelection = true;
            }

            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (targetList.Count == 0)
            {
                string emptySummary = $"Graph closeness centrality ({scopeStr}0 nodes): Empty mindmap";
                return new ClosenessCentralityAudit(0, 0, 0, 0.0, 0.0, Array.Empty<NodeClosenessInfo>(), Array.Empty<string>(), Array.Empty<string>(), "Empty MindMap", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetList.Select(n => n.Id));
            var linksInScope = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            // Adjacency list
            var adj = new Dictionary<string, List<string>>();
            foreach (var n in targetList) adj[n.Id] = new List<string>();
            foreach (var l in linksInScope)
            {
                if (adj.ContainsKey(l.SourceNodeId) && adj.ContainsKey(l.TargetNodeId))
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            int nTotal = targetList.Count;

            // Single node edge case
            if (nTotal == 1)
            {
                var single = targetList[0];
                var singleInfo = new NodeClosenessInfo(single.Id, single.Title, 1.0, 1.0, 0.0, 0, true);
                string singleSummary = $"Graph closeness centrality ({scopeStr}1 node, 0 links · 1 component · Optimal Knowledge Hub · 100% efficiency): Single isolated node '{single.Title}'";
                return new ClosenessCentralityAudit(1, 0, 1, 1.0, 100.0, new[] { singleInfo }, new[] { single.Title }, Array.Empty<string>(), "Single Isolated Node", isSelection, singleSummary);
            }

            // BFS from each node to calculate geodesic distances
            var nodeInfos = new List<NodeClosenessInfo>();
            var visitedComponents = new HashSet<string>();
            int componentCount = 0;

            // Count connected components
            foreach (var node in targetList)
            {
                if (!visitedComponents.Contains(node.Id))
                {
                    componentCount++;
                    var qComp = new Queue<string>();
                    qComp.Enqueue(node.Id);
                    visitedComponents.Add(node.Id);
                    while (qComp.Count > 0)
                    {
                        var curr = qComp.Dequeue();
                        foreach (var neighbor in adj[curr])
                        {
                            if (visitedComponents.Add(neighbor))
                            {
                                qComp.Enqueue(neighbor);
                            }
                        }
                    }
                }
            }

            double totalHarmonic = 0.0;
            double maxHarmonic = -1.0;
            double minHarmonic = double.MaxValue;

            foreach (var node in targetList)
            {
                var dist = new Dictionary<string, int>();
                var queue = new Queue<string>();
                dist[node.Id] = 0;
                queue.Enqueue(node.Id);

                while (queue.Count > 0)
                {
                    var u = queue.Dequeue();
                    int d = dist[u];
                    foreach (var v in adj[u])
                    {
                        if (!dist.ContainsKey(v))
                        {
                            dist[v] = d + 1;
                            queue.Enqueue(v);
                        }
                    }
                }

                int reachableCount = dist.Count - 1; // excluding self
                double sumDist = 0.0;
                double sumReciprocalDist = 0.0;

                foreach (var kvp in dist)
                {
                    if (kvp.Key == node.Id) continue;
                    sumDist += kvp.Value;
                    sumReciprocalDist += 1.0 / kvp.Value;
                }

                // Harmonic centrality normalized by (N - 1)
                double harmonic = Math.Round(sumReciprocalDist / (nTotal - 1), 3);
                // Closeness centrality: Wasserman-Faust formula for disconnected graphs
                double closeness = 0.0;
                if (sumDist > 0 && reachableCount > 0)
                {
                    double rawCloseness = (double)reachableCount / sumDist;
                    closeness = Math.Round(((double)reachableCount / (nTotal - 1)) * rawCloseness, 3);
                }

                double meanDist = reachableCount > 0 ? Math.Round(sumDist / reachableCount, 1) : 0.0;

                totalHarmonic += harmonic;
                if (harmonic > maxHarmonic) maxHarmonic = harmonic;
                if (harmonic < minHarmonic) minHarmonic = harmonic;

                nodeInfos.Add(new NodeClosenessInfo(node.Id, node.Title, closeness, harmonic, meanDist, reachableCount, false));
            }

            double meanHarmonic = Math.Round(totalHarmonic / nTotal, 3);
            double graphEfficiency = Math.Round(meanHarmonic * 100.0, 1);

            // Flag hubs (harmonic >= maxHarmonic - 0.05 and harmonic > 0.1)
            var topHubs = new List<string>();
            var peripheralNodes = new List<string>();

            var updatedInfos = new List<NodeClosenessInfo>();
            foreach (var info in nodeInfos)
            {
                bool isHub = maxHarmonic > 0 && info.HarmonicCentrality >= maxHarmonic - 0.001;
                if (isHub) topHubs.Add(info.Title);
                if (info.HarmonicCentrality <= minHarmonic + 0.001 || info.ReachableNodes == 0)
                {
                    peripheralNodes.Add(info.Title);
                }
                updatedInfos.Add(info with { IsOptimalDisseminationHub = isHub });
            }

            string category;
            if (meanHarmonic >= 0.70)
            {
                category = "High Information Flow Efficiency";
            }
            else if (meanHarmonic >= 0.40)
            {
                category = "Balanced Propagation Dynamics";
            }
            else if (componentCount > 1)
            {
                category = "Fragmented Graph Components";
            }
            else
            {
                category = "Sparse / Linear Propagation Bottlenecks";
            }

            string summary = FormatClosenessCentralitySummary(
                targetList.Count, linksInScope.Count, componentCount, meanHarmonic, graphEfficiency,
                topHubs, peripheralNodes.Count, category, isSelection);

            return new ClosenessCentralityAudit(
                targetList.Count,
                linksInScope.Count,
                componentCount,
                meanHarmonic,
                graphEfficiency,
                updatedInfos,
                topHubs,
                peripheralNodes,
                category,
                isSelection,
                summary);
        }

        /// <summary>
        /// Audits mindmap graph closeness centrality across pure model nodes and links.
        /// </summary>
        public static ClosenessCentralityAudit AuditClosenessCentralityAndHarmonicDistance(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditClosenessCentralityAndHarmonicDistance();
        }

        /// <summary>
        /// Formats a human-readable summary of graph closeness centrality and harmonic distance.
        /// </summary>
        public static string FormatClosenessCentralitySummary(
            int nodeCount, int linkCount, int components, double meanHarmonic, double graphEfficiency,
            IReadOnlyList<string> topHubs, int peripheralCount, string category, bool isSelection)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Graph closeness centrality ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string compLabel = components == 1 ? "1 component" : $"{components} components";
            string hubStr = topHubs.Count > 0 ? string.Join(", ", topHubs.Select(h => $"'{h}'")) : "None";

            return $"Graph closeness centrality ({scopeStr}{nodeLabel}, {linkLabel} · {compLabel} · {category} · {graphEfficiency:0.#}% flow efficiency): Optimal hubs: {hubStr} (Mean harmonic {meanHarmonic:0.###}) · {peripheralCount} peripheral";
        }

        /// <summary>
        /// Audits mindmap graph betweenness centrality, shortest-path bottleneck criticalities,
        /// and bridge concepts using Brandes' algorithm.
        /// </summary>
        public BetweennessCentralityAudit AuditBetweennessCentralityAndBridges(bool selectionOnly = false)
        {
            var targetList = selectionOnly
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && targetList.Count > 0 && targetList.Count < Nodes.Count;
            if (targetList.Count == 0)
            {
                string emptySummary = FormatBetweennessCentralitySummary(0, 0, 0, 0.0, 0.0, Array.Empty<string>(), 0, "Empty Graph", isSelection);
                return new BetweennessCentralityAudit(
                    0, 0, 0, 0.0, 0.0, 0, Array.Empty<NodeBetweennessInfo>(), Array.Empty<string>(), "Empty Graph", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetList.Select(n => n.Id), StringComparer.Ordinal);
            var linksInScope = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            // Build adjacency list (undirected for information propagation)
            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in targetList) adj[n.Id] = new List<string>();
            foreach (var l in linksInScope)
            {
                if (adj.ContainsKey(l.SourceNodeId) && adj.ContainsKey(l.TargetNodeId))
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            // Count connected components
            int componentCount = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in targetList)
            {
                if (!visited.Contains(n.Id))
                {
                    componentCount++;
                    var q = new Queue<string>();
                    q.Enqueue(n.Id);
                    visited.Add(n.Id);
                    while (q.Count > 0)
                    {
                        var curr = q.Dequeue();
                        foreach (var nbr in adj[curr])
                        {
                            if (visited.Add(nbr)) q.Enqueue(nbr);
                        }
                    }
                }
            }

            // Brandes' algorithm for unweighted betweenness centrality
            var cb = new Dictionary<string, double>(StringComparer.Ordinal);
            var pathsThrough = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in targetList)
            {
                cb[n.Id] = 0.0;
                pathsThrough[n.Id] = 0;
            }

            foreach (var s in targetList)
            {
                var S = new Stack<string>();
                var P = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var sigma = new Dictionary<string, double>(StringComparer.Ordinal);
                var d = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var n in targetList)
                {
                    P[n.Id] = new List<string>();
                    sigma[n.Id] = 0.0;
                    d[n.Id] = -1;
                }

                sigma[s.Id] = 1.0;
                d[s.Id] = 0;

                var Q = new Queue<string>();
                Q.Enqueue(s.Id);

                while (Q.Count > 0)
                {
                    var v = Q.Dequeue();
                    S.Push(v);

                    foreach (var w in adj[v])
                    {
                        // Path discovery
                        if (d[w] < 0)
                        {
                            d[w] = d[v] + 1;
                            Q.Enqueue(w);
                        }
                        // Path counting
                        if (d[w] == d[v] + 1)
                        {
                            sigma[w] += sigma[v];
                            P[w].Add(v);
                        }
                    }
                }

                // Accumulation
                var delta = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var n in targetList) delta[n.Id] = 0.0;

                while (S.Count > 0)
                {
                    var w = S.Pop();
                    foreach (var v in P[w])
                    {
                        if (sigma[w] > 0)
                        {
                            delta[v] += (sigma[v] / sigma[w]) * (1.0 + delta[w]);
                        }
                    }
                    if (w != s.Id)
                    {
                        cb[w] += delta[w];
                        if (delta[w] > 0.0001) pathsThrough[w]++;
                    }
                }
            }

            // Halve betweenness for undirected graph
            int totalNodes = targetList.Count;
            double normalizer = totalNodes > 2 ? 2.0 / ((totalNodes - 1) * (totalNodes - 2)) : 1.0;

            var nodeInfos = new List<NodeBetweennessInfo>();
            foreach (var node in targetList)
            {
                double raw = cb[node.Id] / 2.0;
                double normalized = totalNodes > 2 ? Math.Round(raw * normalizer, 4) : 0.0;
                bool isBottleneck = normalized >= 0.15 || (raw >= 1.0 && raw >= (cb.Values.Max() / 2.0) * 0.7);
                nodeInfos.Add(new NodeBetweennessInfo(node.Id, node.Title, raw, normalized, isBottleneck, pathsThrough[node.Id]));
            }

            var bottlenecks = nodeInfos.Where(i => i.IsCriticalBottleneck).OrderByDescending(i => i.NormalizedBetweenness).ToList();
            double maxBetweenness = nodeInfos.Count > 0 ? Math.Round(nodeInfos.Max(i => i.NormalizedBetweenness), 4) : 0.0;
            double meanBetweenness = nodeInfos.Count > 0 ? Math.Round(nodeInfos.Average(i => i.NormalizedBetweenness), 4) : 0.0;

            string category;
            if (bottlenecks.Count == 0)
            {
                category = "Decentralized Mesh (No Critical Bottlenecks)";
            }
            else if (bottlenecks.Count <= 2 && maxBetweenness >= 0.35)
            {
                category = "High Vulnerability (Single-Point-of-Failure Hubs)";
            }
            else if (bottlenecks.Count <= 4)
            {
                category = "Moderate Chokepoint Hierarchy";
            }
            else
            {
                category = "Distributed Bridge Nexus";
            }

            var topTitles = bottlenecks.Take(3).Select(b => b.Title).ToList();
            string summary = FormatBetweennessCentralitySummary(
                targetList.Count, linksInScope.Count, componentCount, maxBetweenness, meanBetweenness,
                topTitles, bottlenecks.Count, category, isSelection);

            return new BetweennessCentralityAudit(
                targetList.Count, linksInScope.Count, componentCount, maxBetweenness, meanBetweenness,
                bottlenecks.Count, nodeInfos, topTitles, category, isSelection, summary);
        }

        public static BetweennessCentralityAudit AuditBetweennessCentralityAndBridges(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditBetweennessCentralityAndBridges();
        }

        public static string FormatBetweennessCentralitySummary(
            int nodeCount, int linkCount, int components, double maxBetweenness, double meanBetweenness,
            IReadOnlyList<string> bottleneckTitles, int bottleneckCount, string category, bool isSelection)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Graph betweenness centrality ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string compLabel = components == 1 ? "1 component" : $"{components} components";
            string bDesc = bottleneckTitles.Count > 0 ? string.Join(", ", bottleneckTitles.Select(t => $"'{t}'")) : "None";

            return $"Graph betweenness centrality ({scopeStr}{nodeLabel}, {linkLabel} · {compLabel} · {category}): Peak chokepoint {maxBetweenness:0.###} (Mean {meanBetweenness:0.###}) · {bottleneckCount} critical bottlenecks: {bDesc}";
        }

        public EigenvectorCentralityAudit AuditEigenvectorCentrality(bool selectionOnly = false)
        {
            var targetList = selectionOnly
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && targetList.Count > 0 && targetList.Count < Nodes.Count;
            if (targetList.Count == 0)
            {
                return new EigenvectorCentralityAudit(
                    0, 0, 0, 0.0, 0, true, 0.0, 0.0,
                    Array.Empty<NodeEigenvectorInfo>(),
                    Array.Empty<string>(),
                    "Empty Mindmap", isSelection,
                    $"Graph eigenvector centrality ({(isSelection ? "Selected Nodes · " : "")}0 nodes): Empty mindmap");
            }

            var nodeSet = new HashSet<string>(targetList.Select(n => n.Id));
            var linksInScope = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            int n = targetList.Count;
            var idToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++) idToIndex[targetList[i].Id] = i;

            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();

            foreach (var l in linksInScope)
            {
                if (idToIndex.TryGetValue(l.SourceNodeId, out int u) && idToIndex.TryGetValue(l.TargetNodeId, out int v))
                {
                    if (u != v)
                    {
                        adj[u].Add(v);
                        adj[v].Add(u);
                    }
                }
            }

            // Connected components via BFS
            var visited = new bool[n];
            int componentCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!visited[i])
                {
                    componentCount++;
                    var q = new Queue<int>();
                    q.Enqueue(i);
                    visited[i] = true;
                    while (q.Count > 0)
                    {
                        int cur = q.Dequeue();
                        foreach (int nbr in adj[cur])
                        {
                            if (!visited[nbr])
                            {
                                visited[nbr] = true;
                                q.Enqueue(nbr);
                            }
                        }
                    }
                }
            }

            // Power iteration for principal eigenvector
            double[] x = new double[n];
            double initVal = 1.0 / Math.Sqrt(n);
            for (int i = 0; i < n; i++) x[i] = initVal;
            double[] nextX = new double[n];

            int maxIterations = 100;
            double tolerance = 1e-6;
            bool converged = false;
            int iterationsRun = 0;

            for (int it = 0; it < maxIterations; it++)
            {
                iterationsRun++;
                double normSq = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double sum = 0.0;
                    foreach (int nbr in adj[i])
                    {
                        sum += x[nbr];
                    }
                    // Self-loop regularization (lazy walk A + I) eliminates bipartite periodicity and guarantees rapid Perron-Frobenius convergence
                    nextX[i] = sum + x[i];
                    normSq += nextX[i] * nextX[i];
                }

                double norm = Math.Sqrt(normSq);
                if (norm < 1e-12)
                {
                    break;
                }

                double maxDiff = 0.0;
                for (int i = 0; i < n; i++)
                {
                    nextX[i] /= norm;
                    double diff = Math.Abs(nextX[i] - x[i]);
                    if (diff > maxDiff) maxDiff = diff;
                    x[i] = nextX[i];
                }

                if (maxDiff < tolerance)
                {
                    converged = true;
                    break;
                }
            }

            double lambda = 0.0;
            for (int i = 0; i < n; i++)
            {
                double rowSum = 0.0;
                foreach (int nbr in adj[i]) rowSum += x[nbr];
                lambda += x[i] * rowSum;
            }
            lambda = Math.Round(Math.Max(0.0, lambda), 3);

            double sumX = x.Sum();
            if (sumX <= 0.0) sumX = 1.0;

            double maxInfluence = 0.0;
            double entropy = 0.0;
            var nodeInfos = new List<NodeEigenvectorInfo>();

            for (int i = 0; i < n; i++)
            {
                double influencePct = Math.Round((x[i] / sumX) * 100.0, 1);
                if (influencePct > maxInfluence) maxInfluence = influencePct;

                double p = x[i] / sumX;
                if (p > 1e-9)
                {
                    entropy -= p * Math.Log2(p);
                }

                string tier;
                if (influencePct >= 25.0) tier = "Dominant Hub";
                else if (influencePct >= 10.0) tier = "High Authority";
                else if (influencePct >= 5.0) tier = "Moderate Influence";
                else tier = "Peripheral Node";

                nodeInfos.Add(new NodeEigenvectorInfo(
                    targetList[i].Id,
                    targetList[i].Title,
                    adj[i].Count,
                    Math.Round(x[i], 4),
                    influencePct,
                    tier,
                    false));
            }

            entropy = Math.Round(entropy, 2);

            double threshold = Math.Max(10.0, maxInfluence * 0.7);
            var topTitles = new List<string>();
            for (int i = 0; i < nodeInfos.Count; i++)
            {
                if (nodeInfos[i].NormalizedInfluencePercent >= threshold)
                {
                    nodeInfos[i] = nodeInfos[i] with { IsTopInfluencer = true };
                    topTitles.Add(nodeInfos[i].Title);
                }
            }

            string category;
            if (n <= 2) category = "Trivial Graph";
            else if (maxInfluence >= 50.0) category = "Monocentric Hub (>50% Influence)";
            else if (maxInfluence >= 25.0) category = "Multi-Hub Core Network";
            else if (entropy >= Math.Log2(n) * 0.85) category = "Equitably Distributed Mesh";
            else category = "Hierarchical Influence Cascade";

            string summary = FormatEigenvectorCentralitySummary(
                targetList.Count, linksInScope.Count, componentCount, lambda, maxInfluence,
                topTitles, topTitles.Count, category, isSelection);

            return new EigenvectorCentralityAudit(
                targetList.Count, linksInScope.Count, componentCount, lambda, iterationsRun, converged,
                maxInfluence, entropy, nodeInfos, topTitles, category, isSelection, summary);
        }

        public static EigenvectorCentralityAudit AuditEigenvectorCentrality(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditEigenvectorCentrality();
        }

        public static string FormatEigenvectorCentralitySummary(
            int nodeCount, int linkCount, int components, double lambda, double maxInfluence,
            IReadOnlyList<string> topTitles, int topCount, string category, bool isSelection)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Graph eigenvector centrality ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkLabel = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string compLabel = components == 1 ? "1 component" : $"{components} components";
            string topDesc = topTitles.Count > 0 ? string.Join(", ", topTitles.Select(t => $"'{t}'")) : "None";

            return $"Graph eigenvector centrality ({scopeStr}{nodeLabel}, {linkLabel} · {compLabel} · {category}): Eigenvalue λ={lambda:0.###} · Peak influence {maxInfluence:0.#}% · {topCount} authoritative hubs: {topDesc}";
        }

        public RadialLayoutAudit AuditRadialLayoutAngularSpacing(bool selectionOnly = false)
        {
            var targetList = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            if (targetList.Count == 0)
            {
                targetList = Nodes.ToList();
            }

            bool isSelection = selectionOnly && HasSelection && targetList.Count < Nodes.Count;

            var targetIds = new HashSet<string>(targetList.Select(n => n.Id));
            var linksInScope = Links.Where(l => targetIds.Contains(l.SourceNodeId) && targetIds.Contains(l.TargetNodeId)).ToList();

            if (targetList.Count == 0)
            {
                string emptySummary = "Graph radial layout audit: 0 nodes · Galaxy is empty";
                StatusMessage = emptySummary;
                return new RadialLayoutAudit(
                    0, 0, 0, 0.0, 100.0, 100.0,
                    Array.Empty<RadialRingMetric>(),
                    Array.Empty<NodeRadialMetric>(),
                    Array.Empty<string>(),
                    "Empty Galaxy",
                    isSelection,
                    emptySummary);
            }

            var inDegree = new Dictionary<string, int>();
            var adj = new Dictionary<string, List<string>>();
            foreach (var n in targetList)
            {
                inDegree[n.Id] = 0;
                adj[n.Id] = new List<string>();
            }

            foreach (var l in linksInScope)
            {
                if (inDegree.ContainsKey(l.TargetNodeId)) inDegree[l.TargetNodeId]++;
                if (adj.ContainsKey(l.SourceNodeId)) adj[l.SourceNodeId].Add(l.TargetNodeId);
                if (adj.ContainsKey(l.TargetNodeId)) adj[l.TargetNodeId].Add(l.SourceNodeId);
            }

            var rootCandidates = targetList.Where(n => inDegree[n.Id] == 0).ToList();
            var root = rootCandidates.Count > 0
                ? rootCandidates.OrderByDescending(n => adj[n.Id].Count).First()
                : targetList.OrderByDescending(n => adj[n.Id].Count).First();

            var depths = new Dictionary<string, int>();
            depths[root.Id] = 0;
            var queue = new Queue<string>();
            queue.Enqueue(root.Id);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                int nextDepth = depths[cur] + 1;
                foreach (var neighbor in adj[cur])
                {
                    if (!depths.ContainsKey(neighbor))
                    {
                        depths[neighbor] = nextDepth;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            foreach (var n in targetList)
            {
                if (!depths.ContainsKey(n.Id))
                {
                    depths[n.Id] = 1;
                }
            }

            double rootX = root.X;
            double rootY = root.Y;

            var nodeRadialList = new List<NodeRadialMetric>();
            var ringsMap = new Dictionary<int, List<(MindMapNodeViewModel Node, double Radius, double AngleDeg)>>();

            foreach (var n in targetList)
            {
                int depth = depths[n.Id];
                double dx = n.X - rootX;
                double dy = n.Y - rootY;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = angleRad * (180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360.0;

                if (!ringsMap.TryGetValue(depth, out var ringList))
                {
                    ringList = new List<(MindMapNodeViewModel, double, double)>();
                    ringsMap[depth] = ringList;
                }
                ringList.Add((n, r, angleDeg));
            }

            var ringMetrics = new List<RadialRingMetric>();
            var crowdedTitles = new List<string>();
            double totalRadiusJitter = 0.0;
            double totalUniformity = 0.0;
            int evaluatedRingsCount = 0;

            foreach (var kvp in ringsMap.OrderBy(k => k.Key))
            {
                int depth = kvp.Key;
                var members = kvp.Value;
                int count = members.Count;

                if (depth == 0)
                {
                    nodeRadialList.Add(new NodeRadialMetric(root.Id, root.Title, 0, 0.0, 0.0, 360.0, false));
                    ringMetrics.Add(new RadialRingMetric(0, 1, 0.0, 0.0, 360.0, 100.0));
                    continue;
                }

                double meanR = members.Average(m => m.Radius);
                double rVariance = members.Select(m => (m.Radius - meanR) * (m.Radius - meanR)).Average();
                double rStdDev = Math.Sqrt(rVariance);
                totalRadiusJitter += rStdDev;

                var sorted = members.OrderBy(m => m.AngleDeg).ToList();
                double idealGap = 360.0 / Math.Max(1, count);
                var gaps = new List<double>();

                for (int i = 0; i < sorted.Count; i++)
                {
                    double nextAngle = (i < sorted.Count - 1) ? sorted[i + 1].AngleDeg : (sorted[0].AngleDeg + 360.0);
                    double gap = nextAngle - sorted[i].AngleDeg;
                    gaps.Add(gap);

                    bool isCrowded = gap < (idealGap * 0.4) || gap < 12.0;
                    if (isCrowded && !crowdedTitles.Contains(sorted[i].Node.Title))
                    {
                        crowdedTitles.Add(sorted[i].Node.Title);
                    }

                    nodeRadialList.Add(new NodeRadialMetric(
                        sorted[i].Node.Id,
                        sorted[i].Node.Title,
                        depth,
                        Math.Round(sorted[i].Radius, 1),
                        Math.Round(sorted[i].AngleDeg, 1),
                        Math.Round(gap, 1),
                        isCrowded));
                }

                double gapVariance = gaps.Select(g => (g - idealGap) * (g - idealGap)).Average();
                double gapStdDev = Math.Sqrt(gapVariance);
                double uniformity = Math.Clamp(100.0 - (gapStdDev * 1.5), 0.0, 100.0);
                totalUniformity += uniformity;
                evaluatedRingsCount++;

                ringMetrics.Add(new RadialRingMetric(
                    depth,
                    count,
                    Math.Round(meanR, 1),
                    Math.Round(rStdDev, 1),
                    Math.Round(idealGap, 1),
                    Math.Round(uniformity, 1)));
            }

            double meanJitter = evaluatedRingsCount > 0 ? totalRadiusJitter / evaluatedRingsCount : 0.0;
            double meanUniformity = evaluatedRingsCount > 0 ? totalUniformity / evaluatedRingsCount : 100.0;

            double fidelityScore = Math.Round(Math.Clamp((meanUniformity * 0.6) + Math.Max(0, 40.0 - (meanJitter * 0.2)), 0.0, 100.0), 1);

            string category;
            if (fidelityScore >= 85.0 && crowdedTitles.Count == 0)
            {
                category = "Harmonic Equidistant Radial Layout";
            }
            else if (fidelityScore >= 70.0 && crowdedTitles.Count == 0)
            {
                category = "Balanced Concentric Distribution";
            }
            else if (crowdedTitles.Count > 0)
            {
                category = "Angular Sector Crowding Detected";
            }
            else
            {
                category = "Irregular Radial Jitter";
            }

            string summary = FormatRadialLayoutSummary(
                targetList.Count, ringMetrics.Count, meanUniformity, meanJitter, fidelityScore, category, crowdedTitles.Count, isSelection);

            StatusMessage = summary;

            return new RadialLayoutAudit(
                targetList.Count,
                linksInScope.Count,
                ringMetrics.Count,
                Math.Round(meanJitter, 1),
                Math.Round(meanUniformity, 1),
                fidelityScore,
                ringMetrics,
                nodeRadialList,
                crowdedTitles,
                category,
                isSelection,
                summary);
        }

        public static RadialLayoutAudit AuditRadialLayoutAngularSpacing(IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditRadialLayoutAngularSpacing();
        }

        public static string FormatRadialLayoutSummary(
            int nodeCount, int ringCount, double angularScore, double radiusJitter,
            double fidelityScore, string category, int crowdedCount, bool isSelection = false)
        {
            string scopeStr = isSelection ? "Selected Nodes · " : "";
            if (nodeCount == 0) return $"Graph radial layout ({scopeStr}0 nodes): Empty mindmap";

            string nodeLabel = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string ringLabel = ringCount == 1 ? "1 ring" : $"{ringCount} rings";
            string crowdAlert = crowdedCount > 0 ? $" · ⚠ {crowdedCount} crowded sector{(crowdedCount == 1 ? "" : "s")}" : " · Equidistant";

            return $"Graph radial layout ({scopeStr}{nodeLabel}, {ringLabel} · {category}): Fidelity {fidelityScore:0.#}% (Angular uniformity {angularScore:0.#}%, Jitter ±{radiusJitter:0.#}px){crowdAlert}";
        }

        /// <summary>
        /// Audits graph clustering coefficients, triadic closure, and network transitivity
        /// to evaluate local community cohesiveness and open triplet bridging opportunities.
        /// </summary>
        public GraphClusteringAndTransitivityAudit AuditClusteringCoefficientAndTransitivity(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            if (targetNodes.Count == 0)
            {
                string emptySummary = FormatClusteringCoefficientSummary(0, 0, 0, 0.0, 0.0, "Empty Galaxy", 0, selectionOnly);
                return new GraphClusteringAndTransitivityAudit(
                    TotalNodes: 0,
                    TotalLinks: 0,
                    TotalTriangles: 0,
                    TotalClosedTriplets: 0,
                    TotalOpenTriplets: 0,
                    GlobalTransitivity: 100.0,
                    MeanClusteringCoefficient: 100.0,
                    Nodes: Array.Empty<NodeClusteringInfo>(),
                    HighClusteringNodeTitles: Array.Empty<string>(),
                    BridgeBottleneckNodeTitles: Array.Empty<string>(),
                    TopologyCategory: "Empty Galaxy",
                    ClusteringFidelityScore: 100.0,
                    IsSelectionScope: selectionOnly,
                    Summary: emptySummary);
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.Ordinal);
            var adj = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var n in targetNodes)
            {
                adj[n.Id] = new HashSet<string>(StringComparer.Ordinal);
            }

            int relevantLinks = 0;
            foreach (var l in Links)
            {
                if (nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId) && l.SourceNodeId != l.TargetNodeId)
                {
                    if (adj[l.SourceNodeId].Add(l.TargetNodeId))
                    {
                        adj[l.TargetNodeId].Add(l.SourceNodeId);
                        relevantLinks++;
                    }
                }
            }

            var nodeMetrics = new List<NodeClusteringInfo>();
            int totalTrianglesCount = 0;
            int totalConnectedTriplets = 0;
            int totalOpenTriplets = 0;

            foreach (var n in targetNodes)
            {
                var neighbors = adj[n.Id].ToList();
                int degree = neighbors.Count;

                if (degree < 2)
                {
                    nodeMetrics.Add(new NodeClusteringInfo(
                        NodeId: n.Id,
                        NodeTitle: string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title,
                        Degree: degree,
                        TriangleCount: 0,
                        LocalClusteringCoefficient: 0.0,
                        OpenTripletCount: 0));
                    continue;
                }

                int localTriangles = 0;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    for (int j = i + 1; j < neighbors.Count; j++)
                    {
                        if (adj[neighbors[i]].Contains(neighbors[j]))
                        {
                            localTriangles++;
                        }
                    }
                }

                int possibleTriangles = (degree * (degree - 1)) / 2;
                int openTriplets = possibleTriangles - localTriangles;
                double localC = possibleTriangles > 0 ? (double)localTriangles / possibleTriangles : 0.0;

                totalTrianglesCount += localTriangles;
                totalConnectedTriplets += possibleTriangles;
                totalOpenTriplets += openTriplets;

                nodeMetrics.Add(new NodeClusteringInfo(
                    NodeId: n.Id,
                    NodeTitle: string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title,
                    Degree: degree,
                    TriangleCount: localTriangles,
                    LocalClusteringCoefficient: Math.Round(localC * 100.0, 1),
                    OpenTripletCount: openTriplets));
            }

            int uniqueTriangles = totalTrianglesCount / 3;
            double globalTransitivity = totalConnectedTriplets > 0
                ? ((3.0 * uniqueTriangles) / totalConnectedTriplets) * 100.0
                : 0.0;

            var nodesWithDegreeGte2 = nodeMetrics.Where(m => m.Degree >= 2).ToList();
            double meanLocalC = nodesWithDegreeGte2.Count > 0
                ? nodesWithDegreeGte2.Average(m => m.LocalClusteringCoefficient)
                : 0.0;

            var highClustering = nodeMetrics
                .Where(m => m.Degree >= 2 && m.LocalClusteringCoefficient >= 50.0)
                .OrderByDescending(m => m.LocalClusteringCoefficient)
                .ThenByDescending(m => m.TriangleCount)
                .Select(m => m.NodeTitle)
                .Take(5)
                .ToList();

            var bridgeBottlenecks = nodeMetrics
                .Where(m => m.Degree >= 3 && m.LocalClusteringCoefficient == 0.0)
                .OrderByDescending(m => m.Degree)
                .Select(m => m.NodeTitle)
                .Take(5)
                .ToList();

            string category;
            double fidelityScore;

            if (targetNodes.Count <= 2 || relevantLinks == 0)
            {
                category = "Disconnected or Minimal Cluster";
                fidelityScore = 100.0;
            }
            else if (uniqueTriangles == 0)
            {
                category = "Acyclic Tree Hierarchy (0 Triangles)";
                fidelityScore = 95.0;
            }
            else if (globalTransitivity >= 45.0)
            {
                category = "Dense Triadic Mesh (High Semantic Clustering)";
                fidelityScore = Math.Min(100.0, 75.0 + (globalTransitivity * 0.25));
            }
            else if (globalTransitivity >= 20.0)
            {
                category = "Harmonic Small-World Network";
                fidelityScore = 90.0;
            }
            else
            {
                category = "Sparse Conceptual Web";
                fidelityScore = 80.0;
            }

            string summary = FormatClusteringCoefficientSummary(
                targetNodes.Count, relevantLinks, uniqueTriangles, globalTransitivity,
                meanLocalC, category, openTriplets: totalOpenTriplets, isSelection: selectionOnly);

            return new GraphClusteringAndTransitivityAudit(
                TotalNodes: targetNodes.Count,
                TotalLinks: relevantLinks,
                TotalTriangles: uniqueTriangles,
                TotalClosedTriplets: uniqueTriangles,
                TotalOpenTriplets: totalOpenTriplets,
                GlobalTransitivity: Math.Round(globalTransitivity, 1),
                MeanClusteringCoefficient: Math.Round(meanLocalC, 1),
                Nodes: nodeMetrics,
                HighClusteringNodeTitles: highClustering,
                BridgeBottleneckNodeTitles: bridgeBottlenecks,
                TopologyCategory: category,
                ClusteringFidelityScore: Math.Round(fidelityScore, 1),
                IsSelectionScope: selectionOnly,
                Summary: summary);
        }

        public static GraphClusteringAndTransitivityAudit AuditClusteringCoefficientAndTransitivity(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditClusteringCoefficientAndTransitivity();
        }

        /// <summary>
        /// Audits mind map graph node clustering coefficients, local triangles count, and global transitivity.
        /// </summary>
        public GraphClusteringAndTransitivityAudit AuditClusteringCoefficientAndTriangles(bool selectionOnly = false)
            => AuditClusteringCoefficientAndTransitivity(selectionOnly);

        /// <summary>
        /// Audits mind map graph node clustering coefficients, local triangles count, and global transitivity.
        /// </summary>
        public static GraphClusteringAndTransitivityAudit AuditClusteringCoefficientAndTriangles(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
            => AuditClusteringCoefficientAndTransitivity(nodes, links);

        public static string FormatClusteringCoefficientSummary(
            int nodeCount, int linkCount, int triangleCount, double transitivity,
            double meanLocalC, string category, int openTriplets = 0, bool isSelection = false)
        {
            string scope = isSelection ? "Selection" : "Galaxy";
            if (nodeCount == 0) return $"Graph clustering & transitivity audit ({scope}): 0 nodes · Empty map";

            string nodeStr = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkStr = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string triStr = triangleCount == 1 ? "1 triangle" : $"{triangleCount} triangles";

            return $"Graph clustering & transitivity audit ({scope} · {nodeStr}, {linkStr}, {triStr} · {category} · {transitivity:0.#}% transitivity · {meanLocalC:0.#}% mean local C · {openTriplets} open triplets)";
        }

        /// <summary>
        /// Audits the mind map graph for algebraic connectivity (Fiedler value lambda_2),
        /// spectral bisection, graph cut size, and conductance across nodes.
        /// Evaluates structural resilience and community partition boundaries.
        /// </summary>
        public GraphSpectralBisectionAudit AuditSpectralBisectionAndAlgebraicConnectivity(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly
                ? Nodes.Where(n => n.IsSelected && !n.IsDimmed).ToList()
                : Nodes.Where(n => !n.IsDimmed).ToList());

            if (targetNodes.Count == 0 && selectionOnly)
            {
                targetNodes = Nodes.Where(n => !n.IsDimmed).ToList();
                selectionOnly = false;
            }

            if (targetNodes.Count == 0)
            {
                string emptySummary = $"Spectral bisection audit ({(selectionOnly ? "Selection" : "Galaxy")}): 0 nodes · Empty map";
                return new GraphSpectralBisectionAudit(
                    TotalNodes: 0,
                    TotalLinks: 0,
                    ConnectedComponents: 0,
                    AlgebraicConnectivity: 0.0,
                    CutEdgesCount: 0,
                    Conductance: 0.0,
                    PartitionASize: 0,
                    PartitionBSize: 0,
                    Nodes: Array.Empty<NodeSpectralInfo>(),
                    BoundaryBridgeNodeTitles: Array.Empty<string>(),
                    SpectralRobustnessCategory: "Empty Graph",
                    SpectralFidelityScore: 100.0,
                    IsSelectionScope: selectionOnly,
                    Summary: emptySummary);
            }

            var nodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var idToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetNodes.Count; i++) idToIndex[targetNodes[i].Id] = i;

            var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes) adj[n.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int relevantLinks = 0;
            // 1. Parent-child links
            foreach (var n in targetNodes)
            {
                if (!string.IsNullOrEmpty(n.ParentId) && nodeIds.Contains(n.ParentId) && n.ParentId != n.Id)
                {
                    if (adj[n.Id].Add(n.ParentId))
                    {
                        adj[n.ParentId].Add(n.Id);
                        relevantLinks++;
                    }
                }
            }

            // 2. Explicit links
            foreach (var l in Links)
            {
                if (!string.IsNullOrEmpty(l.SourceNodeId) && !string.IsNullOrEmpty(l.TargetNodeId) &&
                    nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId) &&
                    l.SourceNodeId != l.TargetNodeId)
                {
                    if (adj[l.SourceNodeId].Add(l.TargetNodeId))
                    {
                        adj[l.TargetNodeId].Add(l.SourceNodeId);
                        relevantLinks++;
                    }
                }
            }

            // 3. Connected components discovery via BFS
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var components = new List<List<string>>();

            foreach (var n in targetNodes)
            {
                if (!visited.Contains(n.Id))
                {
                    var comp = new List<string>();
                    var queue = new Queue<string>();
                    queue.Enqueue(n.Id);
                    visited.Add(n.Id);

                    while (queue.Count > 0)
                    {
                        var curr = queue.Dequeue();
                        comp.Add(curr);
                        foreach (var neighbor in adj[curr])
                        {
                            if (visited.Add(neighbor))
                            {
                                queue.Enqueue(neighbor);
                            }
                        }
                    }
                    components.Add(comp);
                }
            }

            int nNodes = targetNodes.Count;
            double algebraicConnectivity = 0.0;
            int cutEdges = 0;
            double conductance = 0.0;
            var partitionA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var partitionB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double[] fiedlerVector = new double[nNodes];

            if (components.Count > 1)
            {
                // Disconnected graph: lambda_2 = 0 identically
                algebraicConnectivity = 0.0;
                // Partition into component 0 vs all other components
                foreach (var id in components[0]) partitionA.Add(id);
                for (int c = 1; c < components.Count; c++)
                {
                    foreach (var id in components[c]) partitionB.Add(id);
                }

                for (int i = 0; i < nNodes; i++)
                {
                    fiedlerVector[i] = partitionA.Contains(targetNodes[i].Id) ? 1.0 : -1.0;
                }
                cutEdges = 0;
                conductance = 0.0;
            }
            else if (nNodes == 1)
            {
                algebraicConnectivity = 0.0;
                partitionA.Add(targetNodes[0].Id);
                fiedlerVector[0] = 0.0;
            }
            else
            {
                // Connected graph (k == 1): Power iteration for Fiedler vector
                int[] degrees = new int[nNodes];
                int maxDegree = 1;
                for (int i = 0; i < nNodes; i++)
                {
                    degrees[i] = adj[targetNodes[i].Id].Count;
                    if (degrees[i] > maxDegree) maxDegree = degrees[i];
                }

                // Initial vector orthogonal to 1
                double[] x = new double[nNodes];
                for (int i = 0; i < nNodes; i++) x[i] = (i % 2 == 0) ? 1.0 : -1.0;
                double meanInit = x.Average();
                for (int i = 0; i < nNodes; i++) x[i] -= meanInit;

                double normInit = Math.Sqrt(x.Sum(v => v * v));
                if (normInit > 1e-10) for (int i = 0; i < nNodes; i++) x[i] /= normInit;

                // Power iteration on M = I - (1/d_max) L
                for (int iter = 0; iter < 45; iter++)
                {
                    double[] y = new double[nNodes];
                    for (int i = 0; i < nNodes; i++)
                    {
                        double sumNeighbors = 0.0;
                        foreach (var nbrId in adj[targetNodes[i].Id])
                        {
                            sumNeighbors += x[idToIndex[nbrId]];
                        }
                        y[i] = (1.0 - (double)degrees[i] / maxDegree) * x[i] + (1.0 / maxDegree) * sumNeighbors;
                    }

                    double meanY = y.Average();
                    for (int i = 0; i < nNodes; i++) y[i] -= meanY;

                    double normY = Math.Sqrt(y.Sum(v => v * v));
                    if (normY < 1e-12) break;

                    for (int i = 0; i < nNodes; i++) x[i] = y[i] / normY;
                }

                // Compute Rayleigh quotient lambda_2 = x^T L x
                double lambdaSum = 0.0;
                for (int i = 0; i < nNodes; i++)
                {
                    double lx = degrees[i] * x[i];
                    foreach (var nbrId in adj[targetNodes[i].Id])
                    {
                        lx -= x[idToIndex[nbrId]];
                    }
                    lambdaSum += x[i] * lx;
                }
                algebraicConnectivity = Math.Max(0.0, Math.Round(lambdaSum, 3));
                fiedlerVector = x;

                for (int i = 0; i < nNodes; i++)
                {
                    if (x[i] >= 0.0) partitionA.Add(targetNodes[i].Id);
                    else partitionB.Add(targetNodes[i].Id);
                }

                // If one partition is empty (all values same sign), split at median
                if (partitionA.Count == 0 || partitionB.Count == 0)
                {
                    partitionA.Clear();
                    partitionB.Clear();
                    var sortedIndices = Enumerable.Range(0, nNodes).OrderBy(i => x[i]).ToList();
                    int half = nNodes / 2;
                    for (int i = 0; i < nNodes; i++)
                    {
                        if (i < half) partitionA.Add(targetNodes[sortedIndices[i]].Id);
                        else partitionB.Add(targetNodes[sortedIndices[i]].Id);
                    }
                }

                // Calculate cut edges
                int volA = 0;
                int volB = 0;
                for (int i = 0; i < nNodes; i++)
                {
                    string uid = targetNodes[i].Id;
                    bool inA = partitionA.Contains(uid);
                    if (inA) volA += degrees[i];
                    else volB += degrees[i];

                    foreach (var vid in adj[uid])
                    {
                        if (idToIndex[vid] > i)
                        {
                            bool vInA = partitionA.Contains(vid);
                            if (inA != vInA) cutEdges++;
                        }
                    }
                }

                int minVol = Math.Min(volA, volB);
                conductance = minVol > 0 ? (double)cutEdges / minVol : 0.0;
            }

            var boundaryNodes = new List<string>();
            var nodeMetrics = new List<NodeSpectralInfo>();

            for (int i = 0; i < nNodes; i++)
            {
                var n = targetNodes[i];
                bool inA = partitionA.Contains(n.Id);
                string cluster = inA ? "Cluster A (+)" : "Cluster B (−)";
                bool isBoundary = adj[n.Id].Any(nbr => partitionA.Contains(nbr) != inA);

                if (isBoundary)
                {
                    boundaryNodes.Add(string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title);
                }

                nodeMetrics.Add(new NodeSpectralInfo(
                    NodeId: n.Id,
                    NodeTitle: string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title,
                    Degree: adj[n.Id].Count,
                    FiedlerValue: Math.Round(fiedlerVector[i], 3),
                    PartitionCluster: cluster,
                    IsCutBoundaryNode: isBoundary));
            }

            string category;
            double fidelityScore;

            if (components.Count > 1)
            {
                category = $"Disconnected Topology ({components.Count} components · λ₂ = 0)";
                fidelityScore = 70.0;
            }
            else if (algebraicConnectivity < 0.25)
            {
                category = "Fragile Semantic Bottleneck / Sparse Bridge";
                fidelityScore = 80.0;
            }
            else if (algebraicConnectivity < 0.8)
            {
                category = "Moderate Cohesion / Clustered Communities";
                fidelityScore = 90.0;
            }
            else if (algebraicConnectivity < 1.5)
            {
                category = "Robust Small-World Mesh";
                fidelityScore = 95.0;
            }
            else
            {
                category = "Highly Dense Interconnected Topology";
                fidelityScore = 100.0;
            }

            string summary = FormatSpectralBisectionSummary(
                nNodes, relevantLinks, components.Count, algebraicConnectivity, cutEdges, conductance,
                partitionA.Count, partitionB.Count, category, isSelection: selectionOnly);

            return new GraphSpectralBisectionAudit(
                TotalNodes: nNodes,
                TotalLinks: relevantLinks,
                ConnectedComponents: components.Count,
                AlgebraicConnectivity: algebraicConnectivity,
                CutEdgesCount: cutEdges,
                Conductance: Math.Round(conductance, 3),
                PartitionASize: partitionA.Count,
                PartitionBSize: partitionB.Count,
                Nodes: nodeMetrics,
                BoundaryBridgeNodeTitles: boundaryNodes.Take(5).ToList(),
                SpectralRobustnessCategory: category,
                SpectralFidelityScore: Math.Round(fidelityScore, 1),
                IsSelectionScope: selectionOnly,
                Summary: summary);
        }

        public static GraphSpectralBisectionAudit AuditSpectralBisectionAndAlgebraicConnectivity(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditSpectralBisectionAndAlgebraicConnectivity();
        }

        public static string FormatSpectralBisectionSummary(
            int nodeCount, int linkCount, int components, double lambda2, int cutEdges, double conductance,
            int partASize, int partBSize, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection" : "Galaxy";
            if (nodeCount == 0) return $"Spectral bisection audit ({scope}): 0 nodes · Empty map";

            string nodeStr = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkStr = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string cutStr = cutEdges == 1 ? "1 cut edge" : $"{cutEdges} cut edges";
            string compStr = components == 1 ? "1 component" : $"{components} components";

            return $"Spectral bisection audit ({scope} · {nodeStr}, {linkStr} · λ₂={lambda2:0.###} · {category} · Cut: {cutStr}, {conductance:0.###} cond · {partASize} vs {partBSize} bisection · {compStr})";
        }

        /// <summary>
        /// Audits the mindmap graph for biconnected components (maximal 2-connected blocks),
        /// articulation points (cut vertices), and bridge edges using the Hopcroft-Tarjan DFS algorithm.
        /// </summary>
        public GraphBiconnectivityAudit AuditBiconnectedComponentsAndBridges(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly
                ? Nodes.Where(n => n.IsSelected && !n.IsDimmed).ToList()
                : Nodes.Where(n => !n.IsDimmed).ToList());

            bool isSelection = selectionOnly && Nodes.Any(n => n.IsSelected);
            string scope = isSelection ? "Selection" : "Galaxy";

            if (targetNodes.Count == 0)
            {
                string emptySummary = $"Biconnectivity audit ({scope}): 0 nodes · Empty map";
                StatusMessage = emptySummary;
                return new GraphBiconnectivityAudit(
                    0, 0, 0, 0, 0, 0, 100.0,
                    Array.Empty<NodeArticulationInfo>(),
                    Array.Empty<BridgeEdgeInfo>(),
                    Array.Empty<BiconnectedBlockInfo>(),
                    "Empty Graph", isSelection, emptySummary);
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var titleMap = targetNodes.ToDictionary(n => n.Id, n => string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title.Trim(), StringComparer.OrdinalIgnoreCase);

            if (targetNodes.Count == 1)
            {
                var single = targetNodes[0];
                string singleSummary = $"Biconnectivity audit ({scope} · 1 node): 0 articulation points, 0 bridges · 1 block · '{(string.IsNullOrWhiteSpace(single.Title) ? single.Id : single.Title)}'";
                StatusMessage = singleSummary;
                return new GraphBiconnectivityAudit(
                    1, 0, 1, 0, 0, 1, 100.0,
                    new[] { new NodeArticulationInfo(single.Id, titleMap[single.Id], 0, false, 1, 0) },
                    Array.Empty<BridgeEdgeInfo>(),
                    new[] { new BiconnectedBlockInfo(1, 1, 0, new[] { titleMap[single.Id] }, false) },
                    "Single Node (Trivially Biconnected)", isSelection, singleSummary);
            }

            // Build undirected adjacency list
            var adj = new Dictionary<string, List<(string neighborId, string linkId)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes) adj[n.Id] = new List<(string neighborId, string linkId)>();

            var relevantLinks = Links.Where(l => !string.IsNullOrEmpty(l.SourceNodeId) &&
                                                 !string.IsNullOrEmpty(l.TargetNodeId) &&
                                                 nodeSet.Contains(l.SourceNodeId) &&
                                                 nodeSet.Contains(l.TargetNodeId)).ToList();

            var addedEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var l in relevantLinks)
            {
                string u = l.SourceNodeId!;
                string v = l.TargetNodeId!;
                if (u.Equals(v, StringComparison.OrdinalIgnoreCase)) continue; // skip self-loops

                adj[u].Add((v, l.Id));
                adj[v].Add((u, l.Id));
                string key = string.Compare(u, v, StringComparison.OrdinalIgnoreCase) < 0 ? $"{u}--{v}" : $"{v}--{u}";
                addedEdges.Add(key);
            }

            // Fallback for hierarchical ParentId relationships when no explicit link exists
            int parentLinkIndex = 0;
            foreach (var n in targetNodes)
            {
                if (!string.IsNullOrEmpty(n.ParentId) && nodeSet.Contains(n.ParentId))
                {
                    string u = n.ParentId;
                    string v = n.Id;
                    string key = string.Compare(u, v, StringComparison.OrdinalIgnoreCase) < 0 ? $"{u}--{v}" : $"{v}--{u}";
                    if (addedEdges.Add(key))
                    {
                        string syntheticLinkId = $"hier_{++parentLinkIndex}";
                        adj[u].Add((v, syntheticLinkId));
                        adj[v].Add((u, syntheticLinkId));
                    }
                }
            }

            int timer = 0;
            int connectedComponents = 0;
            var discovery = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var isArtPoint = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bridges = new List<BridgeEdgeInfo>();
            var edgeStack = new Stack<(string u, string v, string linkId)>();
            var blocks = new List<BiconnectedBlockInfo>();
            var nodeBlockCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes) nodeBlockCount[n.Id] = 0;

            void Dfs(string u, string? p)
            {
                discovery[u] = low[u] = ++timer;
                parent[u] = p;
                int children = 0;

                foreach (var (v, linkId) in adj[u])
                {
                    if (v.Equals(p, StringComparison.OrdinalIgnoreCase)) continue;

                    if (discovery.ContainsKey(v))
                    {
                        // Back edge
                        low[u] = Math.Min(low[u], discovery[v]);
                        if (discovery[v] < discovery[u])
                        {
                            edgeStack.Push((u, v, linkId));
                        }
                    }
                    else
                    {
                        // Tree edge
                        children++;
                        edgeStack.Push((u, v, linkId));
                        Dfs(v, u);

                        low[u] = Math.Min(low[u], low[v]);

                        // Check articulation point condition
                        if (p == null && children > 1)
                        {
                            isArtPoint.Add(u);
                        }
                        if (p != null && low[v] >= discovery[u])
                        {
                            isArtPoint.Add(u);
                        }

                        // Check bridge condition
                        if (low[v] > discovery[u])
                        {
                            bridges.Add(new BridgeEdgeInfo(linkId, titleMap[u], titleMap[v], true));
                        }

                        // Extract biconnected component (block)
                        if ((p == null && children > 0) || (p != null && low[v] >= discovery[u]))
                        {
                            var blockNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var blockEdges = new List<(string u, string v, string linkId)>();

                            while (edgeStack.Count > 0)
                            {
                                var edge = edgeStack.Pop();
                                blockEdges.Add(edge);
                                blockNodeIds.Add(edge.u);
                                blockNodeIds.Add(edge.v);
                                if (edge.u.Equals(u, StringComparison.OrdinalIgnoreCase) &&
                                    edge.v.Equals(v, StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }
                            }

                            if (blockNodeIds.Count > 0)
                            {
                                foreach (var nid in blockNodeIds)
                                {
                                    if (nodeBlockCount.ContainsKey(nid)) nodeBlockCount[nid]++;
                                }

                                bool isCycleOrDense = blockEdges.Count >= blockNodeIds.Count && blockNodeIds.Count >= 3;
                                var titles = blockNodeIds.Select(nid => titleMap[nid]).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
                                blocks.Add(new BiconnectedBlockInfo(blocks.Count + 1, blockNodeIds.Count, blockEdges.Count, titles, isCycleOrDense));
                            }
                        }
                    }
                }
            }

            foreach (var n in targetNodes)
            {
                if (!discovery.ContainsKey(n.Id))
                {
                    connectedComponents++;
                    Dfs(n.Id, null);

                    // If any remaining edges on stack for this component (e.g. single block tree edge)
                    if (edgeStack.Count > 0)
                    {
                        var blockNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var blockEdges = new List<(string u, string v, string linkId)>();
                        while (edgeStack.Count > 0)
                        {
                            var edge = edgeStack.Pop();
                            blockEdges.Add(edge);
                            blockNodeIds.Add(edge.u);
                            blockNodeIds.Add(edge.v);
                        }
                        if (blockNodeIds.Count > 0)
                        {
                            foreach (var nid in blockNodeIds)
                            {
                                if (nodeBlockCount.ContainsKey(nid)) nodeBlockCount[nid]++;
                            }
                            bool isCycleOrDense = blockEdges.Count >= blockNodeIds.Count && blockNodeIds.Count >= 3;
                            var titles = blockNodeIds.Select(nid => titleMap[nid]).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
                            blocks.Add(new BiconnectedBlockInfo(blocks.Count + 1, blockNodeIds.Count, blockEdges.Count, titles, isCycleOrDense));
                        }
                    }

                    // Isolated single node in multi-node graph
                    if (adj[n.Id].Count == 0 && nodeBlockCount[n.Id] == 0)
                    {
                        blocks.Add(new BiconnectedBlockInfo(blocks.Count + 1, 1, 0, new[] { titleMap[n.Id] }, false));
                        nodeBlockCount[n.Id] = 1;
                    }
                }
            }

            var nodeArticulationInfos = targetNodes.Select(n =>
            {
                bool isCut = isArtPoint.Contains(n.Id);
                int deg = adj[n.Id].Count;
                int bCount = nodeBlockCount[n.Id];
                int disconnectedIfRemoved = isCut ? Math.Max(1, bCount) : 0;
                return new NodeArticulationInfo(n.Id, titleMap[n.Id], deg, isCut, bCount, disconnectedIfRemoved);
            }).OrderByDescending(a => a.IsArticulationPoint).ThenByDescending(a => a.Degree).ToList();

            int artCount = isArtPoint.Count;
            int bridgeCount = bridges.Count;
            int blockCount = blocks.Count;
            int totalLinksCalculated = addedEdges.Count;

            string category;
            if (connectedComponents > 1)
                category = $"Disconnected Multi-Component Graph ({connectedComponents} components)";
            else if (artCount == 0 && targetNodes.Count >= 3)
                category = "Robust 2-Connected Mesh (Zero Bottlenecks)";
            else if (bridgeCount == targetNodes.Count - 1 && targetNodes.Count >= 2)
                category = "Acyclic Tree Hierarchy (All Edges are Bridges)";
            else if (artCount > 0)
                category = $"Articulated Topology ({artCount} Single-Point Failure Bottlenecks)";
            else
                category = "Single Biconnected Block";

            double artRatio = (double)artCount / targetNodes.Count;
            double bridgeRatio = totalLinksCalculated > 0 ? (double)bridgeCount / totalLinksCalculated : 0.0;
            double rawScore = 100.0 - (artRatio * 45.0) - (bridgeRatio * 35.0);
            if (connectedComponents > 1) rawScore -= (connectedComponents - 1) * 10.0;
            double biconnectivityScore = Math.Round(Math.Clamp(rawScore, 10.0, 100.0), 1);
            if (artCount == 0 && connectedComponents == 1) biconnectivityScore = 100.0;

            string summary = FormatBiconnectivitySummary(
                targetNodes.Count, totalLinksCalculated, connectedComponents, artCount, bridgeCount, blockCount,
                biconnectivityScore, category, isSelection);

            StatusMessage = summary;

            return new GraphBiconnectivityAudit(
                targetNodes.Count,
                totalLinksCalculated,
                connectedComponents,
                artCount,
                bridgeCount,
                blockCount,
                biconnectivityScore,
                nodeArticulationInfos,
                bridges,
                blocks,
                category,
                isSelection,
                summary);
        }

        public static GraphBiconnectivityAudit AuditBiconnectedComponentsAndBridges(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditBiconnectedComponentsAndBridges();
        }

        public static string FormatBiconnectivitySummary(
            int nodeCount, int linkCount, int components, int artCount, int bridgeCount, int blockCount,
            double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection" : "Galaxy";
            if (nodeCount == 0) return $"Biconnectivity audit ({scope}): 0 nodes · Empty map";

            string nodeStr = nodeCount == 1 ? "1 node" : $"{nodeCount} nodes";
            string linkStr = linkCount == 1 ? "1 link" : $"{linkCount} links";
            string artStr = artCount == 1 ? "1 cut-vertex" : $"{artCount} cut-vertices";
            string bridgeStr = bridgeCount == 1 ? "1 bridge" : $"{bridgeCount} bridges";
            string blockStr = blockCount == 1 ? "1 block" : $"{blockCount} blocks";

            return $"Biconnectivity audit ({scope} · {nodeStr}, {linkStr} · {category} · {artStr}, {bridgeStr} · {blockStr} · {score:0.#}% robustness)";
        }

        /// <summary>
        /// Audits mind map knowledge graph core-periphery structure using k-core decomposition.
        /// Identifies concept shell indices, isolates the dense conceptual nucleus (k-max core),
        /// and evaluates core-periphery balance.
        /// </summary>
        public GraphKCoreDecompositionAudit AuditKCoreDecompositionAndShells(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly
                ? Nodes.Where(n => n.IsSelected && !n.IsDimmed).ToList()
                : Nodes.Where(n => !n.IsDimmed).ToList());

            bool isSelection = selectionOnly && Nodes.Any(n => n.IsSelected);

            if (targetNodes.Count == 0)
            {
                string emptySummary = isSelection
                    ? "K-Core decomposition audit (Selection): 0 nodes · No nodes selected"
                    : "K-Core decomposition audit (Galaxy): 0 nodes · Knowledge graph is empty";
                return new GraphKCoreDecompositionAudit(
                    0, 0, 0, 0, 0, 0, 0.0, 0.0,
                    Array.Empty<NodeKCoreShellInfo>(),
                    new Dictionary<int, int>(),
                    "Empty Graph",
                    isSelection,
                    emptySummary);
            }


            var nodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

            var relevantLinks = Links
                .Where(l => !string.IsNullOrEmpty(l.SourceNodeId) &&
                            !string.IsNullOrEmpty(l.TargetNodeId) &&
                            nodeIds.Contains(l.SourceNodeId) &&
                            nodeIds.Contains(l.TargetNodeId) &&
                            !string.Equals(l.SourceNodeId, l.TargetNodeId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Build undirected adjacency list with deduplicated edges
            var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes) adj[n.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int totalEdges = 0;
            foreach (var l in relevantLinks)
            {
                if (adj[l.SourceNodeId].Add(l.TargetNodeId))
                {
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                    totalEdges++;
                }
            }

            // Fallback for hierarchical ParentId relationships when no explicit link exists
            foreach (var n in targetNodes)
            {
                if (!string.IsNullOrEmpty(n.ParentId) && nodeIds.Contains(n.ParentId))
                {
                    if (adj[n.ParentId].Add(n.Id))
                    {
                        adj[n.Id].Add(n.ParentId);
                        totalEdges++;
                    }
                }
            }

            // K-Core decomposition via iterative degree pruning
            var degrees = targetNodes.ToDictionary(n => n.Id, n => adj[n.Id].Count, StringComparer.OrdinalIgnoreCase);
            var remaining = new HashSet<string>(nodeIds, StringComparer.OrdinalIgnoreCase);
            var shellAssignment = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int currentK = 0;
            while (remaining.Count > 0)
            {
                var toRemove = remaining.Where(id => degrees[id] <= currentK).ToList();
                if (toRemove.Count == 0)
                {
                    currentK++;
                    continue;
                }

                foreach (var id in toRemove)
                {
                    shellAssignment[id] = currentK;
                    remaining.Remove(id);
                    foreach (var neighbor in adj[id])
                    {
                        if (remaining.Contains(neighbor))
                        {
                            degrees[neighbor] = Math.Max(0, degrees[neighbor] - 1);
                        }
                    }
                }
            }

            int maxCoreness = shellAssignment.Values.Count > 0 ? shellAssignment.Values.Max() : 0;
            var shellDistribution = shellAssignment.Values
                .GroupBy(k => k)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count());

            var nodeInfos = new List<NodeKCoreShellInfo>();
            int nucleusCount = 0;
            int peripheralCount = 0;
            int intermediateCount = 0;

            foreach (var n in targetNodes)
            {
                int shell = shellAssignment.TryGetValue(n.Id, out int sVal) ? sVal : 0;
                int deg = adj[n.Id].Count;
                bool isNucleus = maxCoreness > 0 ? shell == maxCoreness : true;
                bool isPeripheral = shell <= 1;

                if (isNucleus) nucleusCount++;
                if (isPeripheral) peripheralCount++;
                if (!isNucleus && !isPeripheral) intermediateCount++;

                nodeInfos.Add(new NodeKCoreShellInfo(
                    n.Id,
                    n.Title ?? "Untitled",
                    deg,
                    shell,
                    isNucleus,
                    isPeripheral));
            }

            nodeInfos = nodeInfos.OrderByDescending(n => n.CorenessShell).ThenByDescending(n => n.Degree).ToList();

            double meanCoreness = Math.Round(nodeInfos.Average(n => n.CorenessShell), 2);
            double corePeripheryRatio = peripheralCount > 0
                ? Math.Round((double)nucleusCount / peripheralCount, 2)
                : (nucleusCount > 0 ? 1.0 : 0.0);

            string category;
            if (targetNodes.Count <= 2) category = "Trivial Subgraph";
            else if (maxCoreness >= 3) category = "Dense Multi-Tiered Nucleus";
            else if (maxCoreness == 2) category = "Hierarchical Mesh / Ring";
            else if (maxCoreness == 1 && totalEdges >= targetNodes.Count - 1) category = "Tree / Star Hierarchy";
            else if (maxCoreness == 1) category = "Sparse Forest";
            else category = "Isolated Concepts (Disconnected)";

            string summary = FormatKCoreSummary(
                targetNodes.Count, totalEdges, maxCoreness, nucleusCount, peripheralCount,
                corePeripheryRatio, category, isSelection, nodeInfos);

            return new GraphKCoreDecompositionAudit(
                targetNodes.Count,
                totalEdges,
                maxCoreness,
                nucleusCount,
                peripheralCount,
                intermediateCount,
                corePeripheryRatio,
                meanCoreness,
                nodeInfos,
                shellDistribution,
                category,
                isSelection,
                summary);
        }

        public static GraphKCoreDecompositionAudit AuditKCoreDecompositionAndShells(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditKCoreDecompositionAndShells();
        }

        public static string FormatKCoreSummary(
            int totalNodes, int totalEdges, int maxCoreness, int nucleusCount, int peripheralCount,
            double corePeripheryRatio, string category, bool isSelection = false,
            IReadOnlyList<NodeKCoreShellInfo>? nodes = null)
        {
            string scope = isSelection ? "Selection" : "Galaxy";
            if (totalNodes == 0) return $"K-Core decomposition audit ({scope}): 0 nodes · Empty map";

            string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeLabel = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";
            string shellLabel = $"Max Core k={maxCoreness}";
            string breakdown = $"{nucleusCount} nucleus, {peripheralCount} peripheral";

            string topNucleus = "";
            if (nodes != null && nodes.Count > 0)
            {
                var top = nodes.Where(n => n.IsInNucleus).Take(3).Select(n => n.Label);
                if (top.Any()) topNucleus = $" · Core: {string.Join(", ", top)}";
            }

            return $"K-Core decomposition audit ({scope} · {nodeLabel}, {edgeLabel} · {shellLabel} [{breakdown}] · {category}): " +
                   $"C/P ratio {corePeripheryRatio:0.##}{topNucleus}";
        }

        public GraphKCoreDecompositionAudit AuditGraphKCoreDecompositionAndNuclei(bool selectionOnly = false)
            => AuditKCoreDecompositionAndShells(selectionOnly);

        public static GraphKCoreDecompositionAudit AuditGraphKCoreDecompositionAndNuclei(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
            => AuditKCoreDecompositionAndShells(nodes, links);

        public GraphAssortativityAudit AuditGraphAssortativityAndMixing(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList());

            bool isSelection = selectionOnly;
            if (targetNodes.Count == 0 && selectionOnly)
            {
                targetNodes = Nodes.ToList();
                isSelection = false;
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id));
            var targetLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            if (targetNodes.Count == 0)
            {
                string emptySummary = FormatAssortativitySummary(0, 0, 0.0, 0, 0, 0, 0.0, 0.0, "Empty Graph", isSelection);
                return new GraphAssortativityAudit(
                    0, 0, 0.0, 0, 0, 0, 0.0, 0.0,
                    Array.Empty<NodeDegreeAssortativityInfo>(),
                    "Empty Graph", 100.0, isSelection, emptySummary);
            }

            var adj = new Dictionary<string, List<string>>();
            foreach (var n in targetNodes) adj[n.Id] = new List<string>();
            foreach (var l in targetLinks)
            {
                if (l.SourceNodeId != l.TargetNodeId)
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            int totalNodes = targetNodes.Count;
            int totalEdges = targetLinks.Count;

            var degrees = targetNodes.ToDictionary(n => n.Id, n => adj[n.Id].Count);
            double meanDegree = degrees.Values.Count > 0 ? degrees.Values.Average() : 0.0;
            double degreeVariance = degrees.Values.Count > 0
                ? degrees.Values.Average(d => (d - meanDegree) * (d - meanDegree))
                : 0.0;
            double degreeStdDev = Math.Sqrt(degreeVariance);

            double r = 0.0;
            if (totalEdges > 0)
            {
                double sum_jk = 0.0;
                double sum_j_plus_k = 0.0;
                double sum_j2_plus_k2 = 0.0;

                foreach (var l in targetLinks)
                {
                    double j = degrees[l.SourceNodeId];
                    double k = degrees[l.TargetNodeId];

                    sum_jk += j * k;
                    sum_j_plus_k += 0.5 * (j + k);
                    sum_j2_plus_k2 += 0.5 * (j * j + k * k);
                }

                double M = totalEdges;
                double numerator = (sum_jk / M) - Math.Pow(sum_j_plus_k / M, 2);
                double denominator = (sum_j2_plus_k2 / M) - Math.Pow(sum_j_plus_k / M, 2);

                if (Math.Abs(denominator) > 1e-9)
                {
                    r = Math.Clamp(numerator / denominator, -1.0, 1.0);
                }
            }

            int hubThreshold = Math.Max(3, (int)Math.Ceiling(meanDegree + degreeStdDev));
            int hubHubEdges = 0;
            int hubLeafEdges = 0;
            int leafLeafEdges = 0;

            foreach (var l in targetLinks)
            {
                int degS = degrees[l.SourceNodeId];
                int degT = degrees[l.TargetNodeId];
                bool sHub = degS >= hubThreshold;
                bool tHub = degT >= hubThreshold;
                bool sLeaf = degS == 1;
                bool tLeaf = degT == 1;

                if (sHub && tHub) hubHubEdges++;
                else if ((sHub && tLeaf) || (tHub && sLeaf)) hubLeafEdges++;
                else if (sLeaf && tLeaf) leafLeafEdges++;
            }

            var nodeInfos = new List<NodeDegreeAssortativityInfo>();
            foreach (var n in targetNodes)
            {
                int deg = degrees[n.Id];
                var neighbors = adj[n.Id];
                double avgNeighborDeg = neighbors.Count > 0 ? neighbors.Average(nb => degrees[nb]) : 0.0;
                bool isHub = deg >= hubThreshold;
                bool isLeaf = deg == 1;

                nodeInfos.Add(new NodeDegreeAssortativityInfo(
                    n.Id,
                    n.Title ?? "Untitled",
                    deg,
                    Math.Round(avgNeighborDeg, 2),
                    isHub,
                    isLeaf));
            }

            nodeInfos = nodeInfos.OrderByDescending(n => n.Degree).ThenBy(n => n.Title).ToList();

            string category;
            if (totalNodes <= 2 || totalEdges == 0) category = "Trivial / Isolated Graph";
            else if (r >= 0.30) category = "Strongly Assortative (Resilient Hub Core)";
            else if (r >= 0.10) category = "Moderately Assortative";
            else if (r >= -0.10) category = "Neutral / Scale-Free Mixing";
            else if (r >= -0.30) category = "Moderately Disassortative";
            else category = "Strongly Disassortative (Radial Star Hubs)";

            double resilienceScore = totalNodes switch
            {
                0 => 100.0,
                1 => 100.0,
                2 => totalEdges > 0 ? 90.0 : 50.0,
                _ => Math.Clamp(50.0 + (r * 35.0) + Math.Min(15.0, hubHubEdges * 5.0) - (hubLeafEdges > hubHubEdges * 3 ? 10.0 : 0.0), 10.0, 100.0)
            };

            string summary = FormatAssortativitySummary(
                totalNodes, totalEdges, Math.Round(r, 2), hubHubEdges, hubLeafEdges, leafLeafEdges,
                Math.Round(meanDegree, 2), Math.Round(resilienceScore, 1), category, isSelection, nodeInfos);

            return new GraphAssortativityAudit(
                totalNodes,
                totalEdges,
                Math.Round(r, 2),
                hubHubEdges,
                hubLeafEdges,
                leafLeafEdges,
                Math.Round(meanDegree, 2),
                Math.Round(degreeStdDev, 2),
                nodeInfos,
                category,
                Math.Round(resilienceScore, 1),
                isSelection,
                summary);
        }

        public static GraphAssortativityAudit AuditGraphAssortativityAndMixing(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditGraphAssortativityAndMixing();
        }

        public static string FormatAssortativitySummary(
            int totalNodes, int totalEdges, double r, int hubHub, int hubLeaf, int leafLeaf,
            double meanDeg, double resilienceScore, string category, bool isSelection = false,
            IReadOnlyList<NodeDegreeAssortativityInfo>? nodes = null)
        {
            string scope = isSelection ? "Selection" : "Galaxy";
            if (totalNodes == 0) return $"Graph assortativity audit ({scope}): 0 nodes · Empty map";

            string nodeLabel = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeLabel = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";
            string rSign = r >= 0 ? "+" : "";

            string hubDetails = $"Hub-Hub: {hubHub}, Hub-Leaf: {hubLeaf}";
            string topHubs = "";
            if (nodes != null && nodes.Count > 0)
            {
                var hubs = nodes.Where(n => n.IsHub).Take(3).Select(n => $"{n.Title} (d={n.Degree})");
                if (hubs.Any()) topHubs = $" · Hubs: {string.Join(", ", hubs)}";
            }

            return $"Graph assortativity audit ({scope} · {nodeLabel}, {edgeLabel} · r={rSign}{r:0.##} [{category}] · Resilience: {resilienceScore:0.#}%): " +
                   $"{hubDetails}{topHubs}";
        }

        /// <summary>
        /// Audits mind map graph community modularity (Newman-Girvan Q), partition density,
        /// intra-cluster cohesiveness, and inter-cluster bridge edge distribution.
        /// </summary>
        public GraphCommunityModularityAudit AuditCommunityModularityAndClusters(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            if (targetNodes.Count == 0)
            {
                string emptySummary = FormatModularitySummary(0, 0, 0, 0.0, 0, 0, 100.0, "Empty Galaxy", selectionOnly);
                return new GraphCommunityModularityAudit(
                    0, 0, 0, 0.0, 0, 0, 100.0,
                    Array.Empty<CommunityClusterInfo>(), "Empty Galaxy", selectionOnly, emptySummary);
            }

            var nodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var nodeMap = targetNodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

            // Adjacency graph
            var adj = targetNodes.ToDictionary(n => n.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            int totalEdges = 0;

            // 1. Hierarchy links (parent-child)
            foreach (var n in targetNodes)
            {
                if (!string.IsNullOrEmpty(n.ParentId) && nodeIds.Contains(n.ParentId) && n.ParentId != n.Id)
                {
                    if (adj[n.Id].Add(n.ParentId))
                    {
                        adj[n.ParentId].Add(n.Id);
                        totalEdges++;
                    }
                }
            }

            // 2. Explicit links
            foreach (var l in Links)
            {
                if (!string.IsNullOrEmpty(l.SourceNodeId) && !string.IsNullOrEmpty(l.TargetNodeId) &&
                    nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId) &&
                    l.SourceNodeId != l.TargetNodeId)
                {
                    if (adj[l.SourceNodeId].Add(l.TargetNodeId))
                    {
                        adj[l.TargetNodeId].Add(l.SourceNodeId);
                        totalEdges++;
                    }
                }
            }

            // Community partitioning: Group by ColorHex or Connected Component
            var distinctColors = targetNodes.Select(n => n.ColorHex).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            var communityGroups = new Dictionary<string, List<MindMapNodeViewModel>>(StringComparer.OrdinalIgnoreCase);

            if (distinctColors.Count > 1)
            {
                foreach (var n in targetNodes)
                {
                    string key = string.IsNullOrEmpty(n.ColorHex) ? "Default" : n.ColorHex;
                    if (!communityGroups.TryGetValue(key, out var list))
                    {
                        list = new List<MindMapNodeViewModel>();
                        communityGroups[key] = list;
                    }
                    list.Add(n);
                }
            }
            else
            {
                // Group by root branches or connected components via BFS
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int compIdx = 1;
                foreach (var n in targetNodes)
                {
                    if (!visited.Contains(n.Id))
                    {
                        var compList = new List<MindMapNodeViewModel>();
                        var q = new Queue<string>();
                        q.Enqueue(n.Id);
                        visited.Add(n.Id);
                        while (q.Count > 0)
                        {
                            var curr = q.Dequeue();
                            if (nodeMap.TryGetValue(curr, out var vm)) compList.Add(vm);
                            foreach (var nbr in adj[curr])
                            {
                                if (visited.Add(nbr)) q.Enqueue(nbr);
                            }
                        }
                        communityGroups[$"Cluster {compIdx++}"] = compList;
                    }
                }
            }

            // Compute Newman-Girvan Modularity Q
            // Q = sum_c [ e_c / m - (K_c / (2m))^2 ]
            double Q = 0.0;
            int intraEdges = 0;
            int bridgeEdges = 0;
            var communityInfos = new List<CommunityClusterInfo>();

            if (totalEdges > 0)
            {
                double m = totalEdges;
                double twoM = 2.0 * m;

                foreach (var (cid, cNodes) in communityGroups)
                {
                    var cSet = new HashSet<string>(cNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
                    int e_c = 0;
                    int k_c = 0;

                    foreach (var n in cNodes)
                    {
                        int deg = adj[n.Id].Count;
                        k_c += deg;
                        foreach (var nbr in adj[n.Id])
                        {
                            if (cSet.Contains(nbr) && string.Compare(n.Id, nbr, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                e_c++;
                            }
                        }
                    }

                    intraEdges += e_c;
                    double term1 = e_c / m;
                    double term2 = Math.Pow(k_c / twoM, 2);
                    double deltaQ = term1 - term2;
                    Q += deltaQ;

                    double maxPossibleEdges = cNodes.Count * (cNodes.Count - 1) / 2.0;
                    double density = maxPossibleEdges > 0 ? (e_c / maxPossibleEdges) * 100.0 : 100.0;
                    string label = cNodes.Count == 1 ? cNodes[0].Title : $"{cid} ({cNodes.Count} nodes)";

                    communityInfos.Add(new CommunityClusterInfo(
                        cid,
                        label,
                        cNodes.Count,
                        e_c,
                        k_c,
                        Math.Round(density, 1),
                        Math.Round(deltaQ, 4)));
                }

                bridgeEdges = totalEdges - intraEdges;
            }
            else
            {
                // No edges in graph
                Q = 0.0;
                foreach (var (cid, cNodes) in communityGroups)
                {
                    communityInfos.Add(new CommunityClusterInfo(
                        cid, cid, cNodes.Count, 0, 0, 100.0, 0.0));
                }
            }

            Q = Math.Round(Math.Clamp(Q, -1.0, 1.0), 3);
            double healthScore = totalEdges == 0
                ? 100.0
                : Math.Round(Math.Clamp((Q + 0.2) / 0.7 * 100.0, 10.0, 100.0), 1);

            string category;
            if (totalEdges == 0) category = "Unconnected Nodes (0 Edges)";
            else if (Q >= 0.4) category = "Strong Community Modularity";
            else if (Q >= 0.2) category = "Moderate Cluster Partitioning";
            else if (Q >= 0.0) category = "Weak Community Structure";
            else category = "Anti-Community / Tangled Mixing";

            string summary = FormatModularitySummary(
                targetNodes.Count, totalEdges, communityGroups.Count, Q,
                intraEdges, bridgeEdges, healthScore, category, selectionOnly);

            return new GraphCommunityModularityAudit(
                targetNodes.Count,
                totalEdges,
                communityGroups.Count,
                Q,
                intraEdges,
                bridgeEdges,
                healthScore,
                communityInfos,
                category,
                selectionOnly,
                summary);
        }

        public static GraphCommunityModularityAudit AuditCommunityModularityAndClusters(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditCommunityModularityAndClusters();
        }

        public static string FormatModularitySummary(
            int totalNodes, int totalEdges, int communityCount, double Q,
            int intraEdges, int bridgeEdges, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph community modularity ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";
            string qSign = Q >= 0 ? "+" : "";

            return $"Graph community modularity ({scope}{nodeStr}, {edgeStr} across {communityCount} clusters · Q={qSign}{Q:0.###} [{category}] · {score:0.#}% score): " +
                   $"{intraEdges} intra-cluster edges, {bridgeEdges} bridge edges";
        }

        /// <summary>
        /// Audits directed cycles, feedback loop topology, and strongly connected components (Tarjan SCC)
        /// to evaluate semantic acyclicity and directed loop hazards in the knowledge graph.
        /// </summary>
        public GraphCycleAndFeedbackAudit AuditGraphCyclesAndFeedbackLoops(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            if (targetNodes.Count == 0)
            {
                string emptyScope = selectionOnly ? "Selection · " : "";
                string emptySummary = $"Graph cycle & feedback loop audit ({emptyScope}0 nodes): Empty map";
                return new GraphCycleAndFeedbackAudit(
                    0, 0, 0, 0, 0, 0, true, 100.0,
                    Array.Empty<GraphCycleComponentInfo>(),
                    "Empty Galaxy",
                    selectionOnly,
                    emptySummary);
            }

            var nodeMap = targetNodes.ToDictionary(n => n.Id, n => n);
            var targetLinks = Links.Where(l => nodeMap.ContainsKey(l.SourceNodeId) && nodeMap.ContainsKey(l.TargetNodeId)).ToList();

            var adj = new Dictionary<string, List<string>>();
            var selfLoops = new HashSet<string>();
            foreach (var n in targetNodes)
            {
                adj[n.Id] = new List<string>();
            }

            foreach (var l in targetLinks)
            {
                if (l.SourceNodeId == l.TargetNodeId)
                {
                    selfLoops.Add(l.SourceNodeId);
                }
                else if (adj.ContainsKey(l.SourceNodeId))
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                }
            }

            // Tarjan SCC Algorithm
            int index = 0;
            var indices = new Dictionary<string, int>();
            var lowlinks = new Dictionary<string, int>();
            var stack = new Stack<string>();
            var onStack = new HashSet<string>();
            var sccs = new List<List<string>>();

            void StrongConnect(string v)
            {
                indices[v] = index;
                lowlinks[v] = index;
                index++;
                stack.Push(v);
                onStack.Add(v);

                if (adj.TryGetValue(v, out var neighbors))
                {
                    foreach (var w in neighbors)
                    {
                        if (!indices.ContainsKey(w))
                        {
                            StrongConnect(w);
                            lowlinks[v] = Math.Min(lowlinks[v], lowlinks[w]);
                        }
                        else if (onStack.Contains(w))
                        {
                            lowlinks[v] = Math.Min(lowlinks[v], indices[w]);
                        }
                    }
                }

                if (lowlinks[v] == indices[v])
                {
                    var scc = new List<string>();
                    string w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        scc.Add(w);
                    } while (w != v);
                    sccs.Add(scc);
                }
            }

            foreach (var n in targetNodes)
            {
                if (!indices.ContainsKey(n.Id))
                {
                    StrongConnect(n.Id);
                }
            }

            var cyclicComponents = new List<GraphCycleComponentInfo>();
            int nodesInCycles = 0;
            int longestCycle = 0;
            int cycleIdCounter = 1;

            foreach (var scc in sccs)
            {
                bool isSelfLoop = scc.Count == 1 && selfLoops.Contains(scc[0]);
                bool isCycle = scc.Count > 1 || isSelfLoop;

                if (isCycle)
                {
                    nodesInCycles += scc.Count;
                    if (scc.Count > longestCycle) longestCycle = scc.Count;

                    var titles = scc.Select(id => nodeMap.TryGetValue(id, out var node) ? node.Title : id).ToList();
                    string desc = isSelfLoop
                        ? $"Self-loop feedback on node '{titles[0]}'"
                        : $"{scc.Count}-node directed cycle: {string.Join(" ➔ ", titles)} ➔ {titles[0]}";

                    cyclicComponents.Add(new GraphCycleComponentInfo(
                        $"SCC-{cycleIdCounter++}",
                        scc.Count,
                        isSelfLoop,
                        scc,
                        titles,
                        desc));
                }
            }

            int totalNodes = targetNodes.Count;
            int totalEdges = targetLinks.Count;
            int totalSccs = sccs.Count;
            int cyclicCount = cyclicComponents.Count;
            bool isPureDag = cyclicCount == 0;

            double penalty = isPureDag ? 0.0 : ((double)nodesInCycles / totalNodes * 70.0) + (cyclicCount * 10.0);
            double acyclicScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

            string category;
            if (isPureDag)
                category = "Pure Directed Acyclic Graph (DAG)";
            else if (cyclicCount == 1 && longestCycle <= 3)
                category = "Controlled Feedback Loop (1 Small Cycle)";
            else
                category = $"Cyclic Graph Topology ({cyclicCount} Feedback Loops Detected)";

            string summary = FormatCycleSummary(
                totalNodes, totalEdges, totalSccs, cyclicCount, nodesInCycles, longestCycle,
                acyclicScore, category, selectionOnly);

            return new GraphCycleAndFeedbackAudit(
                totalNodes,
                totalEdges,
                totalSccs,
                cyclicCount,
                nodesInCycles,
                longestCycle,
                isPureDag,
                acyclicScore,
                cyclicComponents,
                category,
                selectionOnly,
                summary);
        }

        public static GraphCycleAndFeedbackAudit AuditGraphCyclesAndFeedbackLoops(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditGraphCyclesAndFeedbackLoops();
        }

        public static string FormatCycleSummary(
            int totalNodes, int totalEdges, int totalSccs, int cyclicCount, int nodesInCycles, int longestCycle,
            double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph cycle & feedback loop audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            if (cyclicCount == 0)
            {
                return $"Graph cycle & feedback loop audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% acyclic score): 0 directed cycles (Pure DAG across {totalSccs} components)";
            }

            string cycleLabel = cyclicCount == 1 ? "1 cycle" : $"{cyclicCount} cycles";
            return $"Graph cycle & feedback loop audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% acyclic score): " +
                   $"{cycleLabel} involving {nodesInCycles} node(s) (longest: {longestCycle} nodes)";
        }

        public GraphDiameterAndSmallWorldAudit AuditGraphDiameterAndSmallWorld(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && HasSelection;
            var targetNodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

            var targetLinks = Links.Where(l =>
                targetNodeIds.Contains(l.SourceNodeId) && targetNodeIds.Contains(l.TargetNodeId)).ToList();

            if (targetNodes.Count == 0)
            {
                string scope = isSelection ? "Selection · " : "";
                return new GraphDiameterAndSmallWorldAudit(
                    0, 0, 0, 0, 0.0, 0.0, 0, 100.0,
                    Array.Empty<NodePathEccentricityInfo>(),
                    "Empty Map",
                    isSelection,
                    $"Graph diameter & small-world navigation audit ({scope}0 nodes): Empty map");
            }

            int n = targetNodes.Count;
            int totalEdges = targetLinks.Count;

            var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in targetNodes)
            {
                adj[node.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            foreach (var link in targetLinks)
            {
                if (adj.ContainsKey(link.SourceNodeId) && adj.ContainsKey(link.TargetNodeId))
                {
                    adj[link.SourceNodeId].Add(link.TargetNodeId);
                    adj[link.TargetNodeId].Add(link.SourceNodeId);
                }
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int componentCount = 0;
            foreach (var node in targetNodes)
            {
                if (!visited.Contains(node.Id))
                {
                    componentCount++;
                    var q = new Queue<string>();
                    q.Enqueue(node.Id);
                    visited.Add(node.Id);
                    while (q.Count > 0)
                    {
                        var curr = q.Dequeue();
                        foreach (var neighbor in adj[curr])
                        {
                            if (visited.Add(neighbor))
                            {
                                q.Enqueue(neighbor);
                            }
                        }
                    }
                }
            }

            var nodeMetrics = new List<NodePathEccentricityInfo>();
            int diameter = 0;
            long totalDistanceSum = 0;
            int connectedPairsCount = 0;
            double globalEfficiencySum = 0.0;

            foreach (var source in targetNodes)
            {
                var dist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var q = new Queue<string>();
                dist[source.Id] = 0;
                q.Enqueue(source.Id);

                int eccentricity = 0;
                long localDistSum = 0;
                int reachableCount = 0;

                while (q.Count > 0)
                {
                    var u = q.Dequeue();
                    int d = dist[u];

                    foreach (var v in adj[u])
                    {
                        if (!dist.ContainsKey(v))
                        {
                            dist[v] = d + 1;
                            q.Enqueue(v);

                            int newDist = d + 1;
                            if (newDist > eccentricity) eccentricity = newDist;
                            localDistSum += newDist;
                            reachableCount++;

                            if (string.Compare(source.Id, v, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                totalDistanceSum += newDist;
                                connectedPairsCount++;
                                if (newDist > diameter) diameter = newDist;
                            }

                            globalEfficiencySum += 1.0 / newDist;
                        }
                    }
                }

                double avgDistToOthers = reachableCount > 0
                    ? Math.Round((double)localDistSum / reachableCount, 2)
                    : 0.0;

                nodeMetrics.Add(new NodePathEccentricityInfo(
                    source.Id,
                    source.Title ?? "Untitled Node",
                    eccentricity,
                    avgDistToOthers,
                    reachableCount));
            }

            double characteristicPathLength = connectedPairsCount > 0
                ? Math.Round((double)totalDistanceSum / connectedPairsCount, 2)
                : 0.0;

            double maxPossiblePairs = n > 1 ? (double)n * (n - 1) : 1.0;
            double globalEfficiency = Math.Round((globalEfficiencySum / maxPossiblePairs), 3);

            double smallWorldScore;
            if (n <= 1)
            {
                smallWorldScore = 100.0;
            }
            else if (connectedPairsCount == 0)
            {
                smallWorldScore = 20.0;
            }
            else
            {
                double expectedL = Math.Max(1.0, Math.Log(n));
                double ratio = characteristicPathLength / expectedL;
                double efficiencyPenalty = (1.0 - globalEfficiency) * 40.0;
                double fragmentationPenalty = (componentCount - 1) * 10.0;
                smallWorldScore = Math.Round(Math.Clamp(100.0 - (ratio * 15.0) - efficiencyPenalty - fragmentationPenalty, 10.0, 100.0), 1);
            }

            string category;
            if (n <= 1)
                category = "Trivial Graph";
            else if (componentCount > 1 && connectedPairsCount == 0)
                category = "Completely Disconnected Forest (0 Reachable Paths)";
            else if (componentCount > 1)
                category = $"Partitioned Graph ({componentCount} Components · Moderate Small-World Reachability)";
            else if (diameter <= 4 && characteristicPathLength <= 2.5)
                category = "High Small-World Navigation Efficiency (Tight Semantic Mesh)";
            else if (diameter <= 6)
                category = "Balanced Small-World Navigability (Moderate Diameter)";
            else
                category = $"Elongated Linear Chain (High Diameter {diameter} · High Navigation Latency)";

            string summary = FormatSmallWorldSummary(
                n, totalEdges, connectedPairsCount, diameter, characteristicPathLength,
                globalEfficiency, componentCount, smallWorldScore, category, isSelection);

            return new GraphDiameterAndSmallWorldAudit(
                n,
                totalEdges,
                connectedPairsCount,
                diameter,
                characteristicPathLength,
                globalEfficiency,
                componentCount,
                smallWorldScore,
                nodeMetrics,
                category,
                isSelection,
                summary);
        }

        public static GraphDiameterAndSmallWorldAudit AuditGraphDiameterAndSmallWorld(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditGraphDiameterAndSmallWorld();
        }

        public static string FormatSmallWorldSummary(
            int totalNodes, int totalEdges, int connectedPairs, int diameter, double avgPathLength,
            double globalEfficiency, int componentCount, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph diameter & small-world navigation audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            if (connectedPairs == 0)
            {
                return $"Graph diameter & small-world navigation audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% score): " +
                       $"0 reachable node pairs across {componentCount} disconnected components";
            }

            string compNote = componentCount > 1 ? $" across {componentCount} components" : "";
            return $"Graph diameter & small-world navigation audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% score): " +
                   $"diameter {diameter}, avg path {avgPathLength:0.##}, efficiency {globalEfficiency:0.###}{compNote}";
        }

        public GraphDegreeDistributionAudit AuditDegreeDistributionAndHubs(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && HasSelection;
            var targetNodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

            var targetLinks = Links.Where(l =>
                targetNodeIds.Contains(l.SourceNodeId) && targetNodeIds.Contains(l.TargetNodeId)).ToList();

            int totalNodes = targetNodes.Count;
            int totalEdges = targetLinks.Count;

            if (totalNodes == 0)
            {
                string scope = isSelection ? "Selection · " : "";
                return new GraphDegreeDistributionAudit(
                    0, 0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0, 100.0,
                    Array.Empty<NodeDegreeDistributionInfo>(),
                    "Empty Map",
                    isSelection,
                    $"Graph degree distribution & hub centrality audit ({scope}0 nodes): Empty map");
            }

            var inDegrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var outDegrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes)
            {
                inDegrees[n.Id] = 0;
                outDegrees[n.Id] = 0;
            }

            foreach (var link in targetLinks)
            {
                if (outDegrees.ContainsKey(link.SourceNodeId)) outDegrees[link.SourceNodeId]++;
                if (inDegrees.ContainsKey(link.TargetNodeId)) inDegrees[link.TargetNodeId]++;
            }

            var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes)
            {
                degrees[n.Id] = inDegrees[n.Id] + outDegrees[n.Id];
            }

            double meanDegree = degrees.Values.Average();
            double variance = degrees.Values.Select(d => (d - meanDegree) * (d - meanDegree)).Average();
            double stdDev = Math.Sqrt(variance);

            double skewness = 0.0;
            if (stdDev > 0.0001 && totalNodes >= 3)
            {
                double m3 = degrees.Values.Select(d => Math.Pow(d - meanDegree, 3)).Average();
                skewness = Math.Round(m3 / Math.Pow(stdDev, 3), 2);
            }

            int maxDegree = degrees.Values.Count > 0 ? degrees.Values.Max() : 0;
            double superHubThreshold = meanDegree + 2.0 * stdDev;

            var nodeMetrics = new List<NodeDegreeDistributionInfo>();
            int superHubCount = 0;
            int leafCount = 0;
            int isolatedCount = 0;

            foreach (var n in targetNodes)
            {
                int deg = degrees[n.Id];
                int inDeg = inDegrees[n.Id];
                int outDeg = outDegrees[n.Id];

                bool isSuperHub = stdDev > 0.5 && deg >= 3 && deg > superHubThreshold;
                bool isLeaf = deg == 1;
                bool isIsolated = deg == 0;

                if (isSuperHub) superHubCount++;
                if (isLeaf) leafCount++;
                if (isIsolated) isolatedCount++;

                string role = isIsolated ? "Isolated Node"
                    : isSuperHub ? "Super-Hub Node"
                    : deg >= Math.Max(2, (int)Math.Ceiling(meanDegree)) ? "Core Hub"
                    : isLeaf ? "Leaf Node"
                    : "Intermediate Node";

                nodeMetrics.Add(new NodeDegreeDistributionInfo(
                    n.Id, n.Title, deg, inDeg, outDeg, isSuperHub, isLeaf, isIsolated, role));
            }

            double score = 100.0;
            score -= isolatedCount * 15.0;
            if (leafCount > totalNodes * 0.7 && totalNodes >= 4) score -= 15.0;
            if (maxDegree == 0 && totalNodes > 1) score -= 25.0;
            score = Math.Round(Math.Clamp(score, 10.0, 100.0), 1);

            string category;
            if (totalNodes <= 2)
            {
                category = "Trivial Graph";
            }
            else if (isolatedCount > 0)
            {
                category = $"Fragmented Graph ({isolatedCount} Isolated {(isolatedCount == 1 ? "Node" : "Nodes")})";
            }
            else if (skewness >= 1.0)
            {
                category = "Scale-Free Heavy-Tailed Hub Distribution";
            }
            else if (skewness <= -0.5)
            {
                category = "Uniform / Regular Lattice Topology";
            }
            else if (leafCount > totalNodes * 0.6)
            {
                category = $"High Leaf Node Sprawl ({leafCount} Leaves)";
            }
            else
            {
                category = "Balanced Power-Law Semantic Network";
            }

            string summary = FormatDegreeDistributionSummary(
                totalNodes, totalEdges, meanDegree, stdDev, skewness, maxDegree, superHubCount,
                leafCount, isolatedCount, score, category, isSelection);

            return new GraphDegreeDistributionAudit(
                totalNodes, totalEdges, Math.Round(meanDegree, 2), Math.Round(variance, 2),
                Math.Round(stdDev, 2), skewness, maxDegree, superHubCount, leafCount,
                isolatedCount, score, nodeMetrics, category, isSelection, summary);
        }

        public static GraphDegreeDistributionAudit AuditDegreeDistributionAndHubs(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditDegreeDistributionAndHubs();
        }

        public GraphDegreeDistributionAudit AuditGraphDegreeDistributionAndPowerLaw(bool selectionOnly = false)
            => AuditDegreeDistributionAndHubs(selectionOnly);

        public static GraphDegreeDistributionAudit AuditGraphDegreeDistributionAndPowerLaw(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
            => AuditDegreeDistributionAndHubs(nodes, links);

        public static string FormatDegreeDistributionSummary(
            int totalNodes, int totalEdges, double meanDegree, double stdDev, double skewness,
            int maxDegree, int superHubs, int leafCount, int isolatedCount, double score,
            string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph degree distribution & hub centrality audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (superHubs > 0) alerts.Add($"{superHubs} {(superHubs == 1 ? "super-hub" : "super-hubs")}");
            if (leafCount > 0) alerts.Add($"{leafCount} {(leafCount == 1 ? "leaf" : "leaves")}");
            if (isolatedCount > 0) alerts.Add($"⚠ {isolatedCount} isolated");
            string alertSuffix = alerts.Count > 0 ? $" [{string.Join(", ", alerts)}]" : "";

            return $"Graph degree distribution & hub centrality audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% score): " +
                   $"mean degree {meanDegree:0.##} (±{stdDev:0.##}), skewness {skewness:0.##}, max {maxDegree}{alertSuffix}";
        }

        public GraphArticulationPointsAudit AuditArticulationPointsAndBiconnectivity(bool selectionOnly = false)
        {
            var targetNodes = (selectionOnly && HasSelection)
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && HasSelection;
            var targetNodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

            var targetLinks = Links.Where(l =>
                targetNodeIds.Contains(l.SourceNodeId) && targetNodeIds.Contains(l.TargetNodeId)).ToList();

            string scope = isSelection ? "Selection · " : "";
            if (targetNodes.Count == 0)
            {
                return new GraphArticulationPointsAudit(
                    0, 0, 0, 0, 0, 0, 100.0,
                    Array.Empty<GraphCutVertexInfo>(),
                    Array.Empty<GraphBridgeInfo>(),
                    "Empty Map",
                    isSelection,
                    $"Graph biconnectivity & articulation points audit ({scope}0 nodes): Empty map");
            }

            var nodeMap = targetNodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

            // Build undirected multigraph with unique edge identifiers
            var adj = new Dictionary<string, List<(string targetId, int edgeIndex)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in targetNodes) adj[n.Id] = new List<(string, int)>();

            for (int i = 0; i < targetLinks.Count; i++)
            {
                var l = targetLinks[i];
                if (string.Equals(l.SourceNodeId, l.TargetNodeId, StringComparison.OrdinalIgnoreCase)) continue; // ignore self-loops
                if (adj.ContainsKey(l.SourceNodeId) && adj.ContainsKey(l.TargetNodeId))
                {
                    adj[l.SourceNodeId].Add((l.TargetNodeId, i));
                    adj[l.TargetNodeId].Add((l.SourceNodeId, i));
                }
            }

            int timer = 0;
            var discovery = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cutVertexSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cutBranches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bridgeList = new List<GraphBridgeInfo>();
            int connectedComponents = 0;
            int biconnectedBlockCount = 0;
            var edgeStack = new Stack<int>();

            void Dfs(string u, int parentEdgeIndex)
            {
                discovery[u] = low[u] = ++timer;
                int children = 0;

                foreach (var (v, edgeIndex) in adj[u])
                {
                    if (edgeIndex == parentEdgeIndex) continue;

                    if (discovery.ContainsKey(v))
                    {
                        // Back edge
                        low[u] = Math.Min(low[u], discovery[v]);
                        if (discovery[v] < discovery[u])
                        {
                            edgeStack.Push(edgeIndex);
                        }
                    }
                    else
                    {
                        // Tree edge
                        children++;
                        edgeStack.Push(edgeIndex);
                        Dfs(v, edgeIndex);
                        low[u] = Math.Min(low[u], low[v]);

                        // Check articulation point condition for non-root
                        if (parentEdgeIndex != -1 && low[v] >= discovery[u])
                        {
                            cutVertexSet.Add(u);
                            cutBranches[u] = cutBranches.GetValueOrDefault(u) + 1;
                        }

                        // Check bridge condition
                        if (low[v] > discovery[u])
                        {
                            string srcTitle = nodeMap.TryGetValue(u, out var sn) ? (sn.Title ?? u) : u;
                            string tgtTitle = nodeMap.TryGetValue(v, out var tn) ? (tn.Title ?? v) : v;
                            bridgeList.Add(new GraphBridgeInfo(u, srcTitle, v, tgtTitle, "⚠ Critical Bridge: Edge removal partitions graph"));
                        }

                        // Biconnected component block pop
                        if (low[v] >= discovery[u])
                        {
                            biconnectedBlockCount++;
                            while (edgeStack.Count > 0)
                            {
                                int popped = edgeStack.Pop();
                                if (popped == edgeIndex) break;
                            }
                        }
                    }
                }

                // Check articulation point condition for root
                if (parentEdgeIndex == -1 && children >= 2)
                {
                    cutVertexSet.Add(u);
                    cutBranches[u] = children;
                }
            }

            foreach (var n in targetNodes)
            {
                if (!discovery.ContainsKey(n.Id))
                {
                    connectedComponents++;
                    Dfs(n.Id, -1);
                    if (edgeStack.Count > 0)
                    {
                        biconnectedBlockCount++;
                        edgeStack.Clear();
                    }
                }
            }

            var cutVertexInfos = cutVertexSet.Select(id =>
            {
                var n = nodeMap[id];
                int deg = adj.TryGetValue(id, out var list) ? list.Count : 0;
                int branches = cutBranches.GetValueOrDefault(id, 1);
                string role = branches >= 3 ? "Major Articulation Hub" : "Articulation Cut Vertex";
                return new GraphCutVertexInfo(id, n.Title ?? id, deg, branches, role);
            }).OrderByDescending(c => c.SubgraphBranches).ThenByDescending(c => c.Degree).ToList();

            int nNodes = targetNodes.Count;
            int nEdges = targetLinks.Count;
            double cutRatio = nNodes > 2 ? (double)cutVertexSet.Count / (nNodes - 2) : 0.0;
            double bridgeRatio = nEdges > 0 ? (double)bridgeList.Count / nEdges : 0.0;

            double rawScore;
            if (nNodes <= 2)
            {
                rawScore = 100.0;
            }
            else
            {
                rawScore = 100.0 * (1.0 - 0.45 * cutRatio - 0.55 * bridgeRatio);
            }
            double faultToleranceScore = Math.Max(0.0, Math.Min(100.0, Math.Round(rawScore, 1)));

            string category = (cutVertexSet.Count == 0 && bridgeList.Count == 0)
                ? "2-Connected Fault Tolerant (No Single Points of Failure)"
                : faultToleranceScore >= 75.0
                    ? "Resilient Topology (Few Bridges)"
                    : faultToleranceScore >= 45.0
                        ? "Moderate Fragility (Noticeable Cut Vertices)"
                        : "High Topological Fragility (Tree / Chain Dominated)";

            string summary = FormatArticulationPointsSummary(
                nNodes, nEdges, connectedComponents, cutVertexSet.Count, bridgeList.Count,
                biconnectedBlockCount, faultToleranceScore, category, isSelection);

            return new GraphArticulationPointsAudit(
                nNodes, nEdges, connectedComponents, cutVertexSet.Count, bridgeList.Count,
                biconnectedBlockCount, faultToleranceScore, cutVertexInfos, bridgeList,
                category, isSelection, summary);
        }

        public static GraphArticulationPointsAudit AuditArticulationPointsAndBiconnectivity(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditArticulationPointsAndBiconnectivity();
        }

        public static string FormatArticulationPointsSummary(
            int totalNodes, int totalEdges, int components, int cutVertices, int bridges,
            int biconnectedBlocks, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph biconnectivity & articulation points audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (cutVertices > 0) alerts.Add($"⚠ {cutVertices} cut {(cutVertices == 1 ? "vertex" : "vertices")}");
            if (bridges > 0) alerts.Add($"⚠ {bridges} {(bridges == 1 ? "bridge" : "bridges")}");
            if (components > 1) alerts.Add($"{components} components");
            if (biconnectedBlocks > 0) alerts.Add($"{biconnectedBlocks} 2-connected {(biconnectedBlocks == 1 ? "block" : "blocks")}");

            string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% biconnected, 0 single points of failure";
            return $"Graph biconnectivity & articulation points audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% fault tolerance): {cutVertices} cut vertices, {bridges} bridges{details}";
        }

        public GraphClosenessCentralityAudit AuditClosenessAndHarmonicCentrality(bool selectionOnly = false)
        {
            var targetNodes = selectionOnly && HasSelection
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && HasSelection;
            var targetNodeIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

            var targetLinks = Links.Where(l =>
                targetNodeIds.Contains(l.SourceNodeId) && targetNodeIds.Contains(l.TargetNodeId)).ToList();

            string scope = isSelection ? "Selection · " : "";
            if (targetNodes.Count == 0)
            {
                return new GraphClosenessCentralityAudit(
                    0, 0, 0, 0.0, 0.0, 100.0,
                    "", "", 0.0,
                    Array.Empty<NodeCentralityMetricInfo>(),
                    "Empty Map",
                    isSelection,
                    $"Graph closeness & harmonic centrality audit ({scope}0 nodes): Empty map");
            }

            int n = targetNodes.Count;
            int totalEdges = targetLinks.Count;

            var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in targetNodes) adj[node.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in targetLinks)
            {
                if (adj.ContainsKey(link.SourceNodeId) && adj.ContainsKey(link.TargetNodeId) &&
                    !string.Equals(link.SourceNodeId, link.TargetNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    adj[link.SourceNodeId].Add(link.TargetNodeId);
                    adj[link.TargetNodeId].Add(link.SourceNodeId);
                }
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int components = 0;
            foreach (var node in targetNodes)
            {
                if (!visited.Contains(node.Id))
                {
                    components++;
                    var q = new Queue<string>();
                    q.Enqueue(node.Id);
                    visited.Add(node.Id);
                    while (q.Count > 0)
                    {
                        var curr = q.Dequeue();
                        foreach (var neighbor in adj[curr])
                        {
                            if (visited.Add(neighbor))
                            {
                                q.Enqueue(neighbor);
                            }
                        }
                    }
                }
            }

            if (n == 1)
            {
                var single = targetNodes[0];
                var singleMetric = new NodeCentralityMetricInfo(
                    single.Id, single.Title, 0, 0, 1.0, 0.0, 1.0, "Isolated Root / Star Hub");
                return new GraphClosenessCentralityAudit(
                    1, totalEdges, 1, 1.0, 1.0, 100.0,
                    single.Id, single.Title, 1.0,
                    new[] { singleMetric },
                    "Single Node (Trivial Hub)",
                    isSelection,
                    $"Graph closeness & harmonic centrality audit ({scope}1 node, {totalEdges} edges · Single Node · 100% broadcast efficiency): 1 central node [{single.Title}]");
            }

            var nodeMetrics = new List<NodeCentralityMetricInfo>();
            double sumNormalizedHarmonic = 0.0;
            string topHubId = "";
            string topHubTitle = "";
            double topHubScore = -1.0;

            foreach (var source in targetNodes)
            {
                var dist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [source.Id] = 0 };
                var queue = new Queue<string>();
                queue.Enqueue(source.Id);

                while (queue.Count > 0)
                {
                    var u = queue.Dequeue();
                    int d = dist[u];
                    foreach (var v in adj[u])
                    {
                        if (!dist.ContainsKey(v))
                        {
                            dist[v] = d + 1;
                            queue.Enqueue(v);
                        }
                    }
                }

                int reachableCount = 0;
                int totalGeodesicDist = 0;
                double harmonicSum = 0.0;

                foreach (var (targetId, distance) in dist)
                {
                    if (string.Equals(targetId, source.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    reachableCount++;
                    totalGeodesicDist += distance;
                    if (distance > 0)
                    {
                        harmonicSum += 1.0 / distance;
                    }
                }

                double closeness = (reachableCount > 0 && totalGeodesicDist > 0)
                    ? (double)reachableCount / totalGeodesicDist
                    : 0.0;

                double normalizedHarmonic = harmonicSum / (n - 1);
                sumNormalizedHarmonic += normalizedHarmonic;

                string tier = normalizedHarmonic switch
                {
                    >= 0.8 => "Tier 1: Core Broadcast Hub",
                    >= 0.5 => "Tier 2: Major Transit Node",
                    >= 0.25 => "Tier 3: Peripheral Node",
                    > 0.0 => "Tier 4: Remote Leaf Node",
                    _ => "Tier 5: Isolated Node"
                };

                if (normalizedHarmonic > topHubScore)
                {
                    topHubScore = normalizedHarmonic;
                    topHubId = source.Id;
                    topHubTitle = source.Title;
                }

                nodeMetrics.Add(new NodeCentralityMetricInfo(
                    source.Id,
                    source.Title,
                    reachableCount,
                    totalGeodesicDist,
                    Math.Round(closeness, 4),
                    Math.Round(harmonicSum, 4),
                    Math.Round(normalizedHarmonic, 4),
                    tier));
            }

            double avgHarmonic = Math.Round(sumNormalizedHarmonic / n, 4);
            double efficiencyScore = Math.Round(avgHarmonic * 100.0, 1);

            string category = efficiencyScore switch
            {
                >= 80.0 => "Ultra-Efficient Star / Compact Broadcast Network",
                >= 60.0 => "Balanced Modular Navigable Graph",
                >= 40.0 => "Moderate Linear / Chain Topology",
                > 0.0 => "Fragmented / High-Diameter Sparse Graph",
                _ => "Isolated / Disconnected Nodes"
            };

            string summary = FormatClosenessCentralitySummary(
                n, totalEdges, components, avgHarmonic, efficiencyScore,
                topHubTitle, topHubScore, category, isSelection);

            return new GraphClosenessCentralityAudit(
                n, totalEdges, components, avgHarmonic, avgHarmonic, efficiencyScore,
                topHubId, topHubTitle, Math.Round(topHubScore, 4),
                nodeMetrics, category, isSelection, summary);
        }

        public static GraphClosenessCentralityAudit AuditClosenessAndHarmonicCentrality(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null)
            {
                foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            }
            if (links != null)
            {
                foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            }
            return vm.AuditClosenessAndHarmonicCentrality();
        }

        public static string FormatClosenessCentralitySummary(
            int totalNodes, int totalEdges, int components, double avgHarmonic, double score,
            string topHubTitle, double topHubScore, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph closeness & harmonic centrality audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (components > 1) alerts.Add($"⚠ {components} disconnected components");
            if (!string.IsNullOrEmpty(topHubTitle)) alerts.Add($"top hub: \"{topHubTitle}\" ({topHubScore:0.##})");

            string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : "";
            return $"Graph closeness & harmonic centrality audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% broadcast efficiency): avg harmonic {avgHarmonic:0.###}{details}";
        }

        public GraphBetweennessCentralityAudit AuditBetweennessCentralityAndGateways(bool selectionOnly = false)
        {
            var targetNodes = selectionOnly && HasSelection
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            bool isSelection = selectionOnly && HasSelection;
            var targetNodeIds = new HashSet<string>(targetNodes.Select(n => n.Id));
            var targetLinks = Links.Where(l => targetNodeIds.Contains(l.SourceNodeId) && targetNodeIds.Contains(l.TargetNodeId)).ToList();

            int n = targetNodes.Count;
            int totalEdges = targetLinks.Count;

            if (n == 0)
            {
                return new GraphBetweennessCentralityAudit(
                    0, 0, 0, 0, 0.0, 0.0, 100.0,
                    string.Empty, string.Empty, 0.0,
                    Array.Empty<NodeBetweennessMetricInfo>(), "Empty",
                    isSelection, FormatBetweennessCentralitySummary(0, 0, 0, 0, 0.0, 100.0, string.Empty, 0.0, "Empty", isSelection));
            }

            var adj = new Dictionary<string, List<string>>();
            foreach (var node in targetNodes) adj[node.Id] = new List<string>();
            foreach (var link in targetLinks)
            {
                if (adj.ContainsKey(link.SourceNodeId) && adj.ContainsKey(link.TargetNodeId))
                {
                    if (!adj[link.SourceNodeId].Contains(link.TargetNodeId)) adj[link.SourceNodeId].Add(link.TargetNodeId);
                    if (!adj[link.TargetNodeId].Contains(link.SourceNodeId)) adj[link.TargetNodeId].Add(link.SourceNodeId);
                }
            }

            var cb = new Dictionary<string, double>();
            foreach (var node in targetNodes) cb[node.Id] = 0.0;

            foreach (var s in targetNodes)
            {
                var stack = new Stack<string>();
                var pred = new Dictionary<string, List<string>>();
                var sigma = new Dictionary<string, double>();
                var dist = new Dictionary<string, int>();

                foreach (var node in targetNodes)
                {
                    pred[node.Id] = new List<string>();
                    sigma[node.Id] = 0.0;
                    dist[node.Id] = -1;
                }

                sigma[s.Id] = 1.0;
                dist[s.Id] = 0;

                var queue = new Queue<string>();
                queue.Enqueue(s.Id);

                while (queue.Count > 0)
                {
                    var v = queue.Dequeue();
                    stack.Push(v);

                    foreach (var w in adj[v])
                    {
                        if (dist[w] < 0)
                        {
                            dist[w] = dist[v] + 1;
                            queue.Enqueue(w);
                        }
                        if (dist[w] == dist[v] + 1)
                        {
                            sigma[w] += sigma[v];
                            pred[w].Add(v);
                        }
                    }
                }

                var delta = new Dictionary<string, double>();
                foreach (var node in targetNodes) delta[node.Id] = 0.0;

                while (stack.Count > 0)
                {
                    var w = stack.Pop();
                    foreach (var v in pred[w])
                    {
                        if (sigma[w] > 0)
                        {
                            delta[v] += (sigma[v] / sigma[w]) * (1.0 + delta[w]);
                        }
                    }
                    if (w != s.Id)
                    {
                        cb[w] += delta[w];
                    }
                }
            }

            foreach (var k in targetNodes.Select(node => node.Id))
            {
                cb[k] /= 2.0;
            }

            double normFactor = (n > 2) ? 2.0 / ((double)(n - 1) * (n - 2)) : 1.0;
            double maxBetweenness = cb.Values.Count > 0 ? cb.Values.Max() : 0.0;
            double avgBetweenness = cb.Values.Count > 0 ? cb.Values.Average() : 0.0;

            int components = 0;
            var visited = new HashSet<string>();
            foreach (var node in targetNodes)
            {
                if (!visited.Contains(node.Id))
                {
                    components++;
                    var q = new Queue<string>();
                    q.Enqueue(node.Id);
                    visited.Add(node.Id);
                    while (q.Count > 0)
                    {
                        var curr = q.Dequeue();
                        foreach (var nbr in adj[curr])
                        {
                            if (!visited.Contains(nbr))
                            {
                                visited.Add(nbr);
                                q.Enqueue(nbr);
                            }
                        }
                    }
                }
            }

            int gatewayCount = 0;
            var nodeMetrics = new List<NodeBetweennessMetricInfo>();
            string topBottleneckId = "";
            string topBottleneckTitle = "";
            double topBottleneckScore = -1.0;

            foreach (var node in targetNodes)
            {
                double raw = cb[node.Id];
                double norm = n > 2 ? raw * normFactor : (raw > 0 ? 1.0 : 0.0);
                norm = Math.Round(Math.Clamp(norm, 0.0, 1.0), 4);
                bool isGateway = (n >= 4 && (norm >= 0.20 || (avgBetweenness > 0 && raw >= 2.0 * avgBetweenness)));
                if (isGateway) gatewayCount++;

                if (raw > topBottleneckScore)
                {
                    topBottleneckScore = raw;
                    topBottleneckId = node.Id;
                    topBottleneckTitle = node.Title;
                }

                string tier = norm switch
                {
                    >= 0.40 => "Critical Bottleneck",
                    >= 0.20 => "Gateway Bridge",
                    >= 0.05 => "Transit Node",
                    _ => "Peripheral / Leaf"
                };

                nodeMetrics.Add(new NodeBetweennessMetricInfo(
                    node.Id, node.Title, adj[node.Id].Count, Math.Round(raw, 2), norm, isGateway, tier));
            }

            double decentralization = 100.0;
            if (n > 2)
            {
                double maxNorm = cb.Values.Max() * normFactor;
                double sumDiff = targetNodes.Sum(node => maxNorm - (cb[node.Id] * normFactor));
                double freemanCentralization = sumDiff / (n - 1);
                decentralization = Math.Clamp(100.0 * (1.0 - freemanCentralization), 0.0, 100.0);
            }
            decentralization = Math.Round(decentralization, 1);

            string category = decentralization switch
            {
                >= 85.0 => "Highly Decentralized",
                >= 70.0 => "Balanced Mesh",
                >= 50.0 => "Moderate Hierarchy",
                >= 30.0 => "Centralized Bottleneck",
                _ => "Star Topology Bottleneck"
            };

            string summary = FormatBetweennessCentralitySummary(
                n, totalEdges, components, gatewayCount, avgBetweenness, decentralization,
                topBottleneckTitle, topBottleneckScore, category, isSelection);

            return new GraphBetweennessCentralityAudit(
                n, totalEdges, components, gatewayCount, Math.Round(maxBetweenness, 2),
                Math.Round(avgBetweenness, 2), decentralization, topBottleneckId, topBottleneckTitle,
                Math.Round(topBottleneckScore, 2), nodeMetrics, category, isSelection, summary);
        }

        public static GraphBetweennessCentralityAudit AuditBetweennessCentralityAndGateways(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditBetweennessCentralityAndGateways();
        }

        public static string FormatBetweennessCentralitySummary(
            int totalNodes, int totalEdges, int components, int gatewayCount, double avgBetweenness,
            double score, string topBottleneckTitle, double topScore, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph betweenness & gateway audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (gatewayCount > 0) alerts.Add($"⚠ {gatewayCount} gateway {(gatewayCount == 1 ? "bottleneck" : "bottlenecks")}");
            if (!string.IsNullOrEmpty(topBottleneckTitle)) alerts.Add($"top bridge: \"{topBottleneckTitle}\" ({topScore:0.#})");

            string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · distributed traffic";
            return $"Graph betweenness & gateway audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% decentralization): avg betweenness {avgBetweenness:0.##}{details}";
        }

        /// <summary>
        /// Audits mind map graph node degree distribution, hub dominance, leaf connectivity, and scale-free topology balance.
        /// </summary>
        public GraphScaleFreeTopologyAudit AuditDegreeDistributionAndHubDominance(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0) ? selected : Nodes.ToList();
            bool isSelection = selectionOnly && selected.Count > 0;
            string scope = isSelection ? "Selection · " : "";

            if (targetNodes.Count == 0)
            {
                return new GraphScaleFreeTopologyAudit(
                    0, 0, 0.0, 0, 0, 0, 0, 0.0, 100.0, "", "",
                    Array.Empty<NodeDegreeMetricInfo>(),
                    "Empty Map", isSelection,
                    $"Graph degree distribution & hub dominance audit ({scope}0 nodes): Empty map");
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id));
            var targetLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            var inDegrees = new Dictionary<string, int>();
            var outDegrees = new Dictionary<string, int>();
            foreach (var nd in targetNodes)
            {
                inDegrees[nd.Id] = 0;
                outDegrees[nd.Id] = 0;
            }

            foreach (var link in targetLinks)
            {
                if (outDegrees.ContainsKey(link.SourceNodeId)) outDegrees[link.SourceNodeId]++;
                if (inDegrees.ContainsKey(link.TargetNodeId)) inDegrees[link.TargetNodeId]++;
            }

            int n = targetNodes.Count;
            int totalEdges = targetLinks.Count;
            int maxDegree = 0;
            int isolatedCount = 0;
            int leafCount = 0;
            int hubCount = 0;
            double avgDegree = n > 0 ? (double)(totalEdges * 2) / n : 0.0;
            avgDegree = Math.Round(avgDegree, 2);

            int hubThreshold = Math.Max(3, (int)Math.Ceiling(avgDegree * 1.5));

            string topHubId = "";
            string topHubTitle = "";

            var nodeMetrics = new List<NodeDegreeMetricInfo>();

            foreach (var node in targetNodes)
            {
                int inD = inDegrees[node.Id];
                int outD = outDegrees[node.Id];
                int totD = inD + outD;

                if (totD > maxDegree)
                {
                    maxDegree = totD;
                    topHubId = node.Id;
                    topHubTitle = string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title;
                }

                if (totD == 0) isolatedCount++;
                else if (totD == 1) leafCount++;
                else if (totD >= hubThreshold) hubCount++;

                string role = totD == 0 ? "Isolated Node" :
                              totD == 1 ? "Leaf Node" :
                              totD >= hubThreshold ? "Dominant Hub" : "Connector Node";

                bool isDominantHub = totD >= hubThreshold;

                nodeMetrics.Add(new NodeDegreeMetricInfo(
                    node.Id,
                    string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title,
                    inD, outD, totD, isDominantHub, role));
            }

            double hubDominanceRatio = totalEdges > 0
                ? Math.Round(Math.Min(1.0, (double)maxDegree / Math.Max(1, totalEdges * 2)), 2)
                : 0.0;

            double balanceScore;
            if (n <= 2)
            {
                balanceScore = 100.0;
            }
            else
            {
                double starPenalty = hubDominanceRatio > 0.35 ? (hubDominanceRatio - 0.35) * 50.0 : 0.0;
                double isolatePenalty = (double)isolatedCount / n * 30.0;
                balanceScore = Math.Clamp(100.0 - starPenalty - isolatePenalty, 0.0, 100.0);
                balanceScore = Math.Round(balanceScore, 1);
            }

            string category = balanceScore switch
            {
                >= 85.0 => "Balanced Topology",
                >= 70.0 => "Moderately Distributed",
                >= 50.0 => "Hierarchical Tree",
                >= 30.0 => "Hub-Centric",
                _ => "Extreme Star Topology"
            };

            string summary = FormatDegreeDistributionSummary(
                n, totalEdges, avgDegree, maxDegree, hubCount, leafCount, isolatedCount,
                hubDominanceRatio, balanceScore, topHubTitle, category, isSelection);

            return new GraphScaleFreeTopologyAudit(
                n, totalEdges, avgDegree, maxDegree, hubCount, leafCount, isolatedCount,
                hubDominanceRatio, balanceScore, topHubId, topHubTitle, nodeMetrics,
                category, isSelection, summary);
        }

        public static GraphScaleFreeTopologyAudit AuditDegreeDistributionAndHubDominance(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditDegreeDistributionAndHubDominance();
        }

        public static string FormatDegreeDistributionSummary(
            int totalNodes, int totalEdges, double avgDegree, int maxDegree, int hubCount,
            int leafCount, int isolatedCount, double hubRatio, double balanceScore,
            string topHubTitle, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph degree distribution & hub dominance audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (hubCount > 0) alerts.Add($"{hubCount} {(hubCount == 1 ? "hub" : "hubs")}");
            if (leafCount > 0) alerts.Add($"{leafCount} {(leafCount == 1 ? "leaf" : "leaves")}");
            if (isolatedCount > 0) alerts.Add($"⚠ {isolatedCount} isolated");
            if (!string.IsNullOrEmpty(topHubTitle)) alerts.Add($"top hub: \"{topHubTitle}\" (deg {maxDegree})");

            string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · uniform degrees";
            return $"Graph degree distribution & hub dominance audit ({scope}{nodeStr}, {edgeStr} · {category} · {balanceScore:0.#}% balance): avg deg {avgDegree:0.##}{details}";
        }

        public GraphTopologicalHierarchyAudit AuditTopologicalHierarchyAndCycles(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0) ? selected : Nodes.ToList();
            bool isSelection = selectionOnly && selected.Count > 0;
            string scope = isSelection ? "Selection · " : "";

            if (targetNodes.Count == 0)
            {
                return new GraphTopologicalHierarchyAudit(
                    0, 0, 0, 0, 0, 0, 100.0,
                    Array.Empty<TopologicalStratumNodeInfo>(),
                    "Empty Map", isSelection,
                    $"Graph topological hierarchy & cycle audit ({scope}0 nodes): Empty map");
            }

            var nodeSet = new HashSet<string>(targetNodes.Select(n => n.Id));
            var targetLinks = Links.Where(l => nodeSet.Contains(l.SourceNodeId) && nodeSet.Contains(l.TargetNodeId)).ToList();

            var inDegrees = new Dictionary<string, int>();
            var outDegrees = new Dictionary<string, int>();
            var adj = new Dictionary<string, List<string>>();
            foreach (var nd in targetNodes)
            {
                inDegrees[nd.Id] = 0;
                outDegrees[nd.Id] = 0;
                adj[nd.Id] = new List<string>();
            }

            foreach (var link in targetLinks)
            {
                if (outDegrees.ContainsKey(link.SourceNodeId)) outDegrees[link.SourceNodeId]++;
                if (inDegrees.ContainsKey(link.TargetNodeId)) inDegrees[link.TargetNodeId]++;
                if (adj.ContainsKey(link.SourceNodeId)) adj[link.SourceNodeId].Add(link.TargetNodeId);
            }

            int index = 0;
            var indices = new Dictionary<string, int>();
            var lowlink = new Dictionary<string, int>();
            var onStack = new HashSet<string>();
            var stack = new Stack<string>();
            var sccs = new List<List<string>>();

            void StrongConnect(string v)
            {
                indices[v] = index;
                lowlink[v] = index;
                index++;
                stack.Push(v);
                onStack.Add(v);

                foreach (var w in adj[v])
                {
                    if (!indices.ContainsKey(w))
                    {
                        StrongConnect(w);
                        lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowlink[v] = Math.Min(lowlink[v], indices[w]);
                    }
                }

                if (lowlink[v] == indices[v])
                {
                    var scc = new List<string>();
                    string node;
                    do
                    {
                        node = stack.Pop();
                        onStack.Remove(node);
                        scc.Add(node);
                    } while (node != v);
                    sccs.Add(scc);
                }
            }

            foreach (var nd in targetNodes)
            {
                if (!indices.ContainsKey(nd.Id))
                {
                    StrongConnect(nd.Id);
                }
            }

            var cycleNodeMap = new Dictionary<string, string>();
            int cycleGroupCounter = 1;
            foreach (var scc in sccs)
            {
                bool isCycle = false;
                if (scc.Count > 1)
                {
                    isCycle = true;
                }
                else if (scc.Count == 1)
                {
                    var single = scc[0];
                    if (adj[single].Contains(single))
                    {
                        isCycle = true;
                    }
                }

                if (isCycle)
                {
                    string groupId = $"C{cycleGroupCounter++}";
                    foreach (var cNode in scc)
                    {
                        cycleNodeMap[cNode] = groupId;
                    }
                }
            }

            int cycleNodeCount = cycleNodeMap.Count;
            int cycleCount = cycleGroupCounter - 1;

            var dagNodes = targetNodes.Where(n => !cycleNodeMap.ContainsKey(n.Id)).ToList();
            var dagNodeSet = new HashSet<string>(dagNodes.Select(n => n.Id));
            var dagInDegree = new Dictionary<string, int>();
            var dagAdj = new Dictionary<string, List<string>>();
            var stratumMap = new Dictionary<string, int>();

            foreach (var nd in dagNodes)
            {
                dagInDegree[nd.Id] = 0;
                dagAdj[nd.Id] = new List<string>();
                stratumMap[nd.Id] = 0;
            }

            foreach (var link in targetLinks)
            {
                if (dagNodeSet.Contains(link.SourceNodeId) && dagNodeSet.Contains(link.TargetNodeId))
                {
                    dagAdj[link.SourceNodeId].Add(link.TargetNodeId);
                    dagInDegree[link.TargetNodeId]++;
                }
            }

            var queue = new Queue<string>();
            foreach (var nd in dagNodes)
            {
                if (dagInDegree[nd.Id] == 0)
                {
                    queue.Enqueue(nd.Id);
                }
            }

            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                int currentLayer = stratumMap[u];
                foreach (var v in dagAdj[u])
                {
                    stratumMap[v] = Math.Max(stratumMap[v], currentLayer + 1);
                    dagInDegree[v]--;
                    if (dagInDegree[v] == 0)
                    {
                        queue.Enqueue(v);
                    }
                }
            }

            var layerCounts = new Dictionary<int, int>();
            foreach (var kvp in stratumMap)
            {
                int layer = kvp.Value;
                layerCounts[layer] = layerCounts.GetValueOrDefault(layer, 0) + 1;
            }

            int stratumLayerCount = layerCounts.Count > 0 ? layerCounts.Keys.Max() + 1 : (targetNodes.Count > 0 && cycleNodeCount == 0 ? 1 : 0);
            int maxStratumWidth = layerCounts.Count > 0 ? layerCounts.Values.Max() : 0;

            var items = new List<TopologicalStratumNodeInfo>();
            foreach (var nd in targetNodes)
            {
                bool inCycle = cycleNodeMap.TryGetValue(nd.Id, out var cGroupId);
                int layer = inCycle ? -1 : stratumMap.GetValueOrDefault(nd.Id, 0);
                string title = string.IsNullOrWhiteSpace(nd.Title) ? nd.Id : nd.Title;
                items.Add(new TopologicalStratumNodeInfo(
                    nd.Id,
                    title,
                    layer,
                    inDegrees[nd.Id],
                    outDegrees[nd.Id],
                    inCycle,
                    cGroupId ?? string.Empty));
            }

            int n = targetNodes.Count;
            double acyclicityScore;
            if (cycleNodeCount == 0)
            {
                acyclicityScore = 100.0;
            }
            else
            {
                double cycleRatio = (double)cycleNodeCount / n;
                double penalty = (cycleRatio * 70.0) + (cycleCount * 15.0);
                acyclicityScore = Math.Clamp(100.0 - penalty, 0.0, 100.0);
                acyclicityScore = Math.Round(acyclicityScore, 1);
            }

            string category = acyclicityScore switch
            {
                >= 100.0 => "Pure DAG (Acyclic Layered)",
                >= 80.0 => "Weakly Cyclic (Local Feedback Loops)",
                >= 50.0 => "Moderately Cyclic (Feedback Loops Detected)",
                _ => "Entangled Cyclic Graph"
            };

            string summary = FormatTopologicalHierarchySummary(
                n, targetLinks.Count, stratumLayerCount, maxStratumWidth, cycleNodeCount,
                cycleCount, acyclicityScore, category, isSelection);

            return new GraphTopologicalHierarchyAudit(
                n, targetLinks.Count, stratumLayerCount, maxStratumWidth, cycleNodeCount,
                cycleCount, acyclicityScore, items, category, isSelection, summary);
        }

        public static GraphTopologicalHierarchyAudit AuditTopologicalHierarchyAndCycles(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditTopologicalHierarchyAndCycles();
        }

        public static string FormatTopologicalHierarchySummary(
            int totalNodes, int totalEdges, int stratumCount, int maxWidth, int cycleNodes,
            int cycleCount, double acyclicityScore, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph topological hierarchy & cycle audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (stratumCount > 0) alerts.Add($"{stratumCount} strata (max width {maxWidth})");
            if (cycleCount > 0) alerts.Add($"⚠ {cycleCount} {(cycleCount == 1 ? "cycle" : "cycles")} ({cycleNodes} nodes)");
            else alerts.Add("0 cycles (acyclic)");

            string details = string.Join(" · ", alerts);
            return $"Graph topological hierarchy & cycle audit ({scope}{nodeStr}, {edgeStr} · {category} · {acyclicityScore:0.#}% acyclic): {details}";
        }

        /// <summary>
        /// Audits mind map graph topology for edge crossings and planarity, calculating pairwise edge segment
        /// intersections, identifying acute-angle crossing tangles, computing crossing density, and scoring graph planarity.
        /// Ignores incident edges sharing common source or target nodes.
        /// </summary>
        public GraphEdgeCrossingPlanarityAudit AuditEdgeCrossingsAndPlanarity(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0) ? selected : Nodes.ToList();
            bool isSelection = selectionOnly && selected.Count > 0;
            string scope = isSelection ? "Selection · " : "";

            int n = targetNodes.Count;

            if (n == 0)
            {
                return new GraphEdgeCrossingPlanarityAudit(
                    0, 0, 0, 0, 0, 0.0, 100.0,
                    Array.Empty<EdgeCrossingPairItem>(),
                    "Empty Map",
                    isSelection,
                    $"Graph edge crossing & planarity audit ({scope}0 nodes): Empty map");
            }

            var nodeMap = targetNodes.ToDictionary(
                node => node.Id,
                node => (X: node.X + (node.Width > 0 ? node.Width / 2.0 : 60.0),
                         Y: node.Y + (node.Height > 0 ? node.Height / 2.0 : 20.0),
                         Title: node.Title ?? node.Id));

            var targetLinks = Links.Where(l => !string.IsNullOrEmpty(l.SourceNodeId) &&
                                               !string.IsNullOrEmpty(l.TargetNodeId) &&
                                               nodeMap.ContainsKey(l.SourceNodeId) &&
                                               nodeMap.ContainsKey(l.TargetNodeId) &&
                                               !string.Equals(l.SourceNodeId, l.TargetNodeId, StringComparison.OrdinalIgnoreCase)).ToList();

            int totalEdges = targetLinks.Count;
            if (totalEdges < 2)
            {
                string edgeSummary = FormatEdgeCrossingSummary(n, totalEdges, 0, 0, 0, 0.0, 100.0, "Planar (Zero Crossings)", isSelection);
                return new GraphEdgeCrossingPlanarityAudit(
                    n, totalEdges, 0, 0, 0, 0.0, 100.0,
                    Array.Empty<EdgeCrossingPairItem>(),
                    "Planar (Zero Crossings)",
                    isSelection,
                    edgeSummary);
            }

            var crossings = new List<EdgeCrossingPairItem>();
            int pairsChecked = 0;
            int acuteCount = 0;

            int maxEdgeComparisons = Math.Min(totalEdges, 400); // Guard against runaway O(E^2)

            for (int i = 0; i < maxEdgeComparisons; i++)
            {
                var l1 = targetLinks[i];
                var p1_0 = nodeMap[l1.SourceNodeId];
                var p1_1 = nodeMap[l1.TargetNodeId];

                for (int j = i + 1; j < maxEdgeComparisons; j++)
                {
                    var l2 = targetLinks[j];

                    // Incident edges (sharing endpoints) meet at a common node; do not count as a crossing
                    if (string.Equals(l1.SourceNodeId, l2.SourceNodeId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l1.SourceNodeId, l2.TargetNodeId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l1.TargetNodeId, l2.SourceNodeId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l1.TargetNodeId, l2.TargetNodeId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pairsChecked++;
                    var p2_0 = nodeMap[l2.SourceNodeId];
                    var p2_1 = nodeMap[l2.TargetNodeId];

                    if (CheckSegmentIntersection(
                        p1_0.X, p1_0.Y, p1_1.X, p1_1.Y,
                        p2_0.X, p2_0.Y, p2_1.X, p2_1.Y,
                        out double intX, out double intY, out double angleDeg))
                    {
                        bool isAcute = angleDeg < 25.0;
                        if (isAcute) acuteCount++;

                        crossings.Add(new EdgeCrossingPairItem(
                            p1_0.Title, p1_1.Title,
                            p2_0.Title, p2_1.Title,
                            intX, intY, angleDeg, isAcute));
                    }
                }
            }

            int crossingCount = crossings.Count;
            double density = totalEdges > 0 ? Math.Round((double)crossingCount / totalEdges, 2) : 0.0;
            double penalty = (crossingCount * 12.0) + (acuteCount * 8.0);
            double planarityScore = Math.Clamp(100.0 - penalty, 0.0, 100.0);
            planarityScore = Math.Round(planarityScore, 1);

            string category = crossingCount switch
            {
                0 => "Planar (Zero Crossings)",
                _ when planarityScore >= 80.0 => "Near-Planar (Sparse Crossings)",
                _ when planarityScore >= 50.0 => "Moderate Crossing Clutter",
                _ => "Dense Crossing Matrix"
            };

            string summary = FormatEdgeCrossingSummary(
                n, totalEdges, pairsChecked, crossingCount, acuteCount, density, planarityScore, category, isSelection);

            return new GraphEdgeCrossingPlanarityAudit(
                n, totalEdges, pairsChecked, crossingCount, acuteCount, density, planarityScore,
                crossings, category, isSelection, summary);
        }

        private static bool CheckSegmentIntersection(
            double p0_x, double p0_y, double p1_x, double p1_y,
            double p2_x, double p2_y, double p3_x, double p3_y,
            out double intersectX, out double intersectY, out double angleDegrees)
        {
            intersectX = 0; intersectY = 0; angleDegrees = 0;
            double s1_x = p1_x - p0_x;
            double s1_y = p1_y - p0_y;
            double s2_x = p3_x - p2_x;
            double s2_y = p3_y - p2_y;

            double denom = -s2_x * s1_y + s1_x * s2_y;
            if (Math.Abs(denom) < 1e-9) return false;

            double s = (-s1_y * (p0_x - p2_x) + s1_x * (p0_y - p2_y)) / denom;
            double t = ( s2_x * (p0_y - p2_y) - s2_y * (p0_x - p2_x)) / denom;

            if (s > 0.001 && s < 0.999 && t > 0.001 && t < 0.999)
            {
                intersectX = Math.Round(p0_x + (t * s1_x), 1);
                intersectY = Math.Round(p0_y + (t * s1_y), 1);

                double dot = s1_x * s2_x + s1_y * s2_y;
                double mag1 = Math.Sqrt(s1_x * s1_x + s1_y * s1_y);
                double mag2 = Math.Sqrt(s2_x * s2_x + s2_y * s2_y);
                if (mag1 > 1e-9 && mag2 > 1e-9)
                {
                    double cosTheta = Math.Clamp(dot / (mag1 * mag2), -1.0, 1.0);
                    double angleRad = Math.Acos(Math.Abs(cosTheta));
                    angleDegrees = Math.Round(angleRad * 180.0 / Math.PI, 1);
                }
                return true;
            }
            return false;
        }

        public static GraphEdgeCrossingPlanarityAudit AuditEdgeCrossingsAndPlanarity(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditEdgeCrossingsAndPlanarity();
        }

        public static string FormatEdgeCrossingSummary(
            int totalNodes, int totalEdges, int pairsChecked, int crossingCount,
            int acuteCount, double density, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph edge crossing & planarity audit ({scope}0 nodes): Empty map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            string edgeStr = totalEdges == 1 ? "1 edge" : $"{totalEdges} edges";

            var alerts = new List<string>();
            if (crossingCount == 0)
            {
                alerts.Add("0 crossings (pure planar)");
            }
            else
            {
                alerts.Add($"⚠ {crossingCount} {(crossingCount == 1 ? "crossing" : "crossings")} (density {density:0.##})");
                if (acuteCount > 0) alerts.Add($"⚠ {acuteCount} acute-angle tangle(s)");
            }

            string details = string.Join(" · ", alerts);
            return $"Graph edge crossing & planarity audit ({scope}{nodeStr}, {edgeStr} · {category} · {score:0.#}% score): {details}";
        }

        /// <summary>
        /// Audits mind map graph nodes for visual mass center of gravity, quadrant mass distribution parity,
        /// distance offset from geometric midpoint, and spatial balance.
        /// </summary>
        public GraphSpatialBalanceAudit AuditVisualMassCenterAndSpatialBalance(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0) ? selected : Nodes.ToList();
            bool isSelection = selectionOnly && selected.Count > 0;
            string scope = isSelection ? "Selection · " : "";

            int n = targetNodes.Count;

            if (n == 0)
            {
                return new GraphSpatialBalanceAudit(
                    0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0,
                    Array.Empty<SpatialQuadrantMassItem>(),
                    "Empty Map",
                    isSelection,
                    $"Graph spatial balance audit ({scope}0 nodes): Empty map");
            }

            if (n == 1)
            {
                var single = targetNodes[0];
                var singleItem = new SpatialQuadrantMassItem("Centroid (Single Node)", 1, 100.0, 100.0, 0.0, "✓ Single node centered");
                return new GraphSpatialBalanceAudit(
                    1, single.X, single.Y, single.X, single.Y, 0.0, 0.0, 100.0,
                    new[] { singleItem },
                    "Balanced Single Node",
                    isSelection,
                    $"Graph spatial balance audit ({scope}1 node · Balanced Single Node · 100% score): Centered centroid");
            }

            double minX = targetNodes.Min(node => node.X);
            double maxX = targetNodes.Max(node => node.X);
            double minY = targetNodes.Min(node => node.Y);
            double maxY = targetNodes.Max(node => node.Y);

            double midX = (minX + maxX) / 2.0;
            double midY = (minY + maxY) / 2.0;

            double totalMass = 0.0;
            double weightedSumX = 0.0;
            double weightedSumY = 0.0;

            var nodeMasses = new List<(MindMapNodeViewModel Node, double Mass)>();
            foreach (var node in targetNodes)
            {
                int childLinks = Links.Count(l => l.SourceNodeId == node.Id || l.TargetNodeId == node.Id);
                double mass = 50.0 + (node.Title?.Length ?? 0) * 2.0 + childLinks * 15.0;
                nodeMasses.Add((node, mass));
                totalMass += mass;
                weightedSumX += (node.X * mass);
                weightedSumY += (node.Y * mass);
            }

            double cogX = totalMass > 0 ? weightedSumX / totalMass : midX;
            double cogY = totalMass > 0 ? weightedSumY / totalMass : midY;

            double offsetDistance = Math.Sqrt(Math.Pow(cogX - midX, 2) + Math.Pow(cogY - midY, 2));

            var qNodes = new Dictionary<string, (List<MindMapNodeViewModel> Nodes, double Mass)>(StringComparer.Ordinal)
            {
                ["Q1 (Top-Right)"] = (new List<MindMapNodeViewModel>(), 0.0),
                ["Q2 (Top-Left)"] = (new List<MindMapNodeViewModel>(), 0.0),
                ["Q3 (Bottom-Left)"] = (new List<MindMapNodeViewModel>(), 0.0),
                ["Q4 (Bottom-Right)"] = (new List<MindMapNodeViewModel>(), 0.0)
            };

            foreach (var (node, mass) in nodeMasses)
            {
                string qName;
                if (node.X >= midX)
                {
                    qName = node.Y < midY ? "Q1 (Top-Right)" : "Q4 (Bottom-Right)";
                }
                else
                {
                    qName = node.Y < midY ? "Q2 (Top-Left)" : "Q3 (Bottom-Left)";
                }
                var entry = qNodes[qName];
                entry.Nodes.Add(node);
                qNodes[qName] = (entry.Nodes, entry.Mass + mass);
            }

            var quadrantItems = new List<SpatialQuadrantMassItem>();
            double sumSquaredDiffs = 0.0;
            const double idealPct = 25.0;

            foreach (var kvp in qNodes)
            {
                double pct = totalMass > 0 ? (kvp.Value.Mass / totalMass) * 100.0 : 0.0;
                sumSquaredDiffs += Math.Pow(pct - idealPct, 2);

                double qCogX = kvp.Value.Nodes.Count > 0 ? kvp.Value.Nodes.Average(nd => nd.X) : midX;
                double qCogY = kvp.Value.Nodes.Count > 0 ? kvp.Value.Nodes.Average(nd => nd.Y) : midY;
                double qOffset = Math.Sqrt(Math.Pow(qCogX - midX, 2) + Math.Pow(qCogY - midY, 2));

                string status = kvp.Value.Nodes.Count == 0
                    ? "⚠ Empty quadrant sector"
                    : pct > 45.0
                        ? $"⚠ Dense concentration ({pct:0.#}%)"
                        : $"✓ Well-distributed ({pct:0.#}%)";

                quadrantItems.Add(new SpatialQuadrantMassItem(
                    kvp.Key,
                    kvp.Value.Nodes.Count,
                    Math.Round(kvp.Value.Mass, 1),
                    Math.Round(pct, 1),
                    Math.Round(qOffset, 1),
                    status));
            }

            double massVariance = Math.Round(sumSquaredDiffs / 4.0, 1);

            double span = Math.Max(1.0, Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2)));
            double normalizedOffsetRatio = offsetDistance / span;

            double score = 100.0 - (massVariance * 0.08) - (normalizedOffsetRatio * 40.0);
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Spatial Balance (≥90%)"
                : score >= 75.0
                    ? "Moderate Spatial Asymmetry (75-89%)"
                    : "Severe Visual Mass Imbalance (<75%)";

            string summary = FormatSpatialBalanceSummary(
                n, cogX, cogY, midX, midY, offsetDistance, massVariance, score, category, isSelection);

            return new GraphSpatialBalanceAudit(
                n, Math.Round(cogX, 1), Math.Round(cogY, 1), Math.Round(midX, 1), Math.Round(midY, 1),
                Math.Round(offsetDistance, 1), massVariance, score, quadrantItems, category, isSelection, summary);
        }

        public static GraphSpatialBalanceAudit AuditVisualMassCenterAndSpatialBalance(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditVisualMassCenterAndSpatialBalance();
        }

        public static string FormatSpatialBalanceSummary(
            int totalNodes, double cogX, double cogY, double midX, double midY,
            double offsetDistance, double massVariance, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph spatial balance audit ({scope}0 nodes): Empty map";
            if (totalNodes == 1) return $"Graph spatial balance audit ({scope}1 node · Balanced Single Node · 100% score): Centered centroid";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var alerts = new List<string>();
            alerts.Add($"CoG offset {offsetDistance:0.#}px from center");
            alerts.Add($"quadrant variance {massVariance:0.#}");
            if (score >= 90.0) alerts.Add("uniform 4-quadrant balance");
            else if (score >= 75.0) alerts.Add("moderate visual mass tilt");
            else alerts.Add("⚠ heavy asymmetric mass concentration");

            string details = string.Join(" · ", alerts);
            return $"Graph spatial balance audit ({scope}{nodeStr} · {category} · {score:0.#}% score): {details}";
        }

        /// <summary>
        /// Audits mind map graph node bounding box collisions, clearance distances, crowding density, and visual breathing room.
        /// </summary>
        public GraphNodeProximityAudit AuditNodeProximityAndOverlaps(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            bool isSelection = selectionOnly && selected.Count > 0;
            var targetNodes = isSelection ? selected : Nodes.ToList();
            int n = targetNodes.Count;
            string scope = isSelection ? "Selection · " : "";

            if (n == 0)
            {
                return new GraphNodeProximityAudit(
                    0, 0, 0, 0, 0.0, 0.0, 100.0,
                    Array.Empty<NodeProximityPairItem>(),
                    "Empty Map",
                    isSelection,
                    $"Graph node proximity audit ({scope}0 nodes): Empty map");
            }

            if (n == 1)
            {
                return new GraphNodeProximityAudit(
                    1, 0, 0, 0, double.PositiveInfinity, double.PositiveInfinity, 100.0,
                    Array.Empty<NodeProximityPairItem>(),
                    "Single Node · Full Clearance",
                    isSelection,
                    $"Graph node proximity audit ({scope}1 node · Single Node · 100% score): Infinite clearance");
            }

            const double defaultWidth = 140.0;
            const double defaultHeight = 46.0;

            var proximalPairs = new List<NodeProximityPairItem>();
            int totalPairs = 0;
            int collisions = 0;
            int crowdedCount = 0;
            double sumClearance = 0.0;
            double minClearance = double.MaxValue;

            for (int i = 0; i < n; i++)
            {
                var nodeA = targetNodes[i];
                double wA = defaultWidth;
                double hA = defaultHeight;

                for (int j = i + 1; j < n; j++)
                {
                    var nodeB = targetNodes[j];
                    double wB = defaultWidth;
                    double hB = defaultHeight;

                    totalPairs++;

                    double centerDist = Math.Sqrt(Math.Pow(nodeA.X - nodeB.X, 2) + Math.Pow(nodeA.Y - nodeB.Y, 2));

                    double dx = Math.Abs(nodeA.X - nodeB.X) - ((wA + wB) / 2.0);
                    double dy = Math.Abs(nodeA.Y - nodeB.Y) - ((hA + hB) / 2.0);

                    bool isOverlapping = dx < 0.0 && dy < 0.0;
                    double overlapArea = isOverlapping ? Math.Abs(dx) * Math.Abs(dy) : 0.0;
                    double clearance = isOverlapping ? -Math.Max(Math.Abs(dx), Math.Abs(dy)) : Math.Max(0.0, Math.Max(dx, dy));

                    if (clearance < minClearance) minClearance = clearance;
                    sumClearance += Math.Max(0.0, clearance);

                    string urgency;
                    if (isOverlapping)
                    {
                        collisions++;
                        urgency = "Critical Collision (Overlap)";
                    }
                    else if (clearance < 25.0)
                    {
                        crowdedCount++;
                        urgency = "High Crowding";
                    }
                    else if (clearance < 50.0)
                    {
                        urgency = "Moderate Proximity";
                    }
                    else
                    {
                        urgency = "Optimal Clearance";
                    }

                    if (clearance < 50.0 || isOverlapping)
                    {
                        proximalPairs.Add(new NodeProximityPairItem(
                            nodeA.Id ?? $"n{i}",
                            nodeA.Title ?? "Node",
                            nodeB.Id ?? $"n{j}",
                            nodeB.Title ?? "Node",
                            Math.Round(centerDist, 1),
                            Math.Round(clearance, 1),
                            isOverlapping,
                            Math.Round(overlapArea, 1),
                            urgency));
                    }
                }
            }

            double avgClearance = totalPairs > 0 ? Math.Round(sumClearance / totalPairs, 1) : 0.0;
            if (minClearance == double.MaxValue) minClearance = 0.0;
            else minClearance = Math.Round(minClearance, 1);

            proximalPairs.Sort((a, b) => a.ClearanceDistance.CompareTo(b.ClearanceDistance));

            double score = 100.0 - (collisions * 25.0) - (crowdedCount * 6.0);
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Spatial Breathing Room (≥90%)"
                : score >= 75.0
                    ? "Moderate Node Proximity Crowding (75-89%)"
                    : "Severe Collision & Overlapping Nodes (<75%)";

            string summary = FormatNodeProximitySummary(
                n, totalPairs, collisions, crowdedCount, minClearance, avgClearance, score, category, isSelection);

            return new GraphNodeProximityAudit(
                n, totalPairs, collisions, crowdedCount, minClearance, avgClearance,
                score, proximalPairs, category, isSelection, summary);
        }

        public static GraphNodeProximityAudit AuditNodeProximityAndOverlaps(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var nd in nodes) vm.Nodes.Add(new MindMapNodeViewModel(nd));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditNodeProximityAndOverlaps();
        }

        public static string FormatNodeProximitySummary(
            int totalNodes, int totalPairs, int collisions, int crowdedCount,
            double minClearance, double avgClearance, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph node proximity audit ({scope}0 nodes): Empty map";
            if (totalNodes == 1) return $"Graph node proximity audit ({scope}1 node · Single Node · 100% score): Infinite clearance";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var alerts = new List<string>();
            if (collisions > 0) alerts.Add($"⚠ {collisions} collision {(collisions == 1 ? "overlap" : "overlaps")}");
            else alerts.Add("0 collisions");

            if (crowdedCount > 0) alerts.Add($"⚠ {crowdedCount} crowded {(crowdedCount == 1 ? "pair" : "pairs")}");
            else alerts.Add("ample clearance");

            alerts.Add($"min clearance {minClearance:0.#}px");

            string details = string.Join(" · ", alerts);
            return $"Graph node proximity audit ({scope}{nodeStr} · {category} · {score:0.#}% score): {details}";
        }

        /// <summary>
        /// Audits mind map graph subtree depth uniformity, branching factors, leaf density, and cognitive balance.
        /// </summary>
        public GraphSubtreeDepthAudit AuditSubtreeDepthAndBranching(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            bool isSelection = selectionOnly && selected.Count > 0;
            var targetNodes = isSelection ? selected : Nodes.ToList();
            int totalNodes = targetNodes.Count;
            string scope = isSelection ? "Selection · " : "";

            if (totalNodes == 0)
            {
                return new GraphSubtreeDepthAudit(
                    0, 0, 0.0, 0, 0.0, 0.0, 0, 0, 100.0,
                    Array.Empty<SubtreeBranchingItem>(),
                    "Empty Map",
                    isSelection,
                    $"Graph subtree depth audit ({scope}0 nodes): Empty map");
            }

            if (totalNodes == 1)
            {
                var sole = targetNodes[0];
                var singleItem = new SubtreeBranchingItem(
                    sole.Id ?? "root",
                    sole.Title ?? "Root",
                    1,
                    0,
                    true,
                    false,
                    false,
                    "✓ Root leaf node (Depth 1)");

                return new GraphSubtreeDepthAudit(
                    1, 1, 1.0, 1, 1.0, 0.0, 0, 0, 100.0,
                    new[] { singleItem },
                    "Single Node · Uniform Depth",
                    isSelection,
                    $"Graph subtree depth audit ({scope}1 node · Single Node · 100% score): Depth 1 · 0 branches · 1 leaf");
            }

            var idMap = targetNodes.ToDictionary(n => n.Id ?? string.Empty, n => n);
            var childrenMap = new Dictionary<string, List<MindMapNodeViewModel>>();
            foreach (var node in targetNodes)
            {
                string id = node.Id ?? string.Empty;
                if (!childrenMap.ContainsKey(id)) childrenMap[id] = new List<MindMapNodeViewModel>();
            }

            foreach (var node in targetNodes)
            {
                if (!string.IsNullOrEmpty(node.ParentId) && childrenMap.ContainsKey(node.ParentId))
                {
                    childrenMap[node.ParentId].Add(node);
                }
            }

            // Also check Links if childrenMap is empty or sparse
            foreach (var link in Links)
            {
                if (!string.IsNullOrEmpty(link.SourceNodeId) && !string.IsNullOrEmpty(link.TargetNodeId))
                {
                    if (childrenMap.ContainsKey(link.SourceNodeId) && idMap.ContainsKey(link.TargetNodeId))
                    {
                        var childNode = idMap[link.TargetNodeId];
                        if (!childrenMap[link.SourceNodeId].Contains(childNode))
                        {
                            childrenMap[link.SourceNodeId].Add(childNode);
                        }
                    }
                }
            }

            // Determine root candidates
            var rootCandidates = targetNodes.Where(n => string.IsNullOrEmpty(n.ParentId) || !idMap.ContainsKey(n.ParentId)).ToList();
            if (rootCandidates.Count == 0) rootCandidates = new List<MindMapNodeViewModel> { targetNodes[0] };

            var depthMap = new Dictionary<string, int>();
            var queue = new Queue<(MindMapNodeViewModel Node, int Depth)>();
            var visited = new HashSet<string>();

            foreach (var root in rootCandidates)
            {
                string rootId = root.Id ?? string.Empty;
                if (visited.Add(rootId))
                {
                    queue.Enqueue((root, 1));
                    depthMap[rootId] = 1;
                }
            }

            while (queue.Count > 0)
            {
                var (curr, d) = queue.Dequeue();
                string cId = curr.Id ?? string.Empty;

                if (childrenMap.TryGetValue(cId, out var kids))
                {
                    foreach (var k in kids)
                    {
                        string kId = k.Id ?? string.Empty;
                        if (visited.Add(kId))
                        {
                            depthMap[kId] = d + 1;
                            queue.Enqueue((k, d + 1));
                        }
                    }
                }
            }

            // Handle unvisited components
            foreach (var node in targetNodes)
            {
                string nId = node.Id ?? string.Empty;
                if (!depthMap.ContainsKey(nId))
                {
                    depthMap[nId] = 1;
                }
            }

            var items = new List<SubtreeBranchingItem>();
            int maxDepth = 1;
            long sumDepth = 0;
            int leafCount = 0;
            int sumBranches = 0;
            int nonLeafCount = 0;
            int deepSprawlCount = 0;
            int excessiveBranchingCount = 0;

            foreach (var node in targetNodes)
            {
                string nId = node.Id ?? string.Empty;
                int depth = depthMap[nId];
                if (depth > maxDepth) maxDepth = depth;
                sumDepth += depth;

                int childCount = childrenMap.TryGetValue(nId, out var kids) ? kids.Count : 0;
                bool isLeaf = childCount == 0;
                if (isLeaf) leafCount++;
                else
                {
                    nonLeafCount++;
                    sumBranches += childCount;
                }

                bool hasDeepSprawl = depth > 6;
                if (hasDeepSprawl) deepSprawlCount++;

                bool hasExcessiveBranching = childCount > 7;
                if (hasExcessiveBranching) excessiveBranchingCount++;

                string status;
                if (hasDeepSprawl)
                    status = $"⚠ Deep cognitive sprawl (Depth {depth} > 6 max recommended)";
                else if (hasExcessiveBranching)
                    status = $"⚠ Excessive branching ({childCount} children > 7 Miller's law)";
                else if (isLeaf)
                    status = $"✓ Leaf node (Depth {depth})";
                else
                    status = $"✓ Balanced branch node (Depth {depth} · {childCount} children)";

                items.Add(new SubtreeBranchingItem(
                    nId,
                    node.Title ?? "Node",
                    depth,
                    childCount,
                    isLeaf,
                    hasExcessiveBranching,
                    hasDeepSprawl,
                    status));
            }

            double avgDepth = Math.Round((double)sumDepth / totalNodes, 1);
            double leafRatio = Math.Round((double)leafCount / totalNodes, 2);
            double avgBranching = nonLeafCount > 0 ? Math.Round((double)sumBranches / nonLeafCount, 1) : 0.0;

            double score = 100.0;
            score -= (deepSprawlCount * 12.0);
            score -= (excessiveBranchingCount * 8.0);
            if (maxDepth >= 7) score -= 10.0;
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Subtree Balance & Depth (≥90%)"
                : score >= 75.0
                    ? "Moderate Subtree Sprawl & Asymmetry (75-89%)"
                    : "Severe Cognitive Sprawl & Deep Hierarchy (<75%)";

            string summary = FormatSubtreeDepthSummary(
                totalNodes, maxDepth, avgDepth, leafCount, leafRatio, avgBranching,
                deepSprawlCount, excessiveBranchingCount, score, category, isSelection);

            return new GraphSubtreeDepthAudit(
                totalNodes, maxDepth, avgDepth, leafCount, leafRatio, avgBranching,
                deepSprawlCount, excessiveBranchingCount, score, items, category, isSelection, summary);
        }

        public static GraphSubtreeDepthAudit AuditSubtreeDepthAndBranching(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var nd in nodes) vm.Nodes.Add(new MindMapNodeViewModel(nd));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditSubtreeDepthAndBranching();
        }

        public static string FormatSubtreeDepthSummary(
            int totalNodes, int maxDepth, double avgDepth, int leafCount, double leafRatio,
            double avgBranching, int deepSprawl, int excessiveBranching, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph subtree depth audit ({scope}0 nodes): Empty map";
            if (totalNodes == 1) return $"Graph subtree depth audit ({scope}1 node · Single Node · 100% score): Depth 1 · 0 branches · 1 leaf";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();
            parts.Add($"max depth {maxDepth} (avg {avgDepth:0.#})");
            parts.Add($"{leafCount} leaves ({leafRatio:P0})");
            parts.Add($"avg branching {avgBranching:0.#}");

            if (deepSprawl > 0) parts.Add($"⚠ {deepSprawl} deep sprawl (>6 levels)");
            else parts.Add("0 deep sprawl");

            if (excessiveBranching > 0) parts.Add($"⚠ {excessiveBranching} excessive branching (>7)");

            string details = string.Join(" · ", parts);
            return $"Graph subtree depth audit ({scope}{nodeStr} · {category} · {score:0.#}% score): {details}";
        }

        /// <summary>
        /// Audits mind map graph topology for cyclic loops, directed feedback cycles, and strongly connected components (Tarjan's algorithm).
        /// </summary>
        public GraphCycleDetectionAudit AuditGraphDagFeedbackLoopsAndScc(bool selectionOnly = false)
        {
            var targetList = selectionOnly && HasSelection
                ? Nodes.Where(n => n.IsSelected).ToList()
                : Nodes.ToList();

            if (targetList.Count == 0 && selectionOnly)
            {
                targetList = Nodes.ToList();
            }

            bool isSelection = selectionOnly && HasSelection && targetList.Count < Nodes.Count;
            var targetIds = new HashSet<string>(targetList.Select(n => n.Id));
            var linksInScope = Links.Where(l => targetIds.Contains(l.SourceNodeId) && targetIds.Contains(l.TargetNodeId)).ToList();

            if (targetList.Count == 0)
            {
                string emptySummary = "Graph cycle & SCC audit: 0 nodes · Mind map is empty";
                StatusMessage = emptySummary;
                return new GraphCycleDetectionAudit(
                    0, 0, 0, 0, 0, 0, 0, 100.0,
                    Array.Empty<GraphCycleComponentItem>(),
                    "Empty MindMap",
                    isSelection,
                    emptySummary);
            }

            // Build directed adjacency list
            var nodeMap = targetList.ToDictionary(n => n.Id, n => n);
            var adj = new Dictionary<string, List<string>>();
            var selfLoopNodes = new HashSet<string>();

            foreach (var n in targetList)
            {
                adj[n.Id] = new List<string>();
            }

            foreach (var l in linksInScope)
            {
                if (l.SourceNodeId == l.TargetNodeId)
                {
                    selfLoopNodes.Add(l.SourceNodeId);
                }
                else
                {
                    if (adj.ContainsKey(l.SourceNodeId))
                    {
                        adj[l.SourceNodeId].Add(l.TargetNodeId);
                    }
                }
            }

            // Also consider parent-child relationship as directed edge (Parent -> Child) if both exist in scope
            foreach (var n in targetList)
            {
                if (!string.IsNullOrEmpty(n.ParentId) && targetIds.Contains(n.ParentId))
                {
                    if (n.ParentId == n.Id)
                    {
                        selfLoopNodes.Add(n.Id);
                    }
                    else if (adj.ContainsKey(n.ParentId) && !adj[n.ParentId].Contains(n.Id))
                    {
                        adj[n.ParentId].Add(n.Id);
                    }
                }
            }

            // Tarjan's Strongly Connected Components (SCC) Algorithm
            int index = 0;
            var indices = new Dictionary<string, int>();
            var lowlink = new Dictionary<string, int>();
            var onStack = new HashSet<string>();
            var stack = new Stack<string>();
            var sccs = new List<List<string>>();

            void StrongConnect(string v)
            {
                indices[v] = index;
                lowlink[v] = index;
                index++;
                stack.Push(v);
                onStack.Add(v);

                foreach (var w in adj[v])
                {
                    if (!indices.ContainsKey(w))
                    {
                        StrongConnect(w);
                        lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowlink[v] = Math.Min(lowlink[v], indices[w]);
                    }
                }

                if (lowlink[v] == indices[v])
                {
                    var scc = new List<string>();
                    string w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        scc.Add(w);
                    } while (w != v);
                    sccs.Add(scc);
                }
            }

            foreach (var n in targetList)
            {
                if (!indices.ContainsKey(n.Id))
                {
                    StrongConnect(n.Id);
                }
            }

            var cycleItems = new List<GraphCycleComponentItem>();
            int cycleIdCounter = 1;
            int multiNodeCycles = 0;
            int totalSelfLoops = 0;

            foreach (var scc in sccs)
            {
                bool isMulti = scc.Count > 1;
                bool isSelf = scc.Count == 1 && selfLoopNodes.Contains(scc[0]);

                if (isMulti || isSelf)
                {
                    if (isMulti) multiNodeCycles++;
                    if (isSelf) totalSelfLoops++;

                    var titles = scc.Select(id => nodeMap.TryGetValue(id, out var n) ? n.Title : id).ToList();
                    string path = isSelf
                        ? $"{titles[0]} ↻ (Self Loop)"
                        : $"{string.Join(" -> ", titles)} -> {titles[0]}";

                    string action = isSelf
                        ? $"Remove self-referential link on '{titles[0]}'"
                        : $"Reverse or remove edge '{titles[titles.Count - 1]}' -> '{titles[0]}' to break feedback loop";

                    cycleItems.Add(new GraphCycleComponentItem(
                        cycleIdCounter++,
                        scc.Count,
                        titles,
                        scc,
                        isSelf,
                        isMulti,
                        path,
                        action));
                }
            }

            int totalComponents = sccs.Count;
            int cyclicComponents = cycleItems.Count;
            int acyclicComponents = totalComponents - cyclicComponents;
            int totalCycles = multiNodeCycles + totalSelfLoops;

            double score = 100.0;
            score -= (multiNodeCycles * 35.0);
            score -= (totalSelfLoops * 20.0);
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Directed Acyclic Graph (DAG ≥90%)"
                : score >= 70.0
                    ? "Moderate Feedback Loops & Cycles (70-89%)"
                    : "High Cyclic Coupling & Feedback Risk (<70%)";

            string summary = FormatGraphCyclesSummary(
                targetList.Count, linksInScope.Count, totalComponents, acyclicComponents,
                cyclicComponents, totalCycles, totalSelfLoops, score, category, isSelection);

            StatusMessage = summary;

            return new GraphCycleDetectionAudit(
                targetList.Count,
                linksInScope.Count,
                totalComponents,
                acyclicComponents,
                cyclicComponents,
                totalCycles,
                totalSelfLoops,
                score,
                cycleItems,
                category,
                isSelection,
                summary);
        }

        public static GraphCycleDetectionAudit AuditGraphDagFeedbackLoopsAndScc(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var nd in nodes) vm.Nodes.Add(new MindMapNodeViewModel(nd));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditGraphDagFeedbackLoopsAndScc();
        }

        public static string FormatGraphCyclesSummary(
            int totalNodes, int totalLinks, int totalComponents, int acyclicComponents,
            int cyclicComponents, int totalCycles, int selfLoops, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph cycle & SCC audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();
            parts.Add($"{totalComponents} SCCs ({acyclicComponents} acyclic)");

            if (totalCycles > 0)
            {
                parts.Add($"⚠ {totalCycles} {(totalCycles == 1 ? "cycle" : "cycles")} detected");
            }
            else
            {
                parts.Add("0 cycles (pure DAG)");
            }

            if (selfLoops > 0)
            {
                parts.Add($"⚠ {selfLoops} self {(selfLoops == 1 ? "loop" : "loops")}");
            }

            string details = string.Join(" · ", parts);
            return $"Graph cycle & SCC audit ({scope}{nodeStr}, {totalLinks} links · {category} · {score:0.#}% DAG score): {details}";
        }

        /// <summary>
        /// Audits mind map nodes for spatial density, force-directed bounding overlaps, pairwise Euclidean distances,
        /// collision clusters, and spatial dispersion balance.
        /// </summary>
        public GraphSpatialDensityAudit AuditSpatialDensityAndCollisions(bool selectionOnly = false)
        {
            var targetList = selectionOnly ? Nodes.Where(n => n.IsSelected).ToList() : Nodes.ToList();
            bool isSelection = selectionOnly && targetList.Count < Nodes.Count;

            if (targetList.Count == 0)
            {
                return new GraphSpatialDensityAudit(
                    0, 0, 0, 0, 0.0, 0.0, 0.0, 100.0,
                    Array.Empty<NodeSpatialClusterItem>(),
                    "Empty Mind Map",
                    isSelection,
                    $"Graph spatial density audit ({(isSelection ? "Selection · " : "")}0 nodes): Mind map is empty");
            }

            if (targetList.Count == 1)
            {
                var single = targetList[0];
                var singleItem = new NodeSpatialClusterItem(
                    single.Id, single.Title, single.X, single.Y, 0, 0, 0.0, "Isolated Single Node",
                    "✓ Single node on canvas: no collisions possible");
                return new GraphSpatialDensityAudit(
                    1, 0, 0, 1, 0.0, 0.0, 0.0, 100.0,
                    new[] { singleItem },
                    "Optimal Spatial Dispersion & Balance (≥90%)",
                    isSelection,
                    $"Graph spatial density audit ({(isSelection ? "Selection · " : "")}1 node · Optimal Spatial Dispersion & Balance (≥90%) · 100% score): single isolated node");
            }

            const double DefaultWidth = 140.0;
            const double DefaultHeight = 48.0;
            const double ProximityRadius = 160.0;
            const double IsolatedThreshold = 600.0;

            var clusters = new List<NodeSpatialClusterItem>();
            var distances = new List<double>();
            int collisionPairCount = 0;
            var overlappingNodeIds = new HashSet<string>();
            int crowdedCount = 0;
            int isolatedCount = 0;

            for (int i = 0; i < targetList.Count; i++)
            {
                var n1 = targetList[i];
                double w1 = n1.Width > 0 ? n1.Width : DefaultWidth;
                double h1 = n1.Height > 0 ? n1.Height : DefaultHeight;

                int overlaps = 0;
                int neighborsWithinRadius = 0;
                double nearestDist = double.MaxValue;

                for (int j = 0; j < targetList.Count; j++)
                {
                    if (i == j) continue;
                    var n2 = targetList[j];
                    double w2 = n2.Width > 0 ? n2.Width : DefaultWidth;
                    double h2 = n2.Height > 0 ? n2.Height : DefaultHeight;

                    double dx = (n1.X + w1 / 2.0) - (n2.X + w2 / 2.0);
                    double dy = (n1.Y + h1 / 2.0) - (n2.Y + h2 / 2.0);
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    nearestDist = Math.Min(nearestDist, dist);

                    if (i < j)
                    {
                        distances.Add(dist);
                    }

                    if (dist <= ProximityRadius)
                    {
                        neighborsWithinRadius++;
                    }

                    // AABB overlap check
                    bool overlapsAabb = !(n1.X + w1 <= n2.X || n2.X + w2 <= n1.X || n1.Y + h1 <= n2.Y || n2.Y + h2 <= n1.Y);
                    if (overlapsAabb)
                    {
                        overlaps++;
                        overlappingNodeIds.Add(n1.Id);
                        overlappingNodeIds.Add(n2.Id);
                        if (i < j)
                        {
                            collisionPairCount++;
                        }
                    }
                }

                if (nearestDist == double.MaxValue) nearestDist = 0.0;
                nearestDist = Math.Round(nearestDist, 1);

                string riskLevel;
                string status;

                if (overlaps > 0)
                {
                    riskLevel = "Collision / Overlap Hazard";
                    status = $"⚠ Node overlaps with {overlaps} other {(overlaps == 1 ? "node" : "nodes")} (Nearest: {nearestDist:0.#}px)";
                }
                else if (nearestDist < 70.0)
                {
                    crowdedCount++;
                    riskLevel = "Crowded Proximity";
                    status = $"⚠ Crowded layout: nearest neighbor is only {nearestDist:0.#}px away";
                }
                else if (nearestDist > IsolatedThreshold)
                {
                    isolatedCount++;
                    riskLevel = "Isolated / Disjointed";
                    status = $"ℹ Distant node: {nearestDist:0.#}px to nearest neighbor";
                }
                else
                {
                    riskLevel = "Balanced";
                    status = $"✓ Well-spaced node: {nearestDist:0.#}px nearest ({neighborsWithinRadius} in {ProximityRadius:0}px radius)";
                }

                clusters.Add(new NodeSpatialClusterItem(
                    n1.Id,
                    n1.Title,
                    Math.Round(n1.X, 1),
                    Math.Round(n1.Y, 1),
                    overlaps,
                    neighborsWithinRadius,
                    nearestDist,
                    riskLevel,
                    status));
            }

            double meanDist = distances.Count > 0 ? Math.Round(distances.Average(), 1) : 0.0;
            double minDist = distances.Count > 0 ? Math.Round(distances.Min(), 1) : 0.0;
            double maxDist = distances.Count > 0 ? Math.Round(distances.Max(), 1) : 0.0;

            double score = 100.0;
            score -= (collisionPairCount * 25.0);
            score -= (crowdedCount * 10.0);
            score -= (isolatedCount * 5.0);
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Spatial Dispersion & Balance (≥90%)"
                : score >= 70.0
                    ? "Moderate Node Crowding / Proximity Clutter (70-89%)"
                    : "High Collision Risk & Severe Cluster Overlap (<70%)";

            string summary = FormatSpatialDensitySummary(
                targetList.Count, collisionPairCount, overlappingNodeIds.Count, isolatedCount,
                meanDist, minDist, score, category, isSelection);

            StatusMessage = summary;

            return new GraphSpatialDensityAudit(
                targetList.Count,
                collisionPairCount,
                overlappingNodeIds.Count,
                isolatedCount,
                meanDist,
                minDist,
                maxDist,
                score,
                clusters,
                category,
                isSelection,
                summary);
        }

        public static GraphSpatialDensityAudit AuditSpatialDensityAndCollisions(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links = null)
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            if (nodes != null) foreach (var nd in nodes) vm.Nodes.Add(new MindMapNodeViewModel(nd));
            if (links != null) foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));
            return vm.AuditSpatialDensityAndCollisions();
        }

        public static string FormatSpatialDensitySummary(
            int totalNodes, int collisionPairs, int overlappingNodes, int isolatedNodes,
            double meanDist, double minDist, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph spatial density audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            if (collisionPairs > 0)
            {
                parts.Add($"⚠ {collisionPairs} collision {(collisionPairs == 1 ? "overlap" : "overlaps")} ({overlappingNodes} nodes affected)");
            }
            else
            {
                parts.Add("0 bounding collisions");
            }

            parts.Add($"mean distance {meanDist:0.#}px (min {minDist:0.#}px)");

            if (isolatedNodes > 0)
            {
                parts.Add($"{isolatedNodes} isolated {(isolatedNodes == 1 ? "node" : "nodes")}");
            }

            string details = string.Join(" · ", parts);
            return $"Graph spatial density audit ({scope}{nodeStr} · {category} · {score:0.#}% score): {details}";
        }

        /// <summary>
        /// Audits mind map graph hierarchy tree depth, child fan-out bottlenecks (>8 children),
        /// deep hierarchical sprawl (>6 levels), and branching factor balance.
        /// </summary>
        public GraphTreeHierarchyAudit AuditTreeHierarchyBranching(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = selectionOnly && selected.Count > 0 ? selected : Nodes.ToList();
            bool isSelection = selectionOnly && selected.Count > 0;

            var targetIds = new HashSet<string>(targetNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var relevantLinks = Links.Where(l => targetIds.Contains(l.SourceNodeId) && targetIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditTreeHierarchyBranching(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphTreeHierarchyAudit AuditTreeHierarchyBranching(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links = null, bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            string scopeStr = isSelection ? "Selection · " : "";
            int totalNodes = nodeList.Count;

            if (totalNodes == 0)
            {
                return new GraphTreeHierarchyAudit(
                    0, 0, 0, 0, 0.0, 0, 0, 100.0,
                    Array.Empty<TreeNodeBranchingItem>(),
                    "Clean Baseline", isSelection,
                    $"Graph tree hierarchy audit ({scopeStr}0 nodes): Empty mind map");
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            var outLinks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in nodeList)
            {
                outLinks[n.Id] = new List<string>();
                inDegree[n.Id] = 0;
            }

            foreach (var link in linkList)
            {
                if (outLinks.ContainsKey(link.SourceNodeId) && inDegree.ContainsKey(link.TargetNodeId))
                {
                    outLinks[link.SourceNodeId].Add(link.TargetNodeId);
                    inDegree[link.TargetNodeId]++;
                }
            }

            // Identify root candidates: in-degree == 0, or lowest in-degree
            var roots = nodeList.Where(n => inDegree[n.Id] == 0).ToList();
            if (roots.Count == 0 && nodeList.Count > 0)
            {
                int minIn = inDegree.Values.Min();
                roots = nodeList.Where(n => inDegree[n.Id] == minIn).ToList();
            }

            var depthMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Id, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in roots)
            {
                queue.Enqueue((r.Id, 0));
                depthMap[r.Id] = 0;
                visited.Add(r.Id);
            }

            while (queue.Count > 0)
            {
                var (currId, d) = queue.Dequeue();
                if (outLinks.TryGetValue(currId, out var children))
                {
                    foreach (var childId in children)
                    {
                        if (!visited.Contains(childId))
                        {
                            visited.Add(childId);
                            depthMap[childId] = d + 1;
                            queue.Enqueue((childId, d + 1));
                        }
                    }
                }
            }

            // Unreachable nodes (disconnected components)
            foreach (var n in nodeList)
            {
                if (!depthMap.ContainsKey(n.Id))
                {
                    depthMap[n.Id] = 0;
                }
            }

            var items = new List<TreeNodeBranchingItem>();
            int rootCount = roots.Count;
            int leafCount = 0;
            int overBranchingCount = 0;
            int deepSprawlCount = 0;
            int totalOutDegree = 0;
            int maxDepth = 0;

            foreach (var n in nodeList)
            {
                int depth = depthMap[n.Id];
                if (depth > maxDepth) maxDepth = depth;

                int childCount = outLinks[n.Id].Count;
                totalOutDegree += childCount;

                bool isRoot = roots.Any(r => string.Equals(r.Id, n.Id, StringComparison.OrdinalIgnoreCase));
                bool isLeaf = childCount == 0;
                if (isLeaf) leafCount++;

                bool isOverBranching = childCount > 8;
                if (isOverBranching) overBranchingCount++;

                bool isDeepSprawl = depth > 6;
                if (isDeepSprawl) deepSprawlCount++;

                string status;
                if (isOverBranching && isDeepSprawl) status = $"⚠ Over-branching ({childCount} children) & deep sprawl (depth {depth})";
                else if (isOverBranching) status = $"⚠ Over-branching bottleneck ({childCount} children > 8)";
                else if (isDeepSprawl) status = $"⚠ Deep hierarchy sprawl (depth {depth} > 6)";
                else if (isRoot) status = "Root node";
                else if (isLeaf) status = "Leaf node";
                else status = $"Balanced node ({childCount} children, depth {depth})";

                items.Add(new TreeNodeBranchingItem(n.Id, n.Title ?? "Untitled", depth, childCount, isRoot, isLeaf, isOverBranching, isDeepSprawl, status));
            }

            int branchableNodes = nodeList.Count(n => outLinks[n.Id].Count > 0);
            double avgBranching = branchableNodes > 0 ? Math.Round((double)totalOutDegree / branchableNodes, 1) : 0.0;

            double score = 100.0;
            score -= Math.Min(40.0, overBranchingCount * 10.0);
            score -= Math.Min(30.0, deepSprawlCount * 8.0);
            if (avgBranching > 6.0) score -= 15.0;
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Tree Hierarchy & Balanced Branching (≥90%)"
                : score >= 75.0
                    ? "Good Cognitive Hierarchy & Branch Distribution (75-89%)"
                    : score >= 50.0
                        ? "Moderate Tree Sprawl & Branching Bottlenecks (50-74%)"
                        : "Severe Hierarchy Disorganization & Deep Sprawl (<50%)";

            string summary = FormatTreeHierarchyBranchingSummary(
                totalNodes, rootCount, maxDepth, avgBranching, overBranchingCount, deepSprawlCount, score, category, isSelection);

            return new GraphTreeHierarchyAudit(
                totalNodes,
                rootCount,
                leafCount,
                maxDepth,
                avgBranching,
                overBranchingCount,
                deepSprawlCount,
                score,
                items,
                category,
                isSelection,
                summary);
        }

        public static string FormatTreeHierarchyBranchingSummary(
            int totalNodes, int rootCount, int maxDepth, double avgBranching,
            int overBranchingCount, int deepSprawlCount, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph tree hierarchy audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            parts.Add($"{rootCount} {(rootCount == 1 ? "root" : "roots")}, max depth {maxDepth}");
            parts.Add($"avg branching {avgBranching:0.#}");

            if (overBranchingCount > 0) parts.Add($"⚠ {overBranchingCount} over-branching (>8 children)");
            else parts.Add("0 fan-out bottlenecks");

            if (deepSprawlCount > 0) parts.Add($"⚠ {deepSprawlCount} deep sprawl (>6 levels)");

            string details = string.Join(" · ", parts);
            return $"Graph tree hierarchy audit ({scope}{nodeStr} · {category} · {score:0.#}% balance score): {details}";
        }

        /// <summary>
        /// Audits graph node local and global clustering coefficients, neighbor triadic closure,
        /// hub-and-spoke vs lattice configurations, and small-world interconnectedness.
        /// </summary>
        public GraphClusteringCoefficientAudit AuditClusteringCoefficientAndTopology(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = selectionOnly && selected.Count > 0 ? selected : Nodes.ToList();
            bool isSelection = selectionOnly && selected.Count > 0;

            var targetIds = new HashSet<string>(targetNodes.Select(n => n.Id));
            var relevantLinks = Links.Where(l => targetIds.Contains(l.SourceNodeId) && targetIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditClusteringCoefficientAndTopology(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphClusteringCoefficientAudit AuditClusteringCoefficientAndTopology(
            IEnumerable<MindMapNode>? nodes, IEnumerable<MindMapLink>? links = null, bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            int totalNodes = nodeList.Count;
            int totalLinks = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatClusteringCoefficientSummary(0, 0, 0, 0.0, 0, 0, 0, 100.0, "Empty MindMap", isSelection);
                return new GraphClusteringCoefficientAudit(
                    0, 0, 0, 0.0, 0, 0, 0, 100.0,
                    Array.Empty<NodeClusteringCoefficientItem>(),
                    "Empty MindMap",
                    isSelection,
                    emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id);
            var adjacency = new Dictionary<string, HashSet<string>>();
            foreach (var n in nodeList)
            {
                adjacency[n.Id] = new HashSet<string>();
            }

            foreach (var l in linkList)
            {
                if (adjacency.ContainsKey(l.SourceNodeId) && adjacency.ContainsKey(l.TargetNodeId) && l.SourceNodeId != l.TargetNodeId)
                {
                    adjacency[l.SourceNodeId].Add(l.TargetNodeId);
                    adjacency[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            var items = new List<NodeClusteringCoefficientItem>();
            int totalTriangles = 0;
            double sumClustering = 0.0;
            int clusterableNodes = 0;
            int hubAndSpokeCount = 0;
            int latticeClusterCount = 0;
            int isolatedCount = 0;

            foreach (var n in nodeList)
            {
                var neighbors = adjacency[n.Id].ToList();
                int degree = neighbors.Count;

                if (degree == 0)
                {
                    isolatedCount++;
                    items.Add(new NodeClusteringCoefficientItem(
                        n.Id, n.Title ?? "Untitled", 0, 0, 0, 0.0, false, false, "Isolated Vertex", "No Connections"));
                    continue;
                }

                if (degree == 1)
                {
                    items.Add(new NodeClusteringCoefficientItem(
                        n.Id, n.Title ?? "Untitled", 1, 0, 0, 0.0, false, false, "Pendant Leaf", "Single Branch"));
                    continue;
                }

                int maxPossible = degree * (degree - 1) / 2;
                int neighborEdges = 0;

                for (int i = 0; i < neighbors.Count; i++)
                {
                    for (int j = i + 1; j < neighbors.Count; j++)
                    {
                        if (adjacency[neighbors[i]].Contains(neighbors[j]))
                        {
                            neighborEdges++;
                        }
                    }
                }

                totalTriangles += neighborEdges;
                double localC = maxPossible > 0 ? Math.Round((double)neighborEdges / maxPossible, 3) : 0.0;
                sumClustering += localC;
                clusterableNodes++;

                bool isHub = degree >= 3 && neighborEdges == 0;
                bool isLattice = localC >= 0.65;

                if (isHub) hubAndSpokeCount++;
                if (isLattice) latticeClusterCount++;

                string classification = isHub ? "Hub-and-Spoke Vertex" :
                                        isLattice ? "Dense Lattice Cluster" :
                                        "Balanced Graph Transit";

                string status = $"{localC * 100:0.#}% clustering ({neighborEdges}/{maxPossible} triangles)";

                items.Add(new NodeClusteringCoefficientItem(
                    n.Id, n.Title ?? "Untitled", degree, neighborEdges, maxPossible, localC, isHub, isLattice, classification, status));
            }

            totalTriangles /= 3; // each triangle counted at its 3 vertices
            double globalC = clusterableNodes > 0 ? Math.Round(sumClustering / clusterableNodes, 3) : 0.0;

            double score = 100.0;
            score -= Math.Min(35.0, hubAndSpokeCount * 7.0);
            score -= Math.Min(25.0, isolatedCount * 5.0);
            if (globalC >= 0.3) score = Math.Min(100.0, score + 10.0);
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category = score >= 90.0
                ? "Optimal Clustering & Small-World Topology (≥90%)"
                : score >= 75.0
                    ? "Good Triadic Clustering & Local Transit (75-89%)"
                    : score >= 50.0
                        ? "Moderate Hub-and-Spoke Sprawl & Low Clustering (50-74%)"
                        : "Severe Topological Dispersion & Tree Fragmentation (<50%)";

            string summary = FormatClusteringCoefficientSummary(
                totalNodes, totalLinks, totalTriangles, globalC, hubAndSpokeCount, latticeClusterCount, isolatedCount, score, category, isSelection);

            return new GraphClusteringCoefficientAudit(
                totalNodes,
                totalLinks,
                totalTriangles,
                globalC,
                hubAndSpokeCount,
                latticeClusterCount,
                isolatedCount,
                score,
                items,
                category,
                isSelection,
                summary);
        }

        public static string FormatClusteringCoefficientSummary(
            int totalNodes, int totalLinks, int totalTriangles, double globalC,
            int hubAndSpokeCount, int latticeClusterCount, int isolatedCount, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph clustering coefficient audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            parts.Add($"{totalTriangles} {(totalTriangles == 1 ? "triangle" : "triangles")}, global C={globalC:0.###}");

            if (latticeClusterCount > 0) parts.Add($"{latticeClusterCount} lattice clusters");
            if (hubAndSpokeCount > 0) parts.Add($"⚠ {hubAndSpokeCount} hub-and-spoke sprawl");
            else parts.Add("0 pure hub-and-spoke sprawl");

            if (isolatedCount > 0) parts.Add($"⚠ {isolatedCount} isolated");

            string details = string.Join(" · ", parts);
            return $"Graph clustering coefficient audit ({scope}{nodeStr} · {category} · {score:0.#}% topology score): {details}";
        }

        public GraphBipartiteAudit AuditBipartitePartitionAndModularity(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = targetNodes.Select(n => n.Id).ToHashSet();
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditBipartitePartitionAndModularity(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphBipartiteAudit AuditBipartitePartitionAndModularity(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            int totalNodes = nodeList.Count;
            int totalLinks = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatBipartitePartitionSummary(0, 0, true, 0, 0, 0, 0, 100.0, "Empty MindMap", isSelection);
                return new GraphBipartiteAudit(
                    0, 0, true, 0, 0, 0, 0, 0, 100.0,
                    Array.Empty<NodeBipartiteItem>(),
                    "Empty MindMap",
                    isSelection,
                    emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id);
            var adjacency = new Dictionary<string, HashSet<string>>();
            foreach (var n in nodeList)
            {
                adjacency[n.Id] = new HashSet<string>();
            }

            foreach (var l in linkList)
            {
                if (adjacency.ContainsKey(l.SourceNodeId) && adjacency.ContainsKey(l.TargetNodeId) && l.SourceNodeId != l.TargetNodeId)
                {
                    adjacency[l.SourceNodeId].Add(l.TargetNodeId);
                    adjacency[l.TargetNodeId].Add(l.SourceNodeId);
                }
            }

            // BFS 2-coloring for each connected component
            var colorMap = new Dictionary<string, int>(); // 0: uncolored, 1: Partition A, 2: Partition B
            var componentMap = new Dictionary<string, int>();
            foreach (var n in nodeList) colorMap[n.Id] = 0;

            int componentCount = 0;
            int conflictEdgeCount = 0;
            var processedEdges = new HashSet<string>();
            var nodeConflictCount = new Dictionary<string, int>();
            foreach (var n in nodeList) nodeConflictCount[n.Id] = 0;

            foreach (var rootNode in nodeList)
            {
                if (colorMap[rootNode.Id] != 0) continue;

                componentCount++;
                int compId = componentCount;
                var queue = new Queue<string>();

                colorMap[rootNode.Id] = 1;
                componentMap[rootNode.Id] = compId;
                queue.Enqueue(rootNode.Id);

                while (queue.Count > 0)
                {
                    string currId = queue.Dequeue();
                    int currColor = colorMap[currId];
                    int nextColor = currColor == 1 ? 2 : 1;

                    foreach (var neighborId in adjacency[currId])
                    {
                        if (colorMap[neighborId] == 0)
                        {
                            colorMap[neighborId] = nextColor;
                            componentMap[neighborId] = compId;
                            queue.Enqueue(neighborId);
                        }
                        else if (colorMap[neighborId] == currColor)
                        {
                            // Conflict: odd cycle detected between currId and neighborId
                            string edgeKey = string.CompareOrdinal(currId, neighborId) < 0
                                ? $"{currId}->{neighborId}"
                                : $"{neighborId}->{currId}";

                            if (processedEdges.Add(edgeKey))
                            {
                                conflictEdgeCount++;
                                nodeConflictCount[currId]++;
                                nodeConflictCount[neighborId]++;
                            }
                        }
                    }
                }
            }

            int partition1Count = colorMap.Values.Count(c => c == 1);
            int partition2Count = colorMap.Values.Count(c => c == 2);
            int isolatedCount = nodeList.Count(n => adjacency[n.Id].Count == 0);
            bool isBipartite = conflictEdgeCount == 0;

            var items = new List<NodeBipartiteItem>();
            foreach (var n in nodeList)
            {
                int deg = adjacency[n.Id].Count;
                int part = colorMap[n.Id];
                int conflicts = nodeConflictCount[n.Id];
                bool isConflicted = conflicts > 0;
                int compId = componentMap.GetValueOrDefault(n.Id, 1);

                string status = deg == 0 ? "Isolated Vertex" :
                                isConflicted ? $"Odd-Cycle Conflict ({conflicts} same-partition edge{(conflicts == 1 ? "" : "s")})" :
                                part == 1 ? "Partition Set A (Valid Bipartite)" : "Partition Set B (Valid Bipartite)";

                items.Add(new NodeBipartiteItem(
                    n.Id, n.Title ?? "Untitled", deg, compId, part, conflicts, isConflicted, status));
            }

            double score = 100.0;
            if (conflictEdgeCount > 0)
            {
                score -= Math.Min(50.0, conflictEdgeCount * 12.0);
            }
            if (isBipartite && totalLinks > 0 && partition1Count > 0 && partition2Count > 0)
            {
                double balanceRatio = (double)Math.Min(partition1Count, partition2Count) / Math.Max(partition1Count, partition2Count);
                score -= (1.0 - balanceRatio) * 15.0;
            }
            score -= Math.Min(20.0, isolatedCount * 4.0);
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category;
            if (totalNodes == 0) category = "Empty MindMap";
            else if (score >= 90.0) category = "Optimal Bipartite Partition & Modularity Balance (≥90%)";
            else if (score >= 75.0) category = "Good Bipartite Symmetry with Minor Partition Asymmetry (75-89%)";
            else if (score >= 50.0) category = "Moderate Odd-Cycle Conflicts or High Partition Skew (50-74%)";
            else category = "Severe Graph Mesh Contention & Non-Bipartite Topology (<50%)";

            string summary = FormatBipartitePartitionSummary(
                totalNodes, totalLinks, isBipartite, partition1Count, partition2Count,
                conflictEdgeCount, componentCount, score, category, isSelection);

            return new GraphBipartiteAudit(
                totalNodes,
                totalLinks,
                isBipartite,
                partition1Count,
                partition2Count,
                conflictEdgeCount,
                componentCount,
                isolatedCount,
                score,
                items,
                category,
                isSelection,
                summary);
        }

        public static string FormatBipartitePartitionSummary(
            int totalNodes, int totalLinks, bool isBipartite, int partition1Count,
            int partition2Count, int conflictEdgeCount, int componentCount, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph bipartite partition audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            if (isBipartite)
            {
                parts.Add($"Pure Bipartite ({partition1Count} vs {partition2Count} partition sets)");
            }
            else
            {
                parts.Add($"Non-Bipartite ({conflictEdgeCount} odd-cycle conflict {(conflictEdgeCount == 1 ? "edge" : "edges")})");
            }

            if (componentCount > 1)
                parts.Add($"{componentCount} components");

            string details = string.Join(" · ", parts);
            return $"Graph bipartite partition audit ({scope}{nodeStr} · {category} · {score:0.#}% modularity score): {details}";
        }

        public GraphPlanarityAudit AuditGraphEdgeCrossingsAndPlanarity(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = targetNodes.Select(n => n.Id).ToHashSet();
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditGraphEdgeCrossingsAndPlanarity(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphPlanarityAudit AuditGraphEdgeCrossingsAndPlanarity(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            int totalNodes = nodeList.Count;
            int totalLinks = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatPlanaritySummary(0, 0, 0, false, "None (Planar Graph Topology)", 100.0, "Empty MindMap", isSelection);
                return new GraphPlanarityAudit(
                    0, 0, 0, false, "None (Planar Graph Topology)", 100.0,
                    Array.Empty<EdgeCrossingItem>(), "Empty MindMap", isSelection, emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id, n => n);
            var crossings = new List<EdgeCrossingItem>();

            // Distinct pairs of links
            for (int i = 0; i < linkList.Count; i++)
            {
                var l1 = linkList[i];
                if (!nodeMap.TryGetValue(l1.SourceNodeId, out var u1) || !nodeMap.TryGetValue(l1.TargetNodeId, out var v1))
                    continue;

                for (int j = i + 1; j < linkList.Count; j++)
                {
                    var l2 = linkList[j];
                    if (!nodeMap.TryGetValue(l2.SourceNodeId, out var u2) || !nodeMap.TryGetValue(l2.TargetNodeId, out var v2))
                        continue;

                    // Skip edges that share an endpoint
                    if (l1.SourceNodeId == l2.SourceNodeId || l1.SourceNodeId == l2.TargetNodeId ||
                        l1.TargetNodeId == l2.SourceNodeId || l1.TargetNodeId == l2.TargetNodeId)
                        continue;

                    // Segment intersection test between (u1, v1) and (u2, v2)
                    double x1 = u1.X, y1 = u1.Y;
                    double x2 = v1.X, y2 = v1.Y;
                    double x3 = u2.X, y3 = u2.Y;
                    double x4 = v2.X, y4 = v2.Y;

                    double d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
                    if (Math.Abs(d) < 1e-6) continue;

                    double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d;
                    double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / d;

                    if (t > 0.001 && t < 0.999 && u > 0.001 && u < 0.999)
                    {
                        double ix = x1 + t * (x2 - x1);
                        double iy = y1 + t * (y2 - y1);
                        string e1Name = $"{u1.Title} -> {v1.Title}";
                        string e2Name = $"{u2.Title} -> {v2.Title}";
                        crossings.Add(new EdgeCrossingItem(
                            l1.Id, l2.Id, e1Name, e2Name, Math.Round(ix, 1), Math.Round(iy, 1),
                            $"Edge crossing at ({ix:F1}, {iy:F1}) between [{e1Name}] and [{e2Name}]"));
                    }
                }
            }

            // Kuratowski / Euler condition check
            bool hasObstruction = false;
            string obstructionType = "None (Planar Graph Topology)";

            if (totalNodes >= 3 && totalLinks > (3 * totalNodes - 6))
            {
                hasObstruction = true;
                obstructionType = "Euler Planarity Violation (E > 3V - 6)";
            }
            else if (totalNodes >= 5)
            {
                // Check for K5 complete graph clique
                var adj = new HashSet<string>();
                foreach (var l in linkList)
                {
                    adj.Add($"{l.SourceNodeId}_{l.TargetNodeId}");
                    adj.Add($"{l.TargetNodeId}_{l.SourceNodeId}");
                }
                var ids = nodeList.Select(n => n.Id).ToList();
                for (int a = 0; a < ids.Count && !hasObstruction; a++)
                for (int b = a + 1; b < ids.Count && !hasObstruction; b++)
                for (int c = b + 1; c < ids.Count && !hasObstruction; c++)
                for (int e = c + 1; e < ids.Count && !hasObstruction; e++)
                for (int f = e + 1; f < ids.Count && !hasObstruction; f++)
                {
                    string[] k5 = { ids[a], ids[b], ids[c], ids[e], ids[f] };
                    int edgeCount = 0;
                    for (int x = 0; x < 5; x++)
                    for (int y = x + 1; y < 5; y++)
                    {
                        if (adj.Contains($"{k5[x]}_{k5[y]}")) edgeCount++;
                    }
                    if (edgeCount == 10)
                    {
                        hasObstruction = true;
                        obstructionType = "K5 Complete Subgraph Minor Detected";
                    }
                }
            }

            double score = 100.0;
            score -= Math.Min(60.0, crossings.Count * 15.0);
            if (hasObstruction) score -= 25.0;
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category;
            if (totalNodes == 0) category = "Empty MindMap";
            else if (score >= 90.0) category = "Optimal Planar Geometry & Zero Edge Crossings (≥90%)";
            else if (score >= 75.0) category = "Good Planar Layout with Minor Segment Overlaps (75-89%)";
            else if (score >= 50.0) category = "Moderate Edge Crossings or Planarity Density (50-74%)";
            else category = "Severe Edge Crossing Mesh & Non-Planar Topology (<50%)";

            string summary = FormatPlanaritySummary(
                totalNodes, totalLinks, crossings.Count, hasObstruction, obstructionType, score, category, isSelection);

            return new GraphPlanarityAudit(
                totalNodes, totalLinks, crossings.Count, hasObstruction, obstructionType,
                score, crossings, category, isSelection, summary);
        }

        public static string FormatPlanaritySummary(
            int totalNodes, int totalLinks, int crossingCount, bool hasObstruction,
            string obstructionType, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph edge crossing & planarity audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            if (crossingCount == 0)
                parts.Add("0 edge crossings (Clean 2D Planar)");
            else
                parts.Add($"⚠ {crossingCount} edge crossing{(crossingCount == 1 ? "" : "s")}");

            if (hasObstruction)
                parts.Add($"⚠ {obstructionType}");
            else
                parts.Add("Planar topology");

            string details = string.Join(" · ", parts);
            return $"Graph edge crossing & planarity audit ({scope}{nodeStr} · {category} · {score:0.#}% planarity score): {details}";
        }
        public GraphArticulationAudit AuditCriticalPathAndArticulationPoints(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = targetNodes.Select(n => n.Id).ToHashSet();
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditCriticalPathAndArticulationPoints(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphArticulationAudit AuditCriticalPathAndArticulationPoints(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            int totalNodes = nodeList.Count;
            int totalLinks = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatArticulationPointsSummary(0, 0, 0, 0, 0, 100.0, "Empty MindMap", isSelection);
                return new GraphArticulationAudit(
                    0, 0, 0, 0, 0, 100.0,
                    Array.Empty<GraphArticulationItem>(), "Empty MindMap", isSelection, emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id, n => n);
            var nodeIndex = new Dictionary<string, int>();
            var indexToNode = new List<string>();

            for (int i = 0; i < nodeList.Count; i++)
            {
                nodeIndex[nodeList[i].Id] = i;
                indexToNode.Add(nodeList[i].Id);
            }

            // Build adjacency list with edge tracking (handling multi-edges)
            var adj = new List<List<(int Target, int EdgeId)>>();
            for (int i = 0; i < totalNodes; i++) adj.Add(new List<(int Target, int EdgeId)>());

            var edgeDescriptions = new Dictionary<int, (string U, string V, string Label)>();
            int validEdgeCount = 0;

            for (int e = 0; e < linkList.Count; e++)
            {
                var link = linkList[e];
                if (!nodeIndex.TryGetValue(link.SourceNodeId, out int u) || !nodeIndex.TryGetValue(link.TargetNodeId, out int v))
                    continue;
                if (u == v) continue; // ignore self-loops

                adj[u].Add((v, validEdgeCount));
                adj[v].Add((u, validEdgeCount));
                edgeDescriptions[validEdgeCount] = (link.SourceNodeId, link.TargetNodeId, $"{link.SourceNodeId} <-> {link.TargetNodeId}");
                validEdgeCount++;
            }

            // Tarjan's Biconnectivity DFS
            int timer = 0;
            var disc = new int[totalNodes];
            var low = new int[totalNodes];
            var visited = new bool[totalNodes];
            var isArticulation = new bool[totalNodes];
            var removalComponentCount = new int[totalNodes];
            for (int i = 0; i < totalNodes; i++)
            {
                disc[i] = -1;
                low[i] = -1;
                removalComponentCount[i] = 1;
            }

            var bridgeEdges = new HashSet<int>();
            int connectedComponents = 0;

            for (int i = 0; i < totalNodes; i++)
            {
                if (!visited[i])
                {
                    connectedComponents++;
                    Dfs(i, -1);
                }
            }

            void Dfs(int u, int parentEdge)
            {
                visited[u] = true;
                disc[u] = low[u] = ++timer;
                int children = 0;
                int cutBranches = 0;

                foreach (var edge in adj[u])
                {
                    int v = edge.Target;
                    int edgeId = edge.EdgeId;

                    if (edgeId == parentEdge) continue;

                    if (visited[v])
                    {
                        if (disc[v] < low[u])
                        {
                            low[u] = disc[v];
                        }
                    }
                    else
                    {
                        children++;
                        Dfs(v, edgeId);

                        if (low[v] < low[u])
                        {
                            low[u] = low[v];
                        }

                        // Bridge check: low[v] > disc[u]
                        if (low[v] > disc[u])
                        {
                            bridgeEdges.Add(edgeId);
                        }

                        // Articulation check for non-root: low[v] >= disc[u]
                        if (parentEdge != -1 && low[v] >= disc[u])
                        {
                            isArticulation[u] = true;
                            cutBranches++;
                        }
                    }
                }

                // Articulation check for root
                if (parentEdge == -1)
                {
                    if (children > 1)
                    {
                        isArticulation[u] = true;
                        removalComponentCount[u] = children;
                    }
                }
                else if (isArticulation[u])
                {
                    removalComponentCount[u] = cutBranches + 1;
                }
            }

            var bridgeLabels = new Dictionary<string, List<string>>();
            foreach (var bId in bridgeEdges)
            {
                if (edgeDescriptions.TryGetValue(bId, out var desc))
                {
                    if (!bridgeLabels.TryGetValue(desc.U, out var listU))
                    {
                        listU = new List<string>();
                        bridgeLabels[desc.U] = listU;
                    }
                    listU.Add(desc.Label);

                    if (!bridgeLabels.TryGetValue(desc.V, out var listV))
                    {
                        listV = new List<string>();
                        bridgeLabels[desc.V] = listV;
                    }
                    listV.Add(desc.Label);
                }
            }

            var items = new List<GraphArticulationItem>();
            int articulationCount = 0;

            for (int i = 0; i < totalNodes; i++)
            {
                string id = indexToNode[i];
                var node = nodeMap[id];
                bool isArt = isArticulation[i];
                if (isArt) articulationCount++;

                int degree = adj[i].Count;
                int splitCount = removalComponentCount[i];
                var incidentBridges = bridgeLabels.TryGetValue(id, out var bList) ? (IReadOnlyList<string>)bList : Array.Empty<string>();

                string severity = isArt
                    ? (splitCount >= 3 ? "Critical" : "Moderate")
                    : (incidentBridges.Count > 0 ? "Moderate" : "Normal");

                string status = isArt
                    ? $"Articulation Cut Vertex: Removal disconnects network into {splitCount} components"
                    : incidentBridges.Count > 0
                        ? $"Bridge Endpoint: Incident to {incidentBridges.Count} bottleneck bridge link(s)"
                        : "Biconnected / Redundant (Safe Node)";

                items.Add(new GraphArticulationItem(
                    id, node.Title ?? id, isArt, degree, splitCount, incidentBridges, severity, status));
            }

            int bridgeCount = bridgeEdges.Count;
            double score = 100.0;
            score -= articulationCount * 15.0;
            score -= bridgeCount * 10.0;
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category;
            if (totalNodes == 0) category = "Empty MindMap";
            else if (score >= 90.0) category = "High Structural Resilience & Redundancy (≥90%)";
            else if (score >= 75.0) category = "Good Network Connectivity with Minor Bridge Points (75-89%)";
            else if (score >= 50.0) category = "Moderate Bottleneck Hazards & Cut Vertices (50-74%)";
            else category = "Fragile Topology with Critical Articulation Failure Points (<50%)";

            string summary = FormatArticulationPointsSummary(
                totalNodes, totalLinks, articulationCount, bridgeCount, connectedComponents, score, category, isSelection);

            return new GraphArticulationAudit(
                totalNodes, totalLinks, articulationCount, bridgeCount, connectedComponents,
                score, items, category, isSelection, summary);
        }

        public static string FormatArticulationPointsSummary(
            int totalNodes, int totalLinks, int articulationCount, int bridgeCount,
            int components, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Graph articulation & resilience audit ({scope}0 nodes): Empty mind map";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            if (articulationCount == 0)
                parts.Add("0 cut vertices (High Biconnectivity)");
            else
                parts.Add($"⚠️ {articulationCount} articulation cut vertice{(articulationCount == 1 ? "" : "s")}");

            if (bridgeCount == 0)
                parts.Add("0 bridge bottlenecks");
            else
                parts.Add($"⚠️ {bridgeCount} bridge link{(bridgeCount == 1 ? "" : "s")}");

            if (components > 1)
                parts.Add($"{components} disconnected clusters");

            string details = string.Join(" · ", parts);
            return $"Graph articulation & resilience audit ({scope}{nodeStr} · {category} · {score:0.#}% resilience score): {details}";
        }

        public GraphDegreeDistributionCentralityAudit AuditGraphDegreeDistributionAndHubCentrality(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = targetNodes.Select(n => n.Id).ToHashSet();
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditGraphDegreeDistributionAndHubCentrality(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphDegreeDistributionCentralityAudit AuditGraphDegreeDistributionAndHubCentrality(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            int totalNodes = nodeList.Count;
            int totalLinks = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatHubCentralitySummary(0, 0, 0, 0, 0, 0.0, 0.0, 0.0, 0.0, 100.0, "Empty Galaxy", isSelection);
                return new GraphDegreeDistributionCentralityAudit(
                    0, 0, 0, 0, 0, 0.0, 0.0, 0.0, 0.0, 100.0,
                    Array.Empty<GraphDegreeCentralityItem>(), "Empty Galaxy", isSelection, emptySummary);
            }

            var inDegrees = new Dictionary<string, int>();
            var outDegrees = new Dictionary<string, int>();
            foreach (var node in nodeList)
            {
                inDegrees[node.Id] = 0;
                outDegrees[node.Id] = 0;
            }

            var validNodeIds = nodeList.Select(n => n.Id).ToHashSet();
            foreach (var link in linkList)
            {
                if (validNodeIds.Contains(link.SourceNodeId) && validNodeIds.Contains(link.TargetNodeId))
                {
                    outDegrees[link.SourceNodeId]++;
                    inDegrees[link.TargetNodeId]++;
                }
            }

            var totalDegrees = new Dictionary<string, int>();
            double sumDegree = 0;
            int maxDegree = 0;
            foreach (var node in nodeList)
            {
                int deg = inDegrees[node.Id] + outDegrees[node.Id];
                totalDegrees[node.Id] = deg;
                sumDegree += deg;
                if (deg > maxDegree) maxDegree = deg;
            }

            double avgDegree = Math.Round(sumDegree / totalNodes, 2);

            // Compute standard deviation
            double varianceSum = 0;
            foreach (var node in nodeList)
            {
                double diff = totalDegrees[node.Id] - avgDegree;
                varianceSum += diff * diff;
            }
            double stdDev = Math.Round(Math.Sqrt(varianceSum / totalNodes), 2);

            // Hub threshold: > avg + 2*stdDev (or >= 4 if stdDev is small)
            double hubThreshold = Math.Max(4.0, avgDegree + (2.0 * stdDev));

            int hubCount = 0;
            int isolateCount = 0;
            int leafCount = 0;

            var items = new List<GraphDegreeCentralityItem>();
            foreach (var node in nodeList)
            {
                int inD = inDegrees[node.Id];
                int outD = outDegrees[node.Id];
                int totalD = totalDegrees[node.Id];

                bool isHub = totalD >= hubThreshold;
                bool isIsolate = totalD == 0;
                bool isLeaf = totalD == 1;

                if (isHub) hubCount++;
                if (isIsolate) isolateCount++;
                if (isLeaf) leafCount++;

                double centrality = totalNodes > 1 ? Math.Round((double)totalD / (totalNodes - 1), 3) : 1.0;
                string status = isHub ? "Super-Hub node" : isIsolate ? "Isolated concept" : isLeaf ? "Terminal leaf node" : "Standard branching node";

                items.Add(new GraphDegreeCentralityItem(
                    node.Id, node.Title ?? node.Id, inD, outD, totalD,
                    isHub, isIsolate, isLeaf, centrality, status));
            }

            // Power-law exponent gamma approximation
            double gamma = 2.5;
            var nonZeroDegrees = totalDegrees.Values.Where(d => d > 0).ToList();
            if (nonZeroDegrees.Count > 1)
            {
                int kMin = nonZeroDegrees.Min();
                double lnSum = nonZeroDegrees.Sum(d => Math.Log((double)d / Math.Max(0.5, kMin - 0.5)));
                if (lnSum > 0.001)
                {
                    gamma = Math.Round(1.0 + (nonZeroDegrees.Count / lnSum), 2);
                }
            }

            double score = 100.0;
            score -= isolateCount * 15.0;
            if (hubCount == 0 && totalNodes > 5) score -= 10.0;
            if (maxDegree > totalNodes * 0.8 && totalNodes > 4) score -= 15.0;
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category;
            if (totalNodes == 0) category = "Empty Galaxy";
            else if (score >= 90.0) category = "Optimal Scale-Free Hierarchy (≥90%)";
            else if (score >= 75.0) category = "Balanced Hierarchical Galaxy (75-89%)";
            else if (score >= 50.0) category = "Clustered Multi-Hub Network (50-74%)";
            else category = "Fragmented Graph with Isolates (<50%)";

            string summary = FormatHubCentralitySummary(
                totalNodes, totalLinks, hubCount, isolateCount, leafCount,
                avgDegree, maxDegree, stdDev, gamma, score, category, isSelection);

            return new GraphDegreeDistributionCentralityAudit(
                totalNodes, totalLinks, hubCount, isolateCount, leafCount,
                avgDegree, maxDegree, stdDev, gamma, score, items, category, isSelection, summary);
        }

        public static string FormatHubCentralitySummary(
            int totalNodes, int totalEdges, int hubCount, int isolateCount, int leafCount,
            double avgDegree, double maxDegree, double stdDev, double gamma,
            double score, string category, bool isSelection = false)
        {
            if (totalNodes == 0) return "Graph degree distribution audit: Empty galaxy · 0 nodes detected";

            string scope = isSelection ? "Selection · " : "";
            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>();

            parts.Add($"{totalEdges} link{(totalEdges == 1 ? "" : "s")}");
            parts.Add($"avg degree {avgDegree:F1} (σ={stdDev:F1})");
            parts.Add($"max degree {maxDegree}");
            if (hubCount > 0) parts.Add($"{hubCount} hub{(hubCount == 1 ? "" : "s")}");
            if (leafCount > 0) parts.Add($"{leafCount} leaf node{(leafCount == 1 ? "" : "s")}");
            if (isolateCount > 0) parts.Add($"⚠️ {isolateCount} isolate{(isolateCount == 1 ? "" : "s")}");
            parts.Add($"γ={gamma:F1}");

            string details = string.Join(" · ", parts);
            return $"Graph degree distribution & hub centrality audit ({scope}{nodeStr} · {details} · {category} · {score:0.#}% balance score)";
        }

        public GraphMinimumSpanningTreeAndBackboneAudit AuditGraphMinimumSpanningTreeAndBackbone(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = targetNodes.Select(n => n.Id).ToHashSet();
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditGraphMinimumSpanningTreeAndBackbone(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphMinimumSpanningTreeAndBackboneAudit AuditGraphMinimumSpanningTreeAndBackbone(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();

            int totalNodes = nodeList.Count;
            int totalLinks = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatMstBackboneSummary(0, 0, 0, 0, 0, 0, 0.0, 0.0, 1.0, 100.0, "Empty Galaxy", isSelection);
                return new GraphMinimumSpanningTreeAndBackboneAudit(
                    0, 0, 0, 0, 0, 0, 0.0, 0.0, 1.0, 100.0,
                    Array.Empty<MstBackboneEdgeItem>(), "Empty Galaxy", isSelection, emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id);
            var validNodeIds = nodeMap.Keys.ToHashSet();

            // Calculate Euclidean distance for each valid link
            var edgeCandidates = new List<(MindMapLink Link, MindMapNode Src, MindMapNode Tgt, double Dist)>();
            double totalWeight = 0.0;
            var degreeMap = validNodeIds.ToDictionary(id => id, _ => 0);

            foreach (var link in linkList)
            {
                if (validNodeIds.Contains(link.SourceNodeId) && validNodeIds.Contains(link.TargetNodeId))
                {
                    var src = nodeMap[link.SourceNodeId];
                    var tgt = nodeMap[link.TargetNodeId];

                    double srcX = src.X + (src.Width / 2.0);
                    double srcY = src.Y + (src.Height / 2.0);
                    double tgtX = tgt.X + (tgt.Width / 2.0);
                    double tgtY = tgt.Y + (tgt.Height / 2.0);

                    double dx = tgtX - srcX;
                    double dy = tgtY - srcY;
                    double dist = Math.Round(Math.Sqrt((dx * dx) + (dy * dy)), 1);

                    edgeCandidates.Add((link, src, tgt, dist));
                    totalWeight += dist;
                    degreeMap[link.SourceNodeId]++;
                    degreeMap[link.TargetNodeId]++;
                }
            }

            int isolateCount = degreeMap.Values.Count(d => d == 0);

            // Kruskal's Disjoint Set Union
            var parent = validNodeIds.ToDictionary(id => id, id => id);
            string Find(string i)
            {
                if (parent[i] == i) return i;
                return parent[i] = Find(parent[i]);
            }
            bool Union(string i, string j)
            {
                string rootI = Find(i);
                string rootJ = Find(j);
                if (rootI == rootJ) return false;
                parent[rootI] = rootJ;
                return true;
            }

            // Sort edges ascending by Euclidean distance
            edgeCandidates.Sort((a, b) => a.Dist.CompareTo(b.Dist));

            var mstEdges = new List<(MindMapLink Link, MindMapNode Src, MindMapNode Tgt, double Dist)>();
            var redundantCycleEdges = new List<(MindMapLink Link, MindMapNode Src, MindMapNode Tgt, double Dist)>();
            double mstWeight = 0.0;

            foreach (var cand in edgeCandidates)
            {
                if (Union(cand.Src.Id, cand.Tgt.Id))
                {
                    mstEdges.Add(cand);
                    mstWeight += cand.Dist;
                }
                else
                {
                    redundantCycleEdges.Add(cand);
                }
            }

            // Count connected components
            int componentCount = validNodeIds.Select(Find).Distinct().Count();

            // Degree in MST to classify backbone vs foliage
            var mstDegree = validNodeIds.ToDictionary(id => id, _ => 0);
            foreach (var m in mstEdges)
            {
                mstDegree[m.Src.Id]++;
                mstDegree[m.Tgt.Id]++;
            }

            var items = new List<MstBackboneEdgeItem>();
            foreach (var m in mstEdges)
            {
                bool isTrunk = mstDegree[m.Src.Id] > 1 && mstDegree[m.Tgt.Id] > 1;
                string status = isTrunk ? "Backbone Trunk Corridor" : "Peripheral Foliage Branch";
                items.Add(new MstBackboneEdgeItem(
                    m.Src.Id, m.Src.Title, m.Tgt.Id, m.Tgt.Title, m.Dist,
                    IsMstEdge: true, IsBackboneTrunk: isTrunk, IsRedundantCycle: false, status));
            }

            foreach (var c in redundantCycleEdges)
            {
                items.Add(new MstBackboneEdgeItem(
                    c.Src.Id, c.Src.Title, c.Tgt.Id, c.Tgt.Title, c.Dist,
                    IsMstEdge: false, IsBackboneTrunk: false, IsRedundantCycle: true, "Redundant Cycle Bridge"));
            }

            double backboneRatio = totalWeight > 0.0 ? Math.Round(mstWeight / totalWeight, 3) : 1.0;

            double score = 100.0;
            score -= redundantCycleEdges.Count * 5.0;
            score -= (componentCount - 1) * 8.0;
            score -= isolateCount * 10.0;
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category;
            if (score >= 95.0) category = "Pristine Spanning Backbone (≥95%)";
            else if (score >= 80.0) category = "Efficient Spanning Tree (80-94%)";
            else if (score >= 60.0) category = "Moderate Cycle Redundancy (60-79%)";
            else if (score >= 40.0) category = "High Cross-Link Clutter (40-59%)";
            else category = "Disconnected / Redundant Spaghetti (<40%)";

            string summary = FormatMstBackboneSummary(
                totalNodes, totalLinks, mstEdges.Count, redundantCycleEdges.Count,
                componentCount, isolateCount, totalWeight, mstWeight, backboneRatio, score, category, isSelection);

            return new GraphMinimumSpanningTreeAndBackboneAudit(
                totalNodes, totalLinks, mstEdges.Count, redundantCycleEdges.Count,
                componentCount, isolateCount, totalWeight, mstWeight, backboneRatio, score,
                items, category, isSelection, summary);
        }

        public GraphCycleFeedbackAudit AuditGraphCyclesAndFeedbackArcSet(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = new HashSet<string>(targetNodes.Select(n => n.Id));
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditGraphCyclesAndFeedbackArcSet(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphCycleFeedbackAudit AuditGraphCyclesAndFeedbackArcSet(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();
            int totalNodes = nodeList.Count;
            int totalEdges = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatCycleFeedbackSummary(0, 0, 0, 0, true, 100.0, "Empty Galaxy", isSelection);
                return new GraphCycleFeedbackAudit(
                    0, 0, 0, 0, true, 0, 100.0,
                    Array.Empty<GraphCycleItem>(), Array.Empty<string>(), "Empty Galaxy", isSelection, emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id, n => n.Title ?? n.Id);
            var adj = new Dictionary<string, List<(string TargetId, string LinkId)>>();
            foreach (var node in nodeList)
            {
                adj[node.Id] = new List<(string TargetId, string LinkId)>();
            }
            foreach (var link in linkList)
            {
                if (adj.ContainsKey(link.SourceNodeId) && adj.ContainsKey(link.TargetNodeId))
                {
                    adj[link.SourceNodeId].Add((link.TargetNodeId, link.Id));
                }
            }

            var state = new Dictionary<string, int>();
            foreach (var n in nodeList) state[n.Id] = 0;

            var cycles = new List<GraphCycleItem>();
            var fasSet = new HashSet<string>();
            var path = new List<string>();
            int maxCycleLength = 0;

            void Dfs(string u)
            {
                state[u] = 1;
                path.Add(u);

                if (adj.TryGetValue(u, out var neighbors))
                {
                    foreach (var (v, linkId) in neighbors)
                    {
                        if (state.TryGetValue(v, out int vState))
                        {
                            if (vState == 1)
                            {
                                int idx = path.IndexOf(v);
                                if (idx >= 0)
                                {
                                    var cycleNodes = path.Skip(idx).Concat(new[] { v }).ToList();
                                    int cycleLen = cycleNodes.Count - 1;
                                    if (cycleLen > maxCycleLength) maxCycleLength = cycleLen;

                                    string uTitle = nodeMap.TryGetValue(u, out var ut) ? ut : u;
                                    string vTitle = nodeMap.TryGetValue(v, out var vt) ? vt : v;
                                    string fasEdge = $"{uTitle} -> {vTitle}";
                                    fasSet.Add(fasEdge);

                                    string seq = string.Join(" -> ", cycleNodes.Select(nid => nodeMap.TryGetValue(nid, out var t) ? t : nid));
                                    cycles.Add(new GraphCycleItem(
                                        cycles.Count + 1,
                                        cycleLen,
                                        seq,
                                        fasEdge,
                                        "Cyclic Loop",
                                        $"Reverse or remove back-edge '{fasEdge}' to restore directed hierarchy"));
                                }
                            }
                            else if (vState == 0)
                            {
                                Dfs(v);
                            }
                        }
                    }
                }

                path.RemoveAt(path.Count - 1);
                state[u] = 2;
            }

            foreach (var node in nodeList)
            {
                if (state[node.Id] == 0)
                {
                    Dfs(node.Id);
                }
            }

            bool isDag = cycles.Count == 0;
            double score = 100.0;
            if (!isDag)
            {
                score -= cycles.Count * 20.0;
                score -= fasSet.Count * 10.0;
                score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));
            }

            string category;
            if (isDag) category = "Acyclic Graph (100% DAG Compliant)";
            else if (score >= 80.0) category = "High Acyclic Structure (80-94%)";
            else if (score >= 60.0) category = "Moderate Cyclic Dependency (60-79%)";
            else if (score >= 40.0) category = "High Cyclic Entanglement (40-59%)";
            else category = "Severe Cyclic Recursion (<40%)";

            string summary = FormatCycleFeedbackSummary(
                totalNodes, totalEdges, cycles.Count, fasSet.Count, isDag, score, category, isSelection);

            return new GraphCycleFeedbackAudit(
                totalNodes, totalEdges, cycles.Count, fasSet.Count, isDag, maxCycleLength,
                score, cycles, fasSet.ToList(), category, isSelection, summary);
        }

        public static string FormatCycleFeedbackSummary(
            int totalNodes, int totalEdges, int cycleCount, int fasCount,
            bool isDag, double score, string category, bool isSelection)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0)
                return $"Mind Map cycle audit: {scope}Empty galaxy · 0 nodes";

            if (isDag)
                return $"Mind Map cycle audit: {scope}✓ Acyclic DAG · {totalNodes} nodes · {totalEdges} edges · 0 cycles · 100% DAG compliant";

            var parts = new List<string>
            {
                $"{totalNodes} nodes",
                $"{totalEdges} edges",
                $"⚠️ {cycleCount} cycles",
                $"FAS {fasCount} edges"
            };

            string details = string.Join(" · ", parts);
            return $"Mind Map cycle audit ({scope}{details} · {category} · {score:0.#}% DAG score)";
        }

        public GraphBiconnectedComponentsAudit AuditGraphArticulationPointsAndBiconnectedComponents(bool selectionOnly = false)
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            var targetNodes = (selectionOnly && selected.Count > 0)
                ? selected
                : Nodes.ToList();

            bool isSelection = selectionOnly && selected.Count > 0;
            var nodeIds = new HashSet<string>(targetNodes.Select(n => n.Id));
            var relevantLinks = Links.Where(l => nodeIds.Contains(l.SourceNodeId) && nodeIds.Contains(l.TargetNodeId)).ToList();

            var audit = AuditGraphArticulationPointsAndBiconnectedComponents(targetNodes.Select(n => n.Model), relevantLinks.Select(l => l.Model), isSelection);
            StatusMessage = audit.Summary;
            return audit;
        }

        public static GraphBiconnectedComponentsAudit AuditGraphArticulationPointsAndBiconnectedComponents(
            IEnumerable<MindMapNode>? nodes,
            IEnumerable<MindMapLink>? links,
            bool isSelection = false)
        {
            var nodeList = nodes?.ToList() ?? new List<MindMapNode>();
            var linkList = links?.ToList() ?? new List<MindMapLink>();
            int totalNodes = nodeList.Count;
            int totalEdges = linkList.Count;

            if (totalNodes == 0)
            {
                string emptySummary = FormatBiconnectedComponentsSummary(0, 0, 0, 0, 0, 0, 100.0, "Empty Galaxy", isSelection);
                return new GraphBiconnectedComponentsAudit(
                    0, 0, 0, 0, 0, 0, 100.0,
                    Array.Empty<GraphArticulationPointItem>(),
                    Array.Empty<GraphBiconnectedBlockItem>(),
                    "Empty Galaxy", isSelection, emptySummary);
            }

            var nodeMap = nodeList.ToDictionary(n => n.Id, n => n);
            var adj = new Dictionary<string, List<string>>();
            foreach (var n in nodeList) adj[n.Id] = new List<string>();

            var edgeSet = new HashSet<(string U, string V)>();
            foreach (var l in linkList)
            {
                if (adj.ContainsKey(l.SourceNodeId) && adj.ContainsKey(l.TargetNodeId) && l.SourceNodeId != l.TargetNodeId)
                {
                    adj[l.SourceNodeId].Add(l.TargetNodeId);
                    adj[l.TargetNodeId].Add(l.SourceNodeId);

                    string u = string.CompareOrdinal(l.SourceNodeId, l.TargetNodeId) < 0 ? l.SourceNodeId : l.TargetNodeId;
                    string v = string.CompareOrdinal(l.SourceNodeId, l.TargetNodeId) < 0 ? l.TargetNodeId : l.SourceNodeId;
                    edgeSet.Add((u, v));
                }
            }

            var tin = new Dictionary<string, int>();
            var low = new Dictionary<string, int>();
            var cutVertices = new HashSet<string>();
            var bridges = new HashSet<(string U, string V)>();
            var edgeStack = new Stack<(string U, string V)>();
            var blocks = new List<GraphBiconnectedBlockItem>();
            int timer = 0;
            int componentCount = 0;

            void Dfs(string u, string? parent)
            {
                tin[u] = low[u] = ++timer;
                int children = 0;

                foreach (var v in adj[u])
                {
                    if (v == parent) continue;

                    if (tin.ContainsKey(v))
                    {
                        low[u] = Math.Min(low[u], tin[v]);
                        if (tin[v] < tin[u])
                        {
                            edgeStack.Push((u, v));
                        }
                    }
                    else
                    {
                        edgeStack.Push((u, v));
                        children++;
                        Dfs(v, u);
                        low[u] = Math.Min(low[u], low[v]);

                        if (low[v] >= tin[u])
                        {
                            if (parent != null || children > 1)
                            {
                                cutVertices.Add(u);
                            }

                            var blockNodes = new HashSet<string> { u };
                            int blockEdges = 0;
                            while (edgeStack.Count > 0)
                            {
                                var edge = edgeStack.Pop();
                                blockNodes.Add(edge.U);
                                blockNodes.Add(edge.V);
                                blockEdges++;
                                if (edge.U == u && edge.V == v) break;
                            }
                            blocks.Add(new GraphBiconnectedBlockItem(
                                blocks.Count + 1, blockNodes.Count, blockEdges,
                                blockNodes.ToList(), blockNodes.Count <= 2));
                        }

                        if (low[v] > tin[u])
                        {
                            string bu = string.CompareOrdinal(u, v) < 0 ? u : v;
                            string bv = string.CompareOrdinal(u, v) < 0 ? v : u;
                            bridges.Add((bu, bv));
                        }
                    }
                }
            }

            foreach (var n in nodeList)
            {
                if (!tin.ContainsKey(n.Id))
                {
                    componentCount++;
                    Dfs(n.Id, null);

                    if (adj[n.Id].Count == 0)
                    {
                        blocks.Add(new GraphBiconnectedBlockItem(
                            blocks.Count + 1, 1, 0, new[] { n.Id }, true));
                    }
                }
            }

            var articulationItems = new List<GraphArticulationPointItem>();
            int internalNodes = nodeList.Count(n => adj[n.Id].Count > 1);

            foreach (var n in nodeList)
            {
                bool isCut = cutVertices.Contains(n.Id);
                int degree = adj[n.Id].Count;
                int incidentBridges = adj[n.Id].Count(neighbor =>
                {
                    string bu = string.CompareOrdinal(n.Id, neighbor) < 0 ? n.Id : neighbor;
                    string bv = string.CompareOrdinal(n.Id, neighbor) < 0 ? neighbor : n.Id;
                    return bridges.Contains((bu, bv));
                });
                int blockCount = blocks.Count(b => b.NodeIds.Contains(n.Id));

                string severity = isCut
                    ? (blockCount > 3 || incidentBridges >= 2 ? "Critical" : (blockCount > 2 ? "High" : "Medium"))
                    : "None";
                string status = isCut ? "Articulation Cut Vertex" : (degree == 0 ? "Isolated Node" : (degree == 1 ? "Leaf Node" : "Standard Internal Node"));
                string rec = isCut
                    ? $"Add redundant cross-link bypassing node '{n.Title}' to prevent graph partition"
                    : "Node has resilient multi-path connectivity";

                articulationItems.Add(new GraphArticulationPointItem(
                    n.Id, n.Title ?? n.Id, isCut, degree, incidentBridges, blockCount, severity, status, rec));
            }

            double score = 100.0;
            if (totalNodes > 2)
            {
                if (internalNodes > 0)
                {
                    double ratio = (double)cutVertices.Count / internalNodes;
                    score -= ratio * 50.0;
                }
                score -= bridges.Count * 8.0;
            }
            score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

            string category;
            if (cutVertices.Count == 0 && bridges.Count == 0) category = "High Structural Redundancy (≥95%)";
            else if (score >= 80.0) category = "Good Structural Resilience (80-94%)";
            else if (score >= 60.0) category = "Moderate Network Vulnerability (60-79%)";
            else if (score >= 40.0) category = "High Partition Risk (40-59%)";
            else category = "Critical Single Point of Failure (<40%)";

            string summary = FormatBiconnectedComponentsSummary(
                totalNodes, edgeSet.Count, cutVertices.Count, bridges.Count,
                blocks.Count, componentCount, score, category, isSelection);

            return new GraphBiconnectedComponentsAudit(
                totalNodes, edgeSet.Count, cutVertices.Count, bridges.Count,
                blocks.Count, componentCount, score,
                articulationItems, blocks, category, isSelection, summary);
        }

        public static string FormatBiconnectedComponentsSummary(
            int totalNodes, int totalEdges, int cutVertices, int bridges,
            int blocks, int components, double score, string category, bool isSelection = false)
        {
            string scope = isSelection ? "Selection · " : "";
            if (totalNodes == 0) return $"Mind Map biconnectivity audit: {scope}Empty galaxy · 0 nodes";

            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>
            {
                $"{nodeStr}",
                $"{totalEdges} edge{(totalEdges == 1 ? "" : "s")}"
            };

            if (cutVertices == 0)
                parts.Add("0 cut vertices");
            else
                parts.Add($"⚠️ {cutVertices} cut vertice{(cutVertices == 1 ? "" : "s")}");

            if (bridges > 0)
                parts.Add($"⚠️ {bridges} bridge{(bridges == 1 ? "" : "s")}");

            parts.Add($"{blocks} block{(blocks == 1 ? "" : "s")}");
            if (components > 1)
                parts.Add($"{components} components");

            string details = string.Join(" · ", parts);
            return $"Mind Map biconnectivity & cut vertices audit ({scope}{details} · {category} · {score:0.#}% redundancy score)";
        }


        public static string FormatMstBackboneSummary(
            int totalNodes, int totalEdges, int mstEdges, int cycleEdges,
            int components, int isolates, double totalLen, double mstLen,
            double ratio, double score, string category, bool isSelection = false)
        {
            if (totalNodes == 0) return "Graph minimum spanning tree audit: Empty galaxy · 0 nodes detected";

            string scope = isSelection ? "Selection · " : "";
            string nodeStr = totalNodes == 1 ? "1 node" : $"{totalNodes} nodes";
            var parts = new List<string>
            {
                $"{totalEdges} link{(totalEdges == 1 ? "" : "s")}",
                $"{mstEdges} MST backbone edge{(mstEdges == 1 ? "" : "s")}"
            };

            if (cycleEdges > 0) parts.Add($"⚠️ {cycleEdges} cycle bridge{(cycleEdges == 1 ? "" : "s")}");
            if (components > 1) parts.Add($"{components} components");
            if (isolates > 0) parts.Add($"⚠️ {isolates} isolate{(isolates == 1 ? "" : "s")}");
            parts.Add($"MST len {mstLen:F0}px / Tot {totalLen:F0}px (ratio {ratio:P0})");

            string details = string.Join(" · ", parts);
            return $"Graph minimum spanning tree & backbone audit ({scope}{nodeStr} · {details} · {category} · {score:0.#}% efficiency score)";
        }

    }


    /// <summary>
    /// Represents an edge in a 2D minimum spanning tree wireframe.
    /// </summary>
    public sealed record MstEdgeInfo(
        string SourceId,
        string SourceTitle,
        string TargetId,
        string TargetTitle,
        double Distance);

    /// <summary>
    /// Represents the result of a 2D Minimum Spanning Tree (MST) spatial wireframe audit.
    /// </summary>
    public sealed record MinimumSpanningTreeAudit(
        int TotalEdges,
        int TotalNodes,
        double TotalWireLength,
        double AverageEdgeLength,
        double MaxEdgeLength,
        int TrunkCorridorCount,
        IReadOnlyList<MstEdgeInfo> Edges,
        IReadOnlyList<string> TrunkNodeTitles,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents 2D spatial convex hull and bounding envelope metrics for a tag cluster.
    /// </summary>
    public sealed record TagClusterHullInfo(
        string Tag,
        int NodeCount,
        double HullArea,
        double HullPerimeter,
        double CentroidX,
        double CentroidY,
        double BoundingWidth,
        double BoundingHeight,
        double Density);

    /// <summary>
    /// Represents the result of a spatial convex hull analysis across clusters in the mind map.
    /// </summary>
    public sealed record ClusterConvexHullAudit(
        int TotalClusters,
        int TotalNodes,
        string LargestClusterTag,
        double MaxClusterArea,
        IReadOnlyList<TagClusterHullInfo> Clusters,
        string Summary);

    /// <summary>
    /// Represents the result of an orphaned node and cluster proximity audit in the mind map.
    /// </summary>
    public sealed record OrphanedNodeAudit(
        int TotalNodes,
        int OrphanCount,
        int ConnectedCount,
        IReadOnlyList<string> OrphanTitles,
        double AvgDistanceToNearestCluster,
        string Summary);

    /// <summary>
    /// Represents the result of a topological depth and level hierarchy analysis of the mind map.
    /// </summary>
    public sealed record TopologicalDepthAudit(
        int TotalNodes,
        int RootCount,
        int MaxDepth,
        double AverageDepth,
        IReadOnlyDictionary<int, int> LevelCounts,
        int WidestLevel,
        int MaxWidth,
        string Summary);

    /// <summary>
    /// Supported text casing normalization modes for mind map nodes.
    /// </summary>
    public enum NodeCasingMode
    {
        TitleCase,
        UpperCase,
        LowerCase,
        SentenceCase
    }

    /// <summary>
    /// Represents degree centrality metrics and role classification for an individual node.
    /// </summary>
    public sealed record NodeDegreeInfo(
        string NodeId,
        string Title,
        int InDegree,
        int OutDegree,
        int TotalDegree,
        string Role);

    /// <summary>
    /// Represents the result of a graph degree centrality and node hub audit.
    /// </summary>
    public sealed record DegreeCentralityAudit(
        int TotalNodes,
        int TotalLinks,
        double AverageDegree,
        int MaxDegree,
        string TopHubTitle,
        int LeafNodeCount,
        int OrphanNodeCount,
        IReadOnlyList<NodeDegreeInfo> NodeDegrees,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents the result of a leaf node termination depth and periphery eccentricity audit.
    /// </summary>
    public sealed record PeripheryEccentricityAudit(
        int TotalNodes,
        int LeafNodeCount,
        int MaxEccentricity,
        double AverageLeafDepth,
        IReadOnlyList<string> DeepestLeafTitles,
        int PeripheralNodeCount,
        string EccentricityCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents polar coordinates and concentric ring band info for an individual node relative to root.
    /// </summary>
    public sealed record RadialNodePolarInfo(
        string NodeId,
        string Title,
        double Radius,
        double AngleDegrees,
        int RingBand);

    /// <summary>
    /// Represents the result of a radial layout concentric ring and angular spacing uniformity audit.
    /// </summary>
    public sealed record RadialAngularSpacingAudit(
        int TotalNodes,
        int BranchCount,
        double AverageGapDegrees,
        double MinGapDegrees,
        double MaxGapDegrees,
        double GapStandardDeviation,
        IReadOnlyList<RadialNodePolarInfo> PolarNodes,
        string DispersionCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents geometric and connectivity metrics for an individual semantic or tag cluster.
    /// </summary>
    public sealed record ClusterMetricInfo(
        string Tag,
        int NodeCount,
        int InternalEdgeCount,
        double CentroidX,
        double CentroidY,
        double MeanRadius,
        double Density);

    /// <summary>
    /// Represents the result of a semantic tag cluster cohesion, centroid dispersion, and density audit.
    /// </summary>
    public sealed record ClusterCohesionAudit(
        int TotalNodes,
        int ClusteredNodes,
        int OrphanNodes,
        int ClusterCount,
        double AverageClusterRadius,
        double GlobalCohesionScore,
        IReadOnlyList<ClusterMetricInfo> Clusters,
        string CohesionCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents the result of a graph bipartite 2-coloring and odd cycle partition audit.
    /// </summary>
    public sealed record BipartitePartitionAudit(
        int TotalNodes,
        int TotalLinks,
        bool IsBipartite,
        int SetACount,
        int SetBCount,
        double PartitionBalance,
        string OddCycleDescription,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents the result of a Directed Acyclic Graph (DAG) topological sort and sequence depth audit.
    /// </summary>
    public sealed record TopologicalOrderAudit(
        int TotalNodes,
        int TotalLinks,
        bool IsAcyclic,
        int MaxSequenceDepth,
        int EntryPointsCount,
        int TerminalSinksCount,
        IReadOnlyList<string> OrderedNodeTitles,
        string CycleDescription,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents the result of a mindmap graph bridge links, articulation points (cut vertices),
    /// and structural vulnerability audit.
    /// </summary>
    public sealed record GraphVulnerabilityAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        int BridgeLinkCount,
        int ArticulationPointCount,
        IReadOnlyList<string> BridgeDescriptions,
        IReadOnlyList<string> ArticulationNodeTitles,
        double StructuralFragilityIndex,
        string VulnerabilityCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents the role of an evaluated link in the network spanning backbone (tree edge vs redundant chord).
    /// </summary>
    public sealed record NetworkLinkRoleInfo(
        string LinkId,
        string SourceTitle,
        string TargetTitle,
        double Distance,
        bool IsBackbone,
        bool IsRedundantChord);

    /// <summary>
    /// Represents the result of an audit of the mindmap's network spanning backbone and redundant chords.
    /// </summary>
    public sealed record NetworkSpanningBackboneAudit(
        int TotalNodes,
        int TotalLinks,
        int ComponentCount,
        int BackboneLinkCount,
        int RedundantChordCount,
        double TotalNetworkLength,
        double BackboneLength,
        double RedundancyPercentage,
        IReadOnlyList<NetworkLinkRoleInfo> Links,
        string TopologyCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents detected community cluster metrics in a graph modularity audit.
    /// </summary>
    public sealed record MindMapCommunityInfo(
        int CommunityId,
        string Label,
        int NodeCount,
        IReadOnlyList<string> NodeTitles,
        int InternalLinks,
        int ExternalLinks,
        double InternalDensity);

    /// <summary>
    /// Represents the result of a mindmap graph modularity (Newman-Girvan Q) and community cluster audit.
    /// </summary>
    public sealed record GraphModularityAudit(
        int TotalNodes,
        int TotalLinks,
        int CommunityCount,
        double ModularityQ,
        int BridgingNodeCount,
        IReadOnlyList<MindMapCommunityInfo> Communities,
        IReadOnlyList<string> BridgingNodeTitles,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents geodesic eccentricity and path metrics for an individual node in a graph diameter audit.
    /// </summary>
    public sealed record NodeEccentricityInfo(
        string NodeId,
        string Title,
        int Eccentricity,
        bool IsPeripheral,
        bool IsCentral);

    /// <summary>
    /// Represents the result of an audit of the mindmap's geodesic graph diameter, radial eccentricity,
    /// and longest critical semantic concept path.
    /// </summary>
    public sealed record GraphDiameterAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        int GraphDiameter,
        int GraphRadius,
        int PeripheralNodeCount,
        int CentralNodeCount,
        IReadOnlyList<string> LongestConceptPath,
        IReadOnlyList<NodeEccentricityInfo> Nodes,
        string DiameterCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents closeness centrality, harmonic mean distance, and information propagation metrics for an individual node.
    /// </summary>
    public sealed record NodeClosenessInfo(
        string NodeId,
        string Title,
        double ClosenessCentrality,
        double HarmonicCentrality,
        double MeanDistance,
        int ReachableNodes,
        bool IsOptimalDisseminationHub);

    /// <summary>
    /// Represents the result of an audit of the mindmap's graph closeness centrality, harmonic mean distance,
    /// and information flow propagation efficiency.
    /// </summary>
    public sealed record ClosenessCentralityAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        double MeanHarmonicCentrality,
        double GraphEfficiency,
        IReadOnlyList<NodeClosenessInfo> NodeMetrics,
        IReadOnlyList<string> OptimalHubTitles,
        IReadOnlyList<string> PeripheralTitles,
        string EfficiencyCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents shortest-path betweenness centrality and information bottleneck metrics for an individual node.
    /// </summary>
    public sealed record NodeBetweennessInfo(
        string NodeId,
        string Title,
        double RawBetweenness,
        double NormalizedBetweenness,
        bool IsCriticalBottleneck,
        int ShortestPathsThroughCount);

    /// <summary>
    /// Represents the result of an audit of the mindmap's graph betweenness centrality, shortest-path bottleneck criticalities,
    /// and bridge concepts using Brandes' algorithm.
    /// </summary>
    public sealed record BetweennessCentralityAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        double MaxBetweenness,
        double MeanBetweenness,
        int BottleneckNodeCount,
        IReadOnlyList<NodeBetweennessInfo> NodeMetrics,
        IReadOnlyList<string> BottleneckNodeTitles,
        string BottleneckCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents eigenvector centrality, influence propagation weight, and network authority tier for a mindmap node.
    /// </summary>
    public sealed record NodeEigenvectorInfo(
        string NodeId,
        string Title,
        int Degree,
        double RawEigenvectorScore,
        double NormalizedInfluencePercent,
        string InfluenceTier,
        bool IsTopInfluencer);

    /// <summary>
    /// Represents the result of an audit of the mindmap's graph eigenvector centrality,
    /// power iteration convergence, and influence propagation network structure.
    /// </summary>
    public sealed record EigenvectorCentralityAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        double PrincipalEigenvalueEstimate,
        int IterationsRun,
        bool HasConverged,
        double MaxInfluencePercent,
        double InfluenceEntropy,
        IReadOnlyList<NodeEigenvectorInfo> NodeMetrics,
        IReadOnlyList<string> TopInfluencerTitles,
        string NetworkStructureCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated radial layout metrics, depth ring, and angular spacing for a mindmap node.
    /// </summary>
    public sealed record NodeRadialMetric(
        string NodeId,
        string Title,
        int DepthRing,
        double Radius,
        double AngleDegrees,
        double AngularGapDegrees,
        bool HasAngularCrowding);

    /// <summary>
    /// Represents aggregate spacing and uniformity metrics for an individual concentric depth ring.
    /// </summary>
    public sealed record RadialRingMetric(
        int DepthRing,
        int NodeCount,
        double MeanRadius,
        double RadiusJitterStdDev,
        double MeanAngularSpacingDegrees,
        double AngularUniformityScore);

    /// <summary>
    /// Represents the result of an audit of graph radial layout equidistant spacing,
    /// angular sector distribution, and concentric ring depth uniformity.
    /// </summary>
    public sealed record RadialLayoutAudit(
        int TotalNodes,
        int TotalLinks,
        int RingCount,
        double MeanRadiusJitter,
        double OverallAngularUniformityScore,
        double OverallLayoutFidelityScore,
        IReadOnlyList<RadialRingMetric> Rings,
        IReadOnlyList<NodeRadialMetric> NodeMetrics,
        IReadOnlyList<string> CrowdedNodeTitles,
        string LayoutCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents degree, local triangle count, clustering coefficient, and open triplet metrics for a mind map node.
    /// </summary>
    public sealed record NodeClusteringInfo(
        string NodeId,
        string NodeTitle,
        int Degree,
        int TriangleCount,
        double LocalClusteringCoefficient,
        int OpenTripletCount);

    /// <summary>
    /// Represents the result of an audit of graph clustering coefficients, triadic closure,
    /// network transitivity, and community density across mind map nodes.
    /// </summary>
    public sealed record GraphClusteringAndTransitivityAudit(
        int TotalNodes,
        int TotalLinks,
        int TotalTriangles,
        int TotalClosedTriplets,
        int TotalOpenTriplets,
        double GlobalTransitivity,
        double MeanClusteringCoefficient,
        IReadOnlyList<NodeClusteringInfo> Nodes,
        IReadOnlyList<string> HighClusteringNodeTitles,
        IReadOnlyList<string> BridgeBottleneckNodeTitles,
        string TopologyCategory,
        double ClusteringFidelityScore,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents calculated Fiedler eigenvector value, bisection cluster assignment, and degree for a mind map node.
    /// </summary>
    public sealed record NodeSpectralInfo(
        string NodeId,
        string NodeTitle,
        int Degree,
        double FiedlerValue,
        string PartitionCluster,
        bool IsCutBoundaryNode);

    /// <summary>
    /// Represents the result of an audit of graph algebraic connectivity (second-smallest Laplacian eigenvalue lambda_2),
    /// Fiedler vector spectral bisection, graph cut size, and conductance across mind map nodes.
    /// </summary>
    public sealed record GraphSpectralBisectionAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        double AlgebraicConnectivity,
        int CutEdgesCount,
        double Conductance,
        int PartitionASize,
        int PartitionBSize,
        IReadOnlyList<NodeSpectralInfo> Nodes,
        IReadOnlyList<string> BoundaryBridgeNodeTitles,
        string SpectralRobustnessCategory,
        double SpectralFidelityScore,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents degree, articulation point status, and block participation for an individual mindmap node.
    /// </summary>
    public sealed record NodeArticulationInfo(
        string NodeId,
        string Title,
        int Degree,
        bool IsArticulationPoint,
        int BlockCount,
        int DisconnectedComponentsIfRemoved);

    /// <summary>
    /// Represents bridge (cut-edge) status and endpoints for an evaluated link.
    /// </summary>
    public sealed record BridgeEdgeInfo(
        string LinkId,
        string SourceTitle,
        string TargetTitle,
        bool IsBridge);

    /// <summary>
    /// Represents a maximal 2-connected biconnected component (block) in the graph.
    /// </summary>
    public sealed record BiconnectedBlockInfo(
        int BlockId,
        int NodeCount,
        int EdgeCount,
        IReadOnlyList<string> NodeTitles,
        bool IsCycleOrDense);

    /// <summary>
    /// Represents the result of an audit of graph biconnected components, articulation points (cut vertices),
    /// and bridge edges using the Hopcroft-Tarjan algorithm.
    /// </summary>
    public sealed record GraphBiconnectivityAudit(
        int TotalNodes,
        int TotalLinks,
        int ConnectedComponents,
        int ArticulationPointCount,
        int BridgeEdgeCount,
        int BiconnectedBlockCount,
        double BiconnectivityScore,
        IReadOnlyList<NodeArticulationInfo> ArticulationNodes,
        IReadOnlyList<BridgeEdgeInfo> Bridges,
        IReadOnlyList<BiconnectedBlockInfo> Blocks,
        string TopologyCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated degree, shell index, and coreness classification for an individual concept node.
    /// </summary>
    public sealed record NodeKCoreShellInfo(
        string NodeId,
        string Label,
        int Degree,
        int CorenessShell,
        bool IsInNucleus,
        bool IsPeripheral);

    /// <summary>
    /// Represents the result of an audit of mind map graph core-periphery structure,
    /// k-core decomposition, concept degeneracy, and conceptual nucleus vs leaf shell distribution.
    /// </summary>
    public sealed record GraphKCoreDecompositionAudit(
        int TotalNodes,
        int TotalEdges,
        int MaxCoreness,
        int NucleusNodeCount,
        int PeripheralNodeCount,
        int IntermediateNodeCount,
        double CorePeripheryRatio,
        double MeanCoreness,
        IReadOnlyList<NodeKCoreShellInfo> Nodes,
        IReadOnlyDictionary<int, int> ShellDistribution,
        string CoreStructureCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents degree, average neighbor degree, and degree class for a mind map node in an assortativity audit.
    /// </summary>
    public sealed record NodeDegreeAssortativityInfo(
        string NodeId,
        string Title,
        int Degree,
        double AverageNeighborDegree,
        bool IsHub,
        bool IsLeaf);

    /// <summary>
    /// Represents the result of an audit of graph assortative mixing by degree, degree-degree correlation (Pearson r),
    /// hub-hub vs hub-leaf edge distribution, and scale-free network resilience across mind map nodes.
    /// </summary>
    public sealed record GraphAssortativityAudit(
        int TotalNodes,
        int TotalEdges,
        double PearsonAssortativityCoefficient,
        int HubToHubEdges,
        int HubToLeafEdges,
        int LeafToLeafEdges,
        double MeanDegree,
        double DegreeStandardDeviation,
        IReadOnlyList<NodeDegreeAssortativityInfo> Nodes,
        string NetworkMixingCategory,
        double NetworkResilienceScore,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated cluster metrics, internal edge count, density, and modularity contribution for a concept community.
    /// </summary>
    public sealed record CommunityClusterInfo(
        string CommunityId,
        string ClusterLabel,
        int NodeCount,
        int InternalEdges,
        int TotalDegree,
        double InternalDensity,
        double CommunityModularityContribution);

    /// <summary>
    /// Represents the result of an audit of mind map graph community modularity (Newman-Girvan Q),
    /// cluster partition density, intra-cluster cohesiveness, and inter-cluster bridge edge distribution.
    /// </summary>
    public sealed record GraphCommunityModularityAudit(
        int TotalNodes,
        int TotalEdges,
        int CommunityCount,
        double NewmanGirvanModularityQ,
        int IntraCommunityEdges,
        int InterCommunityBridgeEdges,
        double ModularityHealthScore,
        IReadOnlyList<CommunityClusterInfo> Communities,
        string ModularityCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated metrics and cycle participation for a strongly connected component in the graph.
    /// </summary>
    public sealed record GraphCycleComponentInfo(
        string ComponentId,
        int NodeCount,
        bool IsSelfLoop,
        IReadOnlyList<string> NodeIds,
        IReadOnlyList<string> NodeTitles,
        string CycleDescription);

    /// <summary>
    /// Represents the result of an audit of directed graph cycles, feedback loops, and strongly connected components (Tarjan SCC).
    /// </summary>
    public sealed record GraphCycleAndFeedbackAudit(
        int TotalNodes,
        int TotalEdges,
        int TotalSCCCount,
        int CyclicComponentCount,
        int TotalNodesInCycles,
        int LongestCycleNodeCount,
        bool IsPureDAG,
        double AcyclicityScore,
        IReadOnlyList<GraphCycleComponentInfo> CyclicComponents,
        string TopologyCategory,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated path metrics, eccentricity, and average distance for an individual mind map node.
    /// </summary>
    public sealed record NodePathEccentricityInfo(
        string NodeId,
        string Title,
        int Eccentricity,
        double AvgDistanceToOthers,
        int ReachableNodes);

    /// <summary>
    /// Represents the result of an audit of mind map graph diameter, characteristic path length,
    /// global network efficiency, connected components, and small-world navigation index.
    /// </summary>
    public sealed record GraphDiameterAndSmallWorldAudit(
        int TotalNodes,
        int TotalEdges,
        int ConnectedPairs,
        int Diameter,
        double CharacteristicPathLength,
        double GlobalEfficiency,
        int ComponentCount,
        double SmallWorldIndexScore,
        IReadOnlyList<NodePathEccentricityInfo> NodeMetrics,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated node degree, in/out degree breakdown, hub role, and centrality for an individual mind map node.
    /// </summary>
    public sealed record NodeDegreeDistributionInfo(
        string NodeId,
        string Title,
        int TotalDegree,
        int InDegree,
        int OutDegree,
        bool IsSuperHub,
        bool IsLeaf,
        bool IsIsolated,
        string Role);

    /// <summary>
    /// Represents the result of an audit of mind map graph node degree distribution skewness,
    /// hub centrality, variance, leaf node sprawl, and scale-free network topology.
    /// </summary>
    public sealed record GraphDegreeDistributionAudit(
        int TotalNodes,
        int TotalEdges,
        double MeanDegree,
        double DegreeVariance,
        double DegreeStdDev,
        double DegreeSkewness,
        int MaxDegree,
        int SuperHubCount,
        int LeafCount,
        int IsolatedCount,
        double ScaleFreeFitScore,
        IReadOnlyList<NodeDegreeDistributionInfo> NodeMetrics,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated articulation point metrics, connected branch count, degree, and topological role for a mind map node.
    /// </summary>
    public sealed record GraphCutVertexInfo(
        string NodeId,
        string Title,
        int Degree,
        int SubgraphBranches,
        string Role);

    /// <summary>
    /// Represents a critical bridge edge whose removal partitions the mind map graph into disconnected components.
    /// </summary>
    public sealed record GraphBridgeInfo(
        string SourceId,
        string SourceTitle,
        string TargetId,
        string TargetTitle,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph biconnected components, articulation points (cut vertices),
    /// bridge edges, and structural fault tolerance against single-point-of-failure node/edge partitions.
    /// </summary>
    public sealed record GraphArticulationPointsAudit(
        int TotalNodes,
        int TotalEdges,
        int ConnectedComponents,
        int CutVertexCount,
        int BridgeCount,
        int BiconnectedBlockCount,
        double FaultToleranceScore,
        IReadOnlyList<GraphCutVertexInfo> CutVertices,
        IReadOnlyList<GraphBridgeInfo> Bridges,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated shortest-path geodesic reachability, closeness centrality, and harmonic centrality metrics for an individual mind map node.
    /// </summary>
    public sealed record NodeCentralityMetricInfo(
        string NodeId,
        string Title,
        int ReachableCount,
        int TotalGeodesicDistance,
        double ClosenessCentrality,
        double HarmonicCentrality,
        double NormalizedHarmonicCentrality,
        string CentralityTier);

    /// <summary>
    /// Represents the result of an audit of mind map graph node closeness and harmonic centrality distributions,
    /// all-pairs shortest paths, broadcast efficiency, and communication hub routing.
    /// </summary>
    public sealed record GraphClosenessCentralityAudit(
        int TotalNodes,
        int TotalEdges,
        int ConnectedComponents,
        double AverageHarmonicCentrality,
        double NetworkBroadcastEfficiency,
        double BroadcastEfficiencyScore,
        string TopHubNodeId,
        string TopHubNodeTitle,
        double TopHubHarmonicScore,
        IReadOnlyList<NodeCentralityMetricInfo> NodeCentralities,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents betweenness centrality, shortest-path traffic stress, and gateway bottleneck status for an individual mind map node.
    /// </summary>
    public sealed record NodeBetweennessMetricInfo(
        string NodeId,
        string Title,
        int Degree,
        double BetweennessCentrality,
        double NormalizedBetweenness,
        bool IsGatewayBottleneck,
        string CentralityTier);

    /// <summary>
    /// Represents the result of an audit of mind map graph node betweenness centrality using Brandes' algorithm,
    /// identifying critical routing bottlenecks, gateway bridges, and graph decentralization score.
    /// </summary>
    public sealed record GraphBetweennessCentralityAudit(
        int TotalNodes,
        int TotalEdges,
        int ConnectedComponents,
        int GatewayBottleneckCount,
        double MaxBetweenness,
        double AverageBetweenness,
        double DecentralizationScore,
        string TopBottleneckNodeId,
        string TopBottleneckTitle,
        double TopBottleneckScore,
        IReadOnlyList<NodeBetweennessMetricInfo> NodeMetrics,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated degree metrics, role tier, and hub characteristics for an individual mind map node.
    /// </summary>
    public sealed record NodeDegreeMetricInfo(
        string NodeId,
        string Title,
        int InDegree,
        int OutDegree,
        int TotalDegree,
        bool IsDominantHub,
        string RoleTier);

    /// <summary>
    /// Represents the result of an audit of mind map graph node degree distribution, hub dominance,
    /// scale-free network characteristics, and topology balance.
    /// </summary>
    public sealed record GraphScaleFreeTopologyAudit(
        int TotalNodes,
        int TotalEdges,
        double AverageDegree,
        int MaxDegree,
        int HubCount,
        int LeafCount,
        int IsolatedCount,
        double HubDominanceRatio,
        double TopologyBalanceScore,
        string TopHubNodeId,
        string TopHubTitle,
        IReadOnlyList<NodeDegreeMetricInfo> Items,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents node topological stratum depth layer, degree connectivity, and cycle membership.
    /// </summary>
    public sealed record TopologicalStratumNodeInfo(
        string NodeId,
        string Title,
        int StratumLayer,
        int InDegree,
        int OutDegree,
        bool IsInCycle,
        string CycleGroupId);

    /// <summary>
    /// Represents the result of an audit of graph topological stratification, DAG acyclicity, and directed cycle resolution.
    /// </summary>
    public sealed record GraphTopologicalHierarchyAudit(
        int TotalNodes,
        int TotalEdges,
        int StratumLayerCount,
        int MaxStratumWidth,
        int CycleNodeCount,
        int CycleCount,
        double AcyclicityScore,
        IReadOnlyList<TopologicalStratumNodeInfo> Items,
        string HierarchyClassification,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents an individual detected intersection between two non-incident graph edge segments.
    /// </summary>
    public sealed record EdgeCrossingPairItem(
        string SourceLink1,
        string TargetLink1,
        string SourceLink2,
        string TargetLink2,
        double IntersectionX,
        double IntersectionY,
        double CrossingAngleDegrees,
        bool IsAcuteAngle);

    /// <summary>
    /// Represents the result of an audit of graph edge crossings, planarity score, crossing density, and visual clutter.
    /// </summary>
    public sealed record GraphEdgeCrossingPlanarityAudit(
        int TotalNodes,
        int TotalEdges,
        int TotalEdgePairsChecked,
        int CrossingCount,
        int AcuteAngleCrossingCount,
        double CrossingDensity,
        double PlanarityScore,
        IReadOnlyList<EdgeCrossingPairItem> Crossings,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated visual mass, node count, and centering metrics for an individual spatial quadrant.
    /// </summary>
    public sealed record SpatialQuadrantMassItem(
        string QuadrantName,
        int NodeCount,
        double TotalVisualMass,
        double MassPercentage,
        double CenterOffsetDistance,
        string Status);

    /// <summary>
    /// Represents the result of an audit of graph visual mass center of gravity, quadrant balance, and spatial offset.
    /// </summary>
    public sealed record GraphSpatialBalanceAudit(
        int TotalNodes,
        double CenterOfGravityX,
        double CenterOfGravityY,
        double CanvasMidpointX,
        double CanvasMidpointY,
        double MidpointOffsetDistance,
        double MassVariance,
        double SpatialBalanceScore,
        IReadOnlyList<SpatialQuadrantMassItem> Quadrants,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated clearance distance, overlap area, and repulsion urgency between two proximal or colliding mind map nodes.
    /// </summary>
    public sealed record NodeProximityPairItem(
        string NodeId1,
        string Title1,
        string NodeId2,
        string Title2,
        double DistanceCenterToCenter,
        double ClearanceDistance,
        bool IsOverlapping,
        double OverlapArea,
        string RepulsionUrgency);

    /// <summary>
    /// Represents the result of an audit of graph node bounding box collisions, clearance distances, crowding density, and visual breathing room.
    /// </summary>
    public sealed record GraphNodeProximityAudit(
        int TotalNodes,
        int TotalPairsEvaluated,
        int CollisionOverlapCount,
        int CrowdedPairCount,
        double MinClearanceDistance,
        double AverageClearanceDistance,
        double BreathingRoomScore,
        IReadOnlyList<NodeProximityPairItem> ProximalPairs,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated hierarchy depth, branching factor, leaf status, and sprawl risk for an individual mind map node.
    /// </summary>
    public sealed record SubtreeBranchingItem(
        string NodeId,
        string Title,
        int Depth,
        int ChildCount,
        bool IsLeaf,
        bool HasExcessiveBranching,
        bool HasDeepSprawl,
        string Status);

    /// <summary>
    /// Represents the result of an audit of graph subtree depth uniformity, branching factors, leaf density, and cognitive balance.
    /// </summary>
    public sealed record GraphSubtreeDepthAudit(
        int TotalNodes,
        int MaxDepth,
        double AverageDepth,
        int LeafNodeCount,
        double LeafRatio,
        double AverageBranchingFactor,
        int DeepSprawlCount,
        int ExcessiveBranchingCount,
        double CognitiveBalanceScore,
        IReadOnlyList<SubtreeBranchingItem> Nodes,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated cycle metrics, member nodes, self-loop indicator, and cycle break actions for an individual cyclic component.
    /// </summary>
    public sealed record GraphCycleComponentItem(
        int ComponentId,
        int NodeCount,
        IReadOnlyList<string> NodeTitles,
        IReadOnlyList<string> NodeIds,
        bool IsSelfLoop,
        bool IsMultiNodeCycle,
        string CyclePath,
        string RecommendedBreakAction);

    /// <summary>
    /// Represents the result of an audit of mind map graph topology for cyclic feedback loops,
    /// strongly connected components (SCC via Tarjan's algorithm), and directed acyclic graph (DAG) conformance.
    /// </summary>
    public sealed record GraphCycleDetectionAudit(
        int TotalNodes,
        int TotalLinks,
        int TotalComponents,
        int AcyclicComponentCount,
        int CyclicComponentCount,
        int TotalCyclesDetected,
        int SelfLoopsCount,
        double DagConformanceScore,
        IReadOnlyList<GraphCycleComponentItem> Cycles,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated spatial coordinates, bounding overlaps, nearest neighbor distance, and clustering risk for an individual mind map node.
    /// </summary>
    public sealed record NodeSpatialClusterItem(
        string NodeId,
        string NodeTitle,
        double X,
        double Y,
        int OverlapCount,
        int NeighborCountWithinRadius,
        double NearestNeighborDistance,
        string ClusterRiskLevel,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph node spatial density, force-directed bounding overlaps,
    /// pairwise Euclidean distances, collision clusters, and spatial dispersion balance.
    /// </summary>
    public sealed record GraphSpatialDensityAudit(
        int TotalNodes,
        int TotalCollisions,
        int OverlappingNodesCount,
        int IsolatedNodesCount,
        double MeanPairwiseDistance,
        double MinDistance,
        double MaxDistance,
        double SpatialDispersionScore,
        IReadOnlyList<NodeSpatialClusterItem> Clusters,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated hierarchy depth, child fan-out count, and sprawl risks for an individual mind map node.
    /// </summary>
    public sealed record TreeNodeBranchingItem(
        string NodeId,
        string NodeTitle,
        int Depth,
        int ChildCount,
        bool IsRoot,
        bool IsLeaf,
        bool IsOverBranching,
        bool IsDeepSprawl,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph hierarchy tree depth, child fan-out bottlenecks (>8 children),
    /// deep hierarchical sprawl (>6 levels), and branching factor balance.
    /// </summary>
    public sealed record GraphTreeHierarchyAudit(
        int TotalNodes,
        int RootCount,
        int LeafCount,
        int MaxDepth,
        double AverageBranchingFactor,
        int OverBranchingNodesCount,
        int DeepSprawlNodesCount,
        double HierarchyBalanceScore,
        IReadOnlyList<TreeNodeBranchingItem> Nodes,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated local clustering coefficient, neighbor triangles, and topology classification for an individual node.
    /// </summary>
    public sealed record NodeClusteringCoefficientItem(
        string NodeId,
        string NodeTitle,
        int Degree,
        int NeighborTriangles,
        int MaxPossibleTriangles,
        double LocalClusteringCoefficient,
        bool IsHubAndSpokeVertex,
        bool IsLatticeClusterVertex,
        string TopologyClassification,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph local and global clustering coefficients,
    /// hub-and-spoke vs lattice configurations, and small-world interconnectedness metrics.
    /// </summary>
    public sealed record GraphClusteringCoefficientAudit(
        int TotalNodes,
        int TotalLinks,
        int TotalTriangles,
        double GlobalClusteringCoefficient,
        int HubAndSpokeCount,
        int LatticeClusterCount,
        int IsolatedCount,
        double SmallWorldInterconnectednessScore,
        IReadOnlyList<NodeClusteringCoefficientItem> Nodes,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated bipartite partition set, degree, odd-cycle conflict, and modularity status for an individual mind map node.
    /// </summary>
    public sealed record NodeBipartiteItem(
        string NodeId,
        string NodeTitle,
        int Degree,
        int ComponentId,
        int AssignedPartition,
        int ConflictDegree,
        bool IsConflicted,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph bipartite 2-mode network partition, odd cycles, and modularity symmetry.
    /// </summary>
    public sealed record GraphBipartiteAudit(
        int TotalNodes,
        int TotalLinks,
        bool IsBipartite,
        int Partition1Count,
        int Partition2Count,
        int ConflictEdgeCount,
        int ComponentCount,
        int IsolatedCount,
        double ModularityScore,
        IReadOnlyList<NodeBipartiteItem> Nodes,
        string Category,
        bool IsSelectionScope,
        string Summary);
    /// <summary>
    /// Represents evaluated coordinates, intersected edges, and status for an individual graph edge crossing.
    /// </summary>
    public sealed record EdgeCrossingItem(
        string SourceEdgeId,
        string TargetEdgeId,
        string SourceEdgeLabel,
        string TargetEdgeLabel,
        double IntersectionX,
        double IntersectionY,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map 2D edge crossings, visual planarity, and Kuratowski K5/K3,3 graph obstructions.
    /// </summary>
    public sealed record GraphPlanarityAudit(
        int TotalNodes,
        int TotalEdges,
        int CrossingCount,
        bool HasKuratowskiObstruction,
        string ObstructionType,
        double PlanarityScore,
        IReadOnlyList<EdgeCrossingItem> Crossings,
        string Category,
        bool IsSelectionScope,
        string Summary);

    /// <summary>
    /// Represents evaluated metrics, articulation cut status, incident bridge edges, and removal component count for an individual mind map node.
    /// </summary>
    public sealed record GraphArticulationItem(
        string NodeId,
        string NodeTitle,
        bool IsArticulationPoint,
        int IncidentEdgesCount,
        int SubgraphComponentsOnRemoval,
        IReadOnlyList<string> IncidentBridgeEdges,
        string Severity,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph articulation points (cut vertices), bridge bottlenecks,
    /// and structural network resilience via Tarjan's biconnectivity DFS.
    /// </summary>
    public sealed record GraphArticulationAudit(
        int TotalNodes,
        int TotalEdges,
        int ArticulationPointCount,
        int BridgeCount,
        int ConnectedComponentsCount,
        double ResilienceScore,
        IReadOnlyList<GraphArticulationItem> Items,
        string Category,
        bool IsSelectionScope,
        string Summary);


    /// <summary>
    /// Represents evaluated metrics, in-degree, out-degree, total degree, and hub classification for an individual mind map node.
    /// </summary>
    public sealed record GraphDegreeCentralityItem(
        string NodeId,
        string Label,
        int InDegree,
        int OutDegree,
        int TotalDegree,
        bool IsHub,
        bool IsIsolate,
        bool IsLeaf,
        double DegreeCentralityScore,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph node degree distribution, scale-free power law,
    /// super-hub centrality, isolate detection, and hierarchical balance.
    /// </summary>
    public sealed record GraphDegreeDistributionCentralityAudit(
        int TotalNodes,
        int TotalEdges,
        int HubCount,
        int IsolateCount,
        int LeafCount,
        double AverageDegree,
        double MaxDegree,
        double DegreeStandardDeviation,
        double ScaleFreePowerLawExponent,
        double HierarchyBalanceScore,
        IReadOnlyList<GraphDegreeCentralityItem> Items,
        string Category,
        bool IsSelectionScope,
        string Summary);


    /// <summary>
    /// Represents evaluated metrics, distance, backbone classification, and cycle status for an individual graph edge in an MST audit.
    /// </summary>
    public sealed record MstBackboneEdgeItem(
        string SourceId,
        string SourceTitle,
        string TargetId,
        string TargetTitle,
        double Distance,
        bool IsMstEdge,
        bool IsBackboneTrunk,
        bool IsRedundantCycle,
        string Status);

    /// <summary>
    /// Represents the result of an audit of mind map graph minimum spanning tree (MST / Kruskal),
    /// backbone highway corridors, redundant cycle edges, connected components, and tree efficiency.
    /// </summary>
    public sealed record GraphMinimumSpanningTreeAndBackboneAudit(
        int TotalNodes,
        int TotalEdges,
        int MstEdgeCount,
        int RedundantCycleCount,
        int ConnectedComponentsCount,
        int IsolateCount,
        double TotalWireLength,
        double MstWireLength,
        double BackboneHighwayRatio,
        double TreeEfficiencyScore,
        IReadOnlyList<MstBackboneEdgeItem> Items,
        string Category,
        bool IsSelectionScope,
        string Summary);


    /// <summary>
    /// Represents an identified cycle path, cycle length, and feedback arc candidate in a mind map graph.
    /// </summary>
    public sealed record GraphCycleItem(
        int CycleIndex,
        int CycleLength,
        string NodeSequence,
        string FeedbackArcEdge,
        string Status,
        string Remediation);

    /// <summary>
    /// Represents the result of an audit of mind map graph directed cycles, feedback arc set (FAS),
    /// directed acyclic graph (DAG) compliance, and recursive loop mitigation.
    /// </summary>
    /// <summary>
    /// Represents an individual maximal biconnected subgraph block within the mind map graph.
    /// </summary>
    public sealed record GraphBiconnectedBlockItem(
        int BlockIndex,
        int NodeCount,
        int EdgeCount,
        IReadOnlyList<string> NodeIds,
        bool IsTrivialSingleEdge);

    /// <summary>
    /// Represents evaluated articulation cut status, incident bridge bottlenecks, degree, and removal impact for an individual mind map node.
    /// </summary>
    public sealed record GraphArticulationPointItem(
        string NodeId,
        string NodeTitle,
        bool IsCutVertex,
        int Degree,
        int IncidentBridgeCount,
        int BiconnectedBlockCount,
        string Severity,
        string Status,
        string Recommendation);

    /// <summary>
    /// Represents the result of an audit of mind map graph biconnected components, cut vertices (articulation points),
    /// bridge bottlenecks, and network structural redundancy via Hopcroft-Tarjan DFS.
    /// </summary>
    public sealed record GraphBiconnectedComponentsAudit(
        int TotalNodes,
        int TotalEdges,
        int CutVerticesCount,
        int BridgesCount,
        int BiconnectedBlockCount,
        int ConnectedComponentsCount,
        double StructuralRedundancyScore,
        IReadOnlyList<GraphArticulationPointItem> ArticulationPoints,
        IReadOnlyList<GraphBiconnectedBlockItem> Blocks,
        string Category,
        bool IsSelectionScope,
        string Summary);

    public sealed record GraphCycleFeedbackAudit(
        int TotalNodes,
        int TotalEdges,
        int CycleCount,
        int FeedbackArcSetCount,
        bool IsAcyclicDag,
        int MaxCycleLength,
        double AcyclicComplianceScore,
        IReadOnlyList<GraphCycleItem> Cycles,
        IReadOnlyList<string> FeedbackArcSet,
        string Category,
        bool IsSelectionScope,
        string Summary);

}
