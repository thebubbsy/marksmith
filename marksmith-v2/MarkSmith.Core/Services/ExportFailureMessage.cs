using System;
using System.IO;
using System.Reflection;

namespace MarkSmith.Services;

/// <summary>
/// Turns an export exception into one status-bar sentence that names the format, the file, and what
/// to do next. The raw .NET message ("The process cannot access the file 'C:\…\Report.docx' because it
/// is being used by another process.") was shown verbatim after "Error:", which is accurate but reads
/// as a crash and never says the fix (close it in Word).
/// </summary>
public static class ExportFailureMessage
{
    // Win32 error codes surfaced through IOException.HResult (0x8007xxxx).
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int HandleDiskFull = unchecked((int)0x80070027);
    private const int DiskFull = unchecked((int)0x80070070);

    public static string Describe(string kind, Exception ex, string? outputPath)
    {
        ex = Unwrap(ex);
        var file = string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFileName(outputPath);
        var folder = string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetDirectoryName(outputPath);
        var prefix = $"{kind} export failed: ";

        return ex switch
        {
            PathTooLongException =>
                prefix + "the output path is too long. Pick a shorter output folder or file-name template.",
            DirectoryNotFoundException =>
                prefix + (folder is null ? "the output folder no longer exists." : $"the folder {folder} no longer exists. Choose another output folder."),
            UnauthorizedAccessException =>
                prefix + (file is not null && IsReadOnly(outputPath!)
                    ? $"{file} is read-only. Clear its read-only flag or export under another name."
                    : $"MarkSmith isn't allowed to write to {folder ?? "the output folder"}. Choose another output folder."),
            IOException io when io.HResult is SharingViolation or LockViolation =>
                prefix + $"{file ?? "the output file"} is open in another program. Close it there and export again.",
            IOException io when io.HResult is DiskFull or HandleDiskFull =>
                prefix + "there isn't enough free space on the drive.",
            _ => prefix + TrimMessage(ex.Message),
        };
    }

    /// <summary>
    /// Throws a sharing-violation <see cref="IOException"/> right away when <paramref name="path"/>
    /// exists and another program holds it open (Word and Acrobat lock what they show). Checking up
    /// front means the user hears about it in a moment, not after a long render that then can't be
    /// written — and the PDF renderer only reports a bare "false" in that case.
    /// </summary>
    public static void ThrowIfLocked(string path)
    {
        if (IsLocked(path))
            throw new IOException($"'{path}' is being used by another process.", SharingViolation);
    }

    public static bool IsLocked(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException io) when (io.HResult is SharingViolation or LockViolation)
        {
            return true;
        }
        catch
        {
            // Read-only or access-denied files aren't "locked"; the real write reports those.
            return false;
        }
    }

    private static bool IsReadOnly(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).IsReadOnly; }
        catch { return false; }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } or TargetInvocationException && ex.InnerException is not null)
            ex = ex.InnerException;
        return ex;
    }

    // Status-bar text is one line: keep the first sentence/line of very long messages.
    private static string TrimMessage(string message)
    {
        var msg = (message ?? "").Trim();
        var nl = msg.IndexOfAny(new[] { '\r', '\n' });
        if (nl > 0) msg = msg[..nl].Trim();
        if (msg.Length == 0) return "something went wrong.";
        return msg.EndsWith('.') ? msg : msg + ".";
    }
}
