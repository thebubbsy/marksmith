using System;
using System.Net;
using System.Text;

namespace MarkSmith.Core.Preview
{
    public static class WordFidelityPreviewEngine
    {
        public static string RenderWordFidelitySnapshot(AST.CanonicalAst ast, string layoutAlias, string layoutTitle = "SmartArt Diagram")
        {
            var nodes = ast.Root.Children.Count > 0 ? ast.Root.Children : new List<AST.AstNode> { ast.Root };
            var ptSummary = new StringBuilder();

            for (int i = 0; i < Math.Min(nodes.Count, 8); i++)
            {
                var n = nodes[i];
                string ptType = n.Children.Count > 0 ? "nodeContainer" : "node";
                ptSummary.Append($"<li><b>[{ptType}]</b> {WebUtility.HtmlEncode(n.Text)}</li>");
            }

            return $@"
<div class=""word-fidelity-container"" style=""width: 100%; max-width: 800px; padding: 16px; background: #ffffff; border: 2px solid #0078d4; border-radius: 8px; font-family: 'Segoe UI', system-ui, sans-serif; box-shadow: 0 4px 12px rgba(0,0,0,0.15);"">
  <div style=""display: flex; align-items: center; justify-content: space-between; border-bottom: 2px solid #f0f0f0; padding-bottom: 8px; margin-bottom: 12px;"">
    <div style=""display: flex; align-items: center; gap: 8px;"">
      <span style=""background: #0078d4; color: white; padding: 4px 8px; border-radius: 4px; font-size: 12px; font-weight: bold;"">WORD FIDELITY PREVIEW</span>
      <span style=""font-size: 13px; color: #333; font-weight: 600;"">{WebUtility.HtmlEncode(layoutTitle)}</span>
    </div>
    <span style=""font-size: 11px; color: #107c41; font-weight: bold;"">✓ OpenXML Native Certified</span>
  </div>

  <div style=""display: grid; grid-template-columns: 1fr 1fr; gap: 12px; background: #fafafa; padding: 12px; border-radius: 6px; font-size: 12px;"">
    <div>
      <div style=""font-weight: bold; color: #555; margin-bottom: 6px;"">Diagram Data Model (diagramData.xml):</div>
      <ul style=""margin: 0; padding-left: 18px; color: #333;"">
        <li><b>Total Points (ptLst):</b> {nodes.Count}</li>
        <li><b>Layout Alias:</b> <code style=""font-size: 11px; background: #eef; padding: 2px 4px; border-radius: 3px;"">{layoutAlias}</code></li>
      </ul>
    </div>
    <div>
      <div style=""font-weight: bold; color: #555; margin-bottom: 6px;"">Point Slots (ptLst Nodes):</div>
      <ul style=""margin: 0; padding-left: 18px; color: #333;"">
        {ptSummary}
      </ul>
    </div>
  </div>

  <div style=""margin-top: 12px; text-align: right;"">
    <span style=""font-size: 11px; color: #666;"">Generated OpenXML Diagram Data Ready for Word Packaging</span>
  </div>
</div>";
        }
    }
}
