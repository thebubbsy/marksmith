using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;
using MdToPdf.Core.AdvancedFeatures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Services;

public class SmartArtNode
{
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int Level { get; set; }
    public List<SmartArtNode> Children { get; set; } = new();
}

public static class UniversalSmartArtBuilder
{
    private static readonly XNamespace dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private static readonly XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly Dictionary<string, string> URN_MAP = new(StringComparer.OrdinalIgnoreCase)
    {
        { "timeline", "urn:microsoft.com/office/officeart/2024/layout/BulletTimeline" },
        { "workflow", "urn:microsoft.com/office/officeart/2005/8/layout/process1" },
        { "hierarchy", "urn:microsoft.com/office/officeart/2005/8/layout/orgChart1" },
        { "cycle", "urn:microsoft.com/office/officeart/2005/8/layout/cycle1" },
        { "venn", "urn:microsoft.com/office/officeart/2005/8/layout/venn1" },
        { "matrix", "urn:microsoft.com/office/officeart/2005/8/layout/matrix1" },
        { "pyramid", "urn:microsoft.com/office/officeart/2005/8/layout/pyramid1" },
        { "radial", "urn:microsoft.com/office/officeart/2005/8/layout/radial1" },
        { "target", "urn:microsoft.com/office/officeart/2005/8/layout/target1" },
        { "list", "urn:microsoft.com/office/officeart/2005/8/layout/list1" }
    };

    public static void Build(FeatureNode node, MainDocumentPart mainPart, OpenXmlCompositeElement target, ref uint docPrId)
    {
        string urn = ResolveUrn(node);
        var rootNodes = ParseMarkdownList(node.InnerContent);
        if (rootNodes.Count == 0) return;

        var dmPart = mainPart.AddNewPart<DiagramDataPart>();

        var dataModelDoc = CreateDataModel(rootNodes, dmPart);
        var layoutDoc = CreateLayoutDef(urn);
        var colorsDoc = CreateColorsDef();
        var styleDoc = CreateStyleDef();

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
      <wp:docPr id=""{docPrId}"" name=""SmartArt Diagram {docPrId}"" descr=""Native Word SmartArt""/>
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

    private static string ResolveUrn(FeatureNode node)
    {
        if (node.Attributes.TryGetValue("type", out string? customType) && customType != null)
        {
            if (customType.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
                return customType;
            if (URN_MAP.TryGetValue(customType, out string? mapped) && mapped != null)
                return mapped;
        }
        else if (node.Attributes.TryGetValue("layout", out string? customLayout) && customLayout != null)
        {
            if (customLayout.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
                return customLayout;
            if (URN_MAP.TryGetValue(customLayout, out string? mapped) && mapped != null)
                return mapped;
        }

        // Fallbacks based on feature name
        var feature = node.Detector.FeatureName.ToLowerInvariant();
        if (feature == "timeline") return URN_MAP["timeline"];
        if (feature == "workflow") return URN_MAP["workflow"];
        
        return URN_MAP["list"]; // Safe default
    }

    private static List<SmartArtNode> ParseMarkdownList(string content)
    {
        var lines = content.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var rootNodes = new List<SmartArtNode>();
        var stack = new Stack<SmartArtNode>();
        int currentLevel = -1;

        foreach (var line in lines)
        {
            int indent = line.TakeWhile(c => c == ' ' || c == '\t').Count();
            string text = line.TrimStart(' ', '\t', '-', '*').Trim();
            if (string.IsNullOrEmpty(text)) continue;

            string? imageUrl = null;
            var match = System.Text.RegularExpressions.Regex.Match(text, @"!\[.*?\]\((.*?)\)");
            if (match.Success)
            {
                imageUrl = match.Groups[1].Value;
                text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[.*?\]\(.*?\)", "").Trim();
            }

            // Simplified mapping: 2 spaces or 1 tab = 1 level
            int level = line.Contains('\t') ? indent : indent / 2;

            var node = new SmartArtNode { Text = text, ImageUrl = imageUrl, Level = level };

            if (level == 0)
            {
                rootNodes.Add(node);
                stack.Clear();
                stack.Push(node);
                currentLevel = 0;
            }
            else
            {
                while (stack.Count > 0 && stack.Peek().Level >= level)
                {
                    stack.Pop();
                }

                if (stack.Count > 0)
                {
                    stack.Peek().Children.Add(node);
                    stack.Push(node);
                }
                else
                {
                    rootNodes.Add(node);
                    stack.Push(node);
                }
                currentLevel = level;
            }
        }

        return rootNodes;
    }

    private static XDocument CreateDataModel(List<SmartArtNode> rootNodes, DiagramDataPart dmPart)
    {
        var ptLst = new XElement(dgm + "ptLst",
            new XElement(dgm + "pt",
                new XAttribute("modelId", "0"),
                new XAttribute("type", "doc"),
                new XElement(dgm + "prSet"),
                new XElement(dgm + "spPr"),
                new XElement(dgm + "t",
                    new XElement(a + "bodyPr"),
                    new XElement(a + "lstStyle"),
                    new XElement(a + "p", new XElement(a + "r", new XElement(a + "t", "")))
                )
            )
        );

        var cxnLst = new XElement(dgm + "cxnLst");

        int pointIdCounter = 1;
        int cxnIdCounter = 1000;

        void BuildNodesRecursive(List<SmartArtNode> nodes, int parentId)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                int currentId = pointIdCounter++;

                var spPr = new XElement(dgm + "spPr");
                if (!string.IsNullOrEmpty(n.ImageUrl))
                {
                    try
                    {
                        byte[]? imgBytes = null;
                        if (n.ImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            using var client = new System.Net.Http.HttpClient();
                            imgBytes = client.GetByteArrayAsync(n.ImageUrl).Result;
                        }
                        else if (File.Exists(n.ImageUrl))
                        {
                            imgBytes = File.ReadAllBytes(n.ImageUrl);
                        }

                        if (imgBytes != null)
                        {
                            var partType = ImagePartType.Png;
                            if (n.ImageUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || n.ImageUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                                partType = ImagePartType.Jpeg;
                            
                            var ip = dmPart.AddImagePart(partType);
                            using (var ms = new MemoryStream(imgBytes)) ip.FeedData(ms);
                            string relId = dmPart.GetIdOfPart(ip);

                            XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                            spPr.Add(
                                new XElement(a + "blipFill",
                                    new XElement(a + "blip", new XAttribute(r + "embed", relId)),
                                    new XElement(a + "stretch", new XElement(a + "fillRect"))
                                )
                            );
                        }
                    }
                    catch { }
                }

                ptLst.Add(new XElement(dgm + "pt",
                    new XAttribute("modelId", currentId.ToString()),
                    new XAttribute("type", "node"),
                    new XElement(dgm + "prSet"),
                    spPr,
                    new XElement(dgm + "t",
                        new XElement(a + "bodyPr"),
                        new XElement(a + "lstStyle"),
                        new XElement(a + "p",
                            new XElement(a + "r",
                                new XElement(a + "rPr", new XAttribute("lang", "en-US")),
                                new XElement(a + "t", n.Text)
                            )
                        )
                    )
                ));

                cxnLst.Add(new XElement(dgm + "cxn",
                    new XAttribute("modelId", (cxnIdCounter++).ToString()),
                    new XAttribute("type", "parOf"),
                    new XAttribute("srcId", parentId.ToString()),
                    new XAttribute("destId", currentId.ToString()),
                    new XAttribute("srcOrd", i.ToString()),
                    new XAttribute("destOrd", "0")
                ));

                if (n.Children.Count > 0)
                {
                    BuildNodesRecursive(n.Children, currentId);
                }
            }
        }

        BuildNodesRecursive(rootNodes, 0);

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

    private static string? FindLayoutResource(string urn)
    {
        if (MdToPdf.Core.Services.SmartArtLayoutMap.Map.TryGetValue(urn, out string? res))
            return res;

        string suffix = urn;
        int lastColon = urn.LastIndexOf(':');
        if (lastColon >= 0) suffix = urn.Substring(lastColon + 1);
        int lastSlash = urn.LastIndexOf('/');
        if (lastSlash >= 0) suffix = urn.Substring(lastSlash + 1);

        foreach (var kvp in MdToPdf.Core.Services.SmartArtLayoutMap.Map)
        {
            if (kvp.Key.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.EndsWith("/" + suffix + "1", StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.EndsWith("/" + suffix + "2", StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        
        return null;
    }

    private static XDocument CreateLayoutDef(string urn)
    {
        string? resourcePath = FindLayoutResource(urn);
                              
        if (resourcePath != null)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourcePath);
            if (stream != null)
            {
                return XDocument.Load(stream);
            }
        }

        // Fallback to basic blocks if layout not found
        string algType = "lin";
        string linDir = "fromT"; // Default vertical list
        
        if (urn.Contains("orgChart") || urn.Contains("hierarchy") || urn.Contains("Radial") || urn.Contains("tree")) algType = "hierRoot";
        else if (urn.Contains("cycle")) algType = "cycle";
        else if (urn.Contains("pyramid")) algType = "pyra";
        else if (urn.Contains("Venn") || urn.Contains("Target")) algType = "sp";
        else if (urn.Contains("workflow") || urn.Contains("Timeline") || urn.Contains("Flow")) 
        {
            algType = "lin";
            linDir = "fromL"; // Horizontal
        }

        var algElement = new XElement(dgm + "alg", new XAttribute("type", algType));
        if (algType == "lin") 
        {
            algElement.Add(new XElement(dgm + "param", new XAttribute("type", "linDir"), new XAttribute("val", linDir)));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(dgm + "layoutDef",
                new XAttribute(XNamespace.Xmlns + "dgm", dgm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                new XAttribute("uniqueId", urn),
                new XElement(dgm + "title", new XAttribute("val", "Universal Layout")),
                new XElement(dgm + "desc", new XAttribute("val", "")),
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
                    new XAttribute("name", "root"),
                    new XElement(dgm + "varLst",
                        new XElement(dgm + "chMax", new XAttribute("val", "100")),
                        new XElement(dgm + "dir", new XAttribute("val", "norm")),
                        new XElement(dgm + "animLvl", new XAttribute("val", "lvl"))
                    ),
                    algElement,
                    new XElement(dgm + "shape", new XAttribute("type", "none")),
                    new XElement(dgm + "presOf"),
                    new XElement(dgm + "forEach",
                        new XAttribute("name", "nodeForEach"),
                        new XAttribute("axis", "desc"),
                        new XAttribute("ptType", "node"),
                        new XElement(dgm + "layoutNode",
                            new XAttribute("name", "nodeLayout"),
                            new XElement(dgm + "varLst"),
                            new XElement(dgm + "alg", new XAttribute("type", "tx")),
                            new XElement(dgm + "shape", new XAttribute("type", "roundRect")),
                            new XElement(dgm + "presOf", new XAttribute("axis", "self")),
                            new XElement(dgm + "constrLst"),
                            new XElement(dgm + "ruleLst")
                        )
                    )
                )
            )
        );
    }

    private static XDocument CreateColorsDef()
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

    private static XDocument CreateStyleDef()
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
}
