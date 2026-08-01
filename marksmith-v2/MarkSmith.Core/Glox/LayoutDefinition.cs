using System.Collections.Generic;

namespace MarkSmith.Core.Glox
{
    public class GloxAlgorithm
    {
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }

    public class GloxConstraint
    {
        public string Type { get; set; } = string.Empty;
        public string RefType { get; set; } = string.Empty;
        public string RefFor { get; set; } = string.Empty;
        public double Value { get; set; }
        public double Factor { get; set; } = 1.0;
    }

    public class GloxRule
    {
        public string Type { get; set; } = string.Empty;
        public string Val { get; set; } = string.Empty;
        public string Fact { get; set; } = string.Empty;
    }

    public class GloxForEach
    {
        public string Axis { get; set; } = "ch";
        public string RefNode { get; set; } = "node";
        public bool HideLastTransition { get; set; }
    }

    public class GloxChoose
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Conditions { get; set; } = new List<string>();
    }
}
