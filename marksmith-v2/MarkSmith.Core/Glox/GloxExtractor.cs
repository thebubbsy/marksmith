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

            // Native Office .glox packages keep the uniqueId in layoutHeader1.xml, not on the
            // layoutDef root — pull it so imported native layouts resolve by their real URN.
            string headerXml = string.Empty;
            var headerEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("layoutHeader1.xml", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("layoutHeader.xml", StringComparison.OrdinalIgnoreCase));
            if (headerEntry != null)
            {
                using var headerReader = new StreamReader(headerEntry.Open());
                headerXml = headerReader.ReadToEnd();
            }

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

            // Native glox layoutDef roots carry no uniqueId; take it from the header.
            if (string.IsNullOrWhiteSpace(pkg.UniqueId) && !string.IsNullOrWhiteSpace(headerXml))
            {
                pkg.UniqueId = XDocument.Parse(headerXml).Root?.Attribute("uniqueId")?.Value ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(pkg.Title) && !string.IsNullOrWhiteSpace(headerXml))
            {
                var headerRoot = XDocument.Parse(headerXml).Root;
                pkg.Title = headerRoot?.Element(DgmNs + "title")?.Attribute("val")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(pkg.Title))
                {
                    pkg.Title = headerRoot?.Attribute("uniqueId")?.Value.Split('/').LastOrDefault() ?? string.Empty;
                }
            }

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

            // Extract shape definitions, keyed by the enclosing layoutNode name (if any).
            foreach (var shape in root.Descendants(DgmNs + "shape"))
            {
                string shapeType = shape.Attribute("type")?.Value ?? string.Empty;
                var layoutNode = shape.Ancestors(DgmNs + "layoutNode").FirstOrDefault();
                string nodeName = layoutNode?.Attribute("name")?.Value ?? string.Empty;
                if (!string.IsNullOrEmpty(shapeType))
                {
                    if (string.IsNullOrEmpty(nodeName))
                    {
                        pkg.ShapeMappings["default"] = shapeType;
                    }
                    else
                    {
                        pkg.ShapeMappings[nodeName] = shapeType;
                    }
                }
            }

            // Extract conditional branches (<dgm:choose>) with their if/else condition names.
            foreach (var choose in root.Descendants(DgmNs + "choose"))
            {
                var gChoose = new GloxChoose
                {
                    Name = choose.Attribute("name")?.Value ?? string.Empty
                };
                foreach (var cond in choose.Elements()
                             .Where(el => el.Name.LocalName is "if" or "else"))
                {
                    string condName = cond.Attribute("name")?.Value ?? cond.Name.LocalName;
                    gChoose.Conditions.Add(condName);
                }
                pkg.ChooseBlocks.Add(gChoose);
            }

            // Extract rules (<dgm:rule>).
            foreach (var rule in root.Descendants(DgmNs + "rule"))
            {
                pkg.Rules.Add(new GloxRule
                {
                    Type = rule.Attribute("type")?.Value ?? string.Empty,
                    Val = rule.Attribute("val")?.Value ?? string.Empty,
                    Fact = rule.Attribute("fact")?.Value ?? string.Empty,
                });
            }

            return pkg;
        }
    }
}
