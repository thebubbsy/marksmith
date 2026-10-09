using System.IO;
using System.Text.Json;

namespace MarkSmith.Services;

/// <summary>
/// What MarkSmith leaves for itself when it closes to install an update: which version it was,
/// which it went to, and the document that was open. The next launch takes it (read once, then
/// deleted) to put the document back without asking and to say whether the install worked.
/// </summary>
public sealed record UpdateResumeNote(
    string FromVersion,
    string ToVersion,
    string? DocumentPath,
    string ReleaseUrl,
    DateTime WrittenUtc)
{
    public const string FileName = "update-resume.json";
    public const string InstallLogName = "update-install.log";

    /// <summary>A note older than this is from an install that never came back (the PC was
    /// switched off, say), and isn't worth acting on.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    public static string PathIn(string configDir) => Path.Combine(configDir, FileName);

    public static string InstallLogIn(string configDir) => Path.Combine(configDir, InstallLogName);

    public void Save(string configDir)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(PathIn(configDir), JsonSerializer.Serialize(this));
    }

    /// <summary>Reads and deletes the note. Null when there isn't one, it can't be read, or it is
    /// stale; the file is removed in every case so it can only ever act once.</summary>
    public static UpdateResumeNote? Take(string configDir, DateTime nowUtc)
    {
        var path = PathIn(configDir);
        if (!File.Exists(path)) return null;
        UpdateResumeNote? note = null;
        try { note = JsonSerializer.Deserialize<UpdateResumeNote>(File.ReadAllText(path)); }
        catch { /* a damaged note is just ignored */ }
        try { File.Delete(path); } catch { }
        if (note is null || string.IsNullOrWhiteSpace(note.ToVersion)) return null;
        var age = nowUtc - note.WrittenUtc;
        return age < TimeSpan.Zero || age > MaxAge ? null : note;
    }

    /// <summary>True when the running version is the one the note was installing (or newer).</summary>
    public bool InstalledIn(string currentVersion) =>
        UpdateService.Compare(currentVersion.TrimStart('v', 'V'), ToVersion.TrimStart('v', 'V')) >= 0;
}
