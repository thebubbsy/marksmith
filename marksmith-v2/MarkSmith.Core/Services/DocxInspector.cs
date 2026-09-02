using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.Core.Services;
using MarkSmith.Models;

namespace MarkSmith.Services;

public sealed class DocxInspector : IDocxInspector
{
    public DocxStructureReport Inspect(string docxPath, DocxInspectionOptions? options = null)
    {
        using var stream = File.OpenRead(docxPath);
        return Inspect(stream, options);
    }

    public DocxStructureReport Inspect(Stream docxStream, DocxInspectionOptions? options = null)
    {
        options ??= new DocxInspectionOptions();
        using var doc = WordprocessingDocument.Open(docxStream, false);
        var main = doc.MainDocumentPart;
        if (main == null || main.Document == null || main.Document.Body == null)
        {
            return new DocxStructureReport();
        }

        // 1. Metadata
        var props = doc.PackageProperties;
        var title = props.Title ?? "";
        var creator = props.Creator ?? "";
        var lastModifiedBy = props.LastModifiedBy ?? "";
        var revision = props.Revision ?? "";
        var createdDate = props.Created;
        var modifiedDate = props.Modified;

        // Check embedded source presence
        bool hasEmbedded = main.CustomXmlParts.Any();

        // 2. Comments Part
        var commentsList = new List<CommentSummary>();
        var commentTextMap = new Dictionary<string, (string Author, string? Initials, DateTime? Date, string Text)>();
        if (main.WordprocessingCommentsPart != null && main.WordprocessingCommentsPart.Comments != null)
        {
            foreach (var c in main.WordprocessingCommentsPart.Comments.Elements<Comment>())
            {
                var id = c.Id?.Value ?? "";
                var author = c.Author?.Value ?? "";
                var initials = c.Initials?.Value;
                var date = c.Date?.Value;
                var text = string.Join(" ", c.Descendants<Text>().Select(t => t.Text)).Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    commentTextMap[id] = (author, initials, date, text);
                }
            }
        }

        // Map comment anchors
        var commentAnchorMap = new Dictionary<string, string>();
        foreach (var p in main.Document.Body.Descendants<Paragraph>())
        {
            var rangeStarts = p.Descendants<CommentRangeStart>().ToList();
            foreach (var crs in rangeStarts)
            {
                var cid = crs.Id?.Value ?? "";
                if (!string.IsNullOrEmpty(cid) && !commentAnchorMap.ContainsKey(cid))
                {
                    commentAnchorMap[cid] = p.InnerText.Trim();
                }
            }
        }

        foreach (var kvp in commentTextMap)
        {
            commentAnchorMap.TryGetValue(kvp.Key, out var anchor);
            commentsList.Add(new CommentSummary
            {
                Id = kvp.Key,
                Author = kvp.Value.Author,
                Initials = kvp.Value.Initials,
                Date = kvp.Value.Date,
                AnchorText = anchor,
                CommentText = kvp.Value.Text
            });
        }

        // 3. Sections
        var sectionList = new List<SectionSummary>();
        int secIdx = 0;
        var sectPrList = main.Document.Body.Descendants<SectionProperties>().ToList();
        if (sectPrList.Count == 0 && main.Document.Body.Elements<SectionProperties>().Any())
        {
            sectPrList = main.Document.Body.Elements<SectionProperties>().ToList();
        }

        foreach (var sp in sectPrList)
        {
            var pgSz = sp.GetFirstChild<PageSize>();
            var pgMar = sp.GetFirstChild<PageMargin>();

            double w = pgSz?.Width != null ? pgSz.Width.Value / 20.0 : 612.0; // twips to pt
            double h = pgSz?.Height != null ? pgSz.Height.Value / 20.0 : 792.0;
            string orient = (pgSz?.Orient != null && pgSz.Orient.Value == PageOrientationValues.Landscape) ? "Landscape" : "Portrait";

            double top = pgMar?.Top != null ? pgMar.Top.Value / 20.0 : 72.0;
            double bottom = pgMar?.Bottom != null ? pgMar.Bottom.Value / 20.0 : 72.0;
            double left = pgMar?.Left != null ? pgMar.Left.Value / 20.0 : 72.0;
            double right = pgMar?.Right != null ? pgMar.Right.Value / 20.0 : 72.0;

            sectionList.Add(new SectionSummary
            {
                Index = secIdx++,
                PageWidth = w,
                PageHeight = h,
                Orientation = orient,
                MarginTop = top,
                MarginBottom = bottom,
                MarginLeft = left,
                MarginRight = right
            });
        }

        if (sectionList.Count == 0)
        {
            sectionList.Add(new SectionSummary
            {
                Index = 0,
                PageWidth = 612.0,
                PageHeight = 792.0,
                Orientation = "Portrait",
                MarginTop = 72.0,
                MarginBottom = 72.0,
                MarginLeft = 72.0,
                MarginRight = 72.0
            });
        }

        // 4. Styles Used
        var stylesUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 5. Revisions
        var revisionsList = new List<RevisionSummary>();

        // 6. Blocks
        var blockList = new List<BlockSummary>();
        var headingHierarchy = new List<(int Level, string Text)>();
        int blockIndex = 0;
        int totalParagraphs = 0;
        int totalTables = 0;
        int pIndex = 0;

        foreach (var element in main.Document.Body.ChildElements)
        {
            if (element is SectionProperties) continue;

            if (element is Paragraph p)
            {
                totalParagraphs++;
                var paraId = GetOrGenerateParaId(p, pIndex++);
                var textId = GetTextId(p);
                var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                if (!string.IsNullOrEmpty(styleId)) stylesUsed.Add(styleId);

                var headingLevel = DetermineHeadingLevel(styleId, p.ParagraphProperties?.OutlineLevel?.Val?.Value);
                var text = p.InnerText ?? "";

                // Update heading path
                if (headingLevel.HasValue && !string.IsNullOrWhiteSpace(text))
                {
                    int lvl = headingLevel.Value;
                    headingHierarchy.RemoveAll(h => h.Level >= lvl);
                    headingHierarchy.Add((lvl, text.Trim()));
                }

                string? headingPath = headingHierarchy.Count > 0
                    ? string.Join(" / ", headingHierarchy.Select(h => h.Text))
                    : null;

                // Revisions in paragraph
                var insElements = p.Descendants<InsertedRun>().ToList();
                var delElements = p.Descendants<DeletedRun>().ToList();
                bool hasRevs = insElements.Count > 0 || delElements.Count > 0;

                foreach (var ins in insElements)
                {
                    var revText = string.Join("", ins.Descendants<Text>().Select(t => t.Text));
                    revisionsList.Add(new RevisionSummary
                    {
                        Id = ins.Id?.Value ?? "",
                        Type = "Insert",
                        Author = ins.Author?.Value ?? "",
                        Date = ins.Date?.Value,
                        Text = revText,
                        ParaId = paraId
                    });
                }

                foreach (var del in delElements)
                {
                    var revText = string.Join("", del.Descendants<DeletedText>().Select(t => t.Text));
                    revisionsList.Add(new RevisionSummary
                    {
                        Id = del.Id?.Value ?? "",
                        Type = "Delete",
                        Author = del.Author?.Value ?? "",
                        Date = del.Date?.Value,
                        Text = revText,
                        ParaId = paraId
                    });
                }

                // Comments in paragraph
                var pCommentIds = p.Descendants<CommentReference>()
                    .Select(cr => cr.Id?.Value ?? "")
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
                    .ToList();

                bool hasComments = pCommentIds.Count > 0;

                // Bookmarks
                var bookmarks = p.Descendants<BookmarkStart>()
                    .Select(b => b.Name?.Value ?? "")
                    .Where(n => !string.IsNullOrEmpty(n) && n != "_GoBack")
                    .Distinct()
                    .ToList();

                // Filters
                if (options.FilterRevisions && !hasRevs) continue;
                if (options.FilterComments && !hasComments) continue;

                if (options.MaxParagraphs <= 0 || blockList.Count < options.MaxParagraphs)
                {
                    blockList.Add(new BlockSummary
                    {
                        Index = blockIndex++,
                        ParaId = paraId,
                        TextId = textId,
                        StyleId = styleId,
                        HeadingLevel = headingLevel,
                        HeadingPath = headingPath,
                        Text = options.IncludeText ? text : "",
                        Xml = options.IncludeXml ? p.OuterXml : null,
                        HasRevisions = hasRevs,
                        HasComments = hasComments,
                        CommentIds = pCommentIds,
                        Bookmarks = bookmarks
                    });
                }
            }
            else if (element is Table tbl)
            {
                totalTables++;
                var rows = tbl.Elements<TableRow>().ToList();
                int rowCount = rows.Count;
                int colCount = rows.Count > 0 ? rows.Max(r => r.Elements<TableCell>().Count()) : 0;
                var cellList = new List<TableCellSummary>();

                for (int r = 0; r < rows.Count; r++)
                {
                    var cells = rows[r].Elements<TableCell>().ToList();
                    for (int c = 0; c < cells.Count; c++)
                    {
                        var cellText = string.Join("\n", cells[c].Elements<Paragraph>().Select(p => p.InnerText)).Trim();
                        cellList.Add(new TableCellSummary
                        {
                            Row = r,
                            Column = c,
                            Text = cellText
                        });
                    }
                }

                var tblText = string.Join(" | ", cellList.Select(c => c.Text));
                var firstPara = tbl.Descendants<Paragraph>().FirstOrDefault();
                var paraId = firstPara != null ? GetOrGenerateParaId(firstPara, pIndex++) : null;

                var tblInsElements = tbl.Descendants<InsertedRun>().ToList();
                var tblDelElements = tbl.Descendants<DeletedRun>().ToList();
                foreach (var ins in tblInsElements)
                {
                    var revText = string.Join("", ins.Descendants<Text>().Select(t => t.Text));
                    var pAncest = ins.Ancestors<Paragraph>().FirstOrDefault();
                    revisionsList.Add(new RevisionSummary
                    {
                        Id = ins.Id?.Value ?? "",
                        Type = "Insert",
                        Author = ins.Author?.Value ?? "",
                        Date = ins.Date?.Value,
                        Text = revText,
                        ParaId = pAncest != null ? GetOrGenerateParaId(pAncest) : paraId
                    });
                }
                foreach (var del in tblDelElements)
                {
                    var revText = string.Join("", del.Descendants<DeletedText>().Select(t => t.Text));
                    if (string.IsNullOrEmpty(revText))
                        revText = string.Join("", del.Descendants<Text>().Select(t => t.Text));
                    var pAncest = del.Ancestors<Paragraph>().FirstOrDefault();
                    revisionsList.Add(new RevisionSummary
                    {
                        Id = del.Id?.Value ?? "",
                        Type = "Delete",
                        Author = del.Author?.Value ?? "",
                        Date = del.Date?.Value,
                        Text = revText,
                        ParaId = pAncest != null ? GetOrGenerateParaId(pAncest) : paraId
                    });
                }

                blockList.Add(new BlockSummary
                {
                    Index = blockIndex++,
                    ParaId = paraId,
                    Text = options.IncludeText ? tblText : "",
                    Xml = options.IncludeXml ? tbl.OuterXml : null,
                    TableInfo = new TableSummary
                    {
                        RowCount = rowCount,
                        ColumnCount = colCount,
                        Cells = cellList
                    }
                });
            }
        }

        // 7. Media Parts
        var mediaList = new List<MediaSummary>();
        foreach (var imgPart in main.ImageParts)
        {
            var relId = main.GetIdOfPart(imgPart);
            long size = 0;
            try
            {
                using var s = imgPart.GetStream();
                size = s.Length;
            }
            catch { }

            mediaList.Add(new MediaSummary
            {
                RelId = relId,
                PartName = imgPart.Uri.ToString(),
                ContentType = imgPart.ContentType,
                SizeBytes = size
            });
        }

        return new DocxStructureReport
        {
            Title = title,
            Creator = creator,
            LastModifiedBy = lastModifiedBy,
            Revision = revision,
            CreatedDate = createdDate,
            ModifiedDate = modifiedDate,
            TotalParagraphs = totalParagraphs,
            TotalTables = totalTables,
            TotalSections = sectionList.Count,
            TotalComments = commentsList.Count,
            TotalRevisions = revisionsList.Count,
            TotalMedia = mediaList.Count,
            HasEmbeddedSource = hasEmbedded,
            Sections = sectionList,
            Blocks = blockList,
            Revisions = revisionsList,
            Comments = commentsList,
            StylesUsed = stylesUsed.OrderBy(s => s).ToList(),
            Media = mediaList
        };
    }

    public static string? GetParaId(Paragraph p)
    {
        try
        {
            if (p.ParagraphId?.Value != null)
                return p.ParagraphId.Value;
        }
        catch { }

        try
        {
            var attr = p.GetAttributes().FirstOrDefault(a => string.Equals(a.LocalName, "paraId", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(attr.Value)) return attr.Value;
        }
        catch { }

        return null;
    }

    public static string GetOrGenerateParaId(Paragraph p, int index = -1)
    {
        var existing = GetParaId(p);
        if (!string.IsNullOrEmpty(existing)) return existing;

        int hash = HashCode.Combine(index, p.InnerText ?? "");
        uint val = (uint)(hash & 0x7FFFFFFF);
        if (val == 0) val = 1;
        return val.ToString("X8");
    }

    public static string? GetTextId(Paragraph p)
    {
        try
        {
            if (p.TextId?.Value != null)
                return p.TextId.Value;
        }
        catch { }

        try
        {
            var attr = p.GetAttributes().FirstOrDefault(a => string.Equals(a.LocalName, "textId", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(attr.Value)) return attr.Value;
        }
        catch { }

        return null;
    }

    private static int? DetermineHeadingLevel(string? styleId, int? outlineLevel)
    {
        if (outlineLevel.HasValue && outlineLevel.Value >= 0 && outlineLevel.Value <= 8)
        {
            return outlineLevel.Value + 1;
        }

        if (string.IsNullOrWhiteSpace(styleId)) return null;

        var normalized = styleId.Replace(" ", "").ToLowerInvariant();
        if (normalized.StartsWith("heading"))
        {
            var suffix = normalized.Substring(7);
            if (int.TryParse(suffix, out int level)) return level;
        }

        if (normalized.StartsWith("h") && normalized.Length == 2 && char.IsDigit(normalized[1]))
        {
            return normalized[1] - '0';
        }

        return null;
    }
}
