using System.Collections.Generic;

namespace MarkSmith.Core.Glox
{
    public class GloxPackage
    {
        public string UniqueId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string LayoutXml { get; set; } = string.Empty;
        public string StyleXml { get; set; } = string.Empty;
        public string ColorXml { get; set; } = string.Empty;

        /// <summary>UniqueId of the quickStyle part (e.g. urn:.../quickstyle/simple1).</summary>
        public string StyleUniqueId { get; set; } = string.Empty;

        /// <summary>UniqueId of the colors part (e.g. urn:.../colors/accent1_2).</summary>
        public string ColorUniqueId { get; set; } = string.Empty;

        public List<GloxAlgorithm> Algorithms { get; set; } = new List<GloxAlgorithm>();
        public List<GloxConstraint> Constraints { get; set; } = new List<GloxConstraint>();
        public List<GloxRule> Rules { get; set; } = new List<GloxRule>();
        public List<GloxForEach> ForEachBlocks { get; set; } = new List<GloxForEach>();
        public List<GloxChoose> ChooseBlocks { get; set; } = new List<GloxChoose>();
        public Dictionary<string, string> ShapeMappings { get; set; } = new Dictionary<string, string>();
        public bool HasPictureNode { get; set; }
    }
}
