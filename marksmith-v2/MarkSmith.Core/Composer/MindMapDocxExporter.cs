using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models.MindMap;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Wps = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;

namespace MarkSmith.Core.Composer
{
    public sealed class MindMapDocxExporter
    {
        private const long EmuPerInch = 914400L;
        private const long EmuPerPt = 12700L;

        public void ExportToDocx(MindMapDocument doc, string outputFilePath)
        {
            if (File.Exists(outputFilePath)) File.Delete(outputFilePath);

            using var wordDoc = WordprocessingDocument.Create(outputFilePath, WordprocessingDocumentType.Document);
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new W.Document(new W.Body());
            var body = mainPart.Document.Body!;

            // Title paragraph
            var titleP = new W.Paragraph(
                new W.ParagraphProperties(
                    new W.SpacingBetweenLines { After = "240" },
                    new W.Justification { Val = W.JustificationValues.Center }),
                new W.Run(
                    new W.RunProperties(
                        new W.Bold(),
                        new W.Color { Val = "1F2937" },
                        new W.FontSize { Val = "36" }),
                    new W.Text(doc.Title ?? "Document Galaxy Mind Map")));
            body.Append(titleP);

            // Subtitle
            var subP = new W.Paragraph(
                new W.ParagraphProperties(
                    new W.SpacingBetweenLines { After = "360" },
                    new W.Justification { Val = W.JustificationValues.Center }),
                new W.Run(
                    new W.RunProperties(
                        new W.Italic(),
                        new W.Color { Val = "6B7280" },
                        new W.FontSize { Val = "20" }),
                    new W.Text($"Generated via MarkSmith MindMap Engine · {doc.Nodes.Count} Connected Documents & Notes")));
            body.Append(subP);

            // Drawing canvas paragraph containing the mind map vector graphic
            var drawingP = CreateDrawingMlCanvasParagraph(doc);
            body.Append(drawingP);

            // Document details table
            var table = CreateDocumentInventoryTable(doc);
            body.Append(table);

            // Section Properties
            var sectPr = new W.SectionProperties(
                new W.PageSize { Width = 16838, Height = 11906, Orient = W.PageOrientationValues.Landscape }, // Landscape A4
                new W.PageMargin { Top = 1080, Bottom = 1080, Left = 1080, Right = 1080 });
            body.Append(sectPr);

            mainPart.Document.Save();
        }

        private static W.Paragraph CreateDrawingMlCanvasParagraph(MindMapDocument doc)
        {
            var nodes = doc.Nodes;
            if (nodes.Count == 0) return new W.Paragraph();

            // Calculate bounding box in inches
            double minX = nodes.Min(n => n.X);
            double minY = nodes.Min(n => n.Y);
            double maxX = nodes.Max(n => n.X + n.Width);
            double maxY = nodes.Max(n => n.Y + n.Height);

            double widthInches = Math.Max(7.5, (maxX - minX + 80) / 96.0);
            double heightInches = Math.Max(4.5, (maxY - minY + 80) / 96.0);

            // Normalize so min is near margin
            double offsetX = -minX + 40;
            double offsetY = -minY + 40;

            long cx = (long)(widthInches * EmuPerInch);
            long cy = (long)(heightInches * EmuPerInch);

            var graphicData = new A.GraphicData
            {
                Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"
            };

            // Build raw WordprocessingGroup XML for robust compatibility
            var groupXml = new StringBuilder();
            groupXml.Append(@"<wpg:wgp xmlns:wpg=""http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"" xmlns:wps=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"" xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">");
            groupXml.Append(@"<wpg:cNvGrpSpPr/>");
            groupXml.Append($@"<wpg:grpSpPr><a:xfrm><a:off x=""0"" y=""0""/><a:ext cx=""{cx}"" cy=""{cy}""/><a:chOff x=""0"" y=""0""/><a:chExt cx=""{cx}"" cy=""{cy}""/></a:xfrm></wpg:grpSpPr>");

            uint shapeId = 2000;

            // 1. Draw connecting lines between parent & children
            foreach (var node in nodes)
            {
                if (string.IsNullOrEmpty(node.ParentId)) continue;
                var parent = nodes.FirstOrDefault(n => n.Id == node.ParentId);
                if (parent == null) continue;

                double x1 = (parent.X + parent.Width + offsetX) / 96.0 * EmuPerInch;
                double y1 = (parent.Y + (parent.Height / 2.0) + offsetY) / 96.0 * EmuPerInch;
                double x2 = (node.X + offsetX) / 96.0 * EmuPerInch;
                double y2 = (node.Y + (node.Height / 2.0) + offsetY) / 96.0 * EmuPerInch;

                string lineHex = CleanHex(node.ColorHex, "7C4DFF");
                groupXml.Append(BuildConnectorShapeXml(shapeId++, (long)x1, (long)y1, (long)x2, (long)y2, lineHex, false));
            }

            // 2. Draw cross-links (Synapses)
            foreach (var link in doc.Links)
            {
                var src = nodes.FirstOrDefault(n => n.Id == link.SourceNodeId);
                var tgt = nodes.FirstOrDefault(n => n.Id == link.TargetNodeId);
                if (src == null || tgt == null) continue;

                double x1 = (src.X + (src.Width / 2.0) + offsetX) / 96.0 * EmuPerInch;
                double y1 = (src.Y + (src.Height / 2.0) + offsetY) / 96.0 * EmuPerInch;
                double x2 = (tgt.X + (tgt.Width / 2.0) + offsetX) / 96.0 * EmuPerInch;
                double y2 = (tgt.Y + (tgt.Height / 2.0) + offsetY) / 96.0 * EmuPerInch;

                string lineHex = CleanHex(link.ColorHex, "FF7C4D");
                bool isDashed = link.Style == MindMapLinkStyle.Dashed;
                groupXml.Append(BuildConnectorShapeXml(shapeId++, (long)x1, (long)y1, (long)x2, (long)y2, lineHex, isDashed));
            }

            // 3. Draw nodes
            foreach (var node in nodes)
            {
                double nx = (node.X + offsetX) / 96.0 * EmuPerInch;
                double ny = (node.Y + offsetY) / 96.0 * EmuPerInch;
                double nw = node.Width / 96.0 * EmuPerInch;
                double nh = node.Height / 96.0 * EmuPerInch;

                string fillHex = CleanHex(node.ColorHex, "107C41");
                string title = (node.Title ?? "Document").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                string badge = node.FileExtension?.ToUpperInvariant().TrimStart('.') ?? node.NodeType.ToString().ToUpperInvariant();
                string tag = node.Tags.FirstOrDefault() ?? "";

                groupXml.Append(BuildNodeShapeXml(shapeId++, (long)nx, (long)ny, (long)nw, (long)nh, fillHex, title, badge, tag));
            }

            groupXml.Append("</wpg:wgp>");

            graphicData.InnerXml = groupXml.ToString();

            var inline = new Wp.Inline(
                new Wp.Extent { Cx = cx, Cy = cy },
                new Wp.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new Wp.DocProperties { Id = 101U, Name = "Document Galaxy Mind Map" },
                new Wp.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(graphicData))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            };

            return new W.Paragraph(
                new W.ParagraphProperties(
                    new W.SpacingBetweenLines { After = "360" },
                    new W.Justification { Val = W.JustificationValues.Center }),
                new W.Run(new W.Drawing(inline)));
        }

        private static string BuildConnectorShapeXml(uint id, long x1, long y1, long x2, long y2, string hexColor, bool dashed)
        {
            long minX = Math.Min(x1, x2);
            long minY = Math.Min(y1, y2);
            long w = Math.Max(Math.Abs(x2 - x1), 1000L);
            long h = Math.Max(Math.Abs(y2 - y1), 1000L);
            bool flipH = x2 < x1;
            bool flipV = y2 < y1;

            string dashXml = dashed ? @"<a:prstDash val=""dash""/>" : "";

            return $@"<wps:wsp>
                <wps:cNvPr id=""{id}"" name=""Connector {id}""/>
                <wps:cNvSpPr/>
                <wps:spPr>
                    <a:xfrm{(flipH ? @" flipH=""1""" : "")}{(flipV ? @" flipV=""1""" : "")}>
                        <a:off x=""{minX}"" y=""{minY}""/>
                        <a:ext cx=""{w}"" cy=""{h}""/>
                    </a:xfrm>
                    <a:prstGeom prst=""curvedConnector3""><a:avLst/></a:prstGeom>
                    <a:ln w=""25400"">
                        <a:solidFill><a:srgbClr val=""{hexColor}""/></a:solidFill>
                        {dashXml}
                    </a:ln>
                </wps:spPr>
                <wps:bodyPr rot=""0"" wrap=""square"" lIns=""12700"" tIns=""6350"" rIns=""12700"" bIns=""6350"" anchor=""ctr"" anchorCtr=""0""><a:noAutofit/></wps:bodyPr>
            </wps:wsp>";
        }

        private static string BuildNodeShapeXml(uint id, long x, long y, long w, long h, string hexColor, string title, string badge, string tag)
        {
            return $@"<wps:wsp>
                <wps:cNvPr id=""{id}"" name=""Node {id}""/>
                <wps:cNvSpPr/>
                <wps:spPr>
                    <a:xfrm>
                        <a:off x=""{x}"" y=""{y}""/>
                        <a:ext cx=""{w}"" cy=""{h}""/>
                    </a:xfrm>
                    <a:prstGeom prst=""roundRect"">
                        <a:avLst><a:gd name=""adj"" fmla=""val 16667""/></a:avLst>
                    </a:prstGeom>
                    <a:solidFill><a:srgbClr val=""{hexColor}""/></a:solidFill>
                    <a:ln w=""12700""><a:solidFill><a:srgbClr val=""FFFFFF""/></a:solidFill></a:ln>
                </wps:spPr>
                <wps:txbx>
                    <w:txbxContent>
                        <w:p>
                            <w:pPr><w:jc w:val=""center""/></w:pPr>
                            <w:r>
                                <w:rPr>
                                    <w:b/>
                                    <w:color w:val=""FFFFFF""/>
                                    <w:sz w:val=""20""/>
                                </w:rPr>
                                <w:t>{title}</w:t>
                            </w:r>
                        </w:p>
                    </w:txbxContent>
                </wps:txbx>
                <wps:bodyPr rot=""0"" wrap=""square"" lIns=""12700"" tIns=""6350"" rIns=""12700"" bIns=""6350"" anchor=""ctr"" anchorCtr=""0""><a:noAutofit/></wps:bodyPr>
            </wps:wsp>";
        }

        private static W.Table CreateDocumentInventoryTable(MindMapDocument doc)
        {
            var table = new W.Table();
            var tblPr = new W.TableProperties(
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "D1D5DB" },
                    new W.LeftBorder { Val = W.BorderValues.None },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "D1D5DB" },
                    new W.RightBorder { Val = W.BorderValues.None },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E5E7EB" },
                    new W.InsideVerticalBorder { Val = W.BorderValues.None }));
            table.Append(tblPr);

            table.Append(new W.TableGrid(
                new W.GridColumn { Width = "3600" },
                new W.GridColumn { Width = "1600" },
                new W.GridColumn { Width = "1600" },
                new W.GridColumn { Width = "2200" },
                new W.GridColumn { Width = "2200" }));

            // Header Row
            var headerRow = new W.TableRow(
                CreateTableCell("Document / Node Title", true, "1F2937", "F3F4F6"),
                CreateTableCell("Type / Format", true, "1F2937", "F3F4F6"),
                CreateTableCell("Progress", true, "1F2937", "F3F4F6"),
                CreateTableCell("Tags", true, "1F2937", "F3F4F6"),
                CreateTableCell("Connected Cross-Links", true, "1F2937", "F3F4F6"));
            table.Append(headerRow);

            foreach (var node in doc.Nodes)
            {
                int linkCount = doc.Links.Count(l => l.SourceNodeId == node.Id || l.TargetNodeId == node.Id);
                string formatBadge = node.FileExtension?.ToUpperInvariant() ?? node.NodeType.ToString();
                string tags = string.Join(" ", node.Tags);

                var row = new W.TableRow(
                    CreateTableCell(node.Title ?? "Untitled", false, "111827"),
                    CreateTableCell(formatBadge, false, "4B5563"),
                    CreateTableCell($"{node.Progress}%", false, "059669"),
                    CreateTableCell(tags, false, "6B7280"),
                    CreateTableCell($"{linkCount} links", false, "7C4DFF"));
                table.Append(row);
            }

            return table;
        }

        private static W.TableCell CreateTableCell(string text, bool bold, string colorHex, string? bgHex = null)
        {
            var cell = new W.TableCell();
            var tcPr = new W.TableCellProperties();

            if (!string.IsNullOrEmpty(bgHex))
            {
                tcPr.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = bgHex });
            }

            tcPr.Append(new W.TableCellMargin(
                new W.TopMargin { Width = "120", Type = W.TableWidthUnitValues.Dxa },
                new W.LeftMargin { Width = "160", Type = W.TableWidthUnitValues.Dxa },
                new W.BottomMargin { Width = "120", Type = W.TableWidthUnitValues.Dxa },
                new W.RightMargin { Width = "160", Type = W.TableWidthUnitValues.Dxa }));

            cell.Append(tcPr);

            var rPr = new W.RunProperties();
            if (bold) rPr.Append(new W.Bold());
            rPr.Append(new W.Color { Val = colorHex });
            rPr.Append(new W.FontSize { Val = "19" });

            var p = new W.Paragraph(
                new W.ParagraphProperties(new W.SpacingBetweenLines { After = "0" }),
                new W.Run(rPr, new W.Text(text)));
            cell.Append(p);
            return cell;
        }

        private static string CleanHex(string? hex, string fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            string s = hex.Trim().TrimStart('#');
            if (s.Length == 6) return s.ToUpperInvariant();
            if (s.Length == 3) return $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}".ToUpperInvariant();
            return fallback;
        }
    }
}
