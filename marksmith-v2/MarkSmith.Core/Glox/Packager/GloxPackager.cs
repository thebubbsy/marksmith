using System;
using System.IO;
using System.IO.Compression;

namespace MarkSmith.Core.Glox.Packager
{
    public class GloxPackager
    {
        public static void Package(string xmlContent, string outputPath)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);

            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // [Content_Types].xml
                var ctEntry = archive.CreateEntry("[Content_Types].xml");
                using (var ctStream = ctEntry.Open())
                using (var writer = new StreamWriter(ctStream))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml"" />
  <Default Extension=""xml"" ContentType=""application/xml"" />
  <Override PartName=""/diagrams/layout1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.drawingml.diagramLayout+xml"" />
  <Override PartName=""/diagrams/layoutHeader1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.drawingml.diagramLayoutHeader+xml"" />
</Types>");
                }

                // _rels/.rels
                var relsEntry = archive.CreateEntry("_rels/.rels");
                using (var relsStream = relsEntry.Open())
                using (var writer = new StreamWriter(relsStream))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramLayout"" Target=""diagrams/layout1.xml"" />
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramLayoutHeader"" Target=""diagrams/layoutHeader1.xml"" />
</Relationships>");
                }

                // diagrams/layout1.xml
                var layoutEntry = archive.CreateEntry("diagrams/layout1.xml");
                using (var layoutStream = layoutEntry.Open())
                using (var writer = new StreamWriter(layoutStream))
                {
                    writer.Write(xmlContent);
                }

                // diagrams/layoutHeader1.xml
                var headerEntry = archive.CreateEntry("diagrams/layoutHeader1.xml");
                using (var headerStream = headerEntry.Open())
                using (var writer = new StreamWriter(headerStream))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""utf-8""?>
<dgm:layoutDefHdr xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram"" uniqueId=""urn:microsoft.com/office/officeart/2005/8/layout/custom1"">
  <dgm:title val=""Custom Layout"" />
  <dgm:desc val="""" />
</dgm:layoutDefHdr>");
                }
            }
        }
    }
}
