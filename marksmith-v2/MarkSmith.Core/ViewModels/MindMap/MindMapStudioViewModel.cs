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
    }
}
