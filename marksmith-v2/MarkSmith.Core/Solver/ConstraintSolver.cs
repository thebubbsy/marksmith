using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;

namespace MarkSmith.Core.Solver
{
    public class ConstraintSolver
    {
        public SolvedLayoutStructure Solve(CanonicalAst ast, GloxPackage layoutGlox)
        {
            var solved = new SolvedLayoutStructure();

            // 1. Create Document Root Point
            var docPt = new DiagramPointSlot
            {
                PointType = "doc",
                Text = string.Empty
            };
            solved.Points.Add(docPt);

            var rootAstNode = ast.Root;
            var dataNodes = rootAstNode.Children.Count > 0 ? rootAstNode.Children : new List<AstNode> { rootAstNode };

            // Map AST nodes to Points
            var astToPointMap = new Dictionary<string, DiagramPointSlot>();
            var presPoints = new List<DiagramPointSlot>();

            // Root pres point
            var rootPresPt = new DiagramPointSlot
            {
                PointType = "pres",
                PresAssocId = docPt.ModelId,
                PresName = "diagram",
                PresStyleCnt = 0
            };
            solved.Points.Add(rootPresPt);

            // Connect Doc -> RootPres
            solved.Connections.Add(new DiagramConnectionSlot
            {
                CxnType = "presOf",
                SrcId = docPt.ModelId,
                DestId = rootPresPt.ModelId,
                PresId = layoutGlox.UniqueId
            });

            int nodeIndex = 0;

            void ProcessNodeHierarchy(AstNode astNode, DiagramPointSlot parentDataPt)
            {
                string connGuid = Guid.NewGuid().ToString("B").ToUpper();

                // ParTrans and SibTrans points
                var parTransPt = new DiagramPointSlot
                {
                    PointType = "parTrans",
                    CxnId = connGuid
                };
                var sibTransPt = new DiagramPointSlot
                {
                    PointType = "sibTrans",
                    CxnId = connGuid
                };
                solved.Points.Add(parTransPt);
                solved.Points.Add(sibTransPt);

                // Data Point
                var dataPt = new DiagramPointSlot
                {
                    PointType = "node",
                    AssociatedNodeId = astNode.NodeId,
                    Text = astNode.Text,
                    Description = astNode.Description,
                    ImagePath = astNode.ImagePath
                };
                solved.Points.Add(dataPt);
                astToPointMap[astNode.NodeId] = dataPt;
                solved.NodePointMap[astNode.NodeId] = dataPt;

                // Connection parent -> child
                solved.Connections.Add(new DiagramConnectionSlot
                {
                    ModelId = connGuid,
                    CxnType = "parOf",
                    SrcId = parentDataPt.ModelId,
                    DestId = dataPt.ModelId,
                    SrcOrd = nodeIndex,
                    DestOrd = 0,
                    ParTransId = parTransPt.ModelId,
                    SibTransId = sibTransPt.ModelId
                });

                // Presentation Point for data node
                var presPt = new DiagramPointSlot
                {
                    PointType = "pres",
                    PresAssocId = dataPt.ModelId,
                    PresName = "node",
                    PresStyleLbl = "node1",
                    PresStyleIdx = nodeIndex,
                    PresStyleCnt = dataNodes.Count
                };
                solved.Points.Add(presPt);
                presPoints.Add(presPt);

                // Connection dataPt -> presPt
                solved.Connections.Add(new DiagramConnectionSlot
                {
                    CxnType = "presOf",
                    SrcId = dataPt.ModelId,
                    DestId = presPt.ModelId,
                    PresId = layoutGlox.UniqueId
                });

                // Connect parent pres -> child pres
                solved.Connections.Add(new DiagramConnectionSlot
                {
                    CxnType = "presParOf",
                    SrcId = rootPresPt.ModelId,
                    DestId = presPt.ModelId,
                    SrcOrd = nodeIndex,
                    DestOrd = 0,
                    PresId = layoutGlox.UniqueId
                });

                nodeIndex++;

                // Recurse children
                foreach (var child in astNode.Children)
                {
                    ProcessNodeHierarchy(child, dataPt);
                }
            }

            foreach (var node in dataNodes)
            {
                ProcessNodeHierarchy(node, docPt);
            }

            return solved;
        }
    }
}
