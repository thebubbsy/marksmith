using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

public class AdversarialLineDiffAndMermaidTests
{
    private static readonly ThemeDefinition DefaultTheme = new(
        "Default", "#ffffff", "#000000", "#111111", "#f0f0f0", "#cccccc", "#0066cc", "#e0e0e0", "#333333");

    #region 1. LineDiff Adversarial & Stress Tests

    [Fact]
    public void LineDiff_EmptyVsEmpty_ProducesSingleEmptySameLine()
    {
        var result = LineDiff.Diff("", "");
        Assert.Single(result);
        Assert.Equal(LineDiff.Kind.Same, result[0].Kind);
        Assert.Equal(1, result[0].OldNumber);
        Assert.Equal(1, result[0].NewNumber);
        Assert.Equal("", result[0].Text);

        var rows = LineDiff.BuildSideBySide(result);
        Assert.Single(rows);
        Assert.NotNull(rows[0].Left);
        Assert.NotNull(rows[0].Right);
        Assert.Equal("", rows[0].Left!.Text);
        Assert.Equal("", rows[0].Right!.Text);
    }

    [Fact]
    public void LineDiff_EmptyVsNonEmpty_ProducesAddedLines()
    {
        var result = LineDiff.Diff("", "line1\nline2\nline3");
        // "" splits to [""] (1 line). "line1\nline2\nline3" splits to 3 lines.
        // The empty line is marked Removed, and the 3 new lines are marked Added.
        Assert.Equal(4, result.Count);
        Assert.Equal(LineDiff.Kind.Removed, result[0].Kind);
        Assert.Equal("", result[0].Text);
        Assert.Equal(1, result[0].OldNumber);

        Assert.Equal(3, result.Count(l => l.Kind == LineDiff.Kind.Added));

        // BuildSideBySide pairs the 1 removed line with the 1st added line, leaving 2 added-only rows
        var rows = LineDiff.BuildSideBySide(result);
        Assert.Equal(3, rows.Count);
        Assert.NotNull(rows[0].Left);
        Assert.NotNull(rows[0].Right);
        Assert.Equal("", rows[0].Left!.Text);
        Assert.Equal("line1", rows[0].Right!.Text);

        Assert.Null(rows[1].Left);
        Assert.Equal("line2", rows[1].Right!.Text);

        Assert.Null(rows[2].Left);
        Assert.Equal("line3", rows[2].Right!.Text);
    }

    [Fact]
    public void LineDiff_NonEmptyVsEmpty_ProducesRemovedLines()
    {
        var result = LineDiff.Diff("line1\nline2\nline3", "");
        Assert.Equal(4, result.Count);
        Assert.Equal(3, result.Count(l => l.Kind == LineDiff.Kind.Removed));
        Assert.Equal(LineDiff.Kind.Added, result[3].Kind);
        Assert.Equal("", result[3].Text);
        Assert.Equal(1, result[3].NewNumber);

        // BuildSideBySide pairs the 1st removed line with the 1 added line, leaving 2 removed-only rows
        var rows = LineDiff.BuildSideBySide(result);
        Assert.Equal(3, rows.Count);
        Assert.NotNull(rows[0].Left);
        Assert.NotNull(rows[0].Right);
        Assert.Equal("line1", rows[0].Left!.Text);
        Assert.Equal("", rows[0].Right!.Text);

        Assert.Equal("line2", rows[1].Left!.Text);
        Assert.Null(rows[1].Right);

        Assert.Equal("line3", rows[2].Left!.Text);
        Assert.Null(rows[2].Right);
    }

    [Fact]
    public void LineDiff_SingleLineDiff()
    {
        var result = LineDiff.Diff("hello world", "goodbye world");
        Assert.Equal(2, result.Count);
        Assert.Equal(LineDiff.Kind.Removed, result[0].Kind);
        Assert.Equal("hello world", result[0].Text);
        Assert.Equal(1, result[0].OldNumber);
        Assert.Null(result[0].NewNumber);

        Assert.Equal(LineDiff.Kind.Added, result[1].Kind);
        Assert.Equal("goodbye world", result[1].Text);
        Assert.Null(result[1].OldNumber);
        Assert.Equal(1, result[1].NewNumber);

        var rows = LineDiff.BuildSideBySide(result);
        Assert.Single(rows);
        Assert.NotNull(rows[0].Left);
        Assert.NotNull(rows[0].Right);
        Assert.Equal("hello world", rows[0].Left!.Text);
        Assert.Equal("goodbye world", rows[0].Right!.Text);
    }

    [Fact]
    public void LineDiff_IdenticalLargeFile_TrimsEntirePrefix()
    {
        const int lineCount = 5000;
        var lines = Enumerable.Range(1, lineCount).Select(i => $"Line content #{i} with some arbitrary text {i * 17}").ToArray();
        var text = string.Join("\n", lines);

        var diff = LineDiff.Diff(text, text);
        Assert.Equal(lineCount, diff.Count);
        for (int i = 0; i < lineCount; i++)
        {
            Assert.Equal(LineDiff.Kind.Same, diff[i].Kind);
            Assert.Equal(i + 1, diff[i].OldNumber);
            Assert.Equal(i + 1, diff[i].NewNumber);
            Assert.Equal(lines[i], diff[i].Text);
        }

        var rows = LineDiff.BuildSideBySide(diff);
        Assert.Equal(lineCount, rows.Count);
        for (int i = 0; i < lineCount; i++)
        {
            Assert.NotNull(rows[i].Left);
            Assert.NotNull(rows[i].Right);
            Assert.Equal(i + 1, rows[i].Left!.LineNumber);
            Assert.Equal(i + 1, rows[i].Right!.LineNumber);
            Assert.Equal(lines[i], rows[i].Left!.Text);
            Assert.Equal(lines[i], rows[i].Right!.Text);
        }
    }

    [Fact]
    public void LineDiff_CompletelyDifferentLargeFiles_BelowThreshold_ExecutesLcs()
    {
        const int count = 1000;
        var aLines = Enumerable.Range(1, count).Select(i => $"Left line {i}").ToArray();
        var bLines = Enumerable.Range(1, count).Select(i => $"Right line {i}").ToArray();

        var diff = LineDiff.Diff(string.Join("\n", aLines), string.Join("\n", bLines));
        Assert.Equal(count * 2, diff.Count);
        Assert.Equal(count, diff.Count(d => d.Kind == LineDiff.Kind.Removed));
        Assert.Equal(count, diff.Count(d => d.Kind == LineDiff.Kind.Added));

        var rows = LineDiff.BuildSideBySide(diff);
        Assert.Equal(count, rows.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.NotNull(rows[i].Left);
            Assert.NotNull(rows[i].Right);
            Assert.Equal(LineDiff.Kind.Removed, rows[i].Left!.Kind);
            Assert.Equal(LineDiff.Kind.Added, rows[i].Right!.Kind);
            Assert.Equal(aLines[i], rows[i].Left!.Text);
            Assert.Equal(bLines[i], rows[i].Right!.Text);
        }
    }

    [Fact]
    public void LineDiff_ExceedingThreshold_FallsBackToAppendReplace_Safely()
    {
        const int count = 2500;
        var aLines = Enumerable.Range(1, count).Select(i => $"File A line {i}").ToArray();
        var bLines = Enumerable.Range(1, count).Select(i => $"File B line {i}").ToArray();

        var diff = LineDiff.Diff(string.Join("\n", aLines), string.Join("\n", bLines));
        Assert.Equal(count * 2, diff.Count);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(LineDiff.Kind.Removed, diff[i].Kind);
            Assert.Equal(i + 1, diff[i].OldNumber);
            Assert.Null(diff[i].NewNumber);
            Assert.Equal(aLines[i], diff[i].Text);
        }
        for (int j = 0; j < count; j++)
        {
            Assert.Equal(LineDiff.Kind.Added, diff[count + j].Kind);
            Assert.Null(diff[count + j].OldNumber);
            Assert.Equal(j + 1, diff[count + j].NewNumber);
            Assert.Equal(bLines[j], diff[count + j].Text);
        }

        var rows = LineDiff.BuildSideBySide(diff);
        Assert.Equal(count, rows.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.NotNull(rows[i].Left);
            Assert.NotNull(rows[i].Right);
            Assert.Equal(LineDiff.Kind.Removed, rows[i].Left!.Kind);
            Assert.Equal(LineDiff.Kind.Added, rows[i].Right!.Kind);
            Assert.Equal(aLines[i], rows[i].Left!.Text);
            Assert.Equal(bLines[i], rows[i].Right!.Text);
        }
    }

    [Fact]
    public void LineDiff_MultipleAlternatingHunks_PreservesMonotonicLineNumbers()
    {
        var aList = new List<string>();
        var bList = new List<string>();

        const int hunks = 50;
        for (int h = 0; h < hunks; h++)
        {
            aList.Add($"Common header for hunk {h}");
            bList.Add($"Common header for hunk {h}");

            if (h % 3 == 0)
            {
                aList.Add($"A mod 1 for hunk {h}");
                aList.Add($"A mod 2 for hunk {h}");

                bList.Add($"B mod 1 for hunk {h}");
                bList.Add($"B mod 2 for hunk {h}");
                bList.Add($"B mod 3 for hunk {h}");
            }
            else if (h % 3 == 1)
            {
                aList.Add($"To remove from hunk {h}");
            }
            else
            {
                bList.Add($"To add to hunk {h}");
            }
        }
        aList.Add("Final footer line");
        bList.Add("Final footer line");

        var diff = LineDiff.Diff(string.Join("\n", aList), string.Join("\n", bList));

        int expectedOld = 1;
        int expectedNew = 1;
        foreach (var line in diff)
        {
            if (line.Kind == LineDiff.Kind.Same)
            {
                Assert.Equal(expectedOld, line.OldNumber);
                Assert.Equal(expectedNew, line.NewNumber);
                expectedOld++;
                expectedNew++;
            }
            else if (line.Kind == LineDiff.Kind.Removed)
            {
                Assert.Equal(expectedOld, line.OldNumber);
                Assert.Null(line.NewNumber);
                expectedOld++;
            }
            else if (line.Kind == LineDiff.Kind.Added)
            {
                Assert.Null(line.OldNumber);
                Assert.Equal(expectedNew, line.NewNumber);
                expectedNew++;
            }
        }
        Assert.Equal(aList.Count + 1, expectedOld);
        Assert.Equal(bList.Count + 1, expectedNew);

        var rows = LineDiff.BuildSideBySide(diff);
        Assert.NotEmpty(rows);

        var leftLines = rows.Where(r => r.Left is not null).Select(r => r.Left!.Text).ToList();
        var rightLines = rows.Where(r => r.Right is not null).Select(r => r.Right!.Text).ToList();
        Assert.Equal(aList, leftLines);
        Assert.Equal(bList, rightLines);
    }

    [Fact]
    public void LineDiff_BuildSideBySide_UnbalancedRuns_CheckExactPairing()
    {
        var lines = new List<LineDiff.Line>
        {
            new(LineDiff.Kind.Same, 1, 1, "start"),
            new(LineDiff.Kind.Removed, 2, null, "rem1"),
            new(LineDiff.Kind.Removed, 3, null, "rem2"),
            new(LineDiff.Kind.Removed, 4, null, "rem3"),
            new(LineDiff.Kind.Removed, 5, null, "rem4"),
            new(LineDiff.Kind.Added, null, 2, "add1"),
            new(LineDiff.Kind.Added, null, 3, "add2"),
            new(LineDiff.Kind.Same, 6, 4, "end")
        };

        var rows = LineDiff.BuildSideBySide(lines);
        Assert.Equal(6, rows.Count);
        Assert.Equal("start", rows[0].Left!.Text);
        Assert.Equal("start", rows[0].Right!.Text);

        Assert.Equal("rem1", rows[1].Left!.Text);
        Assert.Equal("add1", rows[1].Right!.Text);

        Assert.Equal("rem2", rows[2].Left!.Text);
        Assert.Equal("add2", rows[2].Right!.Text);

        Assert.Equal("rem3", rows[3].Left!.Text);
        Assert.Null(rows[3].Right);

        Assert.Equal("rem4", rows[4].Left!.Text);
        Assert.Null(rows[4].Right);

        Assert.Equal("end", rows[5].Left!.Text);
        Assert.Equal("end", rows[5].Right!.Text);
    }

    [Fact]
    public void LineDiff_BuildSideBySide_MoreAddedThanRemoved_CheckExactPairing()
    {
        var lines = new List<LineDiff.Line>
        {
            new(LineDiff.Kind.Removed, 1, null, "rem1"),
            new(LineDiff.Kind.Added, null, 1, "add1"),
            new(LineDiff.Kind.Added, null, 2, "add2"),
            new(LineDiff.Kind.Added, null, 3, "add3")
        };

        var rows = LineDiff.BuildSideBySide(lines);
        Assert.Equal(3, rows.Count);
        Assert.Equal("rem1", rows[0].Left!.Text);
        Assert.Equal("add1", rows[0].Right!.Text);

        Assert.Null(rows[1].Left);
        Assert.Equal("add2", rows[1].Right!.Text);

        Assert.Null(rows[2].Left);
        Assert.Equal("add3", rows[2].Right!.Text);
    }

    [Fact]
    public void LineDiff_BuildSideBySide_EmptyList_ReturnsEmpty()
    {
        var rows = LineDiff.BuildSideBySide(new List<LineDiff.Line>());
        Assert.Empty(rows);
    }

    [Fact]
    public void LineDiff_OnlyNewlines_DiffsCorrectly()
    {
        var before = "\n\n";
        var after = "\n";
        var diff = LineDiff.Diff(before, after);
        // "\n\n" has 3 empty lines; "\n" has 2 empty lines
        Assert.Equal(3, diff.Count);
        Assert.Equal(2, diff.Count(l => l.Kind == LineDiff.Kind.Same));
        Assert.Equal(1, diff.Count(l => l.Kind == LineDiff.Kind.Removed));
    }

    [Fact]
    public void LineDiff_MixedCarriageReturns_NormalizedCorrectly()
    {
        var before = "a\r\nb\rc\n";
        var after = "a\nb\nc\n";
        var diff = LineDiff.Diff(before, after);
        Assert.All(diff, l => Assert.Equal(LineDiff.Kind.Same, l.Kind));
        Assert.Equal(4, diff.Count);
    }

    [Fact]
    public void LineDiff_LineSwap_ProducesRemovedAndAdded()
    {
        var before = "lineA\nlineB";
        var after = "lineB\nlineA";
        var diff = LineDiff.Diff(before, after);
        var rows = LineDiff.BuildSideBySide(diff);
        Assert.NotEmpty(rows);
        var left = rows.Where(r => r.Left is not null).Select(r => r.Left!.Text).ToList();
        var right = rows.Where(r => r.Right is not null).Select(r => r.Right!.Text).ToList();
        Assert.Equal(new[] { "lineA", "lineB" }, left);
        Assert.Equal(new[] { "lineB", "lineA" }, right);
    }

    [Fact]
    public void LineDiff_SingleCharacterModifications_PreservesUnmodifiedLines()
    {
        var before = "Header\nAlpha\nBeta\nGamma\nFooter";
        var after = "Header\nAlpha\nBeta Modified\nGamma\nFooter";
        var diff = LineDiff.Diff(before, after);

        Assert.Equal(LineDiff.Kind.Same, diff[0].Kind); // Header
        Assert.Equal(LineDiff.Kind.Same, diff[1].Kind); // Alpha
        Assert.Equal(LineDiff.Kind.Removed, diff[2].Kind); // Beta
        Assert.Equal(LineDiff.Kind.Added, diff[3].Kind); // Beta Modified
        Assert.Equal(LineDiff.Kind.Same, diff[4].Kind); // Gamma
        Assert.Equal(LineDiff.Kind.Same, diff[5].Kind); // Footer

        var rows = LineDiff.BuildSideBySide(diff);
        Assert.Equal(5, rows.Count);
        Assert.Equal("Beta", rows[2].Left!.Text);
        Assert.Equal("Beta Modified", rows[2].Right!.Text);
    }

    #endregion

    #region 2. MermaidHarvestService Adversarial & Stress Tests

    private class MockWebRenderHost : IWebRenderHost
    {
        public bool Ready { get; set; } = true;
        public string? ScriptResult { get; set; }
        public bool ShouldThrowOnNavigate { get; set; }
        public int BeginHarvestCalls { get; private set; }
        public int EndHarvestCalls { get; private set; }
        public List<string> NavigatedHtml { get; } = new();

        public Task<bool> EnsureReadyAsync() => Task.FromResult(Ready);

        public Task NavigateToStringAsync(string html)
        {
            if (ShouldThrowOnNavigate) throw new InvalidOperationException("Simulated navigation failure");
            NavigatedHtml.Add(html);
            return Task.CompletedTask;
        }

        public Task<string?> ExecuteScriptAsync(string javaScript)
        {
            return Task.FromResult(ScriptResult);
        }

        public Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup) => Task.FromResult(true);

        public Task BeginHarvestAsync()
        {
            BeginHarvestCalls++;
            return Task.CompletedTask;
        }

        public Task EndHarvestAsync()
        {
            EndHarvestCalls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MermaidHarvest_FenceCaching_ReusesExtractionForIdenticalMarkdown()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();
        var markdown = "```mermaid\ngraph TD\n  A-->B\n```";

        var partialDiags = new List<GenericDiagram?>
        {
            new() { W = 100, H = 50, Nodes = new(), Edges = new(), Texts = new() }
        };
        host.ScriptResult = JsonSerializer.Serialize(JsonSerializer.Serialize(partialDiags));

        // First pass
        var res1 = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);
        Assert.Single(res1);

        // Second pass with same markdown
        var res2 = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);
        Assert.Single(res2);

        // Third pass with different markdown
        var markdown2 = "```mermaid\ngraph LR\n  X-->Y\n```\n\n```mermaid\ngraph TD\n  Z-->W\n```";
        var twoDiags = new List<GenericDiagram?>
        {
            new() { W = 100, H = 50, Nodes = new(), Edges = new(), Texts = new() },
            new() { W = 200, H = 60, Nodes = new(), Edges = new(), Texts = new() }
        };
        host.ScriptResult = JsonSerializer.Serialize(JsonSerializer.Serialize(twoDiags));

        var res3 = await service.HarvestGenericGeometryAsync(host, markdown2, new AppSettings(), DefaultTheme);
        Assert.Equal(2, res3.Count);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestMermaidGeometry_0Fences_ReturnsEmpty()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();
        var markdown = "No mermaid fences";

        var result = await service.HarvestMermaidGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);
        Assert.Empty(result);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestMermaidGeometry_PartialOutput_PadsExactNullCount()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();
        var markdown = "```mermaid\ngraph TD\n  A-->B\n```\n```mermaid\ngraph TD\n  C-->D\n```\n```mermaid\ngraph TD\n  E-->F\n```";

        var partial = new List<HarvestedDiagram?>
        {
            new() { W = 100, H = 100, Nodes = new() { new() { Id = "A", Kind = "Rect", Label = "A" } } }
        };
        // 1 returned out of 3 fences
        host.ScriptResult = JsonSerializer.Serialize(JsonSerializer.Serialize(partial));

        var result = await service.HarvestMermaidGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Equal(3, result.Count);
        Assert.NotNull(result[0]);
        Assert.Null(result[1]);
        Assert.Null(result[2]);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestGenericGeometry_0Fences_ReturnsEmptyImmediately()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();
        var markdown = "# Title\n\nNo mermaid fences here\n```csharp\nvar x = 1;\n```";

        var result = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Empty(result);
        Assert.Equal(0, host.BeginHarvestCalls);
        Assert.Equal(0, host.EndHarvestCalls);
        Assert.Empty(host.NavigatedHtml);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestGenericGeometry_HostNotReady_ReturnsEmpty()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost { Ready = false };
        var markdown = "```mermaid\ngraph TD; A-->B;\n```";

        var result = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Empty(result);
        Assert.Equal(0, host.BeginHarvestCalls);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestGenericGeometry_1Fence_Success()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();
        var markdown = "```mermaid\ngraph TD; A-->B;\n```";

        var sampleDiag = new GenericDiagram
        {
            W = 400,
            H = 300,
            Nodes = new List<GNode> { new() { X = 10, Y = 20, W = 100, H = 50, Kind = "Rect", Fill = "#fff", Stroke = "#000" } },
            Edges = new List<GEdge>(),
            Texts = new List<GText>()
        };
        var jsonPayload = JsonSerializer.Serialize(new List<GenericDiagram?> { sampleDiag });
        host.ScriptResult = JsonSerializer.Serialize(jsonPayload);

        var result = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Single(result);
        Assert.NotNull(result[0]);
        Assert.Equal(400, result[0]!.W);
        Assert.Equal(1, host.BeginHarvestCalls);
        Assert.Equal(1, host.EndHarvestCalls);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestGenericGeometry_10Fences_Success()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();

        var mdList = Enumerable.Range(1, 10).Select(i => $"```mermaid\nflowchart LR\n  N{i} --> M{i}\n```\n");
        var markdown = string.Join("\n\n", mdList);

        var diags = Enumerable.Range(1, 10).Select(i => (GenericDiagram?)new GenericDiagram
        {
            W = 100 * i,
            H = 50 * i,
            Nodes = new List<GNode>(),
            Edges = new List<GEdge>(),
            Texts = new List<GText>()
        }).ToList();

        var jsonPayload = JsonSerializer.Serialize(diags);
        host.ScriptResult = JsonSerializer.Serialize(jsonPayload);

        var result = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Equal(10, result.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.NotNull(result[i]);
            Assert.Equal(100 * (i + 1), result[i]!.W);
        }
        Assert.Equal(1, host.BeginHarvestCalls);
        Assert.Equal(1, host.EndHarvestCalls);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestGenericGeometry_PartialOutput_PadsExactNullCount()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();

        var mdList = Enumerable.Range(1, 10).Select(i => $"```mermaid\nflowchart LR\n  A{i} --> B{i}\n```");
        var markdown = string.Join("\n\n", mdList);

        var partialDiags = new List<GenericDiagram?>
        {
            new() { W = 100, H = 50, Nodes = new(), Edges = new(), Texts = new() },
            new() { W = 200, H = 50, Nodes = new(), Edges = new(), Texts = new() },
            new() { W = 300, H = 50, Nodes = new(), Edges = new(), Texts = new() }
        };
        // 3 diagrams returned for 10 fences
        host.ScriptResult = JsonSerializer.Serialize(JsonSerializer.Serialize(partialDiags));

        var result = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Equal(10, result.Count);
        Assert.NotNull(result[0]);
        Assert.NotNull(result[1]);
        Assert.NotNull(result[2]);
        Assert.Null(result[3]);
        Assert.Null(result[4]);
        Assert.Null(result[5]);
        Assert.Null(result[6]);
        Assert.Null(result[7]);
        Assert.Null(result[8]);
        Assert.Null(result[9]);
        Assert.Equal(1, host.EndHarvestCalls);
    }

    [Fact]
    public async Task MermaidHarvest_HarvestGenericGeometry_NavigationException_PadsNullsSafely()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost { ShouldThrowOnNavigate = true };

        var mdList = Enumerable.Range(1, 5).Select(i => $"```mermaid\ngraph TD\n  A{i} --> B{i}\n```");
        var markdown = string.Join("\n\n", mdList);

        var result = await service.HarvestGenericGeometryAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Equal(5, result.Count);
        Assert.All(result, item => Assert.Null(item));
        Assert.Equal(1, host.BeginHarvestCalls);
        Assert.Equal(1, host.EndHarvestCalls);
    }

    [Fact]
    public async Task MermaidHarvest_RenderMermaidPngs_PadsNullsOnShortResult()
    {
        var service = new MermaidHarvestService();
        var host = new MockWebRenderHost();

        var mdList = Enumerable.Range(1, 4).Select(i => $"```mermaid\ngraph TD\n  A{i} --> B{i}\n```");
        var markdown = string.Join("\n\n", mdList);

        var dummyPng = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var urls = new List<string?>
        {
            $"data:image/png;base64,{dummyPng}",
            $"data:image/png;base64,{dummyPng}"
        };
        host.ScriptResult = JsonSerializer.Serialize(JsonSerializer.Serialize(urls));

        var result = await service.RenderMermaidPngsAsync(host, markdown, new AppSettings(), DefaultTheme);

        Assert.Equal(4, result.Count);
        Assert.NotNull(result[0]);
        Assert.NotNull(result[1]);
        Assert.Null(result[2]);
        Assert.Null(result[3]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result[0]);
    }

    #endregion
}
