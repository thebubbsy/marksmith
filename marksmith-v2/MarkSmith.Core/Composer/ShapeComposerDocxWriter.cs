using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// Writes a composed shape set (from ImageShapeComposer) into a .docx as ONE native
    /// DrawingML group (wpg:wsp + wps:wsp) — the same structure MarkSmith's Mermaid→DrawingML
    /// renderer uses, which Word opens with every shape individually editable.
    /// </summary>
    public static class ShapeComposerDocxWriter
    {
        private const long Emu = 914400;
        private const string Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private const string Wpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";

        public static void WriteDocx(string outputPath, List<ComposedShape> shapes,
            double canvasWidthInches, double canvasHeightInches, string? themeXml = null) =>
            WritePackage(outputPath, shapes, canvasWidthInches, canvasHeightInches, themeXml, template: false);

        /// <summary>Writes the same composition as a Word TEMPLATE (.dotx) — identical native
        /// DrawingML, but the package is a template (content type + extension) so Word opens it
        /// as a template for reuse.</summary>
        public static void WriteDotx(string outputPath, List<ComposedShape> shapes,
            double canvasWidthInches, double canvasHeightInches, string? themeXml = null) =>
            WritePackage(outputPath, shapes, canvasWidthInches, canvasHeightInches, themeXml, template: true);

        private static void WritePackage(string outputPath, List<ComposedShape> shapes,
            double canvasWidthInches, double canvasHeightInches, string? themeXml, bool template)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);

            string inline = BuildInlineXml(shapes, canvasWidthInches, canvasHeightInches);
            string mainContentType = template
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

            string docXml =
                @"<?xml version=""1.0"" encoding=""utf-8""?>" +
                @"<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">" +
                @"<w:body>" +
                $@"<w:p><w:r><w:drawing>{inline}</w:drawing></w:r></w:p>" +
                @"<w:sectPr><w:pgSz w:w=""12240"" w:h=""15840""/></w:sectPr>" +
                @"</w:body>" +
                @"</w:document>";

            bool hasTheme = !string.IsNullOrWhiteSpace(themeXml);
            string themeOverride = hasTheme
                ? "  <Override PartName=\"/word/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>\n"
                : "";
            string contentTypes =
                @"<?xml version=""1.0"" encoding=""utf-8""?>" +
                @"<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" +
                @"<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>" +
                @"<Default Extension=""xml"" ContentType=""application/xml""/>" +
                $@"<Override PartName=""/word/document.xml"" ContentType=""{mainContentType}""/>" +
                themeOverride +
                @"</Types>";

            string themeRel = hasTheme
                ? "  <Relationship Id=\"rIdTheme1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>\n"
                : "";
            string rels =
                @"<?xml version=""1.0"" encoding=""utf-8""?>" +
                @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" +
                @"<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>" +
                themeRel +
                @"</Relationships>";

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
            void Write(string name, string content)
            {
                var entry = archive.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }

            Write("[Content_Types].xml", contentTypes);
            Write("_rels/.rels", rels);
            Write("word/document.xml", docXml);
            if (hasTheme) Write("word/theme/theme1.xml", themeXml!);
        }

        /// <summary>
        /// Builds the wp:inline wps/wpg group XML for a shape composition — reusable by the
        /// DOCX export path (embedded via W.Drawing.InnerXml) and the standalone writer.
        /// </summary>
        public static string BuildInlineXml(List<ComposedShape> shapes,
            double canvasWidthInches, double canvasHeightInches)
        {
            var sb = new StringBuilder();
            int id = 2;
            foreach (var s in shapes)
            {
                sb.Append(ShapeXml(s, (uint)id++));
            }

            long cx = (long)(canvasWidthInches * Emu);
            long cy = (long)(canvasHeightInches * Emu);

            return
                @"<wp:inline distT=""0"" distB=""0"" distL=""0"" distR=""0"" xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"" xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" xmlns:wpg=""" + Wpg + @""" xmlns:wps=""" + Wps + @""" xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">" +
                $@"<wp:extent cx=""{cx}"" cy=""{cy}""/>" +
                @"<wp:effectExtent l=""0"" t=""0"" r=""0"" b=""0""/>" +
                @"<wp:docPr id=""1"" name=""Shape composition"" descr=""Composed from native DrawingML shapes""/>" +
                @"<wp:cNvGraphicFramePr/>" +
                @"<a:graphic>" +
                @"<a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"">" +
                @"<wpg:wgp>" +
                @"<wpg:cNvGrpSpPr/>" +
                @"<wpg:grpSpPr>" +
                $@"<a:xfrm><a:off x=""0"" y=""0""/><a:ext cx=""{cx}"" cy=""{cy}""/><a:chOff x=""0"" y=""0""/><a:chExt cx=""{cx}"" cy=""{cy}""/></a:xfrm>" +
                @"</wpg:grpSpPr>" +
                sb.ToString() +
                @"</wpg:wgp>" +
                @"</a:graphicData>" +
                @"</a:graphic>" +
                @"</wp:inline>";
        }

        private static string ShapeXml(ComposedShape s, uint id)
        {
            long x = (long)(s.X * Emu), y = (long)(s.Y * Emu);
            long w = (long)(s.W * Emu), h = (long)(s.H * Emu);
            // Curved sketch strokes: emitted EXACTLY like the engine's old curve tracer
            // (DocxShapeEmitter.CurveXml — the v1.2 "exact-layout edges trace mermaid's real
            // curves" mechanism): an MConnector with harvested points, custGeom fill="none"
            // polyline + noFill + thick ln. This is the Word-proven path.
            if (s.PathPoints is { Count: >= 2 })
            {
                var conn = new MarkSmith.Services.Mermaid.MConnector
                {
                    Stroke = "#" + s.Fill,
                    StrokeWidth = s.StrokeWidthPt,
                    StartHead = MarkSmith.Services.Mermaid.ArrowHead.None,
                    EndHead = MarkSmith.Services.Mermaid.ArrowHead.None,
                    Points = s.PathPoints
                        .Select(p => ((s.X + p.X / 100.0 * s.W) * 72.0, (s.Y + p.Y / 100.0 * s.H) * 72.0))
                        .ToList()
                };
                return MarkSmith.Services.Mermaid.DocxShapeEmitter.CurveXml(
                    conn, new MarkSmith.Models.ThemeDefinition(
                        "Sketch", "#FFFFFF", "#111111", "#111111", "#F5F5F5", "#DDDDDD",
                        "#0078D4", "#005A9E", "#E0E0E0"), id, smartConnectors: false);
            }

            string prst = s.Prst switch
            {
                "roundrect" => "roundRect",
                "ellipse" or "circle" => "ellipse",
                "circulararrow" => "circularArrow",
                "smileyface" => "smileyFace",
                _ => s.Prst
            };

            string textXml = "";
            if (!string.IsNullOrWhiteSpace(s.Text) && s.PathPoints is not { Count: >= 2 })
            {
                // CONTRAST RULE for font on top of a shape: the label colour is guarded against
                // the SHAPE'S FILL (not the page) so text can never land on a similar-coloured
                // fill — mirrors DocxShapeEmitter.RunProps (Mermaid nodes). An explicitly supplied
                // tcolor is honoured only when it already passes WCAG 4.5:1 vs the fill.
                string guarded = MarkSmith.Services.ContrastGuard.EnsureLegibleText(
                    s.TextColor ?? "121212", "#" + s.Fill);
                int sz = Math.Clamp((int)Math.Round(s.H * 72 * 2 * 0.35), 16, 96); // half-points
                string rpr = $"<w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/>" +
                             $"<w:color w:val=\"{guarded}\"/><w:sz w:val=\"{sz}\"/><w:szCs w:val=\"{sz}\"/>";
                textXml =
                    @"<wps:txbx><w:txbxContent><w:p><w:pPr>" +
                    @"<w:suppressAutoHyphens/><w:spacing w:before=""0"" w:after=""0"" w:line=""216"" w:lineRule=""auto""/>" +
                    $@"<w:jc w:val=""center""/><w:rPr>{rpr}</w:rPr></w:pPr>" +
                    $@"<w:r><w:rPr>{rpr}</w:rPr><w:t xml:space=""preserve"">{Esc(s.Text)}</w:t></w:r>" +
                    @"</w:p></w:txbxContent></wps:txbx>";
            }

            return
                @"<wps:wsp>" +
                $@"<wps:cNvPr id=""{id}"" name=""shape {s.Prst} {id}""/>" +
                @"<wps:cNvSpPr/>" +
                @"<wps:spPr>" +
                $@"<a:xfrm rot=""{s.Rot}""><a:off x=""{x}"" y=""{y}""/><a:ext cx=""{w}"" cy=""{h}""/></a:xfrm>" +
                $@"<a:prstGeom prst=""{prst}""><a:avLst/></a:prstGeom>" +
                $@"<a:solidFill><a:srgbClr val=""{s.Fill}""/></a:solidFill>" +
                $@"<a:ln w=""6350""><a:solidFill><a:srgbClr val=""{s.Fill}""/></a:solidFill></a:ln>" +
                @"</wps:spPr>" +
                textXml +
                @"<wps:bodyPr rot=""0"" wrap=""square"" lIns=""12700"" tIns=""6350"" rIns=""12700"" bIns=""6350"" anchor=""ctr"" anchorCtr=""0""><a:noAutofit/></wps:bodyPr>" +
                @"</wps:wsp>";
        }

        private static string Esc(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
