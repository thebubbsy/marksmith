using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// <c>:::timeline</c>. Its detector required every line to carry a literal <c>date:</c> key holding
/// an ISO-8601 date — a shape neither the wrapper catalog nor
/// <see cref="InsertSnippetBuilder.Timeline"/> ever produces. Every timeline block therefore failed
/// validation and fell through to the raw-text path. Behind that, the AST parser and the table
/// fallback both read only bulleted lines, so the documented bare <c>year: label</c> form yielded an
/// empty diagram even once the detector let it through.
/// </summary>
public class TimelineBlockTests
{
    private const string Bare = ":::timeline\n2024: Started\n2025: Shipped\n:::";
    private const string Bulleted = ":::timeline\n- 2024: Started\n- 2025: Shipped\n:::";

    [Theory]
    [InlineData("2026: Milestone", true)]
    [InlineData("- 2026: Milestone", true)]
    [InlineData("  * Q3 2026: Beta", true)]
    [InlineData("date: 2026-01-01", true)]
    [InlineData("no colon here", false)]
    [InlineData("trailing colon:", false)]
    public void IsTimelineEntry_Accepts_Both_Documented_Forms(string line, bool expected)
        => Assert.Equal(expected, TimelineDetector.IsTimelineEntry(line));

    [Theory]
    [InlineData(Bare)]
    [InlineData(Bulleted)]
    public void Detector_Accepts_Both_Forms(string block)
    {
        var (valid, confidence, errors) = new TimelineDetector().Validate(block);
        Assert.True(valid, string.Join("; ", errors));
        Assert.True(confidence >= new TimelineDetector().Threshold);
    }

    [Fact]
    public void Detector_Still_Reports_A_Malformed_Explicit_Date()
    {
        // A line that promises a date is still held to one.
        var (valid, _, errors) = new TimelineDetector()
            .Validate(":::timeline\ndate: not-a-date\n:::");
        Assert.False(valid);
        Assert.Contains(errors, e => e.Contains("Invalid date format"));
    }

    [Fact]
    public void Detector_Rejects_A_Body_With_No_Entries()
        => Assert.False(new TimelineDetector().Validate(":::timeline\njust prose\n:::").IsValid);

    private static (string Document, Dictionary<string, string> Parts) Export(string markdown)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-timeline-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(markdown, path, new AppSettings())
                .GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var parts = new Dictionary<string, string>();
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".xml")) continue;
                using var r = new StreamReader(e.Open());
                parts[e.FullName] = r.ReadToEnd();
            }
            return (parts["word/document.xml"], parts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(Bare)]
    [InlineData(Bulleted)]
    public void Docx_Renders_The_Entries_Instead_Of_Printing_Them(string block)
    {
        var (doc, parts) = Export($"# T\n\n{block}\n");

        // The wrapper used to survive as its own literal content.
        Assert.DoesNotContain("2024: Started", System.Text.RegularExpressions.Regex
            .Replace(doc, "<[^>]+>", ""));

        var diagram = parts.FirstOrDefault(p => p.Key.Contains("data") && p.Key.Contains("graphics"));
        Assert.False(diagram.Value is null, "no SmartArt diagram part was produced");
        var text = System.Text.RegularExpressions.Regex.Replace(diagram.Value!, "<[^>]+>", " ");
        Assert.Contains("Started", text);
        Assert.Contains("Shipped", text);
    }

    [Fact]
    public void Bare_And_Bulleted_Forms_Produce_The_Same_Entries()
    {
        static string Entries(Dictionary<string, string> parts)
        {
            var d = parts.First(p => p.Key.Contains("data") && p.Key.Contains("graphics")).Value;
            return string.Join("|", System.Text.RegularExpressions.Regex
                .Matches(d, "<a:t>([^<]*)</a:t>").Select(m => m.Groups[1].Value)
                .Where(v => v.Length > 0));
        }
        Assert.Equal(Entries(Export(Bare).Parts), Entries(Export(Bulleted).Parts));
    }
}
