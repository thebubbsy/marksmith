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
            double canvasWidthInches, double canvasHeightInches, string? themeXml = null)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);

            var sb = new StringBuilder();
            int id = 2;
            foreach (var s in shapes)
            {
                sb.Append(ShapeXml(s, (uint)id++));
            }

            long cx = (long)(canvasWidthInches * Emu);
            long cy = (long)(canvasHeightInches * Emu);

            string inline =
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
                @"<Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>" +
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

        private static string ShapeXml(ComposedShape s, uint id)
        {
            long x = (long)(s.X * Emu), y = (long)(s.Y * Emu);
            long w = (long)(s.W * Emu), h = (long)(s.H * Emu);
            string prst = s.Prst switch
            {
                "roundrect" => "roundRect",
                "ellipse" or "circle" => "ellipse",
                _ => s.Prst
            };
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
                @"<wps:bodyPr rot=""0"" wrap=""square"" lIns=""12700"" tIns=""6350"" rIns=""12700"" bIns=""6350"" anchor=""ctr"" anchorCtr=""0""><a:noAutofit/></wps:bodyPr>" +
                @"</wps:wsp>";
        }
    }
}
