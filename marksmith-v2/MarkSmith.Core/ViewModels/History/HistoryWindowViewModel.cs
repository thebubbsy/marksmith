using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Services;

namespace MarkSmith.ViewModels.History;

public enum HistoryDiffMode
{
    Unified,
    Split,
    Preview
}

/// <summary>One selectable row on the version timeline with stars, labels, and delta metrics.</summary>
public sealed partial class VersionItemViewModel : ObservableObject
{
    public VersionItemViewModel(VersionEntry entry, string timestampLabel, string snippet)
    {
        Entry = entry;
        TimestampLabel = timestampLabel;
        Snippet = snippet;
        _label = entry.Label ?? "";
        _isStarred = entry.IsStarred;
        LinesAdded = entry.LinesAdded;
        LinesRemoved = entry.LinesRemoved;

        SourceLabel = entry.Source switch
        {
            "opened" => "Opened",
            "autosave" => "Auto-Save",
            "snapshot" or "manual" => "Snapshot",
            "ingest" => "AI Ingest",
            var s when s.StartsWith("export:") => "Export · " + s["export:".Length..].ToUpperInvariant(),
            _ => entry.Source,
        };

        SourceIcon = entry.Source switch
        {
            "opened" => "📂",
            "autosave" => "✏️",
            "snapshot" or "manual" => "💾",
            "ingest" => "📥",
            var s when s.Contains("pdf") => "📄",
            var s when s.Contains("docx") => "📝",
            _ => "⏱️"
        };
    }

    public VersionEntry Entry { get; }
    public string TimestampLabel { get; }
    public string Snippet { get; }
    public string SourceLabel { get; }
    public string SourceIcon { get; }
    public string Id => Entry.Id;
    public int LinesAdded { get; }
    public int LinesRemoved { get; }

    public string DeltaBadge => LinesAdded > 0 && LinesRemoved > 0
        ? $"+{LinesAdded}  −{LinesRemoved}"
        : (LinesAdded > 0 ? $"+{LinesAdded}" : (LinesRemoved > 0 ? $"−{LinesRemoved}" : "0"));

    public bool HasDelta => LinesAdded > 0 || LinesRemoved > 0;

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isStarred;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Snippet) &&
                              !string.Equals(Snippet, SourceLabel, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(Snippet, Entry.Source, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A file in the global edit history hub (every file ever touched).</summary>
public sealed partial class FileSummaryViewModel : ObservableObject
{
    public FileSummaryViewModel(VersionHistoryService.FileHistorySummary summary)
    {
        Summary = summary;
        Detail = summary.FilePath;
        VersionCountLabel = summary.VersionCount + (summary.VersionCount == 1 ? " version" : " versions");
        LastModifiedLabel = summary.LastModified.LocalDateTime.ToString("d MMM yyyy · HH:mm");
    }

    public VersionHistoryService.FileHistorySummary Summary { get; }
    public string FileName => Summary.FileName;
    public string Detail { get; }
    public string VersionCountLabel { get; }
    public string LastModifiedLabel { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The version-history hub: interactive Time Machine with visual timeline spine,
/// stars/bookmarks, instant search, unified & split diffs, and live preview.
/// </summary>
public sealed partial class HistoryWindowViewModel : ObservableObject
{
    private readonly Func<string, string> _previewBuilder;
    private readonly Func<string, Task<bool>> _restore;
    private readonly VersionHistoryService _history;
    private List<VersionEntry> _allVersions = new();
    private string _currentFile = "";
    private int _selectionToken;

    public HistoryWindowViewModel(
        Func<string, string> previewBuilder,
        Func<string, Task<bool>> restore,
        VersionHistoryService? history = null,
        string? initialFilePath = null)
    {
        _previewBuilder = previewBuilder;
        _restore = restore;
        _history = history ?? AppServices.VersionHistory;
        InitialFilePath = initialFilePath ?? "";
    }

    private string InitialFilePath { get; }

    public ObservableCollection<FileSummaryViewModel> Files { get; } = new();
    public ObservableCollection<TimeBandViewModel> Bands { get; } = new();

    [ObservableProperty]
    private string _fileName = "—";

    [ObservableProperty]
    private string _selectedHeader = "Select a version";

    [ObservableProperty]
    private string _previewHtml = "";

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _hasVersions;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private VersionItemViewModel? _selected;

    [ObservableProperty]
    private FileSummaryViewModel? _selectedFile;

    [ObservableProperty]
    private string _searchQuery = "";

    partial void OnSearchQueryChanged(string value) => ApplyTimelineFilter();

    [ObservableProperty]
    private bool _isStarredOnlyFilter;

    partial void OnIsStarredOnlyFilterChanged(bool value) => ApplyTimelineFilter();

    [ObservableProperty]
    private HistoryDiffMode _diffMode = HistoryDiffMode.Unified;

    [ObservableProperty]
    private bool _showDiff = true;

    [ObservableProperty]
    private bool _showUnifiedDiff = true;

    [ObservableProperty]
    private bool _showSplitDiff = false;

    [ObservableProperty]
    private bool _showPreview = false;

    [ObservableProperty]
    private string _diffTitle = "Select a version to see its changes";

    [ObservableProperty]
    private string _diffStats = "";

    [ObservableProperty]
    private string _diffHeader = "";

    public ObservableCollection<DiffRowViewModel> DiffRows { get; } = new();
    public ObservableCollection<LineDiff.UnifiedRow> UnifiedDiffRows { get; } = new();

    [RelayCommand]
    public void SetDiffMode(HistoryDiffMode mode)
    {
        DiffMode = mode;
        ShowDiff = mode != HistoryDiffMode.Preview;
        ShowUnifiedDiff = mode == HistoryDiffMode.Unified;
        ShowSplitDiff = mode == HistoryDiffMode.Split;
        ShowPreview = mode == HistoryDiffMode.Preview;
    }

    [RelayCommand]
    private void ShowDiffView() => SetDiffMode(HistoryDiffMode.Unified);

    [RelayCommand]
    private void ShowPreviewView() => SetDiffMode(HistoryDiffMode.Preview);

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            Files.Clear();
            var overview = await _history.GetOverviewAsync();
            foreach (var summary in overview)
                Files.Add(new FileSummaryViewModel(summary));

            // Pre-select the file that's open in the editor if it has history, else the most recent.
            var preferred = Files.FirstOrDefault(f =>
                string.Equals(f.Summary.FilePath, InitialFilePath, StringComparison.OrdinalIgnoreCase))
                ?? Files.FirstOrDefault();
            IsLoaded = true;

            if (preferred is not null) await SelectFileCommand.ExecuteAsync(preferred);
        }
        catch
        {
            // A store failure must never take the window down — show an empty hub.
        }
    }

    [RelayCommand]
    public async Task SelectFileAsync(FileSummaryViewModel file)
    {
        if (SelectedFile is not null) SelectedFile.IsSelected = false;
        SelectedFile = file;
        if (file is not null)
        {
            file.IsSelected = true;
            await LoadTimelineAsync(file.Summary.FilePath);
        }
    }

    private async Task LoadTimelineAsync(string filePath)
    {
        _selectionToken++;
        _currentFile = filePath;
        FileName = Path.GetFileName(filePath);
        Bands.Clear();
        DiffRows.Clear();
        UnifiedDiffRows.Clear();
        Selected = null;
        SelectedHeader = "Select a version";
        DiffTitle = "Select a version to see its changes";
        DiffStats = "";

        try
        {
            var versions = await _history.GetVersionsAsync(filePath);
            if (filePath != _currentFile) return;
            _allVersions = versions;
            HasVersions = versions.Count > 0;
            if (versions.Count == 0) return;

            ApplyTimelineFilter();

            if (Bands.Count > 0 && Bands[0].Items.Count > 0)
                SelectVersion(Bands[0].Items[0]);
        }
        catch
        {
            // Keep the hub alive if one file's history is unreadable.
        }
    }

    private void ApplyTimelineFilter()
    {
        Bands.Clear();
        if (_allVersions.Count == 0) return;

        var now = DateTime.Now;
        var q = SearchQuery?.Trim() ?? "";
        bool filterStarred = IsStarredOnlyFilter;

        var filtered = _allVersions.Where(v =>
        {
            if (filterStarred && !v.IsStarred) return false;
            if (!string.IsNullOrEmpty(q))
            {
                bool matchesLabel = v.Label?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false;
                bool matchesSource = v.Source.Contains(q, StringComparison.OrdinalIgnoreCase);
                bool matchesId = v.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
                return matchesLabel || matchesSource || matchesId;
            }
            return true;
        }).ToList();

        var byBand = new Dictionary<string, List<VersionItemViewModel>>();
        foreach (var entry in filtered)
        {
            var band = BandName(entry.CreatedAt.LocalDateTime, now);
            if (!byBand.TryGetValue(band, out var list))
            {
                list = new List<VersionItemViewModel>();
                byBand[band] = list;
            }
            list.Add(new VersionItemViewModel(entry, TimestampLabel(entry.CreatedAt.LocalDateTime, now), entry.Label ?? ""));
        }

        foreach (var band in new[] { "Today", "Yesterday", "This Week", "This Month", "This Year", "Older" })
        {
            if (!byBand.TryGetValue(band, out var items) || items.Count == 0) continue;
            Bands.Add(new TimeBandViewModel(band, items));
        }
    }

    [RelayCommand]
    public void SelectVersion(VersionItemViewModel item)
    {
        if (Selected is not null) Selected.IsSelected = false;
        Selected = item;
        if (item is not null)
        {
            item.IsSelected = true;
            var t = item.Entry.CreatedAt.LocalDateTime;
            SelectedHeader = t.ToString("dddd, d MMMM yyyy") + " · " + t.ToString("HH:mm");
            var token = ++_selectionToken;
            _ = RefreshDiffAsync(item, token);
            _ = RefreshPreviewAsync(item, token);
        }
    }

    [RelayCommand]
    public async Task ToggleStarAsync(VersionItemViewModel? item)
    {
        var target = item ?? Selected;
        if (target == null) return;
        bool isStarred = await _history.ToggleStarAsync(target.Id);
        target.IsStarred = isStarred;
    }

    [RelayCommand]
    public async Task RenameVersionAsync((VersionItemViewModel? item, string newLabel) args)
    {
        var target = args.item ?? Selected;
        if (target == null) return;
        if (await _history.SetLabelAsync(target.Id, args.newLabel))
        {
            target.Label = args.newLabel;
        }
    }

    [RelayCommand]
    public async Task DeleteVersionAsync(VersionItemViewModel? item)
    {
        var target = item ?? Selected;
        if (target == null) return;
        if (await _history.DeleteVersionAsync(target.Id))
        {
            _allVersions.RemoveAll(v => v.Id == target.Id);
            ApplyTimelineFilter();
            if (Bands.Count > 0 && Bands[0].Items.Count > 0)
                SelectVersion(Bands[0].Items[0]);
        }
    }

    [RelayCommand]
    public async Task TakeSnapshotAsync(string? label)
    {
        if (string.IsNullOrEmpty(_currentFile)) return;
        var currentContent = Selected != null ? await _history.GetContentAsync(Selected.Id) : "";
        if (string.IsNullOrEmpty(currentContent)) return;

        if (await _history.CaptureAsync(_currentFile, currentContent, "snapshot", label ?? "Manual Snapshot", isStarred: true))
        {
            await LoadTimelineAsync(_currentFile);
        }
    }

    /// <summary>Diffs the selected version against the version before it.</summary>
    private async Task RefreshDiffAsync(VersionItemViewModel item, int token)
    {
        try
        {
            var selectedContent = await _history.GetContentAsync(item.Id) ?? "";
            if (token != _selectionToken) return;
            var idx = _allVersions.FindIndex(v => v.Id == item.Id);
            var prevContent = (idx >= 0 && idx + 1 < _allVersions.Count)
                ? await _history.GetContentAsync(_allVersions[idx + 1].Id) ?? ""
                : "";
            if (token != _selectionToken) return;

            var lines = LineDiff.Diff(prevContent, selectedContent);
            var rows = LineDiff.BuildSideBySide(lines);
            var unified = LineDiff.BuildUnified(lines);

            DiffRows.Clear();
            foreach (var row in rows) DiffRows.Add(new DiffRowViewModel(row));

            UnifiedDiffRows.Clear();
            foreach (var u in unified) UnifiedDiffRows.Add(u);

            int added = lines.Count(l => l.Kind == LineDiff.Kind.Added);
            int removed = lines.Count(l => l.Kind == LineDiff.Kind.Removed);
            var stamp = item.Entry.CreatedAt.LocalDateTime;
            DiffTitle = "Changes in this version";
            DiffHeader = (idx >= 0 && idx + 1 < _allVersions.Count
                ? "vs the previous version"
                : "the first version of this file") + " · " + stamp.ToString("dddd, d MMMM yyyy HH:mm");
            DiffStats = added + " added · " + removed + " removed";
        }
        catch { /* keep the last diff */ }
    }

    private async Task RefreshPreviewAsync(VersionItemViewModel item, int token)
    {
        try
        {
            var content = await _history.GetContentAsync(item.Id) ?? "";
            if (token != _selectionToken) return;
            PreviewHtml = _previewBuilder(content);
        }
        catch { /* keep the last preview */ }
    }

    [RelayCommand]
    public async Task RestoreAsync()
    {
        if (Selected is null || IsRestoring) return;
        IsRestoring = true;
        try { await _restore(Selected.Id); }
        finally { IsRestoring = false; }
    }

    internal static string BandName(DateTime t, DateTime now)
    {
        if (t.Date == now.Date) return "Today";
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (t >= now.AddDays(-7)) return "This Week";
        if (t.Month == now.Month && t.Year == now.Year) return "This Month";
        if (t.Year == now.Year) return "This Year";
        return "Older";
    }

    internal static string TimestampLabel(DateTime t, DateTime now)
    {
        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday · " + t.ToString("HH:mm");
        if (t >= now.AddDays(-7)) return t.ToString("dddd") + " · " + t.ToString("HH:mm");
        if (t.Month == now.Month && t.Year == now.Year) return t.ToString("d MMM") + " · " + t.ToString("HH:mm");
        if (t.Year == now.Year) return t.ToString("d MMM");
        return t.ToString("d MMM yyyy");
    }
}

/// <summary>A horizontal band of versions in the timeline (Today / Yesterday / … / Older).</summary>
public sealed class TimeBandViewModel
{
    public TimeBandViewModel(string name, List<VersionItemViewModel> items)
    {
        Name = name;
        Items = new ObservableCollection<VersionItemViewModel>(items);
    }

    public string Name { get; }
    public ObservableCollection<VersionItemViewModel> Items { get; }
}

/// <summary>One side-by-side row of the version diff (left = previous, right = selected).</summary>
public sealed class DiffRowViewModel
{
    public DiffRowViewModel(LineDiff.Row row)
    {
        Left = row.Left is null ? null : new DiffCellViewModel(row.Left);
        Right = row.Right is null ? null : new DiffCellViewModel(row.Right);
    }

    public DiffCellViewModel? Left { get; }
    public DiffCellViewModel? Right { get; }
}

public sealed class DiffCellViewModel
{
    public DiffCellViewModel(LineDiff.Cell cell)
    {
        Kind = cell.Kind;
        NumberLabel = cell.NumberLabel;
        Text = cell.Text;
    }

    public LineDiff.Kind Kind { get; }
    public string NumberLabel { get; }
    public string Text { get; }
    public bool IsRemoved => Kind == LineDiff.Kind.Removed;
    public bool IsAdded => Kind == LineDiff.Kind.Added;
}
