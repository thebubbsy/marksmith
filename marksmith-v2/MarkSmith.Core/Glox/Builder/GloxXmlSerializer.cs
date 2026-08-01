using System.Text;
using System.Xml;

namespace MarkSmith.Core.Glox.Builder
{
    public class GloxXmlSerializer
    {
        public static string Serialize(GloxLayoutDefinition def)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
            
            using (var writer = XmlWriter.Create(sb, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("dgm", "layoutDef", "http://schemas.openxmlformats.org/drawingml/2006/diagram");
                writer.WriteAttributeString("xmlns", "a", null, "http://schemas.openxmlformats.org/drawingml/2006/main");
                
                writer.WriteAttributeString("uniqueId", def.UniqueId);
                writer.WriteAttributeString("title", def.Title);
                writer.WriteAttributeString("desc", def.Desc);
                writer.WriteAttributeString("clrgName", "Colorful");
                writer.WriteAttributeString("clrType", "colorful");

                writer.WriteStartElement("dgm", "title", null);
                writer.WriteAttributeString("val", def.Title);
                writer.WriteEndElement();

                writer.WriteStartElement("dgm", "desc", null);
                writer.WriteAttributeString("val", def.Desc);
                writer.WriteEndElement();

                writer.WriteStartElement("dgm", "catLst", null);
                writer.WriteStartElement("dgm", "cat", null);
                writer.WriteAttributeString("type", def.Category);
                writer.WriteAttributeString("pri", "1");
                writer.WriteEndElement();
                writer.WriteEndElement(); // catLst

                SerializeNode(writer, def.RootNode);

                writer.WriteEndElement(); // layoutDef
                writer.WriteEndDocument();
            }

            return sb.ToString();
        }

        private static void SerializeNode(XmlWriter writer, GloxLayoutNode node)
        {
            writer.WriteStartElement("dgm", "layoutNode", null);
            writer.WriteAttributeString("name", node.Name);

            if (node.Algorithm != null)
            {
                writer.WriteStartElement("dgm", "alg", null);
                writer.WriteAttributeString("type", node.Algorithm.Type);
                writer.WriteEndElement();
            }

            if (node.Shape != null)
            {
                writer.WriteStartElement("dgm", "shape", null);
                writer.WriteAttributeString("type", node.Shape.Type);
                writer.WriteEndElement();
            }

            foreach (var fe in node.ForEachLoops)
            {
                writer.WriteStartElement("dgm", "forEach", null);
                writer.WriteAttributeString("axis", fe.Axis);
                writer.WriteAttributeString("refNode", fe.RefNode);
                writer.WriteEndElement();
            }

            foreach (var child in node.Children)
            {
                SerializeNode(writer, child);
            }

            writer.WriteEndElement(); // layoutNode
        }
    }
}
