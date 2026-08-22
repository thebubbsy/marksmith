using System;
using System.Collections.ObjectModel;
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

        public ObservableCollection<string> Tags { get; } = new();

        [ObservableProperty]
        private int _versionCount;

        [ObservableProperty]
        private string _versionCountText = "";

        [ObservableProperty]
        private string _latestVersionLabel = "";

        public bool HasVersions => VersionCount > 0;

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
                OnPropertyChanged(nameof(HasVersions));
            }
            catch { /* best-effort history lookup */ }
        }

        public string FormatBadge =>
            !string.IsNullOrEmpty(FileExtension) ? FileExtension.TrimStart('.').ToUpperInvariant() : NodeType.ToString().ToUpperInvariant();

        public bool HasFile => !string.IsNullOrEmpty(FilePath);

        public bool HasProgress => Progress > 0;
        public string ProgressText => $"{Progress}%";

        partial void OnProgressChanged(int value)
        {
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(HasProgress));
        }

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
