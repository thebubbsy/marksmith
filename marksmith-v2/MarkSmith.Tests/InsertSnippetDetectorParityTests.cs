using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Every snippet the app's Insert menu writes must satisfy the detector that governs it.
///
/// This pairing is easy to break silently and expensive when it breaks: <c>:::chart</c> shipped
/// with a detector that demanded a JSON spec while <see cref="InsertSnippetBuilder.Chart"/> wrote
/// plain <c>label,value</c> rows, so using the app's own Insert → Chart command produced a block
/// that failed validation and printed as raw text. Nothing caught it because no test ever fed one
/// side's output to the other.
/// </summary>
public class InsertSnippetDetectorParityTests
{
    public static TheoryData<string, string> SnippetsWithDetectors() => new()
    {
        { "Chart",            InsertSnippetBuilder.Chart("bar", new[] { "Q1,10", "Q2,25", "Q3,15" }) },
        { "SmartArt",         InsertSnippetBuilder.SmartArt("process", new[] { "Plan", "Build", "Ship" }) },
        { "Timeline",         InsertSnippetBuilder.Timeline(new[] { "2024: Started", "2025: Shipped" }) },
        { "Columns",          InsertSnippetBuilder.Columns(2) },
        { "Tabs",             InsertSnippetBuilder.Tabs(new[] { "One", "Two" }) },
        { "Canvas",           InsertSnippetBuilder.Canvas(640, 400) },
        { "Watermark",        InsertSnippetBuilder.Watermark("DRAFT") },
        { "LineNumbers",      InsertSnippetBuilder.LineNumbers(5, "continuous") },
        { "CoverPage",        InsertSnippetBuilder.CoverPage() },
        { "DropCap",          InsertSnippetBuilder.DropCap(lines: 3) },
        { "ParallelColumns",  InsertSnippetBuilder.ParallelColumns("Left", "Right") },
        { "ConcordanceIndex", InsertSnippetBuilder.ConcordanceIndex(2) },
        { "Workflow",         InsertSnippetBuilder.Workflow(new[] { "Draft", "Review", "Publish" }) },
        { "Datagrid",         InsertSnippetBuilder.Datagrid(new[] { "Name,Role", "Ada,Engineer" }) },
        { "References",       InsertSnippetBuilder.References("knuth84", "Knuth", "Literate Programming", "1984") },
    };

    private static readonly IFeatureDetector[] Detectors =
    {
        new ChartDetector(), new SmartArtDetector(), new TimelineDetector(), new ColumnsDetector(),
        new TabsDetector(), new CanvasDetector(), new WatermarkDetector(), new LineNumbersDetector(),
        new CoverPageDetector(), new DropCapDetector(), new ParallelDetector(),
        new WorkflowDetector(), new DatagridDetector(), new ReferencesDetector(),
        new EmbedDetector(), new ShapesDetector(), new KanbanDetector(),
        new EngineeringDiagramDetector(), new AiContextDetector(), new IndexDetector(),
    };

    [Theory]
    [MemberData(nameof(SnippetsWithDetectors))]
    public void Inserted_Snippet_Validates_Against_Its_Own_Detector(string name, string snippet)
    {
        var block = snippet.Trim();
        var owner = Detectors.FirstOrDefault(d => d.Matches(block));
        Assert.True(owner is not null, $"{name}: no detector claims the snippet the app inserts:\n{block}");

        var (valid, confidence, errors) = owner!.Validate(block);
        Assert.True(valid,
            $"{name}: {owner.FeatureName} rejected the app's own snippet — {string.Join("; ", errors)}\n{block}");
        Assert.True(confidence >= owner.Threshold,
            $"{name}: confidence {confidence} is below the {owner.Threshold} threshold, so the block " +
            "falls through to the raw-text path.");
    }
}
