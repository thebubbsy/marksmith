namespace MdToPdf.Services;

// Crash-safe text writes. File.WriteAllText truncates-then-writes, so a crash, power loss, or two
// concurrent writers can leave a truncated/interleaved file — and for settings.json that means the
// user's entire configuration silently resets to defaults on next launch (the Load paths all fall
// back to `new()` on parse failure). Writing to a sibling temp file and atomically replacing the
// target removes the torn-write window: a reader sees either the whole old file or the whole new
// one, never a fragment. A process-wide lock serializes writers to the same path.
public static class AtomicFile
{
    private static readonly object Gate = new();

    public static void WriteAllText(string path, string contents)
    {
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);

            if (File.Exists(path))
            {
                // File.Replace is atomic on NTFS and preserves the destination's ACLs; the backup
                // arg is null (we don't keep one). Fall back to delete+move on filesystems that
                // reject Replace (some network shares).
                try { File.Replace(tmp, path, null); }
                catch (PlatformNotSupportedException) { File.Delete(path); File.Move(tmp, path); }
                catch (IOException) { File.Delete(path); File.Move(tmp, path); }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
