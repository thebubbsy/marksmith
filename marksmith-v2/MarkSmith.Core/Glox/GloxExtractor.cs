using System;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MarkSmith.Core.Glox
{
    public static class GloxExtractor
    {
        private static readonly XNamespace DgmNs = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

        public static GloxPackage ExtractFromZip(Stream zipStream)
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var layoutEntry = archive.GetEntry("diagrams/layout1.xml") ?? archive.GetEntry("layoutDef.xml");
            if (layoutEntry == null) throw new InvalidDataException("Missing layoutXml in glox package.");

            using var layoutReader = new StreamReader(layoutEntry.Open());
            string layoutXml = layoutReader.ReadToEnd();

            string styleXml = string.Empty;
            var styleEntry = archive.GetEntry("diagrams/style1.xml") ?? archive.GetEntry("styleDef.xml");
            if (styleEntry != null)
            {
                using var styleReader = new StreamReader(styleEntry.Open());
                styleXml = styleReader.ReadToEnd();
            }

            string colorXml = string.Empty;
            var colorEntry = archive.GetEntry("diagrams/colors1.xml") ?? archive.GetEntry("colorDef.xml");
            if (colorEntry != null)
            {
                using var colorReader = new StreamReader(colorEntry.Open());
                colorXml = colorReader.ReadToEnd();
            }

            var pkg = ExtractFromXmlString(layoutXml);
            pkg.StyleXml = styleXml;
            pkg.ColorXml = colorXml;
            return pkg;
        }

        public static GloxPackage ExtractFromXmlString(string xmlContent)
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) throw new ArgumentException("Invalid XML document.");

            var pkg = new GloxPackage
            {
                LayoutXml = xmlContent,
                UniqueId = root.Attribute("uniqueId")?.Value ?? string.Empty,
                Title = root.Attribute("title")?.Value ?? string.Empty,
                Description = root.Attribute("desc")?.Value ?? string.Empty,
            };

            var catElem = root.Element(DgmNs + "catLst")?.Element(DgmNs + "cat");
            if (catElem != null)
            {
                pkg.Category = catElem.Attribute("type")?.Value ?? string.Empty;
            }

            // Extract algorithms
            foreach (var alg in root.Descendants(DgmNs + "alg"))
            {
                var algType = alg.Attribute("type")?.Value ?? string.Empty;
                var gAlg = new GloxAlgorithm { Type = algType };
                foreach (var attr in alg.Attributes())
                {
                    gAlg.Parameters[attr.Name.LocalName] = attr.Value;
                }
                pkg.Algorithms.Add(gAlg);
            }

            // Extract constraints
            foreach (var c in root.Descendants(DgmNs + "constr"))
            {
                pkg.Constraints.Add(new GloxConstraint
                {
                    Type = c.Attribute("type")?.Value ?? string.Empty,
                    RefType = c.Attribute("refType")?.Value ?? string.Empty,
                    RefFor = c.Attribute("refFor")?.Value ?? string.Empty,
                    Value = double.TryParse(c.Attribute("val")?.Value, out var v) ? v : 0.0,
                    Factor = double.TryParse(c.Attribute("fact")?.Value, out var f) ? f : 1.0,
                });
            }

            // Extract forEach
            foreach (var fe in root.Descendants(DgmNs + "forEach"))
            {
                pkg.ForEachBlocks.Add(new GloxForEach
                {
                    Axis = fe.Attribute("axis")?.Value ?? "ch",
                    RefNode = fe.Attribute("refNode")?.Value ?? "node",
                    HideLastTransition = fe.Attribute("hideLastTrans")?.Value == "1"
                });
            }

            // Extract picture definition
            foreach (var pic in root.Descendants(DgmNs + "pic"))
            {
                pkg.HasPictureNode = true;
            }

            return pkg;
        }
    }
}
