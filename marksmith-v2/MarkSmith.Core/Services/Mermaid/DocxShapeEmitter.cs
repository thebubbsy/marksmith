using System.Text;
using MarkSmith.Models;

namespace MarkSmith.Services.Mermaid;

// ShapeForge's final stage: turns an MDiagram (pure geometry, points) into a single inline Word
// drawing — a wpg (WordprocessingGroup) containing native wps shapes. The result is a REAL Word
// object: select the group, drag it, ungroup it, restyle any node, edit any label. No images.
//
// Emitted as an XML string for a <w:p> paragraph; callers materialize it with
// `new W.Paragraph { InnerXml = ... }` so the OpenXml SDK parses it into typed elements
// (Office 2010 wps/wpg classes) and the document stays schema-validatable.
public static class DocxShapeEmitter
{
    private const long EmuPerPt = 12700;

    // Mermaid renders every diagram in its default font stack ("trebuchet ms", verdana, arial,
    // sans-serif) — the preview never overrides it. Word falls back to the document's body font
    // (Calibri) unless we name a face explicitly, so pin diagram text to Trebuchet MS to keep the
    // exported shapes looking exactly like the live preview.
    private const string DiagramFont = "Trebuchet MS";

    // Returns true when the diagram is too large for print layout even at the floor — the
    // caller opens the document in Word's Web Layout view (scrolls instead of clipping).
    // oversizedMode: 0=Ask,1=Exact,2=Reflow,3=MultiPageVertical,4=Grid,5=ShrinkToFit
    internal static bool ScaleToFit(MDiagram d, int oversizedMode = 0, int gridSize = 1)
    {
        double canvasW = MaxCanvasW, canvasH = MaxCanvasH;

        // Grid mode: enlarge the canvas by the grid multiplier so the diagram has N× the room.
        if (oversizedMode == 4 && gridSize >= 2)
        {
            canvasW *= gridSize;
            canvasH *= gridSize;
        }

        // Multi-page vertical: only constrain width — height is unlimited (will be split into
        // page-height bands by ToMultiPageParagraphXml).
        if (oversizedMode == 3)
        {
            double sw = Math.Min(1, canvasW / Math.Max(1, d.Width));
            if (sw < 1)
            {
                foreach (var sh in d.Shapes)
                {
                    sh.X *= sw; sh.Y *= sw; sh.W *= sw; sh.H *= sw;
                    sh.FontSize = Math.Max(6, sh.FontSize * sw);
                }
                foreach (var c in d.Connectors)
                {
                    c.X1 *= sw; c.Y1 *= sw; c.X2 *= sw; c.Y2 *= sw;
                    c.LabelX *= sw; c.LabelY *= sw; c.LabelW *= sw; c.LabelH *= sw;
                    if (c.Points is { Count: > 0 })
                        c.Points = c.Points.Select(p => (p.X * sw, p.Y * sw)).ToList();
                }
                d.Width *= sw; d.Height *= sw;
            }
            return false; // never Web Layout — the caller splits into bands
        }

        // Exact Layout (mode 1): never scale. Preserve exactly what Mermaid computed, 
        // triggering Web Layout if it exceeds the page bounds.
        if (oversizedMode == 1)
        {
            return d.Width > canvasW || d.Height > canvasH;
        }

        double s = Math.Min(1, Math.Min(
            canvasW / Math.Max(1, d.Width),
            canvasH / Math.Max(1, d.Height)));

        // ShrinkToFit (mode 5): floor drops to 30% — the whole point is to squeeze onto one page.
        double floor = oversizedMode == 5 ? 0.30 : 0.75;
        bool oversized = s < floor;
        if (oversized) s = floor;

        // Modes 6, 7, 8: Always shrink to fit page (down to 30%), but selectively shrink spacing/shapes
        if (oversizedMode is 6 or 7 or 8)
        {
            oversized = false; // It fits, no Web Layout needed
            
            // Re-center around diagram's center point
            double cx = d.Width / 2;
            double cy = d.Height / 2;

            double sSpace = 1;
            double sShape = 1;

            if (d.Shapes.Count > 0)
            {
                var shapeL = d.Shapes.MinBy(s => s.X)!;
                var shapeR = d.Shapes.MaxBy(s => s.X + s.W)!;
                var shapeT = d.Shapes.MinBy(s => s.Y)!;
                var shapeB = d.Shapes.MaxBy(s => s.Y + s.H)!;

                if (oversizedMode == 8) 
                {
                    sSpace = Math.Max(0.30, Math.Min(1, Math.Min(canvasW / d.Width, canvasH / d.Height)));
                    sShape = sSpace;
                }
                else if (oversizedMode == 6) // Shrink Spacing
                {
                    double spanX = shapeR.X - shapeL.X;
                    double spanY = shapeB.Y - shapeT.Y;
                    double sX = spanX > 0 ? (canvasW - shapeR.W) / spanX : 1;
                    double sY = spanY > 0 ? (canvasH - shapeB.H) / spanY : 1;
                    sSpace = Math.Max(0.30, Math.Min(1, Math.Min(sX, sY)));
                    
                    // Fallback to shrinking shapes if spacing alone cannot geometrically fit it
                    if (sX < 0.30 || sY < 0.30)
                    {
                        double reqShapeX = shapeR.W > 0 ? (canvasW - spanX * 0.30) / shapeR.W : 1;
                        double reqShapeY = shapeB.H > 0 ? (canvasH - spanY * 0.30) / shapeB.H : 1;
                        sShape = Math.Max(0.30, Math.Min(1, Math.Min(reqShapeX, reqShapeY)));
                    }
                }
                else if (oversizedMode == 7) // Shrink Shapes
                {
                    double spanCentersX = (shapeR.X + shapeR.W/2) - (shapeL.X + shapeL.W/2);
                    double spanCentersY = (shapeB.Y + shapeB.H/2) - (shapeT.Y + shapeT.H/2);
                    double sumHalfWidths = shapeR.W/2 + shapeL.W/2;
                    double sumHalfHeights = shapeB.H/2 + shapeT.H/2;
                    
                    double sX = sumHalfWidths > 0 ? (canvasW - spanCentersX) / sumHalfWidths : 1;
                    double sY = sumHalfHeights > 0 ? (canvasH - spanCentersY) / sumHalfHeights : 1;
                    sShape = Math.Max(0.30, Math.Min(1, Math.Min(sX, sY)));
                    
                    // Fallback to shrinking spacing if shapes alone cannot geometrically fit it
                    if (sX < 0.30 || sY < 0.30)
                    {
                        double reqSpaceX = spanCentersX > 0 ? (canvasW - sumHalfWidths * 0.30) / spanCentersX : 1;
                        double reqSpaceY = spanCentersY > 0 ? (canvasH - sumHalfHeights * 0.30) / spanCentersY : 1;
                        sSpace = Math.Max(0.30, Math.Min(1, Math.Min(reqSpaceX, reqSpaceY)));
                    }
                }
            }

            foreach (var sh in d.Shapes)
            {
                // 1. Spacing transformation
                sh.X = cx + (sh.X - cx) * sSpace;
                sh.Y = cy + (sh.Y - cy) * sSpace;
                
                // 2. Shape transformation
                double hw = sh.W / 2;
                double hh = sh.H / 2;
                sh.X = (sh.X + hw) - (hw * sShape); // keep center fixed
                sh.Y = (sh.Y + hh) - (hh * sShape);
                sh.W *= sShape;
                sh.H *= sShape;
                sh.FontSize = Math.Max(6, sh.FontSize * sShape);
            }

            foreach (var c in d.Connectors)
            {
                c.X1 = cx + (c.X1 - cx) * sSpace;
                c.Y1 = cy + (c.Y1 - cy) * sSpace;
                c.X2 = cx + (c.X2 - cx) * sSpace;
                c.Y2 = cy + (c.Y2 - cy) * sSpace;
                c.LabelX = cx + (c.LabelX - cx) * sSpace;
                c.LabelY = cy + (c.LabelY - cy) * sSpace;

                c.LabelW *= sShape;
                c.LabelH *= sShape;
                
                // Clear explicit curved path so Smart Connectors take over
                c.Points = null;
            }

            // Recompute bounding box
            double minX = d.Shapes.Count > 0 ? d.Shapes.Min(x => x.X) : 0;
            double minY = d.Shapes.Count > 0 ? d.Shapes.Min(x => x.Y) : 0;
            double maxX = d.Shapes.Count > 0 ? d.Shapes.Max(x => x.X + x.W) : 0;
            double maxY = d.Shapes.Count > 0 ? d.Shapes.Max(x => x.Y + x.H) : 0;
            
            // Normalize so minX/minY is 0
            foreach (var sh in d.Shapes) { sh.X -= minX; sh.Y -= minY; }
            foreach (var c in d.Connectors) { c.X1 -= minX; c.Y1 -= minY; c.X2 -= minX; c.Y2 -= minY; c.LabelX -= minX; c.LabelY -= minY; }

            d.Width = Math.Max(1, maxX - minX);
            d.Height = Math.Max(1, maxY - minY);
            return false;
        }

        if (s >= 1) return d.Height > 480;

        foreach (var sh in d.Shapes)
        {
            sh.X *= s; sh.Y *= s; sh.W *= s; sh.H *= s;
            sh.FontSize = Math.Max(6, sh.FontSize * s); // keep labels legible
        }
        foreach (var c in d.Connectors)
        {
            c.X1 *= s; c.Y1 *= s; c.X2 *= s; c.Y2 *= s;
            c.LabelX *= s; c.LabelY *= s; c.LabelW *= s; c.LabelH *= s;
            if (c.Points is { Count: > 0 })
                c.Points = c.Points.Select(p => (p.X * s, p.Y * s)).ToList();
        }
        d.Width *= s; d.Height *= s;

        // ShrinkToFit: it fits on one page by definition — never trigger Web Layout.
        if (oversizedMode == 5) return false;

        return oversized || d.Height > 480; // page-dominating diagrams read better in Web Layout
    }

    // The printable window (A4/Letter minus margins). Diagrams wider or taller are scaled
    // uniformly so no renderer ever runs a bar or box past the page edge.
    private const double MaxCanvasW = 460, MaxCanvasH = 640;

    public static string ToParagraphXml(MDiagram d, ThemeDefinition theme, uint docPrId, out bool oversized,
        int oversizedMode = 0, int gridSize = 1, bool smartConnectors = true, string connectorRouting = "default")
    {
        // Assign shape XML IDs and anchor every edge to its nodes BEFORE ScaleToFit runs. The
        // topology heuristic matches edge endpoints against shape borders, which only agree on
        // the original layout — the aggressive-shrink modes (6/7/8) then re-space the nodes and
        // discard the sampled curves, so the anchors must be captured first or every edge comes
        // out detached from its nodes (the "nothing is connected" regression).
        uint id = 1;
        foreach (var s in d.Shapes) s.Id = ++id; // shapes take 2,3,…; id 1 is reserved for the background card
        if (smartConnectors) AssignTopologyHeuristics(d);

        oversized = ScaleToFit(d, oversizedMode, gridSize);

        // The aggressive-shrink modes threw the curve paths away; snap each anchored edge back
        // onto its (now moved) shapes' connection sites so the stored line touches the nodes and
        // Word's smart-connector glue (stCxn/endCxn) keeps it attached.
        if (smartConnectors && oversizedMode is 6 or 7 or 8)
            ReanchorConnectorsToShapes(d);

        var ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                 "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
                 "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                 "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
                 "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\"";

        long cx = Emu(Math.Max(1, d.Width)), cy = Emu(Math.Max(1, d.Height));
        var sb = new StringBuilder();

        sb.Append($"<w:r {ns}><w:drawing>");
        sb.Append($"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">");
        sb.Append($"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>");
        sb.Append($"<wp:docPr id=\"{docPrId}\" name=\"ShapeForge diagram {docPrId}\" descr=\"Mermaid diagram rebuilt as editable Word shapes by Marksmith\"/>");
        sb.Append("<wp:cNvGraphicFramePr/>");
        sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\">");
        sb.Append("<wpg:wgp>");
        sb.Append($"<wpg:cNvGrpSpPr/>");
        sb.Append($"<wpg:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/>" +
                  $"<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:noFill/></wpg:grpSpPr>");

        // Full-bleed background card (first child => paints behind everything). The group canvas is
        // transparent, so on a dark-themed page the page colour would otherwise show through the
        // diagram; a solid card in the diagram's own (light) background keeps it crisp and readable.
        sb.Append(BackgroundXml(d, theme, ++id));

        // Shape XML IDs and edge→node topology were assigned up top, before ScaleToFit ran.

        // Z-order (Word paints later children on top): subgraph containers first, then edge lines,
        // then the regular node boxes. A container is an opaque panel that must sit BEHIND its
        // member nodes (otherwise it covers them — the "subgraph on top of the smaller squares"
        // bug), while edge lines trace over the panel and nodes top everything. Matches mermaid,
        // which draws cluster rects below edges below nodes.
        foreach (var s in d.Shapes)
        {
            if (s.Kind == ShapeKind.Subgraph) sb.Append(ShapeXml(s, theme, s.Id));
        }

        foreach (var c in d.Connectors)
        {
            sb.Append(ConnectorXml(c, theme, ++id, smartConnectors, connectorRouting));
            if (!string.IsNullOrWhiteSpace(c.Label))
            {
                id++;
                sb.Append(ShapeXml(new MShape
                {
                    Kind = ShapeKind.Text,
                    X = c.LabelX, Y = c.LabelY, W = c.LabelW, H = c.LabelH,
                    Text = c.Label, FontSize = 8.5, TextColor = null,
                }, theme, id));
            }
        }
        foreach (var s in d.Shapes)
        {
            if (s.Kind != ShapeKind.Subgraph) sb.Append(ShapeXml(s, theme, s.Id));
        }

        sb.Append("</wpg:wgp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>");
        return sb.ToString();
    }

    /// <summary>
    /// Multi-page vertical mode (OversizedDiagramMode 3): splits the diagram into page-height
    /// bands, each emitted as a separate inline drawing paragraph.  The diagram is scaled to fit
    /// page width (MaxCanvasW) but height is unconstrained — each band is at most MaxCanvasH tall.
    /// Returns one paragraph XML string per band (callers append all of them to the document body).
    /// </summary>
    public static List<string> ToMultiPageParagraphXml(MDiagram d, ThemeDefinition theme, ref uint docPrId, bool smartConnectors = true, string connectorRouting = "default")
    {
        // Scale width only — ScaleToFit with mode 3 handles this.
        ScaleToFit(d, oversizedMode: 3);

        // Pre-assign IDs for smart connectors
        uint globalId = 1;
        foreach (var s in d.Shapes) s.Id = ++globalId;
        if (smartConnectors) AssignTopologyHeuristics(d);

        int bandCount = Math.Max(1, (int)Math.Ceiling(d.Height / MaxCanvasH));
        double bandH = bandCount == 1 ? d.Height : MaxCanvasH;
        var results = new List<string>(bandCount);

        var ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                 "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
                 "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                 "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
                 "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\"";

        for (int band = 0; band < bandCount; band++)
        {
            double yStart = band * bandH;
            double yEnd = Math.Min(yStart + bandH, d.Height);
            double thisBandH = yEnd - yStart;

            long cx = Emu(Math.Max(1, d.Width)), cy = Emu(Math.Max(1, thisBandH));
            var sb = new StringBuilder();
            uint id = 1;

            sb.Append($"<w:r {ns}><w:drawing>");
            sb.Append($"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">");
            sb.Append($"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>");
            sb.Append($"<wp:docPr id=\"{docPrId}\" name=\"ShapeForge diagram {docPrId} band {band + 1}\" descr=\"Mermaid diagram band {band + 1} of {bandCount}\"/>");
            sb.Append("<wp:cNvGraphicFramePr/>");
            sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\">");
            sb.Append("<wpg:wgp>");
            sb.Append($"<wpg:cNvGrpSpPr/>");
            sb.Append($"<wpg:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/>" +
                      $"<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:noFill/></wpg:grpSpPr>");

            // Background card for this band (see ToParagraphXml). Spans the full band height.
            sb.Append(BackgroundXml(d, theme, ++id, bandHeight: thisBandH));

            // Z-order matches ToParagraphXml: subgraph containers first (behind), then edges, then
            // nodes. Emit the containers whose centre falls in this band before any connector.
            foreach (var s in d.Shapes)
            {
                if (s.Kind != ShapeKind.Subgraph) continue;
                double centerY = s.Y + s.H / 2;
                if (centerY < yStart || centerY >= yEnd) continue;
                sb.Append(ShapeXml(ShiftShape(s, yStart), theme, s.Id));
            }

            // Emit connectors that overlap this band (at least one endpoint within the band,
            // or the connector spans across it).  Y coordinates are offset by -yStart.
            foreach (var c in d.Connectors)
            {
                if (!ConnectorOverlapsBand(c, yStart, yEnd)) continue;
                // A smart connector must link to a shape in the SAME DrawingML canvas.
                // If the source or target shape is outside this page's band, break the link.
                if (smartConnectors)
                {
                    if (c.FromShapeId.HasValue && !ShapeInBand(d, c.FromShapeId.Value, yStart, yEnd))
                        c.FromShapeId = null;
                    if (c.ToShapeId.HasValue && !ShapeInBand(d, c.ToShapeId.Value, yStart, yEnd))
                        c.ToShapeId = null;
                }

                var shifted = ShiftConnector(c, yStart, thisBandH);
                sb.Append(ConnectorXml(shifted, theme, ++id, smartConnectors, connectorRouting));
                if (!string.IsNullOrWhiteSpace(c.Label) &&
                    c.LabelY + c.LabelH > yStart && c.LabelY < yEnd)
                {
                    id++;
                    sb.Append(ShapeXml(new MShape
                    {
                        Kind = ShapeKind.Text,
                        X = c.LabelX, Y = Math.Max(0, c.LabelY - yStart),
                        W = c.LabelW, H = c.LabelH,
                        Text = c.Label, FontSize = 8.5, TextColor = null,
                    }, theme, id));
                }
            }

            // Emit the remaining (non-subgraph) shapes whose vertical center falls within this band.
            foreach (var s in d.Shapes)
            {
                if (s.Kind == ShapeKind.Subgraph) continue;
                double centerY = s.Y + s.H / 2;
                if (centerY < yStart || centerY >= yEnd) continue;
                id++;
                sb.Append(ShapeXml(ShiftShape(s, yStart), theme, s.Id));
            }

            sb.Append("</wpg:wgp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>");
            results.Add(sb.ToString());
            docPrId++;
        }

        return results;
    }

    private static bool ShapeInBand(MDiagram d, uint shapeId, double yStart, double yEnd)
    {
        var s = d.Shapes.FirstOrDefault(x => x.Id == shapeId);
        if (s == null) return false;
        double centerY = s.Y + s.H / 2;
        return centerY >= yStart && centerY < yEnd;
    }

    // Copy of a shape shifted into a band's local vertical space (Y offset by -yStart).
    private static MShape ShiftShape(MShape s, double yStart) => new()
    {
        Kind = s.Kind, X = s.X, Y = s.Y - yStart, W = s.W, H = s.H,
        Text = s.Text, FontSize = s.FontSize, Bold = s.Bold,
        Fill = s.Fill, Stroke = s.Stroke, StrokeWidth = s.StrokeWidth,
        Dashed = s.Dashed, TextColor = s.TextColor,
        AdjStartDeg = s.AdjStartDeg, AdjEndDeg = s.AdjEndDeg,
    };

    private static void AssignTopologyHeuristics(MDiagram d)
    {
        foreach (var c in d.Connectors)
        {
            if (c.FromShapeId.HasValue && c.ToShapeId.HasValue) continue;

            // Anchor from the curve's endpoints when a sampled path exists, otherwise fall back
            // to the straight-line endpoints. Either way the endpoints sit on the shape borders,
            // so this (the original layout) is the right moment to capture which node each end
            // belongs to — after a shrink the coordinates no longer line up.
            double sx, sy, ex, ey;
            if (c.Points is { Count: >= 2 })
            {
                sx = c.Points[0].X; sy = c.Points[0].Y;
                ex = c.Points[^1].X; ey = c.Points[^1].Y;
            }
            else
            {
                sx = c.X1; sy = c.Y1;
                ex = c.X2; ey = c.Y2;
            }

            c.FromShapeId = FindClosestShape(d, sx, sy, out int fromSite);
            if (c.FromShapeId.HasValue) c.FromConnectionSite = fromSite;

            c.ToShapeId = FindClosestShape(d, ex, ey, out int toSite);
            if (c.ToShapeId.HasValue) c.ToConnectionSite = toSite;
        }
    }

    private static uint? FindClosestShape(MDiagram d, double x, double y, out int site)
    {
        site = 0;
        uint? bestId = null;
        double bestDist = 20; // 20pt max tolerance for topological snapping

        foreach (var s in d.Shapes)
        {
            if (s.Kind == ShapeKind.Text) continue;
            if (s.NodeId is not null) continue; // decorative (e.g. the background card) — never anchor to it

            double cx = s.X + s.W / 2;
            double cy = s.Y + s.H / 2;

            double dTop = Math.Abs(x - cx) + Math.Abs(y - s.Y);
            double dBottom = Math.Abs(x - cx) + Math.Abs(y - (s.Y + s.H));
            double dLeft = Math.Abs(x - s.X) + Math.Abs(y - cy);
            double dRight = Math.Abs(x - (s.X + s.W)) + Math.Abs(y - cy);

            double minLocal = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
            if (minLocal < bestDist)
            {
                bestDist = minLocal;
                bestId = s.Id;
                if (minLocal == dTop) site = 0;
                else if (minLocal == dLeft) site = 1;
                else if (minLocal == dBottom) site = 2;
                else if (minLocal == dRight) site = 3;
            }
        }
        return bestId;
    }

    // After an aggressive shrink (modes 6/7/8) the nodes have moved and the sampled curves are
    // gone; put each anchored edge's endpoints back onto its shapes' connection sites so the
    // stored geometry touches the nodes. Word's stCxn/endCxn glue then keeps them attached.
    private static void ReanchorConnectorsToShapes(MDiagram d)
    {
        foreach (var c in d.Connectors)
        {
            if (!c.FromShapeId.HasValue || !c.ToShapeId.HasValue) continue;
            if (c.FromShapeId.Value == c.ToShapeId.Value) continue; // self-loop: leave as-is

            var from = d.Shapes.FirstOrDefault(s => s.Id == c.FromShapeId.Value);
            var to = d.Shapes.FirstOrDefault(s => s.Id == c.ToShapeId.Value);
            if (from is null || to is null) continue;

            (c.X1, c.Y1) = ConnectionSitePoint(from, c.FromConnectionSite);
            (c.X2, c.Y2) = ConnectionSitePoint(to, c.ToConnectionSite);

            // Keep the label centred on the re-routed line.
            if (!string.IsNullOrWhiteSpace(c.Label))
            {
                c.LabelX = (c.X1 + c.X2) / 2 - c.LabelW / 2;
                c.LabelY = (c.Y1 + c.Y2) / 2 - c.LabelH / 2;
            }
        }
    }

    // Connection-site index convention matches FindClosestShape: 0=top, 1=left, 2=bottom, 3=right.
    private static (double X, double Y) ConnectionSitePoint(MShape s, int site) => site switch
    {
        0 => (s.X + s.W / 2, s.Y),
        1 => (s.X, s.Y + s.H / 2),
        2 => (s.X + s.W / 2, s.Y + s.H),
        3 => (s.X + s.W, s.Y + s.H / 2),
        _ => (s.X + s.W / 2, s.Y + s.H / 2),
    };

    // Does any part of the connector fall within the vertical band [yStart, yEnd)?
    private static bool ConnectorOverlapsBand(MConnector c, double yStart, double yEnd)
    {
        if (c.Points is { Count: >= 2 })
        {
            double minY = c.Points.Min(p => p.Y), maxY = c.Points.Max(p => p.Y);
            return maxY > yStart && minY < yEnd;
        }
        double lo = Math.Min(c.Y1, c.Y2), hi = Math.Max(c.Y1, c.Y2);
        return hi > yStart && lo < yEnd;
    }

    // Create a copy of the connector with Y coordinates shifted into the band's local space
    // and clamped to [0, bandH].
    private static MConnector ShiftConnector(MConnector c, double yStart, double bandH)
    {
        var shifted = new MConnector
        {
            X1 = c.X1, Y1 = Math.Clamp(c.Y1 - yStart, 0, bandH),
            X2 = c.X2, Y2 = Math.Clamp(c.Y2 - yStart, 0, bandH),
            Elbow = c.Elbow, Dashed = c.Dashed, Stroke = c.Stroke,
            StrokeWidth = c.StrokeWidth, Label = c.Label,
            LabelX = c.LabelX, LabelY = Math.Clamp(c.LabelY - yStart, 0, bandH),
            LabelW = c.LabelW, LabelH = c.LabelH,
            StartHead = c.StartHead, EndHead = c.EndHead,
            FromShapeId = c.FromShapeId, FromConnectionSite = c.FromConnectionSite,
            ToShapeId = c.ToShapeId, ToConnectionSite = c.ToConnectionSite,
        };
        if (c.Points is { Count: >= 2 })
            shifted.Points = c.Points
                .Select(p => (p.X, Y: Math.Clamp(p.Y - yStart, 0, bandH)))
                .ToList();
        return shifted;
    }

    // ---- shapes -------------------------------------------------------------------------------

    // Solid full-bleed rectangle the group paints behind every connector/shape. Uses the diagram
    // theme's (light) background so a dark document page never bleeds through the diagram. Named
    // WITHOUT the ms:node=/ms:edge= round-trip tag so DocxShapeParser never recovers it as a real
    // node (its fallback path also skips full-bleed text-less rectangles — see DocxShapeParser).
    private static string BackgroundXml(MDiagram d, ThemeDefinition t, uint id, double? bandHeight = null)
    {
        long w = Emu(Math.Max(1, d.Width));
        long h = Emu(Math.Max(1, bandHeight ?? d.Height));
        var fill = Hex(t.Background) ?? "FFFFFF";
        return "<wps:wsp>" +
               $"<wps:cNvPr id=\"{id}\" name=\"Diagram background {id}\"/>" +
               "<wps:cNvSpPr/>" +
               "<wps:spPr>" +
               $"<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm>" +
               "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
               $"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill>" +
               "<a:ln><a:noFill/></a:ln>" +
               "</wps:spPr>" +
               "<wps:bodyPr/>" +
               "</wps:wsp>";
    }

    private static string ShapeXml(MShape s, ThemeDefinition t, uint id)
    {
        var (prst, avLst) = Preset(s);
        string fill = FillXml(s, t);
        string line = LineXml(s, t);
        long x = Emu(s.X), y = Emu(s.Y), w = Emu(Math.Max(0.5, s.W)), h = Emu(Math.Max(0.5, s.H));

        var sb = new StringBuilder();
        sb.Append("<wps:wsp>");
        // Reverse-import tagging: when a renderer supplied a semantic node id, write a structured
        // ms:node=<id>;kind=<ShapeKind> name so DocxShapeParser can recover the node losslessly.
        // Otherwise keep the human-friendly "<Kind> <id>" name (no round-trip identity available).
        string shapeName = s.NodeId is not null
            ? $"ms:node={s.NodeId};kind={s.Kind}"
            : $"{ShapeName(s)} {id}";
        sb.Append($"<wps:cNvPr id=\"{id}\" name=\"{Esc(shapeName)}\"/>");
        sb.Append("<wps:cNvSpPr/>");
        sb.Append("<wps:spPr>");
        sb.Append($"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm>");
        sb.Append($"<a:prstGeom prst=\"{prst}\">{avLst}</a:prstGeom>");
        sb.Append(fill);
        sb.Append(line);
        sb.Append("</wps:spPr>");

        if (!string.IsNullOrEmpty(s.Text))
        {
            sb.Append("<wps:txbx><w:txbxContent>");
            var lines = s.Text.Replace("\r", "").Split('\n');
            foreach (var lineText in lines)
            {
                sb.Append("<w:p><w:pPr><w:suppressAutoHyphens/><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"216\" w:lineRule=\"auto\"/><w:jc w:val=\"center\"/><w:rPr>");
                sb.Append(RunProps(s, t));
                sb.Append("</w:rPr></w:pPr>");
                sb.Append($"<w:r><w:rPr>{RunProps(s, t)}</w:rPr><w:t xml:space=\"preserve\">{Esc(lineText)}</w:t></w:r>");
                sb.Append("</w:p>");
            }
            sb.Append("</w:txbxContent></wps:txbx>");
        }
        // bodyPr is required (last child). Tight insets; vertical centering (top for subgraphs).
        var anchor = s.Kind == ShapeKind.Subgraph ? "t" : "ctr";
        sb.Append($"<wps:bodyPr rot=\"0\" wrap=\"square\" lIns=\"12700\" tIns=\"6350\" rIns=\"12700\" bIns=\"6350\" anchor=\"{anchor}\" anchorCtr=\"0\"><a:noAutofit/></wps:bodyPr>");
        sb.Append("</wps:wsp>");
        return sb.ToString();
    }

    private static string RunProps(MShape s, ThemeDefinition t)
    {
        var shapeBg = Hex(s.Fill) ?? Hex(t.Background) ?? "FFFFFF";
        var requestedColor = Hex(s.TextColor) ?? Hex(t.Primary) ?? "000000";
        var color = ContrastGuard.EnsureLegibleText(requestedColor, shapeBg, Hex(t.Primary));
        var sz = (int)Math.Round(s.FontSize * 2); // half-points

        var themeAttr = string.Equals(color, "000000", StringComparison.OrdinalIgnoreCase) || string.Equals(color, "121212", StringComparison.OrdinalIgnoreCase) || string.Equals(color, "1F2328", StringComparison.OrdinalIgnoreCase)
            ? " w:themeColor=\"text1\""
            : string.Equals(color, "FFFFFF", StringComparison.OrdinalIgnoreCase) || string.Equals(color, "E6EDF3", StringComparison.OrdinalIgnoreCase)
            ? " w:themeColor=\"background1\""
            : "";

        // CT_RPr enforces a strict child order: rFonts precedes b, which precedes color, which
        // precedes sz/szCs — emitting them out of sequence fails OpenXml validation.
        return $"<w:rFonts w:ascii=\"{DiagramFont}\" w:hAnsi=\"{DiagramFont}\" w:cs=\"{DiagramFont}\"/>" +
               (s.Bold ? "<w:b/>" : "") +
               $"<w:color w:val=\"{color}\"{themeAttr}/><w:sz w:val=\"{sz}\"/><w:szCs w:val=\"{sz}\"/>";
    }

    private static string ShapeName(MShape s) => s.Kind switch
    {
        ShapeKind.Text => "Label",
        ShapeKind.Frame => "Frame",
        ShapeKind.Subgraph => "Subgraph",
        ShapeKind.Pie => "Pie wedge",
        _ => s.Kind.ToString(),
    };

    private static (string prst, string avLst) Preset(MShape s)
    {
        switch (s.Kind)
        {
            case ShapeKind.Rect or ShapeKind.Frame or ShapeKind.Subgraph or ShapeKind.Text: return ("rect", "<a:avLst/>");
            case ShapeKind.RoundRect: return ("roundRect", "<a:avLst><a:gd name=\"adj\" fmla=\"val 16667\"/></a:avLst>");
            case ShapeKind.Diamond: return ("diamond", "<a:avLst/>");
            case ShapeKind.Ellipse or ShapeKind.Circle: return ("ellipse", "<a:avLst/>");
            case ShapeKind.Parallelogram: return ("parallelogram", "<a:avLst/>");
            case ShapeKind.Trapezoid: return ("trapezoid", "<a:avLst/>");
            case ShapeKind.Cylinder: return ("can", "<a:avLst/>");
            case ShapeKind.Hexagon: return ("hexagon", "<a:avLst/>");
            case ShapeKind.Pie:
            {
                // DrawingML pie: adj1/adj2 in 60000ths of a degree, 0 = 3 o'clock, clockwise.
                long a1 = (long)Math.Round(Norm(s.AdjStartDeg) * 60000);
                long a2 = (long)Math.Round(Norm(s.AdjEndDeg) * 60000);
                return ("pie", $"<a:avLst><a:gd name=\"adj1\" fmla=\"val {a1}\"/><a:gd name=\"adj2\" fmla=\"val {a2}\"/></a:avLst>");
            }
            default: return ("rect", "<a:avLst/>");
        }
    }

    private static double Norm(double deg) { deg %= 360; if (deg < 0) deg += 360; return deg; }

    private static string FillXml(MShape s, ThemeDefinition t)
    {
        if (s.Kind is ShapeKind.Frame or ShapeKind.Text) return "<a:noFill/>";
        var fill = s.Fill ?? (s.Kind == ShapeKind.Subgraph ? t.Secondary : t.Code);
        return $"<a:solidFill><a:srgbClr val=\"{Hex(fill)}\"/></a:solidFill>";
    }

    private static string LineXml(MShape s, ThemeDefinition t)
    {
        if (s.Kind == ShapeKind.Text) return "<a:ln><a:noFill/></a:ln>";
        var stroke = Hex(s.Stroke ?? t.Border);
        long w = (long)Math.Round(s.StrokeWidth * EmuPerPt);
        var dash = s.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        return $"<a:ln w=\"{w}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}</a:ln>";
    }

    // ---- connectors ---------------------------------------------------------------------------

    // Freeform curved connector: a custGeom path following the harvested points, so a Word edge
    // traces mermaid's exact curve. Emitted as a shape (sp) with no fill and an arrow tail end.
    // Public: the MLShape sketch mode emits curved strokes through this exact path.
    public static string CurveXml(MConnector c, ThemeDefinition t, uint id, bool smartConnectors)
    {
        var pts = c.Points!;
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        long ox = Emu(minX), oy = Emu(minY);
        long w = Math.Max(1, Emu(maxX - minX)), h = Math.Max(1, Emu(maxY - minY));

        var path = new StringBuilder();
        path.Append($"<a:moveTo><a:pt x=\"{Emu(pts[0].X - minX)}\" y=\"{Emu(pts[0].Y - minY)}\"/></a:moveTo>");
        for (int i = 1; i < pts.Count; i++)
            path.Append($"<a:lnTo><a:pt x=\"{Emu(pts[i].X - minX)}\" y=\"{Emu(pts[i].Y - minY)}\"/></a:lnTo>");

        var stroke = Hex(c.Stroke ?? t.Line);
        long lw = (long)Math.Round(c.StrokeWidth * EmuPerPt);
        var dash = c.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        var head = HeadXml("headEnd", c.StartHead);
        var tail = HeadXml("tailEnd", c.EndHead);

        string cxnAttr = "<wps:cNvCnPr/>";
        if (smartConnectors && (c.FromShapeId.HasValue || c.ToShapeId.HasValue))
        {
            var st = c.FromShapeId.HasValue ? $"<a:stCxn id=\"{c.FromShapeId.Value}\" idx=\"{c.FromConnectionSite}\"/>" : "";
            var en = c.ToShapeId.HasValue ? $"<a:endCxn id=\"{c.ToShapeId.Value}\" idx=\"{c.ToConnectionSite}\"/>" : "";
            cxnAttr = $"<wps:cNvCnPr>{st}{en}</wps:cNvCnPr>";
        }

        return
            "<wps:wsp>" +
            $"<wps:cNvPr id=\"{id}\" name=\"{Esc(c.EdgeKey ?? $"Edge {id}")}\"/>" +
            cxnAttr +
            "<wps:spPr>" +
            $"<a:xfrm><a:off x=\"{ox}\" y=\"{oy}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm>" +
            $"<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"{w}\" b=\"{h}\"/>" +
            $"<a:pathLst><a:path w=\"{w}\" h=\"{h}\" fill=\"none\">{path}</a:path></a:pathLst></a:custGeom>" +
            "<a:noFill/>" +
            $"<a:ln w=\"{lw}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}{head}{tail}</a:ln>" +
            "</wps:spPr>" +
            "<wps:bodyPr/>" +
            "</wps:wsp>";
    }

    private static string ConnectorXml(MConnector c, ThemeDefinition t, uint id, bool smartConnectors, string connectorRouting = "default")
    {
        if (c.Points is { Count: >= 2 }) return CurveXml(c, t, ++id, smartConnectors);

        // Degenerate elbows (both endpoints on one axis) can't be drawn by bentConnector3 — a
        // zero-width/height box renders as a straight line. These are loops (a sequence
        // self-message, a same-row relationship): decompose into a 3-segment U shape.
        if (c.Elbow && Math.Abs(c.X2 - c.X1) < 2)   // vertical → U out to the right
        {
            double bulge = 30;
            var s1 = Seg(c, c.X1, c.Y1, c.X1 + bulge, c.Y1, c.StartHead, ArrowHead.None);
            var s2 = Seg(c, c.X1 + bulge, c.Y1, c.X1 + bulge, c.Y2, ArrowHead.None, ArrowHead.None);
            var s3 = Seg(c, c.X1 + bulge, c.Y2, c.X2, c.Y2, ArrowHead.None, c.EndHead);
            return StraightXml(s1, t, ++id) + StraightXml(s2, t, ++id) + StraightXml(s3, t, ++id);
        }
        if (c.Elbow && Math.Abs(c.Y2 - c.Y1) < 2)   // horizontal → U bowing down
        {
            double bulge = 40;
            var s1 = Seg(c, c.X1, c.Y1, c.X1, c.Y1 + bulge, c.StartHead, ArrowHead.None);
            var s2 = Seg(c, c.X1, c.Y1 + bulge, c.X2, c.Y1 + bulge, ArrowHead.None, ArrowHead.None);
            var s3 = Seg(c, c.X2, c.Y1 + bulge, c.X2, c.Y2, ArrowHead.None, c.EndHead);
            return StraightXml(s1, t, ++id) + StraightXml(s2, t, ++id) + StraightXml(s3, t, ++id);
        }

        // A straight/bent connector draws from the top-left to the bottom-right of its box; encode
        // direction with flipH/flipV so any orientation works.
        double x = Math.Min(c.X1, c.X2), y = Math.Min(c.Y1, c.Y2);
        double w = Math.Abs(c.X2 - c.X1), h = Math.Abs(c.Y2 - c.Y1);
        bool flipH = c.X2 < c.X1, flipV = c.Y2 < c.Y1;
        var prst = connectorRouting switch
        {
            "elbow" => "bentConnector3",
            "curved" => "curveConnector3",
            "straight" => "straightConnector1",
            _ => c.Elbow ? "bentConnector3" : "straightConnector1", // "default": use harvested geometry
        };

        var stroke = Hex(c.Stroke ?? t.Line);
        long lw = (long)Math.Round(c.StrokeWidth * EmuPerPt);
        var dash = c.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        var head = HeadXml("headEnd", c.StartHead);
        var tail = HeadXml("tailEnd", c.EndHead);
        var flips = (flipH ? " flipH=\"1\"" : "") + (flipV ? " flipV=\"1\"" : "");

        string cxnAttr = "";
        if (smartConnectors && (c.FromShapeId.HasValue || c.ToShapeId.HasValue))
        {
            var st = c.FromShapeId.HasValue ? $"<a:stCxn id=\"{c.FromShapeId.Value}\" idx=\"{c.FromConnectionSite}\"/>" : "";
            var en = c.ToShapeId.HasValue ? $"<a:endCxn id=\"{c.ToShapeId.Value}\" idx=\"{c.ToConnectionSite}\"/>" : "";
            cxnAttr = st + en;
        }

        return
            "<wps:wsp>" +
            $"<wps:cNvPr id=\"{id}\" name=\"{Esc(c.EdgeKey ?? $"Connector {id}")}\"/>" +
            $"<wps:cNvCnPr>{cxnAttr}</wps:cNvCnPr>" +
            "<wps:spPr>" +
            $"<a:xfrm{flips}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
            $"<a:prstGeom prst=\"{prst}\"><a:avLst/></a:prstGeom>" +
            "<a:noFill/>" +
            $"<a:ln w=\"{lw}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}{head}{tail}</a:ln>" +
            "</wps:spPr>" +
            "<wps:bodyPr/>" +
            "</wps:wsp>";
    }

    private static MConnector Seg(MConnector src, double x1, double y1, double x2, double y2, ArrowHead start, ArrowHead end) =>
        new()
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Elbow = false,
            StartHead = start, EndHead = end,
            Dashed = src.Dashed, Stroke = src.Stroke, StrokeWidth = src.StrokeWidth,
        };

    // A single straight segment (used by the U-loop decomposition). Same emission as the main path
    // but never recurses.
    private static string StraightXml(MConnector c, ThemeDefinition t, uint id)
    {
        double x = Math.Min(c.X1, c.X2), y = Math.Min(c.Y1, c.Y2);
        double w = Math.Abs(c.X2 - c.X1), h = Math.Abs(c.Y2 - c.Y1);
        bool flipH = c.X2 < c.X1, flipV = c.Y2 < c.Y1;
        var stroke = Hex(c.Stroke ?? t.Line);
        long lw = (long)Math.Round(c.StrokeWidth * EmuPerPt);
        var dash = c.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        var flips = (flipH ? " flipH=\"1\"" : "") + (flipV ? " flipV=\"1\"" : "");
        return
            "<wps:wsp>" +
            $"<wps:cNvPr id=\"{id}\" name=\"Connector {id}\"/>" +
            "<wps:cNvCnPr/>" +
            "<wps:spPr>" +
            $"<a:xfrm{flips}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
            "<a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>" +
            "<a:noFill/>" +
            $"<a:ln w=\"{lw}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill>{dash}{HeadXml("headEnd", c.StartHead)}{HeadXml("tailEnd", c.EndHead)}</a:ln>" +
            "</wps:spPr>" +
            "<wps:bodyPr/>" +
            "</wps:wsp>";
    }

    private static string HeadXml(string el, ArrowHead h) => h switch
    {
        ArrowHead.None => $"<a:{el} type=\"none\"/>",
        ArrowHead.Triangle => $"<a:{el} type=\"triangle\" w=\"med\" len=\"med\"/>",
        ArrowHead.Open => $"<a:{el} type=\"arrow\" w=\"med\" len=\"med\"/>",
        ArrowHead.Diamond => $"<a:{el} type=\"diamond\" w=\"med\" len=\"med\"/>",
        ArrowHead.Oval => $"<a:{el} type=\"oval\" w=\"med\" len=\"med\"/>",
        ArrowHead.Stealth => $"<a:{el} type=\"stealth\" w=\"med\" len=\"med\"/>",
        _ => $"<a:{el} type=\"none\"/>",
    };

    // ---- utils --------------------------------------------------------------------------------

    private static long Emu(double pt) => (long)Math.Round(pt * EmuPerPt);

    private static string? Hex(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        var s = css.TrimStart('#');
        return (s.Length >= 6 ? s[..6] : s.PadLeft(6, '0')).ToUpperInvariant();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
