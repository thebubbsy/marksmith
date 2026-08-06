using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Services;

namespace MarkSmith.ViewModels.History;

/// <summary>One selectable row on the version timeline.</summary>
public sealed partial class VersionItemViewModel : ObservableObject
{
    public VersionItemViewModel(VersionEntry entry, string timestampLabel, string snippet)
    {
        Entry = entry;
        TimestampLabel = timestampLabel;
        Snippet = snippet;
        SourceLabel = entry.Source switch
        {
            "opened" => "Opened",
            var s when s.StartsWith("export:") => "Export · " + s["export:".Length..].ToUpperInvariant(),
            _ => entry.Source,
        };
    }

    public VersionEntry Entry { get; }
    public string TimestampLabel { get; }
    public string Snippet { get; }
    public string SourceLabel { get; }
    public string Id => Entry.Id;

    [ObservableProperty]
    private bool _isSelected;
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
/// The version-history hub: a running history of EVERY file the user has ever touched, newest
/// first. Picking a file shows its Time Machine timeline (Today / Yesterday / This Week / … /
/// Older), and picking a version shows a GitHub-style code-by-code diff (red removed, green added).
/// </summary>
public sealed partial class HistoryWindowViewModel : ObservableObject
{
    private readonly Func<string, string> _previewBuilder;
    private readonly Func<string, Task<bool>> _restore;
    private readonly VersionHistoryService _history;
    private List<VersionEntry> _allVersions = new();
    private string _currentFile = "";

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

    // ---- Diff view (GitHub/VS Code style) ----
    public ObservableCollection<DiffRowViewModel> DiffRows { get; } = new();

    [ObservableProperty]
    private bool _showDiff = true;

    [ObservableProperty]
    private string _diffTitle = "Select a version to see its changes";

    [ObservableProperty]
    private string _diffStats = "";

    [ObservableProperty]
    private string _diffHeader = "";

    [RelayCommand]
    private void ShowDiffView() => ShowDiff = true;

    [RelayCommand]
    private void ShowPreviewView() => ShowDiff = false;

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
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
    private async Task SelectFileAsync(FileSummaryViewModel file)
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
        _currentFile = filePath;
        FileName = Path.GetFileName(filePath);
        Bands.Clear();
        DiffRows.Clear();
        Selected = null;
        SelectedHeader = "Select a version";
        DiffTitle = "Select a version to see its changes";
        DiffStats = "";

        try
        {
            var versions = await _history.GetVersionsAsync(filePath);
            _allVersions = versions;
            HasVersions = versions.Count > 0;
            if (versions.Count == 0) return;

            var now = DateTime.Now;
            var byBand = new Dictionary<string, List<VersionItemViewModel>>();
            foreach (var entry in versions)
            {
                var content = await _history.GetContentAsync(entry.Id) ?? "";
                var band = BandName(entry.CreatedAt.LocalDateTime, now);
                if (!byBand.TryGetValue(band, out var list))
                {
                    list = new List<VersionItemViewModel>();
                    byBand[band] = list;
                }
                list.Add(new VersionItemViewModel(entry, TimestampLabel(entry.CreatedAt.LocalDateTime, now), Snippet(content)));
            }

            foreach (var band in new[] { "Today", "Yesterday", "This Week", "This Month", "This Year", "Older" })
            {
                if (!byBand.TryGetValue(band, out var items)) continue;
                Bands.Add(new TimeBandViewModel(band, items));
            }

            if (Bands.Count > 0 && Bands[0].Items.Count > 0)
                SelectVersion(Bands[0].Items[0]);
        }
        catch
        {
            // Keep the hub alive if one file's history is unreadable.
        }
    }

    [RelayCommand]
    private void SelectVersion(VersionItemViewModel item)
    {
        if (Selected is not null) Selected.IsSelected = false;
        Selected = item;
        if (item is not null)
        {
            item.IsSelected = true;
            var t = item.Entry.CreatedAt.LocalDateTime;
            SelectedHeader = t.ToString("dddd, d MMMM yyyy") + " · " + t.ToString("HH:mm");
            _ = RefreshDiffAsync(item);
            _ = RefreshPreviewAsync(item);
        }
    }

    /// <summary>Diffs the selected version against the version before it (GitHub commit view):
    /// green = added in this version, red = removed. The oldest version diffs against nothing.</summary>
    private async Task RefreshDiffAsync(VersionItemViewModel item)
    {
        try
        {
            var selectedContent = await _history.GetContentAsync(item.Id) ?? "";
            var idx = _allVersions.FindIndex(v => v.Id == item.Id);
            var prevContent = (idx >= 0 && idx + 1 < _allVersions.Count)
                ? await _history.GetContentAsync(_allVersions[idx + 1].Id) ?? ""
                : "";

            var lines = LineDiff.Diff(prevContent, selectedContent);
            var rows = LineDiff.BuildSideBySide(lines);
            DiffRows.Clear();
            foreach (var row in rows) DiffRows.Add(new DiffRowViewModel(row));

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

    private async Task RefreshPreviewAsync(VersionItemViewModel item)
    {
        try
        {
            var content = await _history.GetContentAsync(item.Id) ?? "";
            PreviewHtml = _previewBuilder(content);
        }
        catch { /* keep the last preview */ }
    }

    [RelayCommand]
    private async Task RestoreAsync()
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

    private static string Snippet(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                return trimmed.Length <= 90 ? trimmed : trimmed[..90] + "…";
        }
        return content.Length <= 90 ? content : content[..90] + "…";
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
