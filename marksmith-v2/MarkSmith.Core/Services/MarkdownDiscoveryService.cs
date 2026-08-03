using Microsoft.Win32;

namespace MarkSmith.Services;

// One .md file surfaced in Step 1's picker. Pinned = the user explicitly opened it before (kept at
// the top); the rest are discovered on disk and ordered by how recently they were modified.
public sealed record MarkdownFileEntry(string Path, string Name, string Detail, bool Pinned);

// Finds Markdown files the user might actually want to convert by scanning the usual places —
// Downloads (resolved from the registry, since it can be relocated), Documents, Desktop, and any
// OneDrive roots — newest first. Everything is best-effort and swallows access errors so a locked
// or missing folder never breaks the picker.
public static class MarkdownDiscoveryService
{
    private const int MaxResults = 25;
    private const int MaxDepth = 4;      // don't spelunk arbitrarily deep
    private const int DirBudget = 6000;  // hard cap on directories visited, keeps the scan snappy

    public static Task<List<MarkdownFileEntry>> DiscoverAsync(IEnumerable<string> pinned) => Task.Run(() =>
    {
        var pinnedList = pinned.ToList();
        var roots = ScanRoots();
        // Also scan the folders where the user has opened .md files before — siblings tend to
        // live together, so this surfaces the rest of a notes/project folder.
        foreach (var p in pinnedList)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(p));
                if (dir is not null && Directory.Exists(dir) && !roots.Contains(dir, PathEquality.Comparer))
                    roots.Add(dir);
            }
            catch { /* skip */ }
        }

        var files = new List<string>();
        var budget = new[] { DirBudget };
        foreach (var root in roots)
            Collect(root, MaxDepth, files, budget);

        var result = new List<MarkdownFileEntry>();
        var seen = new HashSet<string>(PathEquality.Comparer);

        // Pinned (explicitly-opened) files first, in their existing order, if they still exist.
        foreach (var p in pinnedList)
        {
            try
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(full) && seen.Add(full))
                    result.Add(Make(full, pinned: true));
            }
            catch { /* skip unreadable path */ }
        }

        // Then discovered files, newest-modified first, excluding any already pinned.
        var discovered = new List<(string Path, DateTime Modified)>();
        foreach (var f in files)
        {
            try
            {
                var full = Path.GetFullPath(f);
                if (seen.Contains(full)) continue;
                discovered.Add((full, File.GetLastWriteTime(full)));
            }
            catch { /* skip */ }
        }
        foreach (var (path, _) in discovered.OrderByDescending(d => d.Modified))
        {
            if (result.Count >= MaxResults) break;
            if (seen.Add(path)) result.Add(Make(path, pinned: false));
        }
        return result;
    });

    private static List<string> ScanRoots()
    {
        var roots = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !roots.Contains(p, PathEquality.Comparer)) roots.Add(p); }

        Add(DownloadsFolder());
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        // OneDrive roots (personal + any "OneDrive - Company"): include their Documents/Desktop.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            foreach (var dir in Directory.GetDirectories(profile, "OneDrive*"))
            {
                Add(dir);
                Add(Path.Combine(dir, "Documents"));
                Add(Path.Combine(dir, "Desktop"));
            }
        }
        catch { /* profile unreadable — skip OneDrive */ }

        return roots;
    }

    // Downloads has no SpecialFolder enum; its real (possibly relocated) path lives in the registry.
    private static string? DownloadsFolder()
    {
        try
        {
            // Shell Folders lives in the Windows registry only; skip on other platforms so the
            // net8.0 engine stays cross-platform clean (CA1416) and falls back to ~/Downloads.
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string v && v.Length > 0)
                    return Environment.ExpandEnvironmentVariables(v);
            }
        }
        catch { /* fall through to the default */ }
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(def) ? def : null;
    }

    private static void Collect(string dir, int depth, List<string> outp, int[] budget)
    {
        if (budget[0] <= 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                    outp.Add(f);
        }
        catch { return; } // access denied / gone — stop this branch

        if (depth <= 0) return;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return; }
        foreach (var s in subs)
        {
            if (budget[0]-- <= 0) return;
            var name = Path.GetFileName(s);
            if (name.StartsWith('.') || name is "node_modules" or "AppData" or "$Recycle.Bin" or ".git") continue;
            try { if ((new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0) continue; } // don't follow junctions
            catch { continue; }
            Collect(s, depth - 1, outp, budget);
        }
    }

    private static MarkdownFileEntry Make(string path, bool pinned)
    {
        var name = Path.GetFileName(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "") is { Length: > 0 } f ? f : path;
        string detail;
        try { detail = $"{folder} · {Ago(File.GetLastWriteTime(path))}"; }
        catch { detail = folder; }
        return new MarkdownFileEntry(path, name, (pinned ? "★ " : "") + detail, pinned);
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return t.ToString("d MMM yyyy");
    }
}
