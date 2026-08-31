using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MarkSmith.Models.MindMap;

namespace MarkSmith.ViewModels.MindMap
{
    public sealed partial class MindMapNodeViewModel : ObservableObject
    {
        public MindMapNode Model { get; }

        public string Id => Model.Id;

        [ObservableProperty]
        private string _title;

        [ObservableProperty]
        private string? _filePath;

        [ObservableProperty]
        private string? _fileExtension;

        [ObservableProperty]
        private MindMapNodeType _nodeType;

        [ObservableProperty]
        private double _x;

        [ObservableProperty]
        private double _y;

        [ObservableProperty]
        private double _width;

        [ObservableProperty]
        private double _height;

        [ObservableProperty]
        private string _colorHex;

        [ObservableProperty]
        private string? _icon;

        [ObservableProperty]
        private int _progress;

        [ObservableProperty]
        private string? _markdownContent;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private bool _isHovered;

        [ObservableProperty]
        private bool _isCollapsed;

        [ObservableProperty]
        private string? _parentId;

        [ObservableProperty]
        private bool _isDimmed;

        [ObservableProperty]
        private bool _isHighlighted;

        /// <summary>Set while focus mode is on and this node is one hop from the selection.</summary>
        [ObservableProperty]
        private bool _isNeighbor;

        /// <summary>How many edges touch this node. Drives the node's "hub" weighting on the canvas.</summary>
        [ObservableProperty]
        private int _connectionCount;

        public ObservableCollection<string> Tags { get; } = new();

        [ObservableProperty]
        private int _versionCount;

        [ObservableProperty]
        private string _versionCountText = "";

        [ObservableProperty]
        private string _latestVersionLabel = "";

        public bool HasVersions => VersionCount > 0;

        partial void OnVersionCountChanged(int value) => OnPropertyChanged(nameof(HasVersions));

        public async Task RefreshVersionHistoryAsync(Services.VersionHistoryService history)
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                VersionCount = 0;
                VersionCountText = "";
                LatestVersionLabel = "";
                return;
            }
            try
            {
                var versions = await history.GetVersionsAsync(FilePath);
                VersionCount = versions.Count;
                if (versions.Count > 0)
                {
                    VersionCountText = $"⏱️ {versions.Count} {(versions.Count == 1 ? "version" : "versions")}";
                    var latest = versions[0];
                    LatestVersionLabel = latest.CreatedAt.LocalDateTime.ToString("d MMM · HH:mm");
                }
                else
                {
                    VersionCountText = "";
                    LatestVersionLabel = "";
                }
            }
            catch { /* best-effort history lookup */ }
        }

        public string FormatBadge =>
            !string.IsNullOrEmpty(FileExtension) ? FileExtension.TrimStart('.').ToUpperInvariant() : NodeType.ToString().ToUpperInvariant();

        public bool HasFile => !string.IsNullOrEmpty(FilePath);

        /// <summary>Just the file name, for the node card and the inspector — a full path is
        /// unreadable at canvas scale.</summary>
        public string FileName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(FilePath)) return "";
                // Split on BOTH separators rather than Path.GetFileName: a .msmap written on
                // Windows carries backslash paths, and on any other OS GetFileName does not treat
                // '\\' as a separator, so the whole path came back as the "file name".
                int cut = FilePath!.LastIndexOfAny(new[] { '/', '\\' });
                string name = cut >= 0 ? FilePath[(cut + 1)..] : FilePath;
                return name.Length == 0 ? FilePath : name;
            }
        }

        /// <summary>True when the node remembers a path that is no longer on disk — worth telling
        /// the user about rather than silently doing nothing when they double-click it.
        ///
        /// Cached, and refreshed only by <see cref="RefreshFileState"/>. This was a computed
        /// property that touched the filesystem on every single read, and the canvas reads it for
        /// every node on every frame: on a network share or a cloud-synced vault that is a
        /// blocking stat per node per frame.</summary>
        [ObservableProperty]
        private bool _isFileMissing;

        /// <summary>Re-probes the disk for this one node. Call it off the UI thread when sweeping
        /// a whole galaxy.</summary>
        public void RefreshFileState()
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                IsFileMissing = false;
                return;
            }
            try
            {
                IsFileMissing = !File.Exists(FilePath) && !Directory.Exists(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable path is not the same as a missing one; don't cry wolf.
                IsFileMissing = false;
            }
        }

        public bool HasProgress => Progress > 0;
        public string ProgressText => $"{Progress}%";
        public bool HasTags => Tags.Count > 0;
        public string TagSummary => Tags.Count == 0 ? "" : string.Join("  ", Tags.Take(3));
        public bool IsHub => ConnectionCount >= 4;

        partial void OnProgressChanged(int value)
        {
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(HasProgress));
        }

        // FormatBadge is what the canvas draws in the corner of every card; without these it kept
        // showing the format the node had when the window opened.
        partial void OnFileExtensionChanged(string? value) => OnPropertyChanged(nameof(FormatBadge));

        partial void OnNodeTypeChanged(MindMapNodeType value) => OnPropertyChanged(nameof(FormatBadge));

        partial void OnFilePathChanged(string? value)
        {
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(FileName));
            RefreshFileState();

            // Typing a path into the inspector should give the node the right badge and icon
            // without the user also having to fill in a separate "extension" field.
            if (!string.IsNullOrWhiteSpace(value))
            {
                string name = FileName;
                int dot = name.LastIndexOf('.');
                if (dot > 0 && dot < name.Length - 1)
                {
                    FileExtension = name[dot..].ToLowerInvariant();
                }
            }
        }

        partial void OnConnectionCountChanged(int value) => OnPropertyChanged(nameof(IsHub));

        public MindMapNodeViewModel(MindMapNode model)
        {
            Model = model;
            _title = model.Title ?? "Untitled";
            _filePath = model.FilePath;
            _fileExtension = model.FileExtension;
            _nodeType = model.NodeType;
            _x = model.X;
            _y = model.Y;
            _width = model.Width > 0 ? model.Width : 180;
            _height = model.Height > 0 ? model.Height : 56;
            _colorHex = model.ColorHex ?? "#FF7C4D";
            _icon = model.Icon ?? "📄";
            _progress = model.Progress;
            _markdownContent = model.MarkdownContent;
            _isCollapsed = model.IsCollapsed;
            _parentId = model.ParentId;

            if (model.Tags != null)
            {
                foreach (var t in model.Tags) Tags.Add(t);
            }
            Tags.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasTags));
                OnPropertyChanged(nameof(TagSummary));
            };
        }

        public void SyncToModel()
        {
            Model.Title = Title;
            Model.FilePath = FilePath;
            Model.FileExtension = FileExtension;
            Model.NodeType = NodeType;
            Model.X = X;
            Model.Y = Y;
            Model.Width = Width;
            Model.Height = Height;
            Model.ColorHex = ColorHex;
            Model.Icon = Icon;
            Model.Progress = Progress;
            Model.MarkdownContent = MarkdownContent;
            Model.IsCollapsed = IsCollapsed;
            Model.ParentId = ParentId;
            Model.Tags = new(Tags);
        }
    }
}
