using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.WebApp.Server.Ot;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MarkSmith.WebApp.Server.Documents;

/// <summary>
/// Applies one <see cref="Operation"/> to an in-memory <see cref="DocxDocument"/> using only the
/// OpenXml SDK public API -- the same package MarkSmith.Core references (3.1.0), never Core
/// itself. Every op is validated against the current document state first; if the op cannot be
/// applied safely, <see cref="ApplyOutcome"/> carries a rejection reason and nothing is mutated.
///
/// The session guarantees single-threaded access per document (each session owns its document),
/// so no locking is needed here.
/// </summary>
public sealed class OpApplier
{
    private const long EmuPerPixel = 9525; // 96 dpi

    /// <summary>Applies a single operation. Returns the outcome; <see cref="ApplyOutcome.Ok"/> is
    /// false when the op was rejected (nothing mutated). <see cref="ApplyOutcome.NoOp"/> means the
    /// op was satisfied without a mutation (e.g. delete of already-deleted text).</summary>
    public ApplyOutcome Apply(DocxDocument doc, Operation op)
    {
        try
        {
            return op.Type switch
            {
                OpType.InsertText => InsertText(doc, op),
                OpType.DeleteText => DeleteText(doc, op),
                OpType.ApplyFormatting => ApplyFormatting(doc, op),
                OpType.InsertParagraph => InsertParagraph(doc, op),
                OpType.DeleteParagraph => DeleteParagraph(doc, op),
                OpType.InsertTable => InsertTable(doc, op),
                OpType.DeleteTable => DeleteTable(doc, op),
                OpType.InsertTableRow => InsertTableRow(doc, op),
                OpType.DeleteTableRow => DeleteTableRow(doc, op),
                OpType.InsertImage => InsertImage(doc, op),
                OpType.DeleteImage => DeleteImage(doc, op),
                OpType.InsertHyperlink => InsertHyperlink(doc, op),
                OpType.DeleteHyperlink => DeleteHyperlink(doc, op),
                OpType.AddComment => AddComment(doc, op),
                OpType.ResolveComment => ResolveComment(doc, op),
                OpType.ApplyTrackChange => ApplyTrackChange(doc, op),
                OpType.AcceptTrackChange => AcceptTrackChange(doc, op),
                OpType.RejectTrackChange => RejectTrackChange(doc, op),
                _ => ApplyOutcome.Reject($"operation type {op.Type} is not applicable to the document"),
            };
        }
        catch (Exception ex)
        {
            // The OpenXml SDK throws on structurally invalid mutations. Map to a clean rejection
            // so a hostile or buggy batch cannot corrupt the session document.
            return ApplyOutcome.Reject($"failed to apply {op.Type}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ text

    private ApplyOutcome InsertText(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"insertText: block {op.Block} is not a paragraph");
        var text = op.Text ?? "";
        var offset = op.Offset ?? -1;
        var len = DocxDocument.TextLength(p);
        if (offset < 0 || offset > len) return ApplyOutcome.Reject($"insertText: offset {offset} out of range (paragraph length {len})");

        var runs = LiveRuns(p);
        var (runIndex, runOffset) = Locate(runs, offset, len);

        if (runIndex < 0)
        {
            // Append at the end of the paragraph; remove empty placeholder runs.
            var emptyRuns = p.Elements<Run>().Where(r => RunTextLength(r) == 0 && !r.Elements<Drawing>().Any()).ToList();
            var newRun = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            if (emptyRuns.Count > 0 && p.Elements<Run>().Count() == emptyRuns.Count)
            {
                CopyRunProperties(emptyRuns[0], newRun);
                foreach (var er in emptyRuns) er.Remove();
            }
            p.AppendChild(newRun);
            return ApplyOutcome.Success(capturedText: text);
        }

        var target = runs[runIndex];
        var targetText = RunText(target);
        if (runOffset >= targetText.Length || runOffset <= 0)
        {
            var newRun = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            target.InsertBeforeSelf(newRun);
            return ApplyOutcome.Success(capturedText: text);
        }

        // Split the target run at runOffset: keep the tail in a new run, text goes between.
        var head = new Run(new Text(targetText[..runOffset]) { Space = SpaceProcessingModeValues.Preserve });
        var tail = new Run(new Text(targetText[runOffset..]) { Space = SpaceProcessingModeValues.Preserve });
        var ins = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        CopyRunProperties(target, head);
        CopyRunProperties(target, tail);
        // Order: head, insert, tail. InsertAfterSelf attaches immediately after `target`,
        // so insert tail first to keep [head][insert][tail].
        target.InsertBeforeSelf(head);
        target.InsertAfterSelf(tail);
        target.InsertAfterSelf(ins);
        target.Remove();
        return ApplyOutcome.Success(capturedText: text);
    }

    private ApplyOutcome DeleteText(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"deleteText: block {op.Block} is not a paragraph");
        var offset = op.Offset ?? -1;
        var length = op.Length ?? 0;
        var len = DocxDocument.TextLength(p);
        if (offset < 0 || offset + length > len) return ApplyOutcome.Reject($"deleteText: range [{offset},{offset + length}) out of bounds (paragraph length {len})");
        if (length == 0) return ApplyOutcome.None;

        var runs = LiveRuns(p);
        var (runIndex, runOffset) = Locate(runs, offset, len);
        if (runIndex < 0) return ApplyOutcome.None;

        var removed = new System.Text.StringBuilder();
        int remaining = length;
        int i = runIndex;
        while (remaining > 0 && i < runs.Count)
        {
            var run = runs[i];
            var texts = run.Elements<Text>().ToList();
            foreach (var t in texts)
            {
                var s = t.Text ?? "";
                if (remaining <= 0) break;
                if (runOffset > 0)
                {
                    if (runOffset >= s.Length)
                    {
                        runOffset -= s.Length;
                        continue;
                    }
                    var head = s[..runOffset];
                    var delLen = Math.Min(remaining, s.Length - runOffset);
                    removed.Append(s.Substring(runOffset, delLen));
                    var tail = s[(runOffset + delLen)..];
                    var newText = head + tail;
                    if (newText.Length > 0) t.Text = newText;
                    else t.Remove();
                    remaining -= delLen;
                    runOffset = 0;
                }
                else
                {
                    var delLen = Math.Min(remaining, s.Length);
                    removed.Append(s[..delLen]);
                    var tail = s[delLen..];
                    if (tail.Length > 0) t.Text = tail;
                    else t.Remove();
                    remaining -= delLen;
                }
            }
            if (!run.Elements<Text>().Any() && !run.Elements<Break>().Any() && !run.Elements<Drawing>().Any()) run.Remove();
            i++;
        }

        return removed.Length == 0
            ? ApplyOutcome.None
            : ApplyOutcome.Success(capturedText: removed.ToString());
    }

    private ApplyOutcome ApplyFormatting(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"applyFormatting: block {op.Block} is not a paragraph");
        var offset = op.Offset ?? -1;
        var length = op.Length ?? 0;
        var len = DocxDocument.TextLength(p);
        if (offset < 0 || offset + length > len) return ApplyOutcome.Reject($"applyFormatting: range out of bounds (paragraph length {len})");
        if (op.Format is null) return ApplyOutcome.Reject("applyFormatting: missing format payload");
        if (length == 0) return ApplyOutcome.None;

        var runs = LiveRuns(p);
        var (runIndex, runOffset) = Locate(runs, offset, len);
        if (runIndex < 0) return ApplyOutcome.None;

        int remaining = length;
        int i = runIndex;
        var touched = false;
        while (remaining > 0 && i < runs.Count)
        {
            var run = runs[i];
            var runLen = RunTextLength(run);
            if (runLen == 0) { i++; continue; }
            var take = Math.Min(remaining, runLen);
            var rpr = run.RunProperties ?? run.PrependChild(new RunProperties());
            if (op.Format.Bold is { } b) rpr.Bold = new Bold { Val = b };
            if (op.Format.Italic is { } it) rpr.Italic = new Italic { Val = it };
            if (op.Format.Underline is { } u) rpr.Underline = new Underline { Val = u ? UnderlineValues.Single : UnderlineValues.None };
            if (op.Format.Strikethrough is { } s) rpr.Strike = new Strike { Val = s };
            if (op.Format.Color is { } c) rpr.Color = new Color { Val = c.TrimStart('#') };
            touched = true;
            remaining -= take;
            i++;
        }
        return touched ? ApplyOutcome.Success() : ApplyOutcome.None;
    }

    // ------------------------------------------------------------------ blocks

    private ApplyOutcome InsertParagraph(DocxDocument doc, Operation op)
    {
        var block = op.Block ?? doc.BlockCount;
        if (block < 0 || block > doc.BlockCount) return ApplyOutcome.Reject($"insertParagraph: block {block} out of range (count {doc.BlockCount})");
        var style = string.IsNullOrWhiteSpace(op.Style) ? "Normal" : op.Style;

        var para = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = style }),
            new Run());

        if (block >= doc.BlockCount)
        {
            doc.DocumentBody.AppendChild(para);
        }
        else
        {
            var anchor = BlockElementAt(doc, block);
            anchor.InsertBeforeSelf(para);
        }
        return ApplyOutcome.Success(capturedStyle: style);
    }

    private ApplyOutcome DeleteParagraph(DocxDocument doc, Operation op)
    {
        var block = op.Block ?? -1;
        var blocks = doc.Blocks();
        if (block < 0 || block >= blocks.Count) return ApplyOutcome.Reject($"deleteParagraph: block {block} out of range");
        if (blocks[block].Kind != BlockKind.Paragraph) return ApplyOutcome.Reject($"deleteParagraph: block {block} is a table");

        var target = BlockElementAt(doc, block);
        var style = (target as Paragraph)?.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        target.Remove();

        // OOXML requires at least one paragraph in the body; restore an empty one if needed.
        if (!doc.DocumentBody.Elements<Paragraph>().Any())
        {
            doc.DocumentBody.AppendChild(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }), new Run()));
        }
        return ApplyOutcome.Success(capturedStyle: style);
    }

    private ApplyOutcome InsertTable(DocxDocument doc, Operation op)
    {
        var block = op.Block ?? doc.BlockCount;
        var rows = op.Rows ?? 1;
        var cols = op.Cols ?? 1;
        if (block < 0 || block > doc.BlockCount) return ApplyOutcome.Reject($"insertTable: block {block} out of range");
        if (rows < 1 || cols < 1 || rows > 100 || cols > 20) return ApplyOutcome.Reject($"insertTable: {rows}x{cols} outside supported bounds");

        var table = BuildTable(rows, cols);
        if (block >= doc.BlockCount) doc.DocumentBody.AppendChild(table);
        else BlockElementAt(doc, block).InsertBeforeSelf(table);
        return ApplyOutcome.Success();
    }

    private ApplyOutcome DeleteTable(DocxDocument doc, Operation op)
    {
        var block = op.Block ?? -1;
        var blocks = doc.Blocks();
        if (block < 0 || block >= blocks.Count) return ApplyOutcome.Reject($"deleteTable: block {block} out of range");
        if (blocks[block].Kind != BlockKind.Table) return ApplyOutcome.Reject($"deleteTable: block {block} is a paragraph");
        BlockElementAt(doc, block).Remove();
        if (!doc.DocumentBody.Elements<Paragraph>().Any())
        {
            doc.DocumentBody.AppendChild(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }), new Run()));
        }
        return ApplyOutcome.Success();
    }

    private ApplyOutcome InsertTableRow(DocxDocument doc, Operation op)
    {
        var table = doc.TableAt(op.Block ?? -1);
        if (table is null) return ApplyOutcome.Reject($"insertTableRow: block {op.Block} is not a table");
        var rows = table.Elements<TableRow>().ToList();
        var row = op.Row ?? rows.Count;
        if (row < 0 || row > rows.Count) return ApplyOutcome.Reject($"insertTableRow: row {row} out of range (count {rows.Count})");
        var cols = rows.Count > 0 ? rows[0].Elements<TableCell>().Count() : (op.Cols ?? 1);
        var newRow = BuildRow(cols);
        if (row >= rows.Count)
        {
            if (rows.Count > 0) rows.Last().InsertAfterSelf(newRow);
            else table.AppendChild(newRow);
        }
        else rows[row].InsertBeforeSelf(newRow);
        return ApplyOutcome.Success();
    }

    private ApplyOutcome DeleteTableRow(DocxDocument doc, Operation op)
    {
        var table = doc.TableAt(op.Block ?? -1);
        if (table is null) return ApplyOutcome.Reject($"deleteTableRow: block {op.Block} is not a table");
        var rows = table.Elements<TableRow>().ToList();
        var row = op.Row ?? -1;
        if (row < 0 || row >= rows.Count) return ApplyOutcome.Reject($"deleteTableRow: row {row} out of range (count {rows.Count})");
        if (rows.Count <= 1) return ApplyOutcome.Reject("deleteTableRow: cannot delete the last row of a table");
        rows[row].Remove();
        return ApplyOutcome.Success();
    }

    // ------------------------------------------------------------------ images

    private ApplyOutcome InsertImage(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"insertImage: block {op.Block} is not a paragraph");
        var offset = op.Offset ?? DocxDocument.TextLength(p);
        if (offset < 0 || offset > DocxDocument.TextLength(p)) return ApplyOutcome.Reject("insertImage: offset out of range");

        var (data, mime) = ResolveImage(op);
        if (data is null) return ApplyOutcome.Reject("insertImage: no dataUri or url provided");

        var imageId = op.ImageId ?? $"img-{Guid.NewGuid():N}";
        var part = CreateImagePart(doc.MainPart, data, mime);
        var relId = doc.MainPart.GetIdOfPart(part);

        var widthEmu = (long)((op.Width ?? 120) * EmuPerPixel);
        var heightEmu = (long)((op.Height ?? 120) * EmuPerPixel);

        var drawing = BuildInlineDrawing(imageId, relId, widthEmu, heightEmu, op.Alt ?? "");

        // Insert the drawing at the character offset by splitting the run chain.
        var runs = LiveRuns(p);
        var len = DocxDocument.TextLength(p);
        var (runIndex, runOffset) = Locate(runs, offset, len);
        var run = new Run(drawing);
        if (runIndex < 0) { p.AppendChild(run); return ApplyOutcome.Success(capturedImageId: imageId); }
        var target = runs[runIndex];
        var targetText = RunText(target);
        if (runOffset >= targetText.Length || runOffset <= 0)
        {
            target.InsertBeforeSelf(run);
        }
        else
        {
            var head = new Run(new Text(targetText[..runOffset]) { Space = SpaceProcessingModeValues.Preserve });
            var tail = new Run(new Text(targetText[runOffset..]) { Space = SpaceProcessingModeValues.Preserve });
            CopyRunProperties(target, head);
            CopyRunProperties(target, tail);
            // Order: head, run, tail (InsertAfterSelf attaches immediately after `target`).
            target.InsertBeforeSelf(head);
            target.InsertAfterSelf(tail);
            target.InsertAfterSelf(run);
            target.Remove();
        }
        return ApplyOutcome.Success(capturedImageId: imageId);
    }

    private ApplyOutcome DeleteImage(DocxDocument doc, Operation op)
    {
        var imageId = op.ImageId;
        if (string.IsNullOrWhiteSpace(imageId)) return ApplyOutcome.Reject("deleteImage: missing imageId");
        var hash = HashId(imageId);
        foreach (var para in doc.DocumentBody.Elements<Paragraph>())
        {
            foreach (var drawing in para.Elements<Drawing>())
            {
                var docPr = drawing.Descendants<DW.DocProperties>().FirstOrDefault();
                if (docPr?.Id?.Value == hash)
                {
                    drawing.Remove();
                    return ApplyOutcome.Success();
                }
            }
        }
        return ApplyOutcome.None; // already gone
    }

    // ------------------------------------------------------------------ hyperlinks

    private ApplyOutcome InsertHyperlink(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"insertHyperlink: block {op.Block} is not a paragraph");
        var offset = op.Offset ?? -1;
        var text = op.Text ?? op.Href ?? "";
        var url = op.Href ?? op.Url ?? "";
        var len = DocxDocument.TextLength(p);
        if (offset < 0 || offset > len) return ApplyOutcome.Reject("insertHyperlink: offset out of range");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text)) return ApplyOutcome.Reject("insertHyperlink: url and text are required");

        var rel = doc.MainPart.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true);
        var hyperlink = new Hyperlink(
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }))
        {
            Id = rel.Id,
        };

        var runs = LiveRuns(p);
        var (runIndex, runOffset) = Locate(runs, offset, len);
        if (runIndex < 0) { p.AppendChild(hyperlink); return ApplyOutcome.Success(); }
        var target = runs[runIndex];
        var targetText = RunText(target);
        if (runOffset >= targetText.Length || runOffset <= 0) target.InsertBeforeSelf(hyperlink);
        else
        {
            var head = new Run(new Text(targetText[..runOffset]) { Space = SpaceProcessingModeValues.Preserve });
            var tail = new Run(new Text(targetText[runOffset..]) { Space = SpaceProcessingModeValues.Preserve });
            CopyRunProperties(target, head);
            CopyRunProperties(target, tail);
            // Order: head, hyperlink, tail.
            target.InsertBeforeSelf(head);
            target.InsertAfterSelf(tail);
            target.InsertAfterSelf(hyperlink);
            target.Remove();
        }
        return ApplyOutcome.Success();
    }

    private ApplyOutcome DeleteHyperlink(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"deleteHyperlink: block {op.Block} is not a paragraph");
        var offset = op.Offset ?? -1;
        var length = op.Length ?? 0;
        var len = DocxDocument.TextLength(p);
        if (offset < 0 || offset + length > len) return ApplyOutcome.Reject("deleteHyperlink: range out of bounds");
        if (length == 0) return ApplyOutcome.None;

        var removed = false;
        foreach (var hyperlink in p.Elements<Hyperlink>().ToList())
        {
            var linkLen = hyperlink.Elements<Run>().Sum(r => RunTextLength(r));
            if (linkLen == 0) continue;
            var linkStart = PositionOf(p, hyperlink);
            var linkEnd = linkStart + linkLen;
            if (linkStart < offset + length && linkEnd > offset)
            {
                hyperlink.Remove();
                removed = true;
            }
        }
        return removed ? ApplyOutcome.Success() : ApplyOutcome.None;
    }

    // ------------------------------------------------------------------ comments

    private ApplyOutcome AddComment(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"addComment: block {op.Block} is not a paragraph");
        var offset = op.Offset ?? -1;
        var length = op.Length ?? 1;
        var commentId = op.CommentId ?? $"c-{Guid.NewGuid():N}";
        var len = DocxDocument.TextLength(p);
        if (offset < 0 || offset + length > len) return ApplyOutcome.Reject("addComment: anchor range out of bounds");
        if (string.IsNullOrWhiteSpace(op.Text)) return ApplyOutcome.Reject("addComment: comment text is required");

        var commentsPart = doc.MainPart.WordprocessingCommentsPart ?? doc.MainPart.AddNewPart<WordprocessingCommentsPart>();
        if (commentsPart.Comments is null) commentsPart.Comments = new Comments();
        var comments = commentsPart.Comments;

        // OOXML comment ids are strings (unlike the numeric commentReference ids in runs).
        var comment = new Comment
        {
            Id = commentId,
            Author = op.Author ?? "anonymous",
            Date = DateTime.UtcNow,
        };
        comment.AppendChild(new Paragraph(new Run(new Text(op.Text) { Space = SpaceProcessingModeValues.Preserve })));
        comments.AppendChild(comment);

        var anchor = p.Elements<Run>().FirstOrDefault() ?? p.AppendChild(new Run());
        var hashIdStr = HashId(commentId).ToString();
        anchor.InsertBeforeSelf(new CommentRangeStart { Id = hashIdStr });
        p.AppendChild(new CommentRangeEnd { Id = hashIdStr });
        p.AppendChild(new Run(new CommentReference { Id = hashIdStr }));

        return ApplyOutcome.Success(capturedCommentId: commentId);
    }

    private ApplyOutcome ResolveComment(DocxDocument doc, Operation op)
    {
        var commentId = op.CommentId;
        if (string.IsNullOrWhiteSpace(commentId)) return ApplyOutcome.Reject("resolveComment: missing commentId");
        var commentsPart = doc.MainPart.WordprocessingCommentsPart;
        if (commentsPart?.Comments is null) return ApplyOutcome.None;
        // Comment.Id is the raw string id (see AddComment).
        var comment = commentsPart.Comments.Elements<Comment>().FirstOrDefault(c => c.Id?.Value == commentId);
        if (comment is null) return ApplyOutcome.None;
        comment.Remove();
        var hashIdStr = HashId(commentId).ToString();
        foreach (var p in doc.DocumentBody.Elements<Paragraph>())
        {
            foreach (var start in p.Elements<CommentRangeStart>().Where(s => s.Id?.Value == hashIdStr).ToList()) start.Remove();
            foreach (var end in p.Elements<CommentRangeEnd>().Where(e => e.Id?.Value == hashIdStr).ToList()) end.Remove();
            foreach (var run in p.Elements<Run>().Where(r => r.Elements<CommentReference>().Any(cr => cr.Id?.Value == hashIdStr)).ToList()) run.Remove();
        }
        return ApplyOutcome.Success();
    }

    // ------------------------------------------------------------------ track changes

    private ApplyOutcome ApplyTrackChange(DocxDocument doc, Operation op)
    {
        var p = doc.ParagraphAt(op.Block ?? -1);
        if (p is null) return ApplyOutcome.Reject($"applyTrackChange: block {op.Block} is not a paragraph");
        var kind = op.Kind ?? "insert";
        var changeId = op.ChangeId ?? $"ch-{Guid.NewGuid():N}";
        var id = HashId(changeId);
        var author = op.Author ?? "anonymous";

        if (kind == "insert")
        {
            var offset = op.Offset ?? -1;
            var len = DocxDocument.TextLength(p);
            if (offset < 0 || offset > len) return ApplyOutcome.Reject("applyTrackChange(insert): offset out of range");
            var text = op.Text ?? "";
            if (text.Length == 0) return ApplyOutcome.None;

            var ins = new InsertedRun { Id = id.ToString(), Author = author, Date = DateTime.UtcNow };
            ins.AppendChild(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

            var runs = LiveRuns(p);
            var (runIndex, runOffset) = Locate(runs, offset, len);
            if (runIndex < 0) p.AppendChild(ins);
            else
            {
                var target = runs[runIndex];
                var targetText = RunText(target);
                if (runOffset >= targetText.Length || runOffset <= 0) target.InsertBeforeSelf(ins);
                else
                {
                    var head = new Run(new Text(targetText[..runOffset]) { Space = SpaceProcessingModeValues.Preserve });
                    var tail = new Run(new Text(targetText[runOffset..]) { Space = SpaceProcessingModeValues.Preserve });
                    CopyRunProperties(target, head);
                    CopyRunProperties(target, tail);
                    // Order: head, ins, tail.
                    target.InsertBeforeSelf(head);
                    target.InsertAfterSelf(tail);
                    target.InsertAfterSelf(ins);
                    target.Remove();
                }
            }
            return ApplyOutcome.Success();
        }

        if (kind == "delete")
        {
            var offset = op.Offset ?? -1;
            var length = op.Length ?? 0;
            var len = DocxDocument.TextLength(p);
            if (offset < 0 || offset + length > len) return ApplyOutcome.Reject("applyTrackChange(delete): range out of bounds");
            if (length == 0) return ApplyOutcome.None;

            var runs = LiveRuns(p);
            var (runIndex, runOffset) = Locate(runs, offset, len);
            if (runIndex < 0) return ApplyOutcome.None;

            var removed = new System.Text.StringBuilder();
            var del = new DeletedRun { Id = id.ToString(), Author = author, Date = DateTime.UtcNow };
            int remaining = length;
            int i = runIndex;
            while (remaining > 0 && i < runs.Count)
            {
                var run = runs[i];
                foreach (var t in run.Elements<Text>().ToList())
                {
                    var s = t.Text ?? "";
                    if (remaining <= 0) break;
                    if (runOffset > 0)
                    {
                        var keep = Math.Min(runOffset, s.Length);
                        runOffset -= keep;
                        if (keep == 0) continue;
                        if (keep < s.Length) t.Text = s[keep..];
                        else { t.Remove(); continue; } // whole node before deletion point
                        s = t.Text ?? "";
                    }
                    var take = Math.Min(remaining, s.Length);
                    if (take > 0)
                    {
                        removed.Append(s[..take]);
                        t.Text = s[take..];
                        if (t.Text.Length == 0) t.Remove();
                        remaining -= take;
                    }
                }
                if (!run.Elements<Text>().Any()) run.Remove();
                i++;
            }
            if (removed.Length == 0) return ApplyOutcome.None;
            del.AppendChild(new Run(new DeletedText { Text = removed.ToString() }));
            p.AppendChild(del);
            return ApplyOutcome.Success();
        }

        // kind == "format": best-effort rPr change (v1: applies the format to the range).
        return ApplyFormatting(doc, op) is { Ok: true } f ? f : ApplyOutcome.Reject($"applyTrackChange: unsupported kind '{kind}'");
    }

    private ApplyOutcome AcceptTrackChange(DocxDocument doc, Operation op)
    {
        var id = HashId(op.ChangeId ?? "");
        if (op.ChangeId is null) return ApplyOutcome.Reject("acceptTrackChange: missing changeId");
        var touched = false;
        foreach (var para in doc.DocumentBody.Elements<Paragraph>())
        {
            foreach (var ins in para.Elements<InsertedRun>().ToList())
            {
                if (ins.Id?.Value == id.ToString())
                {
                    // Keep content, drop the wrapper: move children back to the wrapper's position.
                    var content = ins.ChildElements.ToList();
                    foreach (var child in content) ins.InsertBeforeSelf(child);
                    ins.Remove();
                    touched = true;
                }
            }
            foreach (var del in para.Elements<DeletedRun>().ToList())
            {
                if (del.Id?.Value == id.ToString())
                {
                    del.Remove(); // drop deleted content
                    touched = true;
                }
            }
        }
        return touched ? ApplyOutcome.Success() : ApplyOutcome.None;
    }

    private ApplyOutcome RejectTrackChange(DocxDocument doc, Operation op)
    {
        var id = HashId(op.ChangeId ?? "");
        if (op.ChangeId is null) return ApplyOutcome.Reject("rejectTrackChange: missing changeId");
        var touched = false;
        foreach (var para in doc.DocumentBody.Elements<Paragraph>())
        {
            foreach (var ins in para.Elements<InsertedRun>().ToList())
            {
                if (ins.Id?.Value == id.ToString())
                {
                    ins.Remove(); // drop suggested insert
                    touched = true;
                }
            }
            foreach (var del in para.Elements<DeletedRun>().ToList())
            {
                if (del.Id?.Value == id.ToString())
                {
                    // Restore deleted content: move children back to the wrapper's position.
                    var content = del.ChildElements.ToList();
                    foreach (var child in content) del.InsertBeforeSelf(child);
                    del.Remove();
                    touched = true;
                }
            }
        }
        return touched ? ApplyOutcome.Success() : ApplyOutcome.None;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Runs in document order, including runs nested inside hyperlinks (excludes deleted-track runs).</summary>
    private static List<Run> LiveRuns(Paragraph p)
    {
        var runs = new List<Run>();
        foreach (var child in p.ChildElements)
        {
            switch (child)
            {
                case Run r when !r.Elements<DeletedText>().Any():
                    runs.Add(r);
                    break;
                case Hyperlink h:
                    runs.AddRange(h.Elements<Run>().Where(r => !r.Elements<DeletedText>().Any()));
                    break;
            }
        }
        return runs;
    }

    private static int RunTextLength(Run run)
    {
        var len = 0;
        foreach (var t in run.Elements<Text>()) len += t.Text?.Length ?? 0;
        return len;
    }

    private static string RunText(Run run)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in run.Elements<Text>()) sb.Append(t.Text ?? "");
        return sb.ToString();
    }

    /// <summary>Finds the run and intra-run offset containing a character offset; (-1,-1) means append at end.</summary>
    private static (int RunIndex, int RunOffset) Locate(List<Run> runs, int offset, int paragraphLen)
    {
        if (offset >= paragraphLen || runs.Count == 0) return (-1, -1);
        int cursor = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            var len = RunTextLength(runs[i]);
            if (cursor + len > offset) return (i, offset - cursor);
            cursor += len;
        }
        return (-1, -1);
    }

    /// <summary>Character position of an element within its paragraph (used by deleteHyperlink).</summary>
    private static int PositionOf(Paragraph p, OpenXmlElement el)
    {
        int pos = 0;
        foreach (var child in p.ChildElements)
        {
            if (ReferenceEquals(child, el)) return pos;
            pos += child switch
            {
                Run r => RunTextLength(r),
                Hyperlink h => h.Elements<Run>().Sum(RunTextLength),
                _ => 0,
            };
        }
        return pos;
    }

    private static OpenXmlElement BlockElementAt(DocxDocument doc, int block)
    {
        int i = 0;
        foreach (var child in doc.DocumentBody.ChildElements)
        {
            if (child is Paragraph or Table)
            {
                if (i == block) return child;
                i++;
            }
        }
        throw new InvalidOperationException($"block {block} not found");
    }

    private static void CopyRunProperties(Run source, Run target)
    {
        if (source.RunProperties is { } rpr)
        {
            target.RunProperties = (RunProperties)rpr.CloneNode(true);
        }
    }

    private static Table BuildTable(int rows, int cols)
    {
        var table = new Table();
        var props = new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U },
                new LeftBorder { Val = BorderValues.Single, Size = 4U },
                new BottomBorder { Val = BorderValues.Single, Size = 4U },
                new RightBorder { Val = BorderValues.Single, Size = 4U },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U }));
        table.AppendChild(props);

        var grid = new TableGrid();
        for (int c = 0; c < cols; c++) grid.AppendChild(new GridColumn());
        table.AppendChild(grid);

        for (int r = 0; r < rows; r++) table.AppendChild(BuildRow(cols));
        return table;
    }

    private static TableRow BuildRow(int cols)
    {
        var row = new TableRow();
        for (int c = 0; c < cols; c++)
        {
            var cell = new TableCell(
                new TableCellProperties(new TableCellWidth { Width = "0", Type = TableWidthUnitValues.Auto }),
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }), new Run()));
            row.AppendChild(cell);
        }
        return row;
    }

    private static (byte[]? Data, string Mime) ResolveImage(Operation op)
    {
        if (!string.IsNullOrWhiteSpace(op.DataUri))
        {
            var mime = "image/png";
            var b64 = op.DataUri;
            var comma = b64.IndexOf(',');
            if (comma > 0)
            {
                var header = b64[..comma];
                var semi = header.IndexOf(';');
                if (semi > 0) mime = header[5..semi];
                b64 = b64[(comma + 1)..];
            }
            try { return (Convert.FromBase64String(b64), mime); }
            catch { return (null, mime); }
        }
        if (!string.IsNullOrWhiteSpace(op.Url))
        {
            // v1: URL images are not fetched server-side (SSRF surface). Clients resolve URLs to
            // data URIs before sending; this branch stays for symmetry and future opt-in.
            return (null, "image/png");
        }
        return (null, "image/png");
    }

    private static ImagePart CreateImagePart(MainDocumentPart main, byte[] data, string mime)
    {
        var contentType = mime.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/gif" => "image/gif",
            "image/bmp" => "image/bmp",
            "image/svg+xml" => "image/svg+xml",
            _ => "image/png",
        };
        var part = main.AddImagePart(contentType);
        using var ms = new MemoryStream(data);
        part.FeedData(ms);
        return part;
    }

    private static DW.Inline BuildInlineDrawing(string imageId, string relId, long widthEmu, long heightEmu, string alt)
    {
        var docPr = new DW.DocProperties { Id = HashId(imageId), Name = imageId, Description = alt };
        var extent = new DW.Extent { Cx = widthEmu, Cy = heightEmu };
        var inline = new DW.Inline { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };
        inline.AppendChild(extent);
        inline.AppendChild(docPr);

        var graphic = new A.Graphic();
        var graphicData = new A.GraphicData { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" };
        var picture = new PIC.Picture();
        var nvPicPr = new PIC.NonVisualPictureProperties(
            new PIC.NonVisualDrawingProperties { Id = HashId(imageId), Name = imageId },
            new PIC.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }));
        var blipFill = new PIC.BlipFill(
            new A.Blip { Embed = relId },
            new A.Stretch(new A.FillRectangle()));
        var shapeProps = new PIC.ShapeProperties(
            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = widthEmu, Cy = heightEmu }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
        picture.AppendChild(nvPicPr);
        picture.AppendChild(blipFill);
        picture.AppendChild(shapeProps);
        graphicData.AppendChild(picture);
        graphic.AppendChild(graphicData);
        inline.AppendChild(graphic);
        return inline;
    }

    /// <summary>Maps an arbitrary string id (op id / comment id / change id) to a stable uint for OOXML w:id attributes.</summary>
    public static uint HashId(string id)
    {
        uint hash = 2166136261;
        foreach (var c in id)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}

/// <summary>Result of applying a single operation.</summary>
public sealed record ApplyOutcome(
    bool Ok,
    bool IsNoOp,
    string? RejectReason,
    string? CapturedText = null,      // text actually removed by deleteText (for undo)
    string? CapturedStyle = null,     // style of a removed paragraph (for undo)
    string? CapturedImageId = null,   // image id used by insertImage (for undo)
    string? CapturedCommentId = null)
{
    public bool NoOp => IsNoOp;

    public static ApplyOutcome Success(string? capturedText = null, string? capturedStyle = null,
        string? capturedImageId = null, string? capturedCommentId = null) =>
        new(true, false, null, capturedText, capturedStyle, capturedImageId, capturedCommentId);

    public static ApplyOutcome None =>
        new(true, true, null, null, null, null, null);

    public static ApplyOutcome Reject(string reason) =>
        new(false, false, reason, null, null, null, null);
}
