using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.WebApp.Server.Documents;

/// <summary>
/// A loaded DOCX held in memory, owned by exactly one session. The session keeps this object
/// (and its backing stream) alive between saves; the collaboration server applies operations
/// against it through the OpenXml SDK public API and re-renders full HTML after each batch.
///
/// MarkSmith.Core is never touched: this class is the entire document surface the server needs,
/// built directly on DocumentFormat.OpenXml (the same package Core references, same version).
/// </summary>
public sealed class DocxDocument : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly WordprocessingDocument _package;
    private bool _disposed;

    private DocxDocument(MemoryStream stream, WordprocessingDocument package)
    {
        _stream = stream;
        _package = package;
    }

    public WordprocessingDocument Package => _package;
    public MainDocumentPart MainPart => _package.MainDocumentPart!;
    public Body DocumentBody => MainPart.Document.Body!;

    /// <summary>Loads a DOCX from raw bytes (read-write, in memory).</summary>
    public static DocxDocument Open(byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new InvalidOperationException("Cannot open an empty DOCX payload.");

        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;
        try
        {
            var package = WordprocessingDocument.Open(stream, true);
            if (package.MainDocumentPart?.Document?.Body is null)
                throw new InvalidOperationException("DOCX has no main document body.");
            return new DocxDocument(stream, package);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Creates a blank document with a single empty "Normal" paragraph.</summary>
    public static DocxDocument CreateBlank()
    {
        var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            main.Document = new Document(
                new Body(
                    new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }),
                        new Run())));
            package.Save();
        }
        stream.Position = 0;
        return Open(stream.ToArray());
    }

    /// <summary>Serializes the current in-memory document back to DOCX bytes.</summary>
    public byte[] SaveToBytes()
    {
        _package.Save();
        return _stream.ToArray();
    }

    /// <summary>
    /// The block model used by the OT layer: the body's top-level paragraphs and tables in
    /// document order. Index 0..N-1 matches the <c>block</c> field of text/block operations.
    /// </summary>
    public IReadOnlyList<BlockInfo> Blocks()
    {
        var list = new List<BlockInfo>();
        foreach (var child in DocumentBody.ChildElements)
        {
            switch (child)
            {
                case Paragraph p:
                    list.Add(new BlockInfo(list.Count, BlockKind.Paragraph, TextLength(p)));
                    break;
                case Table t:
                    list.Add(new BlockInfo(list.Count, BlockKind.Table, t.Elements<TableRow>().Count()));
                    break;
            }
        }
        return list;
    }

    /// <summary>Number of block-level children (paragraphs + tables) in the body.</summary>
    public int BlockCount => Blocks().Count;

    /// <summary>Total character length of a paragraph's live text (excludes deleted track-change runs).</summary>
    public static int TextLength(Paragraph paragraph)
    {
        int len = 0;
        foreach (var run in paragraph.Elements<Run>())
        {
            if (run.Elements<DeletedText>().Any()) continue;
            foreach (var t in run.Elements<Text>())
                len += t.Text?.Length ?? 0;
            foreach (var br in run.Elements<Break>())
                len += 1; // a <w:br/> counts as one position
        }
        return len;
    }

    /// <summary>Concatenated live text of a paragraph (used by the renderer and text ops).</summary>
    public static string ParagraphText(Paragraph paragraph)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in paragraph.Elements<Run>())
        {
            if (run.Elements<DeletedText>().Any()) continue;
            foreach (var t in run.Elements<Text>())
                sb.Append(t.Text ?? "");
            foreach (var br in run.Elements<Break>())
                sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The paragraph at a block index, or null when the block is a table / out of range.</summary>
    public Paragraph? ParagraphAt(int block)
    {
        int i = 0;
        foreach (var child in DocumentBody.ChildElements)
        {
            if (child is Paragraph p)
            {
                if (i == block) return p;
                i++;
            }
            else if (child is Table)
            {
                i++;
            }
        }
        return null;
    }

    /// <summary>The table at a block index, or null when the block is a paragraph / out of range.</summary>
    public Table? TableAt(int block)
    {
        int i = 0;
        foreach (var child in DocumentBody.ChildElements)
        {
            if (child is Paragraph)
            {
                i++;
            }
            else if (child is Table t)
            {
                if (i == block) return t;
                i++;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _package.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
    }
}

/// <summary>A top-level block in the body's block list.</summary>
public enum BlockKind { Paragraph, Table }

public sealed record BlockInfo(int Index, BlockKind Kind, int LengthOrRows)
{
    /// <summary>For paragraphs: character length. For tables: row count.</summary>
    public int Length => LengthOrRows;
}
