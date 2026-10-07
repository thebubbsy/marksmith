using System.Diagnostics;

namespace MarkSmith.Services.Email;

/// <summary>Where "Email draft" writes the file it hands to the mail app: a folder under the app's
/// data directory (so <c>MARKSMITH_CONFIG_DIR</c> redirects it in tests). Drafts hold the user's
/// content, so anything older than a week is cleared the next time a draft is written.</summary>
public static class EmailOutbox
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    public static string Directory => Path.Combine(AppPaths.ConfigDir, "outbox");

    /// <summary>Opens a file with its default app. Replaceable so tests never launch Outlook.
    /// Returns false when Windows has no app for the file type.</summary>
    public static Func<string, bool> Open { get; set; } = path =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    };

    /// <summary>A free path in the outbox named after <paramref name="label"/>: "Plan.eml", then
    /// "Plan (2).eml" — never overwriting a draft that may still be open in Outlook.</summary>
    public static string PathFor(string label, string extension)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var stem = SafeStem(label);
        var path = Path.Combine(Directory, $"{stem}.{extension}");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(Directory, $"{stem} ({n}).{extension}");
        return path;
    }

    /// <summary><paramref name="label"/> as a file name: no characters Windows rejects, at most 64
    /// long, and "Email draft" when nothing is left.</summary>
    public static string SafeStem(string? label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string((label ?? "").Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        if (stem.Length > 64) stem = stem[..64].TrimEnd();
        return stem.Length == 0 ? "Email draft" : stem;
    }

    /// <summary>Deletes drafts older than <see cref="KeepFor"/>. Files still open elsewhere are
    /// skipped; a failed clean-up never blocks a new draft.</summary>
    public static int Clean(DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.Now) - KeepFor;
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return removed;
    }

    /// <summary>Removes every draft (Settings ▸ "Clear outbox"). Returns how many went.</summary>
    public static int Clear()
    {
        var removed = 0;
        if (!System.IO.Directory.Exists(Directory)) return 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            try { File.Delete(file); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
