using System;
using System.Linq;
using MdToPdf.Core.AdvancedFeatures;

class Program
{
    static void Main()
    {
        string markdown = ":::kanban\n### BACKLOG\n- Research orthogonal routing libraries\n- Update project documentation\n- Review pull request #42\n:::";
        var pipeline = new AdvancedFeaturePipeline();
        var nodes = pipeline.Process(markdown, "doc123");
        Console.WriteLine($"Detected {nodes.Count} features");
        foreach(var n in nodes) {
            Console.WriteLine($"- {n.Detector.FeatureName}: {n.Block.Start} to {n.Block.End}");
        }
    }
}
