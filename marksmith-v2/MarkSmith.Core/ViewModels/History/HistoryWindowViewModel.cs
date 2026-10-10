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
        _label = string.IsNullOrEmpty(snippet) ? entry.Label ?? "" : snippet;
        _isStarred = entry.IsStarred;
        LinesAdded = entry.LinesAdded;
        LinesRemoved = entry.LinesRemoved;

        SourceLabel = SourceLabelFor(entry.Source);

        // Segoe Fluent Icons code points (rendered by a FontIcon), so the timeline matches the
        // rest of the app's iconography instead of mixing in colour emoji.
        SourceGlyph = GlyphFor(entry.Source);
    }

    // Segoe Fluent Icons code points (rendered by a FontIcon), so the timeline matches the rest of
    // the app's iconography instead of mixing in colour emoji. Exports use the Export menu's icon
    // for their format (Core CommandIcons).
    internal static string GlyphFor(string source) => source switch
    {
        "opened" => CommandIcons.Open,
        "autosave" => "\uE70F",                 // Edit
        "snapshot" or "manual" => CommandIcons.Save,
        "ingest" => "\uE896",                   // Download
        var s when s.StartsWith("export:") => s["export:".Length..] switch
        {
            "pdf" => CommandIcons.Pdf,
            "docx" or "word" => CommandIcons.Word,
            "pptx" or "powerpoint" => CommandIcons.PowerPoint,
            "epub" => CommandIcons.Epub,
            "html" => CommandIcons.WebPage,
            "msg" => CommandIcons.OutlookMessage,
            "eml" or "email" => CommandIcons.Email,
            _ => CommandIcons.Export,
        },
        _ => "\uE823",                          // Clock
    };

    internal static string SourceLabelFor(string source) => source switch
    {
        "opened" => "Opened",
        "autosave" => "Auto-Save",
        "snapshot" or "manual" => "Checkpoint",
        "ingest" => "AI Ingest",
        var s when s.StartsWith("export:") => "Export · " + s["export:".Length..].ToUpperInvariant(),
        _ => source,
    };

    public VersionEntry Entry { get; }
    public string TimestampLabel { get; }
    public string SourceLabel { get; }
    public string SourceGlyph { get; }
    public string Id => Entry.Id;
    public int LinesAdded { get; }
    public int LinesRemoved { get; }

    public string DeltaBadge => LinesAdded > 0 && LinesRemoved > 0
        ? $"+{LinesAdded}  −{LinesRemoved}"
        : (LinesAdded > 0 ? $"+{LinesAdded}" : (LinesRemoved > 0 ? $"−{LinesRemoved}" : "0"));

    public bool HasDelta => LinesAdded > 0 || LinesRemoved > 0;

    public string DeltaTooltip => $"{LinesAdded} line{(LinesAdded == 1 ? "" : "s")} added, " +
                                  $"{LinesRemoved} removed since the version before";

    // The label is the row's second line. It used to be copied into a separate Snippet at
    // construction, so renaming a version changed nothing on screen until the timeline reloaded.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Snippet), nameof(HasLabel), nameof(HasSnippet), nameof(AutomationName))]
    private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarTooltip), nameof(AutomationName))]
    private bool _isStarred;

    [ObservableProperty]
    private bool _isSelected;

    public string Snippet => Label;

    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Snippet) &&
                              !string.Equals(Snippet, SourceLabel, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(Snippet, Entry.Source, StringComparison.OrdinalIgnoreCase);

    public string StarTooltip => IsStarred ? "Remove the star" : "Star this version so it is easy to find";

    /// <summary>What a screen reader announces for the row: time, kind, label and star.</summary>
    public string AutomationName =>
        $"{TimestampLabel}, {SourceLabel}" + (HasSnippet ? ", " + Snippet : "") + (IsStarred ? ", starred" : "");
}

/// <summary>A file in the global edit history hub (every file ever touched).</summary>
public sealed partial class FileSummaryViewModel : ObservableObject
{
    public FileSummaryViewModel(VersionHistoryService.FileHistorySummary summary)
    {
        Summary = summary;
        IsScratch = summary.FilePath.StartsWith("scratch://", StringComparison.OrdinalIgnoreCase);
        Detail = IsScratch ? "Text typed or pasted without a file" : DisplayPath(summary);
        VersionCountLabel = summary.VersionCount + (summary.VersionCount == 1 ? " version" : " versions");
        LastModifiedLabel = summary.LastModified.LocalDateTime.ToString("d MMM yyyy · HH:mm");
    }

    // The store keys paths in lower case; the file name half carries the on-disk spelling.
    private static string DisplayPath(VersionHistoryService.FileHistorySummary summary)
    {
        try { return Path.Combine(Path.GetDirectoryName(summary.FilePath) ?? "", summary.FileName); }
        catch { return summary.FilePath; }
    }

    public VersionHistoryService.FileHistorySummary Summary { get; }
    public bool IsScratch { get; }
    public string FileName => IsScratch ? "Unsaved text" : Summary.FileName;
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
    private readonly Func<string, string?>? _editorTextFor;
    private readonly VersionHistoryService _history;
    private List<VersionEntry> _allVersions = new();
    private string _currentFile = "";
    private int _selectionToken;

    /// <param name="editorTextFor">Given a history file key, returns the editor's current text when
    /// that file is the one open in the editor (or the unsaved scratch text when nothing is), else
    /// null. Checkpoints capture this text; without it the window cannot take one.</param>
    public HistoryWindowViewModel(
        Func<string, string> previewBuilder,
        Func<string, Task<bool>> restore,
        VersionHistoryService? history = null,
        string? initialFilePath = null,
        Func<string, string?>? editorTextFor = null)
    {
        _previewBuilder = previewBuilder;
        _restore = restore;
        _editorTextFor = editorTextFor;
        _history = history ?? AppServices.VersionHistory;
        InitialFilePath = initialFilePath ?? "";
    }

    private string InitialFilePath { get; }

    public ObservableCollection<FileSummaryViewModel> Files { get; } = new();
    public ObservableCollection<TimeBandViewModel> Bands { get; } = new();

    [ObservableProperty]
    private string _fileName = "No document selected";

    [ObservableProperty]
    private string _selectedHeader = "";

    [ObservableProperty]
    private string _previewHtml = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFiles), nameof(IsLoading))]
    private bool _isLoaded;

    public bool IsLoading => !IsLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFiles))]
    private bool _hasFiles;

    public bool ShowNoFiles => IsLoaded && !HasFiles && string.IsNullOrEmpty(LoadError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFiles), nameof(HasLoadError))]
    private string _loadError = "";

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoVersions), nameof(ShowNoMatches), nameof(ShowTimeline))]
    private bool _hasVersions;

    /// <summary>The selected file has versions, but the search / starred filter hides all of them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches), nameof(ShowTimeline))]
    private bool _filterHidesAll;

    public bool ShowNoVersions => SelectedFile is not null && !HasVersions;
    public bool ShowNoMatches => HasVersions && FilterHidesAll;
    public bool ShowTimeline => HasVersions && !FilterHidesAll;

    [ObservableProperty]
    private string _noMatchesText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isRestoring;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowDiffPlaceholder))]
    private VersionItemViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoVersions))]
    private FileSummaryViewModel? _selectedFile;

    /// <summary>The selected file is the document open in the editor, so a checkpoint can capture
    /// its live text and a restore lands in the right document.</summary>
    [ObservableProperty]
    private bool _isSelectedFileOpen;

    [ObservableProperty]
    private string _checkpointTooltip = "Save the editor's current text as a named, starred version (Ctrl+Shift+S)";

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
    [NotifyPropertyChangedFor(nameof(ShowUnifiedRows), nameof(ShowNoChanges))]
    private bool _showUnifiedDiff = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSplitRows), nameof(ShowNoChanges))]
    private bool _showSplitDiff = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiffPlaceholder), nameof(HasDiffStats))]
    private bool _showPreview = false;

    [ObservableProperty]
    private string _diffTitle = "Select a version to see its changes";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffStats))]
    private string _diffStats = "";

    // Line counts describe the diff, so the pill hides while the rendered preview is showing.
    public bool HasDiffStats => !string.IsNullOrEmpty(DiffStats) && !ShowPreview;

    [ObservableProperty]
    private string _diffHeader = "";

    /// <summary>The selected version is byte-for-byte the same text as the one before it (an
    /// export or reopen with no edits in between).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoChanges), nameof(ShowUnifiedRows), nameof(ShowSplitRows))]
    private bool _isUnchanged;

    public bool ShowNoChanges => IsUnchanged && !ShowPreview && Selected is not null;
    public bool ShowUnifiedRows => ShowUnifiedDiff && !IsUnchanged;
    public bool ShowSplitRows => ShowSplitDiff && !IsUnchanged;
    public bool ShowDiffPlaceholder => Selected is null && !ShowPreview;

    /// <summary>A one-line confirmation or error shown in the window's InfoBar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

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
        OnPropertyChanged(nameof(ShowNoChanges));
        ApplyHeading();
    }

    // The comparison's heading ("Changes in this version / vs the version before"), kept apart
    // from DiffTitle/DiffHeader because Preview shows the version itself, not a comparison: the
    // inspector used to say "vs the version before" over a rendered page.
    private string _compareTitle = "Select a version to see its changes";
    private string _compareHeader = "";

    private void ApplyHeading()
    {
        if (ShowPreview && Selected is not null)
        {
            DiffTitle = "This version as a document";
            DiffHeader = $"{Selected.SourceLabel} \u00B7 {Selected.TimestampLabel}";
        }
        else
        {
            DiffTitle = _compareTitle;
            DiffHeader = _compareHeader;
        }
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
            LoadError = "";
            await ReloadFilesAsync(InitialFilePath);
        }
        catch (Exception ex)
        {
            // A store failure must never take the window down, but it must not pass for "no history".
            LoadError = "Couldn't read the version history: " + ex.Message;
        }
        finally
        {
            IsLoaded = true;
        }
    }

    /// <summary>Brings an open window up to date when it is asked for again: re-reads the store,
    /// so versions recorded since it opened appear, and lands on <paramref name="filePath"/>. The
    /// selected version is kept when that document is the one already showing.</summary>
    public async Task ShowFileAsync(string filePath)
    {
        var keep = SamePath(_currentFile, filePath) || _currentFile == filePath ? Selected?.Id : null;
        try
        {
            LoadError = "";
            await ReloadFilesAsync(filePath, keep);
        }
        catch (Exception ex)
        {
            LoadError = "Couldn't read the version history: " + ex.Message;
        }
        finally
        {
            IsLoaded = true;
        }
    }

    private async Task ReloadFilesAsync(string preferredPath, string? keepSelectedId = null)
    {
        Files.Clear();
        var overview = await _history.GetOverviewAsync();
        foreach (var summary in overview)
            Files.Add(new FileSummaryViewModel(summary));
        HasFiles = Files.Count > 0;

        // Pre-select the file that's open in the editor if it has history, else the most recent.
        var preferred = Files.FirstOrDefault(f => SamePath(f.Summary.FilePath, preferredPath))
            ?? Files.FirstOrDefault();

        if (preferred is not null)
        {
            if (SelectedFile is not null) SelectedFile.IsSelected = false;
            SelectedFile = preferred;
            preferred.IsSelected = true;
            await LoadTimelineAsync(preferred.Summary.FilePath, preferred.FileName, keepSelectedId);
        }
        else
        {
            SelectedFile = null;
            ClearTimeline();
            FileName = "No document selected";
            IsSelectedFileOpen = false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    [RelayCommand]
    public async Task SelectFileAsync(FileSummaryViewModel file)
    {
        if (SelectedFile is not null) SelectedFile.IsSelected = false;
        SelectedFile = file;
        if (file is not null)
        {
            file.IsSelected = true;
            await LoadTimelineAsync(file.Summary.FilePath, file.FileName);
        }
    }

    private void ClearTimeline()
    {
        _allVersions = new List<VersionEntry>();
        Bands.Clear();
        DiffRows.Clear();
        UnifiedDiffRows.Clear();
        Selected = null;
        HasVersions = false;
        FilterHidesAll = false;
        IsUnchanged = false;
        SelectedHeader = "";
        _compareTitle = "Select a version to see its changes";
        _compareHeader = "";
        ApplyHeading();
        DiffStats = "";
    }

    private async Task LoadTimelineAsync(string filePath, string displayName, string? keepSelectedId = null)
    {
        _selectionToken++;
        _currentFile = filePath;
        FileName = displayName;
        ClearTimeline();

        IsSelectedFileOpen = _editorTextFor?.Invoke(filePath) is not null;
        CheckpointTooltip = IsSelectedFileOpen
            ? "Save the editor's current text as a named, starred version (Ctrl+Shift+S)"
            : $"Open {displayName} in the editor to take a checkpoint of it";

        try
        {
            var versions = await _history.GetVersionsAsync(filePath);
            if (filePath != _currentFile) return;
            _allVersions = versions;
            HasVersions = versions.Count > 0;
            if (versions.Count == 0) return;

            ApplyTimelineFilter();

            var keep = keepSelectedId is null ? null : FindItem(keepSelectedId);
            if (keep is not null) SelectVersion(keep);
            else if (Bands.Count > 0 && Bands[0].Items.Count > 0)
                SelectVersion(Bands[0].Items[0]);
        }
        catch (Exception ex)
        {
            // Keep the hub alive if one file's history is unreadable, but say so.
            ShowNotice($"Couldn't read the history of {displayName}: {ex.Message}", isError: true);
        }
    }

    private VersionItemViewModel? FindItem(string id) =>
        Bands.SelectMany(b => b.Items).FirstOrDefault(i => i.Id == id);

    private void ApplyTimelineFilter()
    {
        // Rows are rebuilt on every filter change, so carry the selection across by id. Before,
        // typing in the search box dropped the selection highlight while the diff pane kept
        // showing that version, and starring "the selected version" then targeted a dead row.
        var selectedId = Selected?.Id;
        Bands.Clear();
        if (_allVersions.Count == 0)
        {
            FilterHidesAll = false;
            return;
        }

        var now = DateTime.Now;
        var q = SearchQuery?.Trim() ?? "";
        bool filterStarred = IsStarredOnlyFilter;

        var filtered = _allVersions.Where(v =>
        {
            if (filterStarred && !v.IsStarred) return false;
            if (!string.IsNullOrEmpty(q))
            {
                // Match what the row shows (label, "Auto-Save", "Export · PDF", the time) as well
                // as the raw source key, so whatever the user can read they can also search for.
                var local = v.CreatedAt.LocalDateTime;
                return (v.Label?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                       || v.Source.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || VersionItemViewModel.SourceLabelFor(v.Source).Contains(q, StringComparison.OrdinalIgnoreCase)
                       || TimestampLabel(local, now).Contains(q, StringComparison.OrdinalIgnoreCase)
                       || local.ToString("dddd d MMMM yyyy").Contains(q, StringComparison.OrdinalIgnoreCase);
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
            var item = new VersionItemViewModel(entry, TimestampLabel(entry.CreatedAt.LocalDateTime, now), entry.Label ?? "");
            if (item.Id == selectedId)
            {
                item.IsSelected = true;
                Selected = item;
            }
            list.Add(item);
        }

        foreach (var band in new[] { "Today", "Yesterday", "This Week", "This Month", "This Year", "Older" })
        {
            if (!byBand.TryGetValue(band, out var items) || items.Count == 0) continue;
            Bands.Add(new TimeBandViewModel(band, items));
        }

        FilterHidesAll = filtered.Count == 0;
        NoMatchesText = filterStarred && string.IsNullOrEmpty(q)
            ? "No starred versions of this document yet. Star a version to keep it handy."
            : filterStarred
                ? $"No starred versions match “{q}”."
                : $"No versions match “{q}”.";
    }

    [RelayCommand]
    private void ClearFilters()
    {
        IsStarredOnlyFilter = false;
        SearchQuery = "";
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
        var idx = _allVersions.FindIndex(v => v.Id == target.Id);
        if (idx >= 0) _allVersions[idx] = _allVersions[idx] with { IsStarred = isStarred };

        // Un-starring a row while "Starred" is on must take it off the list, like any filter would.
        if (!isStarred && IsStarredOnlyFilter) ApplyTimelineFilter();
    }

    [RelayCommand]
    public async Task RenameVersionAsync((VersionItemViewModel? item, string newLabel) args)
    {
        var target = args.item ?? Selected;
        if (target == null) return;
        var label = (args.newLabel ?? "").Trim();
        if (await _history.SetLabelAsync(target.Id, label.Length == 0 ? null : label))
        {
            target.Label = label;
            var idx = _allVersions.FindIndex(v => v.Id == target.Id);
            if (idx >= 0) _allVersions[idx] = _allVersions[idx] with { Label = label.Length == 0 ? null : label };
        }
        else
        {
            ShowNotice("Couldn't rename that version. It may have been removed.", isError: true);
        }
    }

    [RelayCommand]
    public async Task DeleteVersionAsync(VersionItemViewModel? item)
    {
        var target = item ?? Selected;
        if (target == null) return;
        if (!await _history.DeleteVersionAsync(target.Id))
        {
            ShowNotice("Couldn't delete that version. It may already be gone.", isError: true);
            return;
        }

        var wasSelected = Selected?.Id == target.Id;
        _allVersions.RemoveAll(v => v.Id == target.Id);
        if (_allVersions.Count == 0)
        {
            // That was the file's last version, so the file leaves the vault as well.
            await ReloadFilesAsync("");
            ShowNotice("Deleted the last version. The document no longer has any history.");
            return;
        }

        if (wasSelected) Selected = null;
        ApplyTimelineFilter();
        if (wasSelected && Bands.Count > 0 && Bands[0].Items.Count > 0)
            SelectVersion(Bands[0].Items[0]);
        RefreshSelectedFileCount();
        ShowNotice("Version deleted.");
    }

    private void RefreshSelectedFileCount()
    {
        if (SelectedFile is null) return;
        var i = Files.IndexOf(SelectedFile);
        if (i < 0) return;
        var s = SelectedFile.Summary with { VersionCount = _allVersions.Count };
        var replacement = new FileSummaryViewModel(s) { IsSelected = true };
        Files[i] = replacement;
        SelectedFile = replacement;
    }

    /// <summary>Saves the editor's current text for the selected document as a starred, labelled
    /// version. It used to copy the *selected old version* instead, so a "checkpoint" taken while
    /// browsing last week's history silently saved last week's text as today's milestone.</summary>
    [RelayCommand]
    public async Task TakeSnapshotAsync(string? label)
    {
        if (string.IsNullOrEmpty(_currentFile)) return;
        var text = _editorTextFor?.Invoke(_currentFile);
        if (text is null)
        {
            ShowNotice($"Open {FileName} in the editor first. A checkpoint saves the editor's current text.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowNotice("The editor is empty, so there's nothing to save as a checkpoint.", isError: true);
            return;
        }

        var name = string.IsNullOrWhiteSpace(label) ? "Checkpoint" : label.Trim();
        if (await _history.CaptureAsync(_currentFile, text, "snapshot", name, isStarred: true))
        {
            SearchQuery = "";
            IsStarredOnlyFilter = false;
            await ReloadFilesAsync(_currentFile);
            ShowNotice($"Checkpoint “{name}” saved.");
            return;
        }

        // Nothing changed since the latest version, so the store refused a duplicate. Mark that
        // version as the checkpoint instead of silently doing nothing.
        var latest = _allVersions.FirstOrDefault();
        if (latest is null) return;
        await _history.SetLabelAsync(latest.Id, name);
        if (!latest.IsStarred) await _history.ToggleStarAsync(latest.Id);
        SearchQuery = "";
        IsStarredOnlyFilter = false;
        await LoadTimelineAsync(_currentFile, FileName, latest.Id);
        ShowNotice($"No changes since the latest version, so it was starred and named “{name}”.");
    }

    /// <summary>Diffs the selected version against the version before it.</summary>
    private async Task RefreshDiffAsync(VersionItemViewModel item, int token)
    {
        try
        {
            var selectedContent = await _history.GetContentAsync(item.Id) ?? "";
            if (token != _selectionToken) return;
            var idx = _allVersions.FindIndex(v => v.Id == item.Id);
            bool hasPrevious = idx >= 0 && idx + 1 < _allVersions.Count;
            var prevContent = hasPrevious
                ? await _history.GetContentAsync(_allVersions[idx + 1].Id) ?? ""
                : "";
            if (token != _selectionToken) return;

            // The LCS diff is O(n·m); keep it off the UI thread for long documents.
            // A first version is all additions. Diffing it against "" also listed a phantom
            // removed blank line, so every first version opened with "- (blank)" and "1 removed".
            var lines = hasPrevious
                ? await Task.Run(() => LineDiff.Diff(prevContent, selectedContent))
                : LineDiff.AllAdded(selectedContent);
            if (token != _selectionToken) return;
            var segments = LineDiff.Collapse(lines);

            DiffRows.Clear();
            UnifiedDiffRows.Clear();
            foreach (var row in BuildSplitRows(segments)) DiffRows.Add(row);
            foreach (var u in BuildUnifiedRows(segments)) UnifiedDiffRows.Add(u);

            int added = lines.Count(l => l.Kind == LineDiff.Kind.Added);
            int removed = lines.Count(l => l.Kind == LineDiff.Kind.Removed);
            IsUnchanged = segments.Count == 0;
            _compareTitle = !hasPrevious ? "First version"
                : IsUnchanged ? "No text changes"
                : "Changes in this version";
            // Short on purpose: it shares a row with the three view toggles and the stats pill.
            // The selected version's full date is already in the timeline header.
            _compareHeader = hasPrevious ? "vs the version before" : "nothing earlier to compare";
            ApplyHeading();
            DiffStats = IsUnchanged ? "" : $"{added} added · {removed} removed";
        }
        catch (Exception ex)
        {
            if (token == _selectionToken)
                ShowNotice("Couldn't load that version's text: " + ex.Message, isError: true);
        }
    }

    /// <summary>Unified rows for a collapsed diff, with one fold row per hidden run.</summary>
    internal static List<LineDiff.UnifiedRow> BuildUnifiedRows(IReadOnlyList<LineDiff.Segment> segments)
    {
        var rows = new List<LineDiff.UnifiedRow>(segments.Count);
        var pending = new List<LineDiff.Line>();
        void Flush()
        {
            if (pending.Count == 0) return;
            rows.AddRange(LineDiff.BuildUnified(pending));
            pending.Clear();
        }
        foreach (var seg in segments)
        {
            if (seg.Line is not null) { pending.Add(seg.Line); continue; }
            Flush();
            rows.Add(new LineDiff.UnifiedRow(LineDiff.Kind.Same, null, null, LineDiff.HiddenLabel(seg.HiddenCount), "", IsGap: true));
        }
        Flush();
        return rows;
    }

    /// <summary>Side-by-side rows for a collapsed diff. Each run between folds is paired on its own,
    /// so a removed line is never matched with an added line from a different hunk.</summary>
    internal static List<DiffRowViewModel> BuildSplitRows(IReadOnlyList<LineDiff.Segment> segments)
    {
        var rows = new List<DiffRowViewModel>(segments.Count);
        var pending = new List<LineDiff.Line>();
        void Flush()
        {
            if (pending.Count == 0) return;
            rows.AddRange(LineDiff.BuildSideBySide(pending).Select(r => new DiffRowViewModel(r)));
            pending.Clear();
        }
        foreach (var seg in segments)
        {
            if (seg.Line is not null) { pending.Add(seg.Line); continue; }
            Flush();
            rows.Add(DiffRowViewModel.Gap(LineDiff.HiddenLabel(seg.HiddenCount)));
        }
        Flush();
        return rows;
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

    private bool CanRestore() => Selected is not null && !IsRestoring;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    public async Task RestoreAsync()
    {
        if (Selected is null || IsRestoring) return;
        IsRestoring = true;
        var when = Selected.TimestampLabel;
        try
        {
            if (await _restore(Selected.Id))
                ShowNotice($"Restored the {when} version into the editor. Press Ctrl+Z in the editor to undo.");
            else
                ShowNotice("Couldn't restore that version. Its stored text may be missing.", isError: true);
        }
        finally { IsRestoring = false; }
    }

    public void ShowNotice(string message, bool isError = false)
    {
        NoticeIsError = isError;
        Notice = message;
    }

    [RelayCommand]
    private void DismissNotice() => Notice = "";

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

/// <summary>One side-by-side row of the version diff (left = previous, right = selected), or a
/// fold row standing in for a run of unchanged lines.</summary>
public sealed class DiffRowViewModel
{
    public DiffRowViewModel(LineDiff.Row row)
    {
        Left = row.Left is null ? null : new DiffCellViewModel(row.Left);
        Right = row.Right is null ? null : new DiffCellViewModel(row.Right);
    }

    private DiffRowViewModel(string gapText)
    {
        IsGap = true;
        GapText = gapText;
    }

    public static DiffRowViewModel Gap(string text) => new(text);

    public DiffCellViewModel? Left { get; }
    public DiffCellViewModel? Right { get; }
    public bool IsGap { get; }
    public bool IsLine => !IsGap;
    public string GapText { get; } = "";
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
