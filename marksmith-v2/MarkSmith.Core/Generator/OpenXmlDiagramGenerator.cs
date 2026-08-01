using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Solver;

namespace MarkSmith.Core.Generator
{
    public class DiagramGenerationResult
    {
        public string DiagramDataXml { get; set; } = string.Empty;
        public string DiagramLayoutXml { get; set; } = string.Empty;
        public string DiagramStyleXml { get; set; } = string.Empty;
        public string DiagramColorsXml { get; set; } = string.Empty;
        public Dictionary<string, string> ImageRelMap { get; set; } = new Dictionary<string, string>();
    }

    public class OpenXmlDiagramGenerator
    {
        private static readonly XNamespace DgmNs = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        private static readonly XNamespace ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public DiagramGenerationResult Generate(SolvedLayoutStructure solved, GloxPackage gloxPkg)
        {
            var result = new DiagramGenerationResult
            {
                DiagramLayoutXml = gloxPkg.LayoutXml,
                DiagramStyleXml = string.IsNullOrWhiteSpace(gloxPkg.StyleXml) ? BuildDefaultStyle(gloxPkg.UniqueId) : gloxPkg.StyleXml,
                DiagramColorsXml = string.IsNullOrWhiteSpace(gloxPkg.ColorXml) ? BuildDefaultColors(gloxPkg.UniqueId) : gloxPkg.ColorXml
            };

            var dataModelElem = new XElement(DgmNs + "dataModel",
                new XAttribute(XNamespace.Xmlns + "dgm", DgmNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", ANs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", RNs.NamespaceName)
            );

            var ptLstElem = new XElement(DgmNs + "ptLst");
            int imgCounter = 1;

            foreach (var pt in solved.Points)
            {
                var ptElem = new XElement(DgmNs + "pt",
                    new XAttribute("modelId", pt.ModelId)
                );

                if (pt.PointType == "doc")
                {
                    ptElem.Add(new XAttribute("type", "doc"));
                    ptElem.Add(new XElement(DgmNs + "prSet",
                        new XAttribute("loTypeId", gloxPkg.UniqueId),
                        new XAttribute("loCatId", gloxPkg.Category ?? "list"),
                        new XAttribute("qsTypeId", gloxPkg.UniqueId.Replace("layout", "quickstyle")),
                        new XAttribute("qsCatId", "simple"),
                        new XAttribute("csTypeId", gloxPkg.UniqueId.Replace("layout", "colors")),
                        new XAttribute("csCatId", "colorful"),
                        new XAttribute("phldr", "1")
                    ));
                    ptElem.Add(new XElement(DgmNs + "spPr"));
                    ptElem.Add(BuildTextElement(string.Empty));
                }
                else if (pt.PointType == "node")
                {
                    ptElem.Add(new XElement(DgmNs + "prSet", new XAttribute("phldrT", "[Text]")));

                    var spPrElem = new XElement(DgmNs + "spPr");

                    if (!string.IsNullOrWhiteSpace(pt.ImagePath))
                    {
                        string rId = $"rIdImg{imgCounter++}";
                        result.ImageRelMap[pt.ImagePath] = rId;

                        spPrElem.Add(new XElement(ANs + "blipFill",
                            new XElement(ANs + "blip", new XAttribute(RNs + "embed", rId)),
                            new XElement(ANs + "stretch", new XElement(ANs + "fillRect"))
                        ));
                    }

                    ptElem.Add(spPrElem);
                    ptElem.Add(BuildTextElement(pt.Text ?? string.Empty));
                }
                else if (pt.PointType == "parTrans" || pt.PointType == "sibTrans")
                {
                    ptElem.Add(new XAttribute("type", pt.PointType));
                    if (!string.IsNullOrEmpty(pt.CxnId))
                    {
                        ptElem.Add(new XAttribute("cxnId", pt.CxnId));
                    }
                    ptElem.Add(new XElement(DgmNs + "prSet"));
                    ptElem.Add(new XElement(DgmNs + "spPr"));
                    ptElem.Add(BuildTextElement(string.Empty));
                }
                else if (pt.PointType == "pres")
                {
                    ptElem.Add(new XAttribute("type", "pres"));

                    var prSetElem = new XElement(DgmNs + "prSet");
                    if (!string.IsNullOrEmpty(pt.PresAssocId)) prSetElem.Add(new XAttribute("presAssocID", pt.PresAssocId));
                    if (!string.IsNullOrEmpty(pt.PresName)) prSetElem.Add(new XAttribute("presName", pt.PresName));
                    if (!string.IsNullOrEmpty(pt.PresStyleLbl)) prSetElem.Add(new XAttribute("presStyleLbl", pt.PresStyleLbl));
                    prSetElem.Add(new XAttribute("presStyleIdx", pt.PresStyleIdx.ToString()));
                    prSetElem.Add(new XAttribute("presStyleCnt", pt.PresStyleCnt.ToString()));

                    ptElem.Add(prSetElem);
                    ptElem.Add(new XElement(DgmNs + "spPr"));
                }

                ptLstElem.Add(ptElem);
            }

            var cxnLstElem = new XElement(DgmNs + "cxnLst");
            foreach (var cxn in solved.Connections)
            {
                var cxnElem = new XElement(DgmNs + "cxn",
                    new XAttribute("modelId", cxn.ModelId),
                    new XAttribute("srcId", cxn.SrcId),
                    new XAttribute("destId", cxn.DestId),
                    new XAttribute("srcOrd", cxn.SrcOrd.ToString()),
                    new XAttribute("destOrd", cxn.DestOrd.ToString())
                );

                if (cxn.CxnType == "presOf" || cxn.CxnType == "presParOf")
                {
                    cxnElem.Add(new XAttribute("type", cxn.CxnType));
                    if (!string.IsNullOrEmpty(cxn.PresId)) cxnElem.Add(new XAttribute("presId", cxn.PresId));
                }
                else
                {
                    if (!string.IsNullOrEmpty(cxn.ParTransId)) cxnElem.Add(new XAttribute("parTransId", cxn.ParTransId));
                    if (!string.IsNullOrEmpty(cxn.SibTransId)) cxnElem.Add(new XAttribute("sibTransId", cxn.SibTransId));
                }

                cxnLstElem.Add(cxnElem);
            }

            dataModelElem.Add(ptLstElem);
            dataModelElem.Add(cxnLstElem);

            result.DiagramDataXml = new XDocument(dataModelElem).ToString();
            return result;
        }

        private static XElement BuildTextElement(string text)
        {
            var tElem = new XElement(DgmNs + "t",
                new XElement(ANs + "bodyPr"),
                new XElement(ANs + "lstStyle"),
                new XElement(ANs + "p")
            );

            if (!string.IsNullOrEmpty(text))
            {
                tElem.Element(ANs + "p")?.Add(
                    new XElement(ANs + "r",
                        new XElement(ANs + "rPr", new XAttribute("lang", "en-US")),
                        new XElement(ANs + "t", text)
                    )
                );
            }
            else
            {
                tElem.Element(ANs + "p")?.Add(
                    new XElement(ANs + "endParaRPr", new XAttribute("lang", "en-US"))
                );
            }

            return tElem;
        }

        private static string BuildDefaultStyle(string uniqueId)
        {
            return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" uniqueId=\"{uniqueId.Replace("layout", "quickstyle")}\"/>";
        }

        private static string BuildDefaultColors(string uniqueId)
        {
            return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><dgm:clrData xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" uniqueId=\"{uniqueId.Replace("layout", "colors")}\"/>";
        }
    }
}
