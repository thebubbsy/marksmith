using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkSmith.Services;

/// <summary>
/// Reads a user's file while another app still has it open. <see cref="File.ReadAllText(string)"/>
/// asks for FileShare.Read, so it fails with "being used by another process" whenever the file is
/// open for writing elsewhere: a CSV open in Excel, a Markdown file in an editor that keeps a write
/// handle, a file OneDrive is syncing. These open with ReadWrite | Delete sharing instead and decode
/// the same way File.ReadAllText does (UTF-8 unless a byte-order mark says otherwise).
/// </summary>
public static class SharedFile
{
    public const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;

    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, Sharing);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, Sharing, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
