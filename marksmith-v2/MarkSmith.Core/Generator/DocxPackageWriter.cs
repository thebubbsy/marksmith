using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MarkSmith.Core.Generator
{
    public static class DocxPackageWriter
    {
        public static void WriteDocx(string outputPath, DiagramGenerationResult genResult)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

            // [Content_Types].xml
            WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml(genResult));

            // _rels/.rels
            WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());

            // word/document.xml
            WriteEntry(archive, "word/document.xml", BuildDocumentXml());

            // word/_rels/document.xml.rels
            WriteEntry(archive, "word/_rels/document.xml.rels", BuildDocumentRelsXml(genResult));

            // word/diagrams/data1.xml
            WriteEntry(archive, "word/diagrams/data1.xml", genResult.DiagramDataXml);

            // word/diagrams/layout1.xml
            WriteEntry(archive, "word/diagrams/layout1.xml", genResult.DiagramLayoutXml);

            // word/diagrams/style1.xml
            WriteEntry(archive, "word/diagrams/style1.xml", genResult.DiagramStyleXml);

            // word/diagrams/colors1.xml
            WriteEntry(archive, "word/diagrams/colors1.xml", genResult.DiagramColorsXml);

            // Add images to word/media/
            foreach (var kvp in genResult.ImageRelMap)
            {
                string imagePath = kvp.Key;
                string rId = kvp.Value;
                string extension = Path.GetExtension(imagePath).TrimStart('.').ToLower();
                if (string.IsNullOrEmpty(extension)) extension = "png";

                string mediaName = $"word/media/image_{rId}.{extension}";

                if (File.Exists(imagePath))
                {
                    byte[] imageBytes = File.ReadAllBytes(imagePath);
                    var entry = archive.CreateEntry(mediaName);
                    using var stream = entry.Open();
                    stream.Write(imageBytes, 0, imageBytes.Length);
                }
                else
                {
                    // Create dummy 1x1 PNG byte if file not found
                    byte[] dummyPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
                    var entry = archive.CreateEntry(mediaName);
                    using var stream = entry.Open();
                    stream.Write(dummyPng, 0, dummyPng.Length);
                }
            }
        }

        private static void WriteEntry(ZipArchive archive, string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        private static string BuildContentTypesXml(DiagramGenerationResult gen)
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Default Extension=""png"" ContentType=""image/png""/>
  <Default Extension=""jpeg"" ContentType=""image/jpeg""/>
  <Default Extension=""jpg"" ContentType=""image/jpeg""/>
  <Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>
  <Override PartName=""/word/diagrams/data1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.drawingml.diagramData+xml""/>
  <Override PartName=""/word/diagrams/layout1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.drawingml.diagramLayout+xml""/>
  <Override PartName=""/word/diagrams/style1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.drawingml.diagramStyle+xml""/>
  <Override PartName=""/word/diagrams/colors1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.drawingml.diagramColors+xml""/>
</Types>";
        }

        private static string BuildRootRelsXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>
</Relationships>";
        }

        private static string BuildDocumentXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""
            xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""
            xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing""
            xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main""
            xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram"">
  <w:body>
    <w:p>
      <w:r>
        <w:drawing>
          <wp:inline distT=""0"" distB=""0"" distL=""0"" distR=""0"">
            <wp:extent cx=""5486400"" cy=""3200400""/>
            <wp:docPr id=""1"" name=""SmartArt Diagram""/>
            <a:graphic>
              <a:graphicData uri=""http://schemas.openxmlformats.org/drawingml/2006/diagram"">
                <dgm:relIds r:dm=""rIdData1"" r:lo=""rIdLayout1"" r:qs=""rIdStyle1"" r:cs=""rIdColors1""/>
              </a:graphicData>
            </a:graphic>
          </wp:inline>
        </w:drawing>
      </w:r>
    </w:p>
  </w:body>
</w:document>";
        }

        private static string BuildDocumentRelsXml(DiagramGenerationResult gen)
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
            sb.AppendLine(@"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">");
            sb.AppendLine(@"  <Relationship Id=""rIdData1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData"" Target=""diagrams/data1.xml""/>");
            sb.AppendLine(@"  <Relationship Id=""rIdLayout1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramLayout"" Target=""diagrams/layout1.xml""/>");
            sb.AppendLine(@"  <Relationship Id=""rIdStyle1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramQuickStyle"" Target=""diagrams/style1.xml""/>");
            sb.AppendLine(@"  <Relationship Id=""rIdColors1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramColors"" Target=""diagrams/colors1.xml""/>");

            foreach (var kvp in gen.ImageRelMap)
            {
                string rId = kvp.Value;
                string extension = Path.GetExtension(kvp.Key).TrimStart('.').ToLower();
                if (string.IsNullOrEmpty(extension)) extension = "png";
                sb.AppendLine($@"  <Relationship Id=""{rId}"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"" Target=""media/image_{rId}.{extension}""/>");
            }

            sb.AppendLine(@"</Relationships>");
            return sb.ToString();
        }
    }
}
