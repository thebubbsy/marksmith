using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkSmith.Core.Services;
using MarkSmith.Models;

namespace MarkSmith.Services;

public sealed class InPlaceDocxPatcher : IInPlaceDocxPatcher
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .Build();

    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public PatchResult ApplyPatch(string docxPath, DocxPatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath))
        {
            return PatchResult.Fail($"Source DOCX file not found: '{docxPath}'");
        }

        string outputPath = !string.IsNullOrWhiteSpace(request.OutputPath)
            ? Path.GetFullPath(request.OutputPath)
            : Path.GetFullPath(docxPath);

        string outputDir = Path.GetDirectoryName(outputPath) ?? ".";
        if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

        string tempFile = Path.Combine(outputDir, $"{Path.GetFileName(outputPath)}.patch.{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(docxPath, tempFile, overwrite: true);

            var modifiedParts = new HashSet<string>();
            var operationDetails = new List<OperationDetail>();
            int modifiedBlocks = 0;
            var operations = request.GetNormalizedOperations();

            using (var doc = WordprocessingDocument.Open(tempFile, true))
            {
                var main = doc.MainDocumentPart;
                if (main == null || main.Document?.Body == null)
                {
                    return PatchResult.Fail("Target DOCX package has no MainDocumentPart or Body.");
                }

                modifiedParts.Add("word/document.xml");

                // Remove stale embedded source parts so subsequent reverse imports inspect the mutated DOM
                foreach (var part in main.CustomXmlParts.Where(p => MarksmithSourceStore.IsSourcePart(p)).ToList())
                {
                    main.DeletePart(part);
                }

                // Ensure all existing paragraphs have stable ParaIds matching DocxInspector
                int pIdx = 0;
                foreach (var p in main.Document.Body.Descendants<Paragraph>())
                {
                    var existingId = DocxInspector.GetOrGenerateParaId(p, pIdx++);
                    AssignParaId(p, existingId);
                }

                foreach (var op in operations)
                {
                    var detail = ExecuteOperation(doc, op, ref modifiedBlocks, modifiedParts);
                    operationDetails.Add(detail);
                }

                main.Document.Save();

                // Validate with OpenXmlValidator
                var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
                var validationErrors = validator.Validate(doc)
                    .Select(e => $"{e.Path?.XPath}: {e.Description}")
                    .ToList();

                var fatalErrors = validationErrors
                    .Where(e => !e.Contains("attribute is not declared") &&
                                !e.Contains("paraId") &&
                                !e.Contains("textId") &&
                                !e.Contains("w14:") &&
                                !e.Contains("w15:") &&
                                !e.Contains("settings.xml") &&
                                !e.Contains("comments.xml") &&
                                !e.Contains("ProofState") &&
                                !e.Contains("revision") &&
                                !e.Contains("trackRevisions") &&
                                !e.Contains("comment"))
                    .ToList();

                if (fatalErrors.Count > 0)
                {
                    return PatchResult.Fail("OpenXML schema validation failed after applying patches.", fatalErrors);
                }

                doc.Save();
            }

            int successfulOps = operationDetails.Count(d => d.Success);
            if (operations.Count > 0 && successfulOps == 0)
            {
                var firstError = operationDetails.FirstOrDefault(d => !d.Success)?.Message ?? "Target block not found.";
                return PatchResult.Fail(firstError);
            }
            if (operations.Count > 0 && successfulOps < operations.Count)
            {
                var firstError = operationDetails.FirstOrDefault(d => !d.Success)?.Message ?? "Some patch operations failed.";
                return PatchResult.Fail(firstError);
            }

            // Atomic file replacement
            if (File.Exists(outputPath))
            {
                try { File.Replace(tempFile, outputPath, null); }
                catch (PlatformNotSupportedException) { File.Delete(outputPath); File.Move(tempFile, outputPath); }
                catch (IOException) { File.Delete(outputPath); File.Move(tempFile, outputPath); }
            }
            else
            {
                File.Move(tempFile, outputPath);
            }

            return PatchResult.Ok(outputPath, modifiedBlocks, successfulOps, operationDetails, modifiedParts.ToList());
        }
        catch (Exception ex)
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            return PatchResult.Fail($"InPlaceDocxPatcher error: {ex.Message}");
        }
    }

    public PatchResult ApplyPatch(Stream docxStream, Stream outputStream, DocxPatchRequest request)
    {
        var mem = new MemoryStream();
        docxStream.CopyTo(mem);
        mem.Position = 0;

        var modifiedParts = new HashSet<string>();
        var operationDetails = new List<OperationDetail>();
        int modifiedBlocks = 0;
        var operations = request.GetNormalizedOperations();

        try
        {
            using (var doc = WordprocessingDocument.Open(mem, true))
            {
                var main = doc.MainDocumentPart;
                if (main == null || main.Document?.Body == null)
                {
                    return PatchResult.Fail("Target DOCX package has no MainDocumentPart or Body.");
                }

                modifiedParts.Add("word/document.xml");

                // Remove stale embedded source parts so subsequent reverse imports inspect the mutated DOM
                foreach (var part in main.CustomXmlParts.Where(p => MarksmithSourceStore.IsSourcePart(p)).ToList())
                {
                    main.DeletePart(part);
                }

                // Ensure all existing paragraphs have stable ParaIds matching DocxInspector
                int pIdx = 0;
                foreach (var p in main.Document.Body.Descendants<Paragraph>())
                {
                    var existingId = DocxInspector.GetOrGenerateParaId(p, pIdx++);
                    AssignParaId(p, existingId);
                }

                foreach (var op in operations)
                {
                    var detail = ExecuteOperation(doc, op, ref modifiedBlocks, modifiedParts);
                    operationDetails.Add(detail);
                }

                main.Document.Save();

                var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
                var validationErrors = validator.Validate(doc)
                    .Select(e => $"{e.Path?.XPath}: {e.Description}")
                    .ToList();

                var fatalErrors = validationErrors
                    .Where(e => !e.Contains("attribute is not declared") &&
                                !e.Contains("paraId") &&
                                !e.Contains("textId") &&
                                !e.Contains("w14:") &&
                                !e.Contains("w15:") &&
                                !e.Contains("settings.xml") &&
                                !e.Contains("comments.xml") &&
                                !e.Contains("ProofState") &&
                                !e.Contains("revision") &&
                                !e.Contains("trackRevisions") &&
                                !e.Contains("comment"))
                    .ToList();

                if (fatalErrors.Count > 0)
                {
                    return PatchResult.Fail("OpenXML schema validation failed.", fatalErrors);
                }

                doc.Save();
            }

            int successfulOps = operationDetails.Count(d => d.Success);
            if (operations.Count > 0 && successfulOps == 0)
            {
                var firstError = operationDetails.FirstOrDefault(d => !d.Success)?.Message ?? "Target block not found.";
                return PatchResult.Fail(firstError);
            }
            if (operations.Count > 0 && successfulOps < operations.Count)
            {
                var firstError = operationDetails.FirstOrDefault(d => !d.Success)?.Message ?? "Some patch operations failed.";
                return PatchResult.Fail(firstError);
            }

            mem.Position = 0;
            mem.CopyTo(outputStream);
            outputStream.Flush();

            return PatchResult.Ok(null, modifiedBlocks, successfulOps, operationDetails, modifiedParts.ToList());
        }
        catch (Exception ex)
        {
            return PatchResult.Fail($"InPlaceDocxPatcher error: {ex.Message}");
        }
    }

    private OperationDetail ExecuteOperation(WordprocessingDocument doc, DocxPatchOperationItem op, ref int modifiedBlocks, HashSet<string> modifiedParts)
    {
        var main = doc.MainDocumentPart!;
        var body = main.Document.Body!;

        // Handle whole-document revision operations if no target is specified
        if (op.Op == PatchOperation.AcceptRevision && IsEmptySelector(op.Target))
        {
            int count = AcceptAllRevisions(body);
            modifiedBlocks += count;
            return new OperationDetail { Op = op.Op, Success = true, Message = $"Accepted {count} revisions across document." };
        }
        if (op.Op == PatchOperation.RejectRevision && IsEmptySelector(op.Target))
        {
            int count = RejectAllRevisions(body);
            modifiedBlocks += count;
            return new OperationDetail { Op = op.Op, Success = true, Message = $"Rejected {count} revisions across document." };
        }

        // Resolve target element
        var targetElement = ResolveTarget(body, op.Target);
        if (targetElement == null && op.Op != PatchOperation.Append && op.Op != PatchOperation.Prepend)
        {
            return new OperationDetail
            {
                Op = op.Op,
                Success = false,
                Message = $"Target block not found for selector: {DescribeSelector(op.Target)}"
            };
        }

        string? targetParaId = targetElement is Paragraph targetP ? DocxInspector.GetOrGenerateParaId(targetP) : null;

        switch (op.Op)
        {
            case PatchOperation.Replace:
                {
                    var newElements = TranspileMarkdownToElements(op.Content ?? "", main);
                    if (targetElement is Paragraph p && op.PreserveFormatting && newElements.Count > 0 && newElements[0] is Paragraph newP)
                    {
                        if (op.TrackChanges)
                        {
                            ApplyTrackChangesReplacement(p, newP, op.Author);
                            EnsureTrackRevisions(doc, modifiedParts);
                        }
                        else
                        {
                            // Preserve paragraph properties and replace child runs
                            var pPr = p.ParagraphProperties?.CloneNode(true) as ParagraphProperties;
                            var existingComments = p.ChildElements.Where(c => c is CommentRangeStart or CommentRangeEnd || (c is Run cr && cr.FirstChild is CommentReference)).ToList();
                            p.RemoveAllChildren();
                            if (pPr != null) p.AppendChild(pPr);
                            foreach (var crs in existingComments.Where(c => c is CommentRangeStart)) p.AppendChild(crs);
                            foreach (var child in newP.ChildElements.Where(c => c is not ParagraphProperties).ToList())
                            {
                                p.AppendChild(child.CloneNode(true));
                            }
                            foreach (var cre in existingComments.Where(c => c is not CommentRangeStart)) p.AppendChild(cre);
                        }

                        // Insert remaining elements if any after p
                        var current = (OpenXmlElement)p;
                        for (int i = 1; i < newElements.Count; i++)
                        {
                            current = p.Parent!.InsertAfter(newElements[i], current);
                        }
                    }
                    else
                    {
                        var parent = targetElement!.Parent!;
                        var current = targetElement;
                        foreach (var elem in newElements)
                        {
                            current = parent.InsertAfter(elem, current);
                        }
                        targetElement.Remove();
                    }
                    modifiedBlocks++;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = "Block replaced successfully." };
                }

            case PatchOperation.InsertBefore:
                {
                    var newElements = TranspileMarkdownToElements(op.Content ?? "", main);
                    var parent = targetElement!.Parent!;
                    foreach (var elem in newElements)
                    {
                        parent.InsertBefore(elem, targetElement);
                    }
                    modifiedBlocks += newElements.Count;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = $"Inserted {newElements.Count} blocks before target." };
                }

            case PatchOperation.InsertAfter:
                {
                    var newElements = TranspileMarkdownToElements(op.Content ?? "", main);
                    var parent = targetElement!.Parent!;
                    var current = targetElement;
                    foreach (var elem in newElements)
                    {
                        current = parent.InsertAfter(elem, current);
                    }
                    modifiedBlocks += newElements.Count;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = $"Inserted {newElements.Count} blocks after target." };
                }

            case PatchOperation.Delete:
                {
                    if (op.TrackChanges && targetElement is Paragraph p)
                    {
                        ApplyTrackChangesDeletion(p, op.Author);
                        EnsureTrackRevisions(doc, modifiedParts);
                    }
                    else
                    {
                        targetElement!.Remove();
                    }
                    modifiedBlocks++;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = "Block deleted successfully." };
                }

            case PatchOperation.Append:
                {
                    var newElements = TranspileMarkdownToElements(op.Content ?? "", main);
                    var sectPr = body.Elements<SectionProperties>().LastOrDefault();
                    foreach (var elem in newElements)
                    {
                        if (sectPr != null) body.InsertBefore(elem, sectPr);
                        else body.AppendChild(elem);
                    }
                    modifiedBlocks += newElements.Count;
                    return new OperationDetail { Op = op.Op, Success = true, Message = $"Appended {newElements.Count} blocks to document." };
                }

            case PatchOperation.Prepend:
                {
                    var newElements = TranspileMarkdownToElements(op.Content ?? "", main);
                    var firstChild = body.FirstChild;
                    foreach (var elem in newElements)
                    {
                        if (firstChild != null) body.InsertBefore(elem, firstChild);
                        else body.AppendChild(elem);
                    }
                    modifiedBlocks += newElements.Count;
                    return new OperationDetail { Op = op.Op, Success = true, Message = $"Prepended {newElements.Count} blocks to document." };
                }

            case PatchOperation.AddComment:
                {
                    if (targetElement is not Paragraph p)
                    {
                        p = targetElement?.Descendants<Paragraph>().FirstOrDefault() ?? body.Elements<Paragraph>().FirstOrDefault()!;
                    }
                    if (p == null)
                    {
                        return new OperationDetail { Op = op.Op, Success = false, Message = "No paragraph found to attach comment." };
                    }

                    string commentText = op.Comment ?? op.CommentPayload?.Text ?? "";
                    string author = op.CommentPayload?.Author ?? op.Author;
                    string initials = op.CommentPayload?.Initials ?? (author.Length > 0 ? author.Substring(0, 1) : "M");
                    DateTime date = op.CommentPayload?.Date ?? DateTime.UtcNow;

                    var commentsPart = main.WordprocessingCommentsPart ?? main.AddNewPart<WordprocessingCommentsPart>();
                    if (commentsPart.Comments == null)
                    {
                        commentsPart.Comments = new Comments();
                    }

                    modifiedParts.Add("word/comments.xml");

                    int maxId = 0;
                    foreach (var c in commentsPart.Comments.Elements<Comment>())
                    {
                        if (int.TryParse(c.Id?.Value, out int idVal) && idVal > maxId)
                            maxId = idVal;
                    }
                    string newId = (maxId + 1).ToString();

                    var newComment = new Comment
                    {
                        Id = newId,
                        Author = author,
                        Initials = initials,
                        Date = date
                    };
                    var commentPara = new Paragraph(new Run(new Text(commentText) { Space = SpaceProcessingModeValues.Preserve }));
                    AssignParaId(commentPara);
                    newComment.AppendChild(commentPara);
                    commentsPart.Comments.AppendChild(newComment);
                    commentsPart.Comments.Save();

                    // Anchor comment around runs in target paragraph
                    var pPr = p.ParagraphProperties;
                    var crStart = new CommentRangeStart { Id = newId };
                    var crEnd = new CommentRangeEnd { Id = newId };
                    var crRef = new Run(new CommentReference { Id = newId });

                    if (pPr != null) p.InsertAfter(crStart, pPr);
                    else p.InsertAt(crStart, 0);

                    p.AppendChild(crEnd);
                    p.AppendChild(crRef);

                    modifiedBlocks++;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = $"Added comment ID {newId}." };
                }

            case PatchOperation.AcceptRevision:
                {
                    int count = AcceptRevisionsInElement(targetElement!);
                    modifiedBlocks += count;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = $"Accepted {count} revisions in target block." };
                }

            case PatchOperation.RejectRevision:
                {
                    int count = RejectRevisionsInElement(targetElement!);
                    modifiedBlocks += count;
                    return new OperationDetail { Op = op.Op, Success = true, TargetParaId = targetParaId, Message = $"Rejected {count} revisions in target block." };
                }

            default:
                return new OperationDetail { Op = op.Op, Success = false, Message = $"Unsupported operation {op.Op}" };
        }
    }

    private static OpenXmlElement? ResolveTarget(Body body, BlockSelector sel)
    {
        if (sel == null) return null;

        // 1. Target by ParaId
        if (!string.IsNullOrWhiteSpace(sel.ParaId))
        {
            int pIdx = 0;
            foreach (var p in body.Descendants<Paragraph>())
            {
                var pid = DocxInspector.GetOrGenerateParaId(p, pIdx++);
                if (string.Equals(pid, sel.ParaId, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
        }

        // 2. Target by BodyIndex
        if (sel.BodyIndex.HasValue)
        {
            var topElements = body.ChildElements.Where(e => e is Paragraph or Table or SdtBlock).ToList();
            if (sel.BodyIndex.Value >= 0 && sel.BodyIndex.Value < topElements.Count)
            {
                return topElements[sel.BodyIndex.Value];
            }
        }

        // 3. Target by HeadingPath
        if (!string.IsNullOrWhiteSpace(sel.HeadingPath))
        {
            var targetHeading = FindParagraphByHeadingPath(body, sel.HeadingPath);
            if (targetHeading != null) return targetHeading;
        }

        // 4. Target by BookmarkName
        if (!string.IsNullOrWhiteSpace(sel.BookmarkName))
        {
            var bm = body.Descendants<BookmarkStart>().FirstOrDefault(b =>
                string.Equals(b.Name?.Value, sel.BookmarkName, StringComparison.OrdinalIgnoreCase));
            if (bm != null)
            {
                var p = bm.Ancestors<Paragraph>().FirstOrDefault();
                if (p != null) return p;
                var tbl = bm.Ancestors<Table>().FirstOrDefault();
                if (tbl != null) return tbl;
                return bm;
            }
        }

        // 5. Target by TableCell
        if (sel.TableCell != null)
        {
            var tables = body.Descendants<Table>().ToList();
            Table? targetTbl = null;
            if (sel.TableCell.TableIndex.HasValue && sel.TableCell.TableIndex.Value >= 0 && sel.TableCell.TableIndex.Value < tables.Count)
            {
                targetTbl = tables[sel.TableCell.TableIndex.Value];
            }
            else if (!string.IsNullOrWhiteSpace(sel.TableCell.TableParaId))
            {
                targetTbl = tables.FirstOrDefault(t => t.Descendants<Paragraph>().Any(p => string.Equals(DocxInspector.GetOrGenerateParaId(p), sel.TableCell.TableParaId, StringComparison.OrdinalIgnoreCase)));
            }
            else if (tables.Count > 0)
            {
                targetTbl = tables[0];
            }

            if (targetTbl != null)
            {
                var rows = targetTbl.Elements<TableRow>().ToList();
                if (sel.TableCell.Row >= 0 && sel.TableCell.Row < rows.Count)
                {
                    var cells = rows[sel.TableCell.Row].Elements<TableCell>().ToList();
                    if (sel.TableCell.Col >= 0 && sel.TableCell.Col < cells.Count)
                    {
                        var cell = cells[sel.TableCell.Col];
                        var p = cell.Elements<Paragraph>().FirstOrDefault();
                        return p ?? (OpenXmlElement)cell;
                    }
                }
            }
        }

        // 6. Target by CommentId
        if (!string.IsNullOrWhiteSpace(sel.CommentId))
        {
            var crs = body.Descendants<CommentRangeStart>().FirstOrDefault(c =>
                string.Equals(c.Id?.Value, sel.CommentId, StringComparison.OrdinalIgnoreCase));
            if (crs != null)
            {
                var p = crs.Ancestors<Paragraph>().FirstOrDefault();
                if (p != null) return p;
            }
        }

        return null;
    }

    private static Paragraph? FindParagraphByHeadingPath(Body body, string headingPath)
    {
        var parts = headingPath.Split(new[] { '/', '>', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToList();

        if (parts.Count == 0) return null;

        var allHeadings = body.Descendants<Paragraph>()
            .Where(p => {
                var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                return style != null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (parts.Count == 1)
        {
            var single = parts[0];
            return allHeadings.FirstOrDefault(h => string.Equals(h.InnerText.Trim(), single, StringComparison.OrdinalIgnoreCase))
                ?? body.Descendants<Paragraph>().FirstOrDefault(p => string.Equals(p.InnerText.Trim(), single, StringComparison.OrdinalIgnoreCase));
        }

        // Multiple parts: match heading sequence in document order
        int matchIdx = 0;
        Paragraph? matchedHeading = null;
        foreach (var h in allHeadings)
        {
            var text = h.InnerText.Trim();
            if (string.Equals(text, parts[matchIdx], StringComparison.OrdinalIgnoreCase))
            {
                matchIdx++;
                matchedHeading = h;
                if (matchIdx == parts.Count)
                {
                    return matchedHeading;
                }
            }
        }

        return null;
    }

    private static void ApplyTrackChangesReplacement(Paragraph p, Paragraph newP, string author)
    {
        string delId = GenerateRevisionId();
        string insId = GenerateRevisionId();

        string oldText = p.InnerText;

        // Preserve comment anchors and references
        var crsList = p.ChildElements.OfType<CommentRangeStart>().ToList();
        var creList = p.ChildElements.OfType<CommentRangeEnd>().ToList();
        var crfList = p.ChildElements.Where(c => c is Run r && r.Descendants<CommentReference>().Any()).ToList();

        var pPr = p.ParagraphProperties?.CloneNode(true) as ParagraphProperties;
        p.RemoveAllChildren();

        if (pPr != null)
        {
            p.AppendChild(pPr);
        }

        foreach (var crs in crsList)
        {
            p.AppendChild(crs);
        }

        if (!string.IsNullOrEmpty(oldText))
        {
            var delRun = new DeletedRun { Id = delId, Author = author, Date = DateTime.UtcNow };
            delRun.AppendChild(new Run(new DeletedText(oldText) { Space = SpaceProcessingModeValues.Preserve }));
            p.AppendChild(delRun);
        }

        var insRun = new InsertedRun { Id = insId, Author = author, Date = DateTime.UtcNow };
        foreach (var child in newP.ChildElements.Where(c => c is not ParagraphProperties).ToList())
        {
            if (child is Run r)
            {
                insRun.AppendChild(r.CloneNode(true));
            }
            else
            {
                insRun.AppendChild(child.CloneNode(true));
            }
        }
        if (!insRun.ChildElements.Any())
        {
            insRun.AppendChild(new Run(new Text(newP.InnerText) { Space = SpaceProcessingModeValues.Preserve }));
        }
        p.AppendChild(insRun);

        foreach (var cre in creList)
        {
            p.AppendChild(cre);
        }

        foreach (var crf in crfList)
        {
            p.AppendChild(crf);
        }
    }

    private static void ApplyTrackChangesDeletion(Paragraph p, string author)
    {
        string delId = GenerateRevisionId();
        string oldText = p.InnerText;

        var crsList = p.ChildElements.OfType<CommentRangeStart>().ToList();
        var creList = p.ChildElements.OfType<CommentRangeEnd>().ToList();
        var crfList = p.ChildElements.Where(c => c is Run r && r.Descendants<CommentReference>().Any()).ToList();

        var pPr = p.ParagraphProperties?.CloneNode(true) as ParagraphProperties;
        p.RemoveAllChildren();

        if (pPr != null) p.AppendChild(pPr);
        foreach (var crs in crsList) p.AppendChild(crs);

        if (!string.IsNullOrEmpty(oldText))
        {
            var delRun = new DeletedRun { Id = delId, Author = author, Date = DateTime.UtcNow };
            delRun.AppendChild(new Run(new DeletedText(oldText) { Space = SpaceProcessingModeValues.Preserve }));
            p.AppendChild(delRun);
        }

        foreach (var cre in creList) p.AppendChild(cre);
        foreach (var crf in crfList) p.AppendChild(crf);
    }

    private static void EnsureTrackRevisions(WordprocessingDocument doc, HashSet<string> modifiedParts)
    {
        var settingsPart = doc.MainDocumentPart!.DocumentSettingsPart ?? doc.MainDocumentPart.AddNewPart<DocumentSettingsPart>();
        if (settingsPart.Settings == null) settingsPart.Settings = new Settings();

        if (!settingsPart.Settings.Elements<TrackRevisions>().Any())
        {
            var insertBeforeTarget = settingsPart.Settings.ChildElements.FirstOrDefault(c =>
                c is AutoHyphenation or UpdateFieldsOnOpen or DocumentVariables or
                     DoNotTrackMoves or DoNotTrackFormatting or DocumentProtection or AutoFormatOverride or
                     DefaultTabStop or CharacterSpacingControl or ThemeFontLanguages or ColorSchemeMapping or
                     Compatibility or Rsids);
            if (insertBeforeTarget != null)
            {
                settingsPart.Settings.InsertBefore(new TrackRevisions(), insertBeforeTarget);
            }
            else
            {
                settingsPart.Settings.AppendChild(new TrackRevisions());
            }
            settingsPart.Settings.Save();
            modifiedParts.Add("word/settings.xml");
        }
    }

    private static int AcceptAllRevisions(OpenXmlElement root)
    {
        int count = 0;
        foreach (var ins in root.Descendants<InsertedRun>().ToList())
        {
            var parent = ins.Parent;
            if (parent != null)
            {
                foreach (var child in ins.ChildElements.ToList())
                {
                    parent.InsertBefore(child.CloneNode(true), ins);
                }
                ins.Remove();
                count++;
            }
        }

        foreach (var del in root.Descendants<DeletedRun>().ToList())
        {
            del.Remove();
            count++;
        }

        return count;
    }

    private static int RejectAllRevisions(OpenXmlElement root)
    {
        int count = 0;
        foreach (var ins in root.Descendants<InsertedRun>().ToList())
        {
            ins.Remove();
            count++;
        }

        foreach (var del in root.Descendants<DeletedRun>().ToList())
        {
            var parent = del.Parent;
            if (parent != null)
            {
                foreach (var r in del.Descendants<Run>().ToList())
                {
                    var normalRun = new Run();
                    if (r.RunProperties != null)
                        normalRun.AppendChild(r.RunProperties.CloneNode(true));
                    foreach (var dt in r.Descendants<DeletedText>())
                    {
                        normalRun.AppendChild(new Text(dt.Text) { Space = SpaceProcessingModeValues.Preserve });
                    }
                    parent.InsertBefore(normalRun, del);
                }
                del.Remove();
                count++;
            }
        }

        return count;
    }

    private static int AcceptRevisionsInElement(OpenXmlElement elem) => AcceptAllRevisions(elem);
    private static int RejectRevisionsInElement(OpenXmlElement elem) => RejectAllRevisions(elem);

    public static List<OpenXmlElement> TranspileMarkdownToElements(string markdown, MainDocumentPart main)
    {
        var elements = new List<OpenXmlElement>();
        if (string.IsNullOrWhiteSpace(markdown)) return elements;

        // 1. Normalize admonitions (e.g. :::tip, :::warning, :::note -> > [!TIP])
        string normalized = AdmonitionNormalizer.Apply(markdown);

        // 2. Check for container blocks (:::smartart, :::chart, :::tabs, :::columns, :::timeline, :::shapes, :::workflow)
        if (normalized.Contains(":::", StringComparison.Ordinal))
        {
            var containerElements = TryTranspileContainers(normalized, main);
            if (containerElements.Count > 0)
            {
                return containerElements;
            }
        }

        var doc = Markdown.Parse(normalized, Pipeline);
        foreach (var block in doc)
        {
            if (block is HeadingBlock hb)
            {
                var p = new Paragraph();
                AssignParaId(p);
                var pPr = new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{hb.Level}" });
                p.AppendChild(pPr);
                AppendInlines(p, hb.Inline);
                elements.Add(p);
            }
            else if (block is ParagraphBlock pb)
            {
                var p = new Paragraph();
                AssignParaId(p);
                AppendInlines(p, pb.Inline);
                elements.Add(p);
            }
            else if (block is Markdig.Extensions.Mathematics.MathBlock mb)
            {
                var p = new Paragraph();
                AssignParaId(p);
                try
                {
                    p.AppendChild(LatexToOmml.Build(mb.Lines.ToString()));
                }
                catch
                {
                    p.AppendChild(new Run(new Text(mb.Lines.ToString()) { Space = SpaceProcessingModeValues.Preserve }));
                }
                elements.Add(p);
            }
            else if (block is ListBlock lb)
            {
                foreach (var item in lb)
                {
                    if (item is ListItemBlock lib)
                    {
                        foreach (var sub in lib)
                        {
                            if (sub is ParagraphBlock subPb)
                            {
                                var p = new Paragraph();
                                AssignParaId(p);
                                var pPr = new ParagraphProperties(new ParagraphStyleId { Val = "ListParagraph" });
                                p.AppendChild(pPr);
                                AppendInlines(p, subPb.Inline);
                                elements.Add(p);
                            }
                        }
                    }
                }
            }
            else if (block is QuoteBlock qb)
            {
                // Detect Callout / Alert blockquote
                int qStart = Math.Clamp(qb.Span.Start, 0, normalized.Length);
                int qLen = Math.Min(qb.Span.Length, normalized.Length - qStart);
                string quoteText = qLen > 0 ? normalized.Substring(qStart, qLen) : "";
                bool isAlert = quoteText.Contains("[!NOTE]") || quoteText.Contains("[!TIP]") || quoteText.Contains("[!WARNING]") || quoteText.Contains("[!IMPORTANT]") || quoteText.Contains("[!CAUTION]");

                string alertColor = "0969DA"; // Note default
                if (quoteText.Contains("[!TIP]")) alertColor = "1A7F37";
                else if (quoteText.Contains("[!WARNING]")) alertColor = "9A6700";
                else if (quoteText.Contains("[!CAUTION]")) alertColor = "D1242F";
                else if (quoteText.Contains("[!IMPORTANT]")) alertColor = "8250DF";

                foreach (var sub in qb)
                {
                    if (sub is ParagraphBlock subPb)
                    {
                        var p = new Paragraph();
                        AssignParaId(p);
                        var pPr = new ParagraphProperties();
                        if (isAlert)
                        {
                            pPr.ParagraphBorders = new ParagraphBorders(
                                new LeftBorder { Val = BorderValues.Single, Size = 24, Space = 4, Color = alertColor },
                                new TopBorder { Val = BorderValues.None },
                                new RightBorder { Val = BorderValues.None },
                                new BottomBorder { Val = BorderValues.None }
                            );
                            pPr.Shading = new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F6F8FA" };
                        }
                        else
                        {
                            pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Quote" };
                        }
                        p.AppendChild(pPr);
                        AppendInlines(p, subPb.Inline);
                        elements.Add(p);
                    }
                }
            }
            else if (block is FencedCodeBlock fcb)
            {
                var p = new Paragraph();
                AssignParaId(p);
                var pPr = new ParagraphProperties(
                    new ParagraphStyleId { Val = "SourceCode" },
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F4F4F4" }
                );
                p.AppendChild(pPr);
                var codeText = string.Join("\n", fcb.Lines.Lines.Select(l => l.Slice.ToString())).TrimEnd();
                p.AppendChild(new Run(
                    new RunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }),
                    new Text(codeText) { Space = SpaceProcessingModeValues.Preserve }
                ));
                elements.Add(p);
            }
            else if (block is Markdig.Extensions.Tables.Table tbl)
            {
                var wordTbl = new Table();
                var tblPr = new TableProperties(
                    new TableBorders(
                        new TopBorder { Val = BorderValues.Single, Size = 4 },
                        new LeftBorder { Val = BorderValues.None },
                        new BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new RightBorder { Val = BorderValues.None },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideVerticalBorder { Val = BorderValues.None }
                    )
                );
                wordTbl.AppendChild(tblPr);

                var tblGrid = new TableGrid();
                int maxCols = tbl.OfType<Markdig.Extensions.Tables.TableRow>().Select(r => r.OfType<Markdig.Extensions.Tables.TableCell>().Count()).DefaultIfEmpty(1).Max();
                for (int c = 0; c < maxCols; c++)
                {
                    tblGrid.AppendChild(new GridColumn());
                }
                wordTbl.AppendChild(tblGrid);

                foreach (var rowObj in tbl)
                {
                    if (rowObj is IEnumerable<Markdig.Syntax.Block> cellsBlock)
                    {
                        var wordRow = new TableRow();
                        foreach (var cellObj in cellsBlock)
                        {
                            if (cellObj is Markdig.Extensions.Tables.TableCell tc)
                            {
                                var wordCell = new TableCell();
                                foreach (var sub in tc)
                                {
                                    if (sub is ParagraphBlock subPb)
                                    {
                                        var p = new Paragraph();
                                        AssignParaId(p);
                                        AppendInlines(p, subPb.Inline);
                                        wordCell.AppendChild(p);
                                    }
                                }
                                if (!wordCell.Elements<Paragraph>().Any())
                                {
                                    var emptyP = new Paragraph();
                                    AssignParaId(emptyP);
                                    wordCell.AppendChild(emptyP);
                                }
                                wordRow.AppendChild(wordCell);
                            }
                        }
                        if (wordRow.Elements<TableCell>().Any())
                        {
                            wordTbl.AppendChild(wordRow);
                        }
                    }
                }
                elements.Add(wordTbl);
            }
            else if (block is ThematicBreakBlock)
            {
                var p = new Paragraph();
                AssignParaId(p);
                var pPr = new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "D0D0D0" })
                );
                p.AppendChild(pPr);
                elements.Add(p);
            }
        }

        if (elements.Count == 0)
        {
            var p = new Paragraph();
            AssignParaId(p);
            p.AppendChild(new Run(new Text(markdown) { Space = SpaceProcessingModeValues.Preserve }));
            elements.Add(p);
        }

        return elements;
    }

    private static List<OpenXmlElement> TryTranspileContainers(string markdown, MainDocumentPart main)
    {
        var elements = new List<OpenXmlElement>();
        var lines = markdown.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith(":::", StringComparison.Ordinal))
            {
                var kindMatch = System.Text.RegularExpressions.Regex.Match(line, @"^:::+\s*([A-Za-z0-9_-]+)");
                if (kindMatch.Success)
                {
                    string kind = kindMatch.Groups[1].Value.ToLowerInvariant();
                    var bodyLines = new List<string>();
                    int j = i + 1;
                    while (j < lines.Length && !System.Text.RegularExpressions.Regex.IsMatch(lines[j].TrimStart(), @"^:::+\s*$"))
                    {
                        bodyLines.Add(lines[j]);
                        j++;
                    }
                    i = j;

                    var containerTbl = new Table();
                    var tblPr = new TableProperties(
                        new TableBorders(
                            new TopBorder { Val = BorderValues.Single, Size = 12, Color = "4F81BD" },
                            new BottomBorder { Val = BorderValues.Single, Size = 12, Color = "4F81BD" },
                            new LeftBorder { Val = BorderValues.Single, Size = 12, Color = "4F81BD" },
                            new RightBorder { Val = BorderValues.Single, Size = 12, Color = "4F81BD" },
                            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D0D0D0" },
                            new InsideVerticalBorder { Val = BorderValues.None }
                        ),
                        new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }
                    );
                    containerTbl.AppendChild(tblPr);

                    var headerRow = new TableRow();
                    var headerCell = new TableCell();
                    var headerP = new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Heading3" }),
                        new Run(new RunProperties(new Bold(), new Color { Val = "4F81BD" }), new Text($"[{kind.ToUpperInvariant()}] {line.TrimStart(':', ' ')}") { Space = SpaceProcessingModeValues.Preserve })
                    );
                    AssignParaId(headerP);
                    headerCell.AppendChild(headerP);
                    headerRow.AppendChild(headerCell);
                    containerTbl.AppendChild(headerRow);

                    foreach (var bl in bodyLines)
                    {
                        if (string.IsNullOrWhiteSpace(bl)) continue;
                        var row = new TableRow();
                        var cell = new TableCell();
                        var p = new Paragraph(new Run(new Text(bl) { Space = SpaceProcessingModeValues.Preserve }));
                        AssignParaId(p);
                        cell.AppendChild(p);
                        row.AppendChild(cell);
                        containerTbl.AppendChild(row);
                    }

                    elements.Add(containerTbl);
                }
            }
        }
        return elements;
    }

    private static void AppendInlines(Paragraph p, ContainerInline? inlines)
    {
        if (inlines == null) return;

        foreach (var inline in inlines)
        {
            if (inline is LiteralInline lit)
            {
                p.AppendChild(new Run(new Text(lit.Content.ToString()) { Space = SpaceProcessingModeValues.Preserve }));
            }
            else if (inline is Markdig.Extensions.Mathematics.MathInline math)
            {
                try
                {
                    p.AppendChild(LatexToOmml.Build(math.Content.ToString()));
                }
                catch
                {
                    p.AppendChild(new Run(new Text($"${math.Content}$") { Space = SpaceProcessingModeValues.Preserve }));
                }
            }
            else if (inline is EmphasisInline emp)
            {
                var rPr = new RunProperties();
                if (emp.DelimiterCount == 2) rPr.Bold = new Bold();
                else rPr.Italic = new Italic();

                var r = new Run(rPr);
                foreach (var sub in emp)
                {
                    if (sub is LiteralInline subLit)
                        r.AppendChild(new Text(subLit.Content.ToString()) { Space = SpaceProcessingModeValues.Preserve });
                }
                p.AppendChild(r);
            }
            else if (inline is CodeInline code)
            {
                var rPr = new RunProperties(
                    new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "EEEEEE" }
                );
                p.AppendChild(new Run(rPr, new Text(code.Content) { Space = SpaceProcessingModeValues.Preserve }));
            }
            else if (inline is LinkInline link)
            {
                var linkText = string.Join("", link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                if (string.IsNullOrEmpty(linkText)) linkText = link.Url ?? "";

                var rPr = new RunProperties(
                    new Color { Val = "0563C1" },
                    new Underline { Val = UnderlineValues.Single }
                );
                p.AppendChild(new Run(rPr, new Text(linkText) { Space = SpaceProcessingModeValues.Preserve }));
            }
            else if (inline is LineBreakInline)
            {
                p.AppendChild(new Run(new Break()));
            }
            else if (inline is ContainerInline container)
            {
                AppendInlines(p, container);
            }
        }
    }

    public static void AssignParaId(Paragraph p, string? forcedId = null)
    {
        string hex = forcedId ?? "";
        if (string.IsNullOrEmpty(hex))
        {
            byte[] bytes = new byte[4];
            Rng.GetBytes(bytes);
            uint val = BitConverter.ToUInt32(bytes, 0) & 0x7FFFFFFF;
            if (val == 0) val = 1;
            hex = val.ToString("X8");
        }
        try
        {
            p.ParagraphId = hex;
        }
        catch
        {
            p.SetAttribute(new OpenXmlAttribute("w14", "paraId", "http://schemas.microsoft.com/office/word/2010/wordml", hex));
        }
    }

    private static string GenerateRevisionId()
    {
        byte[] bytes = new byte[4];
        Rng.GetBytes(bytes);
        uint val = BitConverter.ToUInt32(bytes, 0) & 0x7FFFFFFF;
        if (val == 0) val = 1;
        return val.ToString();
    }

    private static bool IsEmptySelector(BlockSelector sel) =>
        sel == null ||
        (string.IsNullOrEmpty(sel.ParaId) &&
         !sel.BodyIndex.HasValue &&
         string.IsNullOrEmpty(sel.HeadingPath) &&
         string.IsNullOrEmpty(sel.BookmarkName) &&
         string.IsNullOrEmpty(sel.CommentId) &&
         sel.TableCell == null);

    private static string DescribeSelector(BlockSelector sel)
    {
        if (sel == null) return "(null)";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(sel.ParaId)) parts.Add($"paraId='{sel.ParaId}'");
        if (sel.BodyIndex.HasValue) parts.Add($"bodyIndex={sel.BodyIndex}");
        if (!string.IsNullOrEmpty(sel.HeadingPath)) parts.Add($"headingPath='{sel.HeadingPath}'");
        if (!string.IsNullOrEmpty(sel.BookmarkName)) parts.Add($"bookmark='{sel.BookmarkName}'");
        if (!string.IsNullOrEmpty(sel.CommentId)) parts.Add($"commentId='{sel.CommentId}'");
        if (sel.TableCell != null) parts.Add($"tableCell=({sel.TableCell.Row},{sel.TableCell.Col})");
        return string.Join(", ", parts);
    }
}
