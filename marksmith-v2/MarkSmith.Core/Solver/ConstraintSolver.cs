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

            // Root pres point (native data models: presStyleIdx=0 presStyleCnt=1, node0).
            var rootPresPt = new DiagramPointSlot
            {
                PointType = "pres",
                PresAssocId = docPt.ModelId,
                PresName = "diagram",
                PresStyleLbl = "node0",
                PresStyleIdx = 0,
                PresStyleCnt = 1
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

            void ProcessNodeHierarchy(AstNode astNode, DiagramPointSlot parentDataPt, DiagramPointSlot parentPresPt, int level, int siblingOrdinal, int siblingCount)
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
                    ImagePath = astNode.ImagePath,
                    Attributes = new Dictionary<string, string>(astNode.Attributes)
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

                // Presentation Point for data node — the native Office coloring contract:
                //   presStyleLbl = "node{level}"  -> per DEPTH LEVEL (node0 at the root, node1 for
                //                                    level-1, node2 for level-2, ... exactly what
                //                                    native data models like orgChart1 emit)
                //   presStyleIdx  = ordinal within this sibling group (0..n-1)
                //   presStyleCnt  = sibling group size
                // Both match the authoritative Office samples in the native corpus, so Word paints
                // vibrant per-level + per-sibling colors instead of uniform grayscale.
                var presPt = new DiagramPointSlot
                {
                    PointType = "pres",
                    PresAssocId = dataPt.ModelId,
                    PresName = "node",
                    PresStyleLbl = "node" + Math.Min(level, 4),
                    PresStyleIdx = siblingOrdinal,
                    PresStyleCnt = siblingCount
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

                // Connect parent pres -> child pres (chained parent→child, like native data
                // models — the root pres is the parent of level-0 nodes only).
                solved.Connections.Add(new DiagramConnectionSlot
                {
                    CxnType = "presParOf",
                    SrcId = parentPresPt.ModelId,
                    DestId = presPt.ModelId,
                    SrcOrd = nodeIndex,
                    DestOrd = 0,
                    PresId = layoutGlox.UniqueId
                });

                nodeIndex++;

                // Recurse children (per-sibling ordinals + deeper level, pres chain parent→child)
                for (int i = 0; i < astNode.Children.Count; i++)
                {
                    ProcessNodeHierarchy(astNode.Children[i], dataPt, presPt, level + 1, i, astNode.Children.Count);
                }
            }

            for (int i = 0; i < dataNodes.Count; i++)
            {
                ProcessNodeHierarchy(dataNodes[i], docPt, rootPresPt, 0, i, dataNodes.Count);
            }

            return solved;
        }
    }
}
