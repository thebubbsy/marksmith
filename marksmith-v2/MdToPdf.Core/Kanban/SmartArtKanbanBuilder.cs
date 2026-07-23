using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;
using MdToPdf.Services.Mermaid;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Core.Kanban;

/// <summary>
/// Handles OpenXML SmartArt diagram part generation and shape emitter fallback for Kanban boards.
/// </summary>
public static class SmartArtKanbanBuilder
{
    private static readonly XNamespace dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private static readonly XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>
    /// Builds a Kanban Board in DOCX using OpenXML SmartArt data model parts (R2),
    /// falling back to DrawingML shapes layout via DocxShapeEmitter (R3) on failure or when requested.
    /// </summary>
    public static void BuildKanban(
        KanbanBlock kanban,
        MainDocumentPart mainPart,
        OpenXmlCompositeElement target,
        ThemeDefinition theme,
        ref uint docPrId,
        bool forceFallback = false)
    {
        if (kanban == null || kanban.Columns.Count == 0) return;

        if (!forceFallback)
        {
            try
            {
                BuildSmartArtDiagram(kanban, mainPart, target, ref docPrId);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SmartArt generation failed, falling back to shape emitter: {ex.Message}");
            }
        }

        BuildFallbackShapes(kanban, target, theme, ref docPrId);
    }

    /// <summary>
    /// Generates true SmartArt OPC parts (DiagramDataPart, DiagramLayoutDefinitionPart, DiagramColorsPart, DiagramStylePart)
    /// and injects the &lt;w:drawing&gt; reference into the main document body.
    /// </summary>
    public static void BuildSmartArtDiagram(
        KanbanBlock kanban,
        MainDocumentPart mainPart,
        OpenXmlCompositeElement target,
        ref uint docPrId)
    {
        var dataModelDoc = CreateDataModel(kanban);
        var layoutDoc = CreateLayoutDef();
        var colorsDoc = CreateColorsDef();
        var styleDoc = CreateStyleDef();

        var dmPart = mainPart.AddNewPart<DiagramDataPart>();
        using (var s = dmPart.GetStream(FileMode.Create, FileAccess.Write))
            dataModelDoc.Save(s);

        var loPart = mainPart.AddNewPart<DiagramLayoutDefinitionPart>();
        using (var s = loPart.GetStream(FileMode.Create, FileAccess.Write))
            layoutDoc.Save(s);

        var csPart = mainPart.AddNewPart<DiagramColorsPart>();
        using (var s = csPart.GetStream(FileMode.Create, FileAccess.Write))
            colorsDoc.Save(s);

        var qsPart = mainPart.AddNewPart<DiagramStylePart>();
        using (var s = qsPart.GetStream(FileMode.Create, FileAccess.Write))
            styleDoc.Save(s);

        string rDm = mainPart.GetIdOfPart(dmPart);
        string rLo = mainPart.GetIdOfPart(loPart);
        string rCs = mainPart.GetIdOfPart(csPart);
        string rQs = mainPart.GetIdOfPart(qsPart);

        docPrId++;
        var drawingXml = $@"<w:pPr><w:jc w:val=""center""/></w:pPr>
<w:r>
  <w:drawing>
    <wp:inline distT=""0"" distB=""0"" distL=""0"" distR=""0"" xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"">
      <wp:extent cx=""5760000"" cy=""3600000""/>
      <wp:effectExtent l=""0"" t=""0"" r=""0"" b=""0""/>
      <wp:docPr id=""{docPrId}"" name=""Kanban SmartArt Diagram {docPrId}"" descr=""Native Word SmartArt Kanban Board""/>
      <wp:cNvGraphicFramePr/>
      <a:graphic xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"">
        <a:graphicData uri=""http://schemas.openxmlformats.org/drawingml/2006/diagram"">
          <dgm:relIds xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram""
                      xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""
                      r:dm=""{rDm}"" r:lo=""{rLo}"" r:qs=""{rQs}"" r:cs=""{rCs}""/>
        </a:graphicData>
      </a:graphic>
    </wp:inline>
  </w:drawing>
</w:r>";

        var para = new W.Paragraph { InnerXml = drawingXml };
        target.Append(para);
    }

    /// <summary>
    /// Constructs the &lt;dgm:dataModel&gt; XML structure containing Level 1 (Column) and Level 2 (Card) nodes.
    /// </summary>
    public static XDocument CreateDataModel(KanbanBlock kanban)
    {
        var ptLst = new XElement(dgm + "ptLst",
            new XElement(dgm + "pt",
                new XAttribute("modelId", "1"),
                new XAttribute("type", "doc"),
                new XElement(dgm + "prSet"),
                new XElement(dgm + "spPr"),
                new XElement(dgm + "t",
                    new XElement(a + "bodyPr"),
                    new XElement(a + "lstStyle"),
                    new XElement(a + "p",
                        new XElement(a + "r",
                            new XElement(a + "t", "")
                        )
                    )
                )
            )
        );

        var cxnLst = new XElement(dgm + "cxnLst");

        int pointIdCounter = 2;
        int cxnIdCounter = 1000;

        for (int i = 0; i < kanban.Columns.Count; i++)
        {
            var col = kanban.Columns[i];
            int colPtId = pointIdCounter++;

            ptLst.Add(new XElement(dgm + "pt",
                new XAttribute("modelId", colPtId.ToString()),
                new XAttribute("type", "node"),
                new XElement(dgm + "prSet"),
                new XElement(dgm + "spPr"),
                new XElement(dgm + "t",
                    new XElement(a + "bodyPr"),
                    new XElement(a + "lstStyle"),
                    new XElement(a + "p",
                        new XElement(a + "r",
                            new XElement(a + "rPr", new XAttribute("lang", "en-US")),
                            new XElement(a + "t", col.Title ?? string.Empty)
                        )
                    )
                )
            ));

            cxnLst.Add(new XElement(dgm + "cxn",
                new XAttribute("modelId", (cxnIdCounter++).ToString()),
                new XAttribute("type", "parOf"),
                new XAttribute("srcId", "1"),
                new XAttribute("destId", colPtId.ToString()),
                new XAttribute("srcOrd", "0"),
                new XAttribute("destOrd", i.ToString())
            ));

            for (int j = 0; j < col.Cards.Count; j++)
            {
                var card = col.Cards[j];
                int cardPtId = pointIdCounter++;

                string prefix = card.IsCompleted == true ? "☑ " : card.IsCompleted == false ? "☐ " : "";
                string text = prefix + (card.Text ?? string.Empty);

                ptLst.Add(new XElement(dgm + "pt",
                    new XAttribute("modelId", cardPtId.ToString()),
                    new XAttribute("type", "node"),
                    new XElement(dgm + "prSet"),
                    new XElement(dgm + "spPr"),
                    new XElement(dgm + "t",
                        new XElement(a + "bodyPr"),
                        new XElement(a + "lstStyle"),
                        new XElement(a + "p",
                            new XElement(a + "r",
                                new XElement(a + "rPr", new XAttribute("lang", "en-US")),
                                new XElement(a + "t", text)
                            )
                        )
                    )
                ));

                cxnLst.Add(new XElement(dgm + "cxn",
                    new XAttribute("modelId", (cxnIdCounter++).ToString()),
                    new XAttribute("type", "parOf"),
                    new XAttribute("srcId", colPtId.ToString()),
                    new XAttribute("destId", cardPtId.ToString()),
                    new XAttribute("srcOrd", j.ToString()),
                    new XAttribute("destOrd", "0")
                ));
            }
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(dgm + "dataModel",
                new XAttribute(XNamespace.Xmlns + "dgm", dgm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                ptLst,
                cxnLst,
                new XElement(dgm + "bg"),
                new XElement(dgm + "whole")
            )
        );
    }

    /// <summary>
    /// Creates the &lt;dgm:layoutDef&gt; XML structure.
    /// </summary>
    public static XDocument CreateLayoutDef()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(dgm + "layoutDef",
                new XAttribute(XNamespace.Xmlns + "dgm", dgm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                new XAttribute("uniqueId", "urn:schemas-microsoft-com:office:office:hList"),
                new XElement(dgm + "title", new XAttribute("val", "Horizontal List")),
                new XElement(dgm + "desc", new XAttribute("val", "Kanban Board Horizontal List")),
                new XElement(dgm + "catLst",
                    new XElement(dgm + "cat", new XAttribute("type", "list"), new XAttribute("pri", "1000"))
                ),
                new XElement(dgm + "sampData",
                    new XElement(dgm + "dataModel",
                        new XElement(dgm + "ptLst",
                            new XElement(dgm + "pt", new XAttribute("modelId", "0"), new XAttribute("type", "doc"))
                        ),
                        new XElement(dgm + "cxnLst"),
                        new XElement(dgm + "bg"),
                        new XElement(dgm + "whole")
                    )
                ),
                new XElement(dgm + "styleData"),
                new XElement(dgm + "clrData"),
                new XElement(dgm + "layoutNode",
                    new XAttribute("name", "layout"),
                    new XElement(dgm + "varLst"),
                    new XElement(dgm + "alg", new XAttribute("type", "lin"))
                )
            )
        );
    }

    /// <summary>
    /// Creates the &lt;dgm:colorsDef&gt; XML structure.
    /// </summary>
    public static XDocument CreateColorsDef()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(dgm + "colorsDef",
                new XAttribute(XNamespace.Xmlns + "dgm", dgm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                new XAttribute("uniqueId", "urn:schemas-microsoft-com:office:office:colors/accent1_2"),
                new XElement(dgm + "title", new XAttribute("val", "Accent 1")),
                new XElement(dgm + "desc", new XAttribute("val", "")),
                new XElement(dgm + "catLst",
                    new XElement(dgm + "cat", new XAttribute("type", "accent"), new XAttribute("pri", "1000"))
                ),
                new XElement(dgm + "styleLbl",
                    new XAttribute("name", "node0"),
                    new XElement(dgm + "fillClrLst", new XElement(a + "schemeClr", new XAttribute("val", "accent1"))),
                    new XElement(dgm + "linClrLst", new XElement(a + "schemeClr", new XAttribute("val", "accent1"))),
                    new XElement(dgm + "effectClrLst"),
                    new XElement(dgm + "txLinClrLst"),
                    new XElement(dgm + "txFillClrLst", new XElement(a + "schemeClr", new XAttribute("val", "dk1"))),
                    new XElement(dgm + "txEffectClrLst")
                )
            )
        );
    }

    /// <summary>
    /// Creates the &lt;dgm:styleDef&gt; XML structure.
    /// </summary>
    public static XDocument CreateStyleDef()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(dgm + "styleDef",
                new XAttribute(XNamespace.Xmlns + "dgm", dgm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                new XAttribute("uniqueId", "urn:schemas-microsoft-com:office:office:style/subtle3D"),
                new XElement(dgm + "title", new XAttribute("val", "Subtle")),
                new XElement(dgm + "desc", new XAttribute("val", "")),
                new XElement(dgm + "catLst",
                    new XElement(dgm + "cat", new XAttribute("type", "3D"), new XAttribute("pri", "1000"))
                ),
                new XElement(dgm + "styleLbl",
                    new XAttribute("name", "node0"),
                    new XElement(dgm + "scene3d",
                        new XElement(a + "camera", new XAttribute("prst", "orthographicFront")),
                        new XElement(a + "lightRig", new XAttribute("rig", "threePt"), new XAttribute("dir", "t"))
                    ),
                    new XElement(dgm + "sp3d"),
                    new XElement(dgm + "txPr"),
                    new XElement(dgm + "style",
                        new XElement(a + "lnRef", new XAttribute("idx", "1"), new XElement(a + "schemeClr", new XAttribute("val", "accent1"))),
                        new XElement(a + "fillRef", new XAttribute("idx", "1"), new XElement(a + "schemeClr", new XAttribute("val", "accent1"))),
                        new XElement(a + "effectRef", new XAttribute("idx", "0"), new XElement(a + "schemeClr", new XAttribute("val", "accent1"))),
                        new XElement(a + "fontRef", new XAttribute("idx", "minor"), new XElement(a + "schemeClr", new XAttribute("val", "tx1")))
                    )
                )
            )
        );
    }

    /// <summary>
    /// Renders fallback DrawingML shapes and text boxes for Kanban board columns and cards using DocxShapeEmitter.
    /// </summary>
    public static void BuildFallbackShapes(
        KanbanBlock kanban,
        OpenXmlCompositeElement target,
        ThemeDefinition theme,
        ref uint docPrId)
    {
        var d = BuildKanbanDiagram(kanban, theme);
        var paragraphXml = DocxShapeEmitter.ToParagraphXml(d, theme, ++docPrId, out _);
        var fallbackPara = new W.Paragraph { InnerXml = paragraphXml };
        target.Append(fallbackPara);
    }

    /// <summary>
    /// Builds an MDiagram object for Kanban board layout to be rendered via DocxShapeEmitter.
    /// </summary>
    public static MDiagram BuildKanbanDiagram(KanbanBlock kanban, ThemeDefinition theme)
    {
        var d = new MDiagram();
        int colCount = kanban.Columns.Count;
        if (colCount == 0) return d;

        double canvasW = 460;
        double gap = 10;
        double colW = (canvasW - (gap * (colCount - 1))) / colCount;
        double maxContentH = 0;

        for (int i = 0; i < colCount; i++)
        {
            var col = kanban.Columns[i];
            double colX = i * (colW + gap);
            double curY = 0;

            var headerShape = new MShape
            {
                Id = (uint)(i + 1),
                Kind = ShapeKind.Rect,
                X = colX,
                Y = curY,
                W = colW,
                H = 30,
                Text = col.Title ?? string.Empty,
                Fill = theme?.Primary ?? "#3B82F6",
                Stroke = theme?.Line ?? "#1E293B",
                TextColor = "#FFFFFF",
                Bold = true,
                FontSize = 10,
            };
            d.Shapes.Add(headerShape);
            curY += 35;

            for (int j = 0; j < col.Cards.Count; j++)
            {
                var card = col.Cards[j];
                string prefix = card.IsCompleted == true ? "☑ " : card.IsCompleted == false ? "☐ " : "";
                string cardText = prefix + (card.Text ?? string.Empty);

                double cardH = Math.Max(35, 20 + (cardText.Split('\n').Length * 12));

                var cardShape = new MShape
                {
                    Id = (uint)(100 + i * 50 + j),
                    Kind = ShapeKind.Rect,
                    X = colX,
                    Y = curY,
                    W = colW,
                    H = cardH,
                    Text = cardText,
                    Fill = theme?.Secondary ?? "#F8FAFC",
                    Stroke = theme?.Border ?? "#CBD5E1",
                    TextColor = theme?.Text ?? "#0F172A",
                    FontSize = 9,
                };
                d.Shapes.Add(cardShape);
                curY += cardH + 6;
            }

            if (curY > maxContentH) maxContentH = curY;
        }

        d.Width = canvasW;
        d.Height = Math.Max(100, maxContentH + 10);
        return d;
    }
}
