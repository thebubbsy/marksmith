using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Ocr;
using Xunit;

namespace MarkSmith.Core.Tests.Ocr;

public class OcrInspectionStudioTests
{
    [Fact]
    public void Picture_Reclassified_To_ShapeMosaic_Generates_Compact_Shapes_And_RoundTrips_Back()
    {
        var box = new OcrNormRect(0.1f, 0.2f, 0.8f, 0.4f);
        var words = new List<OcrTraceWord>
        {
            new("Quarterly", new OcrNormRect(0.2f, 0.25f, 0.2f, 0.05f), 0),
            new("Roadmap", new OcrNormRect(0.45f, 0.25f, 0.2f, 0.05f), 0),
        };
        const string originalPhotoMd = "![Quarterly Strategy Roadmap](media/page1-photo1.png)";

        var elem = new OcrInspectionElement
        {
            Id = 1,
            PageNumber = 1,
            BlockIndex = 0,
            Kind = OcrBlockKind.Picture,
            OriginalKind = OcrBlockKind.Picture,
            OriginalMarkdown = originalPhotoMd,
            CurrentMarkdown = originalPhotoMd,
            Box = box,
            Words = words,
        };

        // User changes classification from Picture to ShapeMosaic
        elem.Reclassify(OcrBlockKind.ShapeMosaic);

        Assert.Equal(OcrBlockKind.ShapeMosaic, elem.Kind);
        Assert.True(elem.IsReclassified);
        Assert.StartsWith(":::shapes compact=true", elem.CurrentMarkdown);
        Assert.Contains("rect", elem.CurrentMarkdown);
        Assert.Contains("roundRect", elem.CurrentMarkdown);
        Assert.Contains("Quarterly", elem.CurrentMarkdown);
        Assert.Contains("Roadmap", elem.CurrentMarkdown);

        // User changes mind and switches back to Picture
        elem.Reclassify(OcrBlockKind.Picture);

        Assert.Equal(OcrBlockKind.Picture, elem.Kind);
        Assert.False(elem.IsReclassified);
        Assert.Equal(originalPhotoMd, elem.CurrentMarkdown);
    }

    [Fact]
    public void Picture_Reclassified_To_Diagram_Generates_Mermaid_Flowchart()
    {
        var box = new OcrNormRect(0.08f, 0.15f, 0.84f, 0.35f);
        var words = new List<OcrTraceWord>
        {
            new("Ingest", new OcrNormRect(0.12f, 0.2f, 0.15f, 0.04f), 0),
            new("Process", new OcrNormRect(0.35f, 0.2f, 0.15f, 0.04f), 0),
            new("Publish", new OcrNormRect(0.60f, 0.2f, 0.15f, 0.04f), 0),
        };

        var elem = new OcrInspectionElement
        {
            Id = 2,
            PageNumber = 1,
            BlockIndex = 1,
            Kind = OcrBlockKind.Picture,
            OriginalKind = OcrBlockKind.Picture,
            OriginalMarkdown = "![Architecture Diagram Image](media/diag.png)",
            CurrentMarkdown = "![Architecture Diagram Image](media/diag.png)",
            Box = box,
            Words = words,
        };

        elem.Reclassify(OcrBlockKind.Diagram);

        Assert.Equal(OcrBlockKind.Diagram, elem.Kind);
        Assert.True(elem.IsReclassified);
        Assert.StartsWith("```mermaid", elem.CurrentMarkdown);
        Assert.Contains("flowchart LR", elem.CurrentMarkdown);
        Assert.Contains("Ingest", elem.CurrentMarkdown);
        Assert.Contains("Process", elem.CurrentMarkdown);
        Assert.Contains("Publish", elem.CurrentMarkdown);
        Assert.Contains("-->", elem.CurrentMarkdown);
    }

    [Fact]
    public void Text_Element_Reclassified_To_Table_Generates_Structured_Markdown_Table()
    {
        var box = new OcrNormRect(0.05f, 0.1f, 0.9f, 0.3f);
        var words = new List<OcrTraceWord>
        {
            new("Server", new OcrNormRect(0.1f, 0.12f, 0.1f, 0.03f), 0),
            new("Online", new OcrNormRect(0.3f, 0.12f, 0.1f, 0.03f), 0),
            new("Database", new OcrNormRect(0.1f, 0.18f, 0.1f, 0.03f), 0),
            new("Healthy", new OcrNormRect(0.3f, 0.18f, 0.1f, 0.03f), 0),
        };

        var elem = new OcrInspectionElement
        {
            Id = 3,
            PageNumber = 1,
            BlockIndex = 2,
            Kind = OcrBlockKind.Paragraph,
            OriginalKind = OcrBlockKind.Paragraph,
            OriginalMarkdown = "Server Online Database Healthy",
            CurrentMarkdown = "Server Online Database Healthy",
            Box = box,
            Words = words,
        };

        elem.Reclassify(OcrBlockKind.Table);

        Assert.Equal(OcrBlockKind.Table, elem.Kind);
        Assert.True(elem.IsReclassified);
        Assert.Contains("| Key / Column | Value / Metric |", elem.CurrentMarkdown);
        Assert.Contains("| Server | Online |", elem.CurrentMarkdown);
        Assert.Contains("| Database | Healthy |", elem.CurrentMarkdown);
    }

    [Fact]
    public void Paragraph_Reclassified_To_Heading_And_KeyValue_And_List()
    {
        var box = new OcrNormRect(0.08f, 0.1f, 0.84f, 0.1f);
        const string text = "Platform Security Guidelines";
        var words = new List<OcrTraceWord>
        {
            new("Platform", new OcrNormRect(0.1f, 0.1f, 0.1f, 0.03f), 0),
            new("Security", new OcrNormRect(0.22f, 0.1f, 0.1f, 0.03f), 0),
            new("Guidelines", new OcrNormRect(0.34f, 0.1f, 0.1f, 0.03f), 0),
        };

        var elem = new OcrInspectionElement
        {
            Id = 4,
            PageNumber = 1,
            BlockIndex = 3,
            Kind = OcrBlockKind.Paragraph,
            OriginalKind = OcrBlockKind.Paragraph,
            OriginalMarkdown = text,
            CurrentMarkdown = text,
            Box = box,
            Words = words,
        };

        // Reclassify to Heading
        elem.Reclassify(OcrBlockKind.Heading);
        Assert.Equal("## Platform Security Guidelines", elem.CurrentMarkdown);

        // Reclassify to List
        elem.Reclassify(OcrBlockKind.List);
        Assert.Contains("- Platform Security Guidelines", elem.CurrentMarkdown);

        // Reclassify to KeyValue
        elem.Reclassify(OcrBlockKind.KeyValue);
        Assert.Contains("- **Platform:** Security", elem.CurrentMarkdown);
    }

    [Fact]
    public void MultiPage_InspectionSession_Supports_Page_Flicking_And_Reclassification_Aggregation()
    {
        var session = new OcrInspectionSession { DocumentTitle = "Multi-Page Annual Report" };

        // Page 1: Heading + Photo
        var p1Preview = new OcrPagePreview(1, 0, 3, new byte[100], 800, 1000, "MarkSmith OCR");
        var p1Blocks = new List<OcrTraceBlock>
        {
            new(0, OcrBlockKind.Heading, "# Annual Overview", new OcrNormRect(0.1f, 0.05f, 0.8f, 0.06f), 2),
            new(1, OcrBlockKind.Picture, "![Team Photo](media/p1-photo.png)", new OcrNormRect(0.1f, 0.2f, 0.8f, 0.4f), 0),
        };
        var p1Trace = new OcrPageTrace(1, new List<OcrTraceWord>(), p1Blocks, "# Annual Overview\n\n![Team Photo](media/p1-photo.png)", TimeSpan.FromMilliseconds(50));
        session.AddOrUpdatePage(p1Preview, p1Trace);

        // Page 2: Paragraph + Diagram
        var p2Preview = new OcrPagePreview(2, 1, 3, new byte[100], 800, 1000, "MarkSmith OCR");
        var p2Words = new List<OcrTraceWord>
        {
            new("Design", new OcrNormRect(0.15f, 0.3f, 0.1f, 0.03f), 1),
            new("Build", new OcrNormRect(0.40f, 0.3f, 0.1f, 0.03f), 1),
            new("Deploy", new OcrNormRect(0.65f, 0.3f, 0.1f, 0.03f), 1),
        };
        var p2Blocks = new List<OcrTraceBlock>
        {
            new(0, OcrBlockKind.Paragraph, "Production lifecycle workflow.", new OcrNormRect(0.1f, 0.1f, 0.8f, 0.05f), 3),
            new(1, OcrBlockKind.Picture, "![Lifecycle Flow](media/p2-flow.png)", new OcrNormRect(0.1f, 0.25f, 0.8f, 0.35f), 3),
        };
        var p2Trace = new OcrPageTrace(2, p2Words, p2Blocks, "Production lifecycle workflow.\n\n![Lifecycle Flow](media/p2-flow.png)", TimeSpan.FromMilliseconds(55));
        session.AddOrUpdatePage(p2Preview, p2Trace);

        // Page 3: Key-Values
        var p3Preview = new OcrPagePreview(3, 2, 3, new byte[100], 800, 1000, "MarkSmith OCR");
        var p3Blocks = new List<OcrTraceBlock>
        {
            new(0, OcrBlockKind.Paragraph, "Revenue 50M Profit 12M", new OcrNormRect(0.1f, 0.1f, 0.8f, 0.2f), 4),
        };
        var p3Trace = new OcrPageTrace(3, new List<OcrTraceWord>(), p3Blocks, "Revenue 50M Profit 12M", TimeSpan.FromMilliseconds(45));
        session.AddOrUpdatePage(p3Preview, p3Trace);

        Assert.Equal(3, session.Pages.Count);
        Assert.Equal(0, session.CurrentPageIndex);
        Assert.Equal(1, session.CurrentPage?.PageNumber);
        Assert.False(session.CanPreviousPage);
        Assert.True(session.CanNextPage);

        // Flick to page 2
        session.NextPage();
        Assert.Equal(1, session.CurrentPageIndex);
        Assert.Equal(2, session.CurrentPage?.PageNumber);
        Assert.True(session.CanPreviousPage);
        Assert.True(session.CanNextPage);

        // Reclassify element 2 on page 2 from Picture to Diagram
        bool reclassifiedP2 = session.ReclassifyElement(pageNumber: 2, elementId: 2, newKind: OcrBlockKind.Diagram);
        Assert.True(reclassifiedP2);
        Assert.Contains("```mermaid", session.CurrentPage!.Elements[1].CurrentMarkdown);

        // Flick to page 1: reclassify element 2 from Picture to ShapeMosaic
        session.PreviousPage();
        Assert.Equal(0, session.CurrentPageIndex);
        bool reclassifiedP1 = session.ReclassifyElement(pageNumber: 1, elementId: 2, newKind: OcrBlockKind.ShapeMosaic);
        Assert.True(reclassifiedP1);
        Assert.Contains(":::shapes compact=true", session.CurrentPage!.Elements[1].CurrentMarkdown);

        // Verify total reclassifications tracked across pages
        Assert.Equal(2, session.TotalReclassifications);

        // Verify full assembled document Markdown across all 3 pages
        string fullDoc = session.GetDocumentMarkdown();
        Assert.Contains("# Annual Overview", fullDoc);
        Assert.Contains(":::shapes compact=true", fullDoc);
        Assert.Contains("```mermaid", fullDoc);
        Assert.Contains("Revenue 50M Profit 12M", fullDoc);
        Assert.Contains("---", fullDoc); // Page break divider
    }

    [Fact]
    public void Empty_Or_Uninitialized_InspectionSession_Does_Not_Throw_And_Handles_Telemetry_Safely()
    {
        var session = new OcrInspectionSession();

        // 1. Initial / empty state sanity
        Assert.Empty(session.Pages);
        Assert.Null(session.CurrentPage);
        Assert.Equal(0, session.CurrentPageIndex);
        Assert.False(session.CanPreviousPage);
        Assert.False(session.CanNextPage);
        Assert.Equal(0, session.TotalElements);
        Assert.Equal(0, session.TotalWords);
        Assert.Equal(0, session.TotalVisuals);
        Assert.Equal(0, session.TotalReclassifications);
        Assert.Equal(string.Empty, session.GetDocumentMarkdown());

        // 2. Navigation operations on empty session must be no-ops without throwing
        session.PreviousPage();
        Assert.Equal(0, session.CurrentPageIndex);
        session.NextPage();
        Assert.Equal(0, session.CurrentPageIndex);
        session.GoToPage(10);
        Assert.Equal(0, session.CurrentPageIndex);
        session.GoToPage(-5);
        Assert.Equal(0, session.CurrentPageIndex);

        // 3. Reclassification attempts on empty session safely return false
        bool reclassified = session.ReclassifyElement(1, 1, OcrBlockKind.Table);
        Assert.False(reclassified);
        Assert.Equal(0, session.TotalReclassifications);
    }
}
