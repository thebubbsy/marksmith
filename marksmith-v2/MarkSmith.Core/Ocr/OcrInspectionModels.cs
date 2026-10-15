namespace MarkSmith.Ocr;

/// <summary>
/// A detected document block or visual element available for user inspection and reclassification
/// inside the standalone OCR Inspection Studio.
/// </summary>
public sealed class OcrInspectionElement
{
    public int Id { get; init; }
    public int PageNumber { get; init; }
    public int BlockIndex { get; init; }
    public OcrBlockKind OriginalKind { get; init; }
    public string OriginalMarkdown { get; init; } = string.Empty;
    public OcrNormRect Box { get; init; }
    public IReadOnlyList<OcrTraceWord> Words { get; init; } = Array.Empty<OcrTraceWord>();
    public int WordCount => Words.Count;

    public OcrBlockKind Kind { get; set; }
    public string CurrentMarkdown { get; set; } = string.Empty;

    public bool IsReclassified => Kind != OriginalKind || !string.Equals(CurrentMarkdown, OriginalMarkdown, StringComparison.Ordinal);

    public string KindDisplayName => Kind switch
    {
        OcrBlockKind.Picture => "Picture",
        OcrBlockKind.ShapeMosaic => "Mosaic / Shape",
        OcrBlockKind.Diagram => "Diagram",
        OcrBlockKind.Table => "Table",
        OcrBlockKind.KeyValue => "Key-Value",
        OcrBlockKind.Heading => "Heading",
        OcrBlockKind.List => "List",
        OcrBlockKind.Paragraph or _ => "Text Block",
    };

    public string KindIconGlyph => Kind switch
    {
        OcrBlockKind.Picture => "\uEB9F",
        OcrBlockKind.ShapeMosaic => "\uE790",
        OcrBlockKind.Diagram => "\uE8A9",
        OcrBlockKind.Table => "\uE8FD",
        OcrBlockKind.KeyValue => "\uE8C0",
        OcrBlockKind.Heading => "\uE8B7",
        OcrBlockKind.List => "\uEA37",
        OcrBlockKind.Paragraph or _ => "\uE8C4",
    };

    public void Reclassify(OcrBlockKind newKind)
    {
        if (newKind == OriginalKind)
        {
            Kind = OriginalKind;
            CurrentMarkdown = OriginalMarkdown;
            return;
        }

        CurrentMarkdown = OcrBlockReclassifier.Reclassify(
            newKind, Kind, CurrentMarkdown, Words, PageNumber, BlockIndex, Box);
        Kind = newKind;
    }

    public void ResetToOriginal()
    {
        Kind = OriginalKind;
        CurrentMarkdown = OriginalMarkdown;
    }
}

/// <summary>
/// A single page inside the interactive OCR inspection studio with its image preview and elements.
/// </summary>
public sealed class OcrInspectionPage
{
    public int PageNumber { get; init; }
    public int PageIndex { get; init; }
    public int PageCount { get; init; }
    public byte[] Image { get; init; } = Array.Empty<byte>();
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public string Engine { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }

    public List<OcrInspectionElement> Elements { get; } = new();
    public List<OcrTraceWord> Words { get; } = new();
    public List<float> CutLines { get; } = new();
    public string OriginalPageMarkdown { get; init; } = string.Empty;

    public int VisualsCount => Elements.Count(e => e.Kind is OcrBlockKind.Picture or OcrBlockKind.ShapeMosaic or OcrBlockKind.Diagram);
    public int ReclassifiedCount => Elements.Count(e => e.IsReclassified);

    /// <summary>
    /// Resets all reclassified elements on this page back to their original OCR classification.
    /// Returns the number of elements that were modified.
    /// </summary>
    public int ResetAllElements()
    {
        int count = 0;
        foreach (var elem in Elements)
        {
            if (elem.IsReclassified)
            {
                elem.ResetToOriginal();
                count++;
            }
        }
        return count;
    }

    public string AssembledPageMarkdown
    {
        get
        {
            if (Elements.Count == 0) return OriginalPageMarkdown;
            return string.Join("\n\n", Elements.Select(e => e.CurrentMarkdown)).Trim();
        }
    }
}

/// <summary>
/// Result returned when the user completes or cancels the inspection studio session.
/// </summary>
public sealed record OcrInspectionResult(
    bool Imported,
    string Markdown,
    int PageCount,
    int ReclassifiedCount);

/// <summary>
/// The stateful session driving the interactive OCR Inspection Studio, allowing page flicking,
/// element selection, and real-time Markdown re-synthesis across all document pages.
/// </summary>
public sealed class OcrInspectionSession
{
    public string DocumentTitle { get; set; } = "Document Import";
    public List<OcrInspectionPage> Pages { get; } = new();

    private int _currentPageIndex;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set => _currentPageIndex = Pages.Count > 0 ? Math.Clamp(value, 0, Pages.Count - 1) : 0;
    }

    public OcrInspectionPage? CurrentPage =>
        Pages.Count > 0 && CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count
            ? Pages[CurrentPageIndex]
            : null;

    public bool CanPreviousPage => CurrentPageIndex > 0;
    public bool CanNextPage => CurrentPageIndex < Pages.Count - 1;

    public int TotalElements => Pages.Sum(p => p.Elements.Count);
    public int TotalWords => Pages.Sum(p => p.Words.Count);
    public int TotalVisuals => Pages.Sum(p => p.VisualsCount);
    public int TotalReclassifications => Pages.Sum(p => p.ReclassifiedCount);

    public void PreviousPage()
    {
        if (CanPreviousPage) CurrentPageIndex--;
    }

    public void NextPage()
    {
        if (CanNextPage) CurrentPageIndex++;
    }

    public void GoToPage(int pageIndex)
    {
        CurrentPageIndex = pageIndex;
    }

    public void GoToFirstPage()
    {
        if (Pages.Count > 0) CurrentPageIndex = 0;
    }

    public void GoToLastPage()
    {
        if (Pages.Count > 0) CurrentPageIndex = Pages.Count - 1;
    }

    /// <summary>
    /// Resets all reclassified elements on the current page back to their original OCR classification.
    /// Returns the number of elements reset.
    /// </summary>
    public int ResetCurrentPage() => CurrentPage?.ResetAllElements() ?? 0;

    /// <summary>
    /// Maps 1-based shortcut numbers (1..8) matching the Quick Reclassify dropdown order to <see cref="OcrBlockKind"/>.
    /// </summary>
    public static bool TryMapShortcutNumberToKind(int number, out OcrBlockKind kind)
    {
        switch (number)
        {
            case 1: kind = OcrBlockKind.Picture; return true;
            case 2: kind = OcrBlockKind.ShapeMosaic; return true;
            case 3: kind = OcrBlockKind.Diagram; return true;
            case 4: kind = OcrBlockKind.Table; return true;
            case 5: kind = OcrBlockKind.KeyValue; return true;
            case 6: kind = OcrBlockKind.Heading; return true;
            case 7: kind = OcrBlockKind.List; return true;
            case 8: kind = OcrBlockKind.Paragraph; return true;
            default: kind = default; return false;
        }
    }

    public OcrInspectionPage AddOrUpdatePage(OcrPagePreview preview, OcrPageTrace trace)
    {
        var existing = Pages.FirstOrDefault(p => p.PageNumber == preview.PageNumber);
        byte[] image = preview.Image.Length > 0 ? preview.Image : (existing?.Image ?? Array.Empty<byte>());
        int imgW = preview.ImageWidth > 0 ? preview.ImageWidth : (existing?.ImageWidth ?? 960);
        int imgH = preview.ImageHeight > 0 ? preview.ImageHeight : (existing?.ImageHeight ?? 1320);
        int pageCount = preview.PageCount > 0 ? preview.PageCount : (existing?.PageCount ?? Math.Max(1, Pages.Count + 1));
        string engine = !string.IsNullOrWhiteSpace(preview.Engine) ? preview.Engine : (existing?.Engine ?? "MarkSmith OCR");

        if (existing is not null) Pages.Remove(existing);

        var page = new OcrInspectionPage
        {
            PageNumber = preview.PageNumber,
            PageIndex = preview.PageIndex >= 0 ? preview.PageIndex : Math.Max(0, preview.PageNumber - 1),
            PageCount = pageCount,
            Image = image,
            ImageWidth = imgW,
            ImageHeight = imgH,
            Engine = engine,
            Elapsed = trace.Elapsed,
            OriginalPageMarkdown = trace.Markdown,
        };

        page.Words.AddRange(trace.Words);
        if (trace.CutLines is not null)
            page.CutLines.AddRange(trace.CutLines);

        int elementIdCounter = 1;
        for (int i = 0; i < trace.Blocks.Count; i++)
        {
            var b = trace.Blocks[i];
            var blockWords = trace.Words.Where(w => w.Block == b.Index).ToList();
            if (blockWords.Count == 0 && !b.Box.Width.Equals(0))
            {
                // Fallback: match words geometrically inside block box
                blockWords = trace.Words.Where(w =>
                    w.Box.X >= b.Box.X - 0.02f && w.Box.Right <= b.Box.Right + 0.02f &&
                    w.Box.Y >= b.Box.Y - 0.02f && w.Box.Bottom <= b.Box.Bottom + 0.02f).ToList();
            }

            var elem = new OcrInspectionElement
            {
                Id = elementIdCounter++,
                PageNumber = preview.PageNumber,
                BlockIndex = i,
                Kind = b.Kind,
                OriginalKind = b.Kind,
                OriginalMarkdown = b.Markdown,
                CurrentMarkdown = b.Markdown,
                Box = b.Box,
                Words = blockWords,
            };
            page.Elements.Add(elem);
        }

        Pages.Add(page);
        Pages.Sort((a, b) => a.PageNumber.CompareTo(b.PageNumber));
        return page;
    }

    /// <summary>
    /// Reclassifies a specific element on a specific page.
    /// </summary>
    public bool ReclassifyElement(int pageNumber, int elementId, OcrBlockKind newKind)
    {
        var page = Pages.FirstOrDefault(p => p.PageNumber == pageNumber);
        if (page is null) return false;
        var elem = page.Elements.FirstOrDefault(e => e.Id == elementId);
        if (elem is null) return false;

        elem.Reclassify(newKind);
        return true;
    }

    /// <summary>
    /// Generates the complete finalized Markdown document across all pages.
    /// Multi-page documents are formatted cleanly with page separator breaks.
    /// </summary>
    public string GetDocumentMarkdown()
    {
        if (Pages.Count == 0) return string.Empty;
        if (Pages.Count == 1) return Pages[0].AssembledPageMarkdown;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Pages.Count; i++)
        {
            string pMd = Pages[i].AssembledPageMarkdown;
            if (!string.IsNullOrWhiteSpace(pMd))
            {
                if (sb.Length > 0) sb.Append("\n\n---\n\n");
                sb.Append(pMd);
            }
        }
        return sb.ToString().Trim();
    }
}
