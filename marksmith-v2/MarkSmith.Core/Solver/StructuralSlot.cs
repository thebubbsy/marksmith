using System;
using System.Collections.Generic;

namespace MarkSmith.Core.Solver
{
    public class DiagramPointSlot
    {
        public string ModelId { get; set; } = Guid.NewGuid().ToString("B").ToUpper();
        public string PointType { get; set; } = "node"; // doc, node, parTrans, sibTrans, pres
        public string? AssociatedNodeId { get; set; }
        public string? Text { get; set; }
        public string? Description { get; set; }
        public string? ImagePath { get; set; }
        public string? PresName { get; set; }
        public string? PresStyleLbl { get; set; }
        public int PresStyleIdx { get; set; } = 0;
        public int PresStyleCnt { get; set; } = 1;
        public string? PresAssocId { get; set; }
        public string? CxnId { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }

    public class DiagramConnectionSlot
    {
        public string ModelId { get; set; } = Guid.NewGuid().ToString("B").ToUpper();
        public string CxnType { get; set; } = "parOf"; // parOf, presOf, presParOf
        public string SrcId { get; set; } = string.Empty;
        public string DestId { get; set; } = string.Empty;
        public int SrcOrd { get; set; } = 0;
        public int DestOrd { get; set; } = 0;
        public string? ParTransId { get; set; }
        public string? SibTransId { get; set; }
        public string? PresId { get; set; }
    }

    public class SolvedLayoutStructure
    {
        public List<DiagramPointSlot> Points { get; set; } = new List<DiagramPointSlot>();
        public List<DiagramConnectionSlot> Connections { get; set; } = new List<DiagramConnectionSlot>();
        public Dictionary<string, DiagramPointSlot> NodePointMap { get; set; } = new Dictionary<string, DiagramPointSlot>();
    }
}
